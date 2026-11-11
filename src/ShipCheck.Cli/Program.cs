using System.ComponentModel;
using System.Text;
using ShipCheck.Cli;
using ShipCheck.Core;
using ShipCheck.Infrastructure;

// ShipCheck.exe: 명령줄 / TortoiseSVN 훅 / ShipCommit 진입점.
// 종료 코드 0 = 커밋 허용, 1 = 검증 실패(차단), 2 = 실행 오류/취소/시간 초과/결과 확정 불가

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

CommandLine options;
try
{
	// 훅 인수는 경로뿐이므로 원본 명령줄을 cmd 방식으로 나눈다 ("D:\Work\" 처럼 \ 로 끝나는 경로 보존)
	options = args.Length > 0 && args[0] == "hook"
		? CommandLine.Parse(CommandLine.SplitRaw(Environment.CommandLine).Skip(1).ToList())
		: CommandLine.Parse(args);
}
catch (ArgumentException ex)
{
	Console.Error.WriteLine("ShipCheck: " + ex.Message);
	Console.Error.WriteLine(CommandLine.Usage);
	return ExitCodes.Error;
}

string toolDir = ShipCheckHost.LocateToolDir();
using CancellationTokenSource cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
	// Ctrl+C: UBT/컴파일러까지 정리한 뒤 종료한다
	e.Cancel = true;
	cancel.Cancel();
};

try
{
	return options.Command switch
	{
		"help" => Help(),
		"query" => await QueryAsync(options),
		"hook" => await HookAsync(options),
		"commit" => await CommitAsync(options),
		_ => await ValidateAsync(options, options.Positional.Concat(ReadPathsFile(options.PathsFile)).ToList(), ReadMessage(options)),
	};
}
catch (ArgumentException ex)
{
	Console.Error.WriteLine("ShipCheck: " + ex.Message);
	return ExitCodes.Error;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
	Console.Error.WriteLine("ShipCheck: 오류 - " + ex.Message);
	return ExitCodes.Error;
}

int Help()
{
	Console.WriteLine(CommandLine.Usage);
	return 0;
}

async Task<int> ValidateAsync(CommandLine cli, IReadOnlyList<string> paths, string? message)
{
	ValidationService service = ShipCheckHost.CreateService(toolDir);
	ValidationRequest request = new ValidationRequest
	{
		Selector = new ProjectSelector(cli.Project, cli.WorkingCopy, cli.ConfigPath),
		Paths = paths,
		Message = message,
		All = cli.All,
		Force = cli.Force,
		NoSingleFile = cli.NoSingleFile,
		DryRun = cli.DryRun,
		Trigger = cli.Trigger,
		StepTimeout = cli.StepTimeout,
	};
	RunResult result = await service.ValidateAsync(request, PrintEvent, cancel.Token);
	if (cli.ResultJson != null)
	{
		FileRunStore.WriteAtomic(cli.ResultJson, ShipCheckJson.Serialize(result));
	}
	if (!result.CommitAllowed)
	{
		// TortoiseSVN 은 stderr 를 커밋 창에 보여 준다
		ShipCheckConfig config = TryLoadConfig(cli);
		Console.Error.WriteLine(RunReport.FailureText(result, config));
	}
	else if (result.Outcome == RunOutcome.Skipped)
	{
		Info($"건너뜀: {result.Reason}");
	}
	return result.ExitCode;
}

ShipCheckConfig TryLoadConfig(CommandLine cli)
{
	try
	{
		return new ProjectLocator(toolDir).Resolve(new ProjectSelector(cli.Project, cli.WorkingCopy, cli.ConfigPath)).Config;
	}
	catch (ShipCheckException)
	{
		return new ShipCheckConfig();
	}
}

async Task<int> QueryAsync(CommandLine cli)
{
	ValidationService service = ShipCheckHost.CreateService(toolDir);
	QueryResult result;
	int code = 0;
	try
	{
		result = await service.QueryAsync(new QueryRequest { Selector = new ProjectSelector(cli.Project, cli.WorkingCopy, cli.ConfigPath) }, cancel.Token);
	}
	catch (ShipCheckException ex)
	{
		result = new QueryResult { Error = ex.Message, Diagnostics = { ex.Diagnostic } };
		Console.Error.WriteLine("ShipCheck: 오류 - " + ex.Message);
		code = ExitCodes.Error;
	}
	string json = ShipCheckJson.Serialize(result);
	if (cli.ResultJson != null)
	{
		FileRunStore.WriteAtomic(cli.ResultJson, json);
	}
	else
	{
		Console.WriteLine(json);
	}
	return code;
}

