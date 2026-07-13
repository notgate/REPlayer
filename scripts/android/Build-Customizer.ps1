[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [switch]$ForceToolDownload
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) '..\..')).Path
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sdkRoot = Join-Path $RepoRoot 'runtime/google-emulator/sdk'
$downloads = Join-Path $RepoRoot 'runtime/google-emulator/downloads'
$buildTools = Join-Path $sdkRoot 'build-tools/33.0.2'
$platform = Join-Path $sdkRoot 'platforms/android-30'
$project = Join-Path $RepoRoot 'android-tools/replayer-customizer/app/src/main'
$work = Join-Path $RepoRoot 'runtime/google-emulator/build/customizer'
$outputApk = Join-Path $RepoRoot 'ReVM/Assets/Android/replayer-customizer.apk'

function Ensure-Archive([string]$Url, [string]$ZipPath, [string]$ExtractPath) {
    if ($ForceToolDownload -or !(Test-Path $ZipPath)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $ZipPath -Parent) | Out-Null
        Invoke-WebRequest -Uri $Url -OutFile $ZipPath
    }
    if (Test-Path $ExtractPath) { Remove-Item $ExtractPath -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ExtractPath | Out-Null
    Expand-Archive -Path $ZipPath -DestinationPath $ExtractPath -Force
}

if (!(Test-Path (Join-Path $buildTools 'aapt2.exe')) -or !(Test-Path (Join-Path $buildTools 'd8.bat')) -or !(Test-Path (Join-Path $buildTools 'apksigner.bat'))) {
    $zip = Join-Path $downloads 'build-tools_r33.0.2-windows.zip'
    $extract = Join-Path $downloads 'build-tools-33.0.2-customizer'
    Ensure-Archive 'https://dl.google.com/android/repository/build-tools_r33.0.2-windows.zip' $zip $extract
    $apksigner = Get-ChildItem $extract -Filter apksigner.bat -Recurse | Select-Object -First 1
    if ($null -eq $apksigner) { throw 'Downloaded Android build-tools did not contain apksigner.bat.' }
    if (Test-Path $buildTools) { Remove-Item $buildTools -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $buildTools -Parent) | Out-Null
    Copy-Item $apksigner.Directory.FullName $buildTools -Recurse -Force
}

if (!(Test-Path (Join-Path $platform 'android.jar'))) {
    $zip = Join-Path $downloads 'platform-30_r03.zip'
    $extract = Join-Path $downloads 'platform-30-customizer'
    Ensure-Archive 'https://dl.google.com/android/repository/platform-30_r03.zip' $zip $extract
    $androidJar = Get-ChildItem $extract -Filter android.jar -Recurse | Select-Object -First 1
    if ($null -eq $androidJar) { throw 'Downloaded Android 30 platform did not contain android.jar.' }
    if (Test-Path $platform) { Remove-Item $platform -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $platform -Parent) | Out-Null
    Copy-Item $androidJar.Directory.FullName $platform -Recurse -Force
}

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work, (Join-Path $work 'generated'), (Join-Path $work 'classes'), (Join-Path $work 'dex') | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $outputApk -Parent) | Out-Null

$aapt2 = Join-Path $buildTools 'aapt2.exe'
$d8Jar = Join-Path $buildTools 'lib/d8.jar'
$zipalign = Join-Path $buildTools 'zipalign.exe'
$apksigner = Join-Path $buildTools 'apksigner.bat'
$androidJar = Join-Path $platform 'android.jar'
$manifest = Join-Path $project 'AndroidManifest.xml'
$resDir = Join-Path $project 'res'
$compiledResources = Join-Path $work 'compiled-resources.zip'
$resourcesApk = Join-Path $work 'resources.ap_'
$unsignedApk = Join-Path $work 'unsigned.apk'
$alignedApk = Join-Path $work 'aligned.apk'

& $aapt2 compile --dir $resDir -o $compiledResources
if ($LASTEXITCODE -ne 0) { throw 'aapt2 resource compilation failed.' }
& $aapt2 link -o $resourcesApk -I $androidJar --manifest $manifest --java (Join-Path $work 'generated') $compiledResources
if ($LASTEXITCODE -ne 0) { throw 'aapt2 resource linking failed.' }

$java8Bin = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Filter javac.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object FullName -Match 'jdk-8' | Select-Object -First 1 | ForEach-Object DirectoryName
if ([string]::IsNullOrWhiteSpace($java8Bin)) { throw 'JDK 8 is required to build the Android customizer APK.' }
$javac = Join-Path $java8Bin 'javac.exe'
$javaSources = @(Get-ChildItem (Join-Path $project 'java') -Filter *.java -Recurse | ForEach-Object FullName)
$generatedSources = @(Get-ChildItem (Join-Path $work 'generated') -Filter *.java -Recurse | ForEach-Object FullName)
& $javac -g:none -source 8 -target 8 -bootclasspath $androidJar -d (Join-Path $work 'classes') @javaSources @generatedSources
if ($LASTEXITCODE -ne 0) { throw 'javac failed for REPlayer customizer.' }

$classFiles = @(Get-ChildItem (Join-Path $work 'classes') -Filter *.class -Recurse | ForEach-Object FullName)
$java = Join-Path $java8Bin 'java.exe'
& $java -cp $d8Jar com.android.tools.r8.D8 --lib $androidJar --min-api 24 --output (Join-Path $work 'dex') @classFiles
if ($LASTEXITCODE -ne 0) { throw 'd8 failed for REPlayer customizer.' }

Copy-Item $resourcesApk $unsignedApk -Force
$archive = [System.IO.Compression.ZipFile]::Open($unsignedApk, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $dexEntry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $work 'dex/classes.dex'), 'classes.dex', [System.IO.Compression.CompressionLevel]::Optimal)
    # ZIP records local timestamps. Pin the injected DEX entry to the minimum
    # portable ZIP timestamp so repeated builds are byte-for-byte reproducible.
    $dexEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
} finally {
    $archive.Dispose()
}

& $zipalign -f -p 4 $unsignedApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw 'zipalign failed for REPlayer customizer.' }

$keystore = Join-Path $RepoRoot 'runtime/google-emulator/build/keys/replayer-development.keystore'
$keytool = Join-Path $java8Bin 'keytool.exe'
if (!(Test-Path $keystore)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $keystore -Parent) | Out-Null
    & $keytool -genkeypair -keystore $keystore -storepass replayer-dev -keypass replayer-dev -alias replayer-development -keyalg RSA -keysize 2048 -validity 3650 -dname 'CN=REPlayer Development,O=REPlayer,C=US' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'keytool failed for REPlayer customizer.' }
}
& $apksigner sign --ks $keystore --ks-key-alias replayer-development --ks-pass pass:replayer-dev --key-pass pass:replayer-dev --out $outputApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw 'apksigner failed for REPlayer customizer.' }
& $apksigner verify --verbose $outputApk
if ($LASTEXITCODE -ne 0) { throw 'REPlayer customizer signature verification failed.' }

$hash = (Get-FileHash -Algorithm SHA256 $outputApk).Hash.ToLowerInvariant()
Write-Host "REPlayer Android customizer built: $outputApk"
Write-Host "SHA256: $hash"
