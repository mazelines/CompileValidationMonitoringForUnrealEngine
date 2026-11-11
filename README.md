# ShipCheck — SVN 커밋 전 언리얼 Shipping 빌드 검증

에디터(Development Editor) 빌드에서는 문제없던 C++ 코드가 Shipping 등 게임 빌드에서야 깨지는 문제를
**커밋 시점에 로컬에서 차단**하는 도구입니다.

## 구성

GUI, 명령줄, TortoiseSVN 훅이 **같은 C# 검증 엔진**을 사용합니다. PowerShell 스크립트는 더 이상 필요하지 않습니다.

| 구성 요소 | 역할 |
|---|---|
| `ShipCheck.Core` (DLL) | 검증 계약(요청/결과/진단), 정책, 검증 순서, 상태 키·캐시 판정, 변경 분류, 빌드 오류 파서, 화면 상태 판정 |
| `ShipCheck.Infrastructure` (DLL) | svn.exe(XML 출력), Build.bat 실행(Job Object), 프로젝트 잠금, 엔진 탐색, 설정·캐시·기록 저장, TortoiseSVN 훅 등록 확인 |
| `ShipCheck.exe` | 명령줄 / 훅 진입점 (`validate`, `query`, `commit`, `hook`) |
| `ShipCheckMonitor.exe` | **GUI** (UGS 스타일 모니터). 엔진을 직접 호출합니다 |
| `TortoiseSVN-PreCommit.cmd`, `ShipCheck.cmd`, `ShipCommit.cmd`, `ShipCheckMonitor.cmd` | 인수만 `ShipCheck.exe` / GUI 로 넘기는 진입점 (판단 로직 없음) |
| `ShipCheck.config.json`, `ShipCheck.<프로젝트명>.config.json` | 설정 (GUI ⚙ 설정에서도 편집) |
| `Build.cmd` / `Publish.cmd` / `Test.cmd` | 소스 트리 빌드(`Bin\`) / 배포 폴더 생성 / 전체 자동 테스트 |
| `Docs\Refactoring\Compatibility.md` | 기존 PowerShell 버전과의 인수·종료 코드·JSON·저장소 호환성 표 |

## 동작 방식

```
TortoiseSVN 커밋 버튼 / ShipCommit.cmd / GUI ▶ 검증
        │
        ▼
 ValidationService (ShipCheck.Core)
  ① 커밋 파일 분류 ── 콘텐츠·설정·에디터 전용 모듈만 변경 → 건너뜀(통과)
  ② 커밋에서 빠진 로컬 코드 변경 검사 (svn add 안 한 파일/폴더 포함)
  ③ 통과 캐시 확인 ── 같은 워킹 카피 상태 + 같은 검증 범위로 이미 통과 → 즉시 통과
  ④ 프로젝트 잠금 (기다렸다면 상태를 다시 평가)
  ⑤ 게임 타깃 증분 빌드 (기본: <Project> Win64 Shipping, 컴파일+링크)
  ⑥ 변경 .cpp/.h 단독 컴파일 (-SingleFile, non-unity)
  ⑦ 빌드 전후 코드가 그대로일 때만 통과로 인정하고 캐시에 남김
        │
   성공 → 커밋 진행     실패 → 오류 요약을 커밋 창에 표시하고 커밋 중단
```

- **증분 빌드**: UBT는 타깃·구성별로 `Intermediate/Build/Win64/<Target>/Shipping`을 따로 유지합니다.
  최초 1회 풀 빌드(프라이밍) 후에는 바뀐 모듈만 다시 컴파일하고 링크합니다.
- **단독 컴파일이 필요한 이유**: unity 빌드에서는 같은 unity 파일 안의 다른 cpp가 include를 대신 해 주어서
  include 누락이 가려집니다. UBT의 adaptive unity(수정한 파일만 따로 빌드)는 **Perforce/Git만 지원하고 SVN은 지원하지 않습니다**
  (`Engine/Source/Programs/UnrealBuildTool/System/SourceFileWorkingSet.cs`). 그래서 변경 파일을 `-SingleFile`로 따로 컴파일합니다.
  UE5에서는 헤더도 단독 컴파일되므로, 헤더가 자기 완결적인지도 함께 검사됩니다.
- **모듈 판별**: 파일이 속한 `*.Build.cs`를 찾고, `.uproject`/`.uplugin`에 적힌 모듈 `Type`으로 판단합니다.
  `Editor`, `EditorNoCommandlet`, `EditorAndProgram`, `UncookedOnly`, `Program`은 게임 타깃에 포함되지 않으므로 건너뜁니다.
  `Developer`는 Shipping/Test만 검사할 때 건너뜁니다. 모듈을 판별할 수 없으면 보수적으로 빌드합니다.
- **SVN 상태**: `svn info --xml`로 워킹 카피인지 먼저 확인하고 `svn status -v --xml`로 읽습니다.
  svn이 없거나 워킹 카피가 아니면 "변경 없음"이 아니라 **"상태 모름"** 입니다 (GUI는 "변경 사항 확인 불가"로 표시).
- **통과 캐시**: 다음이 모두 같을 때만 이전 통과를 재사용합니다.
  - 엔진, 타깃, 검증 정책(단독 컴파일 설정, UBT 추가 인자 배열 등)
  - `Source`/`Plugins`/`.uproject`의 모든 코드 파일의 svn 상태·속성 상태·리비전. 부분 업데이트나 개별 파일 커밋으로 기준이 바뀐 경우도 감지합니다.
  - 수정된 코드 파일과 미등록 코드 파일(미등록 폴더 안의 파일 포함)의 내용
  - 이번 검사가 단독 컴파일해야 할 파일이 같은 상태에서 이미 검증되었는지. 전체 변경을 검증한 뒤 일부만 커밋하면 즉시 통과하지만, A만 검증하고 B를 커밋하면 다시 검사합니다.
  - 상태 키는 캐시를 꺼도 계산합니다. 그래서 `UseCache: false` 여도 GUI가 현재 상태의 실패를 표시합니다.
- **검증 중 변경 감지**: 빌드 전후로 상태 키와 범위 내 모든 코드 파일의 수정 시각·크기를 비교합니다. 다르면 빌드가 어떤 내용을 컴파일했는지 확정할 수 없으므로
  통과로 인정하지 않고(종료 코드 2) 캐시하지 않습니다. 바꿨다가 같은 내용으로 되돌린 경우도 수정 시각으로 잡습니다.

### 이 도구가 잡아내는 대표적인 문제
| 증상 | 원인 예 |
|---|---|
| 정의되지 않은 식별자 | `GEditor`, `UEditorEngine`, `WITH_EDITORONLY_DATA` 멤버를 `#if WITH_EDITOR` 없이 사용 |
| LNK2019 | 에디터 전용 함수 호출, Shipping에서 컴파일 제외된 코드 참조 |
| Shipping 전용 오류 | `#if !UE_BUILD_SHIPPING`으로 선언한 변수나 함수를 가드 밖에서 사용 |
| 모듈 의존성 오류 | 런타임 모듈 `Build.cs`가 `UnrealEd` 등 에디터 모듈에 무조건 의존 |
| include 누락 | unity 빌드에 가려져 있다가 파일 구성이 바뀌면 깨지는 경우 (⑥단계) |

## 종료 코드와 결과

| 종료 코드 | 결과 | 커밋 |
|---|---|---|
| 0 | `Pass`(빌드 통과), `Cached`(캐시 통과), `Skipped`(검증 대상 없음/우회 태그 — 검증된 통과는 아님) | 허용 |
| 1 | `Fail` — 빌드 실패, 또는 `UncommittedSourceChanges: "Fail"` 정책 위반 | 차단 |
| 2 | `Error` / `Canceled` — 설정 오류, 엔진 없음, 시간 초과, 사용자 중지, 검증 중 코드 변경, SVN 상태를 모르는데 엄격 정책 | 차단 |

결과 JSON(`--result-json`)과 `Saved/ShipCheck/History.jsonl`에는 실행 ID, 단계별 종료 코드, 구조화된 진단(`Code`, `Stage`, `File`, `Line`, `ToolCode` ...), 캐시 재사용 여부, 커밋 허용 여부가 들어갑니다.
실패 여부는 항상 **외부 도구 종료 코드**로 정하고, 로그 파서는 파일·줄 정보를 덧붙이는 데만 씁니다.

## 설치

### 1. 배포 폴더 만들기
```
Publish.cmd                      → dist\ShipCheck\ (실행 파일 + DLL + 설정 + .cmd 진입점)
```
이 폴더를 **프로젝트 밖** 원하는 위치에 두고 팀에 배포합니다. 소스 트리나 PowerShell 스크립트는 필요 없습니다.
각 PC에는 **.NET 8 이상 Desktop Runtime**이 필요합니다. svn 상태 확인과 `ShipCommit`에는 TortoiseSVN의 "command line client tools"(`svn.exe`)가 필요합니다.

소스 트리에서 바로 쓰려면 `Build.cmd`(→ `Bin\`)로 빌드합니다. 실행 파일은 자기 폴더, 없으면 상위 폴더의 `ShipCheck.config.json`을 설정 폴더로 사용합니다.

(아래 예시의 도구 위치는 `D:\Tools\ShipCheck`, 프로젝트는 `D:\Work\MyGame\MyGame.uproject`입니다.)

- `--project`에는 `.uproject` 파일이나 그 파일이 있는 폴더를 줄 수 있습니다. 생략하면 설정의 `UProject`, 그것도 없으면 워킹 카피에서 `.uproject`를 자동으로 찾습니다.
- 엔진은 `.uproject`의 `EngineAssociation`(소스 빌드 GUID, 런처 버전, 상대 경로)으로 자동으로 찾습니다.
- 로그·캐시·기록은 프로젝트의 `Saved/ShipCheck/`에 저장됩니다. 프로젝트에 추가되는 파일은 이것뿐이며, `Saved/`는 보통 이미 `svn:ignore`에 들어 있습니다.

**설정 파일 선택 순서** (도구 하나로 여러 프로젝트를 검사할 수 있습니다. 프로젝트를 자동 탐색한 경우에도 같은 순서)
1. `--config`로 지정한 파일
2. 도구 폴더의 `ShipCheck.<프로젝트명>.config.json` (예: `ShipCheck.MyGame.config.json`)
3. 도구 폴더의 `ShipCheck.config.json`

### 2. 최초 1회 프라이밍 (풀 Shipping 빌드)
```
D:\Tools\ShipCheck\ShipCheck.cmd validate --project "D:\Work\MyGame\MyGame.uproject" --all
```
(GUI ▶ 검증 ▸ 전체 빌드와 같습니다.) 소스 빌드 엔진이면 처음에는 엔진 전체를 Shipping으로 빌드하므로 오래 걸립니다. 이후로는 증분 빌드입니다.

### 3. TortoiseSVN 훅 등록 (각 개발자 PC)
TortoiseSVN → Settings → **Hook Scripts** → Add... (GUI ▸ 도구 ▸ "커밋 훅 명령 복사"로 명령을 얻을 수 있습니다)
| 항목 | 값 |
|---|---|
| Hook Type | **Pre-Commit Hook** |
| Working Copy Path | 워킹 카피 루트 (예: `D:\Work\MyGame`) |
| Command Line To Execute | `"D:\Tools\ShipCheck\TortoiseSVN-PreCommit.cmd" "D:\Work\MyGame\MyGame.uproject"` |
| Wait for the script to finish | ✅ 체크 (필수) |
| Hide the script while running | ⬜ 해제 권장 (빌드 진행 상황 콘솔 표시) |

TortoiseSVN은 위 명령줄 뒤에 자체 인자(경로 목록, 깊이, 메시지 파일, 워킹 카피)를 붙여서 실행합니다.
그래서 프로젝트 경로는 첫 번째 인자로 넣습니다. 프로젝트 인자를 빼면 워킹 카피에서 자동으로 찾습니다. 워킹 카피가 여러 개면 워킹 카피마다 훅을 하나씩 등록합니다.
검사에 실패하면 TortoiseSVN 커밋 창에 오류 요약(파일·줄·에러코드, 전체 로그 경로)이 표시되고 커밋이 중단됩니다.

### 4. 명령줄 사용자
`svn.exe`에는 클라이언트 훅이 없으므로 `svn commit` 대신 다음을 사용합니다. 검증 종료 코드가 0이 아니면 `svn commit`을 호출하지 않습니다.
```
cd D:\Work\MyGame
D:\Tools\ShipCheck\ShipCommit.cmd -m "커밋 메시지" [--project P] [--force] [경로...]
```

### 기존 PowerShell 버전에서 옮기기
- TortoiseSVN 훅 등록(`TortoiseSVN-PreCommit.cmd` 경로와 인자)은 그대로 둬도 됩니다. 새 배포 폴더로 옮겼다면 등록된 경로만 바꿉니다.
- `ShipCheck.config.json`, `ShipCheck.<프로젝트명>.config.json`은 형식이 같으므로 새 폴더로 복사합니다.
- `ShipCheck.cmd -Project ... -Paths ... -All` 같은 기존 인수 형식도 계속 받습니다.
- 통과 캐시는 형식이 바뀌어 한 번 무효가 됩니다 (첫 커밋 때 다시 빌드). 실행 기록과 설정은 그대로 유지됩니다.
- 자세한 차이는 `Docs\Refactoring\Compatibility.md`를 보세요. 되돌리려면 기준선 사본(`CompileValidationMonitoring.baseline-20261029`)의 파일을 다시 배치하면 됩니다.

## 명령줄 (`ShipCheck.exe`)
```
ShipCheck validate [옵션] [경로...]        변경 파일 기준 검증
ShipCheck query [옵션]                     빌드 없이 상태 조회 (JSON)
ShipCheck commit -m 메시지 [옵션] [대상...]  검증 통과 시에만 svn commit
ShipCheck hook [프로젝트] PATH DEPTH MESSAGEFILE CWD   TortoiseSVN 훅
```
옵션: `--project`, `--working-copy`, `--config`, `--paths-file`(UTF-8, 한 줄에 하나), `--message-file`, `--all`, `--force`(캐시 무시), `--no-single-file`, `--dry-run`, `--result-json`, `--trigger`, `--step-timeout <분>`.
UBT 인수에 `"` `%` `!` `^`가 들어가면 cmd/Build.bat 경계에서 안전하게 전달할 수 없어 설정 오류(종료 코드 2)로 거부합니다.

## GUI — ShipCheck Monitor

```
ShipCheckMonitor.cmd [--project "D:\Work\MyGame\MyGame.uproject"] [--minimized]
```

| 영역 | 내용 |
|---|---|
| 툴바 | 프로젝트 선택·추가(＋, 또는 창에 `.uproject` 끌어 놓기), **▶ 검증** (메뉴: 캐시 무시 검증, 전체 빌드), ■ 중지, 새로고침, TortoiseSVN 업데이트·커밋, 도구(로그 폴더, 훅 명령 복사, 캐시 초기화), ⚙ 설정 |
| 상태 패널 | 큰 배지: 통과(초록) / 검증 필요(주황) / 실패(빨강) / 진행 중(파랑) / 변경 없음(회색). 프로젝트·엔진·타깃·SVN 리비전, 커밋 훅 적용 여부, 자동 검증 켜기/끄기 |
| 변경 사항 탭 | 코드 변경마다 모듈, 모듈 타입, 게임 빌드 영향 여부. 미등록 소스는 주황색. 우클릭: 편집기, TortoiseSVN 차이 보기 |
| 오류 탭 | 구조화된 컴파일·링크 오류(엔진과 같은 파서). 더블클릭하면 편집기에서 해당 줄로 이동 (VS Code, Visual Studio, Rider 등은 설정의 명령 템플릿 `{file}` `{line}`) |
| 기록 탭 | GUI, 커밋 훅, 명령줄의 모든 실행 기록 (`Saved/ShipCheck/History.jsonl`, 취소 포함). 더블클릭하면 그 실행의 로그와 오류를 불러옵니다 |
| 빌드 로그 | 실시간 출력. 오류는 빨강, 경고는 노랑으로 표시됩니다. 오류 줄을 더블클릭하면 편집기로 이동합니다 |

**모니터링**
- `Source`/`Plugins` 파일 변경을 감시해 상태를 자동으로 갱신합니다. 1분마다, 그리고 창이 활성화될 때도 다시 확인합니다.
- **자동 검증** (기본 꺼짐): 코드 변경 뒤 설정한 유휴 시간(기본 120초)이 지나면 백그라운드에서 검증합니다. 그 결과 커밋할 때 훅이 캐시로 즉시 통과합니다.
  - 언리얼 에디터가 실행 중이면 기본적으로 미룹니다. 다른 UBT 빌드(IDE, Live Coding)가 돌고 있으면 끝날 때까지 기다립니다.
  - 같은 상태로 실패하면 코드가 바뀔 때까지 다시 시도하지 않습니다.
- 트레이 상주: 창을 닫으면 트레이로 내려가고, 아이콘 색이 상태를 나타냅니다. 실패하면 알림을 표시합니다.
- **중지**: Build.bat 과 그 자손(UBT, 컴파일러, 링커)을 Windows Job Object로 묶어 둡니다. Build.bat은 Job에 들어간 뒤에야 시작합니다(stdin 시작 게이트).
  중지하면 모두 종료하고 남은 프로세스가 0개인지 확인합니다. GUI가 비정상 종료돼도 하위 프로세스는 함께 종료됩니다.
- **커밋 훅 표시**: TortoiseSVN 훅 등록을 해석해 종류(Pre-commit), Wait, 워킹 카피 경로, 프로젝트 인자가 모두 맞을 때만 "적용됨"으로 표시합니다. 확인할 수 없으면 "적용 여부 미확인"으로 표시하고, 커밋 차단을 보장한다고 안내하지 않습니다.

## 설정 (`ShipCheck.config.json`)
| 키 | 기본값 | 설명 |
|---|---|---|
| `UProject` | `""` | `--project` 인자가 없을 때 사용 (`.uproject` 또는 폴더, 설정 파일 기준 상대 경로 가능). 비우면 자동 탐색 |
| `EngineDir` | `""` | 비우면 `EngineAssociation`으로 탐색 |
| `Targets` | `[{Name:"", Win64, Shipping}]` | `Name`을 비우면 프로젝트 이름. 예: `MyGameClient`, `MyGameServer`, `Test` 구성 추가 |
| `ExtraUbtArgs` | `[]` | UBT 추가 인자 (배열 항목 하나가 인자 하나) |
| `SingleFileCheck.Enabled` | `true` | 변경 파일 단독 컴파일 |
| `SingleFileCheck.IncludeHeaders` | `true` | 헤더도 단독 컴파일 (UE5) |
| `SingleFileCheck.MaxFiles` | `30` | 단독 컴파일 최대 파일 수 (넘으면 경고 진단) |
| `SkipEditorOnlyChanges` | `true` | 에디터 전용 모듈만 바뀐 커밋은 빌드 생략 |
| `UncommittedSourceChanges` | `"Warn"` | 커밋에서 빠진 로컬 코드 수정이 있을 때 `Warn` / `Fail`. `Fail`이면 SVN 상태를 확인할 수 없을 때도 차단 |
| `SkipTag` | `"[skip-shipcheck]"` | 커밋 메시지에 넣으면 검사 생략 (긴급 대응용, 사용 사실과 메시지를 기록). `""`이면 우회 불가 |
| `UseCache` | `true` | 같은 워킹 카피 상태로 통과한 기록이 있으면 재빌드 생략 |
| `MaxErrorLines` | `25` | 실패 메시지에 표시할 오류 줄 수 |

설정 파일 형식이 잘못되면 기본값으로 바꾸지 않고 설정 오류(종료 코드 2)로 알립니다.

## 알아 둘 점
- **빌드는 워킹 카피 전체 기준입니다.** 커밋에 넣지 않은 로컬 수정이 빌드를 통과시키는 경우가 있을 수 있어서,
  검사할 때 이를 찾아 경고합니다. 팀 규칙을 엄격하게 하려면 `UncommittedSourceChanges: "Fail"`로 설정합니다.
- **검증 범위**: "통과"는 *현재 워킹 카피*가 게임 타깃 빌드를 통과했다는 뜻입니다. 커밋에서 빠지는 파일이 있으면 저장소 상태와 다를 수 있습니다.
- **검증 중 변경 감지의 한계**: 입력 스냅샷으로 빌드하는 방식이 아니라 수정 시각·크기·내용 해시 비교입니다. 수정 시각을 보존하면서 같은 크기로 바꾸는 도구나, svn 확인 범위 밖(엔진 소스 등)의 변경은 놓칠 수 있습니다.
- 검사 중에도 에디터를 켜 둘 수 있습니다. 게임 타깃은 바이너리가 따로이고, Live Coding의 빌드 차단은 에디터 타깃에만 적용됩니다.
  다른 UBT가 실행 중이면 `-WaitMutex`로 끝날 때까지 기다립니다.
- 실행이 끝난 뒤 결과는 `Saved/ShipCheck/LastResult.txt`, 전체 빌드 로그는 `Saved/ShipCheck/Logs/`에 남습니다.
- **실제 언리얼 빌드로는 아직 검증하지 않았습니다.** 자동 테스트는 임시 SVN 저장소와 모의 UBT(`ShipCheck.FakeBuild`)로 합니다. UBT 옵션(`-SingleFile`, `-WaitMutex`)과 Build.bat 동작은 UE 5.8 소스로 확인했습니다. 실제 프로젝트에 처음 적용할 때는 다음을 확인해 주세요.
  - 프라이밍 빌드 결과
  - 실제 오류 출력이 오류 탭에 제대로 파싱되는지 (컴파일러 출력 인코딩 포함)
  - 성공 / 컴파일 실패 / 중지 각각의 동작
  - TortoiseSVN 훅의 "적용됨" 표시와 커밋 차단

## 테스트
```
Test.cmd            (= dotnet test ShipCheck.sln -c Release)
```
svn.exe / svnadmin.exe가 PATH에 있어야 합니다. 실패가 하나라도 있으면 0이 아닌 종료 코드를 돌려줍니다. 테스트는 모두 제품 어셈블리를 참조합니다 (소스 복사/링크 없음).
- `ShipCheck.Core.Tests`: 설정 우선순위, 파일/모듈 분류, 정책 키, 캐시 키·적합성, SVN XML, 오류 파서, 종료 코드, 화면 상태, 명령줄/훅 인수, cmd 인용, 훅 등록 해석, 가짜 경계로 검증 순서(잠금 대기 후 재평가, 검증 중 변경, 캐시 저장 실패, 상태 불명, 취소/시간 초과)
- `ShipCheck.Integration.Tests`: 임시 실제 SVN 저장소(공백·한글 경로) + 모의 UBT 프로세스. 2차 리뷰 R2-1~R2-5, 동시 실행, 취소 시 손자 프로세스 종료, 시간 초과, 출력 파이프를 문 잔류 프로세스, 비정상 종료로 버려진 잠금, 손상된 캐시/기록, 인수 경계. 경쟁 조건은 이름 있는 이벤트로 제어합니다.
- `ShipCheck.Acceptance.Tests`: 배포 폴더 형태(PowerShell 스크립트 없음)로 `ShipCheck.exe`·`TortoiseSVN-PreCommit.cmd`·`ShipCommit.cmd`를 실제 프로세스로 실행 — JSON/종료 코드, 기존 인수 형식, 훅 인수(끝이 `\`인 경로 포함), 검증 실패 시 커밋 미실행, GUI 서비스 호출과 CLI 결과 비교.
  - `TestCategory=Legacy`: 환경 변수 `SHIPCHECK_LEGACY_SCRIPT`에 기준선 `ShipCheck.ps1`을 주면 기존 엔진과 결과를 비교합니다.
- GUI 화면 확인(검증 필요 → 실패 → 통과 → 중지)은 자동 테스트와 별도로 수동 스모크로 확인합니다.

## 권장 보완 계층
로컬 검사는 개발자가 우회(`SkipTag`)하거나 훅을 등록하지 않으면 빠질 수 있으므로 다음과 함께 쓰는 것을 권장합니다.
1. **커밋 후 CI** (Jenkins, TeamCity 등): 리비전마다 전체 타깃(Client/Server/콘솔 플랫폼)을 Shipping으로 빌드하고 실패하면 알림
2. **(선택) 서버 pre-commit 훅**: `[skip-shipcheck]` 커밋에 사유가 적혀 있는지 강제하거나, 우회 커밋을 채널에 알림
