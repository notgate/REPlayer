param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$ProcessName = "ReplayerAutomationProbe",
    [int]$TimeoutSeconds = 20
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class AgentWindowCaptureNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@
[AgentWindowCaptureNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$process = $null
while ([DateTime]::UtcNow -lt $deadline) {
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
    if ($process) { break }
    Start-Sleep -Milliseconds 200
}
if (-not $process) { throw "No visible $ProcessName window appeared." }

$rect = New-Object AgentWindowCaptureNative+RECT
if (-not [AgentWindowCaptureNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) { throw "GetWindowRect failed." }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -lt 1 -or $height -lt 1) { throw "Window bounds are invalid." }

$bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    if (-not [AgentWindowCaptureNative]::PrintWindow($process.MainWindowHandle, $hdc, 2)) { throw "PrintWindow failed." }
}
finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}
$directory = [IO.Path]::GetDirectoryName($OutputPath)
if ($directory) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
[PSCustomObject]@{ path = $OutputPath; title = $process.MainWindowTitle; width = $width; height = $height; handle = $process.MainWindowHandle.ToInt64() } | ConvertTo-Json -Compress
