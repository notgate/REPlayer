using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ReVM;

public class QemuService
{
    private readonly string _baseDir;
    private readonly string _runtimeDir;
    private readonly string _instancesDir;
    private readonly string _qemuExe;
    private readonly string _qemuImgExe;
    private readonly string _baseImagePath;
    private readonly string _baseImageReadyPath;
    private readonly Dictionary<string, Process> _running = new();
    private readonly Dictionary<string, int> _monitorPorts = new();
    private readonly Dictionary<string, int> _adbPorts = new();
    private readonly Dictionary<string, IntPtr> _embeddedWindows = new();

    public event Action<string, IntPtr>? WindowCreated;
    public event Action<string>? StatusChanged;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_CHILD = 0x40000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public QemuService()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        _baseDir = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", ".."));
        _runtimeDir = Path.Combine(_baseDir, "runtime");
        _instancesDir = Path.Combine(_baseDir, "instances");
        _qemuExe = Path.Combine(_runtimeDir, "qemu-system-x86_64.exe");
        _qemuImgExe = Path.Combine(_runtimeDir, "qemu-img.exe");
        _baseImagePath = Path.Combine(_runtimeDir, "android-base.qcow2");
        _baseImageReadyPath = Path.Combine(_runtimeDir, ".base-ready");

        Directory.CreateDirectory(_runtimeDir);
        Directory.CreateDirectory(_instancesDir);
    }

    public bool CheckEngine() =>
        File.Exists(_qemuExe) && RunSimple(_qemuExe, "--version", 3000);

    public bool IsBaseImageReady() => File.Exists(_baseImageReadyPath);

    public List<VmInstance> GetInstances()
    {
        var instances = new List<VmInstance>();
        if (!Directory.Exists(_instancesDir)) return instances;
        foreach (var dir in Directory.GetDirectories(_instancesDir))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;
            try
            {
                var config = JsonSerializer.Deserialize<InstanceConfig>(File.ReadAllText(configPath));
                if (config == null) continue;
                var diskPath = Path.Combine(dir, "system.qcow2");
                var status = _running.ContainsKey(config.Id) ? "running"
                    : !File.Exists(diskPath) ? "broken"
                    : "stopped";
                instances.Add(new VmInstance
                {
                    Name = config.Name, Id = config.Id,
                    CpuCount = config.CpuCount, RamMB = config.RamMB, StorageGB = config.StorageGB,
                    Status = status
                });
            }
            catch { }
        }
        return instances;
    }

    /// <summary>
    /// One-time automated Android-x86 installation.
    /// Launches QEMU with -display sdl and -monitor tcp, then drives the
    /// text-mode installer via QEMU monitor sendkey commands.
    /// Zero user interaction. Takes ~3-5 minutes.
    /// </summary>
    public async Task<(bool success, string message)> EnsureBaseImageAsync(CancellationToken ct = default)
    {
        if (File.Exists(_baseImageReadyPath))
            return (true, "Base image already exists");

        // Clean up stale files
        try { File.Delete(_baseImagePath); } catch { }
        try { File.Delete(_baseImageReadyPath); } catch { }

        var isoPath = Path.Combine(_runtimeDir, "android-x86_64-9.0-r2.iso");
        if (!File.Exists(isoPath))
            return (false, "Android ISO not found in runtime/");

        StatusChanged?.Invoke("Creating 16GB disk image...");

        // Create blank qcow2
        await Task.Run(() => RunQemuImg($"create -f qcow2 \"{_baseImagePath}\" 16G", _runtimeDir), ct);

        StatusChanged?.Invoke("Starting Android installer...");

        var accel = DetectAcceleration();
        var cpuModel = (accel == "kvm" || accel == "hvf" || accel == "whpx") ? "host" : "max";
        var monitorPort = 14444;

        var psi = new ProcessStartInfo
        {
            FileName = _qemuExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _runtimeDir
        };

        foreach (var a in new[]
        {
            "-L", Path.Combine(_runtimeDir, "share"),
            "-machine", $"pc,accel={accel}",
            "-cpu", cpuModel,
            "-smp", "2",
            "-m", "2048",
            "-vga", "std",
            "-display", "sdl",
            "-drive", $"file={_baseImagePath},if=ide,format=qcow2",
            "-cdrom", isoPath,
            "-boot", "d",
            "-net", "none",
            "-monitor", $"tcp:127.0.0.1:{monitorPort},server,nowait"
        }) psi.ArgumentList.Add(a);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
                return (false, "Failed to start QEMU");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start QEMU: {ex.Message}");
        }

        // Wait for monitor to be ready
        StatusChanged?.Invoke("Waiting for QEMU monitor...");
        var monitorReady = await WaitForMonitorAsync(monitorPort, 30_000, ct);
        if (!monitorReady)
        {
            if (!proc.HasExited) proc.Kill();
            return (false, "QEMU monitor never appeared. Display may not be available.");
        }

        // Drive the installer via monitor sendkey
        StatusChanged?.Invoke("Installing Android (automated, ~3 min)...");
        try
        {
            await DriveInstallerAsync(monitorPort, ct);
        }
        catch (OperationCanceledException)
        {
            if (!proc.HasExited) proc.Kill();
            return (false, "Install cancelled");
        }
        catch (Exception ex)
        {
            if (!proc.HasExited) proc.Kill();
            return (false, $"Install error: {ex.Message}");
        }

        // Wait for QEMU to exit (installer reboots, QEMU exits)
        StatusChanged?.Invoke("Waiting for installer to finish...");
        try
        {
            await Task.Run(() => proc.WaitForExit(120_000), ct);
        }
        catch (OperationCanceledException) { }

        if (!proc.HasExited)
        {
            proc.Kill();
            await Task.Delay(500, CancellationToken.None);
        }

        // Verify
        var info = new FileInfo(_baseImagePath);
        if (info.Length < 500_000_000)
        {
            return (false, $"Install may have failed: disk is only {info.Length / 1024 / 1024}MB");
        }

        File.WriteAllText(_baseImageReadyPath, DateTime.UtcNow.ToString("O"));
        StatusChanged?.Invoke("Android installed successfully");
        return (true, "Base image created successfully");
    }

    private static async Task<bool> WaitForMonitorAsync(int port, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, ct);
                return true;
            }
            catch
            {
                await Task.Delay(1000, ct);
            }
        }
        return false;
    }

    private static async Task DriveInstallerAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, ct);
        using var stream = client.GetStream();

        async Task SendKey(string key, int delayMs = 200)
        {
            var cmd = $"sendkey {key}\n";
            var bytes = Encoding.ASCII.GetBytes(cmd);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            await Task.Delay(delayMs, ct);
        }

        // Wait for ISOLINUX vesamenu to fully load
        await Task.Delay(8000, ct);

        // Esc dismisses the graphical vesamenu, falling back to text "boot:" prompt
        await SendKey("esc", 2000);

        // Type "install" at the text boot prompt (deterministic, no arrow keys)
        foreach (var c in "install")
            await SendKey(c.ToString(), 150);
        await SendKey("ret", 1000);

        // Wait for kernel + installer to fully boot (kernel load + init + dialog UI)
        await Task.Delay(15000, ct);

        // === "Choose Partition" → "Create/Modify partitions" ===
        // The "Create/Modify partitions" is the LAST entry in the partition list.
        // Navigate down to it (typically 1-2 items, but we overshoot safely with 5 downs)
        for (int i = 0; i < 5; i++)
            await SendKey("down", 200);
        await SendKey("ret", 1000);
        await Task.Delay(3000, ct);

        // === cfdisk: New → Primary → full size → Write → Quit ===
        // cfdisk opens on the free space. "New" is the default selection.
        await SendKey("ret", 500);   // New
        await Task.Delay(500, ct);
        await SendKey("ret", 500);   // Primary
        await Task.Delay(500, ct);
        await SendKey("ret", 500);   // Accept full size (default)
        await Task.Delay(500, ct);
        // Navigate to Write (5 down arrows from New)
        for (int i = 0; i < 5; i++)
            await SendKey("down", 200);
        await SendKey("ret", 500);   // Write
        await Task.Delay(500, ct);
        foreach (var c in "yes")
            await SendKey(c.ToString(), 150);
        await SendKey("ret", 1000);  // Confirm write
        await Task.Delay(1000, ct);
        await SendKey("down", 300);  // Quit (one down from Write)
        await SendKey("ret", 1000);
        await Task.Delay(3000, ct);

        // === Select sda1 (the new partition) ===
        // sda1 should be the first/only partition listed. Press Enter.
        await SendKey("ret", 1000);
        await Task.Delay(2000, ct);

        // === Format: ext4, then Yes ===
        // "Do not re-format" is first, ext4 is second. Down once to ext4.
        await SendKey("down", 500);
        await SendKey("ret", 500);
        await Task.Delay(500, ct);
        // Confirmation: "No" is default, left to "Yes"
        await SendKey("left", 500);
        await SendKey("ret", 1000);
        await Task.Delay(5000, ct);   // Formatting takes a few seconds

        // === Install GRUB: Yes ===
        // "Skip" is default, left to "Yes"
        await SendKey("left", 500);
        await SendKey("ret", 1000);
        await Task.Delay(3000, ct);

        // === /system read-write: Yes ===
        // "No" is default, left to "Yes"
        await SendKey("left", 500);
        await SendKey("ret", 1000);
        await Task.Delay(15000, ct);  // Copying system files (~900MB)

        // === Done — "Run Android-x86" / "Reboot" ===
        // "Run" is first, "Reboot" is second. Down once to Reboot.
        await Task.Delay(3000, ct);
        await SendKey("down", 500);
        await SendKey("ret", 1000);
        await Task.Delay(3000, ct);

        // Send quit to QEMU monitor
        var quitCmd = Encoding.ASCII.GetBytes("quit\n");
        await stream.WriteAsync(quitCmd, ct);
        await stream.FlushAsync(ct);
    }

    public void CreateInstance(string name, int cpuCount, int ramMB, int storageGB)
    {
        if (!File.Exists(_baseImageReadyPath))
            throw new InvalidOperationException("Base image not ready. Android must be installed first.");

        var id = Guid.NewGuid().ToString("N")[..12];
        var instanceDir = Path.Combine(_instancesDir, id);
        Directory.CreateDirectory(instanceDir);
        var diskPath = Path.Combine(instanceDir, "system.qcow2");

        RunQemuImg($"create -f qcow2 -b \"{_baseImagePath}\" -F qcow2 \"{diskPath}\"", instanceDir);

        File.WriteAllText(Path.Combine(instanceDir, "config.json"),
            JsonSerializer.Serialize(new InstanceConfig
            {
                Id = id, Name = name, CpuCount = cpuCount,
                RamMB = ramMB, StorageGB = storageGB, CreatedAt = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public (bool success, string debug) StartInstanceWithDebug(string vmId, IntPtr hostHandle, int hostX, int hostY, int hostW, int hostH)
    {
        var instanceDir = Path.Combine(_instancesDir, vmId);
        var configPath = Path.Combine(instanceDir, "config.json");
        if (!File.Exists(configPath))
            return (false, $"Instance {vmId} not found");

        var config = JsonSerializer.Deserialize<InstanceConfig>(File.ReadAllText(configPath));
        if (config == null)
            return (false, "Corrupt config");

        var diskPath = Path.Combine(instanceDir, "system.qcow2");
        if (!File.Exists(diskPath))
            return (false, $"Disk not found: {diskPath}");

        var accel = DetectAcceleration();
        // WHPX + -cpu host hangs Android-x86 9.0 kernel. Use max for compatibility.
        // Still hardware-accelerated — just a more compatible CPU feature set.
        var cpuModel = (accel == "kvm" || accel == "hvf") ? "host" : "max";
        var machine = accel == "whpx" ? "q35" : "pc";
        var monitorPort = 10000 + (int)(Convert.ToUInt32(vmId[..4], 16) % 50000);
        var adbPort = 20000 + (int)(Convert.ToUInt32(vmId[..4], 16) % 40000);
        var fridaPort = adbPort + 1;
        var mitmPort = adbPort + 2;
        _monitorPorts[vmId] = monitorPort;
        _adbPorts[vmId] = adbPort;

        var psi = new ProcessStartInfo
        {
            FileName = _qemuExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = instanceDir
        };

        foreach (var a in new[]
        {
            "-L", _runtimeDir + "\\share",
            "-name", config.Name,
            "-machine", $"{machine},accel={accel}",
            "-cpu", cpuModel,
            "-smp", config.CpuCount.ToString(),
            "-m", config.RamMB.ToString(),
            "-vga", "std",
            "-display", "sdl",
            "-drive", $"file={diskPath},if=ide,format=qcow2",
            "-netdev", $"user,id=net0,hostfwd=tcp::{adbPort}-:5555,hostfwd=tcp::{fridaPort}-:27042,hostfwd=tcp::{mitmPort}-:8080",
            "-device", "virtio-net-pci,netdev=net0",
            "-audiodev", "dsound,id=audio0",
            "-device", "intel-hda",
            "-device", "hda-duplex,audiodev=audio0",
            "-usb",
            "-device", "usb-tablet",
            "-rtc", "base=localtime",
            "-monitor", $"tcp:127.0.0.1:{monitorPort},server,nowait"
        }) psi.ArgumentList.Add(a);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
                return (false, "Process.Start returned null");
        }
        catch (Exception ex)
        {
            return (false, $"Process.Start failed: {ex.Message}");
        }

        Thread.Sleep(500);
        if (proc.HasExited)
        {
            var stderr = proc.StandardError.ReadToEnd();
            return (false, $"QEMU exited immediately (code {proc.ExitCode}): {stderr[..Math.Min(stderr.Length, 200)]}");
        }

        _running[vmId] = proc;

        _ = EmbedQemuWindow(vmId, proc, hostHandle, hostX, hostY, hostW, hostH);

        return (true, $"QEMU started (PID {proc.Id}, monitor :{monitorPort}, accel={accel})");
    }

    private async Task EmbedQemuWindow(string vmId, Process proc, IntPtr hostHandle, int screenX, int screenY, int w, int h)
    {
        IntPtr qemuHwnd = IntPtr.Zero;
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            if (proc.HasExited) return;

            qemuHwnd = FindWindowByProcess(proc);
            if (qemuHwnd != IntPtr.Zero) break;
        }

        if (qemuHwnd == IntPtr.Zero) return;

        var style = GetWindowLong(qemuHwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        style |= WS_CHILD;
        SetWindowLong(qemuHwnd, GWL_STYLE, style);

        SetParent(qemuHwnd, hostHandle);

        // Convert screen coordinates to parent client coordinates
        var pt = new POINT { X = screenX, Y = screenY };
        ScreenToClient(hostHandle, ref pt);

        SetWindowPos(qemuHwnd, HWND_TOP, pt.X, pt.Y, w, h, SWP_NOZORDER | SWP_FRAMECHANGED);

        _embeddedWindows[vmId] = qemuHwnd;
        WindowCreated?.Invoke(vmId, qemuHwnd);
    }

    private static IntPtr FindWindowByProcess(Process proc)
    {
        IntPtr found = IntPtr.Zero;
        foreach (ProcessThread thread in proc.Threads)
        {
            EnumThreadWindows((uint)thread.Id, (hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == proc.Id && IsWindowVisible(hWnd))
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) break;
        }
        return found;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public void StartInstance(string vmId, IntPtr hostHandle, int x, int y, int w, int h)
    {
        var (success, debug) = StartInstanceWithDebug(vmId, hostHandle, x, y, w, h);
        if (!success)
            throw new InvalidOperationException(debug);
    }

    public void StopInstance(string vmId)
    {
        _monitorPorts.Remove(vmId);
        _adbPorts.Remove(vmId);
        _embeddedWindows.Remove(vmId);
        if (!_running.TryGetValue(vmId, out var proc)) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(5000)) proc.Kill();
            }
        }
        catch { }
        finally { _running.Remove(vmId); proc.Dispose(); }
    }

    public void StopAll() { foreach (var kvp in _running.ToList()) StopInstance(kvp.Key); }

    public void DeleteInstance(string vmId)
    {
        StopInstance(vmId);
        var dir = Path.Combine(_instancesDir, vmId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public void ResizeEmbeddedWindow(string vmId, int screenX, int screenY, int w, int h)
    {
        if (!_embeddedWindows.TryGetValue(vmId, out var hWnd)) return;
        // Convert screen coordinates to parent client coordinates
        var parent = GetParent(hWnd);
        var pt = new POINT { X = screenX, Y = screenY };
        ScreenToClient(parent, ref pt);
        MoveWindow(hWnd, pt.X, pt.Y, w, h, true);

        // Set Android logical display to match the viewport
        _ = SetAndroidResolutionAsync(vmId, w, h);
    }

    /// <summary>
    /// Set Android logical display resolution via ADB.
    /// This makes Android render at the exact viewport size, like BlueStacks.
    /// </summary>
    public async Task SetAndroidResolutionAsync(string vmId, int w, int h)
    {
        if (!_adbPorts.TryGetValue(vmId, out var adbPort)) return;
        try
        {
            // Ensure ADB is connected to this instance
            await RunAdbAsync($"connect 127.0.0.1:{adbPort}");
            // Reset any previous override, then set new size
            await RunAdbAsync($"-s 127.0.0.1:{adbPort} shell wm size reset");
            await RunAdbAsync($"-s 127.0.0.1:{adbPort} shell wm size {w}x{h}");
            // Set density proportional to width (phone-like: ~320 DPI at 1080px wide)
            int density = Math.Max(160, w * 320 / 1080);
            await RunAdbAsync($"-s 127.0.0.1:{adbPort} shell wm density {density}");
        }
        catch { }
    }

    /// <summary>
    /// Rotate Android display via ADB. 0=portrait, 1=landscape.
    /// </summary>
    public async Task SetAndroidRotationAsync(string vmId, int rotation)
    {
        if (!_adbPorts.TryGetValue(vmId, out var adbPort)) return;
        try
        {
            await RunAdbAsync($"connect 127.0.0.1:{adbPort}");
            await RunAdbAsync($"-s 127.0.0.1:{adbPort} shell settings put system user_rotation {rotation}");
        }
        catch { }
    }

    private static async Task RunAdbAsync(string args)
    {
        var psi = new ProcessStartInfo("adb", args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p != null) await p.WaitForExitAsync();
    }

    private static string DetectAcceleration()
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            // Check if Hyper-V / WHPX is available via Windows features
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c dism /online /get-featureinfo /featurename:Microsoft-Hyper-V-All 2>&1 | findstr State")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);
                if (output.Contains("Enabled"))
                    return "whpx";
            }
            catch { }

            // Fallback: try systeminfo
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c systeminfo 2>&1 | findstr /i \"Hyper-V\"")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);
                if (output.Contains("Enabled") || output.Contains("Yes"))
                    return "whpx";
            }
            catch { }
        }
        if (Directory.Exists("/dev/kvm")) return "kvm";
        if (Directory.Exists("/System/Library/Frameworks/Hypervisor.framework")) return "hvf";
        return "tcg";
    }

    private static bool RunSimple(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(timeoutMs);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private void RunQemuImg(string arguments, string workDir)
    {
        var psi = new ProcessStartInfo(_qemuImgExe, arguments)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workDir };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start qemu-img");
        p.WaitForExit(30000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"qemu-img failed (exit {p.ExitCode}): {p.StandardError.ReadToEnd()}");
    }
}

public class InstanceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int CpuCount { get; set; } = 2;
    public int RamMB { get; set; } = 2048;
    public int StorageGB { get; set; } = 16;
    public DateTime CreatedAt { get; set; }
}
