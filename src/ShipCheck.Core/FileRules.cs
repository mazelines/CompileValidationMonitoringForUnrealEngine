namespace ShipCheck.Core;

public enum FileKind
{
	Source,
	BuildCs,
	Target,
	Descriptor,
}

/// <summary>파일 이름/경로만으로 판단하는 분류 규칙</summary>
public static class FileClassifier
{
	static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".h", ".hpp", ".inl", ".c", ".cc", ".cxx", ".ipp", ".ispc" };
	static readonly HashSet<string> CompileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".c", ".cc", ".cxx" };
	static readonly string[] GeneratedFolders = { "Intermediate", "Binaries", "Saved", "DerivedDataCache" };

	/// <summary>빌드에 관련된 코드 파일 종류. 관련 없으면 null.</summary>
	public static FileKind? GetKind(string path)
	{
		string name = Path.GetFileName(path);
		if (name.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
		{
			return FileKind.Target;
		}
		if (name.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
		{
			return FileKind.BuildCs;
		}
		string extension = Path.GetExtension(name);
		if (extension.Equals(".uproject", StringComparison.OrdinalIgnoreCase) || extension.Equals(".uplugin", StringComparison.OrdinalIgnoreCase))
		{
			return FileKind.Descriptor;
		}
		return SourceExtensions.Contains(extension) ? FileKind.Source : null;
	}

	public static bool IsCode(string path) => GetKind(path) != null;

	/// <summary>단독 컴파일 가능한 파일 (.cpp 등, 헤더 포함 여부 선택)</summary>
	public static bool IsSingleFileCandidate(string path, bool includeHeaders)
	{
		string extension = Path.GetExtension(path);
		return CompileExtensions.Contains(extension) || (includeHeaders && extension.Equals(".h", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>빌드 산출물 폴더(Intermediate/Binaries/Saved/DerivedDataCache) 자체 또는 그 아래</summary>
	public static bool IsGeneratedPath(string path)
	{
		foreach (string segment in path.Split('\\', '/'))
		{
			if (GeneratedFolders.Any(x => x.Equals(segment, StringComparison.OrdinalIgnoreCase)))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>프로젝트 폴더 기준으로 산출물 경로인지 판단 (프로젝트 위쪽 폴더 이름은 보지 않는다)</summary>
	public static bool IsGeneratedPath(string path, string projectDir)
	{
		string relative = PathUtil.IsSameOrUnder(path, projectDir) ? Path.GetRelativePath(projectDir, path) : path;
		return IsGeneratedPath(relative);
	}
}

/// <summary>모듈 타입 규칙 (.uproject/.uplugin 의 Modules[].Type)</summary>
public static class ModuleRules
{
	static readonly HashSet<string> EditorOnly = new(StringComparer.OrdinalIgnoreCase) { "Editor", "EditorNoCommandlet", "EditorAndProgram", "UncookedOnly", "Program" };
	static readonly HashSet<string> Developer = new(StringComparer.OrdinalIgnoreCase) { "Developer", "DeveloperTool" };

	/// <summary>이 파일이 검사 대상 타깃 빌드에 포함되는지. 모듈을 판단할 수 없으면 보수적으로 포함.</summary>
	public static bool Affects(FileKind kind, string? moduleType, ValidationPolicy policy)
	{
		if (kind is FileKind.Target or FileKind.Descriptor)
		{
			return true;
		}
		if (!policy.SkipNonTargetModules || moduleType == null)
		{
			return true;
		}
		if (EditorOnly.Contains(moduleType))
		{
			return false;
		}
		return !Developer.Contains(moduleType) || policy.IncludeDeveloperModules;
	}

	/// <summary>엔진 소스 트리의 모듈은 폴더 위치로 타입이 정해진다. 해당 없으면 null.</summary>
	public static string? EngineModuleTypeFromPath(string moduleDir)
	{
		string normalized = moduleDir.Replace('/', '\\') + "\\";
		if (Contains(normalized, @"\Engine\Source\Editor\") || Contains(normalized, @"\Engine\Source\Programs\"))
		{
			return "Editor";
		}
		if (Contains(normalized, @"\Engine\Source\Developer\"))
		{
			return "Developer";
		}
		if (Contains(normalized, @"\Engine\Source\Runtime\") || Contains(normalized, @"\Engine\Source\ThirdParty\"))
		{
			return "Runtime";
		}
		return null;
	}

	static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary>단독 컴파일 대상: 정렬 후 MaxFiles 로 자르므로 같은 변경 집합이면 실행 주체와 무관하게 같은 범위가 된다</summary>
public static class SingleFileSelector
{
	public sealed record Selection(IReadOnlyList<string> Files, int Truncated);

	public static Selection Select(IEnumerable<string> files, ValidationPolicy policy, bool all, Func<string, bool> fileExists)
	{
		if (!policy.SingleFileEnabled || all)
		{
			return new Selection(Array.Empty<string>(), 0);
		}
		List<string> set = files
			.Where(x => !string.IsNullOrEmpty(x) && FileClassifier.IsSingleFileCandidate(x, policy.IncludeHeaders) && fileExists(x))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
			.ToList();
		int truncated = Math.Max(0, set.Count - policy.MaxSingleFiles);
		return new Selection(truncated > 0 ? set.Take(policy.MaxSingleFiles).ToList() : set, truncated);
	}
}

public static class PathUtil
{
	public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

	public static bool IsSameOrUnder(string path, string root)
	{
		try
		{
			string normalizedRoot = Normalize(root);
			string normalizedPath = Normalize(path);
			return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
				normalizedPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
}
