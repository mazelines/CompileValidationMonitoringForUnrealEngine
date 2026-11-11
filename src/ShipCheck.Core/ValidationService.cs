using System.Diagnostics;
using System.Text;

namespace ShipCheck.Core;

/// <summary>외부 경계 묶음</summary>
public sealed class ShipCheckServices
{
	public required IProjectLocator Locator { get; init; }
	public required ISourceControl SourceControl { get; init; }
	public required IModuleResolver Modules { get; init; }
	public required IFileSystem Files { get; init; }
	public required IBuildRunner Builds { get; init; }
	public required ILockProvider Locks { get; init; }
	public required IRunStore Store { get; init; }
	public required IClock Clock { get; init; }
}

/// <summary>
/// 조회/검증 순서와 판정. GUI, CLI, TortoiseSVN 훅이 모두 이 클래스를 사용한다.
///  1. 커밋 대상 중 게임 빌드에 영향을 주는 파일만 추린다 (에디터 전용 모듈/콘텐츠만 바뀌면 건너뜀)
///  2. 같은 워킹 카피 상태로 이미 통과했으면 캐시로 통과
///  3. 프로젝트 잠금 → 상태 재평가 → 타깃 증분 빌드 → 변경 파일 단독 컴파일
///  4. 빌드 전후 상태가 같을 때만 통과로 인정하고 캐시에 남긴다
/// </summary>
public sealed class ValidationService
{
	const int TailLines = 15;
	readonly ShipCheckServices _services;

	public ValidationService(ShipCheckServices services) => _services = services;

	#region 조회

