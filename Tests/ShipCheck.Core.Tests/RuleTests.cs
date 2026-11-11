using ShipCheck.Core;

namespace ShipCheck.Core.Tests;

[TestClass]
public sealed class ConfigTests
{
	[TestMethod]
	public void 설정_우선순위_명시_프로젝트전용_공통()
	{
		HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase) { @"C:\Tool\ShipCheck.Game.config.json" };
		Assert.AreEqual(@"D:\x.json", ConfigSelector.Select(@"D:\x.json", @"D:\W\Game\Game.uproject", @"C:\Tool", existing.Contains));
		Assert.AreEqual(@"C:\Tool\ShipCheck.Game.config.json", ConfigSelector.Select(null, @"D:\W\Game\Game.uproject", @"C:\Tool", existing.Contains));
		Assert.AreEqual(@"C:\Tool\ShipCheck.config.json", ConfigSelector.Select(null, @"D:\W\Other\Other.uproject", @"C:\Tool", existing.Contains));
		Assert.AreEqual(@"C:\Tool\ShipCheck.config.json", ConfigSelector.Select(null, null, @"C:\Tool", existing.Contains));
	}

	[TestMethod]
	public void 설정_형식_오류는_기본값으로_숨기지_않는다()
	{
		ShipCheckException ex = Assert.ThrowsException<ShipCheckException>(() => ShipCheckConfig.Parse("{ \"UseCache\": ", "x.json"));
		Assert.AreEqual(ErrorKind.ConfigError, ex.Kind);
		Assert.AreEqual(DiagnosticCodes.ConfigInvalid, ex.Diagnostic.Code);
	}

	[TestMethod]
	public void 기존_설정_파일_형식을_그대로_읽는다()
	{
		ShipCheckConfig config = ShipCheckConfig.Parse("""
			{
			    "UProject": "", "EngineDir": "",
			    "Targets": [ { "Name": "", "Platform": "Win64", "Configuration": "Shipping" } ],
			    "ExtraUbtArgs": [],
			    "SingleFileCheck": { "Enabled": true, "IncludeHeaders": true, "MaxFiles": 30 },
			    "SkipEditorOnlyChanges": true, "UncommittedSourceChanges": "Fail",
			    "SkipTag": "[skip-shipcheck]", "UseCache": true, "MaxErrorLines": 25
			}
			""", "x.json");
		Assert.IsTrue(config.FailOnUncommittedChanges);
		List<BuildTarget> targets = BuildTarget.FromConfig(config, "Game");
		Assert.AreEqual(new BuildTarget("Game", "Win64", "Shipping"), targets.Single());
	}

	[TestMethod]
	public void 정책_키는_인수_경계를_구분한다()
	{
		ValidationPolicy joined = ValidationPolicy.From(new ShipCheckConfig { ExtraUbtArgs = { "-a -b" } }, new[] { new BuildTarget("G", "Win64", "Shipping") }, false);
		ValidationPolicy split = ValidationPolicy.From(new ShipCheckConfig { ExtraUbtArgs = { "-a", "-b" } }, new[] { new BuildTarget("G", "Win64", "Shipping") }, false);
		Assert.AreNotEqual(joined.ToKeyText(), split.ToKeyText());
	}

	[TestMethod]
	public void 에디터_타깃이면_에디터_모듈도_검사한다()
	{
		ValidationPolicy game = ValidationPolicy.From(new ShipCheckConfig(), new[] { new BuildTarget("Game", "Win64", "Shipping") }, false);
		ValidationPolicy editor = ValidationPolicy.From(new ShipCheckConfig(), new[] { new BuildTarget("GameEditor", "Win64", "Development") }, false);
		Assert.IsFalse(ModuleRules.Affects(FileKind.Source, "Editor", game));
		Assert.IsTrue(ModuleRules.Affects(FileKind.Source, "Editor", editor));
		Assert.IsFalse(ModuleRules.Affects(FileKind.Source, "Developer", game), "Shipping 은 Developer 모듈을 포함하지 않는다");
		Assert.IsTrue(ModuleRules.Affects(FileKind.Source, "Developer", editor));
		Assert.IsTrue(ModuleRules.Affects(FileKind.Source, null, game), "모듈을 모르면 보수적으로 포함");
		Assert.IsTrue(ModuleRules.Affects(FileKind.Descriptor, "Editor", game));
		Assert.IsTrue(ModuleRules.Affects(FileKind.Source, "Runtime", game));
	}
}

