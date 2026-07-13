using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReVM.NativeRendering;

namespace ReVM;

/// <summary>
/// REPlayer-owned runtime backend. Production target: a headless x86_64 Android
/// runtime with a gfxstream/fastpipe-style host renderer. The current
/// revm-managed-bootstrap mode starts a real REPlayer-owned VMM process and render
/// host artifact, then reports Android boot as blocked until real images/engine
/// are installed.
/// </summary>
public sealed class RevmNativeRuntimeService : IAndroidRuntimeBackend
{
    private readonly string _baseDir;
    private readonly string _runtimeRoot;
    private readonly string _manifestPath;
    private readonly string _logPath;
    private readonly string _rendererDebugLogPath;
    private readonly Dictionary<string, RenderHostSession> _renderSessions = new();
    private readonly Dictionary<string, RendererReadinessTracker> _rendererReadiness = new();
    private readonly Dictionary<string, Process> _vmmProcesses = new();
    private readonly Dictionary<string, string> _adbSerials = new();
    private RevmRuntimeManifest? _manifest;

    public event Action<string, IntPtr>? WindowCreated;
    public event Action<string>? StatusChanged;

    public RevmNativeRuntimeService()
    {
        _baseDir = RevmPaths.BaseDir;
        _runtimeRoot = Path.Combine(_baseDir, "runtime", "revm-engine");
        _manifestPath = Path.Combine(_runtimeRoot, "config", "runtime.json");
        _logPath = Path.Combine(_baseDir, "logs", "revm-startup.log");
        _rendererDebugLogPath = Path.Combine(_baseDir, "logs", "revm-renderer-debug.log");
        TryLoadManifest();
    }

    public bool CheckEngine() => TryLoadManifest() && ValidateManifest(out _);
    public bool IsBaseImageReady() => CheckEngine();

    public Task<(bool success, string message)> EnsureBaseImageAsync(CancellationToken ct = default)
    {
        if (!TryLoadManifest())
            return Task.FromResult((false,
                "REPlayer native runtime is not installed yet. Expected runtime\\revm-engine\\config\\runtime.json."));

        if (!ValidateManifest(out var error))
            return Task.FromResult((false, error));

        return Task.FromResult((true, IsManagedBootstrap
            ? "REPlayer managed runtime bootstrap is available."
            : "REPlayer native runtime is available."));
    }

    public List<VmInstance> GetInstances()
    {
        if (!CheckEngine()) return new List<VmInstance>();
        return new List<VmInstance>
        {
            new()
            {
                Id = "native-0",
                Name = IsManagedBootstrap ? "REPlayer Managed Runtime Bootstrap" : "REPlayer Native Runtime",
                CpuCount = 4,
                RamMB = 6144,
                StorageGB = 16,
                Status = "stopped"
            }
        };
    }

    public void CreateInstance(string name, int cpuCount, int ramMB, int storageGB)
    {
        throw new NotSupportedException("Native REPlayer instance creation is not implemented until disk/image templates are real.");
    }

    public (bool success, string debug) StartInstanceWithDebug(string vmId, IntPtr hostHandle, int hostX, int hostY, int hostW, int hostH)
    {
        if (!CheckEngine())
            return (false, "Native REPlayer runtime missing or invalid.");

        return IsManagedBootstrap
            ? StartManagedBootstrap(vmId, hostHandle, Math.Max(hostW, 1), Math.Max(hostH, 1))
            : StartManagedBootstrap(vmId, hostHandle, Math.Max(hostW, 1), Math.Max(hostH, 1));
    }

    public void StartInstance(string vmId, IntPtr hostHandle, int x, int y, int w, int h)
    {
        var (success, debug) = StartInstanceWithDebug(vmId, hostHandle, x, y, w, h);
        if (!success) throw new InvalidOperationException(debug);
    }

    public void ResizeEmbeddedWindow(string vmId, int x, int y, int w, int h)
    {
        if (_renderSessions.TryGetValue(vmId, out var session))
            session.Controller.Resize(w, h);
    }

