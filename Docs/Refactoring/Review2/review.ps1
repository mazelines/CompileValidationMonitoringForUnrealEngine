$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$tool="$root\tool"
$wc="$root\wc"
$engine="$root\engine"
New-Item -ItemType Directory -Force $tool,"$engine\Engine\Build\BatchFiles" | Out-Null
Copy-Item 'D:\MazeLineGit\CompileValidationMonitoring\ShipCheck.ps1' "$tool\ShipCheck.ps1"
& svnadmin create "$root\repo"
$url=([uri]("$root\repo".Replace('\','/')+'/')).AbsoluteUri
& svn checkout -q $url $wc
New-Item -ItemType Directory -Force "$wc\Source\Game" | Out-Null
'{"FileVersion":3,"Modules":[{"Name":"Game","Type":"Runtime"}]}' | Set-Content -Encoding UTF8 "$wc\Game.uproject"
'// initial' | Set-Content -Encoding ASCII "$wc\Source\Game\Foo.cpp"
'// helper initial' | Set-Content -Encoding ASCII "$wc\Source\Game\Helper.h"
'// module' | Set-Content -Encoding ASCII "$wc\Source\Game\Game.Build.cs"
& svn add -q "$wc\Game.uproject" "$wc\Source"
& svn commit -q $wc -m 'review fixture'
$batch="@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$root\fake-build.ps1`"`r`nexit /b %ERRORLEVEL%`r`n"
[IO.File]::WriteAllText("$engine\Engine\Build\BatchFiles\Build.bat",$batch,[Text.Encoding]::ASCII)
@'
$root=$PSScriptRoot
if (Test-Path "$root\race.flag") {
 'ready' | Set-Content "$root\ready.flag"
 for($i=0;$i -lt 300 -and -not (Test-Path "$root\release.flag");$i++){Start-Sleep -Milliseconds 100}
}
$bad=Get-ChildItem "$root\wc\Source" -Recurse -File | Where-Object { [IO.File]::ReadAllText($_.FullName).Contains('BROKEN') }
if($bad){Write-Output 'error C2065: fixture broken';exit 1}
exit 0
'@ | Set-Content -Encoding UTF8 "$root\fake-build.ps1"
function Config([string]$name,[string]$mode='Warn') {
 @{EngineDir=$engine;SingleFileCheck=@{Enabled=$false};UseCache=$true;UncommittedSourceChanges=$mode} | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 "$tool\$name"
}
Config 'ShipCheck.config.json' 'Fail'
function Check([string]$name,[string[]]$extra=@()) {
 $argList=@('-NoProfile','-ExecutionPolicy','Bypass','-File',"$tool\ShipCheck.ps1",'-Project',"$wc\Game.uproject",'-Paths',"$wc\Source\Game\Foo.cpp",'-ResultJson',"$root\$name.json")+$extra
 $save=$ErrorActionPreference;$ErrorActionPreference='Continue'
 & powershell @argList *> "$root\$name.log"
 $code=$LASTEXITCODE;$ErrorActionPreference=$save
 $json=[IO.File]::ReadAllText("$root\$name.json")|ConvertFrom-Json
 return [pscustomobject]@{Case=$name;Exit=$code;Result=$json.Result;Reason=$json.Reason;StateKey=$json.StateKey}
}
$results=New-Object System.Collections.Generic.List[object]

# New unversioned directory hides its source files from status and strict omission policy.
'// modified' | Set-Content -Encoding ASCII "$wc\Source\Game\Foo.cpp"
New-Item -ItemType Directory "$wc\Source\Game\NewFolder" | Out-Null
'// new source GOOD' | Set-Content -Encoding ASCII "$wc\Source\Game\NewFolder\Hidden.cpp"
$results.Add((Check 'untracked-folder-strict'))
'// new source BROKEN' | Set-Content -Encoding ASCII "$wc\Source\Game\NewFolder\Hidden.cpp"
$results.Add((Check 'untracked-folder-cached'))
$results.Add((Check 'untracked-folder-forced' @('-Force')))
Remove-Item -LiteralPath "$wc\Source\Game\NewFolder\Hidden.cpp"

# Snapshot is taken before compiling; change bad source to good after snapshot, then restore bad source.
Config 'ShipCheck.config.json' 'Warn'
'// BROKEN race-original' | Set-Content -Encoding ASCII "$wc\Source\Game\Foo.cpp"
'race' | Set-Content "$root\race.flag"
$argText="-NoProfile -ExecutionPolicy Bypass -File `"$tool\ShipCheck.ps1`" -Project `"$wc\Game.uproject`" -Paths `"$wc\Source\Game\Foo.cpp`" -ResultJson `"$root\race-first.json`" -Force"
$p=Start-Process powershell -ArgumentList $argText -WindowStyle Hidden -PassThru -RedirectStandardOutput "$root\race-first.out" -RedirectStandardError "$root\race-first.err"
for($i=0;$i -lt 300 -and -not(Test-Path "$root\ready.flag");$i++){Start-Sleep -Milliseconds 100}
if(-not(Test-Path "$root\ready.flag")){throw 'Race synchronization failed'}
'// GOOD race-replacement' | Set-Content -Encoding ASCII "$wc\Source\Game\Foo.cpp"
'go' | Set-Content "$root\release.flag"
if(-not $p.WaitForExit(30000)){throw 'Fixture did not exit'}
$first=[IO.File]::ReadAllText("$root\race-first.json")|ConvertFrom-Json
$results.Add([pscustomobject]@{Case='race-first';Exit=$p.ExitCode;Result=$first.Result;StateKey=$first.StateKey})
Remove-Item -LiteralPath "$root\race.flag","$root\ready.flag","$root\release.flag"
'// BROKEN race-original' | Set-Content -Encoding ASCII "$wc\Source\Game\Foo.cpp"
$results.Add((Check 'race-restored-cached'))
$results.Add((Check 'race-restored-forced' @('-Force')))

# Project-specific strict configuration is ignored during automatic project discovery.
'// GOOD config-test' | Set-Content -Encoding ASCII "$wc\Source\Game\Foo.cpp"
'// modified omitted helper' | Set-Content -Encoding ASCII "$wc\Source\Game\Helper.h"
Config 'ShipCheck.Game.config.json' 'Fail'
$results.Add((Check 'config-explicit-project'))
$save=$ErrorActionPreference;$ErrorActionPreference='Continue'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tool\ShipCheck.ps1" -WorkingCopy $wc -Paths "$wc\Source\Game\Foo.cpp" -ResultJson "$root\config-auto.json" *> "$root\config-auto.log"
$autoExit=$LASTEXITCODE;$ErrorActionPreference=$save
$auto=[IO.File]::ReadAllText("$root\config-auto.json")|ConvertFrom-Json
$results.Add([pscustomobject]@{Case='config-auto';Exit=$autoExit;Result=$auto.Result})

# An exported project must not be identified as a valid SVN WC.
$export="$root\export"
& svn export -q $url $export
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tool\ShipCheck.ps1" -Project "$export\Game.uproject" -Query -ResultJson "$root\non-wc.json" *> "$root\non-wc.log"
$q=[IO.File]::ReadAllText("$root\non-wc.json")|ConvertFrom-Json
$results.Add([pscustomobject]@{Case='non-wc-query';SvnAvailable=$q.SvnAvailable;WcRoot=$q.WcRoot;Changes=@($q.Changes).Count;StateKey=$q.StateKey})
$results | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 "$root\results.json"
$results | ConvertTo-Json -Depth 5
