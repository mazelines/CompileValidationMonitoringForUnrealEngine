using System.Text.RegularExpressions;

namespace ShipCheck.Core;

/// <summary>
/// UBT/컴파일러/링커 출력 해석. 실패 여부는 외부 도구 종료 코드로 정하고, 이 파서는 설명(파일/줄/코드)만 풍부하게 한다.
/// </summary>
public static class BuildOutputParser
{
	// MSVC / UHT: C:\Path\File.cpp(12,3): error C2065: message  |  C:\Path\File.h(5): Error: message
	static readonly Regex MsvcPattern = new Regex(@"^\s*(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?\)\s*:\s*(?:fatal\s+)?error\b\s*(?<code>[A-Z]{1,4}\d+)?\s*:\s*(?<msg>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	// clang: /path/File.cpp:12:3: error: message
	static readonly Regex ClangPattern = new Regex(@"^\s*(?<file>.+?):(?<line>\d+):(?:(?<col>\d+):)?\s*(?:fatal\s+)?error:\s*(?<msg>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	// 링커: Module.obj : error LNK2019: message
	static readonly Regex LinkerPattern = new Regex(@"^\s*(?<file>.+?)\s*:\s*(?:fatal\s+)?error\s+(?<code>LNK\d+)\s*:\s*(?<msg>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	static readonly Regex ZeroErrors = new Regex(@"\b0 error\(s\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	static readonly Regex[] ErrorPatterns =
	{
		new Regex(@"\b(fatal )?error [A-Z]{1,4}\d{3,5}\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled), // MSVC: error C2065:, error LNK2019:
		new Regex(@":\s*(fatal )?error\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),                  // clang / UHT: file(12): Error:
		new Regex(@"^\s*ERROR:", RegexOptions.Compiled),                                                          // UBT
		new Regex(@"unresolved external symbol", RegexOptions.IgnoreCase | RegexOptions.Compiled),
	};

	public static bool IsErrorLine(string line) => !ZeroErrors.IsMatch(line) && ErrorPatterns.Any(x => x.IsMatch(line));

	/// <summary>오류 줄 하나를 진단으로. 위치를 알 수 없으면 File 이 null.</summary>
	public static Diagnostic Parse(string raw, string step)
	{
		Diagnostic diagnostic = new Diagnostic
		{
			Code = DiagnosticCodes.CompileError,
			Severity = DiagnosticSeverity.Error,
			Stage = step,
			Message = raw.Trim(),
			Detail = raw,
		};
		Match match = MsvcPattern.Match(raw);
		if (!match.Success)
		{
			match = ClangPattern.Match(raw);
		}
		if (match.Success)
		{
			diagnostic.File = match.Groups["file"].Value.Trim();
			diagnostic.Line = int.Parse(match.Groups["line"].Value);
			diagnostic.Column = match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : null;
			diagnostic.ToolCode = match.Groups["code"].Success && match.Groups["code"].Value.Length > 0 ? match.Groups["code"].Value : null;
			diagnostic.Message = match.Groups["msg"].Value.Trim();
			return diagnostic;
		}
		match = LinkerPattern.Match(raw);
		if (match.Success)
		{
			diagnostic.File = match.Groups["file"].Value.Trim();
			diagnostic.ToolCode = match.Groups["code"].Value;
			diagnostic.Message = match.Groups["msg"].Value.Trim();
		}
		return diagnostic;
	}

	/// <summary>
	/// 실행 기록에서 화면에 보여 줄 빌드 오류 목록. 오류 줄을 찾지 못한 실패 단계는 로그 마지막 줄들을 보여 준다.
	/// (Version 1 기록에도 Steps 가 있으므로 같은 방식으로 동작한다)
	/// </summary>
	public static List<Diagnostic> FromRun(RunResult run)
	{
		List<Diagnostic> errors = new List<Diagnostic>();
		if (run.Outcome == RunOutcome.Canceled)
		{
			// 사용자가 중지한 실행의 마지막 출력은 오류가 아니다
			return errors;
		}
		foreach (StepResult step in run.Steps ?? new List<StepResult>())
		{
			if (step.Errors is { Count: > 0 })
			{
				errors.AddRange(step.Errors.Select(x => Parse(x, step.Label)));
			}
			else if (step.ExitCode is int code && code != 0)
			{
				errors.AddRange((step.Tail ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
					.Select(x => new Diagnostic { Code = DiagnosticCodes.BuildFailed, Stage = step.Label, Message = x.Trim(), Detail = x }));
			}
		}
		if (errors.Count == 0 && !run.IsPass && run.Outcome != RunOutcome.Skipped)
		{
			// 빌드 전 단계의 오류(설정/SVN/정책/검증 중 변경)
			errors.AddRange((run.Diagnostics ?? new List<Diagnostic>()).Where(x => x.Severity == DiagnosticSeverity.Error));
		}
		return errors;
	}
}
