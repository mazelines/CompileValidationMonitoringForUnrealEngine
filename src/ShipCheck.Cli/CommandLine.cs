namespace ShipCheck.Cli;

/// <summary>해석된 명령줄. 기존 ShipCheck.ps1 형식(-Project, -PathListFile ...)도 같은 이름으로 받는다.</summary>
sealed class CommandLine
{
	public string Command { get; set; } = "";
	public string? Project { get; set; }
	public string? WorkingCopy { get; set; }
	public string? ConfigPath { get; set; }
	public string? PathsFile { get; set; }
	public string? MessageFile { get; set; }
	public string? Message { get; set; }
	public string? ResultJson { get; set; }
	public string Trigger { get; set; } = "cli";
	public bool All { get; set; }
	public bool Force { get; set; }
	public bool NoSingleFile { get; set; }
	public bool DryRun { get; set; }
	public TimeSpan? StepTimeout { get; set; }
	public List<string> Positional { get; } = new();

	static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
	{
		["project"] = "project",
		["working-copy"] = "working-copy",
		["workingcopy"] = "working-copy",
		["config"] = "config",
		["configpath"] = "config",
		["paths-file"] = "paths-file",
		["pathlistfile"] = "paths-file",
		["message-file"] = "message-file",
		["messagefile"] = "message-file",
		["m"] = "message",
		["message"] = "message",
		["result-json"] = "result-json",
		["resultjson"] = "result-json",
		["trigger"] = "trigger",
		["step-timeout"] = "step-timeout",
		["all"] = "all",
		["force"] = "force",
		["no-single-file"] = "no-single-file",
		["nosinglefile"] = "no-single-file",
		["dry-run"] = "dry-run",
		["dryrun"] = "dry-run",
		["paths"] = "paths",
		["query"] = "query",
	};

	static readonly HashSet<string> Switches = new() { "all", "force", "no-single-file", "dry-run", "query" };

	/// <summary>명령 이름이 없으면 validate (기존 ShipCheck.ps1 인수 호환). -Query 는 query 명령.</summary>
	public static CommandLine Parse(IReadOnlyList<string> args)
	{
		CommandLine result = new CommandLine();
		int index = 0;
		if (args.Count > 0 && args[0] is "validate" or "query" or "hook" or "commit" or "help")
		{
			result.Command = args[0];
			index = 1;
		}
		for (; index < args.Count; index++)
		{
			string arg = args[index];
			string? name = OptionName(arg);
			if (name == null || result.Command == "hook")
			{
				// 훅 인수(PATH DEPTH MESSAGEFILE CWD)는 모두 위치 인수. 경로가 '-' 로 시작할 일은 없다
				result.Positional.Add(arg);
				continue;
			}
			if (Switches.Contains(name))
			{
				switch (name)
				{
					case "all": result.All = true; break;
					case "force": result.Force = true; break;
					case "no-single-file": result.NoSingleFile = true; break;
					case "dry-run": result.DryRun = true; break;
					case "query": result.Command = "query"; break;
				}
				continue;
			}
			if (name == "paths")
			{
				// -Paths a b c : 다음 옵션 전까지 경로
				for (; index + 1 < args.Count && OptionName(args[index + 1]) == null; index++)
				{
					result.Positional.Add(args[index + 1]);
				}
				continue;
			}
			if (index + 1 >= args.Count)
			{
				throw new ArgumentException($"{arg} 뒤에 값이 필요합니다.");
			}
			string value = args[++index];
			switch (name)
			{
				case "project": result.Project = value; break;
				case "working-copy": result.WorkingCopy = value; break;
				case "config": result.ConfigPath = value; break;
				case "paths-file": result.PathsFile = value; break;
				case "message-file": result.MessageFile = value; break;
				case "message": result.Message = value; break;
				case "result-json": result.ResultJson = value; break;
				case "trigger": result.Trigger = value; break;
				case "step-timeout":
					result.StepTimeout = int.TryParse(value, out int minutes) && minutes > 0 ? TimeSpan.FromMinutes(minutes) : throw new ArgumentException("--step-timeout 은 분 단위 양의 정수입니다.");
					break;
			}
		}
		if (result.Command.Length == 0)
		{
			result.Command = "validate";
		}
		return result;
	}

