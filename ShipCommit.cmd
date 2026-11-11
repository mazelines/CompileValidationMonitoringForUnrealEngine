@echo off
rem Command-line replacement for "svn commit": ShipCommit.cmd -m "message" [--project P] [--force] [paths...]
rem Commits only when the ShipCheck validation passes. Forwards arguments to "ShipCheck.exe commit".
set "SHIPCHECK_EXE=%~dp0ShipCheck.exe"
if not exist "%SHIPCHECK_EXE%" set "SHIPCHECK_EXE=%~dp0Bin\ShipCheck.exe"
if not exist "%SHIPCHECK_EXE%" (
    echo ShipCheck: ShipCheck.exe not found next to %~nx0 or in Bin\. Run Build.cmd first. 1>&2
    exit /b 2
)
"%SHIPCHECK_EXE%" commit %*
exit /b %ERRORLEVEL%