	public async Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken)
	{
		ProjectContext context = _services.Locator.Resolve(request.Selector);
		context.EngineRoot = _services.Locator.ResolveEngineRoot(context);
		ValidationPolicy policy = ValidationPolicy.From(context.Config, context.Targets, noSingleFile: false);

		SourceControlState svn = await _services.SourceControl.GetStateAsync(context.ProjectDir, context.SourceScope, cancellationToken);
		QueryResult result = new QueryResult
		{
			Project = context.UProject,
			ProjectDir = context.ProjectDir,
			ProjectName = context.ProjectName,
			EngineRoot = context.EngineRoot,
			ConfigPath = context.ConfigPath,
			ToolDir = context.ToolDir,
			Targets = context.Targets.Select(x => new TargetInfo { Name = x.Name, Platform = x.Platform, Configuration = x.Configuration, Primed = _services.Locator.IsTargetPrimed(context.ProjectDir, x) }).ToList(),
			SvnAvailable = svn.IsAvailable,
			SvnState = svn.Status,
			SvnError = svn.Error,
			WcRoot = svn.WcRoot,
			Revision = svn.Revision,
		};
		if (!svn.IsAvailable)
		{
			result.Diagnostics.Add(SourceControlDiagnostic(svn, DiagnosticSeverity.Warning));
			return result;
		}

		foreach (SourceChange change in svn.Changes)
		{
			FileKind? kind = FileClassifier.GetKind(change.Path);
			if (kind == null)
			{
				continue;
			}
			ModuleInfo? module = _services.Modules.Find(change.Path);
			result.Changes.Add(new ChangeEntry
			{
				Path = change.Path,
				Status = change.Status,
				Kind = kind.Value.ToString(),
				Module = module?.Name ?? "",
				ModuleType = module?.Type ?? "",
				Affects = ModuleRules.Affects(kind.Value, module?.Type, policy),
			});
		}
		result.StateKey = ComputeStateKey(context, svn, policy, svn.Changes.Select(x => x.Path));
		if (context.Config.UseCache)
		{
			// 영향 있는 변경 전체가 단독 컴파일로 검증되었어야 "최신 통과"
			IReadOnlyList<string> required = SingleFileSelector.Select(result.AffectingChanges.Select(x => x.Path), policy, all: false, _services.Files.FileExists).Files;
			result.UpToDate = PassCache.IsHit(_services.Store.ReadPassCache(context.StateDir), result.StateKey, required);
		}
		return result;
	}

	#endregion

	#region 검증

	public async Task<RunResult> ValidateAsync(ValidationRequest request, Action<RunEvent>? onEvent, CancellationToken cancellationToken)
	{
		Run run = new Run(request, _services.Clock.Now, onEvent ?? (_ => { }));
		try
		{
			await ValidateCoreAsync(run, cancellationToken);
		}
		catch (ShipCheckException ex)
		{
			run.Diagnostics.Add(ex.Diagnostic);
			run.Finish(ex.Kind == ErrorKind.Canceled ? RunOutcome.Canceled : RunOutcome.Error, ex.Kind, ex.Diagnostic.Message);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			run.Diagnostics.Add(Diagnostic.Info(DiagnosticCodes.Canceled, "run", "사용자가 검증을 중지했습니다."));
			run.Finish(RunOutcome.Canceled, ErrorKind.Canceled, "사용자 중지");
		}
		catch (Exception ex)
		{
			// 알 수 없는 예외도 실행 경계에서 오류 결과로 바꾸고 원인을 보존한다
			run.Diagnostics.Add(Diagnostic.Error(DiagnosticCodes.Unexpected, "run", ex.Message, ex.ToString()));
			run.Finish(RunOutcome.Error, ErrorKind.Unexpected, ex.Message);
		}

		RunResult result = run.ToResult(_services.Clock.Now);
		SaveHistory(run, result);
		return result;
	}

	async Task ValidateCoreAsync(Run run, CancellationToken cancellationToken)
	{
		ValidationRequest request = run.Request;
		ProjectContext context = _services.Locator.Resolve(request.Selector);
		run.Context = context;

		// --- 우회 태그 (엔진을 찾지 못해도 동작하도록 먼저 확인) ---
		string skipTag = context.Config.SkipTag ?? "";
		if (skipTag.Length > 0 && request.Message != null && request.Message.Contains(skipTag, StringComparison.OrdinalIgnoreCase))
		{
			run.Warn($"커밋 메시지에 {skipTag} 태그가 있어 검사를 건너뜁니다.");
			run.Diagnostics.Add(Diagnostic.Warning(DiagnosticCodes.BypassTag, "policy", $"우회 태그 {skipTag} 사용", request.Message.Trim()));
			run.Finish(RunOutcome.Skipped, ErrorKind.None, $"커밋 메시지 우회 태그: {request.Message.Trim()}");
			run.RecordHistory = true;
			return;
		}

		context.EngineRoot = _services.Locator.ResolveEngineRoot(context);
		ValidationPolicy policy = ValidationPolicy.From(context.Config, context.Targets, request.NoSingleFile);
		run.Info($"프로젝트: {context.UProject}");
		run.Info($"엔진    : {context.EngineRoot}");
		run.Info("타깃    : " + string.Join(", ", context.Targets));

		// --- 커밋 대상 파일 ---
		string baseDir = request.Selector.WorkingCopy is { Length: > 0 } wc ? Path.GetFullPath(wc) : Environment.CurrentDirectory;
		List<string> commitFiles = new List<string>();
		foreach (string raw in request.Paths.Where(x => !string.IsNullOrWhiteSpace(x)))
		{
			string full = PathUtil.Normalize(Path.IsPathRooted(raw.Trim()) ? raw.Trim() : Path.Combine(baseDir, raw.Trim()));
			if (_services.Files.DirectoryExists(full))
			{
				commitFiles.AddRange(_services.Files.EnumerateCodeFiles(full));
			}
			else if (FileClassifier.IsCode(full))
			{
				commitFiles.Add(full);
			}
		}
		HashSet<string> commitSet = new HashSet<string>(commitFiles, PathUtil.Comparer);
		List<string> relevant = commitFiles.Distinct(PathUtil.Comparer).Where(x => Affects(x, policy)).ToList();
		run.Files = relevant;

		if (!request.All)
		{
			if (!request.Paths.Any(x => !string.IsNullOrWhiteSpace(x)))
			{
				run.Warn("검사할 경로가 지정되지 않았습니다. (경로 인수, --paths-file 또는 --all)");
				run.Diagnostics.Add(Diagnostic.Info(DiagnosticCodes.NoPaths, "project", "검사할 경로 없음"));
				run.Finish(RunOutcome.Skipped, ErrorKind.None, "검사할 경로 없음");
				return;
			}
			if (relevant.Count == 0)
			{
				run.Info("게임 빌드에 영향을 주는 코드 변경이 없어 검사를 건너뜁니다.");
				run.Diagnostics.Add(Diagnostic.Info(DiagnosticCodes.NoRelevantChanges, "project", "게임 빌드에 영향을 주는 코드 변경 없음"));
				run.Finish(RunOutcome.Skipped, ErrorKind.None, "게임 빌드에 영향을 주는 코드 변경 없음");
				return;
			}
			run.Info($"빌드 영향 파일 {relevant.Count}개:");
			foreach (string file in relevant.Take(20))
			{
				run.Info("    " + file);
			}
		}

		// svn status 범위 밖의 파일(예: 같은 WC 의 엔진 소스)은 수정 여부를 알 수 없으므로 내용을 키에 넣는다
		List<string> outsideScope = relevant.Where(f => !context.SourceScope.Any(scope => PathUtil.IsSameOrUnder(f, scope))).ToList();
		bool useCache = context.Config.UseCache && !request.All;
		string cacheFileDir = context.StateDir;

		WorkingState state = await GetWorkingStateAsync(context, policy, relevant, outsideScope, request.All, cancellationToken);
		run.StateKey = state.Key;
		run.Revision = state.SourceControl.Revision;
		if (!CheckOmittedChanges(run, context, state, commitSet, policy))
		{
			return;
		}
		_services.Store.PrepareLogDirectory(context.StateDir);

		bool IsCacheHit(WorkingState current) =>
			useCache && !request.Force && current.Key.Length > 0 &&
			PassCache.IsHit(_services.Store.ReadPassCache(cacheFileDir), current.Key, current.SingleFiles.Files);

		if (IsCacheHit(state))
		{
			run.Info("동일한 워킹 카피 상태로 이미 검사를 통과했습니다. (캐시)");
			run.CacheReused = true;
			run.Finish(RunOutcome.Cached, ErrorKind.None, "같은 워킹 카피 상태로 이미 통과");
			return;
		}

		// --- 동시 실행 방지 (같은 프로젝트에 대해 한 번에 하나만) ---
		(IProjectLock projectLock, bool waited) = await _services.Locks.AcquireAsync(context.UProject,
			() => run.Info("다른 ShipCheck 가 실행 중입니다. 끝날 때까지 기다립니다..."), cancellationToken);
		using (projectLock)
		{
			if (waited)
			{
				// 기다리는 동안 코드가 바뀌었을 수 있으므로 상태/검증 범위/누락 파일/캐시 적합성을 다시 평가한다
				state = await GetWorkingStateAsync(context, policy, relevant, outsideScope, request.All, cancellationToken);
				run.StateKey = state.Key;
				run.Revision = state.SourceControl.Revision;
				if (!CheckOmittedChanges(run, context, state, commitSet, policy))
				{
					return;
				}
				if (IsCacheHit(state))
				{
					run.Info("대기 중에 다른 실행이 같은 워킹 카피 상태를 통과시켰습니다. (캐시)");
					run.CacheReused = true;
					run.Finish(RunOutcome.Cached, ErrorKind.None, "같은 워킹 카피 상태로 이미 통과");
					return;
				}
			}

			string snapshotBefore = StabilitySnapshot(context, state.Key, outsideScope);
			await BuildAsync(run, context, policy, state, cancellationToken);
			if (run.Outcome != null)
			{
				// 취소/시간 초과
				return;
			}

			// 빌드 도중 코드가 바뀌었으면 빌드가 어떤 내용을 컴파일했는지 확정할 수 없다 → 통과로 인정하거나 캐시하지 않는다
			bool stable = request.DryRun;
			if (!stable)
			{
				WorkingState after = await GetWorkingStateAsync(context, policy, relevant, outsideScope, request.All, CancellationToken.None);
				stable = after.SourceControl.Status == state.SourceControl.Status && StabilitySnapshot(context, after.Key, outsideScope) == snapshotBefore;
			}
			Complete(run, context, stable, useCache && !request.DryRun, state);
		}
	}

	sealed record WorkingState(SourceControlState SourceControl, IReadOnlyList<string>? Modified, string Key, SingleFileSelector.Selection SingleFiles);

	/// <summary>현재 워킹 카피 상태: svn 상태, 수정된 코드 파일, 상태 키(svn 상태를 모르면 빈 값), 단독 컴파일 대상</summary>
	async Task<WorkingState> GetWorkingStateAsync(ProjectContext context, ValidationPolicy policy, IReadOnlyList<string> relevant, IReadOnlyList<string> outsideScope, bool all, CancellationToken cancellationToken)
	{
		SourceControlState svn = await _services.SourceControl.GetStateAsync(context.ProjectDir, context.SourceScope, cancellationToken);
		IReadOnlyList<string>? modified = null;
		string key = "";
		if (svn.IsAvailable)
		{
			modified = svn.Changes.Select(x => x.Path).ToList();
			key = ComputeStateKey(context, svn, policy, modified.Concat(outsideScope));
		}
		return new WorkingState(svn, modified, key, SingleFileSelector.Select(relevant, policy, all, _services.Files.FileExists));
	}

	string ComputeStateKey(ProjectContext context, SourceControlState svn, ValidationPolicy policy, IEnumerable<string> files)
	{
		IEnumerable<KeyValuePair<string, string>> hashes = files
			.Where(FileClassifier.IsCode)
			.Distinct(PathUtil.Comparer)
			.Select(x => new KeyValuePair<string, string>(x, _services.Files.HashFile(x)));
		return StateKey.Compute(context.EngineRoot, svn.Baseline, context.Targets, policy, hashes);
	}

	/// <summary>
	/// 검증 도중 코드가 바뀌었는지 판단하기 위한 지문: 상태 키 + 범위 내 모든 코드 파일의 수정 시각/크기.
	/// 내용을 바꿨다가 원래대로 되돌린 경우(상태 키는 같아짐)도 수정 시각으로 잡는다.
	/// 입력 스냅샷이 아니므로 수정 시각을 보존하면서 같은 크기로 바꾸는 변경은 잡지 못한다.
	/// </summary>
	string StabilitySnapshot(ProjectContext context, string key, IReadOnlyList<string> outsideScope)
	{
		StringBuilder text = new StringBuilder(key).Append('\n');
		IEnumerable<string> roots = context.SourceScope.Where(x => _services.Files.DirectoryExists(x));
		IEnumerable<string> files = context.SourceScope.Where(x => _services.Files.FileExists(x)).Concat(outsideScope);
		foreach (FileStamp stamp in _services.Files.StampCodeFiles(roots, files))
		{
			text.Append(stamp.Path).Append('|').Append(stamp.LastWriteTicks).Append('|').Append(stamp.Length).Append('\n');
		}
		return text.ToString();
	}

	/// <summary>
	/// 커밋에서 빠진 로컬 코드 변경 검사. 빌드는 워킹 카피 전체로 하므로 "로컬에선 통과 / 저장소는 깨짐" 이 될 수 있다.
	/// 계속 진행하면 true.
	/// </summary>
	bool CheckOmittedChanges(Run run, ProjectContext context, WorkingState state, HashSet<string> commitSet, ValidationPolicy policy)
	{
		if (!state.SourceControl.IsAvailable)
		{
			Diagnostic unknown = SourceControlDiagnostic(state.SourceControl, DiagnosticSeverity.Warning);
			if (context.Config.FailOnUncommittedChanges && !run.Request.All)
			{
				// 엄격 정책은 누락 파일을 확인할 수 없으면 통과시키지 않는다
				unknown.Severity = DiagnosticSeverity.Error;
				run.Diagnostics.Add(unknown);
				run.Finish(RunOutcome.Error, ErrorKind.SourceControlUnknown, "SVN 상태를 확인할 수 없어 커밋에서 빠진 변경을 검사할 수 없음 (UncommittedSourceChanges=Fail)");
				return false;
			}
			run.Diagnostics.Add(unknown);
			run.Warn(unknown.Message + " — 누락 파일 검사와 통과 캐시 없이 진행합니다.");
			return true;
		}
		if (run.Request.All)
		{
			return true;
		}
		List<string> outsideCommit = state.Modified!.Where(x => FileClassifier.IsCode(x) && !commitSet.Contains(x) && Affects(x, policy)).ToList();
		if (outsideCommit.Count == 0)
		{
			return true;
		}
		string list = string.Join("\n", outsideCommit.Take(10).Select(x => "    " + x));
		string message = $"커밋에 포함되지 않은 로컬 코드 변경 {outsideCommit.Count}개가 빌드에 함께 반영됩니다:\n{list}";
		if (context.Config.FailOnUncommittedChanges)
		{
			run.Diagnostics.Add(Diagnostic.Error(DiagnosticCodes.UncommittedChanges, "policy",
				$"커밋에 포함되지 않은 로컬 코드 변경 {outsideCommit.Count}개", message + "\n해당 파일을 커밋에 포함하거나 되돌린 뒤 다시 시도하세요."));
			run.Finish(RunOutcome.Fail, ErrorKind.PolicyBlocked, $"커밋에 포함되지 않은 로컬 코드 변경 {outsideCommit.Count}개");
			return false;
		}
		run.Diagnostics.Add(Diagnostic.Warning(DiagnosticCodes.UncommittedChanges, "policy", $"커밋에 포함되지 않은 로컬 코드 변경 {outsideCommit.Count}개", message));
		run.Warn(message);
		return true;
	}

	static Diagnostic SourceControlDiagnostic(SourceControlState svn, DiagnosticSeverity severity)
	{
		(string code, string message) = svn.Status switch
		{
			SourceControlStatus.ToolMissing => (DiagnosticCodes.SvnToolMissing, "svn 명령줄 도구를 찾을 수 없어 SVN 상태를 알 수 없습니다"),
			SourceControlStatus.NotWorkingCopy => (DiagnosticCodes.SvnNotWorkingCopy, "SVN 워킹 카피가 아니어서 변경 사항을 알 수 없습니다"),
			_ => (DiagnosticCodes.SvnFailed, "svn 상태 확인에 실패했습니다"),
		};
		return new Diagnostic { Code = code, Severity = severity, Stage = "svn", Message = message, Detail = svn.Error };
	}

	bool Affects(string path, ValidationPolicy policy)
	{
		FileKind? kind = FileClassifier.GetKind(path);
		if (kind == null)
		{
			return false;
		}
		if (!policy.SkipNonTargetModules || kind is FileKind.Target or FileKind.Descriptor)
		{
			return true;
		}
		return ModuleRules.Affects(kind.Value, _services.Modules.Find(path)?.Type, policy);
	}

	async Task BuildAsync(Run run, ProjectContext context, ValidationPolicy policy, WorkingState state, CancellationToken cancellationToken)
	{
		string logDir = _services.Store.PrepareLogDirectory(context.StateDir);
		string stamp = _services.Clock.Now.ToLocalTime().ToString("yyyyMMdd_HHmmss");
		string uprojectArg = "-Project=" + context.UProject;

		// --- 1단계: 타깃 증분 빌드 ---
		foreach (BuildTarget target in context.Targets)
		{
			List<string> arguments = new List<string> { target.Name, target.Platform, target.Configuration, uprojectArg, "-WaitMutex" };
			arguments.AddRange(policy.ExtraUbtArgs);
			string log = Path.Combine(logDir, $"{stamp}_{target.Name}_{target.Platform}_{target.Configuration}.log");
			if (!await RunStepAsync(run, context, new BuildInvocation(context.BuildBat, arguments, log, target.ToString(), run.Request.StepTimeout), "증분 빌드", cancellationToken))
			{
				return;
			}
		}

		// --- 2단계: 변경 소스 단독 컴파일 (unity 빌드에 가려진 include 누락 검출) ---
		// (UE5 의 -SingleFile 은 헤더도 단독 컴파일하여 자기 완결적이지 않은 헤더를 검출한다)
		if (state.SingleFiles.Truncated > 0)
		{
			string message = $"단독 컴파일 대상이 {state.SingleFiles.Files.Count + state.SingleFiles.Truncated}개라 앞의 {state.SingleFiles.Files.Count}개만 검사합니다.";
			run.Warn(message);
			run.Diagnostics.Add(Diagnostic.Warning(DiagnosticCodes.SingleFileTruncated, "single-file", message));
		}
		run.SingleFiles = state.SingleFiles.Files.ToList();
		if (run.Steps.Any(x => x.ExitCode != 0) || state.SingleFiles.Files.Count == 0)
		{
			return;
		}
		BuildTarget first = context.Targets[0];
		List<string> singleArguments = new List<string> { first.Name, first.Platform, first.Configuration, uprojectArg, "-WaitMutex" };
		singleArguments.AddRange(policy.ExtraUbtArgs);
		singleArguments.AddRange(state.SingleFiles.Files.Select(x => "-SingleFile=" + x));
		string singleLog = Path.Combine(logDir, $"{stamp}_SingleFile.log");
		await RunStepAsync(run, context, new BuildInvocation(context.BuildBat, singleArguments, singleLog, $"{first} SingleFile", run.Request.StepTimeout),
			$"변경된 소스 {state.SingleFiles.Files.Count}개 단독 컴파일", cancellationToken);
	}

	/// <summary>단계 하나 실행. 취소/시간 초과면 결과를 확정하고 false.</summary>
	async Task<bool> RunStepAsync(Run run, ProjectContext context, BuildInvocation invocation, string description, CancellationToken cancellationToken)
	{
		run.Event(new RunEvent(RunEventKind.StepStarted, $"[{invocation.Label}] {description} 시작", invocation.Label));
		run.Info("Build.bat " + string.Join(" ", invocation.Arguments));
		StepResult step = new StepResult { Label = invocation.Label, Log = invocation.LogPath, Errors = new List<string>(), Tail = new List<string>() };
		if (run.Request.DryRun)
		{
			step.ExitCode = 0;
			run.Steps.Add(step);
			return true;
		}

		Queue<string> tail = new Queue<string>();
		HashSet<string> seen = new HashSet<string>();
		object sync = new object();
		Stopwatch timer = Stopwatch.StartNew();
		BuildProcessResult result = await _services.Builds.RunAsync(invocation, line =>
		{
			lock (sync)
			{
				string trimmed = line.Trim();
				if (BuildOutputParser.IsErrorLine(line) && seen.Add(trimmed))
				{
					step.Errors!.Add(trimmed);
				}
				tail.Enqueue(line);
				if (tail.Count > TailLines)
				{
					tail.Dequeue();
				}
			}
			run.Event(new RunEvent(RunEventKind.Output, line, invocation.Label));
		}, cancellationToken);
		lock (sync)
		{
			step.Tail = tail.ToList();
		}
		step.ExitCode = result.ExitCode;
		step.ErrorCount = step.Errors!.Count;
		step.DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 1);
		step.TimedOut = result.TimedOut;
		run.Steps.Add(step);
		run.Event(new RunEvent(RunEventKind.StepCompleted, $"[{invocation.Label}] 종료코드 {result.ExitCode}", invocation.Label));

		if (result.LeftoverProcesses > 0)
		{
			run.Diagnostics.Add(Diagnostic.Error(DiagnosticCodes.LeftoverProcesses, "build",
				$"하위 프로세스 {result.LeftoverProcesses}개가 종료되지 않았습니다. 작업 관리자에서 확인하세요."));
		}
		if (result.Canceled)
		{
			run.Diagnostics.Add(Diagnostic.Info(DiagnosticCodes.Canceled, "build", "사용자가 검증을 중지했습니다."));
			run.Finish(RunOutcome.Canceled, ErrorKind.Canceled, "사용자 중지");
			return false;
		}
		if (result.TimedOut)
		{
			run.Diagnostics.Add(Diagnostic.Error(DiagnosticCodes.Timeout, "build", $"[{invocation.Label}] 제한 시간({invocation.Timeout})을 넘겨 중지했습니다."));
			run.Finish(RunOutcome.Error, ErrorKind.Timeout, $"{invocation.Label} 시간 초과");
			return false;
		}
		if (result.ExitCode == 0 && step.Errors.Count > 0)
		{
			// 종료 코드가 우선. 오류처럼 보이는 줄은 참고용으로만 남긴다
			run.Diagnostics.Add(Diagnostic.Warning(DiagnosticCodes.ErrorLineWithSuccess, "build",
				$"[{invocation.Label}] 종료 코드 0 이지만 오류처럼 보이는 줄 {step.Errors.Count}개", string.Join("\n", step.Errors.Take(5))));
		}
		return true;
	}

	void Complete(Run run, ProjectContext context, bool stable, bool publishCache, WorkingState state)
	{
		List<StepResult> failures = run.Steps.Where(x => x.ExitCode != 0).ToList();
		string elapsed = run.ElapsedText(_services.Clock.Now);
		const string changedText = "검증 중에 코드 파일이 변경되어 결과를 현재 워킹 카피에 적용할 수 없습니다. 다시 검증하세요.";
		if (!stable)
		{
			// 이 실행의 결과는 시작 시점 상태의 것이 아니므로 상태 키에 연결하지 않는다
			run.StateKey = "";
			run.Diagnostics.Add(new Diagnostic { Code = DiagnosticCodes.ChangedDuringValidation, Severity = failures.Count == 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, Stage = "run", Message = changedText });
			if (failures.Count == 0)
			{
				TryWriteLastResult(run, context, $"ERROR {_services.Clock.Now.ToLocalTime():s} ({elapsed})\r\n{changedText}");
				run.Finish(RunOutcome.Error, ErrorKind.ChangedDuringValidation, "검증 중 코드 변경됨");
				return;
			}
			run.Warn(changedText);
		}

		if (failures.Count == 0)
		{
			if (publishCache && state.Key.Length > 0)
			{
				try
				{
					_services.Store.AddPassCache(context.StateDir, state.Key, state.SingleFiles.Files);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					// 검증은 통과했으므로 결과는 Pass. 캐시를 못 남긴 사실은 경고로 알린다
					run.Diagnostics.Add(Diagnostic.Warning(DiagnosticCodes.CacheWriteFailed, "storage", "통과 캐시를 저장하지 못했습니다. 다음 커밋 때 다시 빌드합니다.", ex.Message));
					run.Warn("통과 캐시 저장 실패: " + ex.Message);
				}
			}
			TryWriteLastResult(run, context, $"PASS {_services.Clock.Now.ToLocalTime():s} ({elapsed})");
			run.Info($"통과 ({elapsed})");
			run.Finish(RunOutcome.Pass, ErrorKind.None, "");
			return;
		}

		foreach (StepResult failure in failures)
		{
			run.Diagnostics.Add(Diagnostic.Error(DiagnosticCodes.BuildFailed, failure.Label, $"[{failure.Label}] 종료코드 {failure.ExitCode}", failure.Log));
			run.Diagnostics.AddRange(failure.Errors!.Select(x => BuildOutputParser.Parse(x, failure.Label)));
		}
		run.Finish(RunOutcome.Fail, ErrorKind.BuildFailed, string.Join(", ", failures.Select(x => x.Label)));
		TryWriteLastResult(run, context, $"FAIL {_services.Clock.Now.ToLocalTime():s}\r\n" + RunReport.FailureText(run.ToResult(_services.Clock.Now), context.Config));
	}

	void TryWriteLastResult(Run run, ProjectContext context, string text)
	{
		try
		{
			_services.Store.WriteLastResult(context.StateDir, text);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			run.Diagnostics.Add(Diagnostic.Warning(DiagnosticCodes.HistoryWriteFailed, "storage", "LastResult.txt 를 쓰지 못했습니다.", ex.Message));
		}
	}

	/// <summary>의미 있는 실행만 기록 (코드 변경이 없어 건너뛴 커밋은 제외, 우회 태그는 기록)</summary>
	void SaveHistory(Run run, RunResult result)
	{
		if (run.Context == null || run.Request.DryRun || (result.Outcome == RunOutcome.Skipped && !run.RecordHistory))
		{
			return;
		}
		try
		{
			_services.Store.AppendHistory(run.Context.UProject, run.Context.StateDir, result);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Diagnostic diagnostic = Diagnostic.Warning(DiagnosticCodes.HistoryWriteFailed, "storage", "실행 기록(History.jsonl)을 저장하지 못했습니다.", ex.Message);
			result.Diagnostics ??= new List<Diagnostic>();
			result.Diagnostics.Add(diagnostic);
			run.Warn(diagnostic.Message + " " + ex.Message);
		}
	}

	#endregion

	/// <summary>실행 중 상태</summary>
	sealed class Run
	{
		readonly Action<RunEvent> _onEvent;
		readonly DateTimeOffset _start;

		public ValidationRequest Request { get; }
		public ProjectContext? Context { get; set; }
		public List<string> Files { get; set; } = new();
		public List<string> SingleFiles { get; set; } = new();
		public List<StepResult> Steps { get; } = new();
		public List<Diagnostic> Diagnostics { get; } = new();
		public string StateKey { get; set; } = "";
		public string Revision { get; set; } = "";
		public bool CacheReused { get; set; }
		public bool RecordHistory { get; set; }
		public RunOutcome? Outcome { get; private set; }
		public ErrorKind Kind { get; private set; }
		public string Reason { get; private set; } = "";

		public Run(ValidationRequest request, DateTimeOffset start, Action<RunEvent> onEvent)
		{
			Request = request;
			_start = start;
			_onEvent = onEvent;
		}

		public void Finish(RunOutcome outcome, ErrorKind kind, string reason)
		{
			Outcome = outcome;
			Kind = kind;
			Reason = reason;
		}

		public void Event(RunEvent e)
		{
			try
			{
				_onEvent(e);
			}
			catch (Exception)
			{
				// 표시 쪽 오류가 검증 결과를 바꾸지 않게 한다
			}
		}

		public void Info(string message) => Event(new RunEvent(RunEventKind.Info, message));

		public void Warn(string message) => Event(new RunEvent(RunEventKind.Warning, message));

		public string ElapsedText(DateTimeOffset now)
		{
			TimeSpan elapsed = now - _start;
			return $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s";
		}

		public RunResult ToResult(DateTimeOffset now)
		{
			RunOutcome outcome = Outcome ?? RunOutcome.Error;
			int exitCode = ExitCodes.For(outcome);
			return new RunResult
			{
				RunId = RunIds.Next(_start),
				Trigger = Request.Trigger,
				StartTime = _start.ToString("o"),
				EndTime = now.ToString("o"),
				Result = outcome.ToString(),
				Reason = Reason,
				ElapsedSeconds = Math.Round((now - _start).TotalSeconds, 1),
				Project = Context?.UProject ?? "",
				ProjectDir = Context?.ProjectDir ?? "",
				EngineRoot = Context?.EngineRoot ?? "",
				Targets = Context?.Targets.Select(x => x.ToString()).ToList() ?? new List<string>(),
				Revision = Revision,
				StateKey = StateKey,
				Files = Files.ToList(),
				SingleFiles = SingleFiles.ToList(),
				Steps = Steps.ToList(),
				Diagnostics = Diagnostics.ToList(),
				ExitCode = exitCode,
				CommitAllowed = exitCode == ExitCodes.Allowed,
				CacheReused = CacheReused,
				ErrorKind = Kind == ErrorKind.None ? null : Kind.ToString(),
			};
		}
	}
}

