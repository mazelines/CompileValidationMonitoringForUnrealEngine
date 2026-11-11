namespace ShipCheck.Core;

/// <summary>GUI 상태 표시 (배지/트레이 색)</summary>
public enum Health
{
	Unknown,
	Running,
	NoChanges,
	Passed,
	NeedsValidation,
	Failed,
	Error,
}

/// <summary>화면 상태 판정. 상태 키는 캐시 사용 여부와 무관하므로 캐시를 꺼도 실패 표시가 유지된다.</summary>
public static class HealthEvaluator
{
	public static Health Evaluate(bool hasProject, bool running, string? queryError, QueryResult? query, IReadOnlyList<RunResult> history)
	{
		if (running)
		{
			return Health.Running;
		}
		if (!hasProject)
		{
			return Health.Unknown;
		}
		if (queryError != null)
		{
			return Health.Error;
		}
		if (query == null)
		{
			return Health.Unknown;
		}
		if (!query.SvnAvailable)
		{
			// 변경 사항을 알 수 없는 상태를 "변경 없음" 으로 보여 주지 않는다
			return LatestFailure(query, history) != null ? Health.Failed : Health.NeedsValidation;
		}
		if (!query.AffectingChanges.Any())
		{
			return Health.NoChanges;
		}
		if (query.UpToDate)
		{
			return Health.Passed;
		}
		return LatestFailure(query, history) != null ? Health.Failed : Health.NeedsValidation;
	}

	/// <summary>현재 워킹 카피 상태로 마지막에 실행한 검증이 실패였다면 그 기록 (history 는 최신순)</summary>
	public static RunResult? LatestFailure(QueryResult? query, IReadOnlyList<RunResult> history)
	{
		if (query?.StateKey is not { Length: > 0 } key)
		{
			return null;
		}
		RunResult? latest = history.FirstOrDefault(x => x.StateKey == key && x.Result is "Pass" or "Cached" or "Fail");
		return latest?.Result == "Fail" ? latest : null;
	}
}
