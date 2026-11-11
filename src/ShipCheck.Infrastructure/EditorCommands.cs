using System.ComponentModel;
using System.Diagnostics;

namespace ShipCheck.Infrastructure;

/// <summary>오류 위치를 여는 편집기 명령 ({file}, {line} 치환)</summary>
public static class EditorCommands
{
	/// <summary>설치된 편집기로 기본 명령을 정한다: VS Code → Visual Studio → (없으면 빈 문자열 = 기본 프로그램)</summary>
	public static string Detect()
	{
		string vsCode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "bin", "code.cmd");
		if (File.Exists(vsCode))
		{
			return $"\"{vsCode}\" -g \"{{file}}:{{line}}\"";
		}
		string? devenv = FindDevenv();
		return devenv != null ? $"\"{devenv}\" /edit \"{{file}}\" /command \"Edit.GoTo {{line}}\"" : "";
	}

	static string? FindDevenv()
	{
		string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (!File.Exists(vswhere))
		{
			return null;
		}
		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo(vswhere, "-latest -property productPath") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
			using Process process = Process.Start(startInfo)!;
			string output = process.StandardOutput.ReadToEnd().Trim();
			process.WaitForExit(5000);
			return File.Exists(output) ? output : null;
		}
		catch (Win32Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// .cmd 실행 파일(code.cmd 등)도 동작하도록 cmd 를 거친다.
	/// cmd 는 \" 이스케이프를 모르므로 ArgumentList 가 아닌 원문 인자로 넘기고, /s 로 바깥 따옴표 한 쌍만 벗기게 한다.
	/// </summary>
	public static ProcessStartInfo CreateStartInfo(string editorCommand, string file, int line)
	{
		string command = editorCommand.Replace("{file}", file).Replace("{line}", line.ToString());
		return new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"") { UseShellExecute = false, CreateNoWindow = true };
	}
}
