@echo off
rem Creates a standalone deployment folder (no source tree, no PowerShell scripts needed):
rem   Publish.cmd [output folder]     default: dist\ShipCheck
rem The folder holds ShipCheck.exe, ShipCheckMonitor.exe, their DLLs, ShipCheck.config.json and the .cmd entry points.
rem Target PCs need the .NET 8+ Desktop Runtime (framework-dependent build).
setlocal
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%~dp0dist\ShipCheck"
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish "%~dp0src\ShipCheck.Cli\ShipCheck.Cli.csproj" -c Release -o "%OUT%" --nologo || exit /b 1
dotnet publish "%~dp0Gui\ShipCheckMonitor\ShipCheckMonitor.csproj" -c Release -o "%OUT%" --nologo || exit /b 1
for %%F in (ShipCheck.config.json TortoiseSVN-PreCommit.cmd ShipCheck.cmd ShipCommit.cmd ShipCheckMonitor.cmd README.md) do (
    copy /y "%~dp0%%F" "%OUT%\" >nul || exit /b 1
)
del /q "%OUT%\*.pdb" 2>nul
echo Published to %OUT%
exit /b 0
