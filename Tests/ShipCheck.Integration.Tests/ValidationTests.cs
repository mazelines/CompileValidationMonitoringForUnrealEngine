using System.Collections.Concurrent;
using ShipCheck.Core;
using ShipCheck.Infrastructure;
using ShipCheck.TestSupport;

namespace ShipCheck.Integration.Tests;

/// <summary>기존 Run-Regression.ps1 시나리오 + 2차 리뷰(R2-1~R2-5) 회귀 행렬</summary>
[TestClass]
public sealed class ValidationTests
{
	const string Hero = @"Source\Game\Private\Hero.cpp";
	const string HeroH = @"Source\Game\Private\Hero.h";
	const string Bug = @"Source\Game\Private\Bug.cpp";
	const string Helper = @"Source\Game\Private\Helper.h";
	const string EdTool = @"Source\GameEditor\Private\EdTool.cpp";
	static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

	ShipCheckFixture _f = null!;

	[TestInitialize]
	public void Setup() => _f = new ShipCheckFixture();

	[TestCleanup]
	public void Cleanup() => _f.Dispose();

	static (EventWaitHandle Started, EventWaitHandle Release, string StartedName, string ReleaseName) Barrier()
	{
		string id = Guid.NewGuid().ToString("N");
		string started = $@"Local\ShipCheckTest_Started_{id}";
		string release = $@"Local\ShipCheckTest_Release_{id}";
		return (new EventWaitHandle(false, EventResetMode.ManualReset, started), new EventWaitHandle(false, EventResetMode.ManualReset, release), started, release);
	}

	[TestMethod, Timeout(120_000)]
	public async Task 콘텐츠나_에디터_모듈만_바뀌면_건너뜀()
	{
		_f.Touch(@"Content\Map.umap");
		RunResult run = await _f.ValidateAsync(new[] { @"Content\Map.umap" });
		Assert.AreEqual("Skipped", run.Result);
		_f.Touch(EdTool);
		run = await _f.ValidateAsync(new[] { EdTool });
		Assert.AreEqual("Skipped", run.Result);
		Assert.AreEqual(0, _f.Calls().Count);
	}

	[TestMethod, Timeout(120_000)]
	public async Task 런타임_변경은_빌드와_단독_컴파일_후_캐시()
	{
		_f.Touch(Hero);
		RunResult run = await _f.ValidateAsync(new[] { Hero });
		Assert.AreEqual("Pass", run.Result, Describe(run));
		Assert.AreEqual(2, run.Steps!.Count);
		List<string[]> calls = _f.Calls();
		CollectionAssert.AreEqual(new[] { "Game", "Win64", "Shipping", "-Project=" + _f.UProject, "-WaitMutex" }, calls[0], "공백/한글 경로가 인수 하나로 전달");
		CollectionAssert.Contains(calls[1], "-SingleFile=" + _f.F(Hero));
		Assert.IsTrue(File.Exists(run.Steps[0].Log));

		run = await _f.ValidateAsync(new[] { Hero });
		Assert.AreEqual("Cached", run.Result);
		Assert.AreEqual(2, _f.Calls().Count, "캐시 적중 시 빌드하지 않음");
	}

