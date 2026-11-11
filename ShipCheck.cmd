@echo off
rem Manual run. Forwards arguments to ShipCheck.exe (validate / query / commit / hook).
rem First-time priming (full Shipping build):
rem   ShipCheck.cmd validate --project "D:\Work\MyGame\MyGame.uproject" --all
rem Old ShipCheck.ps1 style arguments (-Project, -Paths, -All ...) are still accepted.
set "SHIPCHECK_EXE=%~dp0ShipCheck.exe"
if not exist "%SHIPCHECK_EXE%" set "SHIPCHECK_EXE=%~dp0Bin\ShipCheck.exe"
if not exist "%SHIPCHECK_EXE%" (
    echo ShipCheck: ShipCheck.exe not found next to %~nx0 or in Bin\. Run Build.cmd first. 1>&2
    exit /b 2
)
"%SHIPCHECK_EXE%" %*
exit /b %ERRORLEVEL%
