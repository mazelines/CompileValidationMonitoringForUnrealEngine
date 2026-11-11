using System.Diagnostics;
using System.Text;
using ShipCheck.Core;
using ShipCheck.Infrastructure;
using ShipCheck.TestSupport;

namespace ShipCheck.Acceptance.Tests;

/// <summary>
/// 전환 검증: 수정 완료된 기존 PowerShell 엔진(기준선 사본)과 새 엔진을 같은 시나리오로 실행해 의미상 결과를 비교한다.
/// 시간/로그 경로/상태 키(계산 방식이 바뀜) 같은 비결정적 필드는 비교하지 않는다.
/// 기준선 스크립트 경로를 SHIPCHECK_LEGACY_SCRIPT 환경 변수로 줄 때만 실행된다 (전환 기간의 증거용).
/// </summary>
[TestClass]
public sealed class LegacyEquivalenceTests
{
	const string Hero = @"Source\Game\Private\Hero.cpp";
	const string Bug = @"Source\Game\Private\Bug.cpp";

	sealed record Outcome(string Result, int ExitCode, IReadOnlyList<string> Files);

	[TestMethod, Timeout(600_000), TestCategory("Legacy")]
	public async Task 기존_엔진과_새_엔진의_결과가_같다()
	{
		List<string> differences = await CompareAsync(asciiPath: true);
		Assert.AreEqual(0, differences.Count, string.Join("\n", differences));
	}

	/// <summary>
	/// 공백/한글 경로에서는 기존 엔진의 알려진 결함(PowerShell 5.1 Get-Content 가 UTF-8 PassCache 를 ANSI 로 읽어
	/// 한글 경로의 검증 파일 목록이 깨짐 → 캐시가 적중하지 않고 매번 다시 빌드)만 차이로 남는다. 새 엔진은 이 결함을 복제하지 않는다.
	/// </summary>
	[TestMethod, Timeout(600_000), TestCategory("Legacy")]
	public async Task 한글_경로에서는_기존_엔진의_캐시_결함만_다르다()
	{
		List<string> differences = await CompareAsync(asciiPath: false);
		Assert.IsTrue(differences.All(x => x.Contains("기존=Pass/0 새=Cached/0")), string.Join("\n", differences));
	}

	async Task<List<string>> CompareAsync(bool asciiPath)
	{
		string? legacy = Environment.GetEnvironmentVariable("SHIPCHECK_LEGACY_SCRIPT");
		if (string.IsNullOrEmpty(legacy) || !File.Exists(legacy))
		{
			Assert.Inconclusive("SHIPCHECK_LEGACY_SCRIPT 에 기준선 ShipCheck.ps1 경로를 지정하면 실행됩니다.");
		}

		using ShipCheckFixture oldSide = new ShipCheckFixture(asciiPath);
		using ShipCheckFixture newSide = new ShipCheckFixture(asciiPath);
		string legacyDir = Path.Combine(oldSide.Root, "legacy");
		Directory.CreateDirectory(legacyDir);
		File.Copy(legacy, Path.Combine(legacyDir, "ShipCheck.ps1"));

		Outcome RunLegacy(string[] paths, string? message)
		{
			string json = Path.Combine(oldSide.Root, "legacy.json");
			string list = Path.Combine(oldSide.Root, "legacy-paths.txt");
			File.Delete(json);
			File.WriteAllLines(list, paths.Select(oldSide.F), new UTF8Encoding(false));
			ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(legacyDir, "ShipCheck.ps1"),
				"-Project", oldSide.UProject, "-ConfigPath", oldSide.ConfigPath, "-PathListFile", list, "-ResultJson", json, "-Trigger", "test" })
			{
				startInfo.ArgumentList.Add(argument);
			}
			if (message != null)
			{
				string messageFile = Path.Combine(oldSide.Root, "legacy-message.txt");
				File.WriteAllText(messageFile, message, new UTF8Encoding(false));
				startInfo.ArgumentList.Add("-MessageFile");
				startInfo.ArgumentList.Add(messageFile);
			}
			using Process process = Process.Start(startInfo)!;
			_ = process.StandardOutput.ReadToEndAsync();
			_ = process.StandardError.ReadToEndAsync();
			Assert.IsTrue(process.WaitForExit(300_000));
			RunResult result = ShipCheckJson.Deserialize<RunResult>(File.ReadAllText(json))!;
			return new Outcome(result.Result, process.ExitCode, Relative(oldSide, result.Files));
		}

		async Task<Outcome> RunNew(string[] paths, string? message)
		{
			RunResult result = await newSide.ValidateAsync(paths, r => new ValidationRequest { Selector = r.Selector, Paths = r.Paths, Message = message, Trigger = "test" });
			return new Outcome(result.Result, result.ExitCode, Relative(newSide, result.Files));
		}

		List<string> differences = new List<string>();
		async Task Step(string name, Action<ShipCheckFixture> arrange, string[] paths, string? message = null)
		{
			arrange(oldSide);
			arrange(newSide);
			Outcome before = RunLegacy(paths, message);
			Outcome after = await RunNew(paths, message);
			string line = $"{name}: 기존={before.Result}/{before.ExitCode} 새={after.Result}/{after.ExitCode}";
			Console.WriteLine(line);
			if (before.Result != after.Result || before.ExitCode != after.ExitCode || !before.Files.SequenceEqual(after.Files))
			{
				differences.Add(line + $" 파일 기존=[{string.Join(",", before.Files)}] 새=[{string.Join(",", after.Files)}]");
			}
		}

		await Step("콘텐츠만 변경", f => f.Touch(@"Content\Map.umap"), new[] { @"Content\Map.umap" });
		await Step("에디터 모듈만 변경", f => f.Touch(@"Source\GameEditor\Private\EdTool.cpp"), new[] { @"Source\GameEditor\Private\EdTool.cpp" });
		await Step("런타임 변경 통과", f => { f.Revert(@"Source\GameEditor\Private\EdTool.cpp"); f.Touch(Hero); }, new[] { Hero });
		await Step("같은 상태 캐시", _ => { }, new[] { Hero });
		await Step("단독 컴파일 실패", f => f.Touch(Bug), new[] { Bug });
		await Step("우회 태그", _ => { }, new[] { Bug }, "긴급 [skip-shipcheck] 장애 대응");
		await Step("미등록 소스 + Fail 정책", f => { f.Revert(Bug); f.Write(@"Source\Game\Private\New.cpp", "n"); f.WriteConfig(c => c.UncommittedSourceChanges = "Fail"); }, new[] { Hero });
		await Step("타깃 빌드 실패", f => { File.Delete(f.F(@"Source\Game\Private\New.cpp")); f.WriteConfig(); f.Touch(Hero); f.Control(new FakeControl { Fail = true }); }, new[] { Hero });
		await Step("경로 없음", f => f.Control(new FakeControl()), Array.Empty<string>());
		return differences;
	}

	static IReadOnlyList<string> Relative(ShipCheckFixture fixture, IEnumerable<string>? files) =>
		(files ?? Enumerable.Empty<string>()).Select(x => Path.GetRelativePath(fixture.ProjectDir, x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
}
