using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ShipCheck.Core;
using ShipCheck.Infrastructure;

namespace ShipCheck.TestSupport;

/// <summary>빌드 산출물 위치 (테스트는 저장소의 실제 제품 실행 파일을 사용한다)</summary>
public static class Products
{
	public static string RepoRoot { get; } = FindRepoRoot();

	/// <summary>테스트 어셈블리와 같은 구성 (Debug/Release)</summary>
	public static string Configuration { get; } = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

	public static string CliDir => Path.Combine(RepoRoot, "src", "ShipCheck.Cli", "bin", Configuration, "net8.0-windows");
	public static string CliExe => Path.Combine(CliDir, "ShipCheck.exe");
	public static string FakeBuildExe => Path.Combine(RepoRoot, "tests", "ShipCheck.FakeBuild", "bin", Configuration, "net8.0-windows", "ShipCheck.FakeBuild.exe");

	static string FindRepoRoot()
	{
		for (DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, "ShipCheck.sln")))
			{
				return dir.FullName;
			}
		}
		throw new InvalidOperationException("ShipCheck.sln 을 찾을 수 없습니다.");
	}
}

/// <summary>모의 UBT 동작 (fakebuild.json)</summary>
public sealed class FakeControl
{
	public bool Fail { get; set; }
	public string? FailSingleFilePattern { get; set; } = "Bug.cpp";
	public string? FailIfSourceContains { get; set; }
	public string? SourceRoot { get; set; }
	public string? StartedEvent { get; set; }
	public string? ReleaseEvent { get; set; }
	public bool SpawnChildren { get; set; }
	public bool LingerChild { get; set; }
	public string? PidFile { get; set; }
	public List<string> OutputLines { get; set; } = new();
	public int ExitCode { get; set; }
}

/// <summary>
/// 임시 SVN 저장소 + 워킹 카피 + 모의 프로젝트(Game: Runtime, GameEditor: Editor) + 모의 엔진(Build.bat → FakeBuild).
/// 경로에 공백과 한글을 넣어 인수/경로 보존을 함께 검증한다. 실제 프로젝트는 건드리지 않는다.
/// </summary>
public sealed class ShipCheckFixture : IDisposable
{
	public string Root { get; }
	public string RepoUrl { get; }
	public string WorkingCopy { get; }
	public string ProjectDir { get; }
	public string UProject { get; }
	public string EngineRoot { get; }
	public string BatchDir { get; }
	public string ToolDir { get; }
	public string ConfigPath { get; }
	public static string SvnExe { get; } = SvnClient.Locate() ?? throw new InvalidOperationException("svn.exe 가 필요합니다.");

	static readonly Dictionary<string, string> InitialFiles = new()
	{
		[@"Source\Game.Target.cs"] = "x",
		[@"Source\Game\Game.Build.cs"] = "x",
		[@"Source\Game\Private\Hero.cpp"] = "x",
		[@"Source\Game\Private\Hero.h"] = "x",
		[@"Source\Game\Private\Bug.cpp"] = "x",
		[@"Source\Game\Private\Helper.h"] = "x",
		[@"Source\GameEditor\GameEditor.Build.cs"] = "x",
		[@"Source\GameEditor\Private\EdTool.cpp"] = "x",
		[@"Content\Map.umap"] = "x",
	};

