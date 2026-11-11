using ShipCheck.Cli;
using ShipCheck.Core;
using ShipCheck.Infrastructure;

namespace ShipCheck.Core.Tests;

[TestClass]
public sealed class CommandLineTests
{
	[TestMethod]
	public void 새_형식_옵션()
	{
		CommandLine cli = CommandLine.Parse(new[] { "validate", "--project", @"D:\공백 경로\Game.uproject", "--force", "--no-single-file", "a.cpp", "b.h", "--result-json", "r.json", "--step-timeout", "30" });
		Assert.AreEqual("validate", cli.Command);
		Assert.AreEqual(@"D:\공백 경로\Game.uproject", cli.Project);
		Assert.IsTrue(cli.Force && cli.NoSingleFile);
		CollectionAssert.AreEqual(new[] { "a.cpp", "b.h" }, cli.Positional);
		Assert.AreEqual("r.json", cli.ResultJson);
		Assert.AreEqual(TimeSpan.FromMinutes(30), cli.StepTimeout);
	}

	[TestMethod]
	public void 기존_ShipCheck_ps1_인수_호환()
	{
		CommandLine cli = CommandLine.Parse(new[] { "-Project", "P", "-Paths", "a.cpp", "b.cpp", "-All", "-PathListFile", "list.txt", "-MessageFile", "m.txt", "-WorkingCopy", "W", "-ConfigPath", "c.json", "-ResultJson", "r.json", "-Trigger", "hook" });
		Assert.AreEqual("validate", cli.Command);
		Assert.AreEqual("P", cli.Project);
		CollectionAssert.AreEqual(new[] { "a.cpp", "b.cpp" }, cli.Positional);
		Assert.IsTrue(cli.All);
		Assert.AreEqual("list.txt", cli.PathsFile);
		Assert.AreEqual("m.txt", cli.MessageFile);
		Assert.AreEqual("W", cli.WorkingCopy);
		Assert.AreEqual("c.json", cli.ConfigPath);
		Assert.AreEqual("hook", cli.Trigger);
		Assert.AreEqual("query", CommandLine.Parse(new[] { "-Project", "P", "-Query" }).Command);
	}

	[TestMethod]
	public void 잘못된_옵션은_거부()
	{
		Assert.ThrowsException<ArgumentException>(() => CommandLine.Parse(new[] { "--unknown" }));
		Assert.ThrowsException<ArgumentException>(() => CommandLine.Parse(new[] { "--project" }));
		Assert.ThrowsException<ArgumentException>(() => CommandLine.Parse(new[] { "--step-timeout", "0" }));
	}

