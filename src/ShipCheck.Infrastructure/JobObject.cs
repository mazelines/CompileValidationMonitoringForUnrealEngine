using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShipCheck.Infrastructure;

/// <summary>
/// Windows Job Object. 넣은 프로세스와 그 모든 자손(UBT, cl.exe, link.exe ...)을 한 번에 종료하고,
/// 남은 프로세스가 없는지 확인할 수 있다. 핸들이 닫히면(GUI 비정상 종료 포함) 남은 프로세스도 종료된다.
/// BREAKAWAY_OK 를 주지 않으므로 자손이 Job 을 빠져나갈 수 없다 (UBT/UBA 는 breakaway 를 요청하지 않음).
/// </summary>
public sealed class JobObject : IDisposable
{
	const int JobObjectBasicAccountingInformation = 1;
	const int JobObjectExtendedLimitInformation = 9;
	const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

	[StructLayout(LayoutKind.Sequential)]
	struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
	{
		public long TotalUserTime;
		public long TotalKernelTime;
		public long ThisPeriodTotalUserTime;
		public long ThisPeriodTotalKernelTime;
		public uint TotalPageFaultCount;
		public uint TotalProcesses;
		public uint ActiveProcesses;
		public uint TotalTerminatedProcesses;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, IntPtr returnLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

	readonly SafeFileHandle _handle;

	public JobObject()
	{
		_handle = CreateJobObject(IntPtr.Zero, null);
		if (_handle.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Job Object 를 만들 수 없습니다.");
		}
		JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
		info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
		if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
		{
			int error = Marshal.GetLastWin32Error();
			_handle.Dispose();
			throw new Win32Exception(error, "Job Object 설정 실패");
		}
	}

	public void Assign(Process process)
	{
		if (!AssignProcessToJobObject(_handle, process.Handle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "프로세스를 Job Object 에 넣을 수 없습니다.");
		}
	}

	/// <summary>Job 안에서 아직 살아 있는 프로세스 수</summary>
	public int ActiveProcessCount
	{
		get
		{
			if (_handle.IsClosed)
			{
				return 0;
			}
			if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			return (int)info.ActiveProcesses;
		}
	}

	/// <summary>
	/// Job 의 모든 프로세스를 종료하고, 실제로 0개가 될 때까지 기다린다. 남은 프로세스 수를 반환 (0 이면 성공).
	/// </summary>
	public int TerminateAndWait(TimeSpan timeout)
	{
		if (_handle.IsClosed)
		{
			return 0;
		}
		TerminateJobObject(_handle, 1);
		Stopwatch timer = Stopwatch.StartNew();
		int remaining = ActiveProcessCount;
		while (remaining > 0 && timer.Elapsed < timeout)
		{
			Thread.Sleep(50);
			// 종료 중에 새로 생긴 프로세스가 있어도 다시 종료한다
			TerminateJobObject(_handle, 1);
			remaining = ActiveProcessCount;
		}
		return remaining;
	}

	public void Dispose() => _handle.Dispose();
}
