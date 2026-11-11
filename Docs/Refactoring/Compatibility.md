# ShipCheck 호환성 표 (단계 1)

기준선: `ShipCheck.ps1` R2 수정본 (SHA256 `0D3119B9…D255`, 사본 `D:\MazeLineGit\CompileValidationMonitoring.baseline-20261029`).
새 엔진(C# Core/Infrastructure/CLI)은 아래 계약을 유지하고, 의도적으로 바꾼 점은 "변경" 열에 적는다.

## 1. 진입점과 인수

| 기존 | 새 엔진 | 비고 |
|---|---|---|
| `ShipCheck.ps1 -Project P -Paths a b` | `ShipCheck.exe validate --project P a b` | 경로는 `--working-copy`(없으면 현재 폴더) 기준 상대 경로 허용 |
| `-PathListFile F` | `--paths-file F` | UTF-8, 한 줄에 하나 (TortoiseSVN PATH 파일과 같은 형식) |
| `-MessageFile F` | `--message-file F` | 우회 태그 검사용, UTF-8 |
| `-WorkingCopy W` | `--working-copy W` | |
| `-ConfigPath C` | `--config C` | 명시하면 최우선 |
| `-All` / `-Force` / `-NoSingleFile` / `-DryRun` | `--all` / `--force` / `--no-single-file` / `--dry-run` | |
| `-Query -ResultJson F` | `ShipCheck.exe query --result-json F` | |
| `-ResultJson F` | `--result-json F` | |
| `-Trigger T` | `--trigger T` | hook / cli / gui / auto |
| `-StartGate` (GUI 전용) | 없음 | GUI 가 Core 를 직접 호출하므로 필요 없음 |
| `TortoiseSVN-PreCommit.cmd [project] PATH DEPTH MESSAGEFILE CWD` | 동일 (.cmd 는 `ShipCheck.exe hook` 으로 인수만 전달) | 기존 TortoiseSVN 등록 그대로 동작 |
| `ShipCommit.ps1 -m MSG [-Project P] [-Force] [targets]` | `ShipCheck.exe commit -m MSG [--project P] [--force] [targets]` | `ShipCommit.cmd` 는 인수 전달만 |
| `ShipCheck.cmd` | 동일 이름, `ShipCheck.exe validate` 로 전달 | |

## 2. 종료 코드

| 코드 | 기존 의미 | 새 엔진 | 커밋 허용 |
|---|---|---|---|
| 0 | Pass / Cached / Skipped | 동일 | 예 |
| 1 | Fail (빌드 실패, Fail 정책의 누락 파일) | 동일 | 아니오 |
| 2 | Error (예외, 검증 중 코드 변경) | Error + **Canceled + 시간 초과 + 상태 불명(엄격 정책)** | 아니오 |
| 3 | StartGate 신호 없음 (GUI 내부) | 없음 | - |

`commit` 명령은 검증 종료 코드가 0 이 아니면 `svn commit` 을 호출하지 않고 그 코드를 그대로 반환한다.

## 3. 결과 (`Result` 문자열)

| 값 | 의미 | 표시 |
|---|---|---|
| Pass | 이번 실행에서 빌드 통과 | 통과 |
| Cached | 같은 상태 키 + 검증 범위로 통과한 기록 재사용 | 통과(캐시) |
| Skipped | 검증하지 않음 (영향 없는 변경 / 경로 없음 / 우회 태그) — **검증된 통과가 아님** | 건너뜀 |
| Fail | 빌드 실패 또는 엄격 정책 위반 | 실패 |
| Error | 실행할 수 없음 / 결과를 확정할 수 없음 (`ErrorKind` 로 구분) | 오류 |
| Canceled | 사용자 취소 (기존: GUI 만 표시, 기록 없음) | 취소 — **변경: 기록에 남김** |

## 4. 결과 JSON / History.jsonl

기존 필드는 이름과 의미를 유지한다: `Version, Trigger, StartTime, Result, Reason, ElapsedSeconds, Project, ProjectDir, EngineRoot, Targets[], Revision, StateKey, Files[], Steps[{Label, ExitCode, Log, ErrorCount, Errors[], Tail[]}]`.

추가 필드 (Version = 2): `RunId, EndTime, ExitCode, CommitAllowed, CacheReused, ErrorKind, SingleFiles[], Diagnostics[{Code, Severity, Stage, Message, Detail, File, Line, Column, ToolCode}]`.
GUI 는 Version 1 줄도 계속 읽는다. Skipped 는 우회 태그일 때만 기록한다 (기존과 동일).

Query JSON: 기존 필드(`Project, ProjectDir, ProjectName, EngineRoot, ConfigPath, ToolDir, Targets[{Name, Platform, Configuration, Primed}], SvnAvailable, WcRoot, Revision, StateKey, UpToDate, Changes[{Path, Status, Kind, Module, ModuleType, Affects}]`) 유지.
추가: `SvnState` (Available / ToolMissing / NotWorkingCopy / Failed), `SvnError`, `Diagnostics[]`, `Error` 는 조회 자체가 실패한 경우만.

## 5. 설정 파일

형식 변경 없음 (`ShipCheck.config.json`, `ShipCheck.<프로젝트명>.config.json`). 우선순위: 명시 경로 > 프로젝트 전용 > 공통. 프로젝트를 자동 탐색한 경우에도 탐색 후 프로젝트 전용 설정을 다시 고른다 (R2-3).
상대 경로(`UProject`, `EngineDir`)는 설정 파일 폴더 기준. 도구 폴더 = 실행 파일 폴더, 그곳에 `ShipCheck.config.json` 이 없고 상위 폴더에 있으면 상위 폴더 (기존 `도구\Bin\` 배치 호환).

## 6. 상태 저장소 (`<Project>/Saved/ShipCheck/`)

| 파일 | 기존 | 새 엔진 |
|---|---|---|
| History.jsonl | 한 줄 = 실행 기록, 기록 전용 뮤텍스 | 동일 형식(필드 추가), 같은 뮤텍스 이름 |
| PassCache.txt | `키\t파일|파일`, 머리말 없음 | **변경:** 첫 줄 `#ShipCheckPassCache 2`. 머리말 없는 기존 파일은 무효(빈 캐시)로 보고 다음 통과 때 새 형식으로 교체 |
| LastResult.txt | PASS/FAIL/ERROR 요약 | 동일 |
| Logs/*.log | 단계별 UBT 로그 | 동일 |

상태 키: 계산 방식이 바뀌므로(SVN XML, 인수 배열 인코딩, 스키마 버전) 기존 키와 호환되지 않는다. 기존 통과 캐시는 모두 무효가 되고 GUI 의 실패 표시는 새 실행부터 적용된다.

## 7. 잠금

프로젝트 잠금 `Local\ShipCheck_<sha1(소문자 .uproject 경로)>`, 기록 잠금 `Local\ShipCheck_History_<sha1>` — 이름 유지. 전환 기간에 구 스크립트와 새 엔진이 섞여 실행돼도 서로 직렬화된다 (단, 캐시 형식이 달라 서로의 통과 기록은 재사용하지 않는다).

## 8. 의도한 동작 차이

1. 취소/시간 초과는 종료 코드 2, 결과 `Canceled` 또는 `Error(Timeout)` 로 기록된다.
2. SVN 상태 불명 + `UncommittedSourceChanges=Fail` 이면 누락 검사를 할 수 없으므로 Error(2). `Warn` 이면 기존처럼 빌드하되 경고 진단을 남기고 캐시하지 않는다 (TortoiseSVN 명령줄 도구 미설치 환경 호환).
3. 외부 도구 종료 코드가 0 이 아니면 오류 줄 파싱 결과와 무관하게 실패. 종료 코드 0 이면 오류처럼 보이는 줄이 있어도 통과 (기존과 동일, 진단에 경고로만 남김).
4. UBT 인수에 `"` `%` `!` 가 들어가면 cmd 경계에서 안전하게 전달할 수 없으므로 설정 오류(Error)로 거부한다.
5. 빌드 중 변경 감지: 상태 키 + 범위 내 코드 파일 수정 시각/크기 비교 (기존 R2-1 과 동일한 보장 범위). 입력 스냅샷이 아니므로 수정 시각을 보존하며 같은 크기로 바꾸는 변경은 놓칠 수 있다.
6. `--config` 로 지정한 파일이 없거나 설정 JSON 형식이 잘못되면 기본값으로 진행하지 않고 설정 오류(2).
7. `hook` 명령은 원본 명령줄을 cmd 방식(따옴표는 묶기만, 역슬래시는 그대로)으로 나눈다. `"D:\Work\MyGame\"` 처럼 `\` 로 끝나는 프로젝트 폴더 인수도 기존 .cmd 처럼 동작한다.
8. 산출물 폴더(Intermediate/Binaries/Saved/DerivedDataCache) 판정은 프로젝트 폴더 기준 상대 경로로 한다 (프로젝트 위쪽 폴더 이름이 `Saved` 여도 제외하지 않음).
9. 기존 엔진의 알려진 결함은 옮기지 않았다: 공백/한글 경로에서 PowerShell 5.1 `Get-Content` 가 UTF-8 PassCache 를 ANSI 로 읽어 캐시가 적중하지 않던 문제 (동등성 테스트 `한글_경로에서는_기존_엔진의_캐시_결함만_다르다`).
10. 취소 기록(`Canceled`)은 오류 탭에 표시하지 않는다 (마지막 출력 줄은 오류가 아님).

## 9. 동등성 비교 결과 (ASCII 경로, 기존 엔진 = 기준선 사본)

| 시나리오 | 기존 | 새 엔진 |
|---|---|---|
| 콘텐츠만 변경 | Skipped/0 | Skipped/0 |
| 에디터 모듈만 변경 | Skipped/0 | Skipped/0 |
| 런타임 변경 | Pass/0 | Pass/0 |
| 같은 상태 재검사 | Cached/0 | Cached/0 |
| 단독 컴파일 실패 | Fail/1 | Fail/1 |
| 우회 태그 | Skipped/0 | Skipped/0 |
| 미등록 소스 + Fail 정책 | Fail/1 | Fail/1 |
| 타깃 빌드 실패 | Fail/1 | Fail/1 |
| 경로 없음 | Skipped/0 | Skipped/0 |

검증 대상 파일 목록(Files)도 모든 단계에서 같다. 공백/한글 경로에서는 "같은 상태 재검사" 만 기존 Pass / 새 Cached (위 9번 결함).