[TestClass]
public sealed class FileRuleTests
{
	[TestMethod]
	[DataRow(@"D:\P\Source\A.cpp", FileKind.Source)]
	[DataRow(@"D:\P\Source\A.H", FileKind.Source)]
	[DataRow(@"D:\P\Source\Game.Target.cs", FileKind.Target)]
	[DataRow(@"D:\P\Source\Game\Game.Build.cs", FileKind.BuildCs)]
	[DataRow(@"D:\P\Game.uproject", FileKind.Descriptor)]
	[DataRow(@"D:\P\Plugins\X\X.uplugin", FileKind.Descriptor)]
	public void 코드_파일_종류(string path, FileKind kind) => Assert.AreEqual(kind, FileClassifier.GetKind(path));

	[TestMethod]
	public void 코드가_아닌_파일() => Assert.IsNull(FileClassifier.GetKind(@"D:\P\Content\Map.umap"));

	[TestMethod]
	public void 산출물_폴더는_프로젝트_기준으로만_판단한다()
	{
		Assert.IsTrue(FileClassifier.IsGeneratedPath(@"D:\Work\Game\Plugins\X\Intermediate\Gen.cpp", @"D:\Work\Game"));
		Assert.IsTrue(FileClassifier.IsGeneratedPath(@"D:\Work\Game\Plugins\X\Binaries", @"D:\Work\Game"));
		Assert.IsFalse(FileClassifier.IsGeneratedPath(@"D:\Saved\Game\Source\A.cpp", @"D:\Saved\Game"), "프로젝트 위쪽 폴더 이름(Saved)은 보지 않는다");
		Assert.IsFalse(FileClassifier.IsGeneratedPath(@"D:\Work\Game\Source\IntermediateStuff\A.cpp", @"D:\Work\Game"));
	}

	[TestMethod]
	public void 단독_컴파일_대상_정렬_절단_헤더옵션()
	{
		ValidationPolicy policy = ValidationPolicy.From(new ShipCheckConfig { SingleFileCheck = new SingleFileConfig { MaxFiles = 2, IncludeHeaders = false } }, new[] { new BuildTarget("G", "Win64", "Shipping") }, false);
		SingleFileSelector.Selection selection = SingleFileSelector.Select(new[] { @"C:\c.cpp", @"C:\a.cpp", @"C:\b.h", @"C:\b.cpp", @"C:\A.CPP" }, policy, all: false, _ => true);
		CollectionAssert.AreEqual(new[] { @"C:\a.cpp", @"C:\b.cpp" }, selection.Files.ToArray());
		Assert.AreEqual(1, selection.Truncated);
		Assert.AreEqual(0, SingleFileSelector.Select(new[] { @"C:\a.cpp" }, policy, all: true, _ => true).Files.Count, "--all 은 단독 컴파일 생략");
		Assert.AreEqual(0, SingleFileSelector.Select(new[] { @"C:\gone.cpp" }, policy, all: false, _ => false).Files.Count, "삭제된 파일 제외");
	}
}

[TestClass]
public sealed class StateKeyTests
{
	static readonly BuildTarget[] Targets = { new BuildTarget("Game", "Win64", "Shipping") };
	static readonly ValidationPolicy Policy = ValidationPolicy.From(new ShipCheckConfig(), Targets, false);

	static string Key(string baseline = "b", ValidationPolicy? policy = null, params (string, string)[] files) =>
		StateKey.Compute(@"D:\Engine", baseline, Targets, policy ?? Policy, files.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)));

	[TestMethod]
	public void 같은_입력은_같은_키_순서_무관()
	{
		Assert.AreEqual(Key("b", null, ("a.cpp", "1"), ("b.cpp", "2")), Key("b", null, ("b.cpp", "2"), ("a.cpp", "1")));
	}

	[TestMethod]
	public void 기준_정책_내용이_바뀌면_다른_키()
	{
		string key = Key("b", null, ("a.cpp", "1"));
		Assert.AreNotEqual(key, Key("b2", null, ("a.cpp", "1")));
		Assert.AreNotEqual(key, Key("b", null, ("a.cpp", "2")));
		Assert.AreNotEqual(key, Key("b", ValidationPolicy.From(new ShipCheckConfig { SingleFileCheck = new SingleFileConfig { IncludeHeaders = false } }, Targets, false), ("a.cpp", "1")));
		Assert.AreNotEqual(key, Key("b", ValidationPolicy.From(new ShipCheckConfig(), Targets, noSingleFile: true), ("a.cpp", "1")));
	}
}

