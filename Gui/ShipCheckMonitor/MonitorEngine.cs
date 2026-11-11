using ShipCheck.Core;
using ShipCheck.Infrastructure;

namespace ShipCheckMonitor;

/// <summary>
/// GUI 와 공통 검증 엔진(ShipCheck.Core) 사이의 얇은 연결. 커밋 훅/명령줄과 같은 ValidationService 를 직접 호출한다.
/// </summary>
sealed class MonitorEngine
{
	static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);

	readonly ValidationService _service;

	public string ToolDir { get; }

	public MonitorEngine(string toolDir)
	{
		ToolDir = toolDir;
		_service = ShipCheckHost.CreateService(toolDir);
	}

	/// <summary>빌드 없이 프로젝트/엔진/변경 사항/검증 상태를 조회한다. 실패하면 사용자에게 보여 줄 메시지와 함께 예외.</summary>
	public async Task<QueryResult> QueryAsync(string project, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(QueryTimeout);
		try
		{
			// 디스크/svn 작업이 UI 스레드를 막지 않도록 작업 스레드에서 실행
			return await Task.Run(() => _service.QueryAsync(new QueryRequest { Selector = new ProjectSelector(project, null, null) }, timeout.Token), timeout.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException("상태 조회가 2분 안에 끝나지 않았습니다.");
		}
	}

	/// <summary>검증을 시작한다. 진행 이벤트는 작업 스레드에서 onEvent 로 전달된다.</summary>
	public ValidationRun StartValidation(string project, IReadOnlyCollection<string> paths, bool all, bool force, string trigger, Action<RunEvent> onEvent)
	{
		ValidationRequest request = new ValidationRequest
		{
			Selector = new ProjectSelector(project, null, null),
			Paths = all ? Array.Empty<string>() : paths.ToList(),
			All = all,
			Force = force,
			Trigger = trigger,
		};
		return new ValidationRun(project, trigger, all, cancellationToken => Task.Run(() => _service.ValidateAsync(request, onEvent, cancellationToken)));
	}

	public static List<RunResult> LoadHistory(string projectDir) => FileRunStore.ReadHistory(projectDir);

	public HookStatus HookStatus(string project) => TortoiseHooks.GetStatus(ToolDir, project);

	public string HookCommandLine(string project) => TortoiseHooks.GetCommandLine(ToolDir, project);
}

/// <summary>
/// 실행 중인 검증 1건. 프로젝트 경로를 함께 보관하여 실행 중 프로젝트 전환 시 결과가 섞이지 않게 한다.
/// 취소하면 엔진이 UBT/컴파일러까지 종료하고 Canceled 결과를 돌려준다.
/// </summary>
sealed class ValidationRun
{
	static int _nextId;
	readonly CancellationTokenSource _cancel = new CancellationTokenSource();

	public int Id { get; } = Interlocked.Increment(ref _nextId);
	public string Project { get; }
	public string Trigger { get; }
	public bool All { get; }
	public DateTime StartedUtc { get; } = DateTime.UtcNow;
	public Task<RunResult> Completion { get; }
	public bool Canceled => _cancel.IsCancellationRequested;

	public ValidationRun(string project, string trigger, bool all, Func<CancellationToken, Task<RunResult>> start)
	{
		Project = project;
		Trigger = trigger;
		All = all;
		Completion = start(_cancel.Token);
	}

	public void Cancel() => _cancel.Cancel();
}
