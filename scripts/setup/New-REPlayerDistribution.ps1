[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$OutputDirectory = "",
    [string]$RuntimeSource = "",
    [string]$AppSource = "",
    [switch]$SkipBuild,
    [switch]$UseHardLinks,
    [switch]$ReleaseOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$Path, [string]$Base) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $Base $Path))
}

function Assert-OutputDoesNotContainInput([string]$Output, [string]$InputPath, [string]$Label) {
    $outputFull = [System.IO.Path]::GetFullPath($Output).TrimEnd('\')
    $inputFull = [System.IO.Path]::GetFullPath($InputPath).TrimEnd('\')
    if ([string]::Equals($outputFull, $inputFull, [System.StringComparison]::OrdinalIgnoreCase) -or
        $inputFull.StartsWith($outputFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputDirectory must not equal or contain $Label`: $outputFull"
    }
}

function Get-RelativePath([string]$BasePath, [string]$TargetPath) {
    $base = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $target = [System.IO.Path]::GetFullPath($TargetPath)
    $baseUri = New-Object System.Uri($base)
    $targetUri = New-Object System.Uri($target)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

function Copy-FileVerified([string]$Source, [string]$Destination, [bool]$HardLink) {
    $parent = Split-Path -Parent $Destination
    if ($parent) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    if ($HardLink) {
        try {
            New-Item -ItemType HardLink -Path $Destination -Target $Source -Force | Out-Null
            return
        } catch {
            Write-Verbose "Hard link unavailable for $Source; copying instead."
        }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-DirectorySmart([string]$Source, [string]$Destination, [bool]$HardLink) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required directory is missing: $Source"
    }
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    $sourceFull = [System.IO.Path]::GetFullPath($Source).TrimEnd('\')
    Get-ChildItem -LiteralPath $sourceFull -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($sourceFull.Length).TrimStart('\')
        $destinationFile = Join-Path $Destination $relative
        $requiresPrivateCopy = $_.Name -in @('system.img.qcow2', 'vendor.img.qcow2', 'replayer-baseline.json')
        Copy-FileVerified $_.FullName $destinationFile ($HardLink -and -not $requiresPrivateCopy)
    }
}

function Invoke-QemuImg([string]$QemuImg, [string]$WorkingDirectory, [string[]]$Arguments) {
    Push-Location $WorkingDirectory
    try {
        & $QemuImg @Arguments | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "qemu-img failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
        }
    } finally {
        Pop-Location
    }
}

function Set-MarkerFileHash([string]$MarkerPath, [string]$AvdDirectory, [string]$FileName) {
    $marker = Get-Content -LiteralPath $MarkerPath -Raw | ConvertFrom-Json
    if ($null -eq $marker.files.$FileName) {
        throw "Publication marker does not declare ${FileName}: $MarkerPath"
    }
    $filePath = Join-Path $AvdDirectory $FileName
    $marker.files.$FileName.sha256 = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $marker.files.$FileName.bytes = (Get-Item -LiteralPath $filePath).Length
    $marker | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $MarkerPath -Encoding UTF8
}

function New-HashManifest([string]$Root, [string]$ManifestPath, [string[]]$ExcludedRelativePaths, [hashtable]$Metadata) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $excluded = @{}
    foreach ($item in $ExcludedRelativePaths) { $excluded[$item.Replace('\','/')] = $true }
    $entries = New-Object System.Collections.Generic.List[object]
    Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\','/')
        if ($relative -eq (Split-Path -Leaf $ManifestPath)) { return }
        if ($excluded.ContainsKey($relative)) { return }
        $entries.Add([ordered]@{
            path = $relative
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    [long]$totalBytes = 0
    foreach ($entry in $entries) { $totalBytes += [long]$entry['bytes'] }
    $manifest = [ordered]@{
        schema = 1
        product = 'REPlayer'
        packageKind = $Metadata.packageKind
        packageVersion = $Metadata.packageVersion
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        totalBytes = $totalBytes
        files = $entries
        mutableFiles = $ExcludedRelativePaths
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
}

$SourceRoot = Resolve-FullPath $SourceRoot (Get-Location).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $SourceRoot 'dist\REPlayer-Distribution' }
$OutputDirectory = Resolve-FullPath $OutputDirectory $SourceRoot
if (-not $RuntimeSource) { $RuntimeSource = Join-Path $SourceRoot 'runtime' }
$RuntimeSource = Resolve-FullPath $RuntimeSource $SourceRoot
if ($AppSource) { $AppSource = Resolve-FullPath $AppSource $SourceRoot }

Assert-OutputDoesNotContainInput $OutputDirectory $SourceRoot 'SourceRoot'
Assert-OutputDoesNotContainInput $OutputDirectory $RuntimeSource 'RuntimeSource'
if ($AppSource) { Assert-OutputDoesNotContainInput $OutputDirectory $AppSource 'AppSource' }

if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

Write-Host "[1/5] Publishing the self-contained REPlayer application..."
if (-not $AppSource) {
    if ($SkipBuild) { throw '-SkipBuild requires -AppSource.' }
    $dotnetCandidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe'),
        'dotnet.exe'
    ) | Where-Object { $_ -and ((Test-Path -LiteralPath $_) -or (Get-Command $_ -ErrorAction SilentlyContinue)) }
    $dotnet = $dotnetCandidates | Select-Object -First 1
    if (-not $dotnet) {
        throw '.NET 9 SDK is required to build from source. Use the prebuilt REPlayer distribution on end-user systems.'
    }
    $project = Join-Path $SourceRoot 'ReVM\ReVM.csproj'
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
} else {
    Copy-DirectorySmart $AppSource $OutputDirectory $false
}
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'REPlayer.exe'))) {
    throw 'Self-contained publish did not produce REPlayer.exe.'
}

Write-Host "[2/5] Staging the pinned Google API 34 runtime..."
$runtimeOut = Join-Path $OutputDirectory 'runtime'
$googleOut = Join-Path $runtimeOut 'google-emulator'
[System.IO.Directory]::CreateDirectory($googleOut) | Out-Null
$requiredDirectories = @(
    'google-emulator\persona-emulator-37.1.7',
    'google-emulator\sdk\platform-tools',
    'google-emulator\sdk\build-tools\33.0.2',
    'google-emulator\sdk\system-images\android-34\google_apis\x86_64',
    'google-emulator\avd-home\ReVM.avd'
)
if (-not $ReleaseOnly) { $requiredDirectories += 'google-emulator\avd-home\ReVMResizable.avd' }
foreach ($relative in $requiredDirectories) {
    Copy-DirectorySmart (Join-Path $RuntimeSource $relative) (Join-Path $runtimeOut $relative) ([bool]$UseHardLinks)
}
foreach ($name in @('ReVM.ini') + $(if (-not $ReleaseOnly) { @('ReVMResizable.ini') } else { @() })) {
    Copy-FileVerified (Join-Path $RuntimeSource "google-emulator\avd-home\$name") (Join-Path $googleOut "avd-home\$name") $false
}

Write-Host "[3/5] Making published QCOW baselines relocatable..."
$qemuImg = Join-Path $googleOut 'persona-emulator-37.1.7\qemu-img.exe'
if (-not (Test-Path -LiteralPath $qemuImg)) { throw "qemu-img is missing from runtime payload: $qemuImg" }
$profiles = @('ReVM')
if (-not $ReleaseOnly) { $profiles += 'ReVMResizable' }
$relativeImageRoot = '..\..\sdk\system-images\android-34\google_apis\x86_64'
foreach ($profile in $profiles) {
    $avd = Join-Path $googleOut "avd-home\$profile.avd"
    Invoke-QemuImg $qemuImg $avd @('rebase','-u','-f','qcow2','-F','raw','-b',"$relativeImageRoot\system.img",'system.img.qcow2')
    Invoke-QemuImg $qemuImg $avd @('rebase','-u','-f','qcow2','-F','raw','-b',"$relativeImageRoot\vendor.img",'vendor.img.qcow2')
    $marker = Join-Path $avd 'replayer-baseline.json'
    Set-MarkerFileHash $marker $avd 'system.img.qcow2'
    Set-MarkerFileHash $marker $avd 'vendor.img.qcow2'
}

Write-Host "[4/5] Adding installer entry points..."
$setupScripts = Join-Path $OutputDirectory 'scripts\setup'
[System.IO.Directory]::CreateDirectory($setupScripts) | Out-Null
Copy-Item -LiteralPath (Join-Path $SourceRoot 'scripts\setup\Install-REPlayer.ps1') -Destination $setupScripts -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'scripts\setup\New-REPlayerDistribution.ps1') -Destination $setupScripts -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'setup.bat') -Destination $OutputDirectory -Force

Write-Host "[5/5] Hashing the distributable payload..."
$runtimeMutable = @(
    'google-emulator/avd-home/ReVM.ini',
    'google-emulator/avd-home/ReVM.avd/config.ini'
)
if (-not $ReleaseOnly) {
    $runtimeMutable += 'google-emulator/avd-home/ReVMResizable.ini'
    $runtimeMutable += 'google-emulator/avd-home/ReVMResizable.avd/config.ini'
}
$version = 'source'
try { $version = (& git -C $SourceRoot rev-parse --short=12 HEAD 2>$null).Trim() } catch { }
New-HashManifest $runtimeOut (Join-Path $runtimeOut 'replayer-runtime-manifest.json') $runtimeMutable @{
    packageKind = 'published-api34-runtime'
    packageVersion = $version
}
$appExclusions = @(
    'setup.bat',
    'scripts/setup/Install-REPlayer.ps1',
    'scripts/setup/New-REPlayerDistribution.ps1',
    'runtime/replayer-runtime-manifest.json'
)
Get-ChildItem -LiteralPath $runtimeOut -Recurse -File | ForEach-Object {
    $appExclusions += ('runtime/' + (Get-RelativePath $runtimeOut $_.FullName).Replace('\','/'))
}
New-HashManifest $OutputDirectory (Join-Path $OutputDirectory 'replayer-distribution-manifest.json') $appExclusions @{
    packageKind = 'self-contained-win-x64-application'
    packageVersion = $version
}
$runtimeManifestPath = Join-Path $runtimeOut 'replayer-runtime-manifest.json'
$distributionManifestPath = Join-Path $OutputDirectory 'replayer-distribution-manifest.json'
$runtimeManifestSha256 = (Get-FileHash -LiteralPath $runtimeManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$distributionManifestSha256 = (Get-FileHash -LiteralPath $distributionManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$packagedSetupPath = Join-Path $OutputDirectory 'setup.bat'
$packagedSetup = Get-Content -LiteralPath $packagedSetupPath -Raw
$packagedSetup = $packagedSetup.Replace('__REPLAYER_RUNTIME_MANIFEST_SHA256__', $runtimeManifestSha256)
$packagedSetup = $packagedSetup.Replace('__REPLAYER_DISTRIBUTION_MANIFEST_SHA256__', $distributionManifestSha256)
[System.IO.File]::WriteAllText($packagedSetupPath, $packagedSetup, [System.Text.Encoding]::ASCII)

$result = [ordered]@{
    verdict = 'PASS'
    outputDirectory = $OutputDirectory
    executable = (Join-Path $OutputDirectory 'REPlayer.exe')
    runtimeManifest = (Join-Path $runtimeOut 'replayer-runtime-manifest.json')
    runtimeManifestSha256 = $runtimeManifestSha256
    distributionManifestSha256 = $distributionManifestSha256
    profiles = $profiles
    hardLinksRequested = [bool]$UseHardLinks
}
$result | ConvertTo-Json -Depth 5