    public void StopInstance(string vmId)
    {
        _adbSerials.Remove(vmId);
        if (_vmmProcesses.Remove(vmId, out var proc))
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
        }

        _rendererReadiness.Remove(vmId);

        if (_renderSessions.Remove(vmId, out var session))
        {
            session.Controller.StopAsync().GetAwaiter().GetResult();
            session.Controller.Dispose();
        }
    }

    public void StopAll()
    {
        foreach (var vmId in _vmmProcesses.Keys.ToArray()) StopInstance(vmId);
        foreach (var session in _renderSessions.Values)
        {
            session.Controller.StopAsync().GetAwaiter().GetResult();
            session.Controller.Dispose();
        }
        _renderSessions.Clear();
        _rendererReadiness.Clear();
        _adbSerials.Clear();
    }

    public void DeleteInstance(string vmId) => throw new NotSupportedException("Native runtime deletion is not implemented yet.");

    public Task SetAndroidResolutionAsync(string vmId, int w, int h)
    {
        // Native REPlayer-owned QEMU currently exposes a fixed 1024x768 std-VGA
        // scanout mirrored into shm-frame-ring-v1. WPF/window resize must resize
        // only the child HWND/presenter target (handled by ResizeEmbeddedWindow),
        // not the guest/frame-ring resolution. Mutating this on the debounce path
        // desynchronizes the scanout transport and can turn the view gray after
        // mouse interaction or REPlayer window resize.
        return Task.CompletedTask;
    }

    public Task SetAndroidRotationAsync(string vmId, int rotation)
    {
        if (_renderSessions.TryGetValue(vmId, out var session))
            session.Controller.SetRotation(rotation);
        return Task.CompletedTask;
    }

    public async Task SendAndroidTouchAsync(string vmId, int fromX, int fromY, int toX, int toY, int durationMs)
    {
        if (!_adbSerials.TryGetValue(vmId, out var serial) || string.IsNullOrWhiteSpace(serial))
            return;

        fromX = Math.Clamp(fromX, 0, 1023);
        toX = Math.Clamp(toX, 0, 1023);
        fromY = Math.Clamp(fromY, 0, 767);
        toY = Math.Clamp(toY, 0, 767);
        durationMs = Math.Clamp(durationMs, 40, 1200);

        var dx = Math.Abs(toX - fromX);
        var dy = Math.Abs(toY - fromY);
        var inputArgs = dx <= 8 && dy <= 8
            ? $"-s {serial} shell input tap {toX} {toY}"
            : $"-s {serial} shell input swipe {fromX} {fromY} {toX} {toY} {durationMs}";

        try
        {
            await RunAdbControlAsync(inputArgs, TimeSpan.FromSeconds(2));
            LogRendererDebug(vmId, $"android.input {inputArgs}");
        }
        catch (Exception ex)
        {
            LogRendererDebug(vmId, $"android.input failed: {ex.Message}");
        }
    }

    public string GetAdbPath() => Path.Combine(_runtimeRoot, "bin", "adb.exe");

    public RendererReadinessSnapshot? GetRendererReadinessSnapshot(string vmId) =>
        _rendererReadiness.TryGetValue(vmId, out var tracker) ? tracker.Snapshot(vmId) : null;

    private bool IsManagedBootstrap => string.Equals(_manifest?.EngineKind, "revm-managed-bootstrap", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(_manifest?.EngineKind, "revm-dev-scaffold", StringComparison.OrdinalIgnoreCase);

    private (bool success, string debug) StartManagedBootstrap(string vmId, IntPtr hostHandle, int w, int h)
    {
        Log($"StartManagedBootstrap begin vmId={vmId} host=0x{hostHandle.ToInt64():X} size={w}x{h}");
        StopInstance(vmId);

        var hwndResult = CreateRenderSurface(vmId, hostHandle, w, h);
        if (!hwndResult.success) return hwndResult;

        var vmmExe = Full(_manifest!.VmmPath);
        var pipeName = $"revm-vmm-{vmId}-{Environment.ProcessId}";

        var psi = new ProcessStartInfo(vmmExe)
        {
            WorkingDirectory = _runtimeRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--manifest");
        psi.ArgumentList.Add(_manifestPath);
        psi.ArgumentList.Add("--log-dir");
        psi.ArgumentList.Add(Path.Combine(_baseDir, "logs"));
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--renderer-endpoint");
        psi.ArgumentList.Add(_renderSessions[vmId].Controller.TransportEndpoint);
        psi.ArgumentList.Add("--renderer-api");
        psi.ArgumentList.Add(GetRendererApiSetting());
        var adbSerial = $"127.0.0.1:{GetAvailableTcpPort()}";
        _adbSerials[vmId] = adbSerial;
        psi.ArgumentList.Add("--adb-serial");
        psi.ArgumentList.Add(adbSerial);
        LogRendererDebug(vmId, $"native runtime assigned ADB control serial {adbSerial}; ADB remains control/readiness only, not display");

        var blocked = RuntimeComponentsArePlaceholders();
        if (blocked)
        {
            psi.ArgumentList.Add("--synthetic-producer-frames");
            psi.ArgumentList.Add("1800");
            psi.ArgumentList.Add("--synthetic-producer-interval-ms");
            psi.ArgumentList.Add("33");
            psi.ArgumentList.Add("--validate-after-synthetic-producer");
            LogRendererDebug(vmId, "enabled bounded WPF renderer test producer: frames=1800 intervalMs=33");
        }

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Log($"vmm stdout: {e.Data}");
                ForwardRuntimeEvent(e.Data);
            };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"vmm stderr: {e.Data}"); };
            proc.Exited += (_, _) =>
            {
                Log($"VMM process exited vmId={vmId} pid={proc.Id} code={SafeExitCode(proc)}");
                StatusChanged?.Invoke($"REPlayer VMM exited ({SafeExitCode(proc)})");
            };

            if (!proc.Start()) return (false, "Failed to start revm-vmm.exe");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _vmmProcesses[vmId] = proc;
            Log($"revm-vmm.exe started pid={proc.Id} pipe={pipeName}");
            LogRendererDebug(vmId, $"revm-vmm.exe started pid={proc.Id}; endpoint={_renderSessions[vmId].Controller.TransportEndpoint}");
            StatusChanged?.Invoke("REPlayer VMM bootstrap started");
        }
        catch (Exception ex)
        {
            Log($"Failed to start revm-vmm.exe: {ex}");
            return (false, $"Failed to start revm-vmm.exe: {ex.Message}");
        }

        WindowCreated?.Invoke(vmId, _renderSessions[vmId].Controller.ChildHwnd);

        return (true, blocked
            ? "REPlayer WPF renderer test is running; Android boot blocked because system/data/agent are placeholders."
            : "REPlayer VMM bootstrap is running; Android boot integration next.");
    }

    private (bool success, string debug) CreateRenderSurface(string vmId, IntPtr hostHandle, int w, int h)
    {
        var options = new RenderHostOptions(vmId, Full(_manifest!.RendererPath), Path.Combine(_baseDir, "logs", "renderer-transport"), GetRendererApiSetting());
        var controller = new RenderHostController(options);

        try
        {
            controller.StartAsync(hostHandle, w, h, CancellationToken.None).GetAwaiter().GetResult();
            var readiness = new RendererReadinessTracker();
            readiness.TryRecord("renderer.pending", "Render host controller created; waiting for transport attach", out var pendingSummary);
            _rendererReadiness[vmId] = readiness;
            controller.AttachTransportAsync(controller.TransportEndpoint, CancellationToken.None).GetAwaiter().GetResult();
            readiness.TryRecord("transport.attached", $"controller status={controller.LastStatus}", out var attachedSummary);
            _renderSessions[vmId] = new RenderHostSession(vmId, controller, $"transport.attached:{controller.LastStatus}");
            Log($"Render host controller started hwnd=0x{controller.ChildHwnd.ToInt64():X} status={controller.LastStatus} endpoint={controller.TransportEndpoint}");
            LogRendererDebug(vmId, $"render host started hwnd=0x{controller.ChildHwnd.ToInt64():X} status={controller.LastStatus} readiness={attachedSummary}");
            StatusChanged?.Invoke($"renderer-debug:|{DateTime.Now:HH:mm:ss.fff}|renderer.pending {pendingSummary}");
            StatusChanged?.Invoke($"renderer-debug:|{DateTime.Now:HH:mm:ss.fff}|transport.attached {attachedSummary}");
            StatusChanged?.Invoke("REPlayer renderer controller transport attached");
            return (true, "render host controller started");
        }
        catch (Exception ex)
        {
            controller.Dispose();
            Log($"Render host controller start failed: {ex}");
            return (false, ex.Message);
        }
    }

    private bool RuntimeComponentsArePlaceholders()
    {
        var qemuBootAssets = new[]
        {
            Path.Combine(_baseDir, "runtime", "qemu-system-x86_64.exe"),
            Path.Combine(_baseDir, "runtime", "android-base.qcow2"),
            Full(Path.Combine("boot", "kernel")),
            Full(Path.Combine("boot", "revm-ramdisk.img"))
        };

        if (qemuBootAssets.All(File.Exists))
            return false;

        var files = new[] { _manifest!.SystemImage, _manifest.DataImageTemplate, _manifest.GuestAgentApk };
        foreach (var rel in files)
        {
            var info = new FileInfo(Full(rel));
            if (!info.Exists || info.Length < 1024 * 1024) return true;
        }
        return false;
    }

    private string Full(string rel) => Path.GetFullPath(Path.Combine(_runtimeRoot, rel));

    private static int GetAvailableTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -999; } }

    private async Task RunAdbControlAsync(string args, TimeSpan timeout)
    {
        var adb = GetAdbPath();
        if (!File.Exists(adb)) return;
        using var cts = new CancellationTokenSource(timeout);
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(adb, args)
            {
                WorkingDirectory = Path.GetDirectoryName(adb)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        await proc.WaitForExitAsync(cts.Token);
    }

    private void ForwardRuntimeEvent(string json)
    {
        try
        {
            var ev = JsonSerializer.Deserialize<RuntimeEvent>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (ev == null) return;
            LogRendererDebug("runtime", $"{ev.Type} progress={ev.Progress} message={ev.Message}");
            StatusChanged?.Invoke($"runtime-event:|{ev.Type}|{ev.Progress}|{ev.Message}");
            var readinessObserved = RecordRendererReadiness(ev);
            if (ev.Type.Equals("producer.frame_written", StringComparison.OrdinalIgnoreCase) ||
                ev.Type.Equals("fastpipe.frame_written", StringComparison.OrdinalIgnoreCase))
            {
                PresentLatestProducerFrames(ev.Message);
                return;
            }
            if (readinessObserved)
                StatusChanged?.Invoke($"renderer-debug:|{DateTime.Now:HH:mm:ss.fff}|{ev.Type} {ev.Message}");
            if (ShouldForwardAsBootProgress(ev))
                StatusChanged?.Invoke($"progress:|{ev.Progress}|{ev.Message}");

        }
        catch
        {
            // VMM stdout is usually JSON events, but keep raw text harmless.
        }
    }

    private static bool ShouldForwardAsBootProgress(RuntimeEvent ev)
    {
        if (ev.Type.Equals("runtime.heartbeat", StringComparison.OrdinalIgnoreCase) ||
            ev.Type.Equals("producer.frame_written", StringComparison.OrdinalIgnoreCase) ||
            ev.Type.Equals("android.ui.pending", StringComparison.OrdinalIgnoreCase) ||
            ev.Type.Equals("scanout.pending", StringComparison.OrdinalIgnoreCase) ||
            ev.Type.Equals("scanout.frame_observed", StringComparison.OrdinalIgnoreCase) ||
            ev.Type.Equals("adb.wait", StringComparison.OrdinalIgnoreCase) ||
            ev.Type.Equals("adb.timeout", StringComparison.OrdinalIgnoreCase) ||
            ev.Type.Equals("android.partial", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private string GetRendererApiSetting()
    {
        try
        {
            var path = Path.Combine(_baseDir, "runtime", "backend-settings.json");
            if (!File.Exists(path)) return "vulkan";
            var settings = JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return string.Equals(settings?.RendererApi, "opengl", StringComparison.OrdinalIgnoreCase) ? "opengl" : "vulkan";
        }
        catch
        {
            return "vulkan";
        }
    }

    private bool RecordRendererReadiness(RuntimeEvent ev)
    {
        var observed = false;
        foreach (var (vmId, tracker) in _rendererReadiness.ToArray())
        {
            if (!tracker.TryRecord(ev.Type, ev.Message, out var summary))
                continue;

            observed = true;
            LogRendererDebug(vmId, $"readiness event={ev.Type} progress={ev.Progress} {summary}; message={ev.Message}");
            if (tracker.IsReadyForPresent)
                StatusChanged?.Invoke($"renderer-debug:|{DateTime.Now:HH:mm:ss.fff}|renderer.ready_chain {summary}");
        }

        return observed;
    }

    private void PresentLatestProducerFrames(string reason)
    {
        foreach (var (vmId, session) in _renderSessions.ToArray())
        {
            try
            {
                var status = session.Controller.PresentLatestFrame();
                if (status is RenderHostControllerStatus.InvalidState or RenderHostControllerStatus.InvalidSize)
                {
                    try
                    {
                        if (status == RenderHostControllerStatus.InvalidSize &&
                            TryGetTransportDimensions(session.Controller.TransportEndpoint, out var transportWidth, out var transportHeight))
                        {
                            session.Controller.Resize(transportWidth, transportHeight);
                        }

                        session.Controller.AttachTransportAsync(session.Controller.TransportEndpoint, CancellationToken.None).GetAwaiter().GetResult();
                        status = session.Controller.PresentLatestFrame();
                    }
                    catch (Exception attachEx)
                    {
                        LogRendererDebug(vmId, $"present_latest_frame reattach failed: {attachEx.Message}");
                    }
                }
                LogRendererDebug(vmId, $"present_latest_frame status={status} reason={reason}");
                StatusChanged?.Invoke($"renderer-debug:|{DateTime.Now:HH:mm:ss.fff}|present_latest_frame {status}: {reason}");
                if (status == RenderHostControllerStatus.Ok)
                    RecordLocalPresentReadiness(vmId);
            }
            catch (Exception ex)
            {
                LogRendererDebug(vmId, $"present_latest_frame exception: {ex}");
                StatusChanged?.Invoke($"renderer-debug:|{DateTime.Now:HH:mm:ss.fff}|present_latest_frame failed: {ex.Message}");
            }
        }
    }

    private static bool TryGetTransportDimensions(string endpointJson, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            var root = doc.RootElement;
            var frameRoot = root;
            if (root.TryGetProperty("kind", out var kindProp) &&
                string.Equals(kindProp.GetString(), "revm-gpu-producer-v1", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("frameEndpoint", out frameRoot) || frameRoot.ValueKind != JsonValueKind.Object)
                    return false;
            }

            width = frameRoot.TryGetProperty("width", out var widthProp) && widthProp.TryGetInt32(out var w) ? w : 0;
            height = frameRoot.TryGetProperty("height", out var heightProp) && heightProp.TryGetInt32(out var h) ? h : 0;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private void RecordLocalPresentReadiness(string vmId)
    {
        if (!_rendererReadiness.TryGetValue(vmId, out var tracker)) return;

        if (!tracker.RendererReady && tracker.TryRecord("renderer.ready", "Render host controller presented latest produced frame", out var rendererSummary))
        {
            LogRendererDebug(vmId, $"local readiness event=renderer.ready {rendererSummary}");
            StatusChanged?.Invoke("runtime-event:|renderer.ready|93|Render host controller presented latest produced frame");
        }

        if (!tracker.FirstFrameReady && tracker.TryRecord("first_frame.ready", "First producer frame presented through native renderer controller", out var firstFrameSummary))
        {
            LogRendererDebug(vmId, $"local readiness event=first_frame.ready {firstFrameSummary}");
            StatusChanged?.Invoke("runtime-event:|first_frame.ready|96|First producer frame presented through native renderer controller");
            StatusChanged?.Invoke($"renderer-debug:|{DateTime.Now:HH:mm:ss.fff}|renderer.ready_chain {firstFrameSummary}");
        }
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }

    private void LogRendererDebug(string vmId, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_rendererDebugLogPath)!);
            File.AppendAllText(_rendererDebugLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{vmId}] {message}\r\n");
        }
        catch { }
    }

    private bool TryLoadManifest()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return false;
            _manifest = JsonSerializer.Deserialize<RevmRuntimeManifest>(File.ReadAllText(_manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return _manifest != null;
        }
        catch
        {
            _manifest = null;
            return false;
        }
    }

    private bool ValidateManifest(out string error)
    {
        error = "";
        if (_manifest == null)
        {
            error = "Native runtime manifest could not be loaded.";
            return false;
        }

        var required = new[] { _manifest.VmmPath, _manifest.RendererPath, _manifest.SystemImage, _manifest.DataImageTemplate, _manifest.GuestAgentApk };
        foreach (var rel in required)
        {
            if (string.IsNullOrWhiteSpace(rel) || !File.Exists(Full(rel)))
            {
                error = $"Native runtime component missing: {rel}";
                return false;
            }
        }

        if (!string.Equals(_manifest.AndroidArch, "x86_64", StringComparison.OrdinalIgnoreCase))
        {
            error = "Native runtime must be Android x86_64 for REPlayer v1 performance target.";
            return false;
        }

        if (!ValidateDisplayContract(_manifest.DisplayMode, out error))
            return false;

        return true;
    }

    private static bool ValidateDisplayContract(string? displayMode, out string error)
    {
        error = "";
        var mode = (displayMode ?? "").Trim();
        if (mode.Length == 0)
        {
            error = "Native runtime manifest must declare DisplayMode.";
            return false;
        }

        var bannedTokens = new[] { "scrcpy", "adb", "sdl", "video", "h264", "h.264", "encoder", "encoded", "webrtc", "decoder" };
        foreach (var token in bannedTokens)
        {
            if (mode.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Native runtime display mode '{mode}' is rejected: display must not depend on {token}.";
                return false;
            }
        }

        var allowedModes = new[]
        {
            "native-hwnd-gfxstream",
            "native-hwnd-shm-ring",
            "native-hwnd-qmp-scanout",
            "native-hwnd-virtio-gpu-gfxstream",
            "native-hwnd-gfxstream-dev",
            "native-hwnd-gfxstream-host"
        };

        if (!allowedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Native runtime display mode '{mode}' is not one of: {string.Join(", ", allowedModes)}.";
            return false;
        }

        return true;
    }
}

public sealed record RuntimeEvent(string Type, int Progress, string Message);

public sealed class RevmRuntimeManifest
{
    public string RuntimeVersion { get; set; } = "";
    public string AndroidArch { get; set; } = "x86_64";
    public string EngineKind { get; set; } = "revm-gfxstream";
    public string VmmPath { get; set; } = "bin/revm-vmm.exe";
    public string FastpipeHostPath { get; set; } = "bin/revm-fastpipe-host.exe";
    public string RendererPath { get; set; } = "bin/revm-render-host.dll";
    public string SystemImage { get; set; } = "images/android-x86_64-system.img";
    public string DataImageTemplate { get; set; } = "images/android-x86_64-data-template.img";
    public string GuestAgentApk { get; set; } = "guest/revm-agent.apk";
    public string AdbMode { get; set; } = "tcp";
    public string DisplayMode { get; set; } = "native-hwnd-gfxstream";
}
