using System.Diagnostics;
using System.Text;
using System.Text.Json;

// 모의 UBT. Build.bat 이 SHIPCHECK_FAKE_DIR 를 설정하고 이 프로그램에 UBT 인수를 그대로 넘긴다.
// 동작은 같은 폴더의 fakebuild.json 으로 제어하고, 받은 인수는 calls.jsonl 에 한 줄씩 남긴다.
//   --sleep-child         : 자식 모드 (손자 1개를 만들고 오래 대기) — 취소 테스트용
//   --hold-mutex <이름>   : 뮤텍스를 잡은 채 해제하지 않고 종료 — 비정상 종료 후 잠금 복구 테스트용

if (args.Length >= 1 && args[0] == "--sleep-child")
{
	string? pidFile = args.Length > 1 ? args[1] : null;
	if (pidFile != null)
	{
		using Process grandchild = StartSelf("--sleep-leaf");
		AppendLine(pidFile, grandchild.Id.ToString());
	}
	Thread.Sleep(TimeSpan.FromMinutes(5));
	return 0;
}
if (args.Length >= 1 && args[0] == "--sleep-leaf")
{
	Thread.Sleep(TimeSpan.FromMinutes(5));
	return 0;
}
if (args.Length >= 2 && args[0] == "--hold-mutex")
{
	Mutex mutex = new Mutex(false, args[1]);
	mutex.WaitOne();
	Console.WriteLine("held");
	// ReleaseMutex 없이 종료 → 다음 소유자는 AbandonedMutexException 을 받는다
	Environment.Exit(0);
}

string dir = Environment.GetEnvironmentVariable("SHIPCHECK_FAKE_DIR") ?? AppContext.BaseDirectory;
FakeControl control = File.Exists(Path.Combine(dir, "fakebuild.json"))
	? JsonSerializer.Deserialize<FakeControl>(File.ReadAllText(Path.Combine(dir, "fakebuild.json"))) ?? new FakeControl()
	: new FakeControl();
AppendLine(Path.Combine(dir, "calls.jsonl"), JsonSerializer.Serialize(args));
Console.WriteLine("FakeUBT " + string.Join(" ", args));
foreach (string line in control.OutputLines)
{
	Console.WriteLine(line);
}

if (control.PidFile != null)
{
	AppendLine(control.PidFile, Environment.ProcessId.ToString());
}
if (control.SpawnChildren)
{
	using Process child = StartSelf("--sleep-child", control.PidFile ?? "");
	AppendLine(control.PidFile!, child.Id.ToString());
}
if (control.LingerChild)
{
	// 출력 파이프를 물려받은 채 남는 백그라운드 프로세스 (mspdbsrv 같은 경우)
	using Process lingering = StartSelf("--sleep-leaf");
	if (control.PidFile != null)
	{
		AppendLine(control.PidFile, lingering.Id.ToString());
	}
}
if (control.StartedEvent != null)
{
	using EventWaitHandle started = EventWaitHandle.OpenExisting(control.StartedEvent);
	started.Set();
}
if (control.ReleaseEvent != null)
{
	using EventWaitHandle release = EventWaitHandle.OpenExisting(control.ReleaseEvent);
	release.WaitOne(TimeSpan.FromMinutes(5));
}
if (control.SpawnChildren)
{
	Thread.Sleep(TimeSpan.FromMinutes(5));
}

if (control.Fail)
{
	Console.WriteLine(@"C:\Proj\Source\Game\Private\Hero.cpp(42): error C2065: 'GEditor': undeclared identifier");
	return 6;
}
string? singleFile = args.FirstOrDefault(x => x.StartsWith("-SingleFile=", StringComparison.Ordinal) && control.FailSingleFilePattern != null && x.Contains(control.FailSingleFilePattern, StringComparison.OrdinalIgnoreCase));
if (singleFile != null)
{
	Console.WriteLine($"{singleFile["-SingleFile=".Length..]}(1): fatal error C1083: Cannot open include file: 'Missing.h': No such file or directory");
	return 6;
}
if (control.FailIfSourceContains != null && control.SourceRoot != null)
{
	foreach (string file in Directory.EnumerateFiles(control.SourceRoot, "*.*", SearchOption.AllDirectories))
	{
		if (File.ReadAllText(file).Contains(control.FailIfSourceContains, StringComparison.Ordinal))
		{
			Console.WriteLine($"{file}(1): error C2065: fixture broken");
			return 1;
		}
	}
}
return control.ExitCode;

static Process StartSelf(params string[] arguments)
{
	ProcessStartInfo startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
	foreach (string argument in arguments)
	{
		startInfo.ArgumentList.Add(argument);
	}
	return Process.Start(startInfo)!;
}

static void AppendLine(string path, string line)
{
	for (int attempt = 0; ; attempt++)
	{
		try
		{
			File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
			return;
		}
		catch (IOException) when (attempt < 50)
		{
			Thread.Sleep(20);
		}
	}
}

sealed class FakeControl
{
	public bool Fail { get; set; }
	public string? FailSingleFilePattern { get; set; }
	public string? FailIfSourceContains { get; set; }
	public string? SourceRoot { get; set; }
	public string? StartedEvent { get; set; }
	public string? ReleaseEvent { get; set; }
	public bool SpawnChildren { get; set; }
	public bool LingerChild { get; set; }
	public string? PidFile { get; set; }
	public List<string> OutputLines { get; set; } = new();
	public int ExitCode { get; set; }
}
