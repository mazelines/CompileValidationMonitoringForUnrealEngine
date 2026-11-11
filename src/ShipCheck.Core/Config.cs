using System.Text;
using System.Text.Json;

namespace ShipCheck.Core;

/// <summary>
/// ShipCheck.config.json / ShipCheck.&lt;프로젝트명&gt;.config.json 형식 (기존 스크립트와 같다)
/// </summary>
public sealed class ShipCheckConfig
{
	public string UProject { get; set; } = "";
	public string EngineDir { get; set; } = "";
	public List<TargetConfig> Targets { get; set; } = new() { new TargetConfig() };
	public List<string> ExtraUbtArgs { get; set; } = new();
	public SingleFileConfig SingleFileCheck { get; set; } = new();
	public bool SkipEditorOnlyChanges { get; set; } = true;
	/// <summary>Warn / Fail</summary>
	public string UncommittedSourceChanges { get; set; } = "Warn";
	public string SkipTag { get; set; } = "[skip-shipcheck]";
	public bool UseCache { get; set; } = true;
	public int MaxErrorLines { get; set; } = 25;

	public bool FailOnUncommittedChanges => UncommittedSourceChanges.Equals("Fail", StringComparison.OrdinalIgnoreCase);

	/// <summary>파일 내용 해석. 형식 오류는 설정 오류로 보고한다 (기본값으로 조용히 바꾸지 않는다).</summary>
	public static ShipCheckConfig Parse(string json, string path)
	{
		try
		{
			ShipCheckConfig config = ShipCheckJson.Deserialize<ShipCheckConfig>(json) ?? new ShipCheckConfig();
			config.Targets ??= new List<TargetConfig>();
			config.ExtraUbtArgs ??= new List<string>();
			config.SingleFileCheck ??= new SingleFileConfig();
			config.UProject ??= "";
			config.EngineDir ??= "";
			config.SkipTag ??= "";
			config.UncommittedSourceChanges ??= "Warn";
			return config;
		}
		catch (JsonException ex)
		{
			throw new ShipCheckException(ErrorKind.ConfigError,
				Diagnostic.Error(DiagnosticCodes.ConfigInvalid, "project", $"설정 파일 형식 오류: {path}", ex.Message), ex);
		}
	}

	public string ToJson() => ShipCheckJson.Serialize(this);

	public static string PerProjectFileName(string projectName) => $"ShipCheck.{projectName}.config.json";

	public const string DefaultFileName = "ShipCheck.config.json";
}

public sealed class TargetConfig
{
	public string Name { get; set; } = "";
	public string Platform { get; set; } = "Win64";
	public string Configuration { get; set; } = "Shipping";
}

public sealed class SingleFileConfig
{
	public bool Enabled { get; set; } = true;
	public bool IncludeHeaders { get; set; } = true;
	public int MaxFiles { get; set; } = 30;
}

/// <summary>
/// 설정 파일 선택: 명시 경로 &gt; 도구 폴더의 ShipCheck.&lt;프로젝트명&gt;.config.json &gt; ShipCheck.config.json.
/// 프로젝트를 자동 탐색한 경우에도 탐색이 끝난 뒤 프로젝트 전용 설정을 다시 고른다.
/// </summary>
public static class ConfigSelector
{
	public static string Select(string? explicitPath, string? uproject, string toolDir, Func<string, bool> fileExists)
	{
		if (!string.IsNullOrEmpty(explicitPath))
		{
			return explicitPath;
		}
		if (!string.IsNullOrEmpty(uproject))
		{
			string perProject = Path.Combine(toolDir, ShipCheckConfig.PerProjectFileName(Path.GetFileNameWithoutExtension(uproject)));
			if (fileExists(perProject))
			{
				return perProject;
			}
		}
		return Path.Combine(toolDir, ShipCheckConfig.DefaultFileName);
	}
}

/// <summary>검증 타깃 하나 (이름이 비면 프로젝트 이름)</summary>
public sealed record BuildTarget(string Name, string Platform, string Configuration)
{
	public override string ToString() => $"{Name} {Platform} {Configuration}";

	public static List<BuildTarget> FromConfig(ShipCheckConfig config, string projectName)
	{
		List<BuildTarget> targets = config.Targets
			.Select(x => new BuildTarget(string.IsNullOrWhiteSpace(x.Name) ? projectName : x.Name.Trim(),
				string.IsNullOrWhiteSpace(x.Platform) ? "Win64" : x.Platform.Trim(),
				string.IsNullOrWhiteSpace(x.Configuration) ? "Shipping" : x.Configuration.Trim()))
			.ToList();
		if (targets.Count == 0)
		{
			targets.Add(new BuildTarget(projectName, "Win64", "Shipping"));
		}
		return targets;
	}
}

/// <summary>설정과 타깃에서 정한 검증 정책</summary>
public sealed class ValidationPolicy
{
	/// <summary>에디터 전용(및 Shipping/Test 의 Developer) 모듈 변경은 빌드하지 않는다</summary>
	public bool SkipNonTargetModules { get; init; }
	/// <summary>Developer 모듈은 Debug/Development 구성에만 포함된다</summary>
	public bool IncludeDeveloperModules { get; init; }
	public bool SingleFileEnabled { get; init; }
	public bool IncludeHeaders { get; init; }
	public int MaxSingleFiles { get; init; }
	public IReadOnlyList<string> ExtraUbtArgs { get; init; } = Array.Empty<string>();
	public bool SkipEditorOnlyChanges { get; init; }

	public static ValidationPolicy From(ShipCheckConfig config, IReadOnlyList<BuildTarget> targets, bool noSingleFile)
	{
		return new ValidationPolicy
		{
			// 에디터 타깃을 검사하는 경우 에디터 모듈 변경도 빌드해야 한다
			SkipNonTargetModules = config.SkipEditorOnlyChanges && !targets.Any(x => x.Name.EndsWith("Editor", StringComparison.OrdinalIgnoreCase)),
			IncludeDeveloperModules = targets.Any(x => !x.Configuration.Equals("Shipping", StringComparison.OrdinalIgnoreCase) && !x.Configuration.Equals("Test", StringComparison.OrdinalIgnoreCase)),
			SingleFileEnabled = config.SingleFileCheck.Enabled && !noSingleFile,
			IncludeHeaders = config.SingleFileCheck.IncludeHeaders,
			MaxSingleFiles = Math.Max(1, config.SingleFileCheck.MaxFiles),
			ExtraUbtArgs = config.ExtraUbtArgs.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
			SkipEditorOnlyChanges = config.SkipEditorOnlyChanges,
		};
	}

	/// <summary>
	/// 검증 결과에 영향을 주는 정책 전체. 정책을 강화하면 예전의 약한 검사 통과 기록을 재사용하지 않는다.
	/// 인수 배열은 JSON 으로 인코딩하여 서로 다른 인수 목록이 같은 문자열이 되지 않게 한다 ("a b" 와 "a","b").
	/// </summary>
	public string ToKeyText()
	{
		StringBuilder text = new StringBuilder();
		text.Append("ExtraUbtArgs=").AppendLine(JsonSerializer.Serialize(ExtraUbtArgs));
		text.Append("SingleFile.Enabled=").AppendLine(SingleFileEnabled.ToString());
		text.Append("SingleFile.IncludeHeaders=").AppendLine(IncludeHeaders.ToString());
		text.Append("SingleFile.MaxFiles=").AppendLine(MaxSingleFiles.ToString());
		text.Append("SkipEditorOnlyChanges=").AppendLine(SkipEditorOnlyChanges.ToString());
		return text.ToString();
	}
}