[TestClass]
public sealed class PassCacheTests
{
	const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	const string KeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

	[TestMethod]
	public void 머리말_없는_구형식은_빈_캐시()
	{
		Assert.AreEqual(0, PassCache.Parse(new[] { KeyA + "\tC:\\a.cpp" }).Count);
	}

	[TestMethod]
	public void 손상된_줄은_적중_근거로_쓰지_않는다()
	{
		List<PassCache.Entry> entries = PassCache.Parse(new[] { PassCache.Header, "garbage\tC:\\a.cpp", KeyA[..20], KeyA + "\tC:\\a.cpp" });
		Assert.AreEqual(1, entries.Count);
	}

	[TestMethod]
	public void 검증된_파일_집합을_모두_포함해야_적중()
	{
		List<PassCache.Entry> entries = PassCache.Parse((PassCache.Header + "\r\n" + KeyA + "\tC:\\a.cpp\r\n" + KeyA + "\tC:\\b.cpp\r\n" + KeyB + "\t\r\n").Split("\r\n"));
		Assert.IsTrue(PassCache.IsHit(entries, KeyA, new[] { @"c:\A.cpp", @"C:\b.cpp" }), "같은 키의 기록들을 합쳐서 판단, 대소문자 무시");
		Assert.IsFalse(PassCache.IsHit(entries, KeyA, new[] { @"C:\c.cpp" }));
		Assert.IsTrue(PassCache.IsHit(entries, KeyB, Array.Empty<string>()));
		Assert.IsFalse(PassCache.IsHit(entries, "", Array.Empty<string>()), "빈 키는 적중하지 않는다");
	}

	[TestMethod]
	public void 추가하면_최근_50개만_유지()
	{
		List<PassCache.Entry> entries = Enumerable.Range(0, 60).Select(i => new PassCache.Entry(i.ToString("x40"), Array.Empty<string>())).ToList();
		string text = PassCache.Append(entries, KeyA, new[] { @"C:\a.cpp" });
		List<PassCache.Entry> parsed = PassCache.Parse(text.Split("\r\n"));
		Assert.AreEqual(PassCache.MaxEntries, parsed.Count);
		Assert.AreEqual(KeyA, parsed[^1].Key);
	}
}

[TestClass]
public sealed class SvnXmlTests
{
	const string StatusXml = """
		<?xml version="1.0" encoding="UTF-8"?>
		<status>
		<target path="D:\W\공백 경로\Game\Source">
		<entry path="D:\W\공백 경로\Game\Source\Game\Private\Hero.cpp">
		<wc-status item="modified" revision="5" props="none"><commit revision="3"><author>a</author></commit></wc-status>
		</entry>
		<entry path="D:\W\공백 경로\Game\Source\New Dir">
		<wc-status item="unversioned" props="none"></wc-status>
		</entry>
		<entry path="D:\W\공백 경로\Game\Source\Game\Game.Build.cs">
		<wc-status item="normal" revision="5" props="modified" switched="true"><commit revision="2"/></wc-status>
		</entry>
		</target>
		<changelist name="cl">
		<entry path="D:\W\공백 경로\Game\Source\Game\Private\Bug.cpp">
		<wc-status item="added" revision="-1" props="none" tree-conflicted="true"></wc-status>
		</entry>
		</changelist>
		</status>
		""";

	[TestMethod]
	public void 상태_XML_해석_공백_한글_체인지리스트()
	{
		List<SvnStatusEntry> entries = SvnXml.ParseStatus(StatusXml);
		Assert.AreEqual(4, entries.Count);
		Assert.AreEqual(@"D:\W\공백 경로\Game\Source\Game\Private\Hero.cpp", entries[0].Path);
		Assert.AreEqual("M", entries[0].StatusLetter);
		Assert.AreEqual("3", entries[0].CommittedRevision);
		Assert.AreEqual("?", entries[1].StatusLetter);
		Assert.IsFalse(entries[2].IsChange, "속성만 바뀐 파일은 변경 목록이 아니라 기준 지문에 반영");
		Assert.IsTrue(entries[2].Switched);
		Assert.AreNotEqual(SvnXml.BaselineLine(entries[2]), SvnXml.BaselineLine(entries[2] with { Props = "none" }));
		Assert.AreEqual("A", entries[3].StatusLetter);
		Assert.IsTrue(entries[3].TreeConflicted);
	}

