using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM;

public class HyperVService
{
    private readonly string _baseDir;
    private readonly string _runtimeDir;
    private readonly string _instancesDir;
    private readonly string _baseVhdxPath;
    private readonly string _hypervBaseVhdxPath;
    private readonly string _baseImageReadyPath;
    private readonly string _vmconnectExe;
    private readonly string _adbExe;
    private readonly string _scrcpyExe;
    private readonly Dictionary<string, Process> _vmconnectProcs = new();
    private readonly Dictionary<string, IntPtr> _embeddedWindows = new();

    public event Action<string, IntPtr>? WindowCreated;
    public event Action<string>? StatusChanged;

    // ── Win32 interop for window embedding ──────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CHILD = 0x40000000;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    // ── Constructor ─────────────────────────────────────────────
    public HyperVService()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        _baseDir = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", ".."));
        _runtimeDir = Path.Combine(_baseDir, "runtime");
        _instancesDir = Path.Combine(_baseDir, "instances");
        _baseVhdxPath = Path.Combine(_runtimeDir, "android-base.vhdx");
        _hypervBaseVhdxPath = Path.Combine(_baseDir, "hyperv", "disks", "android-base-hyperv.vhdx");
        _baseImageReadyPath = Path.Combine(_runtimeDir, ".base-ready");
        _vmconnectExe = Path.Combine(Environment.SystemDirectory, "vmconnect.exe");
        _adbExe = Path.Combine(_runtimeDir, "adb.exe");
        _scrcpyExe = Path.Combine(_runtimeDir, "scrcpy", "scrcpy.exe");

        Directory.CreateDirectory(_runtimeDir);
        Directory.CreateDirectory(_instancesDir);

