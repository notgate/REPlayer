@echo off
setlocal enabledelayedexpansion
title RE-VM Setup

echo.
echo   ========================================
echo   RE-VM Setup - Self-Contained Installer
echo   ========================================
echo.
echo   This will download and bundle:
echo     - QEMU 9.2 (Windows x86_64)
echo     - Android-x86 9.0 (pre-rooted, no GApps)
echo     - ADB + Frida + mitmproxy
echo.
echo   Everything installs into the runtime\ directory.
echo   No external dependencies needed after setup.
echo.

set "RUNTIME_DIR=%~dp0runtime"
if not exist "%RUNTIME_DIR%" mkdir "%RUNTIME_DIR%"

:: ============================================
:: Step 1: Download QEMU for Windows
:: ============================================
echo [1/4] Downloading QEMU 9.2 for Windows...

set "QEMU_URL=https://qemu.weilnetz.de/w64/2025/qemu-w64-setup-20251224.exe"
set "QEMU_INSTALLER=%TEMP%\qemu-setup.exe"

if exist "%RUNTIME_DIR%\qemu-system-x86_64.exe" (
    echo   QEMU already installed, skipping.
    goto :android
)

echo   Downloading QEMU installer (~200MB)...
powershell -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri '%QEMU_URL%' -OutFile '%QEMU_INSTALLER%'" 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo   [ERROR] Failed to download QEMU.
    echo   Download manually from: https://qemu.weilnetz.de/w64/2025/
    echo   Run the installer and copy qemu-system-x86_64.exe + qemu-img.exe to: %RUNTIME_DIR%
    goto :android
)

:: Extract QEMU silently
echo   Extracting QEMU...
"%QEMU_INSTALLER%" /S /D="%TEMP%\qemu-install" 2>nul
if exist "%TEMP%\qemu-install\qemu-system-x86_64.exe" (
    copy "%TEMP%\qemu-install\qemu-system-x86_64.exe" "%RUNTIME_DIR%\" >nul
    copy "%TEMP%\qemu-install\qemu-img.exe" "%RUNTIME_DIR%\" >nul
    copy "%TEMP%\qemu-install\*.dll" "%RUNTIME_DIR%\" >nul 2>&1
    echo   QEMU installed successfully.
) else (
    echo   [WARN] Silent install failed. QEMU installer saved to: %QEMU_INSTALLER%
    echo   Run it manually and copy the binaries to: %RUNTIME_DIR%
)
rmdir /s /q "%TEMP%\qemu-install" 2>nul
del "%QEMU_INSTALLER%" 2>nul

:: ============================================
:: Step 2: Download Android-x86 ISO
:: ============================================
:android
echo [2/4] Downloading Android-x86 9.0...

set "ANDROID_URL=https://sourceforge.net/projects/android-x86/files/Release%%209.0/android-x86_64-9.0-r2.iso/download"
set "ANDROID_ISO=%RUNTIME_DIR%\android-x86_64-9.0-r2.iso"

if exist "%ANDROID_ISO%" (
    :: Check if it's the real ISO (>100MB) or a redirect stub
    for %%A in ("%ANDROID_ISO%") do set "SIZE=%%~zA"
    if !SIZE! GTR 104857600 (
        echo   Android-x86 ISO already downloaded, skipping.
        goto :adb
    )
    echo   Stub file detected, re-downloading...
    del "%ANDROID_ISO%" 2>nul
)

echo   Downloading Android-x86 ISO (~920MB, this may take several minutes)...
powershell -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri '%ANDROID_URL%' -OutFile '%ANDROID_ISO%' -MaximumRedirection 5" 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo   [WARN] SourceForge download failed. Trying direct mirror...
    powershell -Command "Invoke-WebRequest -Uri 'https://osdn.net/dl/android-x86/android-x86_64-9.0-r2.iso' -OutFile '%ANDROID_ISO%'" 2>nul
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Failed to download Android-x86.
        echo   Download manually from: https://www.android-x86.org/download
        echo   Place the ISO at: %ANDROID_ISO%
    )
)

:: Verify it's the real ISO
for %%A in ("%ANDROID_ISO%") do set "SIZE=%%~zA"
if !SIZE! LSS 104857600 (
    echo   [WARN] Downloaded file is too small (!SIZE! bytes). May be a redirect stub.
    echo   Download manually from: https://www.android-x86.org/download
) else (
    echo   Android-x86 downloaded (!SIZE! bytes).
)

:: ============================================
:: Step 3: Download ADB (Platform Tools)
:: ============================================
:adb
echo [3/4] Downloading Android Platform Tools (ADB)...

set "ADB_URL=https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
set "ADB_ZIP=%TEMP%\platform-tools.zip"

if exist "%RUNTIME_DIR%\adb.exe" (
    echo   ADB already installed, skipping.
    goto :frida
)

echo   Downloading platform-tools...
powershell -Command "Invoke-WebRequest -Uri '%ADB_URL%' -OutFile '%ADB_ZIP%'" 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo   [ERROR] Failed to download platform-tools.
    goto :frida
)

powershell -Command "Expand-Archive -Path '%ADB_ZIP%' -DestinationPath '%TEMP%\platform-tools' -Force"
copy "%TEMP%\platform-tools\platform-tools\adb.exe" "%RUNTIME_DIR%\" >nul
copy "%TEMP%\platform-tools\platform-tools\AdbWinApi.dll" "%RUNTIME_DIR%\" >nul
copy "%TEMP%\platform-tools\platform-tools\AdbWinUsbApi.dll" "%RUNTIME_DIR%\" >nul
del "%ADB_ZIP%" 2>nul
rmdir /s /q "%TEMP%\platform-tools" 2>nul
echo   ADB installed.

:: ============================================
:: Step 4: Download Frida Server
:: ============================================
:frida
echo [4/4] Downloading Frida server...

set "FRIDA_URL=https://github.com/frida/frida/releases/download/16.5.9/frida-server-16.5.9-android-x86_64.xz"
set "FRIDA_XZ=%TEMP%\frida-server.xz"

if exist "%RUNTIME_DIR%\frida-server-16.5.9-android-x86_64" (
    echo   Frida server already downloaded, skipping.
    goto :done
)

echo   Downloading Frida server...
powershell -Command "Invoke-WebRequest -Uri '%FRIDA_URL%' -OutFile '%FRIDA_XZ%'" 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo   [WARN] Could not download Frida server. You can add it later.
    goto :done
)

:: Extract .xz (need 7z or tar)
where 7z >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    7z x "%FRIDA_XZ%" -o"%RUNTIME_DIR%" -y >nul 2>&1
    echo   Frida server extracted.
) else (
    where tar >nul 2>&1
    if !ERRORLEVEL! EQU 0 (
        tar -xf "%FRIDA_XZ%" -C "%RUNTIME_DIR%" 2>nul
        echo   Frida server extracted.
    ) else (
        echo   [WARN] Cannot extract .xz (need 7z or tar). Frida server saved as .xz.
        copy "%FRIDA_XZ%" "%RUNTIME_DIR%\frida-server.xz" >nul
    )
)
del "%FRIDA_XZ%" 2>nul

:: ============================================
:: Done
:: ============================================
:done
echo.
echo   ========================================
echo   Setup complete!
echo   ========================================
echo.
echo   Runtime directory: %RUNTIME_DIR%
echo.
echo   Files installed:
dir /b "%RUNTIME_DIR%" 2>nul
echo.
echo   You can now run ReVM.exe
echo.
pause
