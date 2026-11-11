using System.Security.Cryptography;
using System.Text;

namespace ShipCheck.Core;

/// <summary>
/// 워킹 카피 상태 키: 스키마 버전, 엔진, svn 기준 지문, 타깃, 검증 정책, 수정된 코드 파일 내용이 같으면 같은 키.
/// 캐시 사용 여부와 무관하게 계산한다 (GUI 가 현재 상태의 실패 기록을 찾는 데 쓴다).
/// </summary>
public static class StateKey
{
	public const int SchemaVersion = 2;

	/// <param name="fileHashes">경로 → 내용 해시 (삭제된 파일은 "deleted")</param>
	public static string Compute(string engineRoot, string baseline, IEnumerable<BuildTarget> targets, ValidationPolicy policy, IEnumerable<KeyValuePair<string, string>> fileHashes)
	{
		StringBuilder text = new StringBuilder();
		text.Append("ShipCheckStateKey ").Append(SchemaVersion).Append('\n');
		text.Append(engineRoot).Append('\n');
		text.Append(baseline).Append('\n');
		text.Append(string.Join(";", targets.Select(x => $"{x.Name}|{x.Platform}|{x.Configuration}"))).Append('\n');
		text.Append(policy.ToKeyText());
		foreach (KeyValuePair<string, string> file in fileHashes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
		{
			text.Append(file.Key).Append('|').Append(file.Value).Append('\n');
		}
		return Sha1Hex(text.ToString());
	}

	public static string Sha1Hex(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// PassCache.txt: 첫 줄 머리말 + 한 줄 = "상태 키\t단독 컴파일로 검증한 파일|...".
/// 같은 상태 키로 통과한 기록들이 이번에 단독 컴파일해야 할 파일을 모두 검증했을 때만 적중한다.
/// 머리말이 없거나 다른 버전이면(구 형식/손상) 빈 캐시로 본다. 손상된 캐시를 통과 근거로 쓰지 않는다.
/// </summary>
public static class PassCache
{
	public const string Header = "#ShipCheckPassCache 2";
	public const int MaxEntries = 50;

	public sealed record Entry(string Key, IReadOnlyList<string> VerifiedFiles);

	public static List<Entry> Parse(IReadOnlyList<string> lines)
	{
		List<Entry> entries = new List<Entry>();
		if (lines.Count == 0 || lines[0].Trim() != Header)
		{
			return entries;
		}
		foreach (string line in lines.Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}
			string[] parts = line.Split('\t', 2);
			if (parts[0].Length != 40 || !parts[0].All(Uri.IsHexDigit))
			{
				// 손상된 줄은 무시 (적중 근거로 쓰지 않는다)
				continue;
			}
			string[] files = parts.Length > 1 && parts[1].Length > 0 ? parts[1].Split('|') : Array.Empty<string>();
			entries.Add(new Entry(parts[0], files));
		}
		return entries;
	}

	public static bool IsHit(IEnumerable<Entry> entries, string key, IEnumerable<string> requiredFiles)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		HashSet<string> verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		bool found = false;
		foreach (Entry entry in entries.Where(x => x.Key == key))
		{
			found = true;
			verified.UnionWith(entry.VerifiedFiles);
		}
		return found && requiredFiles.All(verified.Contains);
	}

	/// <summary>새 통과 기록을 추가한 전체 파일 내용 (최근 MaxEntries 개 유지)</summary>
	public static string Append(IEnumerable<Entry> existing, string key, IEnumerable<string> verifiedFiles)
	{
		List<Entry> entries = existing.ToList();
		entries.Add(new Entry(key, verifiedFiles.ToList()));
		StringBuilder text = new StringBuilder(Header).Append("\r\n");
		foreach (Entry entry in entries.Skip(Math.Max(0, entries.Count - MaxEntries)))
		{
			text.Append(entry.Key).Append('\t').Append(string.Join("|", entry.VerifiedFiles)).Append("\r\n");
		}
		return text.ToString();
	}
}
