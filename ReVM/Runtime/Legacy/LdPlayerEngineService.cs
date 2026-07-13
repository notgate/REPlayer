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

/// <summary>
/// LDPlayer-style backend for REPlayer.
///
/// Phase 1 is an LDPlayer bridge: use LDPlayer's proven headless VirtualBox/gfxstream
/// engine and dock its native render window into REPlayer's NativeChildHost. This removes
/// Hyper-V/vmconnect from the production path while we build a REPlayer-owned fastpipe /
/// gfxstream backend behind the same public API.
/// </summary>
public sealed class LdPlayerEngineService : IAndroidRuntimeBackend
{
    private readonly string _baseDir;
    private readonly string _runtimeDir;
    private readonly string _instancesDir;
    private readonly string _ldRoot;
    private readonly string _ldConsoleExe;
    private readonly string _dnPlayerExe;
    private readonly string _adbExe;
    private readonly Dictionary<string, IntPtr> _embeddedWindows = new();
    private readonly Dictionary<string, int> _instanceToLdIndex = new();

    public event Action<string, IntPtr>? WindowCreated;
    public event Action<string>? StatusChanged;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

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
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public LdPlayerEngineService()
    {
        _baseDir = RevmPaths.BaseDir;
        _runtimeDir = Path.Combine(_baseDir, "runtime");
        _instancesDir = Path.Combine(_baseDir, "instances");

        // Phase 1 bridge target. Later this becomes runtime\ldbox\... when bundled.
        _ldRoot = Directory.Exists(Path.Combine(_runtimeDir, "ldplayer9"))
            ? Path.Combine(_runtimeDir, "ldplayer9")
            : @"D:\LDPlayer\LDPlayer9";
        _ldConsoleExe = Path.Combine(_ldRoot, "ldconsole.exe");
        _dnPlayerExe = Path.Combine(_ldRoot, "dnplayer.exe");
        _adbExe = Path.Combine(_ldRoot, "adb.exe");

        Directory.CreateDirectory(_runtimeDir);
        Directory.CreateDirectory(_instancesDir);

        Log("LdPlayerEngineService initialized");
        Log($"  Base dir: {_baseDir}");
        Log($"  LD root: {_ldRoot}");
        Log($"  ldconsole: {_ldConsoleExe}");
        Log($"  dnplayer: {_dnPlayerExe}");
    }

