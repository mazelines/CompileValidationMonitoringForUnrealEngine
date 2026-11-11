using ShipCheck.Core;

namespace ShipCheck.Core.Tests;

/// <summary>검증 순서/판정을 가짜 경계로 확인한다 (프로세스/SVN/디스크 없이)</summary>
[TestClass]
public sealed class ValidationServiceTests
{
	const string ProjectDir = @"D:\W\Game";
	const string Hero = @"D:\W\Game\Source\Game\Private\Hero.cpp";
	const string Other = @"D:\W\Game\Source\Game\Private\Other.cpp";

	sealed class Fakes
	{
		public ShipCheckConfig Config { get; } = new ShipCheckConfig();
		public Queue<SourceControlState> States { get; } = new();
		public SourceControlState? LastState;
		public Dictionary<string, string> Contents { get; } = new(StringComparer.OrdinalIgnoreCase) { [Hero] = "hero", [Other] = "other" };
		public Dictionary<string, long> Stamps { get; } = new(StringComparer.OrdinalIgnoreCase) { [Hero] = 1, [Other] = 1 };
		public Func<BuildInvocation, CancellationToken, Task<BuildProcessResult>> Build { get; set; } = (_, _) => Task.FromResult(new BuildProcessResult(0, false, false, 0));
		public List<BuildInvocation> Builds { get; } = new();
		public List<string> Lines { get; } = new();
		public bool LockWaits { get; set; }
		public Action? OnLockAcquired { get; set; }
		public List<(string Key, IReadOnlyList<string> Files)> Cache { get; } = new();
		public bool CacheWriteFails { get; set; }
		public List<RunResult> History { get; } = new();
		public Exception? SvnThrows { get; set; }

		public SourceControlState Modified(params string[] files) => new SourceControlState
		{
			Status = SourceControlStatus.Available,
			Revision = "5",
			Baseline = "base",
			Changes = files.Select(x => new SourceChange(x, "M")).ToList(),
		};

		public ValidationService Service() => new ValidationService(new ShipCheckServices
		{
			Locator = new Locator(this),
			SourceControl = new Svn(this),
			Modules = new Modules(),
			Files = new Files(this),
			Builds = new Builder(this),
			Locks = new Locks(this),
			Store = new Store(this),
			Clock = new Clock(),
		});

		public Task<RunResult> ValidateAsync(params string[] paths) => Service().ValidateAsync(new ValidationRequest { Selector = new ProjectSelector(ProjectDir + @"\Game.uproject", null, null), Paths = paths, Trigger = "test" }, null, CancellationToken.None);
	}

	sealed class Locator(Fakes f) : IProjectLocator
	{
		public ProjectContext Resolve(ProjectSelector selector) => new ProjectContext
		{
			UProject = ProjectDir + @"\Game.uproject",
			ConfigPath = @"C:\Tool\ShipCheck.config.json",
			Config = f.Config,
			ToolDir = @"C:\Tool",
			Targets = BuildTarget.FromConfig(f.Config, "Game"),
		};

		public string ResolveEngineRoot(ProjectContext context) => @"D:\Engine";

		public bool IsTargetPrimed(string projectDir, BuildTarget target) => true;
	}

	sealed class Svn(Fakes f) : ISourceControl
	{
		public Task<SourceControlState> GetStateAsync(string projectDir, IReadOnlyList<string> scope, CancellationToken cancellationToken)
		{
			if (f.SvnThrows != null)
			{
				throw f.SvnThrows;
			}
			if (f.States.Count > 0)
			{
				f.LastState = f.States.Dequeue();
			}
			return Task.FromResult(f.LastState ?? f.Modified());
		}
	}

	sealed class Modules : IModuleResolver
	{
		public ModuleInfo? Find(string filePath) => new ModuleInfo("Game", ProjectDir, "Runtime");
	}

	sealed class Files(Fakes f) : IFileSystem
	{
		public bool FileExists(string path) => f.Contents.ContainsKey(path);
		public bool DirectoryExists(string path) => false;
		public string HashFile(string path) => f.Contents.TryGetValue(path, out string? content) ? content : "deleted";
		public IReadOnlyList<string> EnumerateCodeFiles(string directory) => Array.Empty<string>();
		public IReadOnlyList<FileStamp> StampCodeFiles(IEnumerable<string> roots, IEnumerable<string> extraFiles) =>
			f.Stamps.OrderBy(x => x.Key).Select(x => new FileStamp(x.Key, x.Value, f.Contents[x.Key].Length)).ToList();
		public string ReadAllText(string path) => f.Contents[path];
	}

	sealed class Builder(Fakes f) : IBuildRunner
	{
		public Task<BuildProcessResult> RunAsync(BuildInvocation invocation, Action<string> onLine, CancellationToken cancellationToken)
		{
			f.Builds.Add(invocation);
			foreach (string line in f.Lines)
			{
				onLine(line);
			}
			return f.Build(invocation, cancellationToken);
		}
	}

