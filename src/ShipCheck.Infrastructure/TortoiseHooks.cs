using Microsoft.Win32;
using ShipCheck.Core;

namespace ShipCheck.Infrastructure;

/// <summary>값이 작을수록 우선 (Applied 가 하나라도 있으면 적용됨)</summary>
public enum HookState
{
	Applied,
	Misconfigured,
	Unverified,
	NotRegistered,
}

public sealed record HookStatus(HookState State, string Detail)
{
	public bool BlocksCommits => State == HookState.Applied;

	public string DisplayText => State switch
	{
		HookState.Applied => "적용됨" + (Detail.Length > 0 ? $" ({Detail})" : ""),
		HookState.Misconfigured => $"설정 문제 — {Detail}",
		HookState.Unverified => $"등록 발견 — 적용 여부 미확인 ({Detail})",
		_ => "미등록 — 클릭하여 등록 명령 복사",
	};
}

/// <summary>TortoiseSVN 클라이언트 훅 등록 확인 (HKCU\Software\TortoiseSVN\hooks 읽기 전용)</summary>
public static class TortoiseHooks
{
	public const string HookScriptName = "TortoiseSVN-PreCommit.cmd";

	static readonly HashSet<string> HookTypeNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"start_commit_hook", "check_commit_hook", "pre_commit_hook", "manual_precommit", "post_commit_hook",
		"start_update_hook", "pre_update_hook", "post_update_hook", "pre_connect_hook",
		"pre_lock_hook", "post_lock_hook", "pre_unlock_hook", "post_unlock_hook",
	};

	public static HookStatus GetStatus(string toolDir, string uproject) =>
		ParseStatus(Registry.GetValue(@"HKEY_CURRENT_USER\Software\TortoiseSVN", "hooks", null) as string, toolDir, uproject);

	/// <summary>
	/// 이 프로젝트에 ShipCheck 커밋 훅이 실제로 적용되는지 확인한다.
	/// TortoiseSVN 은 훅마다 "종류\n워킹카피경로\n명령줄\nwait(true/false)\nshow/hide[\nenforce/ask]" 를 이어 붙여 저장한다.
	/// 형식을 해석할 수 없으면 Unverified 로 돌려 적용을 보장하지 않는다.
	/// </summary>
	public static HookStatus ParseStatus(string? hooks, string toolDir, string uproject)
	{
		string script = Path.Combine(toolDir, HookScriptName);
		if (hooks == null || !hooks.Contains(script, StringComparison.OrdinalIgnoreCase))
		{
			return new HookStatus(HookState.NotRegistered, "");
		}

		string projectDir = Path.GetDirectoryName(uproject)!;
		string[] fields = hooks.Replace("\r", "").Split('\n');
		HookStatus? best = null;
		for (int i = 0; i + 4 < fields.Length; i++)
		{
			if (!HookTypeNames.Contains(fields[i]) || !fields[i + 2].Contains(script, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string type = fields[i];
			string path = fields[i + 1].Trim();
			string command = fields[i + 2];
			string wait = fields[i + 3].Trim();

			HookStatus status;
			if (!type.Equals("pre_commit_hook", StringComparison.OrdinalIgnoreCase))
			{
				status = new HookStatus(HookState.Misconfigured, $"훅 종류가 Pre-Commit 이 아님 ({type})");
			}
			else if (!wait.Equals("true", StringComparison.OrdinalIgnoreCase))
			{
				status = new HookStatus(HookState.Misconfigured, "\"Wait for the script to finish\" 이 꺼져 있어 커밋이 차단되지 않음");
			}
			else if (!(path == "*" || PathUtil.IsSameOrUnder(projectDir, path)))
			{
				status = new HookStatus(HookState.Misconfigured, $"다른 워킹 카피에 등록됨 ({path})");
			}
			else
			{
				// 스크립트 경로 뒤의 인자 (경로에는 따옴표가 올 수 없으므로 모두 제거하고 비교)
				string arguments = command[(command.IndexOf(script, StringComparison.OrdinalIgnoreCase) + script.Length)..].Replace("\"", "").Trim();
				if (arguments.Length == 0)
				{
					status = new HookStatus(HookState.Applied, "워킹 카피에서 프로젝트 자동 탐색");
				}
				else if (arguments.Equals(uproject, StringComparison.OrdinalIgnoreCase) || arguments.TrimEnd('\\').Equals(projectDir, StringComparison.OrdinalIgnoreCase))
				{
					status = new HookStatus(HookState.Applied, "");
				}
				else
				{
					status = new HookStatus(HookState.Misconfigured, $"다른 프로젝트를 검사하도록 등록됨 ({arguments})");
				}
			}
			if (best == null || status.State < best.State)
			{
				best = status;
			}
		}
		return best ?? new HookStatus(HookState.Unverified, "등록 형식을 해석할 수 없음");
	}

	public static string GetCommandLine(string toolDir, string uproject) => $"\"{Path.Combine(toolDir, HookScriptName)}\" \"{uproject}\"";
}
