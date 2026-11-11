using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ShipCheck.Core;

namespace ShipCheck.Infrastructure;

/// <summary>
/// Build.bat 실행기. cmd.exe 와 그 자손(UBT, cl.exe, link.exe ...)을 Job Object 에 넣어
/// 취소/시간 초과 시 손자까지 종료하고, 남은 프로세스가 0 인지 확인한다.
/// </summary>
public sealed class BuildRunner : IBuildRunner
{
	static readonly TimeSpan TerminateTimeout = TimeSpan.FromSeconds(15);
	static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
	// cmd 경계에서 안전하게 전달할 수 없는 문자: " (인용), % (변수 확장), ! (Build.bat 의 지연 확장), ^ (call 이 따옴표 안의 ^ 를 두 배로 만듦)
	static readonly char[] Unsupported = { '"', '%', '!', '^', '\r', '\n' };
	static readonly char[] NeedsQuotes = { ' ', '\t', '&', '|', '<', '>', '(', ')' };

	public async Task<BuildProcessResult> RunAsync(BuildInvocation invocation, Action<string> onLine, CancellationToken cancellationToken)
	{
		string command = CreateCommandLine(invocation.BuildBat, invocation.Arguments);
		Directory.CreateDirectory(Path.GetDirectoryName(invocation.LogPath)!);

		ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = ProcessRunner.ConsoleEncoding,
			StandardErrorEncoding = ProcessRunner.ConsoleEncoding,
			WorkingDirectory = Path.GetDirectoryName(invocation.BuildBat)!,
		};

		using StreamWriter log = new StreamWriter(invocation.LogPath, false, new UTF8Encoding(true));
		object logLock = new object();
		void Line(string line)
		{
			lock (logLock)
			{
				log.WriteLine(line);
			}
			onLine(line);
		}
		TaskCompletionSource outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		using JobObject job = new JobObject();
		using Process process = new Process { StartInfo = startInfo };
		process.OutputDataReceived += (_, e) => { if (e.Data == null) { outputClosed.TrySetResult(); } else { Line(e.Data); } };
		process.ErrorDataReceived += (_, e) => { if (e.Data == null) { errorClosed.TrySetResult(); } else { Line(e.Data); } };
		try
		{
			process.Start();
		}
		catch (Win32Exception ex)
		{
			throw new ShipCheckException(ErrorKind.ExternalToolFailed, Diagnostic.Error(DiagnosticCodes.BuildToolFailed, invocation.Label, "cmd.exe 를 실행할 수 없습니다.", ex.Message), ex);
		}
		try
		{
			job.Assign(process);
		}
		catch (Win32Exception ex)
		{
			// 게이트를 열지 않았으므로 Build.bat 은 아직 실행되지 않았다
			TryKill(process);
			throw new ShipCheckException(ErrorKind.ExternalToolFailed, Diagnostic.Error(DiagnosticCodes.BuildToolFailed, invocation.Label, "빌드 프로세스를 Job Object 에 넣을 수 없습니다.", ex.Message), ex);
		}
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		// 시작 게이트: cmd 는 stdin 한 줄을 읽은 뒤에야 Build.bat 을 실행한다 → Job 에 넣기 전에 생긴 자손이 없다
		try
		{
			process.StandardInput.WriteLine("go");
			process.StandardInput.Close();
		}
		catch (IOException)
		{
			// cmd 가 이미 끝났다 (종료 코드로 판정)
		}

		bool canceled = false;
		bool timedOut = false;
		using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			if (invocation.Timeout is TimeSpan timeout)
			{
				limit.CancelAfter(timeout);
			}
			try
			{
				// WaitForExitAsync 는 출력 EOF 까지 기다리므로, 파이프를 문 잔류 프로세스가 있으면 끝나지 않는다 → 종료만 확인
				while (!process.HasExited)
				{
					await Task.Delay(100, limit.Token);
				}
			}
			catch (OperationCanceledException)
			{
				canceled = cancellationToken.IsCancellationRequested;
				timedOut = !canceled;
			}
		}

		// 정상 종료여도 Job 에 남은 자손(출력 파이프를 문 mspdbsrv 등)을 정리해야 출력 읽기가 끝난다
		int leftover = await Task.Run(() => job.TerminateAndWait(TerminateTimeout));
		if (leftover > 0)
		{
			// Job 종료가 막힌 경우를 대비한 보조 수단
			TryKill(process);
			leftover = await Task.Run(() => job.TerminateAndWait(TerminateTimeout));
		}
		for (Stopwatch wait = Stopwatch.StartNew(); !process.HasExited && wait.Elapsed < TerminateTimeout;)
		{
			await Task.Delay(50);
		}
		await Task.WhenAny(Task.WhenAll(outputClosed.Task, errorClosed.Task), Task.Delay(DrainTimeout));

		int exitCode = canceled || timedOut || !process.HasExited ? -1 : process.ExitCode;
		lock (logLock)
		{
			log.WriteLine(canceled ? "[ShipCheck] 사용자 중지" : timedOut ? "[ShipCheck] 시간 초과로 중지" : $"[ShipCheck] 종료코드 {exitCode}");
		}
		return new BuildProcessResult(exitCode, timedOut, canceled, leftover);
	}

	/// <summary>
	/// cmd /s /c 에 넘길 명령줄. 첫 명령 "set /p" 는 시작 게이트(stdin 한 줄 대기)이고, 읽기에 실패하면(&&) Build.bat 을 실행하지 않는다.
	/// </summary>
	public static string CreateCommandLine(string buildBat, IReadOnlyList<string> arguments)
	{
		StringBuilder command = new StringBuilder("set /p SHIPCHECK_GATE=&&");
		command.Append('"').Append(Validate(buildBat, "Build.bat 경로")).Append('"');
		foreach (string argument in arguments)
		{
			command.Append(' ').Append(QuoteArgument(argument));
		}
		return command.ToString();
	}

	public static string QuoteArgument(string argument)
	{
		Validate(argument, "UBT 인수");
		return argument.Length == 0 || argument.IndexOfAny(NeedsQuotes) >= 0 ? $"\"{argument}\"" : argument;
	}

	static string Validate(string value, string what)
	{
		int index = value.IndexOfAny(Unsupported);
		if (index >= 0)
		{
			throw new ShipCheckException(ErrorKind.ConfigError, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "build",
				$"{what}에 cmd 로 안전하게 전달할 수 없는 문자 '{value[index]}' 가 있습니다: {value}",
				"\" % ! ^ 와 줄바꿈은 Build.bat 경계에서 지원하지 않습니다. 경로/설정의 ExtraUbtArgs 를 확인하세요."));
		}
		return value;
	}

	static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
		}
		catch (Win32Exception)
		{
		}
	}
}