	sealed class Locks(Fakes f) : ILockProvider
	{
		public Task<(IProjectLock Lock, bool Waited)> AcquireAsync(string uproject, Action onWaiting, CancellationToken cancellationToken)
		{
			if (f.LockWaits)
			{
				onWaiting();
				f.OnLockAcquired?.Invoke();
			}
			return Task.FromResult<(IProjectLock, bool)>((new Lock(), f.LockWaits));
		}

		sealed class Lock : IProjectLock
		{
			public void Dispose()
			{
			}
		}
	}

	sealed class Store(Fakes f) : IRunStore
	{
		public IReadOnlyList<PassCache.Entry> ReadPassCache(string stateDir) => f.Cache.Select(x => new PassCache.Entry(x.Key, x.Files)).ToList();

		public void AddPassCache(string stateDir, string key, IReadOnlyList<string> verifiedFiles)
		{
			if (f.CacheWriteFails)
			{
				throw new UnauthorizedAccessException("읽기 전용");
			}
			f.Cache.Add((key, verifiedFiles));
		}

		public void AppendHistory(string uproject, string stateDir, RunResult run) => f.History.Add(run);
		public void WriteLastResult(string stateDir, string text) { }
		public string PrepareLogDirectory(string stateDir) => @"D:\W\Game\Saved\ShipCheck\Logs";
	}

	sealed class Clock : IClock
	{
		public DateTimeOffset Now => DateTimeOffset.Now;
	}

	[TestMethod]
	public async Task 통과_후_같은_상태는_캐시()
	{
		Fakes f = new Fakes();
		f.States.Enqueue(f.Modified(Hero));
		RunResult first = await f.ValidateAsync(Hero);
		Assert.AreEqual("Pass", first.Result);
		Assert.AreEqual(2, f.Builds.Count, "타깃 빌드 + 단독 컴파일");
		Assert.IsTrue(f.Builds[1].Arguments.Contains("-SingleFile=" + Hero));
		RunResult second = await f.ValidateAsync(Hero);
		Assert.AreEqual("Cached", second.Result);
		Assert.IsTrue(second.CacheReused);
		Assert.AreEqual(first.StateKey, second.StateKey);
		Assert.AreEqual(2, f.History.Count);
	}

	[TestMethod]
	public async Task 캐시를_꺼도_상태_키는_만든다()
	{
		Fakes f = new Fakes();
		f.Config.UseCache = false;
		f.Build = (_, _) => Task.FromResult(new BuildProcessResult(6, false, false, 0));
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Fail", run.Result);
		Assert.AreEqual(1, run.ExitCode);
		Assert.AreNotEqual("", run.StateKey);
		Assert.AreEqual(0, f.Cache.Count);
	}

	[TestMethod]
	public async Task 빌드_중_변경되면_통과로_인정하지_않는다()
	{
		Fakes f = new Fakes();
		f.Build = (_, _) =>
		{
			// 같은 내용으로 되돌렸지만 수정 시각이 바뀐 경우 (A → B → A)
			f.Stamps[Hero] = 2;
			return Task.FromResult(new BuildProcessResult(0, false, false, 0));
		};
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Error", run.Result);
		Assert.AreEqual(nameof(ErrorKind.ChangedDuringValidation), run.ErrorKind);
		Assert.AreEqual(2, run.ExitCode);
		Assert.AreEqual("", run.StateKey);
		Assert.AreEqual(0, f.Cache.Count);
	}