/// <summary>TortoiseSVN Pre-commit 훅: [프로젝트] PATH DEPTH MESSAGEFILE CWD</summary>
async Task<int> HookAsync(CommandLine cli)
{
	(string? project, string pathFile, string messageFile, string workingCopy) = CommandLine.ParseHookArguments(cli.Positional, Directory.Exists);
	CommandLine hook = new CommandLine { Project = project, WorkingCopy = workingCopy, Trigger = "hook", ConfigPath = cli.ConfigPath };
	return await ValidateAsync(hook, ReadPathsFile(pathFile).ToList(),File.Exists(messageFile) ? File.ReadAllText(messageFile, Encoding.UTF8) : null);
}

/// <summary>svn commit 대체: 커밋될 파일(svn status 와 같은 범위)을 검증하고 통과한 경우에만 커밋한다</summary>
async Task<int> CommitAsync(CommandLine cli)
{
	if (string.IsNullOrEmpty(cli.Message))
	{
		throw new ArgumentException("커밋 메시지가 필요합니다: commit -m \"메시지\"");
	}
	string? svn = SvnClient.Locate();
	if (svn == null)
	{
		Console.Error.WriteLine("ShipCheck: svn.exe 를 찾을 수 없어 커밋할 수 없습니다.");
		return ExitCodes.Error;
	}
	List<string> targets = cli.Positional.Count > 0 ? cli.Positional : new List<string> { "." };
	ProcessOutput status;
	try
	{
		status = await ProcessRunner.RunAsync(svn, new[] { "status", "-q", "--xml", "--non-interactive" }.Concat(targets), new UTF8Encoding(false), TimeSpan.FromMinutes(2), cancel.Token, errorEncoding: ProcessRunner.ConsoleEncoding);
	}
	catch (Exception ex) when (ex is Win32Exception or TimeoutException)
	{
		Console.Error.WriteLine("ShipCheck: svn status 실패 - " + ex.Message);
		return ExitCodes.Error;
	}
	if (status.ExitCode != 0)
	{
		Console.Error.WriteLine(status.StandardError.Trim());
		return ExitCodes.Error;
	}
	List<string> files = SvnXml.ParseStatus(status.StandardOutput)
		.Where(x => x.StatusLetter is "A" or "M" or "R" or "D")
		.Select(x => Path.GetFullPath(x.Path))
		.ToList();
	if (files.Count == 0)
	{
		Console.WriteLine("커밋할 변경 사항이 없습니다.");
		return 0;
	}

	int code = await ValidateAsync(new CommandLine { Project = cli.Project, WorkingCopy = Environment.CurrentDirectory, ConfigPath = cli.ConfigPath, Force = cli.Force, Trigger = "cli", ResultJson = cli.ResultJson }, files, cli.Message);
	if (code != ExitCodes.Allowed)
	{
		Console.Error.WriteLine("[ShipCommit] 검증을 통과하지 못해 커밋하지 않았습니다.");
		return code;
	}

	string messageFile = Path.Combine(Path.GetTempPath(), $"ShipCommit_{Guid.NewGuid():N}.txt");
	try
	{
		File.WriteAllText(messageFile, cli.Message, new UTF8Encoding(false));
		ProcessOutput commit = await ProcessRunner.RunAsync(svn, new[] { "commit", "--non-interactive", "-F", messageFile, "--encoding", "utf-8" }.Concat(targets),
			ProcessRunner.ConsoleEncoding, TimeSpan.FromMinutes(30), CancellationToken.None);
		Console.Write(commit.StandardOutput);
		Console.Error.Write(commit.StandardError);
		return commit.ExitCode;
	}
	finally
	{
		File.Delete(messageFile);
	}
}

static IEnumerable<string> ReadPathsFile(string? path) =>
	path == null ? Array.Empty<string>() : File.ReadAllLines(path, Encoding.UTF8).Select(x => x.Trim()).Where(x => x.Length > 0);

static string? ReadMessage(CommandLine cli) =>
	cli.MessageFile != null && File.Exists(cli.MessageFile) ? File.ReadAllText(cli.MessageFile, Encoding.UTF8) : cli.Message;

static void Info(string message)
{
	ConsoleColor previous = Console.ForegroundColor;
	Console.ForegroundColor = ConsoleColor.Cyan;
	Console.WriteLine("[ShipCheck] " + message);
	Console.ForegroundColor = previous;
}

static void PrintEvent(RunEvent e)
{
	switch (e.Kind)
	{
		case RunEventKind.Output:
			Console.WriteLine(e.Message);
			break;
		case RunEventKind.Warning:
			ConsoleColor previous = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("[ShipCheck] " + e.Message);
			Console.ForegroundColor = previous;
			break;
		default:
			Info(e.Message);
			break;
	}
}
