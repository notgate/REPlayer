param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OutputApk = '',
    [string]$Keystore = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sdk = Join-Path $RepoRoot 'runtime\google-emulator\sdk'
$buildTools = Join-Path $sdk 'build-tools\33.0.2'
$platform = Join-Path $sdk 'platforms\android-30'
$project = Join-Path $RepoRoot 'android-tools\replayer-settings-overlay'
$work = Join-Path $RepoRoot 'runtime\google-emulator\build\settings-overlay'
if ([string]::IsNullOrWhiteSpace($OutputApk)) {
    $OutputApk = Join-Path $work 'REPlayerSettingsIdentityOverlay.apk'
}
if ([string]::IsNullOrWhiteSpace($Keystore)) {
    $Keystore = Join-Path $RepoRoot 'runtime\google-emulator\build\keys\replayer-development.keystore'
}

$aapt2 = Join-Path $buildTools 'aapt2.exe'
$zipalign = Join-Path $buildTools 'zipalign.exe'
$apksigner = Join-Path $buildTools 'apksigner.bat'
$androidJar = Join-Path $platform 'android.jar'
foreach ($required in @($aapt2, $zipalign, $apksigner, $androidJar, (Join-Path $project 'AndroidManifest.xml'))) {
    if (!(Test-Path $required)) { throw "Required overlay build input is missing: $required" }
}

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work, (Split-Path $OutputApk -Parent), (Split-Path $Keystore -Parent) | Out-Null
$compiled = Join-Path $work 'compiled-resources.zip'
$unsigned = Join-Path $work 'unsigned.apk'
$aligned = Join-Path $work 'aligned.apk'

& $aapt2 compile --dir (Join-Path $project 'res') -o $compiled
if ($LASTEXITCODE -ne 0) { throw 'aapt2 compile failed for the Settings identity overlay.' }
& $aapt2 link -o $unsigned -I $androidJar --manifest (Join-Path $project 'AndroidManifest.xml') --auto-add-overlay $compiled
if ($LASTEXITCODE -ne 0) { throw 'aapt2 link failed for the Settings identity overlay.' }
& $zipalign -f -p 4 $unsigned $aligned
if ($LASTEXITCODE -ne 0) { throw 'zipalign failed for the Settings identity overlay.' }

$javaBin = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Filter keytool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object FullName -Match 'jdk-8' | Select-Object -First 1 | ForEach-Object DirectoryName
if ([string]::IsNullOrWhiteSpace($javaBin)) { throw 'JDK 8 is required to sign the Settings identity overlay.' }
$keytool = Join-Path $javaBin 'keytool.exe'
if (!(Test-Path $Keystore)) {
    & $keytool -genkeypair -keystore $Keystore -storepass replayer-dev -keypass replayer-dev -alias replayer-development -keyalg RSA -keysize 2048 -validity 3650 -dname 'CN=REPlayer Development,O=REPlayer,C=US' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'keytool failed for the Settings identity overlay.' }
}
& $apksigner sign --ks $Keystore --ks-key-alias replayer-development --ks-pass pass:replayer-dev --key-pass pass:replayer-dev --out $OutputApk $aligned
if ($LASTEXITCODE -ne 0) { throw 'apksigner failed for the Settings identity overlay.' }
& $apksigner verify --verbose $OutputApk
if ($LASTEXITCODE -ne 0) { throw 'Settings identity overlay signature verification failed.' }

$hash = (Get-FileHash -Algorithm SHA256 $OutputApk).Hash.ToLowerInvariant()
[pscustomobject]@{ output = $OutputApk; sha256 = $hash } | ConvertTo-Json -Compress
