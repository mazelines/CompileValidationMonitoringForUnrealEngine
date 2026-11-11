using System.Diagnostics;
using System.Text;
using ShipCheck.Core;
using ShipCheck.Infrastructure;
using ShipCheck.TestSupport;

namespace ShipCheck.Acceptance.Tests;

/// <summary>
/// 배포 폴더(ShipCheck.exe + DLL + 설정 + .cmd 진입점, PowerShell 스크립트 없음)를 만들어
/// 명령줄/TortoiseSVN 훅/ShipCommit 의 JSON·종료 코드·커밋 여부를 실제 프로세스로 확인한다.
/// </summary>
[TestClass]
public sealed class CliAcceptanceTests
{
	const string Hero = @"Source\Game\Private\Hero.cpp";
	const string Bug = @"Source\Game\Private\Bug.cpp";

	ShipCheckFixture _f = null!;
	string _deploy = null!;

	[TestInitialize]
	public void Setup()
	{
		_f = new ShipCheckFixture();
		_deploy = Path.Combine(_f.Root, "배포 폴더");
		Directory.CreateDirectory(_deploy);
		foreach (string file in Directory.EnumerateFiles(Products.CliDir))
		{
			File.Copy(file, Path.Combine(_deploy, Path.GetFileName(file)));
		}
		foreach (string cmd in new[] { "TortoiseSVN-PreCommit.cmd", "ShipCheck.cmd", "ShipCommit.cmd" })
		{
			File.Copy(Path.Combine(Products.RepoRoot, cmd), Path.Combine(_deploy, cmd));
		}
		_f.WriteConfig(path: Path.Combine(_deploy, ShipCheckConfig.DefaultFileName));
	}

	[TestCleanup]
	public void Cleanup() => _f.Dispose();

	sealed record Execution(int ExitCode, string Output, string Error);

