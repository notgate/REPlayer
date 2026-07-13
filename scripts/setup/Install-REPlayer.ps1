[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$RuntimeSource = "",
    [string]$InstallDirectory = "",
    [switch]$NoLaunch,
    [switch]$SkipHostChecks,
    [switch]$SkipShortcut,
    [switch]$UseHardLinks,
    [switch]$LaunchOnly,
    [switch]$Help,
    [string]$ExpectedRuntimeManifestSha256 = "",
    [string]$ExpectedDistributionManifestSha256 = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:TemporaryDistribution = $null
$script:LogPath = Join-Path $env:TEMP 'REPlayer-setup.log'

function Write-Step([string]$Message) {
    $line = "[{0}] {1}" -f ([DateTime]::Now.ToString('HH:mm:ss')), $Message
    Write-Host $line
    try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { }
}

function Resolve-FullPath([string]$Path, [string]$Base) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $Base $Path))
}

function Test-SamePath([string]$A, [string]$B) {
    return [string]::Equals(
        [System.IO.Path]::GetFullPath($A).TrimEnd('\'),
        [System.IO.Path]::GetFullPath($B).TrimEnd('\'),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-RelativePath([string]$BasePath, [string]$TargetPath) {
    $base = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $target = [System.IO.Path]::GetFullPath($TargetPath)
    $baseUri = New-Object System.Uri($base)
    $targetUri = New-Object System.Uri($target)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

function Assert-SafeRelativePath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Manifest contains an invalid path: $RelativePath"
    }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath.Replace('/', '\')))
    if (-not $candidate.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path escapes the package root: $RelativePath"
    }
    return $candidate
}

function Assert-ManifestHash([string]$ManifestPath, [string]$ExpectedHash, [string]$Label) {
    if (-not $ExpectedHash) { return }
    if ($ExpectedHash -notmatch '^[0-9a-fA-F]{64}$') { throw "$Label expected SHA-256 is malformed." }
    $actual = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actual, $ExpectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label manifest SHA-256 mismatch. Expected $ExpectedHash, got $actual."
    }
}

function Test-HashManifest([string]$Root, [string]$ManifestPath, [string]$ExpectedHash, [string]$Label) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "$Label manifest is missing: $ManifestPath" }
    Assert-ManifestHash $ManifestPath $ExpectedHash $Label
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.product -ne 'REPlayer' -or $null -eq $manifest.files) {
        throw "$Label manifest has an unsupported schema."
    }
    $verified = 0
    foreach ($entry in $manifest.files) {
        $path = Assert-SafeRelativePath $Root ([string]$entry.path)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Label payload file is missing: $($entry.path)" }
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne [long]$entry.bytes) { throw "$Label payload size mismatch: $($entry.path)" }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (-not [string]::Equals($actual, [string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label payload SHA-256 mismatch: $($entry.path)"
        }
        $verified++
    }
    if ($verified -eq 0) { throw "$Label manifest contains no files." }
    return $manifest
}