	[TestMethod, Timeout(120_000)]
	public async Task 정책을_강화하면_캐시를_재사용하지_않는다()
	{
		_f.Touch(Hero);
		_f.Touch(HeroH);
		_f.WriteConfig(c => c.SingleFileCheck.IncludeHeaders = false);
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero, HeroH })).Result);
		_f.WriteConfig();
		RunResult run = await _f.ValidateAsync(new[] { Hero, HeroH });
		Assert.AreEqual("Pass", run.Result, "헤더 포함 정책으로 다시 빌드");
		CollectionAssert.Contains(_f.Calls().Last(), "-SingleFile=" + _f.F(HeroH));
	}

	[TestMethod, Timeout(120_000)]
	public async Task 다른_파일만_검증된_캐시는_재사용하지_않는다()
	{
		_f.Touch(Hero);
		_f.Touch(Bug);
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result);
		RunResult run = await _f.ValidateAsync(new[] { Bug });
		Assert.AreEqual("Fail", run.Result);
		Assert.AreEqual(1, run.ExitCode);
		Diagnostic error = run.Diagnostics!.Single(x => x.ToolCode == "C1083");
		Assert.AreEqual(_f.F(Bug), error.File);
		Assert.AreEqual(1, error.Line);
	}

	[TestMethod, Timeout(120_000)]
	public async Task 상위_집합_검증_후_부분_커밋은_캐시_개별_커밋으로_기준이_바뀌면_무효()
	{
		_f.Touch(Hero);
		_f.Touch(HeroH);
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero, HeroH })).Result);
		Assert.IsTrue((await _f.QueryAsync()).UpToDate);
		Assert.AreEqual("Cached", (await _f.ValidateAsync(new[] { HeroH })).Result);

		// 루트 리비전은 그대로인데 깨끗한 파일의 기준이 바뀜 (개별 파일 커밋)
		_f.Touch(Helper);
		_f.Commit(Helper);
		Assert.IsFalse((await _f.QueryAsync()).UpToDate);
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero, HeroH })).Result);
	}

	[TestMethod, Timeout(120_000)]
	public async Task 미등록_소스_경고와_엄격_정책()
	{
		_f.Touch(Hero);
		_f.Write(@"Source\Game\Private\Untracked.cpp", "new");
		List<RunEvent> events = new List<RunEvent>();
		RunResult run = await _f.ValidateAsync(new[] { Hero }, onEvent: e => { lock (events) { events.Add(e); } });
		Assert.AreEqual("Pass", run.Result);
		Assert.IsTrue(events.Any(x => x.Kind == RunEventKind.Warning && x.Message.Contains("Untracked.cpp")));
		_f.WriteConfig(c => c.UncommittedSourceChanges = "Fail");
		run = await _f.ValidateAsync(new[] { Hero });
		Assert.AreEqual("Fail", run.Result);
		Assert.AreEqual(nameof(ErrorKind.PolicyBlocked), run.ErrorKind);
	}

	[TestMethod, Timeout(120_000)]
	public async Task R2_2_미등록_폴더_안의_소스()
	{
		_f.Touch(Hero);
		_f.Write(@"Source\Game\Private\NewDir\Hidden.cpp", "good");
		_f.Write(@"Source\Game\Private\NewDir\Intermediate\Gen.cpp", "gen");
		QueryResult query = await _f.QueryAsync();
		Assert.IsTrue(query.Changes.Any(x => x.Path.EndsWith("Hidden.cpp") && x.Status == "?"));
		Assert.IsFalse(query.Changes.Any(x => x.Path.EndsWith("Gen.cpp")), "산출물 폴더 제외");

		_f.WriteConfig(c => c.UncommittedSourceChanges = "Fail");
		RunResult run = await _f.ValidateAsync(new[] { Hero });
		Assert.AreEqual("Fail", run.Result);
		StringAssert.Contains(run.Diagnostics!.Single(x => x.Code == DiagnosticCodes.UncommittedChanges).Detail, "Hidden.cpp");

		_f.WriteConfig();
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result);
		_f.Write(@"Source\Game\Private\NewDir\Hidden.cpp", "changed");
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result, "미등록 폴더 안 파일이 바뀌면 캐시 재사용 안 함");
	}

	[TestMethod, Timeout(120_000)]
	public async Task 타깃_빌드_실패()
	{
		_f.Touch(Hero);
		_f.Control(new FakeControl { Fail = true, OutputLines = { "한글 출력 확인" } });
		ConcurrentQueue<RunEvent> events = new ConcurrentQueue<RunEvent>();
		RunResult run = await _f.ValidateAsync(new[] { Hero }, onEvent: events.Enqueue);
		Assert.AreEqual("Fail", run.Result);
		Assert.AreEqual(1, run.Steps!.Count, "타깃 빌드 실패 시 단독 컴파일 생략");
		Diagnostic error = run.Diagnostics!.Single(x => x.ToolCode == "C2065");
		Assert.AreEqual(42, error.Line);
		Assert.IsTrue(events.Any(x => x.Kind == RunEventKind.Output && x.Message == "한글 출력 확인"), "OEM 코드 페이지 출력 디코딩");
		string last = File.ReadAllText(Path.Combine(_f.ProjectDir, "Saved", "ShipCheck", "LastResult.txt"));
		StringAssert.StartsWith(last, "FAIL");
	}

	[TestMethod, Timeout(120_000)]
	public async Task 동시_실행은_한쪽만_빌드()
	{
		_f.Touch(Hero);
		(EventWaitHandle started, EventWaitHandle release, string startedName, string releaseName) = Barrier();
		using (started)
		using (release)
		{
			_f.Control(new FakeControl { StartedEvent = startedName, ReleaseEvent = releaseName });
			Task<RunResult> first = _f.ValidateAsync(new[] { Hero });
			Assert.IsTrue(started.WaitOne(Wait), "첫 실행의 빌드 시작");
			TaskCompletionSource waiting = new TaskCompletionSource();
			Task<RunResult> second = _f.ValidateAsync(new[] { Hero }, onEvent: e => { if (e.Message.Contains("다른 ShipCheck")) { waiting.TrySetResult(); } });
			await waiting.Task.WaitAsync(Wait);
			release.Set();
			RunResult a = await first;
			RunResult b = await second;
			Assert.AreEqual("Pass", a.Result, Describe(a));
			Assert.AreEqual("Cached", b.Result, Describe(b));
		}
	}

	[TestMethod, Timeout(120_000)]
	public async Task R2_1_빌드_중_변경했다가_되돌리면_통과로_인정하지_않는다()
	{
		_f.Touch(Hero);
		byte[] original = File.ReadAllBytes(_f.F(Hero));
		(EventWaitHandle started, EventWaitHandle release, string startedName, string releaseName) = Barrier();
		using (started)
		using (release)
		{
			_f.Control(new FakeControl { StartedEvent = startedName, ReleaseEvent = releaseName });
			Task<RunResult> running = _f.ValidateAsync(new[] { Hero });
			Assert.IsTrue(started.WaitOne(Wait));
			File.WriteAllText(_f.F(Hero), "edited during build");
			File.WriteAllBytes(_f.F(Hero), original);
			File.SetLastWriteTimeUtc(_f.F(Hero), DateTime.UtcNow.AddSeconds(5));
			release.Set();
			RunResult run = await running;
			Assert.AreEqual("Error", run.Result, Describe(run));
			Assert.AreEqual(nameof(ErrorKind.ChangedDuringValidation), run.ErrorKind);
			Assert.AreEqual(2, run.ExitCode);
			Assert.AreEqual("", run.StateKey);
		}
		_f.Control(new FakeControl());
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result, "변경된 상태는 캐시되지 않았으므로 다시 빌드");
	}

	[TestMethod, Timeout(120_000)]
	public async Task R2_1_잠금_대기_중_변경되면_대기한_실행은_새_상태로_검증한다()
	{
		_f.Touch(Hero);
		(EventWaitHandle started, EventWaitHandle release, string startedName, string releaseName) = Barrier();
		using (started)
		using (release)
		{
			_f.Control(new FakeControl { StartedEvent = startedName, ReleaseEvent = releaseName });
			Task<RunResult> first = _f.ValidateAsync(new[] { Hero });
			Assert.IsTrue(started.WaitOne(Wait));
			TaskCompletionSource waiting = new TaskCompletionSource();
			Task<RunResult> second = _f.ValidateAsync(new[] { Hero }, onEvent: e => { if (e.Message.Contains("다른 ShipCheck")) { waiting.TrySetResult(); } });
			await waiting.Task.WaitAsync(Wait);
			_f.Touch(Hero);
			_f.Control(new FakeControl());
			release.Set();
			RunResult a = await first;
			RunResult b = await second;
			Assert.AreEqual("Error", a.Result, "첫 실행은 빌드 중 변경");
			Assert.AreEqual("Pass", b.Result, "대기한 실행은 캐시 없이 새 상태를 빌드");
			Assert.AreEqual((await _f.QueryAsync()).StateKey, b.StateKey);
		}
	}

	[TestMethod, Timeout(120_000)]
	public async Task R2_3_자동_탐색한_프로젝트에도_전용_설정을_적용한다()
	{
		_f.Touch(Hero);
		_f.Touch(Helper);
		_f.WriteConfig(c => c.UncommittedSourceChanges = "Warn", Path.Combine(_f.ToolDir, ShipCheckConfig.DefaultFileName));
		_f.WriteConfig(c => c.UncommittedSourceChanges = "Fail", Path.Combine(_f.ToolDir, ShipCheckConfig.PerProjectFileName("Game")));
		ValidationService service = _f.Service();
		ValidationRequest Request(string? project, string? config) => new ValidationRequest { Selector = new ProjectSelector(project, _f.ProjectDir, config), Paths = new[] { _f.F(Hero) } };

		RunResult auto = await service.ValidateAsync(Request(null, null), null, CancellationToken.None);
		RunResult explicitProject = await service.ValidateAsync(Request(_f.UProject, null), null, CancellationToken.None);
		RunResult explicitConfig = await service.ValidateAsync(Request(null, Path.Combine(_f.ToolDir, ShipCheckConfig.DefaultFileName)), null, CancellationToken.None);
		Assert.AreEqual("Fail", auto.Result, "자동 탐색 + 전용 설정(Fail)");
		Assert.AreEqual("Fail", explicitProject.Result);
		Assert.AreEqual("Pass", explicitConfig.Result, "명시한 설정(Warn)이 우선");
	}

	[TestMethod, Timeout(120_000)]
	public async Task R2_4_워킹_카피가_아니면_상태_불명()
	{
		string export = Path.Combine(_f.Root, "export", "Game");
		_f.Svn("export", "-q", _f.RepoUrl + "/Game", export);
		QueryResult query = await _f.QueryAsync(project: export);
		Assert.IsFalse(query.SvnAvailable);
		Assert.AreEqual(SourceControlStatus.NotWorkingCopy, query.SvnState);
		Assert.AreEqual("", query.StateKey ?? "");
		Assert.AreEqual(Health.NeedsValidation, HealthEvaluator.Evaluate(true, false, null, query, Array.Empty<RunResult>()));

		ValidationRequest Request() => new ValidationRequest { Selector = new ProjectSelector(export, null, _f.ConfigPath), Paths = new[] { Path.Combine(export, Hero) } };
		RunResult warn = await _f.Service().ValidateAsync(Request(), null, CancellationToken.None);
		Assert.AreEqual("Pass", warn.Result, "Warn 정책: 빌드는 하되");
		Assert.IsTrue(warn.Diagnostics!.Any(x => x.Code == DiagnosticCodes.SvnNotWorkingCopy));
		Assert.IsFalse(File.Exists(Path.Combine(export, "Saved", "ShipCheck", "PassCache.txt")), "캐시하지 않음");

		_f.WriteConfig(c => c.UncommittedSourceChanges = "Fail");
		RunResult strict = await _f.Service().ValidateAsync(Request(), null, CancellationToken.None);
		Assert.AreEqual("Error", strict.Result);
		Assert.AreEqual(2, strict.ExitCode);
	}

	[TestMethod, Timeout(120_000)]
	public async Task R2_4_svn_도구가_없으면_상태_불명()
	{
		QueryResult query = await _f.QueryAsync(withSvn: false);
		Assert.AreEqual(SourceControlStatus.ToolMissing, query.SvnState);
		Assert.IsFalse(query.SvnAvailable);
	}

	[TestMethod, Timeout(120_000)]
	public async Task R2_5_캐시를_꺼도_실패가_현재_상태와_연결된다()
	{
		_f.WriteConfig(c => c.UseCache = false);
		_f.Touch(Bug);
		RunResult failed = await _f.ValidateAsync(new[] { Bug });
		Assert.AreEqual("Fail", failed.Result);
		QueryResult query = await _f.QueryAsync();
		Assert.AreNotEqual("", query.StateKey);
		Assert.AreEqual(query.StateKey, failed.StateKey);
		Assert.AreEqual(Health.Failed, HealthEvaluator.Evaluate(true, false, null, query, FileRunStore.ReadHistory(_f.ProjectDir)));

		_f.Touch(Hero);
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result);
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result, "캐시 사용 안 함");
	}

	[TestMethod, Timeout(120_000)]
	public async Task 우회_태그는_기록에_남는다()
	{
		_f.Touch(Hero);
		RunResult run = await _f.ValidateAsync(new[] { Hero }, r => new ValidationRequest { Selector = r.Selector, Paths = r.Paths, Message = "hotfix [skip-shipcheck] 서버 장애 대응" });
		Assert.AreEqual("Skipped", run.Result);
		Assert.IsTrue(FileRunStore.ReadHistory(_f.ProjectDir).Any(x => x.Result == "Skipped" && x.Reason!.Contains("서버 장애")));
	}

	[TestMethod, Timeout(120_000)]
	public async Task 취소하면_손자_프로세스까지_종료된다()
	{
		_f.Touch(Hero);
		string pidFile = Path.Combine(_f.Root, "pids.txt");
		_f.Control(new FakeControl { SpawnChildren = true, PidFile = pidFile });
		using CancellationTokenSource cancel = new CancellationTokenSource();
		Task<RunResult> running = _f.ValidateAsync(new[] { Hero }, cancellationToken: cancel.Token);
		Assert.IsTrue(Processes.WaitUntil(() => Processes.ReadPids(pidFile).Count >= 3, Wait), "모의 UBT + 자식 + 손자");
		List<int> pids = Processes.ReadPids(pidFile);
		Assert.IsTrue(pids.All(Processes.IsAlive));
		cancel.Cancel();
		RunResult run = await running.WaitAsync(Wait);
		Assert.AreEqual("Canceled", run.Result);
		Assert.AreEqual(2, run.ExitCode);
		Assert.IsFalse(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.LeftoverProcesses));
		Assert.IsTrue(Processes.WaitUntil(() => !pids.Any(Processes.IsAlive), TimeSpan.FromSeconds(5)), "잔류: " + string.Join(",", pids.Where(Processes.IsAlive)));
		Assert.IsTrue(FileRunStore.ReadHistory(_f.ProjectDir).Any(x => x.Result == "Canceled"), "취소도 기록");
	}

	[TestMethod, Timeout(120_000)]
	public async Task 시간_초과는_오류이고_프로세스를_정리한다()
	{
		_f.Touch(Hero);
		string pidFile = Path.Combine(_f.Root, "pids.txt");
		(EventWaitHandle started, EventWaitHandle release, string startedName, string releaseName) = Barrier();
		using (started)
		using (release)
		{
			_f.Control(new FakeControl { ReleaseEvent = releaseName, PidFile = pidFile });
			RunResult run = await _f.ValidateAsync(new[] { Hero }, r => new ValidationRequest { Selector = r.Selector, Paths = r.Paths, StepTimeout = TimeSpan.FromSeconds(3) });
			Assert.AreEqual("Error", run.Result);
			Assert.AreEqual(nameof(ErrorKind.Timeout), run.ErrorKind);
			Assert.IsTrue(run.Steps![0].TimedOut);
			Assert.IsTrue(Processes.WaitUntil(() => !Processes.ReadPids(pidFile).Any(Processes.IsAlive), TimeSpan.FromSeconds(5)));
		}
	}

	[TestMethod, Timeout(120_000)]
	public async Task 출력_파이프를_문_잔류_프로세스가_있어도_끝나고_정리된다()
	{
		_f.Touch(Hero);
		string pidFile = Path.Combine(_f.Root, "pids.txt");
		_f.Control(new FakeControl { LingerChild = true, PidFile = pidFile });
		System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
		RunResult run = await _f.ValidateAsync(new[] { Hero }).WaitAsync(Wait);
		Assert.AreEqual("Pass", run.Result, Describe(run));
		Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(40), $"{timer.Elapsed}");
		Assert.IsTrue(Processes.WaitUntil(() => !Processes.ReadPids(pidFile).Any(Processes.IsAlive), TimeSpan.FromSeconds(5)));
	}

	[TestMethod, Timeout(120_000)]
	public async Task UBT_추가_인수_경계_보존과_안전하지_않은_인수_거부()
	{
		_f.Touch(Hero);
		_f.WriteConfig(c => c.ExtraUbtArgs = new List<string> { "-Define=A B", "-NoHotReload" });
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result);
		string[] call = _f.Calls()[0];
		CollectionAssert.AreEqual(new[] { "-Define=A B", "-NoHotReload" }, call[^2..]);

		_f.WriteConfig(c => c.ExtraUbtArgs = new List<string> { "-Path=%TEMP%" });
		RunResult run = await _f.ValidateAsync(new[] { Hero });
		Assert.AreEqual("Error", run.Result);
		Assert.AreEqual(nameof(ErrorKind.ConfigError), run.ErrorKind);
		Assert.IsTrue(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.InvalidArgument));
	}

	[TestMethod, Timeout(120_000)]
	public async Task 손상된_캐시와_기록은_통과_근거로_쓰지_않는다()
	{
		_f.Touch(Hero);
		RunResult first = await _f.ValidateAsync(new[] { Hero });
		string stateDir = Path.Combine(_f.ProjectDir, "Saved", "ShipCheck");
		// 구형식(머리말 없음) 캐시
		File.WriteAllText(Path.Combine(stateDir, "PassCache.txt"), first.StateKey + "\t" + _f.F(Hero) + "\r\n");
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero })).Result, "구형식 캐시 무효");
		// 손상된 기록 줄
		File.AppendAllText(Path.Combine(stateDir, "History.jsonl"), "{ broken json\n");
		List<RunResult> history = FileRunStore.ReadHistory(_f.ProjectDir);
		Assert.AreEqual(2, history.Count);
	}

	[TestMethod, Timeout(120_000)]
	public async Task 캐시_저장_실패는_경고로_남긴다()
	{
		_f.Touch(Hero);
		Assert.AreEqual("Pass", (await _f.ValidateAsync(new[] { Hero }, r => new ValidationRequest { Selector = r.Selector, Paths = r.Paths, Force = true })).Result);
		string cache = Path.Combine(_f.ProjectDir, "Saved", "ShipCheck", "PassCache.txt");
		_f.Touch(Hero);
		File.SetAttributes(cache, FileAttributes.ReadOnly);
		RunResult run = await _f.ValidateAsync(new[] { Hero });
		Assert.AreEqual("Pass", run.Result);
		Assert.IsTrue(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.CacheWriteFailed));
	}

	[TestMethod, Timeout(120_000)]
	public async Task 비정상_종료로_버려진_잠금을_넘겨받는다()
	{
		using (System.Diagnostics.Process holder = System.Diagnostics.Process.Start(Products.FakeBuildExe, new[] { "--hold-mutex", NamedMutexLockProvider.ProjectLockName(_f.UProject) }))
		{
			await holder.WaitForExitAsync().WaitAsync(Wait);
		}
		_f.Touch(Hero);
		RunResult run = await _f.ValidateAsync(new[] { Hero }).WaitAsync(Wait);
		Assert.AreEqual("Pass", run.Result);
	}

	[TestMethod, Timeout(120_000)]
	public async Task 전체_빌드는_단독_컴파일_없이_캐시하지_않는다()
	{
		RunResult run = await _f.ValidateAsync(Array.Empty<string>(), r => new ValidationRequest { Selector = r.Selector, All = true });
		Assert.AreEqual("Pass", run.Result);
		Assert.AreEqual(1, run.Steps!.Count);
		Assert.IsFalse(File.Exists(Path.Combine(_f.ProjectDir, "Saved", "ShipCheck", "PassCache.txt")));
	}

	[TestMethod, Timeout(120_000)]
	public void 편집기_명령_공백_경로()
	{
		string editorDir = Path.Combine(_f.Root, "My Editor");
		Directory.CreateDirectory(editorDir);
		string editor = Path.Combine(editorDir, "fake editor.cmd");
		string output = Path.Combine(_f.Root, "editor-args.txt");
		File.WriteAllText(editor, $"@echo off\r\necho [%~1] [%~2]> \"{output}\"\r\n", ProcessRunner.ConsoleEncoding);
		string source = Path.Combine(_f.Root, "Source Dir", "My File.cpp");
		foreach ((string template, string expected) in new[]
		{
			($"\"{editor}\" -g \"{{file}}:{{line}}\"", $"[-g] [{source}:42]"),
			($"\"{editor}\" \"{{file}}\" --line {{line}}", $"[{source}] [--line]"),
		})
		{
			File.Delete(output);
			using (System.Diagnostics.Process? process = System.Diagnostics.Process.Start(EditorCommands.CreateStartInfo(template, source, 42)))
			{
				process?.WaitForExit(10_000);
			}
			Assert.AreEqual(expected, File.ReadAllText(output, ProcessRunner.ConsoleEncoding).Trim());
		}
	}

	static string Describe(RunResult run) =>
		$"{run.Result} {run.ErrorKind} {run.Reason} | " + string.Join(" / ", (run.Diagnostics ?? new List<Diagnostic>()).Select(x => $"{x.Code}: {x.Message} {x.Detail}"));
}