	/// <summary>배포 폴더의 실행 파일/스크립트를 실제로 실행 (cmd 는 TortoiseSVN 처럼 명령줄 문자열로)</summary>
	Execution Run(string commandLine, string? workingDirectory = null)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{commandLine}\"")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = ProcessRunner.ConsoleEncoding,
			StandardErrorEncoding = ProcessRunner.ConsoleEncoding,
			WorkingDirectory = workingDirectory ?? _f.WorkingCopy,
		};
		using Process process = Process.Start(startInfo)!;
		Task<string> output = process.StandardOutput.ReadToEndAsync();
		Task<string> error = process.StandardError.ReadToEndAsync();
		Assert.IsTrue(process.WaitForExit(120_000), "프로세스 제한 시간 초과");
		return new Execution(process.ExitCode, output.Result, error.Result);
	}

	string Exe => $"\"{Path.Combine(_deploy, "ShipCheck.exe")}\"";

	static string Q(string value) => $"\"{value}\"";

	RunResult ReadResult(string path) => ShipCheckJson.Deserialize<RunResult>(File.ReadAllText(path))!;

	[TestMethod, Timeout(180_000)]
	public void 배포_폴더에는_PowerShell_스크립트가_없다()
	{
		Assert.IsFalse(Directory.EnumerateFiles(_deploy, "*.ps1").Any());
		Execution help = Run($"{Exe} help");
		Assert.AreEqual(0, help.ExitCode);
		StringAssert.Contains(help.Output, "종료 코드");
	}

	[TestMethod, Timeout(180_000)]
	public void validate_종료_코드와_결과_JSON()
	{
		string json = Path.Combine(_f.Root, "result.json");
		_f.Touch(Hero);
		Execution pass = Run($"{Exe} validate --project {Q(_f.UProject)} --result-json {Q(json)} {Q(_f.F(Hero))}");
		Assert.AreEqual(0, pass.ExitCode, pass.Error);
		RunResult result = ReadResult(json);
		Assert.AreEqual("Pass", result.Result);
		Assert.IsTrue(result.CommitAllowed);
		Assert.AreEqual(2, result.Version);

		Assert.AreEqual(0, Run($"{Exe} validate --project {Q(_f.UProject)} --result-json {Q(json)} {Q(_f.F(Hero))}").ExitCode);
		Assert.AreEqual("Cached", ReadResult(json).Result);

		_f.Touch(Bug);
		Execution fail = Run($"{Exe} validate --project {Q(_f.UProject)} --result-json {Q(json)} {Q(_f.F(Bug))}");
		Assert.AreEqual(1, fail.ExitCode);
		StringAssert.Contains(fail.Error, "C1083", "stderr 에 오류 요약 (TortoiseSVN 커밋 창 표시)");
		StringAssert.Contains(fail.Error, "커밋이 차단", "한글 stderr");
		Assert.IsFalse(ReadResult(json).CommitAllowed);

		_f.WriteConfig(c => c.ExtraUbtArgs = new List<string> { "-X=%PATH%" }, Path.Combine(_deploy, ShipCheckConfig.DefaultFileName));
		Execution error = Run($"{Exe} validate --project {Q(_f.UProject)} --result-json {Q(json)} {Q(_f.F(Hero))}");
		Assert.AreEqual(2, error.ExitCode);
		Assert.AreEqual(nameof(ErrorKind.ConfigError), ReadResult(json).ErrorKind);
	}

	[TestMethod, Timeout(180_000)]
	public void 기존_ShipCheck_ps1_인수로도_실행된다()
	{
		string json = Path.Combine(_f.Root, "legacy.json");
		_f.Touch(Hero);
		Execution run = Run($"\"{Path.Combine(_deploy, "ShipCheck.cmd")}\" -Project {Q(_f.UProject)} -Paths {Q(_f.F(Hero))} -ResultJson {Q(json)}");
		Assert.AreEqual(0, run.ExitCode, run.Error);
		Assert.AreEqual("Pass", ReadResult(json).Result);
	}

	[TestMethod, Timeout(180_000)]
	public void query_JSON()
	{
		_f.Touch(Hero);
		string json = Path.Combine(_f.Root, "query.json");
		Assert.AreEqual(0, Run($"{Exe} query --project {Q(_f.UProject)} --result-json {Q(json)}").ExitCode);
		QueryResult query = ShipCheckJson.Deserialize<QueryResult>(File.ReadAllText(json))!;
		Assert.AreEqual(_f.UProject, query.Project);
		Assert.IsTrue(query.SvnAvailable);
		Assert.AreEqual(_f.F(Hero), query.Changes.Single().Path);
		Assert.AreEqual(_deploy, query.ToolDir);

		Execution missing = Run($"{Exe} query --project {Q(Path.Combine(_f.Root, "없음.uproject"))} --result-json {Q(json)}");
		Assert.AreEqual(2, missing.ExitCode);
		StringAssert.Contains(ShipCheckJson.Deserialize<QueryResult>(File.ReadAllText(json))!.Error, "찾을 수 없습니다");
	}

	/// <summary>TortoiseSVN 이 붙이는 인수: PATH DEPTH MESSAGEFILE CWD</summary>
	Execution Hook(string? projectArgument, IEnumerable<string> paths, string message)
	{
		string pathFile = Path.Combine(_f.Root, "svn-paths.tmp");
		string messageFile = Path.Combine(_f.Root, "svn-message.tmp");
		File.WriteAllLines(pathFile, paths, new UTF8Encoding(false));
		File.WriteAllText(messageFile, message, new UTF8Encoding(false));
		string project = projectArgument == null ? "" : Q(projectArgument) + " ";
		return Run($"\"{Path.Combine(_deploy, "TortoiseSVN-PreCommit.cmd")}\" {project}{Q(pathFile)} infinity {Q(messageFile)} {Q(_f.WorkingCopy + "\\")}");
	}

	[TestMethod, Timeout(180_000)]
	public void TortoiseSVN_훅_인수_호환()
	{
		_f.Touch(Bug);
		Execution blocked = Hook(_f.UProject, new[] { _f.F(Bug) }, "버그 수정");
		Assert.AreEqual(1, blocked.ExitCode);
		StringAssert.Contains(blocked.Error, "C1083");

		Execution bypass = Hook(_f.UProject, new[] { _f.F(Bug) }, "긴급 [skip-shipcheck] 서버 장애");
		Assert.AreEqual(0, bypass.ExitCode, "우회 태그");

		_f.Revert(Bug);
		_f.Touch(Hero);
		Assert.AreEqual(0, Hook(_f.ProjectDir + "\\", new[] { _f.F(Hero) }, "프로젝트 폴더 인수").ExitCode);
		Assert.AreEqual(0, Hook(null, new[] { _f.F(Hero) }, "인수 없이 워킹 카피에서 자동 탐색").ExitCode);

		List<RunResult> history = FileRunStore.ReadHistory(_f.ProjectDir);
		Assert.IsTrue(history.All(x => x.Trigger == "hook"));
		Assert.IsTrue(history.Any(x => x.Result == "Skipped" && x.Reason!.Contains("서버 장애")), "우회 사용 기록");
	}

	[TestMethod, Timeout(180_000)]
	public void ShipCommit은_검증에_실패하면_커밋하지_않는다()
	{
		string Head() => _f.Svn("info", "--show-item", "revision", _f.RepoUrl).StandardOutput.Trim();
		string before = Head();
		_f.Touch(Bug);
		Execution blocked = Run($"\"{Path.Combine(_deploy, "ShipCommit.cmd")}\" -m \"실패해야 함\" {Q(_f.F(Bug))}", _f.ProjectDir);
		Assert.AreEqual(1, blocked.ExitCode, blocked.Error);
		StringAssert.Contains(blocked.Error, "커밋하지 않았습니다");
		Assert.AreEqual(before, Head(), "svn commit 이 호출되지 않음");

		_f.Revert(Bug);
		_f.Touch(Hero);
		Execution committed = Run($"\"{Path.Combine(_deploy, "ShipCommit.cmd")}\" -m \"한글 커밋 메시지\" {Q(_f.F(Hero))}", _f.ProjectDir);
		Assert.AreEqual(0, committed.ExitCode, committed.Error + committed.Output);
		Assert.AreEqual((int.Parse(before) + 1).ToString(), Head());
		StringAssert.Contains(_f.Svn("log", "-l", "1", "--xml", _f.RepoUrl).StandardOutput, "한글 커밋 메시지");
	}

	[TestMethod, Timeout(180_000)]
	public async Task GUI_서비스_호출과_CLI가_같은_결과를_낸다()
	{
		_f.Touch(Hero);
		_f.Touch(@"Source\Game\Private\Hero.h");
		string[] paths = { Hero, @"Source\Game\Private\Hero.h" };
		string config = Path.Combine(_deploy, ShipCheckConfig.DefaultFileName);

		// GUI 는 같은 ValidationService 를 직접 호출한다
		RunResult service = await _f.Service().ValidateAsync(new ValidationRequest { Selector = new ProjectSelector(_f.UProject, null, config), Paths = paths.Select(_f.F).ToList(), Force = true, Trigger = "gui" }, null, CancellationToken.None);
		string json = Path.Combine(_f.Root, "cli.json");
		Execution cli = Run($"{Exe} validate --force --project {Q(_f.UProject)} --config {Q(config)} --result-json {Q(json)} " + string.Join(" ", paths.Select(x => Q(_f.F(x)))));
		RunResult fromCli = ReadResult(json);

		Assert.AreEqual(service.Result, fromCli.Result);
		Assert.AreEqual(service.ExitCode, cli.ExitCode);
		Assert.AreEqual(service.StateKey, fromCli.StateKey);
		CollectionAssert.AreEqual(service.Files, fromCli.Files);
		CollectionAssert.AreEqual(service.SingleFiles, fromCli.SingleFiles);
		CollectionAssert.AreEqual(service.Steps!.Select(x => x.Label).ToList(), fromCli.Steps!.Select(x => x.Label).ToList());
	}
}