	/// <summary>"-x" / "--x" 형식의 알려진 옵션이면 정규화한 이름, 아니면 null (경로 등 위치 인수)</summary>
	static string? OptionName(string arg)
	{
		if (arg.Length < 2 || arg[0] != '-')
		{
			return null;
		}
		string name = arg.TrimStart('-');
		return Aliases.TryGetValue(name, out string? canonical) ? canonical : throw new ArgumentException($"알 수 없는 옵션: {arg}");
	}

	/// <summary>
	/// 원본 명령줄을 cmd 방식으로 나눈다: 따옴표는 묶기만 하고 역슬래시는 그대로 둔다.
	/// 표준 규칙에서는 "D:\Work\" 의 \" 가 따옴표 이스케이프가 되어 다음 인수와 합쳐지므로,
	/// 경로만 받는 훅 인수(TortoiseSVN 경로, 끝에 \ 가 붙은 프로젝트 폴더)는 이 방식으로 읽는다.
	/// </summary>
	public static List<string> SplitRaw(string commandLine)
	{
		List<string> tokens = new List<string>();
		System.Text.StringBuilder current = new System.Text.StringBuilder();
		bool quoted = false;
		bool hasToken = false;
		foreach (char c in commandLine)
		{
			if (c == '"')
			{
				quoted = !quoted;
				hasToken = true;
			}
			else if (char.IsWhiteSpace(c) && !quoted)
			{
				if (hasToken)
				{
					tokens.Add(current.ToString());
					current.Clear();
					hasToken = false;
				}
			}
			else
			{
				current.Append(c);
				hasToken = true;
			}
		}
		if (hasToken)
		{
			tokens.Add(current.ToString());
		}
		return tokens;
	}

	/// <summary>
	/// TortoiseSVN Pre-commit 훅 인수: [프로젝트] PATH DEPTH MESSAGEFILE CWD.
	/// 첫 인수가 .uproject 이거나 존재하는 폴더면 프로젝트 (기존 TortoiseSVN-PreCommit.cmd 와 같은 규칙).
	/// </summary>
	public static (string? Project, string PathFile, string MessageFile, string WorkingCopy) ParseHookArguments(IReadOnlyList<string> positional, Func<string, bool> directoryExists)
	{
		List<string> args = positional.ToList();
		string? project = null;
		if (args.Count > 0 && (args[0].EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) || (args.Count >= 5 && directoryExists(args[0]))))
		{
			project = args[0].TrimEnd('\\');
			args.RemoveAt(0);
		}
		if (args.Count < 4)
		{
			throw new ArgumentException("훅 인수가 부족합니다: [프로젝트] PATH DEPTH MESSAGEFILE CWD");
		}
		return (project, args[0], args[2], args[3].TrimEnd('\\'));
	}

	public const string Usage = """
		ShipCheck - SVN 커밋 전 언리얼 게임 타깃(기본 Shipping) 증분 빌드 검증

		사용법:
		  ShipCheck validate [옵션] [경로...]       변경 파일 기준 검증 (경로는 --working-copy 기준 상대 경로 가능)
		  ShipCheck query [옵션]                    빌드 없이 상태 조회 (--result-json 으로 JSON 출력)
		  ShipCheck commit -m 메시지 [옵션] [대상...]  검증 통과 시에만 svn commit
		  ShipCheck hook [프로젝트] PATH DEPTH MESSAGEFILE CWD   TortoiseSVN Pre-commit 훅

		옵션:
		  --project P         .uproject 또는 프로젝트 폴더 (생략하면 워킹 카피에서 자동 탐색)
		  --working-copy W    워킹 카피 / 상대 경로 기준 폴더
		  --config C          설정 파일 (기본: 도구 폴더의 ShipCheck.<프로젝트명>.config.json > ShipCheck.config.json)
		  --paths-file F      검사 대상 경로 목록 파일 (UTF-8, 한 줄에 하나)
		  --message-file F    커밋 메시지 파일 (우회 태그 검사)
		  --all               파일 필터 없이 전체 빌드 (최초 프라이밍)
		  --force             통과 캐시 무시
		  --no-single-file    변경 파일 단독 컴파일 생략
		  --dry-run           UBT 를 실행하지 않고 명령만 출력
		  --result-json F     결과 JSON 저장
		  --step-timeout N    단계별 UBT 제한 시간(분)

		종료 코드: 0 통과/캐시/건너뜀, 1 검증 실패(커밋 차단), 2 실행 오류/취소/시간 초과/결과 확정 불가
		""";
}