	[TestMethod]
	public void 원본_명령줄은_역슬래시로_끝나는_경로를_보존한다()
	{
		List<string> tokens = CommandLine.SplitRaw(@"""C:\Tool\ShipCheck.exe"" hook ""D:\Work\My Game\"" ""C:\Temp\p.tmp"" infinity ""C:\Temp\m.tmp"" ""D:\Work\""");
		CollectionAssert.AreEqual(new[] { @"C:\Tool\ShipCheck.exe", "hook", @"D:\Work\My Game\", @"C:\Temp\p.tmp", "infinity", @"C:\Temp\m.tmp", @"D:\Work\" }, tokens);
		CollectionAssert.AreEqual(new[] { "a", "", "b c" }, CommandLine.SplitRaw(@"a """" ""b c"""));
	}

	[TestMethod]
	public void 훅_인수_프로젝트_유무()
	{
		(string? project, string pathFile, string messageFile, string wc) = CommandLine.ParseHookArguments(new[] { @"D:\W\Game\Game.uproject", "p.tmp", "infinity", "m.tmp", @"D:\W\" }, _ => false);
		Assert.AreEqual(@"D:\W\Game\Game.uproject", project);
		Assert.AreEqual("p.tmp", pathFile);
		Assert.AreEqual("m.tmp", messageFile);
		Assert.AreEqual(@"D:\W", wc);

		(project, pathFile, _, _) = CommandLine.ParseHookArguments(new[] { "p.tmp", "infinity", "m.tmp", @"D:\W" }, _ => true);
		Assert.IsNull(project, "TortoiseSVN 인수 4개뿐이면 프로젝트 없음 (자동 탐색)");
		Assert.AreEqual("p.tmp", pathFile);

		(project, _, _, _) = CommandLine.ParseHookArguments(new[] { @"D:\W\Game\", "p.tmp", "infinity", "m.tmp", @"D:\W" }, x => x.StartsWith(@"D:\W\Game"));
		Assert.AreEqual(@"D:\W\Game", project, "프로젝트 폴더 인수 (끝 \\ 제거)");

		Assert.ThrowsException<ArgumentException>(() => CommandLine.ParseHookArguments(new[] { "p.tmp", "infinity" }, _ => false));
	}
}

[TestClass]
public sealed class BuildCommandLineTests
{
	[TestMethod]
	public void 공백_한글_인수는_인용하고_나머지는_그대로()
	{
		string command = BuildRunner.CreateCommandLine(@"D:\Unreal Engine\Engine\Build\BatchFiles\Build.bat",
			new[] { "Game", "Win64", "Shipping", @"-Project=D:\공백 경로\Game.uproject", "-WaitMutex", "-SingleFile=D:\\a,b;c=d.cpp", "" });
		Assert.AreEqual(@"set /p SHIPCHECK_GATE=&&""D:\Unreal Engine\Engine\Build\BatchFiles\Build.bat"" Game Win64 Shipping ""-Project=D:\공백 경로\Game.uproject"" -WaitMutex -SingleFile=D:\a,b;c=d.cpp """"", command);
	}

	[TestMethod]
	[DataRow("-Define=\"X\"")]
	[DataRow("-Path=%TEMP%")]
	[DataRow("-Name=Hello!")]
	[DataRow("-A=^B")]
	[DataRow("-A=1\n-B")]
	public void cmd_경계에서_안전하지_않은_문자는_설정_오류(string argument)
	{
		ShipCheckException ex = Assert.ThrowsException<ShipCheckException>(() => BuildRunner.CreateCommandLine(@"D:\E\Build.bat", new[] { argument }));
		Assert.AreEqual(ErrorKind.ConfigError, ex.Kind);
		Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Diagnostic.Code);
	}
}

[TestClass]
public sealed class TortoiseHookTests
{
	const string Tool = @"D:\Tools\ShipCheck";
	const string UProject = @"D:\Work\MyGame\MyGame.uproject";
	static readonly string Cmd = $"\"{Tool}\\TortoiseSVN-PreCommit.cmd\"";

	static string Hook(string type, string path, string command, string wait) => $"{type}\n{path}\n{command}\n{wait}\nhide\n";

	[TestMethod]
	[DataRow("미등록", null, HookState.NotRegistered)]
	[DataRow("다른 스크립트만", "pre_commit_hook\nD:\\Work\n\"C:\\other.cmd\"\ntrue\nhide\n", HookState.NotRegistered)]
	[DataRow("해석 불가 형식", "garbage \"D:\\Tools\\ShipCheck\\TortoiseSVN-PreCommit.cmd\" garbage", HookState.Unverified)]
	public void 등록_없음과_해석_불가(string name, string? hooks, HookState expected) =>
		Assert.AreEqual(expected, TortoiseHooks.ParseStatus(hooks, Tool, UProject).State, name);

	[TestMethod]
	public void 등록_해석()
	{
		(string Name, string Hooks, HookState Expected)[] cases =
		{
			("WC 루트 + 프로젝트 인자", Hook("pre_commit_hook", @"D:\Work", $"{Cmd} \"{UProject}\"", "true"), HookState.Applied),
			("프로젝트 폴더 인자", Hook("pre_commit_hook", @"D:\Work\MyGame", $"{Cmd} \"D:\\Work\\MyGame\\\"", "true"), HookState.Applied),
			("인자 없음(자동 탐색)", Hook("pre_commit_hook", @"D:\Work", Cmd, "true"), HookState.Applied),
			("Wait 꺼짐", Hook("pre_commit_hook", @"D:\Work", $"{Cmd} \"{UProject}\"", "false"), HookState.Misconfigured),
			("다른 워킹 카피", Hook("pre_commit_hook", @"D:\Other", $"{Cmd} \"{UProject}\"", "true"), HookState.Misconfigured),
			("이름만 비슷한 경로", Hook("pre_commit_hook", @"D:\Work\MyGam", Cmd, "true"), HookState.Misconfigured),
			("다른 프로젝트 인자", Hook("pre_commit_hook", @"D:\Work", $"{Cmd} \"D:\\Work\\Other\\Other.uproject\"", "true"), HookState.Misconfigured),
			("Pre-commit 이 아님", Hook("start_commit_hook", @"D:\Work", $"{Cmd} \"{UProject}\"", "true"), HookState.Misconfigured),
			("enforce 필드 + 여러 훅 중 하나 적용", Hook("post_update_hook", @"D:\Work", "x.cmd", "true") + "ask\n" + Hook("pre_commit_hook", @"D:\Other", Cmd, "true") + "enforce\n" + Hook("pre_commit_hook", @"D:\Work", Cmd, "true") + "enforce\n", HookState.Applied),
		};
		foreach ((string name, string hooks, HookState expected) in cases)
		{
			HookStatus status = TortoiseHooks.ParseStatus(hooks, Tool, UProject);
			Assert.AreEqual(expected, status.State, $"{name}: {status.Detail}");
		}
	}
}
