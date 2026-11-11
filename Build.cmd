@echo off
rem Builds ShipCheck.exe (CLI / hook) and ShipCheckMonitor.exe (GUI) into Bin\ of the source tree.
rem Requires .NET 8 SDK or later. Users need the .NET 8+ Desktop Runtime.
dotnet publish "%~dp0src\ShipCheck.Cli\ShipCheck.Cli.csproj" -c Release -o "%~dp0Bin" --nologo || exit /b 1
dotnet publish "%~dp0Gui\ShipCheckMonitor\ShipCheckMonitor.csproj" -c Release -o "%~dp0Bin" --nologo || exit /b 1
exit /b 0
