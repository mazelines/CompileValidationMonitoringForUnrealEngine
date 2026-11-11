@echo off
rem Runs every automated test (Core unit, Integration with a temporary SVN repo + fake UBT, Acceptance via ShipCheck.exe).
rem Needs svn.exe / svnadmin.exe (TortoiseSVN command line tools) on PATH. Returns non-zero when any test fails.
dotnet test "%~dp0ShipCheck.sln" -c Release %*
exit /b %ERRORLEVEL%
