using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ShipCheck.Core;

namespace ShipCheck.Infrastructure;

/// <summary>프로젝트/설정/엔진 탐색 (기존 스크립트와 같은 순서와 상대 경로 규칙)</summary>
public sealed class ProjectLocator : IProjectLocator
{
	static readonly Regex GuidPattern = new Regex(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$", RegexOptions.Compiled);
	static readonly Regex VersionPattern = new Regex(@"^\d+\.\d+", RegexOptions.Compiled);

	readonly string _toolDir;

	public ProjectLocator(string toolDir) => _toolDir = toolDir;

	public ProjectContext Resolve(ProjectSelector selector)
	{
		string currentDir = Environment.CurrentDirectory;
		string? workingCopy = string.IsNullOrWhiteSpace(selector.WorkingCopy) ? null : Path.GetFullPath(selector.WorkingCopy);
		string? uproject = string.IsNullOrWhiteSpace(selector.Project) ? null : ResolveUProjectPath(selector.Project, currentDir);
		string? explicitConfig = string.IsNullOrWhiteSpace(selector.ConfigPath) ? null : Path.GetFullPath(selector.ConfigPath);
		if (explicitConfig != null && !File.Exists(explicitConfig))
		{
			throw ConfigError(DiagnosticCodes.ConfigInvalid, $"지정한 설정 파일이 없습니다: {explicitConfig}");
		}

		// --- 설정: 명시 경로 > ShipCheck.<프로젝트명>.config.json > ShipCheck.config.json (도구 폴더) ---
		string configPath = ConfigSelector.Select(explicitConfig, uproject, _toolDir, File.Exists);
		(ShipCheckConfig config, string configDir) = LoadConfig(configPath);

		if (uproject == null)
		{
			uproject = FindUProject(config, configDir, workingCopy ?? currentDir);
			if (uproject == null)
			{
				throw ConfigError(DiagnosticCodes.ProjectNotFound, ".uproject 파일을 찾을 수 없습니다. --project 인수나 설정의 UProject 를 지정하세요.");
			}
			// 자동 탐색한 프로젝트에도 전용 설정을 적용한다 (명시한 설정 파일이 우선)
			string selected = ConfigSelector.Select(explicitConfig, uproject, _toolDir, File.Exists);
			if (!selected.Equals(configPath, StringComparison.OrdinalIgnoreCase))
			{
				configPath = selected;
				(config, configDir) = LoadConfig(configPath);
			}
		}

		return new ProjectContext
		{
			UProject = uproject,
			ConfigPath = configPath,
			Config = config,
			ToolDir = _toolDir,
			Targets = BuildTarget.FromConfig(config, Path.GetFileNameWithoutExtension(uproject)),
		};
	}

	/// <summary>설정 파일이 없으면 기본값 (기존 동작). 형식 오류는 설정 오류.</summary>
	static (ShipCheckConfig Config, string ConfigDir) LoadConfig(string configPath)
	{
		if (!File.Exists(configPath))
		{
			return (new ShipCheckConfig(), Path.GetDirectoryName(configPath)!);
		}
		try
		{
			return (ShipCheckConfig.Parse(File.ReadAllText(configPath), configPath), Path.GetDirectoryName(Path.GetFullPath(configPath))!);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new ShipCheckException(ErrorKind.ConfigError, Diagnostic.Error(DiagnosticCodes.ConfigInvalid, "project", $"설정 파일을 읽을 수 없습니다: {configPath}", ex.Message), ex);
		}
	}

	/// <summary>.uproject 파일 또는 프로젝트 폴더 경로를 .uproject 전체 경로로 변환</summary>
	public static string ResolveUProjectPath(string path, string baseDir)
	{
		string full = PathUtil.Normalize(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
		if (Directory.Exists(full))
		{
			return Directory.EnumerateFiles(full, "*.uproject").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
				?? throw ConfigError(DiagnosticCodes.ProjectNotFound, $"폴더에 .uproject 파일이 없습니다: {full}");
		}
		if (!File.Exists(full))
		{
			throw ConfigError(DiagnosticCodes.ProjectNotFound, $"프로젝트 파일을 찾을 수 없습니다: {full}");
		}
		return full;
	}

	string? FindUProject(ShipCheckConfig config, string configDir, string workingCopy)
	{
		if (!string.IsNullOrWhiteSpace(config.UProject))
		{
			return ResolveUProjectPath(config.UProject, configDir);
		}
		foreach (string start in new[] { workingCopy, _toolDir })
		{
			string? found = FindUpward(start);
			if (found != null)
			{
				return found;
			}
		}
		// 워킹 카피 루트 아래 1~2 단계에 프로젝트가 있는 경우
		if (Directory.Exists(workingCopy))
		{
			foreach (string dir in new[] { workingCopy }.Concat(SafeSubdirectories(workingCopy)).Concat(SafeSubdirectories(workingCopy).SelectMany(SafeSubdirectories)))
			{
				string? found = SafeFiles(dir, "*.uproject").FirstOrDefault();
				if (found != null)
				{
					return found;
				}
			}
		}
		return null;
	}

	static string? FindUpward(string start)
	{
		for (DirectoryInfo? dir = Directory.Exists(start) ? new DirectoryInfo(start) : null; dir != null; dir = dir.Parent)
		{
			string? found = SafeFiles(dir.FullName, "*.uproject").FirstOrDefault();
			if (found != null)
			{
				return found;
			}
		}
		return null;
	}

	static IEnumerable<string> SafeSubdirectories(string dir)
	{
		try
		{
			return Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase).ToList();
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			return Array.Empty<string>();
		}
	}

	static IEnumerable<string> SafeFiles(string dir, string pattern)
	{
		try
		{
			return Directory.GetFiles(dir, pattern).Order(StringComparer.OrdinalIgnoreCase).ToList();
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			return Array.Empty<string>();
		}
	}

	public string ResolveEngineRoot(ProjectContext context)
	{
		string configDir = Path.GetDirectoryName(context.ConfigPath) ?? _toolDir;
		if (!string.IsNullOrWhiteSpace(context.Config.EngineDir))
		{
			string configured = Path.IsPathRooted(context.Config.EngineDir) ? context.Config.EngineDir : Path.Combine(configDir, context.Config.EngineDir);
			return EngineRootFromCandidate(Path.GetFullPath(configured))
				?? throw ConfigError(DiagnosticCodes.EngineNotFound, $"설정된 EngineDir 에서 Engine\\Build\\BatchFiles\\Build.bat 를 찾을 수 없습니다: {context.Config.EngineDir}");
		}

		string association = ReadEngineAssociation(context.UProject);
		if (association.Length > 0)
		{
			string? candidate = null;
			if (GuidPattern.IsMatch(association))
			{
				// 소스 빌드 엔진 (UnrealVersionSelector 로 등록된 GUID)
				using RegistryKey? builds = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
				string? name = builds?.GetValueNames().FirstOrDefault(x => x.Trim('{', '}').Equals(association.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
				candidate = name == null ? null : builds!.GetValue(name) as string;
			}
			else if (VersionPattern.IsMatch(association))
			{
				// 런처 설치 엔진
				using RegistryKey? installed = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\EpicGames\Unreal Engine\{association}");
				candidate = installed?.GetValue("InstalledDirectory") as string;
			}
			else
			{
				candidate = Path.GetFullPath(Path.Combine(context.ProjectDir, association));
			}
			string? root = EngineRootFromCandidate(candidate);
			if (root != null)
			{
				return root;
			}
		}

		// 엔진과 프로젝트가 같은 트리에 있는 경우 (네이티브 프로젝트)
		for (DirectoryInfo? dir = new DirectoryInfo(context.ProjectDir); dir != null; dir = dir.Parent)
		{
			string? root = EngineRootFromCandidate(dir.FullName);
			if (root != null)
			{
				return root;
			}
		}
		throw ConfigError(DiagnosticCodes.EngineNotFound, $"엔진 경로를 찾을 수 없습니다 (EngineAssociation='{association}'). 설정의 EngineDir 을 지정하세요.");
	}

	static string ReadEngineAssociation(string uproject)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(uproject), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			return ModuleResolver.TryGet(document.RootElement, "EngineAssociation", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			throw new ShipCheckException(ErrorKind.ConfigError, Diagnostic.Error(DiagnosticCodes.ProjectNotFound, "project", $".uproject 를 읽을 수 없습니다: {uproject}", ex.Message), ex);
		}
	}

	static string? EngineRootFromCandidate(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		path = path.TrimEnd('\\', '/');
		if (File.Exists(Path.Combine(path, "Engine", "Build", "BatchFiles", "Build.bat")))
		{
			return path;
		}
		if (File.Exists(Path.Combine(path, "Build", "BatchFiles", "Build.bat")))
		{
			return Path.GetDirectoryName(path);
		}
		return null;
	}

	/// <summary>타깃의 증분 빌드 산출물(Intermediate)이 있는지 = 프라이밍 여부</summary>
	public bool IsTargetPrimed(string projectDir, BuildTarget target)
	{
		// UE5 는 Intermediate/Build/Win64/x64/<Target>/<Config>, UE4 는 Intermediate/Build/Win64/<Target>/<Config>
		string buildDir = Path.Combine(projectDir, "Intermediate", "Build", target.Platform);
		if (!Directory.Exists(buildDir))
		{
			return false;
		}
		IEnumerable<string> candidates = new[] { buildDir }.Concat(SafeSubdirectories(buildDir));
		return candidates.Any(x => Directory.Exists(Path.Combine(x, target.Name, target.Configuration)));
	}

	static ShipCheckException ConfigError(string code, string message) =>
		new ShipCheckException(ErrorKind.ConfigError, Diagnostic.Error(code, "project", message));
}
