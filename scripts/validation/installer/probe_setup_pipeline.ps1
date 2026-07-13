[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$installer = Join-Path $root 'scripts\setup\Install-REPlayer.ps1'
$builder = Join-Path $root 'scripts\setup\New-REPlayerDistribution.ps1'
$work = Join-Path $env:TEMP ("REPlayer-setup-probe-" + $PID)
$payload = Join-Path $work 'payload'
$install = Join-Path $work 'installed'
$rejected = Join-Path $work 'rejected'
$unexpected = Join-Path $work 'unexpected'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Set-FixtureFile([string]$RelativePath, [string]$Content) {
    $path = Join-Path $payload $RelativePath
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    [System.IO.File]::WriteAllText($path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function New-Manifest([string]$ManifestRoot, [string]$ManifestPath, [string[]]$RelativeFiles, [string]$Kind, [string[]]$MutableFiles = @()) {
    $entries = @()
    foreach ($relative in ($RelativeFiles | Sort-Object)) {
        $path = Join-Path $ManifestRoot $relative
        $entries += [ordered]@{
            path = $relative.Replace('\','/')
            bytes = (Get-Item -LiteralPath $path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    [long]$totalBytes = 0
    foreach ($entry in $entries) { $totalBytes += [long]$entry['bytes'] }
    $manifest = [ordered]@{
        schema = 1
        product = 'REPlayer'
        packageKind = $Kind
        packageVersion = 'fixture'
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        totalBytes = $totalBytes
        files = $entries
        mutableFiles = $MutableFiles
    }
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $ManifestPath)) | Out-Null
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
}

function Invoke-Installer([string]$Destination) {
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installer `
        -SourceRoot $payload -InstallDirectory $Destination -NoLaunch -SkipHostChecks -SkipShortcut -UseHardLinks | Out-Host
    [int]$code = $LASTEXITCODE
    return $code
}

try {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($payload) | Out-Null

    Set-FixtureFile 'REPlayer.exe' 'fixture-app'
    Set-FixtureFile 'runtime\google-emulator\persona-emulator-37.1.7\emulator.exe' 'fixture-emulator'
    Set-FixtureFile 'runtime\google-emulator\persona-emulator-37.1.7\emulator-check.exe' 'fixture-emulator-check'
    Set-FixtureFile 'runtime\google-emulator\persona-emulator-37.1.7\qemu-img.exe' 'fixture-qemu-img'
    Set-FixtureFile 'runtime\google-emulator\sdk\platform-tools\adb.exe' 'fixture-adb'
    Set-FixtureFile 'runtime\google-emulator\sdk\system-images\android-34\google_apis\x86_64\system.img' 'fixture-system'
    Set-FixtureFile 'runtime\google-emulator\avd-home\ReVM.avd\replayer-baseline.json' '{"schema":1,"mode":"stealth","targetAvd":"ReVM"}'
    Set-FixtureFile 'runtime\google-emulator\avd-home\ReVM.avd\config.ini' 'image.sysdir.1=stale'
    Set-FixtureFile 'runtime\google-emulator\avd-home\ReVM.ini' 'path=stale'

    $runtimeFiles = @(
        'google-emulator\persona-emulator-37.1.7\emulator.exe',
        'google-emulator\persona-emulator-37.1.7\emulator-check.exe',
        'google-emulator\persona-emulator-37.1.7\qemu-img.exe',
        'google-emulator\sdk\platform-tools\adb.exe',
        'google-emulator\sdk\system-images\android-34\google_apis\x86_64\system.img',
        'google-emulator\avd-home\ReVM.avd\replayer-baseline.json'
    )
    New-Manifest (Join-Path $payload 'runtime') (Join-Path $payload 'runtime\replayer-runtime-manifest.json') $runtimeFiles 'published-api34-runtime' @(
        'google-emulator/avd-home/ReVM.ini',
        'google-emulator/avd-home/ReVM.avd/config.ini'
    )
    New-Manifest $payload (Join-Path $payload 'replayer-distribution-manifest.json') @('REPlayer.exe') 'self-contained-win-x64-application'

    $sourceIniBefore = Get-Content -LiteralPath (Join-Path $payload 'runtime\google-emulator\avd-home\ReVM.ini') -Raw
    $sourceConfigBefore = Get-Content -LiteralPath (Join-Path $payload 'runtime\google-emulator\avd-home\ReVM.avd\config.ini') -Raw
    $positiveExit = Invoke-Installer $install
    if ($positiveExit -ne 0) { throw "Fixture install failed with exit code $positiveExit." }
    if (-not (Test-Path -LiteralPath (Join-Path $install 'REPlayer.exe'))) { throw 'Installed executable is missing.' }
    $ini = Get-Content -LiteralPath (Join-Path $install 'runtime\google-emulator\avd-home\ReVM.ini') -Raw
    $config = Get-Content -LiteralPath (Join-Path $install 'runtime\google-emulator\avd-home\ReVM.avd\config.ini') -Raw
    if ($ini -notmatch [regex]::Escape((Join-Path $install 'runtime\google-emulator\avd-home\ReVM.avd'))) {
        throw 'Installed AVD descriptor was not relocated.'
    }
    $expectedImagePath = (Join-Path $install 'runtime\google-emulator\sdk\system-images\android-34').Replace('\','\\')
    if (-not $config.Contains($expectedImagePath)) {
        throw "Installed AVD image.sysdir was not relocated: $config"
    }
    if ((Get-Content -LiteralPath (Join-Path $payload 'runtime\google-emulator\avd-home\ReVM.ini') -Raw) -ne $sourceIniBefore -or
        (Get-Content -LiteralPath (Join-Path $payload 'runtime\google-emulator\avd-home\ReVM.avd\config.ini') -Raw) -ne $sourceConfigBefore) {
        throw 'Installer path repair mutated the source payload through a hard link.'
    }

    Add-Content -LiteralPath (Join-Path $payload 'runtime\google-emulator\sdk\platform-tools\adb.exe') -Value 'tampered'
    $negativeExit = Invoke-Installer $rejected
    if ($negativeExit -eq 0) { throw 'Tampered runtime payload was accepted.' }

    Set-FixtureFile 'runtime\google-emulator\sdk\platform-tools\adb.exe' 'fixture-adb'
    Set-FixtureFile 'runtime\google-emulator\persona-emulator-37.1.7\unmanifested.dll' 'unexpected'
    $unexpectedExit = Invoke-Installer $unexpected
    if ($unexpectedExit -eq 0) { throw 'Unmanifested runtime DLL was accepted.' }

    $guardParent = Join-Path $work 'builder-guard'
    $guardSource = Join-Path $guardParent 'source'
    $guardRuntime = Join-Path $guardSource 'runtime'
    $guardApp = Join-Path $guardSource 'app'
    [System.IO.Directory]::CreateDirectory($guardRuntime) | Out-Null
    [System.IO.Directory]::CreateDirectory($guardApp) | Out-Null
    $guardSentinel = Join-Path $guardParent 'sentinel.txt'
    Set-Content -LiteralPath $guardSentinel -Value 'must-survive' -Encoding ASCII
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $builder `
            -SourceRoot $guardSource -RuntimeSource $guardRuntime -AppSource $guardApp `
            -OutputDirectory $guardParent -SkipBuild *> $null
        [int]$guardExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($guardExit -eq 0 -or -not (Test-Path -LiteralPath $guardSentinel -PathType Leaf)) {
        throw 'Distribution builder accepted a destructive output directory.'
    }

    [ordered]@{
        schema = 1
        verdict = 'PASS'
        positiveInstallExitCode = $positiveExit
        tamperedPayloadExitCode = $negativeExit
        unmanifestedPayloadExitCode = $unexpectedExit
        relocatedDescriptor = $true
        sourcePayloadUnchanged = $true
        hashFailureClosed = $true
        unexpectedFileFailureClosed = $true
        destructiveOutputExitCode = $guardExit
        destructiveOutputFailureClosed = $true
    } | ConvertTo-Json -Depth 4
    exit 0
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
