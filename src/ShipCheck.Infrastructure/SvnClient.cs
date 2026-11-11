using System.ComponentModel;
using System.Text;
using ShipCheck.Core;

namespace ShipCheck.Infrastructure;

/// <summary>
/// svn.exe 로 워킹 카피 상태를 읽는다 (구조화된 --xml 출력). 공백/한글 경로는 ArgumentList 와 XML 로 보존된다.
/// 상태를 알 수 없으면 예외 대신 원인과 함께 Unknown 상태를 돌려준다.
/// </summary>
public sealed class SvnClient : ISourceControl
{
	static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

	readonly string? _svn;
	readonly IFileSystem _files;

	/// <param name="svnPath">svn.exe 경로. null 이면 svn 이 없는 환경으로 동작</param>
	public SvnClient(string? svnPath, IFileSystem files)
	{
		_svn = svnPath;
		_files = files;
	}

	/// <summary>PATH 의 svn.exe, 없으면 TortoiseSVN 명령줄 도구</summary>
	public static string? Locate()
	{
		foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			try
			{
				string candidate = Path.Combine(dir.Trim().Trim('"'), "svn.exe");
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}
			catch (ArgumentException)
			{
			}
		}
		string tortoise = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TortoiseSVN", "bin", "svn.exe");
		return File.Exists(tortoise) ? tortoise : null;
	}

	public async Task<SourceControlState> GetStateAsync(string projectDir, IReadOnlyList<string> scope, CancellationToken cancellationToken)
	{
		if (_svn == null)
		{
			return SourceControlState.Unknown(SourceControlStatus.ToolMissing, "svn.exe 를 PATH 에서 찾을 수 없습니다 (TortoiseSVN 설치 시 'command line client tools' 선택 필요).");
		}

		// svn export 폴더 등에서도 svn status 는 경고만 내고 0 을 반환하므로 info 로 워킹 카피인지 먼저 확인한다
		ProcessOutput info;
		try
		{
			info = await RunAsync(new[] { "info", "--xml", "--non-interactive", projectDir }, cancellationToken);
		}
		catch (Exception ex) when (ex is Win32Exception or TimeoutException)
		{
			return SourceControlState.Unknown(SourceControlStatus.Failed, ex.Message);
		}
		if (info.ExitCode != 0)
		{
			bool notWorkingCopy = info.StandardError.Contains("E155007", StringComparison.Ordinal) || info.StandardError.Contains("W155007", StringComparison.Ordinal);
			return SourceControlState.Unknown(notWorkingCopy ? SourceControlStatus.NotWorkingCopy : SourceControlStatus.Failed, info.StandardError.Trim());
		}
		SvnInfo? wc;
		try
		{
			wc = SvnXml.ParseInfo(info.StandardOutput);
		}
		catch (FormatException ex)
		{
			return SourceControlState.Unknown(SourceControlStatus.Failed, ex.Message);
		}
		if (wc == null)
		{
			return SourceControlState.Unknown(SourceControlStatus.NotWorkingCopy, info.StandardError.Trim());
		}

		List<string> existing = scope.Where(x => _files.FileExists(x) || _files.DirectoryExists(x)).ToList();
		if (existing.Count == 0)
		{
			return new SourceControlState { Status = SourceControlStatus.Available, WcRoot = wc.WcRoot, Revision = wc.Revision, Baseline = SvnXml.BuildBaseline(wc.Url, Array.Empty<string>()) };
		}

		ProcessOutput status;
		try
		{
			status = await RunAsync(new[] { "status", "-v", "--xml", "--non-interactive" }.Concat(existing), cancellationToken);
		}
		catch (Exception ex) when (ex is Win32Exception or TimeoutException)
		{
			return SourceControlState.Unknown(SourceControlStatus.Failed, ex.Message);
		}
		// 경고(W...)만 있어도 일부 경로의 상태를 모르는 것이므로 "상태 불명" 으로 본다
		if (status.ExitCode != 0 || status.StandardError.Trim().Length > 0)
		{
			return SourceControlState.Unknown(SourceControlStatus.Failed, (status.StandardError.Trim().Length > 0 ? status.StandardError : $"svn status 종료코드 {status.ExitCode}").Trim());
		}

		List<SvnStatusEntry> entries;
		try
		{
			entries = SvnXml.ParseStatus(status.StandardOutput);
		}
		catch (FormatException ex)
		{
			return SourceControlState.Unknown(SourceControlStatus.Failed, ex.Message);
		}

		List<SourceChange> changes = new List<SourceChange>();
		List<string> baseline = new List<string>();
		foreach (SvnStatusEntry entry in entries)
		{
			string path = PathUtil.Normalize(entry.Path);
			// 미등록 빌드 산출물 폴더는 무시
			if (FileClassifier.IsGeneratedPath(path, projectDir) || entry.Item == "external")
			{
				continue;
			}
			// 미등록 폴더는 "? 폴더" 항목 하나로만 나오므로 안의 코드 파일을 직접 찾는다
			if (entry.Item == "unversioned" && _files.DirectoryExists(path))
			{
				foreach (string file in _files.EnumerateCodeFiles(path))
				{
					baseline.Add($"{file}|unversioned-file||||");
					changes.Add(new SourceChange(file, "?"));
				}
				continue;
			}
			if (!FileClassifier.IsCode(path))
			{
				continue;
			}
			baseline.Add(SvnXml.BaselineLine(entry with { Path = path }));
			if (entry.IsChange)
			{
				changes.Add(new SourceChange(path, entry.StatusLetter));
			}
		}
		return new SourceControlState
		{
			Status = SourceControlStatus.Available,
			WcRoot = wc.WcRoot,
			Revision = wc.Revision,
			Changes = changes,
			Baseline = SvnXml.BuildBaseline(wc.Url, baseline),
		};
	}

	Task<ProcessOutput> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
		// --xml 출력은 항상 UTF-8, 오류 메시지는 콘솔 코드 페이지
		ProcessRunner.RunAsync(_svn!, arguments, new UTF8Encoding(false), CommandTimeout, cancellationToken, errorEncoding: ProcessRunner.ConsoleEncoding);
}