	[TestMethod]
	public void info_XML_워킹카피_루트()
	{
		SvnInfo? info = SvnXml.ParseInfo("""
			<info><entry kind="dir" path="D:\W" revision="7"><url>file:///R/trunk</url>
			<wc-info><wcroot-abspath>D:/W</wcroot-abspath></wc-info></entry></info>
			""");
		Assert.IsNotNull(info);
		Assert.AreEqual(@"D:\W", info.WcRoot);
		Assert.AreEqual("7", info.Revision);
		Assert.IsNull(SvnXml.ParseInfo("<info><entry kind=\"dir\" path=\"x\" revision=\"1\"><url>u</url></entry></info>"), "wc-root 가 없으면 워킹 카피가 아니다");
	}

	[TestMethod]
	public void 잘못된_XML은_형식_오류()
	{
		Assert.ThrowsException<FormatException>(() => SvnXml.ParseStatus("<status><entry"));
	}
}

[TestClass]
public sealed class BuildOutputTests
{
	[TestMethod]
	[DataRow(@"C:\P\Hero.cpp(42): error C2065: 'GEditor': undeclared identifier", true)]
	[DataRow(@"Module.obj : error LNK2019: unresolved external symbol foo", true)]
	[DataRow(@"/p/File.cpp:12:3: error: no member named 'x'", true)]
	[DataRow(@"C:\P\Hero.h(5): Error: Missing GENERATED_BODY", true)]
	[DataRow("ERROR: UBT failed", true)]
	[DataRow("Build succeeded: 0 error(s), 3 warning(s)", false)]
	[DataRow("warning C4996: deprecated", false)]
	public void 오류_줄_판정(string line, bool expected) => Assert.AreEqual(expected, BuildOutputParser.IsErrorLine(line));

	[TestMethod]
	public void MSVC_위치와_코드()
	{
		Diagnostic d = BuildOutputParser.Parse(@"C:\공백 경로\Hero.cpp(42,7): fatal error C1083: Cannot open include file: 'Missing.h'", "step");
		Assert.AreEqual(@"C:\공백 경로\Hero.cpp", d.File);
		Assert.AreEqual(42, d.Line);
		Assert.AreEqual(7, d.Column);
		Assert.AreEqual("C1083", d.ToolCode);
		Assert.AreEqual("step", d.Stage);
	}

	[TestMethod]
	public void 링커_clang_위치()
	{
		Diagnostic linker = BuildOutputParser.Parse("Game.obj : error LNK2019: unresolved external symbol", "s");
		Assert.AreEqual("LNK2019", linker.ToolCode);
		Assert.IsNull(linker.Line);
		Diagnostic clang = BuildOutputParser.Parse("/p/File.cpp:12:3: error: bad", "s");
		Assert.AreEqual(12, clang.Line);
		Assert.AreEqual("bad", clang.Message);
	}

	[TestMethod]
	public void 오류_줄이_없는_실패는_로그_끝을_보여준다()
	{
		RunResult run = new RunResult { Result = "Fail", Steps = new() { new StepResult { Label = "Game Win64 Shipping", ExitCode = 6, Errors = new(), Tail = new() { "", "something broke" } } } };
		List<Diagnostic> errors = BuildOutputParser.FromRun(run);
		Assert.AreEqual(1, errors.Count);
		Assert.AreEqual("something broke", errors[0].Message);
	}

	[TestMethod]
	public void 취소한_실행은_오류로_보여주지_않는다()
	{
		RunResult run = new RunResult { Result = "Canceled", Steps = new() { new StepResult { Label = "s", ExitCode = -1, Tail = new() { "FakeUBT ..." } } } };
		Assert.AreEqual(0, BuildOutputParser.FromRun(run).Count);
	}

	[TestMethod]
	public void 빌드_전_오류는_진단에서_가져온다()
	{
		RunResult run = new RunResult { Result = "Error", Diagnostics = new() { Diagnostic.Error(DiagnosticCodes.ChangedDuringValidation, "run", "변경됨"), Diagnostic.Warning("w", "x", "경고") } };
		Assert.AreEqual(DiagnosticCodes.ChangedDuringValidation, BuildOutputParser.FromRun(run).Single().Code);
	}
}

