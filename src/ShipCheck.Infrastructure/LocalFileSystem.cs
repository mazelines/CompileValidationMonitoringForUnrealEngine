using System.Security.Cryptography;
using System.Text.Json;
using ShipCheck.Core;

namespace ShipCheck.Infrastructure;

public sealed class LocalFileSystem : IFileSystem
{
	public bool FileExists(string path) => File.Exists(path);

	public bool DirectoryExists(string path) => Directory.Exists(path);

	public string HashFile(string path)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return Convert.ToHexString(SHA1.HashData(stream));
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			return "deleted";
		}
	}

	public IReadOnlyList<string> EnumerateCodeFiles(string directory) =>
		EnumerateCodeFileInfos(directory).Select(x => x.FullName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

	public IReadOnlyList<FileStamp> StampCodeFiles(IEnumerable<string> roots, IEnumerable<string> extraFiles)
	{
		List<FileInfo> infos = roots.SelectMany(EnumerateCodeFileInfos).ToList();
		infos.AddRange(extraFiles.Select(x => new FileInfo(x)).Where(x => x.Exists));
		return infos
			.GroupBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
			.Select(x => x.First())
			.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
			.Select(x => new FileStamp(x.FullName, x.LastWriteTimeUtc.Ticks, x.Length))
			.ToList();
	}

	public string ReadAllText(string path) => File.ReadAllText(path);

	/// <summary>폴더 아래 코드 파일 (빌드 산출물 폴더는 들어가지 않는다)</summary>
	static List<FileInfo> EnumerateCodeFileInfos(string directory)
	{
		List<FileInfo> files = new List<FileInfo>();
		if (!Directory.Exists(directory))
		{
			return files;
		}
		Stack<DirectoryInfo> pending = new Stack<DirectoryInfo>();
		pending.Push(new DirectoryInfo(directory));
		while (pending.Count > 0)
		{
			DirectoryInfo current = pending.Pop();
			try
			{
				foreach (DirectoryInfo sub in current.EnumerateDirectories())
				{
					// 심볼릭 링크/정션은 따라가지 않는다 (순환 방지)
					if (!FileClassifier.IsGeneratedPath(sub.Name) && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
					{
						pending.Push(sub);
					}
				}
				foreach (FileInfo file in current.EnumerateFiles())
				{
					if (FileClassifier.IsCode(file.Name))
					{
						files.Add(file);
					}
				}
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
			{
			}
		}
		return files;
	}
}

/// <summary>파일이 속한 모듈(*.Build.cs)과 모듈 타입 (.uproject/.uplugin 의 Modules[].Type)</summary>
public sealed class ModuleResolver : IModuleResolver
{
	readonly Dictionary<string, JsonDocument?> _descriptors = new(StringComparer.OrdinalIgnoreCase);
	readonly object _sync = new object();

	public ModuleInfo? Find(string filePath)
	{
		string? dir = Path.GetDirectoryName(filePath);
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(dir))
			{
				string? buildCs = Directory.EnumerateFiles(dir, "*.Build.cs").FirstOrDefault();
				if (buildCs != null)
				{
					string name = Path.GetFileName(buildCs)[..^".Build.cs".Length];
					return new ModuleInfo(name, dir, GetModuleType(dir, name));
				}
				// 디스크립터 폴더까지 올라왔는데 모듈이 없으면 중단
				if (FindDescriptor(dir) != null)
				{
					return null;
				}
			}
			string? parent = Path.GetDirectoryName(dir);
			if (parent == null || parent == dir)
			{
				break;
			}
			dir = parent;
		}
		return null;
	}

	static string? FindDescriptor(string dir) =>
		Directory.EnumerateFiles(dir).FirstOrDefault(x => x.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase));

	string GetModuleType(string moduleDir, string moduleName)
	{
		string? engineType = ModuleRules.EngineModuleTypeFromPath(moduleDir);
		if (engineType != null)
		{
			return engineType;
		}
		string? dir = moduleDir;
		while (!string.IsNullOrEmpty(dir))
		{
			string? descriptor = Directory.Exists(dir) ? FindDescriptor(dir) : null;
			if (descriptor != null)
			{
				return ReadModuleType(descriptor, moduleName) ?? "Unknown";
			}
			string? parent = Path.GetDirectoryName(dir);
			if (parent == null || parent == dir)
			{
				break;
			}
			dir = parent;
		}
		return "Unknown";
	}

	string? ReadModuleType(string descriptor, string moduleName)
	{
		JsonDocument? document;
		lock (_sync)
		{
			if (!_descriptors.TryGetValue(descriptor, out document))
			{
				try
				{
					document = JsonDocument.Parse(File.ReadAllText(descriptor), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
				}
				catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
				{
					document = null;
				}
				_descriptors[descriptor] = document;
			}
		}
		if (document == null || !TryGet(document.RootElement, "Modules", out JsonElement modules) || modules.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		foreach (JsonElement module in modules.EnumerateArray())
		{
			if (TryGet(module, "Name", out JsonElement name) && name.ValueKind == JsonValueKind.String && name.GetString() == moduleName)
			{
				return TryGet(module, "Type", out JsonElement type) && type.ValueKind == JsonValueKind.String ? type.GetString() : "Unknown";
			}
		}
		return null;
	}

	/// <summary>속성 이름 대소문자 무시 (UE 디스크립터는 대소문자를 구분하지 않는다)</summary>
	internal static bool TryGet(JsonElement element, string name, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
				{
					value = property.Value;
					return true;
				}
			}
		}
		value = default;
		return false;
	}
}