	/// <param name="asciiPath">true 면 공백/한글 없는 경로 (기존 PowerShell 엔진 비교용)</param>
	public ShipCheckFixture(bool asciiPath = false)
	{
		Root = Path.Combine(Path.GetTempPath(), (asciiPath ? "shipcheck-ascii-" : "shipcheck 테스트 ") + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(Root);
		try
		{
			EngineRoot = Path.Combine(Root, "Engine");
			BatchDir = Path.Combine(EngineRoot, "Engine", "Build", "BatchFiles");
			Directory.CreateDirectory(BatchDir);
			// cmd 는 .bat 을 OEM 코드 페이지로 읽는다
			File.WriteAllText(Path.Combine(BatchDir, "Build.bat"),
				$"@echo off\r\nset \"SHIPCHECK_FAKE_DIR=%~dp0\"\r\n\"{Products.FakeBuildExe}\" %*\r\nexit /b %ERRORLEVEL%\r\n", ProcessRunner.ConsoleEncoding);
			Control(new FakeControl());

			ToolDir = Path.Combine(Root, "tool");
			Directory.CreateDirectory(ToolDir);
			ConfigPath = Path.Combine(Root, "config.json");
			WriteConfig();

			string repo = Path.Combine(Root, "repo");
			Run("svnadmin", Path.Combine(Path.GetDirectoryName(SvnExe)!, "svnadmin.exe"), "create", repo);
			RepoUrl = new Uri(repo + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
			WorkingCopy = Path.Combine(Root, "wc");
			Svn("checkout", "-q", RepoUrl, WorkingCopy);

			ProjectDir = Path.Combine(WorkingCopy, "Game");
			UProject = Path.Combine(ProjectDir, "Game.uproject");
			foreach (string dir in new[] { @"Source\Game\Private", @"Source\GameEditor\Private", "Content" })
			{
				Directory.CreateDirectory(Path.Combine(ProjectDir, dir));
			}
			File.WriteAllText(UProject, JsonSerializer.Serialize(new
			{
				FileVersion = 3,
				EngineAssociation = "../../Engine",
				Modules = new[] { new { Name = "Game", Type = "Runtime" }, new { Name = "GameEditor", Type = "Editor" } },
			}));
			foreach (KeyValuePair<string, string> file in InitialFiles)
			{
				File.WriteAllText(F(file.Key), file.Value);
			}
			Svn("add", "-q", ProjectDir);
			Svn("commit", "-q", "-m", "init", WorkingCopy);
		}
		catch
		{
			// 준비 중 실패해도 임시 폴더를 남기지 않는다
			Dispose();
			throw;
		}
	}

	public string F(string relative) => Path.Combine(ProjectDir, relative);

	/// <summary>내용을 바꿔 수정 상태로 만든다</summary>
	public void Touch(string relative) => File.AppendAllText(F(relative), Guid.NewGuid().ToString() + "\n");

	public void Write(string relative, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(F(relative))!);
		File.WriteAllText(F(relative), text);
	}

	public void Revert(params string[] relative) => Svn(new[] { "revert", "-q", "-R" }.Concat(relative.Select(F)).ToArray());

	public void Commit(params string[] relative) => Svn(new[] { "commit", "-q", "-m", "fixture" }.Concat(relative.Select(F)).ToArray());

	public void WriteConfig(Action<ShipCheckConfig>? edit = null, string? path = null)
	{
		ShipCheckConfig config = new ShipCheckConfig();
		edit?.Invoke(config);
		File.WriteAllText(path ?? ConfigPath, config.ToJson());
	}

	public void Control(FakeControl control) => File.WriteAllText(Path.Combine(BatchDir, "fakebuild.json"), JsonSerializer.Serialize(control));

	/// <summary>모의 UBT 가 받은 인수 (호출 순서대로)</summary>
	public List<string[]> Calls()
	{
		string path = Path.Combine(BatchDir, "calls.jsonl");
		return File.Exists(path)
			? File.ReadAllLines(path).Where(x => x.Length > 0).Select(x => JsonSerializer.Deserialize<string[]>(x)!).ToList()
			: new List<string[]>();
	}

	public ShipCheckServices Services(bool withSvn = true) => ShipCheckHost.CreateServices(ToolDir, withSvn ? SvnExe : null, locateSvn: false);

	public ValidationService Service(bool withSvn = true) => new ValidationService(Services(withSvn));

	public Task<RunResult> ValidateAsync(IEnumerable<string> relativePaths, Func<ValidationRequest, ValidationRequest>? edit = null, Action<RunEvent>? onEvent = null,
		CancellationToken cancellationToken = default, bool withSvn = true, string? configPath = "")
	{
		ValidationRequest request = new ValidationRequest
		{
			Selector = new ProjectSelector(UProject, null, configPath == "" ? ConfigPath : configPath),
			Paths = relativePaths.Select(F).ToList(),
			Trigger = "test",
		};
		return Service(withSvn).ValidateAsync(edit?.Invoke(request) ?? request, onEvent, cancellationToken);
	}

	public Task<QueryResult> QueryAsync(bool withSvn = true, string? project = null) =>
		Service(withSvn).QueryAsync(new QueryRequest { Selector = new ProjectSelector(project ?? UProject, null, ConfigPath) }, CancellationToken.None);

	public ProcessOutput Svn(params string[] arguments) => Run("svn", SvnExe, arguments);

	static ProcessOutput Run(string name, string exe, params string[] arguments)
	{
		ProcessOutput output = ProcessRunner.RunAsync(exe, arguments, new UTF8Encoding(false), TimeSpan.FromMinutes(2), CancellationToken.None, errorEncoding: ProcessRunner.ConsoleEncoding).GetAwaiter().GetResult();
		if (output.ExitCode != 0)
		{
			throw new InvalidOperationException($"{name} {string.Join(" ", arguments)} 실패: {output.StandardError}");
		}
		return output;
	}

	public void Dispose()
	{
		for (int attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
		{
			try
			{
				foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}
				Directory.Delete(Root, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Thread.Sleep(200);
			}
		}
	}
}

/// <summary>프로세스 생존 확인</summary>
public static class Processes
{
	public static bool IsAlive(int pid)
	{
		try
		{
			using Process process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	/// <summary>모의 UBT 가 기록 중일 수 있으므로 공유 모드로 읽고, 잠겨 있으면 빈 목록 (WaitUntil 이 다시 확인한다)</summary>
	public static List<int> ReadPids(string pidFile)
	{
		try
		{
			using FileStream stream = new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new StreamReader(stream);
			return reader.ReadToEnd().Split('\n').Select(x => x.Trim()).Where(x => int.TryParse(x, out _)).Select(int.Parse).ToList();
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
		{
			return new List<int>();
		}
	}

	/// <summary>조건이 참이 될 때까지 짧게 확인 (임의 대기가 아니라 상태를 기다린다)</summary>
	public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
	{
		Stopwatch timer = Stopwatch.StartNew();
		while (!condition())
		{
			if (timer.Elapsed > timeout)
			{
				return false;
			}
			Thread.Sleep(50);
		}
		return true;
	}
}
