using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using ShipCheck.Infrastructure;

namespace ShipCheckMonitor;

/// <summary>
/// TortoiseSVN 창, 코드 편집기, 실행 중인 프로세스 등 GUI 전용 외부 연동.
/// (훅 등록 해석과 편집기 명령 구성은 ShipCheck.Infrastructure 에 있다)
/// </summary>
static class Integrations
{
	#region TortoiseSVN

	public static string? FindTortoiseProc()
	{
		string? fromRegistry = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\TortoiseSVN", "ProcPath", null) as string;
		if (!string.IsNullOrEmpty(fromRegistry) && File.Exists(fromRegistry))
		{
			return fromRegistry;
		}
		string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TortoiseSVN", "bin", "TortoiseProc.exe");
		return File.Exists(defaultPath) ? defaultPath : null;
	}

	/// <summary>
	/// TortoiseProc 명령 실행 (commit, update, diff, log, settings ...). 창이 닫힐 때까지 기다리는 Task 반환.
	/// </summary>
	public static Task RunTortoiseAsync(string command, string? path)
	{
		string? tortoiseProc = FindTortoiseProc();
		if (tortoiseProc == null)
		{
			throw new InvalidOperationException("TortoiseSVN 을 찾을 수 없습니다.");
		}
		ProcessStartInfo startInfo = new ProcessStartInfo(tortoiseProc) { UseShellExecute = false };
		startInfo.ArgumentList.Add($"/command:{command}");
		if (path != null)
		{
			startInfo.ArgumentList.Add($"/path:{path}");
		}
		Process process = Process.Start(startInfo)!;
		return process.WaitForExitAsync().ContinueWith(_ => process.Dispose());
	}

	#endregion

	#region 편집기 / 탐색기

	public static void OpenInEditor(string editorCommand, string file, int line)
	{
		if (string.IsNullOrWhiteSpace(editorCommand) || line <= 0)
		{
			OpenWithShell(file);
			return;
		}
		Process.Start(EditorCommands.CreateStartInfo(editorCommand, file, line))?.Dispose();
	}

	public static void OpenWithShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

	public static void ShowInExplorer(string path)
	{
		if (File.Exists(path))
		{
			Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
		}
		else if (Directory.Exists(path))
		{
			Process.Start("explorer.exe", $"\"{path}\"")?.Dispose();
		}
	}

	#endregion

	#region 프로세스

	public static bool IsUnrealEditorRunning()
	{
		foreach (string name in new[] { "UnrealEditor", "UE4Editor" })
		{
			Process[] processes = Process.GetProcessesByName(name);
			bool running = processes.Length > 0;
			foreach (Process process in processes)
			{
				process.Dispose();
			}
			if (running)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// UBT 실행 여부. UE5 는 "dotnet UnrealBuildTool.dll" 로 실행되므로 명령줄까지 확인한다 (WMI).
	/// 사용자의 에디터/IDE 빌드나 Live Coding 과 겹치지 않도록 자동 검증 직전에만 호출한다.
	/// </summary>
	public static bool IsUnrealBuildToolRunning()
	{
		try
		{
			using ManagementObjectSearcher searcher = new ManagementObjectSearcher(
				"SELECT Name, CommandLine FROM Win32_Process WHERE Name = 'dotnet.exe' OR Name = 'UnrealBuildTool.exe'");
			foreach (ManagementBaseObject process in searcher.Get())
			{
				using (process)
				{
					string name = process["Name"] as string ?? "";
					string commandLine = process["CommandLine"] as string ?? "";
					if (name.Equals("UnrealBuildTool.exe", StringComparison.OrdinalIgnoreCase) ||
						commandLine.Contains("UnrealBuildTool", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}
		catch (ManagementException)
		{
			// 확인할 수 없으면 실행 중으로 간주하여 자동 검증을 미룬다
			return true;
		}
	}

	#endregion
}
