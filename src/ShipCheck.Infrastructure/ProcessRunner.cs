using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ShipCheck.Infrastructure;

public sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

/// <summary>짧게 끝나는 외부 도구(svn 등) 실행. 인수는 ArgumentList 로 넘겨 경계를 보존한다.</summary>
public static class ProcessRunner
{
	[DllImport("kernel32.dll")]
	static extern uint GetOEMCP();

	static ProcessRunner()
	{
		// 속성 초기화식은 정적 생성자 본문보다 먼저 실행되므로 등록 후 여기서 만든다
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		ConsoleEncoding = Encoding.GetEncoding((int)GetOEMCP());
	}

	/// <summary>숨김 콘솔 프로세스(cmd/Build.bat/UBT/cl)의 출력 인코딩 = 시스템 OEM 코드 페이지 (한국어 Windows: 949)</summary>
	public static Encoding ConsoleEncoding { get; }

	/// <exception cref="Win32Exception">실행 파일을 시작할 수 없음</exception>
	/// <exception cref="TimeoutException">제한 시간 초과 (프로세스 트리는 종료됨)</exception>
	public static async Task<ProcessOutput> RunAsync(string fileName, IEnumerable<string> arguments, Encoding outputEncoding, TimeSpan timeout, CancellationToken cancellationToken,
		string? workingDirectory = null, Encoding? errorEncoding = null)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo(fileName)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			StandardOutputEncoding = outputEncoding,
			StandardErrorEncoding = errorEncoding ?? outputEncoding,
		};
		if (workingDirectory != null)
		{
			startInfo.WorkingDirectory = workingDirectory;
		}
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using Process process = new Process { StartInfo = startInfo };
		process.Start();
		process.StandardInput.Close();
		Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(timeout);
		try
		{
			await process.WaitForExitAsync(limit.Token);
		}
		catch (OperationCanceledException)
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
			await process.WaitForExitAsync(CancellationToken.None);
			cancellationToken.ThrowIfCancellationRequested();
			throw new TimeoutException($"{Path.GetFileName(fileName)} 이(가) {timeout.TotalSeconds:0}초 안에 끝나지 않았습니다.");
		}
		return new ProcessOutput(process.ExitCode, await stdout, await stderr);
	}
}