static class RunIds
{
	static int _counter;

	public static string Next(DateTimeOffset start) => $"{start.ToLocalTime():yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Interlocked.Increment(ref _counter)}";
}

/// <summary>사람이 읽는 결과 요약 (CLI stderr, TortoiseSVN 커밋 창, LastResult.txt)</summary>
public static class RunReport
{
	public static string FailureText(RunResult run, ShipCheckConfig config)
	{
		StringBuilder report = new StringBuilder();
		int maxLines = Math.Max(1, config.MaxErrorLines);
		switch (run.Outcome)
		{
			case RunOutcome.Fail when run.ErrorKind == nameof(ErrorKind.BuildFailed):
				report.AppendLine($"ShipCheck: 게임 빌드 검증 실패 - 커밋이 차단되었습니다. ({FormatElapsed(run.ElapsedSeconds)})");
				foreach (StepResult step in (run.Steps ?? new List<StepResult>()).Where(x => x.ExitCode != 0))
				{
					report.AppendLine();
					report.AppendLine($"[{step.Label}] 종료코드 {step.ExitCode}");
					List<string> lines = step.Errors is { Count: > 0 } ? step.Errors : step.Tail ?? new List<string>();
					foreach (string line in lines.Take(maxLines))
					{
						report.AppendLine("  " + line);
					}
					if ((step.Errors?.Count ?? 0) > maxLines)
					{
						report.AppendLine($"  ... 외 {step.Errors!.Count - maxLines}건");
					}
					report.AppendLine($"  전체 로그: {step.Log}");
				}
				break;
			default:
				report.AppendLine($"ShipCheck: {run.ResultText} - {run.Reason}");
				foreach (Diagnostic diagnostic in (run.Diagnostics ?? new List<Diagnostic>()).Where(x => x.Severity == DiagnosticSeverity.Error))
				{
					report.AppendLine($"  [{diagnostic.Code}] {diagnostic.Message}");
					if (!string.IsNullOrWhiteSpace(diagnostic.Detail))
					{
						foreach (string line in diagnostic.Detail.Split('\n').Take(maxLines))
						{
							report.AppendLine("    " + line.TrimEnd('\r'));
						}
					}
				}
				break;
		}
		if ((run.Diagnostics ?? new List<Diagnostic>()).Any(x => x.Code == DiagnosticCodes.ChangedDuringValidation) && run.Outcome == RunOutcome.Fail)
		{
			report.AppendLine();
			report.AppendLine("주의: 검증 중에 코드 파일이 변경되었습니다. 수정 후 다시 검증하세요.");
		}
		if (run.Outcome == RunOutcome.Fail && !string.IsNullOrEmpty(config.SkipTag))
		{
			report.AppendLine();
			report.AppendLine($"긴급 우회가 필요하면 커밋 메시지에 {config.SkipTag} 를 넣으세요. (사유를 함께 기록)");
		}
		return report.ToString();
	}

	public static string FormatElapsed(double seconds) => $"{(int)(seconds / 60)}m{(int)(seconds % 60):00}s";
}