        Log("HyperVService initialized");
        Log($"  Base dir: {_baseDir}");
        Log($"  Runtime dir: {_runtimeDir}");
        Log($"  Instances dir: {_instancesDir}");
        Log($"  Base VHDX: {_baseVhdxPath}");
        Log($"  Hyper-V safe base VHDX: {_hypervBaseVhdxPath}");
        Log($"  vmconnect: {_vmconnectExe}");
        Log($"  adb: {_adbExe}");
        Log($"  scrcpy: {_scrcpyExe}");
    }

    // ── Logging ─────────────────────────────────────────────────
    private static readonly object _logLock = new();

    private void Log(string message)
    {
        try
        {
            var logDir = Path.Combine(_baseDir, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "hyperv-wpf-backend.log");
            lock (_logLock)
            {
                File.AppendAllText(logPath,
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
            }
        }
        catch
        {
            // best-effort logging — never throw from the logger
        }
    }

    // ── PowerShell execution helper ─────────────────────────────
    /// <summary>
    /// Writes <paramref name="script"/> to a temp .ps1 file, runs it via
    /// powershell.exe -File, and returns (success, stdout, stderr).
    /// </summary>
    private (bool success, string output, string error) RunPowerShell(
        string script, int timeoutMs = 30000)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"revm_ps_{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(tempFile, script, Encoding.UTF8);

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var preview = script.Length > 200 ? script[..200] + "..." : script;
            Log($"PS exec: {preview.Replace('\n', ' ').Replace('\r', ' ')}");

            using var p = Process.Start(psi);
            if (p == null)
            {
                Log("PS exec: Process.Start returned null");
                return (false, "", "Failed to start PowerShell process");
            }

            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                Log($"PS exec: timed out after {timeoutMs}ms");
                return (false, output, $"PowerShell timed out after {timeoutMs}ms. {error}");
            }

            if (p.ExitCode != 0)
            {
                var errPreview = error.Length > 500 ? error[..500] + "..." : error;
                Log($"PS exec: exit code {p.ExitCode}, error: {errPreview}");
            }
            else
            {
                var outPreview = output.Length > 200 ? output[..200] + "..." : output;
                Log($"PS exec: success, output: {outPreview}");
            }

            return (p.ExitCode == 0, output.Trim(), error.Trim());
        }
        catch (Exception ex)
        {
            Log($"PS exec exception: {ex.Message}");
            return (false, "", ex.Message);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // ── Public API (identical surface to QemuService) ───────────

    /// <summary>Check that Hyper-V is installed and usable.</summary>
    public bool CheckEngine()
    {
        Log("CheckEngine called");

        if (!File.Exists(_vmconnectExe))
        {
            Log("vmconnect.exe not found — Hyper-V may not be installed");
            return false;
        }

        // Quick check: can we load the Hyper-V PowerShell module?
        var (success, output, _) = RunPowerShell(
            "Get-Command -Module Hyper-V -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Name",
            10000);

        if (!success || string.IsNullOrWhiteSpace(output))
        {
            Log("Hyper-V PowerShell module not available. " +
                "Is Hyper-V enabled and are you running as Administrator?");
            return false;
        }

        Log($"CheckEngine: Hyper-V available. Found cmdlet: {output}");
        return true;
    }

    /// <summary>True when the base VHDX exists and has been validated.</summary>
    public bool IsBaseImageReady() =>
        File.Exists(_baseImageReadyPath) &&
        (File.Exists(_hypervBaseVhdxPath) || File.Exists(_baseVhdxPath));

    /// <summary>Enumerate all instances from disk + Hyper-V state.</summary>
    public List<VmInstance> GetInstances()
    {
        var instances = new List<VmInstance>();

        // Always expose the manually-created Hyper-V base VM as the primary runnable instance.
        // This lets the UI attach to the VM that already boots Android correctly.
        var (baseOk, baseState, _) = RunPowerShell(
            "if (Get-VM -Name 'ReVM-Base' -ErrorAction SilentlyContinue) { (Get-VM -Name 'ReVM-Base').State } else { 'NOT_FOUND' }",
            5000);
        if (baseOk && baseState.Trim() != "NOT_FOUND")
        {
            instances.Add(new VmInstance
            {
                Name = "ReVM Base",
                Id = "base",
                CpuCount = 4,
                RamMB = 6144,
                StorageGB = 16,
                Status = baseState.Trim() == "Running" ? "running" : "stopped"
            });
        }

        if (!Directory.Exists(_instancesDir)) return instances;

        foreach (var dir in Directory.GetDirectories(_instancesDir))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;
            try
            {
                var config = JsonSerializer.Deserialize<InstanceConfig>(
                    File.ReadAllText(configPath));
                if (config == null) continue;

                var vmName = GetVmName(config.Id);
                var status = _vmconnectProcs.ContainsKey(config.Id) ? "running" : "stopped";

                // Double-check with Hyper-V if our in-memory state says "stopped"
                if (status == "stopped")
                {
                    var (ok, state, _) = RunPowerShell(
                        $"(Get-VM -Name '{vmName}' -ErrorAction SilentlyContinue).State",
                        5000);
                    if (ok && state?.Trim() == "Running")
                        status = "running";
                }

                instances.Add(new VmInstance
                {
                    Name = config.Name,
                    Id = config.Id,
                    CpuCount = config.CpuCount,
                    RamMB = config.RamMB,
                    StorageGB = config.StorageGB,
                    Status = status
                });
            }
            catch (Exception ex)
            {
                Log($"GetInstances error for {dir}: {ex.Message}");
            }
        }
        return instances;
    }

    /// <summary>
    /// Validate (or create) the base VHDX marker.
    /// Unlike the QEMU version this does NOT run an automated Android installer.
    /// The user must supply a pre-built android-base.vhdx.
    /// </summary>
    public async Task<(bool success, string message)> EnsureBaseImageAsync(
        CancellationToken ct = default)
    {
        Log("EnsureBaseImageAsync called");

        if (File.Exists(_baseImageReadyPath) && File.Exists(_baseVhdxPath))
        {
            Log("Base image already exists and is marked ready");
            return (true, "Base image already exists");
        }

        // Clean up stale marker
        try { File.Delete(_baseImageReadyPath); } catch { }

        StatusChanged?.Invoke("Checking for base VHDX...");

        if (!File.Exists(_baseVhdxPath))
        {
            var msg =
                $"Base VHDX not found at:\n  {_baseVhdxPath}\n\n" +
                "To create a base image:\n" +
                "  1. Install Android-x86 in a Hyper-V VM manually, OR\n" +
                "  2. Convert an existing QCOW2 image to VHDX:\n" +
                "     qemu-img convert -f qcow2 -O vhdx android-base.qcow2 android-base.vhdx\n" +
                "  3. Place the VHDX at the path above and restart ReVM.";
            Log($"EnsureBaseImageAsync: {msg}");
            return (false, msg);
        }

        // Verify minimum size (500 MB)
        var info = new FileInfo(_baseVhdxPath);
        if (info.Length < 500_000_000)
        {
            var msg = $"Base VHDX is too small ({info.Length / 1024 / 1024} MB). " +
                      "Expected at least 500 MB.";
            Log($"EnsureBaseImageAsync: {msg}");
            return (false, msg);
        }

        // Validate with Hyper-V
        StatusChanged?.Invoke("Verifying base VHDX...");
        var (vhdOk, vhdOut, vhdErr) = await Task.Run(() => RunPowerShell(
            "$vhd = Get-VHD -Path '" + _baseVhdxPath + "' -ErrorAction Stop; " +
            "\"OK:$($vhd.Size/1GB):$($vhd.VhdFormat):$($vhd.VhdType)\"",
            15000), ct);

        if (!vhdOk || !(vhdOut?.StartsWith("OK:") ?? false))
        {
            var msg = $"Base VHDX validation failed: {vhdErr}";
            Log(msg);
            return (false, msg);
        }

        Log($"Base VHDX validated: {vhdOut}");

        // Write ready marker
        File.WriteAllText(_baseImageReadyPath, DateTime.UtcNow.ToString("O"));
        StatusChanged?.Invoke("Base image ready");
        Log("Base image marked ready");
        return (true, "Base image verified successfully");
    }

    /// <summary>
    /// Create a new instance: differencing VHDX + Hyper-V VM + config.
    /// </summary>
    public void CreateInstance(string name, int cpuCount, int ramMB, int storageGB)
    {
        Log($"CreateInstance: name={name}, cpu={cpuCount}, ram={ramMB}MB, storage={storageGB}GB");

        if (!File.Exists(_baseImageReadyPath))
            throw new InvalidOperationException(
                "Base image not ready. Run setup first.");

        var id = Guid.NewGuid().ToString("N")[..12];
        var instanceDir = Path.Combine(_instancesDir, id);
        Directory.CreateDirectory(instanceDir);

        var hypervInstanceDir = Path.Combine(_baseDir, "hyperv", "instances", id);
        Directory.CreateDirectory(hypervInstanceDir);
        var diskPath = Path.Combine(hypervInstanceDir, "system.vhdx");
        var parentVhdxPath = File.Exists(_hypervBaseVhdxPath) ? _hypervBaseVhdxPath : _baseVhdxPath;
        var vmName = GetVmName(id);

        ClearVhdxFileAttributes(Path.GetDirectoryName(diskPath)!);

        // ── 1. Create instance VHDX ──
        StatusChanged?.Invoke($"Creating disk for {name}...");
        Log($"Creating full VHDX copy: parent={parentVhdxPath}, child={diskPath}");

        var (diskOk, _, diskErr) = RunPowerShell(
            "$ErrorActionPreference = 'Stop'; " +
            $"compact.exe /U /I /Q '{Path.GetDirectoryName(diskPath)}' | Out-Null; " +
            $"Copy-Item -LiteralPath '{parentVhdxPath}' -Destination '{diskPath}' -Force; " +
            $"compact.exe /U /I /Q '{diskPath}' | Out-Null; " +
            $"cipher.exe /D '{diskPath}' | Out-Null; " +
            $"fsutil.exe sparse setflag '{diskPath}' 0 | Out-Null; " +
            $"Get-Item -LiteralPath '{diskPath}' | Select-Object -ExpandProperty Length",
            120000);

        if (!diskOk)
        {
            Log($"Instance disk creation failed: {diskErr}");
            throw new InvalidOperationException(
                $"Failed to create instance disk: {diskErr}");
        }

        // ── 2. Create Hyper-V VM ──
        StatusChanged?.Invoke($"Creating Hyper-V VM: {vmName}...");
        Log($"Creating Hyper-V VM: {vmName}");

        var ramBytes = (long)ramMB * 1024 * 1024;
        var createScript = new StringBuilder();
        createScript.AppendLine("$ErrorActionPreference = 'Stop'");
        createScript.AppendLine(
            $"New-VM -Name '{vmName}' -VHDPath '{diskPath}' " +
            $"-MemoryStartupBytes {ramBytes} -Generation 1 -ErrorAction Stop");
        createScript.AppendLine(
            $"Set-VMProcessor -VMName '{vmName}' -Count {cpuCount} -ErrorAction Stop");
        createScript.AppendLine(
            $"Set-VM -VMName '{vmName}' -AutomaticCheckpointsEnabled $false " +
            "-ErrorAction Stop");

        // Attach to a suitable virtual switch
        createScript.AppendLine(
            "$switch = Get-VMSwitch -Name 'Default Switch' -ErrorAction SilentlyContinue");
        createScript.AppendLine(
            "if (-not $switch) { $switch = Get-VMSwitch -SwitchType NAT " +
            "-ErrorAction SilentlyContinue | Select-Object -First 1 }");
        createScript.AppendLine(
            "if (-not $switch) { $switch = Get-VMSwitch -ErrorAction SilentlyContinue " +
            "| Select-Object -First 1 }");
        createScript.AppendLine(
            "if (-not $switch) { Write-Error 'No virtual switch found. " +
            "Create one in Hyper-V Manager.'; exit 1 }");
        createScript.AppendLine(
            $"Connect-VMNetworkAdapter -VMName '{vmName}' " +
            "-SwitchName $switch.Name -ErrorAction Stop");
        createScript.AppendLine("Write-Output 'VM_CREATED'");

        var (createOk, _, createErr) = RunPowerShell(createScript.ToString(), 30000);
        if (!createOk)
        {
            Log($"VM creation failed: {createErr}");
            // Clean up the differencing disk
            try { File.Delete(diskPath); } catch { }
            try { Directory.Delete(instanceDir, true); } catch { }
            throw new InvalidOperationException(
                $"Failed to create Hyper-V VM: {createErr}");
        }

        // ── 3. Write config ──
        File.WriteAllText(Path.Combine(instanceDir, "config.json"),
            JsonSerializer.Serialize(new InstanceConfig
            {
                Id = id,
                Name = name,
                CpuCount = cpuCount,
                RamMB = ramMB,
                StorageGB = storageGB,
                CreatedAt = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));

        Log($"Instance created: {id} ({name}), VM: {vmName}");
        StatusChanged?.Invoke($"Created: {name}");
    }

    /// <summary>
    /// Start a VM and embed vmconnect.exe. Returns debug info.
    /// </summary>
    public (bool success, string debug) StartInstanceWithDebug(
        string vmId, IntPtr hostHandle,
        int hostX, int hostY, int hostW, int hostH)
    {
        Log($"StartInstanceWithDebug: vmId={vmId}, " +
            $"pos=({hostX},{hostY}), size={hostW}x{hostH}");

        var instanceDir = Path.Combine(_instancesDir, vmId);
        var configPath = Path.Combine(instanceDir, "config.json");
        InstanceConfig? config;
        if (vmId == "base")
        {
            config = new InstanceConfig
            {
                Id = "base",
                Name = "ReVM Base",
                CpuCount = 4,
                RamMB = 6144,
                StorageGB = 16,
                CreatedAt = DateTime.UtcNow
            };
        }
        else
        {
            if (!File.Exists(configPath))
            {
                Log($"Instance {vmId} not found (no config)");
                return (false, $"Instance {vmId} not found");
            }

            config = JsonSerializer.Deserialize<InstanceConfig>(
                File.ReadAllText(configPath));
            if (config == null)
            {
                Log($"Instance {vmId}: corrupt config");
                return (false, "Corrupt config");
            }
        }

        var vmName = GetVmName(vmId);

        // ── Check VM exists in Hyper-V ──
        var (existsOk, existsOut, _) = RunPowerShell(
            "if (Get-VM -Name '" + vmName + "' -ErrorAction SilentlyContinue) " +
            "{ 'EXISTS' } else { 'NOT_FOUND' }",
            10000);

        if (!existsOk || existsOut?.Trim() != "EXISTS")
        {
            Log($"VM {vmName} not found in Hyper-V");
            return (false,
                $"Hyper-V VM '{vmName}' not found. The instance may need to be recreated.");
        }

        // ── Check if already running ──
        var (stateOk, stateOut, _) = RunPowerShell(
            $"(Get-VM -Name '{vmName}' -ErrorAction Stop).State",
            10000);

        var alreadyRunning = stateOk && stateOut?.Trim() == "Running";
        if (alreadyRunning)
        {
            Log($"VM {vmName} is already running; attaching display only");
        }
        else
        {
            // ── Start the VM ──
            Log($"Starting VM: {vmName}");
            var (startOk, _, startErr) = RunPowerShell(
                $"Start-VM -Name '{vmName}' -ErrorAction Stop; Write-Output 'STARTED'",
                30000);

            if (!startOk)
            {
                Log($"Failed to start VM {vmName}: {startErr}");
                return (false, $"Failed to start VM: {startErr}");
            }

            Log($"VM {vmName} started successfully");
        }

        // ── Preferred display: scrcpy over ADB (smooth 60 FPS stream) ──
        var scrcpyResult = StartScrcpyDisplayAsync(vmId, vmName, hostHandle, hostX, hostY, hostW, hostH)
            .GetAwaiter().GetResult();
        if (scrcpyResult.success)
            return (true, scrcpyResult.debug);

        Log($"scrcpy display unavailable, falling back to vmconnect: {scrcpyResult.debug}");
        StatusChanged?.Invoke("scrcpy unavailable; falling back to Hyper-V console");

        // ── Fallback display: vmconnect.exe console ──
        Log($"Launching vmconnect.exe for {vmName}");
        Process? vmconnectProc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _vmconnectExe,
                Arguments = $"localhost \"{vmName}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };
            vmconnectProc = Process.Start(psi);
            if (vmconnectProc == null)
                return (false, "Failed to start vmconnect.exe");
        }
        catch (Exception ex)
        {
            Log($"vmconnect.exe launch failed: {ex.Message}");
            return (false, $"Failed to start vmconnect.exe: {ex.Message}");
        }

        _vmconnectProcs[vmId] = vmconnectProc;

        // ── Async window embedding ──
        _ = EmbedDisplayWindow(vmId, vmconnectProc, hostHandle,
            hostX, hostY, hostW, hostH, "vmconnect");

        Log($"StartInstanceWithDebug fallback success: {vmName} (vmconnect PID {vmconnectProc.Id})");
        return (true,
            $"VM started: {vmName} (vmconnect PID {vmconnectProc.Id}; scrcpy unavailable: {scrcpyResult.debug})");
    }

    private async Task<(bool success, string debug)> StartScrcpyDisplayAsync(
        string vmId, string vmName, IntPtr hostHandle,
        int hostX, int hostY, int hostW, int hostH)
    {
        if (!File.Exists(_scrcpyExe))
            return (false, $"scrcpy.exe not found: {_scrcpyExe}");
        if (!File.Exists(_adbExe))
            return (false, $"adb.exe not found: {_adbExe}");

        StatusChanged?.Invoke("Waiting for Android ADB for high-FPS display...");
        var serial = await DiscoverAdbSerialAsync(vmId, vmName, TimeSpan.FromSeconds(10));
        if (string.IsNullOrWhiteSpace(serial))
            return (false, "No ADB device reachable. Enable Android TCP ADB or wait for guest network.");

        Log($"Launching scrcpy for serial {serial}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _scrcpyExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(_scrcpyExe) ?? _runtimeDir
            };

            foreach (var arg in new[]
            {
                "--serial", serial,
                "--window-title", $"ReVM-{vmName}",
                "--max-fps", "60",
                "--video-bit-rate", "24M",
                "--video-codec", "h264",
                "--no-audio",
                "--stay-awake",
                "--render-driver", "direct3d"
            }) psi.ArgumentList.Add(arg);

            var proc = Process.Start(psi);
            if (proc == null) return (false, "Process.Start returned null for scrcpy");

            _vmconnectProcs[vmId] = proc;
            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await proc.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(stderr)) Log($"scrcpy stderr: {stderr}");
                }
                catch { }
            });

            _ = EmbedDisplayWindow(vmId, proc, hostHandle, hostX, hostY, hostW, hostH, "scrcpy");
            StatusChanged?.Invoke("High-FPS scrcpy display attached");
            return (true, $"scrcpy display started for {serial} (PID {proc.Id})");
        }
        catch (Exception ex)
        {
            Log($"scrcpy launch failed: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private async Task<string?> DiscoverAdbSerialAsync(string vmId, string vmName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;

        await RunAdbAsync("kill-server");
        await RunAdbAsync("start-server");

        while (DateTime.UtcNow < deadline)
        {
            // Existing attached/connected device
            var (devicesOk, devicesOut) = await RunAdbCaptureAsync("devices");
            if (devicesOk)
            {
                var serial = ParseFirstAdbDevice(devicesOut);
                if (!string.IsNullOrWhiteSpace(serial))
                    return serial;
                last = devicesOut;
            }

            // Try Hyper-V-reported IP address, if integration services can see one.
            var ip = await ResolveVmIpAsync(vmId);
            if (ip != null)
            {
                foreach (var target in new[] { $"{ip}:5555", ip })
                {
                    var (_, connectOut) = await RunAdbCaptureAsync($"connect {target}");
                    Log($"adb connect {target}: {connectOut}");
                }
            }

            await Task.Delay(3000);
        }

        Log($"ADB discovery timed out for {vmName}. Last adb devices output: {last}");
        return null;
    }

    private static string? ParseFirstAdbDevice(string output)
    {
        foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "device")
                return parts[0];
        }
        return null;
    }

    /// <summary>Start a VM; throws on failure.</summary>
    public void StartInstance(string vmId, IntPtr hostHandle,
        int x, int y, int w, int h)
    {
        var (success, debug) = StartInstanceWithDebug(
            vmId, hostHandle, x, y, w, h);
        if (!success)
            throw new InvalidOperationException(debug);
    }

    /// <summary>Stop a VM and kill its vmconnect window.</summary>
    public void StopInstance(string vmId)
    {
        Log($"StopInstance: {vmId}");

        _embeddedWindows.Remove(vmId);

        // Kill vmconnect process
        if (_vmconnectProcs.TryGetValue(vmId, out var vmconnectProc))
        {
            try
            {
                if (!vmconnectProc.HasExited)
                {
                    vmconnectProc.CloseMainWindow();
                    if (!vmconnectProc.WaitForExit(3000))
                        vmconnectProc.Kill();
                }
            }
            catch (Exception ex)
            {
                Log($"Error killing vmconnect for {vmId}: {ex.Message}");
            }
            finally
            {
                vmconnectProc.Dispose();
                _vmconnectProcs.Remove(vmId);
            }
        }

        // Stop the Hyper-V VM
        var vmName = GetVmName(vmId);
        var (stopOk, _, stopErr) = RunPowerShell(
            $"Stop-VM -Name '{vmName}' -Force -ErrorAction Stop; Write-Output 'STOPPED'",
            30000);

        if (!stopOk)
            Log($"Warning: Failed to stop VM {vmName}: {stopErr}");
        else
            Log($"VM {vmName} stopped");
    }

    /// <summary>Stop all running VMs.</summary>
    public void StopAll()
    {
        Log("StopAll called");
        foreach (var kvp in _vmconnectProcs.ToList())
            StopInstance(kvp.Key);
    }

    /// <summary>Delete an instance: stop VM, remove from Hyper-V, delete files.</summary>
    public void DeleteInstance(string vmId)
    {
        Log($"DeleteInstance: {vmId}");

        StopInstance(vmId);

        var vmName = GetVmName(vmId);

        // Remove the Hyper-V VM
        var (removeOk, _, removeErr) = RunPowerShell(
            $"Remove-VM -Name '{vmName}' -Force -ErrorAction Stop; Write-Output 'REMOVED'",
            15000);

        if (!removeOk)
            Log($"Warning: Failed to remove VM {vmName}: {removeErr}");

        // Delete instance directory
        var dir = Path.Combine(_instancesDir, vmId);
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, true);
                Log($"Instance directory deleted: {dir}");
            }
            catch (Exception ex)
            {
                Log($"Error deleting instance directory: {ex.Message}");
            }
        }
    }

    /// <summary>Reposition / resize the embedded vmconnect window.</summary>
    public void ResizeEmbeddedWindow(string vmId,
        int screenX, int screenY, int w, int h)
    {
        if (!_embeddedWindows.TryGetValue(vmId, out var hWnd)) return;

        var parent = GetParent(hWnd);
        var pt = new POINT { X = screenX, Y = screenY };
        ScreenToClient(parent, ref pt);
        MoveWindow(hWnd, pt.X, pt.Y, w, h, true);

        // Also try to set Android resolution
        _ = SetAndroidResolutionAsync(vmId, w, h);
    }

    /// <summary>
    /// Set Android logical display resolution via ADB.
    /// Resolves the VM's IP from Hyper-V, then issues wm size/density commands.
    /// </summary>
    public async Task SetAndroidResolutionAsync(string vmId, int w, int h)
    {
        Log($"SetAndroidResolutionAsync: {vmId} -> {w}x{h}");
        try
        {
            var ip = await ResolveVmIpAsync(vmId);
            if (ip == null) return;

            await RunAdbAsync($"connect {ip}:5555");
            await RunAdbAsync($"-s {ip}:5555 shell wm size reset");
            await RunAdbAsync($"-s {ip}:5555 shell wm size {w}x{h}");
            int density = Math.Max(160, w * 320 / 1080);
            await RunAdbAsync($"-s {ip}:5555 shell wm density {density}");
        }
        catch (Exception ex)
        {
            Log($"SetAndroidResolutionAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Rotate Android display via ADB. 0=portrait, 1=landscape.
    /// </summary>
    public async Task SetAndroidRotationAsync(string vmId, int rotation)
    {
        Log($"SetAndroidRotationAsync: {vmId} -> rotation={rotation}");
        try
        {
            var ip = await ResolveVmIpAsync(vmId);
            if (ip == null) return;

            await RunAdbAsync($"connect {ip}:5555");
            await RunAdbAsync(
                $"-s {ip}:5555 shell settings put system user_rotation {rotation}");
        }
        catch (Exception ex)
        {
            Log($"SetAndroidRotationAsync error: {ex.Message}");
        }
    }

    private static void ClearVhdxFileAttributes(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            foreach (var cmd in new[]
            {
                $"compact.exe /U /I /Q \"{path}\"",
                $"cipher.exe /D \"{path}\"",
                $"fsutil.exe sparse setflag \"{path}\" 0"
            })
            {
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(5000);
                }
                catch { }
            }
        }
        catch { }
    }

    // ── Private helpers ─────────────────────────────────────────

    private static string GetVmName(string instanceId) =>
        instanceId == "base" ? "ReVM-Base" : $"ReVM_{instanceId}";

    /// <summary>Resolve a VM's first IPv4 address via Hyper-V.</summary>
    private async Task<string?> ResolveVmIpAsync(string vmId)
    {
        var vmName = GetVmName(vmId);
        var (ok, output, _) = await Task.Run(() => RunPowerShell(
            "$ip = (Get-VMNetworkAdapter -VMName '" + vmName + "' " +
            "-ErrorAction SilentlyContinue).IPAddresses | " +
            "Where-Object { $_ -match '^\\d+\\.\\d+\\.\\d+\\.\\d+' } | " +
            "Select-Object -First 1; " +
            "if ($ip) { $ip } else { 'NO_IP' }",
            10000));

        if (ok && output?.Trim() is string ip &&
            ip != "NO_IP" && ip.Contains('.'))
        {
            Log($"Resolved VM IP for {vmName}: {ip.Trim()}");
            return ip.Trim();
        }

        Log($"Could not resolve VM IP for {vmName}");
        return null;
    }

    private async Task RunAdbAsync(string args)
    {
        _ = await RunAdbCaptureAsync(args);
    }

    private async Task<(bool success, string output)> RunAdbCaptureAsync(string args)
    {
        try
        {
            if (!File.Exists(_adbExe))
                return (false, $"ADB missing: {_adbExe}");

            var psi = new ProcessStartInfo(_adbExe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _runtimeDir
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "Process.Start returned null");
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var output = ((await stdoutTask) + "\n" + (await stderrTask)).Trim();
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Wait for a display process to create its window, then strip chrome
    /// and reparent it into the WPF host.
    /// </summary>
    private async Task EmbedDisplayWindow(string vmId, Process proc,
        IntPtr hostHandle, int screenX, int screenY, int w, int h, string displayKind)
    {
        Log($"EmbedDisplayWindow: waiting for {displayKind} window (PID {proc.Id})");

        IntPtr displayHwnd = IntPtr.Zero;

        // scrcpy/vmconnect can take several seconds to connect and render
        for (int i = 0; i < 120; i++) // up to 60 s
        {
            await Task.Delay(500);
            if (proc.HasExited)
            {
                Log($"{displayKind} exited during embedding wait (code {proc.ExitCode})");
                return;
            }

            displayHwnd = FindWindowByProcess(proc);
            if (displayHwnd != IntPtr.Zero)
            {
                Log($"Found {displayKind} window: 0x{displayHwnd:X} " +
                    $"after {(i + 1) * 500}ms");
                break;
            }
        }

        if (displayHwnd == IntPtr.Zero)
        {
            Log($"{displayKind} window never appeared");
            return;
        }

        // Strip the full top-level-window shell and make it a child.
        // If WS_POPUP/sysmenu/app-window bits remain, vmconnect/scrcpy can appear
        // as a whole floating overlay instead of a clean child inside DisplayHost.
        var style = GetWindowLong(displayHwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU |
                   WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_POPUP);
        style |= WS_CHILD;
        SetWindowLong(displayHwnd, GWL_STYLE, style);

        var exStyle = GetWindowLong(displayHwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(displayHwnd, GWL_EXSTYLE, exStyle);

        SetParent(displayHwnd, hostHandle);

        // Convert screen coordinates to parent client coordinates
        var pt = new POINT { X = screenX, Y = screenY };
        ScreenToClient(hostHandle, ref pt);
        Log($"Embedding {displayKind}: screen=({screenX},{screenY}) client=({pt.X},{pt.Y}) size={w}x{h}");

        SetWindowPos(displayHwnd, HWND_TOP,
            pt.X, pt.Y, w, h, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        _embeddedWindows[vmId] = displayHwnd;
        Log($"{displayKind} window embedded: {vmId}");
        WindowCreated?.Invoke(vmId, displayHwnd);
    }

    /// <summary>
    /// Find the first visible top-level window owned by a process.
    /// </summary>
    private static IntPtr FindWindowByProcess(Process proc)
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            foreach (ProcessThread thread in proc.Threads)
            {
                EnumThreadWindows((uint)thread.Id, (hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == proc.Id && IsWindowVisible(hWnd))
                    {
                        found = hWnd;
                        return false; // stop enumeration
                    }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) break;
            }
        }
        catch
        {
            // Process may have exited during enumeration
        }
        return found;
    }
}

// ── Shared model (moved from QemuService.cs) ────────────────────
public class InstanceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int CpuCount { get; set; } = 4;
    public int RamMB { get; set; } = 6144;
    public int StorageGB { get; set; } = 16;
    public DateTime CreatedAt { get; set; }
}
