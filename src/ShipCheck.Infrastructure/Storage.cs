using System.Text;
using ShipCheck.Core;

namespace ShipCheck.Infrastructure;

/// <summary>
/// 프로젝트 잠금: Local\ShipCheck_&lt;sha1(소문자 .uproject 경로)&gt; (기존 스크립트와 같은 이름).
/// Mutex 는 스레드 소유이므로 전용 스레드가 잡고 있다가 Dispose 때 놓는다 (async 흐름에서도 안전).
/// </summary>
public sealed class NamedMutexLockProvider : ILockProvider
{
	public static string ProjectLockName(string uproject, string suffix = "") => @"Local\ShipCheck_" + suffix + StateKey.Sha1Hex(uproject.ToLowerInvariant());

	public Task<(IProjectLock Lock, bool Waited)> AcquireAsync(string uproject, Action onWaiting, CancellationToken cancellationToken) =>
		MutexLock.AcquireAsync(ProjectLockName(uproject), onWaiting, cancellationToken);

	sealed class MutexLock : IProjectLock
	{
		readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
		readonly Thread _thread;
		int _disposed;

		MutexLock(Thread thread) => _thread = thread;

		public static Task<(IProjectLock, bool)> AcquireAsync(string name, Action onWaiting, CancellationToken cancellationToken)
		{
			TaskCompletionSource<(IProjectLock, bool)> acquired = new TaskCompletionSource<(IProjectLock, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
			MutexLock? holder = null;
			Thread thread = new Thread(() =>
			{
				using Mutex mutex = new Mutex(false, name);
				bool waited = false;
				bool owned;
				try
				{
					owned = mutex.WaitOne(0);
					if (!owned)
					{
						waited = true;
						onWaiting();
						// 취소되면 대기를 포기한다
						owned = WaitHandle.WaitAny(new[] { mutex, cancellationToken.WaitHandle }) == 0;
					}
				}
				catch (AbandonedMutexException)
				{
					// 이전 소유자가 비정상 종료 → 잠금을 넘겨받는다
					owned = true;
				}
				if (!owned)
				{
					acquired.TrySetCanceled(cancellationToken);
					return;
				}
				acquired.TrySetResult((holder!, waited));
				holder!._release.Wait();
				mutex.ReleaseMutex();
			})
			{ IsBackground = true, Name = "ShipCheck project lock" };
			holder = new MutexLock(thread);
			thread.Start();
			return acquired.Task;
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				_release.Set();
				_thread.Join(TimeSpan.FromSeconds(5));
				_release.Dispose();
			}
		}
	}
}

/// <summary>Saved/ShipCheck 아래 상태 파일 (PassCache.txt, History.jsonl, LastResult.txt, Logs)</summary>
public sealed class FileRunStore : IRunStore
{
	public const string PassCacheFile = "PassCache.txt";
	public const string HistoryFile = "History.jsonl";
	public const string LastResultFile = "LastResult.txt";

	public IReadOnlyList<PassCache.Entry> ReadPassCache(string stateDir)
	{
		string path = Path.Combine(stateDir, PassCacheFile);
		try
		{
			return File.Exists(path) ? PassCache.Parse(ReadAllLinesShared(path)) : Array.Empty<PassCache.Entry>();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 읽을 수 없는 캐시는 통과 근거로 쓰지 않는다
			return Array.Empty<PassCache.Entry>();
		}
	}

	public void AddPassCache(string stateDir, string key, IReadOnlyList<string> verifiedFiles)
	{
		Directory.CreateDirectory(stateDir);
		WriteAtomic(Path.Combine(stateDir, PassCacheFile), PassCache.Append(ReadPassCache(stateDir), key, verifiedFiles));
	}

	public void AppendHistory(string uproject, string stateDir, RunResult run)
	{
		Directory.CreateDirectory(stateDir);
		string line = ShipCheckJson.Serialize(run, compact: true) + "\n";
		// 훅/GUI 가 동시에 끝나도 줄이 섞이지 않도록 기록 전용 뮤텍스로 직렬화 (기존 스크립트와 같은 이름)
		using Mutex mutex = new Mutex(false, NamedMutexLockProvider.ProjectLockName(uproject, "History_"));
		bool owned;
		try
		{
			owned = mutex.WaitOne(TimeSpan.FromSeconds(30));
		}
		catch (AbandonedMutexException)
		{
			owned = true;
		}
		if (!owned)
		{
			throw new IOException("실행 기록 잠금을 30초 안에 얻지 못했습니다.");
		}
		try
		{
			File.AppendAllText(Path.Combine(stateDir, HistoryFile), line, new UTF8Encoding(false));
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	public void WriteLastResult(string stateDir, string text)
	{
		Directory.CreateDirectory(stateDir);
		WriteAtomic(Path.Combine(stateDir, LastResultFile), text, new UTF8Encoding(true));
	}

	public string PrepareLogDirectory(string stateDir)
	{
		string dir = Path.Combine(stateDir, "Logs");
		Directory.CreateDirectory(dir);
		return dir;
	}

	/// <summary>프로젝트의 실행 기록 (최신순). 쓰는 중인 마지막 줄/손상된 줄은 건너뛴다.</summary>
	public static List<RunResult> ReadHistory(string projectDir, int maxEntries = 300)
	{
		string path = Path.Combine(projectDir, "Saved", "ShipCheck", HistoryFile);
		List<RunResult> results = new List<RunResult>();
		if (!File.Exists(path))
		{
			return results;
		}
		List<string> lines;
		try
		{
			lines = ReadAllLinesShared(path).Where(x => x.Trim().Length > 0).ToList();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return results;
		}
		foreach (string line in lines.Skip(Math.Max(0, lines.Count - maxEntries)))
		{
			try
			{
				RunResult? result = ShipCheckJson.Deserialize<RunResult>(line);
				if (result != null)
				{
					results.Add(result);
				}
			}
			catch (System.Text.Json.JsonException)
			{
			}
		}
		results.Reverse();
		return results;
	}

	public static void ClearPassCache(string projectDir)
	{
		string path = Path.Combine(projectDir, "Saved", "ShipCheck", PassCacheFile);
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	/// <summary>훅이 쓰는 중일 수 있으므로 공유 모드로 읽는다</summary>
	static List<string> ReadAllLinesShared(string path)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
		return reader.ReadToEnd().Replace("\r", "").Split('\n').ToList();
	}

	/// <summary>임시 파일에 쓴 뒤 교체하여, 읽는 쪽이 반쯤 쓰인 파일을 보지 않게 한다</summary>
	public static void WriteAtomic(string path, string text, Encoding? encoding = null)
	{
		string temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllText(temp, text, encoding ?? new UTF8Encoding(false));
			if (File.Exists(path))
			{
				File.Replace(temp, path, null);
			}
			else
			{
				File.Move(temp, path);
			}
		}
		finally
		{
			if (File.Exists(temp))
			{
				File.Delete(temp);
			}
		}
	}
}

public sealed class SystemClock : IClock
{
	public DateTimeOffset Now => DateTimeOffset.Now;
}
