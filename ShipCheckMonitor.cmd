@echo off
rem Launches ShipCheck Monitor (GUI). In the source tree it builds Bin\ first if missing (needs .NET 8+ SDK).
rem Options: --project "D:\Work\MyGame\MyGame.uproject"  --minimized
if exist "%~dp0ShipCheckMonitor.exe" (
    start "" "%~dp0ShipCheckMonitor.exe" %*
    exit /b 0
)
if not exist "%~dp0Bin\ShipCheckMonitor.exe" (
    call "%~dp0Build.cmd" || exit /b 1
)
start "" "%~dp0Bin\ShipCheckMonitor.exe" %*
