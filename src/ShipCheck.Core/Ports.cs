namespace ShipCheck.Core;

// 외부 경계. Infrastructure 가 구현하고 테스트는 가짜로 바꿔 끼운다.

/// <summary>프로젝트/설정/엔진 탐색 결과</summary>
public sealed class ProjectContext
{
	public string UProject { get; init; } = "";
	public string ProjectDir => Path.GetDirectoryName(UProject)!;
	public string ProjectName => Path.GetFileNameWithoutExtension(UProject);
	public string ConfigPath { get; init; } = "";
	public ShipCheckConfig Config { get; init; } = new ShipCheckConfig();
	/// <summary>엔진 루트. 우회 태그 검사는 엔진 없이도 되도록 탐색을 나중에 한다.</summary>
	public string EngineRoot { get; set; } = "";
	public string BuildBat => Path.Combine(EngineRoot, "Engine", "Build", "BatchFiles", "Build.bat");
	public string ToolDir { get; init; } = "";
	public IReadOnlyList<BuildTarget> Targets { get; init; } = Array.Empty<BuildTarget>();

	/// <summary>svn 상태 확인 범위</summary>
	public IReadOnlyList<string> SourceScope => new[] { Path.Combine(ProjectDir, "Source"), Path.Combine(ProjectDir, "Plugins"), UProject };

	public string StateDir => Path.Combine(ProjectDir, "Saved", "ShipCheck");
}

public interface IProjectLocator
{
	/// <summary>
	/// 프로젝트를 정하고(명시 / 설정의 UProject / 워킹 카피 위쪽 / 아래 2단계) 그 프로젝트의 설정을 고른다.
	/// 실패하면 ShipCheckException(ConfigError).
	/// </summary>
	ProjectContext Resolve(ProjectSelector selector);

	/// <summary>설정의 EngineDir / EngineAssociation / 상위 폴더로 엔진 루트를 찾는다. 실패하면 ShipCheckException(ConfigError).</summary>
	string ResolveEngineRoot(ProjectContext context);

	bool IsTargetPrimed(string projectDir, BuildTarget target);
}

public interface ISourceControl
{
	/// <summary>범위 내 코드 파일의 svn 상태. 상태를 알 수 없으면 Status 로 원인을 알린다 (예외를 던지지 않는다).</summary>
	Task<SourceControlState> GetStateAsync(string projectDir, IReadOnlyList<string> scope, CancellationToken cancellationToken);
}

public sealed record ModuleInfo(string Name, string Dir, string Type);

public interface IModuleResolver
{
	/// <summary>파일이 속한 모듈(*.Build.cs)과 타입. 삭제된 파일도 경로만으로 판단. 모르면 null.</summary>
	ModuleInfo? Find(string filePath);
}

public sealed record FileStamp(string Path, long LastWriteTicks, long Length);

public interface IFileSystem
{
	bool FileExists(string path);
	bool DirectoryExists(string path);
	/// <summary>내용 해시 (없으면 "deleted")</summary>
	string HashFile(string path);
	/// <summary>폴더 아래 코드 파일 (산출물 폴더는 들어가지 않는다), 정렬된 전체 경로</summary>
	IReadOnlyList<string> EnumerateCodeFiles(string directory);
	/// <summary>수정 시각/크기 (빌드 중 변경 감지용)</summary>
	IReadOnlyList<FileStamp> StampCodeFiles(IEnumerable<string> roots, IEnumerable<string> extraFiles);
	string ReadAllText(string path);
}

public sealed record BuildInvocation(string BuildBat, IReadOnlyList<string> Arguments, string LogPath, string Label, TimeSpan? Timeout);

/// <param name="LeftoverProcesses">취소/시간 초과 후에도 살아 있는 프로세스 수 (0 이어야 정상)</param>
public sealed record BuildProcessResult(int ExitCode, bool TimedOut, bool Canceled, int LeftoverProcesses);

public interface IBuildRunner
{
	/// <summary>
	/// Build.bat 실행. 출력 줄은 onLine 으로 전달하고 로그 파일에 남긴다.
	/// 취소/시간 초과 시 손자 프로세스까지 종료하고 Canceled/TimedOut 결과를 돌려준다 (예외를 던지지 않는다).
	/// 실행 자체를 못 하면 ShipCheckException(ExternalToolFailed).
	/// </summary>
	Task<BuildProcessResult> RunAsync(BuildInvocation invocation, Action<string> onLine, CancellationToken cancellationToken);
}

public interface IProjectLock : IDisposable
{
}

public interface ILockProvider
{
	/// <summary>즉시 얻으면 waited=false. 다른 실행이 잡고 있으면 onWaiting 호출 후 대기.</summary>
	Task<(IProjectLock Lock, bool Waited)> AcquireAsync(string uproject, Action onWaiting, CancellationToken cancellationToken);
}

public interface IRunStore
{
	IReadOnlyList<PassCache.Entry> ReadPassCache(string stateDir);
	/// <summary>실패하면 예외 (조용히 무시하지 않는다 → 호출 측이 진단으로 남긴다)</summary>
	void AddPassCache(string stateDir, string key, IReadOnlyList<string> verifiedFiles);
	void AppendHistory(string uproject, string stateDir, RunResult run);
	void WriteLastResult(string stateDir, string text);
	string PrepareLogDirectory(string stateDir);
}

public interface IClock
{
	DateTimeOffset Now { get; }
}
