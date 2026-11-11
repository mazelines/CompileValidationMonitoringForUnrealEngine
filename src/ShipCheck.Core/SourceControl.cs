using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ShipCheck.Core;

/// <summary>SVN 상태를 알 수 있는지. Available 이 아니면 "변경 없음" 이 아니라 "모름" 이다.</summary>
public enum SourceControlStatus
{
	Available,
	ToolMissing,
	NotWorkingCopy,
	Failed,
}

/// <summary>svn status 항목 1개 (XML 의 entry/wc-status)</summary>
public sealed record SvnStatusEntry(string Path, string Item, string Props, string? Revision, string? CommittedRevision, bool Switched, bool TreeConflicted)
{
	/// <summary>기존 svn status 텍스트 출력의 첫 열 문자</summary>
	public string StatusLetter => Item switch
	{
		"added" => "A",
		"conflicted" => "C",
		"deleted" => "D",
		"ignored" => "I",
		"incomplete" => "!",
		"missing" => "!",
		"external" => "X",
		"modified" => "M",
		"obstructed" => "~",
		"replaced" => "R",
		"unversioned" => "?",
		_ => " ",
	};

	/// <summary>커밋 대상이 되는(또는 저장소와 다른) 변경</summary>
	public bool IsChange => StatusLetter is "A" or "M" or "R" or "D" or "C" or "!" or "~" or "?";
}

public sealed record SvnInfo(string WcRoot, string Url, string Revision);

/// <summary>범위 내 코드 파일의 svn 상태</summary>
public sealed class SourceControlState
{
	public SourceControlStatus Status { get; init; }
	/// <summary>Available 이 아닐 때의 원인 (svn stderr 등)</summary>
	public string? Error { get; init; }
	public string WcRoot { get; init; } = "";
	public string Revision { get; init; } = "";
	/// <summary>수정/추가/삭제/미등록 코드 파일 (미등록 폴더 안의 파일 포함)</summary>
	public IReadOnlyList<SourceChange> Changes { get; init; } = Array.Empty<SourceChange>();
	/// <summary>
	/// 모든 코드 파일의 "상태 + 속성 상태 + 작업 리비전 + 최종 변경 리비전" 지문.
	/// 부분 update / 개별 파일 커밋 / switch 로 루트 리비전은 그대로인데 깨끗한 파일의 기준이 바뀌는 경우를 잡는다.
	/// </summary>
	public string Baseline { get; init; } = "";

	public bool IsAvailable => Status == SourceControlStatus.Available;

	public static SourceControlState Unknown(SourceControlStatus status, string? error) => new SourceControlState { Status = status, Error = error };
}

public sealed record SourceChange(string Path, string Status);

public static class SvnXml
{
	/// <summary>svn status -v --xml 출력 해석. 경로는 svn 이 준 그대로(호출 시 절대 경로를 넘긴다).</summary>
	public static List<SvnStatusEntry> ParseStatus(string xml)
	{
		XDocument document = Load(xml);
		List<SvnStatusEntry> entries = new List<SvnStatusEntry>();
		foreach (XElement entry in document.Descendants("entry"))
		{
			XElement? status = entry.Element("wc-status");
			string? path = (string?)entry.Attribute("path");
			if (status == null || string.IsNullOrEmpty(path))
			{
				continue;
			}
			entries.Add(new SvnStatusEntry(
				path.Replace('/', '\\'),
				(string?)status.Attribute("item") ?? "",
				(string?)status.Attribute("props") ?? "",
				(string?)status.Attribute("revision"),
				(string?)status.Element("commit")?.Attribute("revision"),
				(string?)status.Attribute("switched") == "true",
				(string?)status.Attribute("tree-conflicted") == "true"));
		}
		return entries;
	}

	/// <summary>svn info --xml 출력 해석. 워킹 카피 루트가 없으면 null (워킹 카피가 아님).</summary>
	public static SvnInfo? ParseInfo(string xml)
	{
		XElement? entry = Load(xml).Descendants("entry").FirstOrDefault();
		string? wcRoot = entry?.Element("wc-info")?.Element("wcroot-abspath")?.Value;
		if (entry == null || string.IsNullOrWhiteSpace(wcRoot))
		{
			return null;
		}
		return new SvnInfo(wcRoot.Replace('/', '\\'), entry.Element("url")?.Value ?? "", (string?)entry.Attribute("revision") ?? "");
	}

	static XDocument Load(string xml)
	{
		try
		{
			return XDocument.Parse(xml);
		}
		catch (XmlException ex)
		{
			throw new FormatException("svn XML 출력을 해석할 수 없습니다: " + ex.Message, ex);
		}
	}

	/// <summary>기준 지문 한 줄</summary>
	public static string BaselineLine(SvnStatusEntry entry) =>
		$"{entry.Path}|{entry.Item}|{entry.Props}|{entry.Revision}|{entry.CommittedRevision}|{(entry.Switched ? "S" : "")}{(entry.TreeConflicted ? "T" : "")}";

	public static string BuildBaseline(string url, IEnumerable<string> lines)
	{
		StringBuilder text = new StringBuilder(url).Append('\n');
		foreach (string line in lines.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
		{
			text.Append(line).Append('\n');
		}
		return text.ToString();
	}
}