    private void Log(string message)
    {
        try
        {
            var logDir = Path.Combine(_baseDir, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "ldplayer-engine.log"),
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    public bool CheckEngine()
    {
        var ok = File.Exists(_ldConsoleExe) && File.Exists(_dnPlayerExe);
        Log($"CheckEngine: {ok}");
        return ok;
    }

    public bool IsBaseImageReady() => CheckEngine();

    public Task<(bool success, string message)> EnsureBaseImageAsync(CancellationToken ct = default)
    {
        if (!CheckEngine())
            return Task.FromResult((false,
                "LDPlayer runtime not found. Install LDPlayer 9 at D:\\LDPlayer\\LDPlayer9 or bundle it under runtime\\ldplayer9."));
        return Task.FromResult((true, "LDPlayer-style runtime available."));
    }

    public List<VmInstance> GetInstances()
    {
        var result = new List<VmInstance>();
        if (!CheckEngine()) return result;

        var (ok, output, err) = RunProcess(_ldConsoleExe, "list2", 15000);
        if (!ok)
        {
            Log($"ldconsole list2 failed: {err}");
            result.Add(new VmInstance
            {
                Id = "ld-0",
                Name = "LDPlayer Bridge",
                CpuCount = 4,
                RamMB = 6144,
                StorageGB = 16,
                Status = "unknown"
            });
            _instanceToLdIndex["ld-0"] = 0;
            return result;
        }

        foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // list2 example: 0,LDPlayer,0,0,0,-1,-1,1280,720,240
            var parts = raw.Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var index)) continue;
            var id = $"ld-{index}";
            var running = parts.Length > 4 && parts[4] == "1";
            result.Add(new VmInstance
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(parts[1]) ? $"LDPlayer {index}" : parts[1],
                CpuCount = 4,
                RamMB = 6144,
                StorageGB = 16,
                Status = running ? "running" : "stopped"
            });
            _instanceToLdIndex[id] = index;
        }

        if (result.Count == 0)
        {
            result.Add(new VmInstance
            {
                Id = "ld-0",
                Name = "LDPlayer Bridge",
                CpuCount = 4,
                RamMB = 6144,
                StorageGB = 16,
                Status = "stopped"
            });
            _instanceToLdIndex["ld-0"] = 0;
        }

        return result;
    }

    public void CreateInstance(string name, int cpuCount, int ramMB, int storageGB)
    {
        if (!CheckEngine()) throw new InvalidOperationException("LDPlayer runtime not found.");
        var safeName = string.IsNullOrWhiteSpace(name) ? "ReVM-LD" : name.Trim();
        StatusChanged?.Invoke($"Creating LDPlayer-style instance: {safeName}...");
        var (ok, output, err) = RunProcess(_ldConsoleExe, $"add --name \"{safeName}\"", 60000);
        if (!ok) throw new InvalidOperationException($"ldconsole add failed: {err}\n{output}");

        RefreshLdConsoleProfile(cpuCount, ramMB);
    }

    public (bool success, string debug) StartInstanceWithDebug(string vmId, IntPtr hostHandle, int hostX, int hostY, int hostW, int hostH)
    {
        try
        {
            if (!CheckEngine()) return (false, "LDPlayer runtime not found.");
            var index = GetLdIndex(vmId);

            StatusChanged?.Invoke($"Launching LDPlayer backend index {index}...");
            RefreshLdConsoleProfile(4, 6144);

            var (launchOk, launchOut, launchErr) = RunProcess(_ldConsoleExe, $"launch --index {index}", 30000);
            Log($"ldconsole launch index={index} ok={launchOk} out={launchOut} err={launchErr}");
            if (!launchOk && !IsAlreadyRunning(index))
                return (false, $"ldconsole launch failed: {launchErr}\n{launchOut}");

            var hwnd = WaitForLdPlayerWindow(index, TimeSpan.FromSeconds(45));
            if (hwnd == IntPtr.Zero)
                return (false, "LDPlayer render window did not appear.");

            EmbedWindow(vmId, hwnd, hostHandle, hostW, hostH);
            _embeddedWindows[vmId] = hwnd;
            WindowCreated?.Invoke(vmId, hwnd);
            StatusChanged?.Invoke("LDPlayer backend embedded");
            return (true, "LDPlayer backend embedded");
        }
        catch (Exception ex)
        {
            Log($"StartInstanceWithDebug exception: {ex}");
            return (false, ex.Message);
        }
    }

    public void StartInstance(string vmId, IntPtr hostHandle, int x, int y, int w, int h)
    {
        var (success, debug) = StartInstanceWithDebug(vmId, hostHandle, x, y, w, h);
        if (!success) throw new InvalidOperationException(debug);
    }

    public void ResizeEmbeddedWindow(string vmId, int x, int y, int w, int h)
    {
        if (_embeddedWindows.TryGetValue(vmId, out var hwnd) && hwnd != IntPtr.Zero)
            MoveWindow(hwnd, 0, 0, Math.Max(1, w), Math.Max(1, h), true);
    }

    public void StopInstance(string vmId)
    {
        var index = GetLdIndex(vmId);
        RunProcess(_ldConsoleExe, $"quit --index {index}", 15000);
        _embeddedWindows.Remove(vmId);
    }

    public void StopAll()
    {
        if (File.Exists(_ldConsoleExe)) RunProcess(_ldConsoleExe, "quitall", 15000);
        _embeddedWindows.Clear();
    }

    public void DeleteInstance(string vmId)
    {
        var index = GetLdIndex(vmId);
        if (index == 0) throw new InvalidOperationException("LDPlayer base instance cannot be deleted from REPlayer.");
        RunProcess(_ldConsoleExe, $"remove --index {index}", 30000);
    }

    public Task SetAndroidResolutionAsync(string vmId, int w, int h)
    {
        var index = GetLdIndex(vmId);
        return Task.Run(() => RunProcess(_ldConsoleExe,
            $"modify --index {index} --resolution {Math.Max(320, w)},{Math.Max(240, h)},240", 15000));
    }

    public Task SetAndroidRotationAsync(string vmId, int rotation)
    {
        // LDPlayer handles rotation internally; keep hook for toolbar compatibility.
        return Task.CompletedTask;
    }

    public Task SendAndroidTouchAsync(string vmId, int fromX, int fromY, int toX, int toY, int durationMs)
    {
        return Task.CompletedTask;
    }

    public string GetAdbPath() => File.Exists(_adbExe) ? _adbExe : "adb";

    private int GetLdIndex(string vmId)
    {
        if (_instanceToLdIndex.TryGetValue(vmId, out var index)) return index;
        if (vmId.StartsWith("ld-", StringComparison.OrdinalIgnoreCase) && int.TryParse(vmId[3..], out index)) return index;
        return 0;
    }

    private bool IsAlreadyRunning(int index)
    {
        var (ok, output, _) = RunProcess(_ldConsoleExe, $"isrunning --index {index}", 10000);
        return ok && output.Contains("running", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshLdConsoleProfile(int cpuCount, int ramMB)
    {
        try
        {
            RunProcess(_ldConsoleExe, "globalsetting --fps 60 --audio 1 --fastplay 1 --cleanmode 0", 15000);
            RunProcess(_ldConsoleExe, $"modify --index 0 --cpu {Math.Clamp(cpuCount, 1, 4)} --memory {Math.Clamp(ramMB, 1024, 8192)}", 15000);
        }
        catch { }
    }

    private IntPtr WaitForLdPlayerWindow(int index, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = FindLdPlayerWindow();
            if (hwnd != IntPtr.Zero) return hwnd;
            Thread.Sleep(500);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindLdPlayerWindow()
    {
        IntPtr candidate = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;

            string title = GetWindowText(hwnd);
            string cls = GetClassName(hwnd);

            bool titleMatch = title.Contains("LDPlayer", StringComparison.OrdinalIgnoreCase) ||
                              title.Contains("雷电", StringComparison.OrdinalIgnoreCase);
            bool classMatch = cls.Contains("LD", StringComparison.OrdinalIgnoreCase) ||
                              cls.Contains("Qt", StringComparison.OrdinalIgnoreCase) ||
                              cls.Contains("SDL", StringComparison.OrdinalIgnoreCase);

            if (titleMatch || classMatch)
            {
                candidate = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return candidate;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private void EmbedWindow(string vmId, IntPtr hwnd, IntPtr hostHandle, int w, int h)
    {
        Log($"Embedding LDPlayer window vmId={vmId} hwnd=0x{hwnd.ToInt64():X} host=0x{hostHandle.ToInt64():X} size={w}x{h}");

        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_POPUP);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLong(hwnd, GWL_STYLE, style);

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        SetParent(hwnd, hostHandle);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, Math.Max(1, w), Math.Max(1, h),
            SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static (bool success, string output, string error) RunProcess(string fileName, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "", "Process.Start returned null");
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, output, $"Timed out after {timeoutMs}ms. {error}");
            }
            return (p.ExitCode == 0, output.Trim(), error.Trim());
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}

public class InstanceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int CpuCount { get; set; } = 4;
    public int RamMB { get; set; } = 6144;
    public int StorageGB { get; set; } = 16;
    public DateTime CreatedAt { get; set; }
}

public class VmInstance : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int CpuCount { get; set; }
    public int RamMB { get; set; }
    public int StorageGB { get; set; }
    public string BootProfile { get; set; } = "utility-root";
    public DateTime CreatedAt { get; set; }

    private string _status = "stopped";
    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RunActionText)));
        }
    }

    public string RunActionText => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(Status, "starting", StringComparison.OrdinalIgnoreCase)
        ? "Stop"
        : "Start";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