[TestClass]
public sealed class ContractTests
{
	[TestMethod]
	public void 종료_코드_매핑()
	{
		Assert.AreEqual(0, ExitCodes.For(RunOutcome.Pass));
		Assert.AreEqual(0, ExitCodes.For(RunOutcome.Cached));
		Assert.AreEqual(0, ExitCodes.For(RunOutcome.Skipped));
		Assert.AreEqual(1, ExitCodes.For(RunOutcome.Fail));
		Assert.AreEqual(2, ExitCodes.For(RunOutcome.Error));
		Assert.AreEqual(2, ExitCodes.For(RunOutcome.Canceled), "취소는 커밋 허용 코드가 아니다");
	}

	[TestMethod]
	public void Version1_기록을_읽는다()
	{
		RunResult? run = ShipCheckJson.Deserialize<RunResult>("""{"Version":1,"Trigger":"hook","StartTime":"2026-10-28T10:00:00+09:00","Result":"Cached","Reason":"","ElapsedSeconds":"1.5","StateKey":"k","Files":["a"],"Steps":[]}""");
		Assert.IsNotNull(run);
		Assert.AreEqual(RunOutcome.Cached, run.Outcome);
		Assert.IsTrue(run.IsPass);
		Assert.AreEqual(1.5, run.ElapsedSeconds);
		Assert.AreEqual("커밋 훅", run.TriggerText);
	}

	[TestMethod]
	public void 건너뜀은_검증된_통과가_아니다()
	{
		Assert.IsFalse(new RunResult { Result = "Skipped" }.IsPass);
	}
}

[TestClass]
public sealed class HealthTests
{
	static QueryResult Query(bool svn = true, bool upToDate = false, string key = "k", bool affecting = true) => new QueryResult
	{
		SvnAvailable = svn,
		StateKey = key,
		UpToDate = upToDate,
		Changes = affecting ? new() { new ChangeEntry { Path = "a.cpp", Affects = true } } : new() { new ChangeEntry { Path = "e.cpp", Affects = false } },
	};

	static readonly RunResult Failure = new RunResult { Result = "Fail", StateKey = "k" };

	[TestMethod]
	public void 상태_판정()
	{
		Assert.AreEqual(Health.Running, HealthEvaluator.Evaluate(true, true, null, Query(), Array.Empty<RunResult>()));
		Assert.AreEqual(Health.Unknown, HealthEvaluator.Evaluate(false, false, null, null, Array.Empty<RunResult>()));
		Assert.AreEqual(Health.Error, HealthEvaluator.Evaluate(true, false, "엔진 없음", null, Array.Empty<RunResult>()));
		Assert.AreEqual(Health.NoChanges, HealthEvaluator.Evaluate(true, false, null, Query(affecting: false), Array.Empty<RunResult>()));
		Assert.AreEqual(Health.Passed, HealthEvaluator.Evaluate(true, false, null, Query(upToDate: true), Array.Empty<RunResult>()));
		Assert.AreEqual(Health.NeedsValidation, HealthEvaluator.Evaluate(true, false, null, Query(), Array.Empty<RunResult>()));
		Assert.AreEqual(Health.Failed, HealthEvaluator.Evaluate(true, false, null, Query(), new[] { Failure }));
	}

	[TestMethod]
	public void 캐시를_꺼도_현재_상태의_실패를_표시한다()
	{
		// UseCache=false 여도 상태 키가 있으므로 실패 기록과 연결된다 (R2-5)
		QueryResult query = Query(upToDate: false, key: "k");
		Assert.AreEqual(Health.Failed, HealthEvaluator.Evaluate(true, false, null, query, new[] { Failure }));
	}

	[TestMethod]
	public void SVN_상태를_모르면_변경_없음이_아니다()
	{
		// R2-4: 워킹 카피가 아니면 변경 목록이 비어도 NoChanges 로 보이지 않는다
		QueryResult query = new QueryResult { SvnAvailable = false, StateKey = "" };
		Assert.AreEqual(Health.NeedsValidation, HealthEvaluator.Evaluate(true, false, null, query, Array.Empty<RunResult>()));
	}

	[TestMethod]
	public void 나중에_통과했으면_실패로_보지_않는다()
	{
		RunResult[] history = { new RunResult { Result = "Pass", StateKey = "k" }, Failure };
		Assert.AreEqual(Health.NeedsValidation, HealthEvaluator.Evaluate(true, false, null, Query(), history));
		Assert.AreEqual(Health.Failed, HealthEvaluator.Evaluate(true, false, null, Query(), new[] { new RunResult { Result = "Skipped", StateKey = "k" }, Failure }));
	}
}
