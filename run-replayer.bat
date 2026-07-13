@echo off
setlocal
set "POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "INSTALLER=%~dp0scripts\setup\Install-REPlayer.ps1"
if not exist "%POWERSHELL%" (
    echo [ERROR] Windows PowerShell is unavailable.
    exit /b 1
)
if not exist "%INSTALLER%" (
    echo [ERROR] REPlayer launcher is incomplete. Run setup.bat from the release package.
    exit /b 1
)
"%POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" -SourceRoot "%~dp0." -LaunchOnly
exit /b %ERRORLEVEL%
