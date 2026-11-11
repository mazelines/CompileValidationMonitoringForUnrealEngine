using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShipCheck.Core;

/// <summary>실행 결과. 문자열 값(Result 필드)은 기존 ShipCheck.ps1 / History.jsonl 과 같다.</summary>
public enum RunOutcome
{
	Pass,
	Cached,
	Skipped,
	Fail,
	Error,
	Canceled,
}

/// <summary>Error/Canceled 의 세부 원인. 빈 목록이나 false 로 오류를 숨기지 않도록 원인별로 구분한다.</summary>
public enum ErrorKind
{
	None,
	BuildFailed,
	PolicyBlocked,
	ConfigError,
	SourceControlUnknown,
	ExternalToolFailed,
	Timeout,
	Canceled,
	ChangedDuringValidation,
	StorageFailed,
	Unexpected,
}

public enum DiagnosticSeverity
{
	Info,
	Warning,
	Error,
}

/// <summary>안정적인 진단 코드. GUI/CLI/훅이 같은 코드를 표현만 달리 보여 준다.</summary>
public static class DiagnosticCodes
{
	public const string ConfigInvalid = "config.invalid";
	public const string ProjectNotFound = "project.not-found";
	public const string EngineNotFound = "engine.not-found";
	public const string SvnToolMissing = "svn.tool-missing";
	public const string SvnNotWorkingCopy = "svn.not-working-copy";
	public const string SvnFailed = "svn.failed";
	public const string UncommittedChanges = "policy.uncommitted-changes";
	public const string BypassTag = "policy.bypass-tag";
	public const string NoRelevantChanges = "skip.no-relevant-changes";
	public const string NoPaths = "skip.no-paths";
	public const string BuildFailed = "build.failed";
	public const string CompileError = "build.compile-error";
	public const string ErrorLineWithSuccess = "build.error-line-ignored";
	public const string BuildToolFailed = "build.tool-failed";
	public const string InvalidArgument = "build.invalid-argument";
	public const string SingleFileTruncated = "build.single-file-truncated";
	public const string ChangedDuringValidation = "run.changed-during-validation";
	public const string Timeout = "run.timeout";
	public const string Canceled = "run.canceled";
	public const string LeftoverProcesses = "run.leftover-processes";
	public const string CacheWriteFailed = "storage.cache-write-failed";
	public const string HistoryWriteFailed = "storage.history-write-failed";
	public const string Unexpected = "run.unexpected";
}

public sealed class Diagnostic
{
	public string Code { get; set; } = "";
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Error;
	/// <summary>project / svn / policy / cache / build / single-file / storage / run</summary>
	public string Stage { get; set; } = "";
	public string Message { get; set; } = "";
	/// <summary>원인 / 원본 stderr / 원본 로그 줄</summary>
	public string? Detail { get; set; }
	public string? File { get; set; }
	public int? Line { get; set; }
	public int? Column { get; set; }
	/// <summary>컴파일러/링커 오류 코드 (C2065, LNK2019 ...)</summary>
	public string? ToolCode { get; set; }

	public static Diagnostic Error(string code, string stage, string message, string? detail = null) =>
		new Diagnostic { Code = code, Severity = DiagnosticSeverity.Error, Stage = stage, Message = message, Detail = detail };

	public static Diagnostic Warning(string code, string stage, string message, string? detail = null) =>
		new Diagnostic { Code = code, Severity = DiagnosticSeverity.Warning, Stage = stage, Message = message, Detail = detail };

	public static Diagnostic Info(string code, string stage, string message, string? detail = null) =>
		new Diagnostic { Code = code, Severity = DiagnosticSeverity.Info, Stage = stage, Message = message, Detail = detail };
}

/// <summary>실행을 중단해야 하는 오류. 실행 경계에서 Error 결과로 바뀐다.</summary>
public sealed class ShipCheckException : Exception
{
	public ErrorKind Kind { get; }
	public Diagnostic Diagnostic { get; }

	public ShipCheckException(ErrorKind kind, Diagnostic diagnostic, Exception? inner = null) : base(diagnostic.Message, inner)
	{
		Kind = kind;
		Diagnostic = diagnostic;
	}
}

/// <summary>검사할 프로젝트를 고르는 방법 (명시 프로젝트 / 워킹 카피에서 자동 탐색 / 명시 설정)</summary>
public sealed record ProjectSelector(string? Project, string? WorkingCopy, string? ConfigPath);

public sealed class ValidationRequest
{
	public ProjectSelector Selector { get; init; } = new ProjectSelector(null, null, null);
	/// <summary>커밋 대상 경로 (파일 또는 폴더). 상대 경로는 WorkingCopy(없으면 현재 폴더) 기준.</summary>
	public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
	/// <summary>커밋 메시지 (우회 태그 검사)</summary>
	public string? Message { get; init; }
	public bool All { get; init; }
	public bool Force { get; init; }
	public bool NoSingleFile { get; init; }
	public bool DryRun { get; init; }
	public string Trigger { get; init; } = "cli";
	/// <summary>단계별 UBT 실행 제한 시간 (null 이면 무제한)</summary>
	public TimeSpan? StepTimeout { get; init; }
}

public sealed class QueryRequest
{
	public ProjectSelector Selector { get; init; } = new ProjectSelector(null, null, null);
}

public enum RunEventKind
{
	Info,
	Warning,
	/// <summary>UBT 출력 한 줄</summary>
	Output,
	StepStarted,
	StepCompleted,
}

/// <summary>진행 이벤트 (작업 스레드에서 전달된다)</summary>
public sealed record RunEvent(RunEventKind Kind, string Message, string? Step = null);

