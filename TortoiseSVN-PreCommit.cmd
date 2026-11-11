@echo off
rem TortoiseSVN Pre-commit hook entry point. Only forwards arguments to "ShipCheck.exe hook".
rem Command line to register in TortoiseSVN:
rem   "<ShipCheck dir>\TortoiseSVN-PreCommit.cmd" "D:\Work\MyGame\MyGame.uproject"
rem TortoiseSVN then appends: PATH DEPTH MESSAGEFILE CWD
rem Non-zero exit code aborts the commit and shows stderr in the commit dialog.
set "SHIPCHECK_EXE=%~dp0ShipCheck.exe"
if not exist "%SHIPCHECK_EXE%" set "SHIPCHECK_EXE=%~dp0Bin\ShipCheck.exe"
if not exist "%SHIPCHECK_EXE%" (
    echo ShipCheck: ShipCheck.exe not found next to %~nx0 or in Bin\. Run Build.cmd or reinstall. 1>&2
    exit /b 2
)
"%SHIPCHECK_EXE%" hook %*
exit /b %ERRORLEVEL%