function Assert-ManifestFileSet(
    [string]$Root,
    [string]$ManifestPath,
    [string]$ExpectedHash,
    [string]$Label,
    [string[]]$AllowedExtraFiles = @(),
    [string[]]$AllowedExtraPrefixes = @()) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "$Label manifest is missing: $ManifestPath" }
    Assert-ManifestHash $ManifestPath $ExpectedHash $Label
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.product -ne 'REPlayer' -or $null -eq $manifest.files) {
        throw "$Label manifest has an unsupported schema."
    }
    $allowed = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $relative = ([string]$entry.path).Replace('\','/')
        $path = Assert-SafeRelativePath $Root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Label payload file is missing: $relative" }
        if ((Get-Item -LiteralPath $path).Length -ne [long]$entry.bytes) { throw "$Label payload size mismatch: $relative" }
        [void]$allowed.Add($relative)
    }
    if ($null -ne $manifest.mutableFiles) {
        foreach ($relativeValue in $manifest.mutableFiles) {
            $relative = ([string]$relativeValue).Replace('\','/')
            [void](Assert-SafeRelativePath $Root $relative)
            [void]$allowed.Add($relative)
        }
    }
    [void]$allowed.Add((Get-RelativePath $Root $ManifestPath).Replace('\','/'))
    foreach ($relative in $AllowedExtraFiles) { [void]$allowed.Add($relative.Replace('\','/')) }
    $prefixes = @($AllowedExtraPrefixes | ForEach-Object { $_.Replace('\','/').TrimEnd('/') + '/' })
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    Get-ChildItem -LiteralPath $rootFull -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\','/')
        $prefixAllowed = $false
        foreach ($prefix in $prefixes) {
            if ($relative.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { $prefixAllowed = $true; break }
        }
        if (-not $allowed.Contains($relative) -and -not $prefixAllowed) {
            throw "$Label payload contains an unmanifested file: $relative"
        }
    }
    return $manifest
}

function Assert-PayloadShape([string]$Root) {
    $required = @(
        'REPlayer.exe',
        'runtime\replayer-runtime-manifest.json',
        'runtime\google-emulator\persona-emulator-37.1.7\emulator.exe',
        'runtime\google-emulator\persona-emulator-37.1.7\emulator-check.exe',
        'runtime\google-emulator\persona-emulator-37.1.7\qemu-img.exe',
        'runtime\google-emulator\sdk\platform-tools\adb.exe',
        'runtime\google-emulator\sdk\system-images\android-34\google_apis\x86_64\system.img',
        'runtime\google-emulator\avd-home\ReVM.avd\replayer-baseline.json'
    )
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf)) {
            throw "REPlayer distribution is incomplete: $relative"
        }
    }
}

function Test-WindowsHost([string]$EmulatorCheckPath) {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'REPlayer requires 64-bit Windows.' }

    $systemInfoPath = Join-Path $env:SystemRoot 'System32\systeminfo.exe'
    $hypervisorDetected = $false
    if (Test-Path -LiteralPath $systemInfoPath) {
        $systemInfo = & $systemInfoPath 2>&1 | Out-String
        $hypervisorDetected = $systemInfo -match 'A hypervisor has been detected'
    }

    if (Test-Path -LiteralPath $EmulatorCheckPath -PathType Leaf) {
        $accelerationOutput = & $EmulatorCheckPath accel 2>&1 | Out-String
        $accelerationExit = $LASTEXITCODE
        if ($accelerationExit -ne 0 -or $accelerationOutput -notmatch 'WHPX.*installed and usable') {
            throw "WHPX acceleration is unavailable. Enable 'Windows Hypervisor Platform', reboot Windows, then rerun setup. Google probe output: $($accelerationOutput.Trim())"
        }
        Write-Step (($accelerationOutput -split "`r?`n" | Where-Object { $_ -match 'WHPX.*installed and usable' } | Select-Object -First 1).Trim())
    } elseif (-not $hypervisorDetected) {
        $processor = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($processor -and $processor.PSObject.Properties.Name -contains 'VirtualizationFirmwareEnabled' -and
            $processor.VirtualizationFirmwareEnabled -eq $false) {
            throw 'Hardware virtualization is disabled in firmware. Enable Intel VT-x/AMD-V before installing REPlayer.'
        }
    }

    $featureLabel = 'WindowsHypervisorPlatform'
    $dism = Join-Path $env:SystemRoot 'System32\dism.exe'
    if (Test-Path -LiteralPath $dism) {
        $featureOutput = & $dism /English /Online /Get-FeatureInfo /FeatureName:HypervisorPlatform 2>&1 | Out-String
        $featureExit = $LASTEXITCODE
        if ($featureExit -eq 0 -and $featureOutput -match 'State\s*:\s*Disabled') {
            throw "$featureLabel is disabled. Enable 'Windows Hypervisor Platform', reboot Windows, then rerun setup."
        }
        if ($featureExit -ne 0 -and -not $hypervisorDetected) {
            Write-Warning "$featureLabel could not be queried without elevation; the Google acceleration probe remains authoritative."
        }
    }
}

function Assert-FreeSpace([string]$Destination, [long]$PayloadBytes, [bool]$HardLink) {
    $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($Destination))
    $drive = New-Object System.IO.DriveInfo($root)
    $required = [long]($PayloadBytes * 1.08) + 2GB
    if ($drive.AvailableFreeSpace -lt $required) {
        $need = [math]::Ceiling($required / 1GB)
        $have = [math]::Floor($drive.AvailableFreeSpace / 1GB)
        throw "Insufficient disk space. REPlayer needs approximately $need GiB free; $have GiB is available."
    }
}

function Copy-FileSmart([string]$Source, [string]$Destination, [bool]$HardLink) {
    $parent = Split-Path -Parent $Destination
    if ($parent) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    if ($HardLink) {
        try {
            New-Item -ItemType HardLink -Path $Destination -Target $Source -Force | Out-Null
            return
        } catch { }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-Payload([string]$Source, [string]$Destination, [bool]$HardLink) {
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    $sourceFull = [System.IO.Path]::GetFullPath($Source).TrimEnd('\')
    Get-ChildItem -LiteralPath $sourceFull -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($sourceFull.Length).TrimStart('\')
        $isMutableDescriptor = $_.Name.EndsWith('.ini', [System.StringComparison]::OrdinalIgnoreCase)
        Copy-FileSmart $_.FullName (Join-Path $Destination $relative) ($HardLink -and -not $isMutableDescriptor)
    }
}

function Set-IniValue([string]$Path, [string]$Key, [string]$Value) {
    $lines = @()
    if (Test-Path -LiteralPath $Path) { $lines = @(Get-Content -LiteralPath $Path) }
    $found = $false
    $updated = foreach ($line in $lines) {
        if ($line -match ('^' + [regex]::Escape($Key) + '=')) {
            $found = $true
            "$Key=$Value"
        } else { $line }
    }
    if (-not $found) { $updated += "$Key=$Value" }
    $updated | Set-Content -LiteralPath $Path -Encoding ASCII
}

function Repair-RuntimePaths([string]$Root) {
    $google = Join-Path $Root 'runtime\google-emulator'
    $imageDir = Join-Path $google 'sdk\system-images\android-34\google_apis\x86_64'
    $escapedImageDir = $imageDir.Replace('\', '\\') + '\\'
    foreach ($profile in @('ReVM','ReVMResizable')) {
        $avd = Join-Path $google "avd-home\$profile.avd"
        if (-not (Test-Path -LiteralPath $avd -PathType Container)) { continue }
        $ini = Join-Path $google "avd-home\$profile.ini"
        @(
            'avd.ini.encoding=UTF-8',
            ('path=' + $avd),
            ('path.rel=avd/' + $profile + '.avd'),
            'target=android-34'
        ) | Set-Content -LiteralPath $ini -Encoding ASCII
        Set-IniValue (Join-Path $avd 'config.ini') 'image.sysdir.1' $escapedImageDir
        Set-IniValue (Join-Path $avd 'config.ini') 'AvdId' $profile
        Set-IniValue (Join-Path $avd 'config.ini') 'avd.ini.displayname' $profile
    }
}

function New-StartMenuShortcut([string]$Root) {
    try {
        $programs = [Environment]::GetFolderPath('Programs')
        $folder = Join-Path $programs 'REPlayer'
        [System.IO.Directory]::CreateDirectory($folder) | Out-Null
        $shortcutPath = Join-Path $folder 'REPlayer.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $Root 'REPlayer.exe'
        $shortcut.WorkingDirectory = $Root
        $shortcut.Description = 'REPlayer Android analysis workbench'
        $shortcut.Save()
    } catch {
        Write-Warning "Start Menu shortcut could not be created: $($_.Exception.Message)"
    }
}

function Find-LaunchRoot([string]$Preferred, [string]$Source) {
    $candidates = @($Preferred, $Source, (Join-Path $Source 'dist\REPlayer-Distribution'))
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'REPlayer.exe') -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

if ($Help) {
    @'
REPlayer setup

  setup.bat
  setup.bat -RuntimeSource D:\path\to\published-runtime
  setup.bat -InstallDirectory D:\Apps\REPlayer -NoLaunch

The release package is self-contained. Building from source requires the .NET 9 SDK
and a prepublished API 34 runtime. Installation is per-user and does not require
administrator privileges. Windows Hypervisor Platform must already be enabled.
'@ | Write-Host
    exit 0
}

try {
    if (Test-Path -LiteralPath $script:LogPath) { Remove-Item -LiteralPath $script:LogPath -Force }
    $SourceRoot = Resolve-FullPath $SourceRoot (Get-Location).Path
    if (-not $InstallDirectory) { $InstallDirectory = Join-Path $env:LOCALAPPDATA 'REPlayer' }
    $InstallDirectory = Resolve-FullPath $InstallDirectory $SourceRoot

    if ($LaunchOnly) {
        $launchRoot = Find-LaunchRoot $InstallDirectory $SourceRoot
        if (-not $launchRoot) { throw 'REPlayer is not installed. Run setup.bat first.' }
        Start-Process -FilePath (Join-Path $launchRoot 'REPlayer.exe') -WorkingDirectory $launchRoot
        exit 0
    }

    Write-Step 'Starting non-admin REPlayer setup.'

    if (-not $ExpectedRuntimeManifestSha256 -and $env:REPLAYER_EXPECTED_RUNTIME_MANIFEST_SHA256 -notlike '__*__') {
        $ExpectedRuntimeManifestSha256 = $env:REPLAYER_EXPECTED_RUNTIME_MANIFEST_SHA256
    }
    if (-not $ExpectedDistributionManifestSha256 -and $env:REPLAYER_EXPECTED_DISTRIBUTION_MANIFEST_SHA256 -notlike '__*__') {
        $ExpectedDistributionManifestSha256 = $env:REPLAYER_EXPECTED_DISTRIBUTION_MANIFEST_SHA256
    }

    $payloadRoot = $null
    if ((Test-Path -LiteralPath (Join-Path $SourceRoot 'REPlayer.exe')) -and
        (Test-Path -LiteralPath (Join-Path $SourceRoot 'runtime\replayer-runtime-manifest.json'))) {
        $payloadRoot = $SourceRoot
        Write-Step 'Using the prebuilt REPlayer release payload.'
    } elseif (Test-Path -LiteralPath (Join-Path $SourceRoot 'dist\REPlayer-Distribution\runtime\replayer-runtime-manifest.json')) {
        $payloadRoot = Join-Path $SourceRoot 'dist\REPlayer-Distribution'
        Write-Step 'Using the existing local REPlayer distribution.'
    } else {
        if (-not $RuntimeSource) { $RuntimeSource = Join-Path $SourceRoot 'runtime' }
        $RuntimeSource = Resolve-FullPath $RuntimeSource $SourceRoot
        if (-not (Test-Path -LiteralPath (Join-Path $RuntimeSource 'google-emulator\avd-home\ReVM.avd\replayer-baseline.json'))) {
            throw 'A prepublished API 34 runtime is required. Download the REPlayer release package or pass -RuntimeSource to a verified published runtime directory.'
        }
        $builder = Join-Path $SourceRoot 'scripts\setup\New-REPlayerDistribution.ps1'
        if (-not (Test-Path -LiteralPath $builder)) { throw "Distribution builder is missing: $builder" }
        $UseHardLinks = $true
        $stageParent = Split-Path -Parent $InstallDirectory
        [System.IO.Directory]::CreateDirectory($stageParent) | Out-Null
        $script:TemporaryDistribution = Join-Path $stageParent (".REPlayer-Distribution-" + $PID)
        Write-Step 'Building a self-contained local distribution from verified source inputs.'
        & $builder -SourceRoot $SourceRoot -RuntimeSource $RuntimeSource -OutputDirectory $script:TemporaryDistribution -UseHardLinks:$UseHardLinks
        if ($LASTEXITCODE -ne 0) { throw "Distribution builder failed with exit code $LASTEXITCODE." }
        $payloadRoot = $script:TemporaryDistribution
        $ExpectedRuntimeManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $payloadRoot 'runtime\replayer-runtime-manifest.json') -Algorithm SHA256).Hash
        $ExpectedDistributionManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $payloadRoot 'replayer-distribution-manifest.json') -Algorithm SHA256).Hash
    }

    Assert-PayloadShape $payloadRoot
    if (-not $SkipHostChecks) {
        Test-WindowsHost (Join-Path $payloadRoot 'runtime\google-emulator\persona-emulator-37.1.7\emulator-check.exe')
    }
    $runtimeManifestPath = Join-Path $payloadRoot 'runtime\replayer-runtime-manifest.json'
    $distributionManifestPath = Join-Path $payloadRoot 'replayer-distribution-manifest.json'
    $distributionManifest = Assert-ManifestFileSet $payloadRoot $distributionManifestPath `
        $ExpectedDistributionManifestSha256 'Application' @('setup.bat','run-replayer.bat') @('runtime','scripts/setup')
    $runtimeManifest = Assert-ManifestFileSet (Join-Path $payloadRoot 'runtime') $runtimeManifestPath `
        $ExpectedRuntimeManifestSha256 'Runtime'
    $payloadBytes = [long]$runtimeManifest.totalBytes + [long]$distributionManifest.totalBytes
    Assert-FreeSpace $InstallDirectory $payloadBytes ([bool]$UseHardLinks)

    Write-Step 'Installing the verified payload into the per-user application directory.'
    if (-not (Test-SamePath $payloadRoot $InstallDirectory)) {
        Copy-Payload $payloadRoot $InstallDirectory ([bool]$UseHardLinks)
    }

    Write-Step 'Verifying installed application and runtime SHA-256 manifests.'
    Assert-PayloadShape $InstallDirectory
    [void](Test-HashManifest $InstallDirectory (Join-Path $InstallDirectory 'replayer-distribution-manifest.json') $ExpectedDistributionManifestSha256 'Application')
    [void](Test-HashManifest (Join-Path $InstallDirectory 'runtime') (Join-Path $InstallDirectory 'runtime\replayer-runtime-manifest.json') $ExpectedRuntimeManifestSha256 'Runtime')

    Repair-RuntimePaths $InstallDirectory
    if (-not $SkipShortcut) { New-StartMenuShortcut $InstallDirectory }
    Write-Step "REPlayer setup completed: $InstallDirectory"

    if (-not $NoLaunch) {
        Write-Step 'Launching REPlayer.'
        Start-Process -FilePath (Join-Path $InstallDirectory 'REPlayer.exe') -WorkingDirectory $InstallDirectory
    }
    Copy-Item -LiteralPath $script:LogPath -Destination (Join-Path $InstallDirectory 'setup.log') -Force
    exit 0
} catch {
    $message = $_.Exception.Message
    Write-Host "[ERROR] $message" -ForegroundColor Red
    try { Add-Content -LiteralPath $script:LogPath -Value ("[ERROR] " + $_.Exception.ToString()) -Encoding UTF8 } catch { }
    try {
        if ($InstallDirectory -and (Test-Path -LiteralPath $InstallDirectory -PathType Container)) {
            Copy-Item -LiteralPath $script:LogPath -Destination (Join-Path $InstallDirectory 'setup.log') -Force
        }
    } catch { }
    exit 1
} finally {
    if ($script:TemporaryDistribution -and (Test-Path -LiteralPath $script:TemporaryDistribution)) {
        try { Remove-Item -LiteralPath $script:TemporaryDistribution -Recurse -Force } catch { }
    }
}