public sealed class StepResult
{
	public string Label { get; set; } = "";
	public int? ExitCode { get; set; }
	public string? Log { get; set; }
	public int ErrorCount { get; set; }
	public List<string>? Errors { get; set; }
	public List<string>? Tail { get; set; }
	public double DurationSeconds { get; set; }
	public bool TimedOut { get; set; }
}

/// <summary>
/// 검증 실행 1건의 결과. ResultJson 과 Saved/ShipCheck/History.jsonl 한 줄의 형식.
/// 기존(Version 1) 필드는 이름과 의미를 유지하고 Version 2 필드를 덧붙인다.
/// </summary>
public sealed class RunResult
{
	public int Version { get; set; } = 2;
	public string RunId { get; set; } = "";
	public string Trigger { get; set; } = "";
	public string StartTime { get; set; } = "";
	public string? EndTime { get; set; }
	public string Result { get; set; } = nameof(RunOutcome.Error);
	public string? Reason { get; set; }
	public double ElapsedSeconds { get; set; }
	public string? Project { get; set; }
	public string? ProjectDir { get; set; }
	public string? EngineRoot { get; set; }
	public List<string>? Targets { get; set; }
	public string? Revision { get; set; }
	public string? StateKey { get; set; }
	public List<string>? Files { get; set; }
	public List<string>? SingleFiles { get; set; }
	public List<StepResult>? Steps { get; set; }
	public List<Diagnostic>? Diagnostics { get; set; }
	public int ExitCode { get; set; }
	public bool CommitAllowed { get; set; }
	public bool CacheReused { get; set; }
	public string? ErrorKind { get; set; }

	[JsonIgnore]
	public RunOutcome Outcome => Enum.TryParse(Result, out RunOutcome value) ? value : RunOutcome.Error;

	[JsonIgnore]
	public DateTimeOffset Time => DateTimeOffset.TryParse(StartTime, out DateTimeOffset value) ? value : DateTimeOffset.MinValue;

	/// <summary>검증된 통과 (Skipped 는 포함하지 않는다)</summary>
	[JsonIgnore]
	public bool IsPass => Result is nameof(RunOutcome.Pass) or nameof(RunOutcome.Cached);

	[JsonIgnore]
	public string ResultText => Result switch
	{
		"Pass" => "통과",
		"Cached" => "통과(캐시)",
		"Fail" => "실패",
		"Skipped" => "건너뜀",
		"Error" => "오류",
		"Canceled" => "취소",
		_ => Result
	};

	[JsonIgnore]
	public string TriggerText => Trigger switch
	{
		"hook" => "커밋 훅",
		"gui" => "수동",
		"auto" => "자동",
		"cli" => "명령줄",
		_ => Trigger
	};
}

public sealed class TargetInfo
{
	public string Name { get; set; } = "";
	public string Platform { get; set; } = "";
	public string Configuration { get; set; } = "";
	public bool Primed { get; set; }

	public override string ToString() => $"{Name} {Platform} {Configuration}";
}

public sealed class ChangeEntry
{
	public string Path { get; set; } = "";
	public string Status { get; set; } = "";
	public string Kind { get; set; } = "";
	public string? Module { get; set; }
	public string? ModuleType { get; set; }
	public bool Affects { get; set; }

	[JsonIgnore]
	public bool IsUnversioned => Status == "?";

	[JsonIgnore]
	public string StatusText => Status switch
	{
		"M" => "수정",
		"A" => "추가",
		"D" => "삭제",
		"R" => "교체",
		"C" => "충돌",
		"!" => "누락",
		"~" => "종류 변경",
		"?" => "미등록",
		_ => Status
	};
}

/// <summary>빌드 없이 조회한 프로젝트/변경 사항/검증 상태 (기존 -Query 결과와 같은 필드)</summary>
public sealed class QueryResult
{
	public int Version { get; set; } = 2;
	/// <summary>조회 자체가 실패한 경우의 메시지</summary>
	public string? Error { get; set; }
	public string Project { get; set; } = "";
	public string ProjectDir { get; set; } = "";
	public string ProjectName { get; set; } = "";
	public string EngineRoot { get; set; } = "";
	public string ConfigPath { get; set; } = "";
	public string ToolDir { get; set; } = "";
	public List<TargetInfo> Targets { get; set; } = new();
	public bool SvnAvailable { get; set; }
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public SourceControlStatus SvnState { get; set; }
	public string? SvnError { get; set; }
	public string? WcRoot { get; set; }
	public string? Revision { get; set; }
	public string? StateKey { get; set; }
	public bool UpToDate { get; set; }
	public List<ChangeEntry> Changes { get; set; } = new();
	public List<Diagnostic> Diagnostics { get; set; } = new();

	[JsonIgnore]
	public IEnumerable<ChangeEntry> AffectingChanges => Changes.Where(x => x.Affects);
}

public static class ExitCodes
{
	public const int Allowed = 0;
	public const int Blocked = 1;
	public const int Error = 2;

	/// <summary>Pass/Cached/Skipped 만 커밋 허용. 취소/시간 초과/상태 불명/검증 중 변경은 Error(2).</summary>
	public static int For(RunOutcome outcome) => outcome switch
	{
		RunOutcome.Pass or RunOutcome.Cached or RunOutcome.Skipped => Allowed,
		RunOutcome.Fail => Blocked,
		_ => Error,
	};
}

public static class ShipCheckJson
{
	public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		NumberHandling = JsonNumberHandling.AllowReadingFromString,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>History.jsonl 한 줄용 (들여쓰기 없음)</summary>
	public static readonly JsonSerializerOptions Compact = new JsonSerializerOptions(Options) { WriteIndented = false };

	public static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);

	public static string Serialize<T>(T value, bool compact = false) => JsonSerializer.Serialize(value, compact ? Compact : Options);
}
