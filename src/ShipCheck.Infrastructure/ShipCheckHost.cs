using ShipCheck.Core;

namespace ShipCheck.Infrastructure;

/// <summary>실제 Windows 환경용 서비스 구성 (GUI/CLI 공통 진입점)</summary>
public static class ShipCheckHost
{
	/// <summary>
	/// 도구 폴더 = 설정 파일(ShipCheck.config.json)과 훅 스크립트가 있는 곳.
	/// 실행 파일 폴더에 없고 상위 폴더에 있으면 상위 폴더 (기존 "도구\Bin\실행 파일" 배치 호환).
	/// </summary>
	public static string LocateToolDir(string? baseDir = null)
	{
		string start = PathUtil.Normalize(baseDir ?? AppContext.BaseDirectory);
		string? parent = Path.GetDirectoryName(start);
		if (!File.Exists(Path.Combine(start, ShipCheckConfig.DefaultFileName)) && parent != null && File.Exists(Path.Combine(parent, ShipCheckConfig.DefaultFileName)))
		{
			return parent;
		}
		return start;
	}

	public static ShipCheckServices CreateServices(string toolDir, string? svnPath = null, bool locateSvn = true)
	{
		LocalFileSystem files = new LocalFileSystem();
		return new ShipCheckServices
		{
			Locator = new ProjectLocator(toolDir),
			SourceControl = new SvnClient(svnPath ?? (locateSvn ? SvnClient.Locate() : null), files),
			Modules = new ModuleResolver(),
			Files = files,
			Builds = new BuildRunner(),
			Locks = new NamedMutexLockProvider(),
			Store = new FileRunStore(),
			Clock = new SystemClock(),
		};
	}

	public static ValidationService CreateService(string toolDir) => new ValidationService(CreateServices(toolDir));
}
