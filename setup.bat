@echo off
setlocal
title REPlayer Setup

set "POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "INSTALLER=%~dp0scripts\setup\Install-REPlayer.ps1"
set "REPLAYER_EXPECTED_RUNTIME_MANIFEST_SHA256=__REPLAYER_RUNTIME_MANIFEST_SHA256__"
set "REPLAYER_EXPECTED_DISTRIBUTION_MANIFEST_SHA256=__REPLAYER_DISTRIBUTION_MANIFEST_SHA256__"

if not exist "%POWERSHELL%" (
    echo [ERROR] Windows PowerShell is unavailable: %POWERSHELL%
    exit /b 1
)
if not exist "%INSTALLER%" (
    echo [ERROR] REPlayer installer is unavailable: %INSTALLER%
    exit /b 1
)

if /i "%~1"=="--help" (
    "%POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" -SourceRoot "%~dp0." -Help
) else (
    "%POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" -SourceRoot "%~dp0." %*
)
set "SETUP_EXIT=%ERRORLEVEL%"
if not "%SETUP_EXIT%"=="0" (
    echo.
    echo REPlayer setup failed. Details: %TEMP%\REPlayer-setup.log
)
exit /b %SETUP_EXIT%
