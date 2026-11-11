using System.Text.Json;
using ShipCheck.Core;
using ShipCheck.Infrastructure;

namespace ShipCheckMonitor;

/// <summary>
/// 사용자별 앱 설정 (%LOCALAPPDATA%\ShipCheckMonitor\settings.json).
/// 프로젝트별 검증 설정은 커밋 훅/명령줄과 같은 ShipCheckConfig (도구 폴더의 ShipCheck.&lt;프로젝트명&gt;.config.json).
/// </summary>
sealed class AppSettings
{
	public List<string> Projects { get; set; } = new();
	public string? SelectedProject { get; set; }

	/// <summary>코드 변경 후 유휴 시간이 지나면 백그라운드에서 자동 검증 (커밋 시 캐시로 즉시 통과)</summary>
	public bool AutoValidate { get; set; }
	public int IdleSeconds { get; set; } = 120;
	public bool SkipAutoWhileEditorRunning { get; set; } = true;

	/// <summary>오류 위치를 여는 명령. {file}, {line} 치환. 비우면 기본 프로그램으로 파일만 연다.</summary>
	public string EditorCommand { get; set; } = "";

	public bool MinimizeToTray { get; set; } = true;
	public bool NotifyOnResult { get; set; } = true;

	public int WindowX { get; set; } = -1;
	public int WindowY { get; set; } = -1;
	public int WindowWidth { get; set; } = 1180;
	public int WindowHeight { get; set; } = 820;
	public bool WindowMaximized { get; set; }
	public int SplitterDistance { get; set; } = 330;

	static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShipCheckMonitor", "settings.json");

	public static AppSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				AppSettings? settings = ShipCheckJson.Deserialize<AppSettings>(File.ReadAllText(FilePath));
				if (settings != null)
				{
					if (string.IsNullOrWhiteSpace(settings.EditorCommand))
					{
						settings.EditorCommand = EditorCommands.Detect();
					}
					return settings;
				}
			}
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
		}
		return new AppSettings { EditorCommand = EditorCommands.Detect() };
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			FileRunStore.WriteAtomic(FilePath, ShipCheckJson.Serialize(this));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}
}