	[TestMethod]
	public async Task 빌드_중_변경되고_실패하면_실패를_유지하되_상태와_연결하지_않는다()
	{
		Fakes f = new Fakes();
		f.Build = (_, _) =>
		{
			f.Contents[Hero] = "changed";
			return Task.FromResult(new BuildProcessResult(6, false, false, 0));
		};
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Fail", run.Result);
		Assert.AreEqual("", run.StateKey);
		Assert.IsTrue(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.ChangedDuringValidation));
	}

	[TestMethod]
	public async Task 잠금_대기_후_상태를_다시_평가한다()
	{
		Fakes f = new Fakes();
		f.Config.UncommittedSourceChanges = "Fail";
		f.LockWaits = true;
		// 잠금 전: Hero 만 수정 / 대기 중 Other 도 수정됨 → 엄격 정책 차단
		f.States.Enqueue(f.Modified(Hero));
		f.OnLockAcquired = () => f.States.Enqueue(f.Modified(Hero, Other));
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Fail", run.Result);
		Assert.AreEqual(nameof(ErrorKind.PolicyBlocked), run.ErrorKind);
		Assert.AreEqual(0, f.Builds.Count);
	}

	[TestMethod]
	public async Task 잠금_대기_중_다른_실행이_통과시켰으면_캐시()
	{
		Fakes f = new Fakes();
		f.LockWaits = true;
		RunResult reference = await new Fakes().ValidateAsync(Hero);
		f.OnLockAcquired = () => f.Cache.Add((reference.StateKey!, new[] { Hero }));
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Cached", run.Result);
		Assert.AreEqual(0, f.Builds.Count);
	}

	[TestMethod]
	public async Task SVN_상태를_모르면_엄격_정책은_오류()
	{
		Fakes f = new Fakes();
		f.Config.UncommittedSourceChanges = "Fail";
		f.States.Enqueue(SourceControlState.Unknown(SourceControlStatus.NotWorkingCopy, "E155007"));
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Error", run.Result);
		Assert.AreEqual(nameof(ErrorKind.SourceControlUnknown), run.ErrorKind);
		Assert.IsFalse(run.CommitAllowed);
	}

	[TestMethod]
	public async Task SVN_상태를_모르면_경고_정책은_빌드하되_캐시하지_않는다()
	{
		Fakes f = new Fakes();
		f.States.Enqueue(SourceControlState.Unknown(SourceControlStatus.ToolMissing, "no svn"));
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Pass", run.Result);
		Assert.AreEqual("", run.StateKey);
		Assert.AreEqual(0, f.Cache.Count);
		Assert.IsTrue(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.SvnToolMissing && x.Severity == DiagnosticSeverity.Warning));
	}

	[TestMethod]
	public async Task 캐시_저장_실패는_통과와_구분해_경고로_남긴다()
	{
		Fakes f = new Fakes { CacheWriteFails = true };
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Pass", run.Result);
		Assert.IsTrue(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.CacheWriteFailed));
	}

	[TestMethod]
	public async Task 알_수_없는_예외는_오류_결과로_바뀐다()
	{
		Fakes f = new Fakes { SvnThrows = new InvalidOperationException("boom") };
		RunResult run = await f.ValidateAsync(Hero);
		Assert.AreEqual("Error", run.Result);
		Assert.AreEqual(nameof(ErrorKind.Unexpected), run.ErrorKind);
		StringAssert.Contains(run.Diagnostics!.Single().Detail, "boom");
		Assert.AreEqual(1, f.History.Count, "오류도 기록에 남긴다");
	}

	[TestMethod]
	public async Task 취소와_시간_초과는_커밋_허용이_아니다()
	{
		Fakes canceled = new Fakes { Build = (_, _) => Task.FromResult(new BuildProcessResult(-1, false, true, 0)) };
		RunResult run = await canceled.ValidateAsync(Hero);
		Assert.AreEqual("Canceled", run.Result);
		Assert.AreEqual(2, run.ExitCode);

		Fakes timedOut = new Fakes { Build = (_, _) => Task.FromResult(new BuildProcessResult(-1, true, false, 1)) };
		run = await timedOut.ValidateAsync(Hero);
		Assert.AreEqual("Error", run.Result);
		Assert.AreEqual(nameof(ErrorKind.Timeout), run.ErrorKind);
		Assert.IsTrue(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.LeftoverProcesses));
	}

	[TestMethod]
	public async Task 종료_코드가_우선이다()
	{
		// 종료 코드 0 + 오류처럼 보이는 줄 → 통과 (경고 진단만)
		Fakes passing = new Fakes();
		passing.Lines.Add(@"C:\x.cpp(1): error C2065: looks bad");
		RunResult run = await passing.ValidateAsync(Hero);
		Assert.AreEqual("Pass", run.Result);
		Assert.IsTrue(run.Diagnostics!.Any(x => x.Code == DiagnosticCodes.ErrorLineWithSuccess));

		// 종료 코드 != 0 + 해석 가능한 오류 줄 없음 → 실패 (성공으로 바꾸지 않는다)
		Fakes failing = new Fakes { Build = (_, _) => Task.FromResult(new BuildProcessResult(3, false, false, 0)) };
		failing.Lines.Add("something went wrong");
		run = await failing.ValidateAsync(Hero);
		Assert.AreEqual("Fail", run.Result);
		Assert.AreEqual("something went wrong", BuildOutputParser.FromRun(run).Single().Message);
	}

	[TestMethod]
	public async Task 우회_태그는_엔진_없이도_건너뛰고_기록한다()
	{
		Fakes f = new Fakes();
		RunResult run = await f.Service().ValidateAsync(new ValidationRequest { Selector = new ProjectSelector(null, null, null), Paths = new[] { Hero }, Message = "hotfix [SKIP-shipcheck] 서버 장애" }, null, CancellationToken.None);
		Assert.AreEqual("Skipped", run.Result);
		Assert.AreEqual(0, run.ExitCode);
		Assert.AreEqual(1, f.History.Count);
		Assert.AreEqual(0, f.Builds.Count);
	}

	[TestMethod]
	public async Task 경로_없음_영향_없음은_건너뛰되_기록하지_않는다()
	{
		Fakes f = new Fakes();
		RunResult none = await f.ValidateAsync();
		Assert.AreEqual("Skipped", none.Result);
		RunResult content = await f.ValidateAsync(@"D:\W\Game\Content\Map.umap");
		Assert.AreEqual("Skipped", content.Result);
		Assert.AreEqual(0, f.History.Count);
	}
}
