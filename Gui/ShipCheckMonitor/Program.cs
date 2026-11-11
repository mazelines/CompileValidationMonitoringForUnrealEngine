using System.Text;

namespace ShipCheckMonitor;

static class Program
{
	const string InstanceMutexName = @"Local\ShipCheckMonitor_SingleInstance";
	const string ShowEventName = @"Local\ShipCheckMonitor_Show";

	/// <summary>
	/// 사용법: ShipCheckMonitor.exe [--project &lt;.uproject|폴더&gt;] [--minimized]
	/// </summary>
	[STAThread]
	static void Main(string[] args)
	{
		using Mutex instanceMutex = new Mutex(true, InstanceMutexName, out bool createdNew);
		using EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
		if (!createdNew)
		{
			// 이미 실행 중이면 기존 창을 앞으로 가져온다
			showEvent.Set();
			return;
		}

		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		ApplicationConfiguration.Initialize();

		string? project = null;
		bool minimized = false;
		for (int i = 0; i < args.Length; i++)
		{
			if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
			{
				project = args[++i];
			}
			else if (args[i].Equals("--minimized", StringComparison.OrdinalIgnoreCase))
			{
				minimized = true;
			}
		}

		// 도구 폴더 = 설정 파일과 훅 스크립트 위치 (실행 파일 폴더, 또는 "도구\Bin\" 배치면 상위 폴더)
		string toolDir = ShipCheck.Infrastructure.ShipCheckHost.LocateToolDir();
		Application.Run(new MainForm(new MonitorEngine(toolDir), AppSettings.Load(), showEvent, project, minimized));
	}
}
