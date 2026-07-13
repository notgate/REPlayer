using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM;

/// <summary>
/// Public REPlayer runtime backend using Google's official Android Emulator engine
/// and x86_64 system images. This is the maintainable public equivalent of a
/// commercial emulator image/runtime: no ISO installer, direct launcher boot,
/// built-in ADB, and WHPX-capable engine on Windows.
/// </summary>
public sealed class GoogleEmulatorRuntimeService : IAndroidRuntimeBackend
{
    private const string EmulatorUrl = "https://dl.google.com/android/repository/emulator-windows_x64-15769812.zip";
    private const string SystemImageUrl = "https://dl.google.com/android/repository/sys-img/google_apis/x86_64-34_r14.zip";
    private const string PlayStoreSystemImageUrl = "https://dl.google.com/android/repository/sys-img/google_apis_playstore/x86_64-30_r10-windows.zip";
    private const string PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip";
    private const string BuildToolsUrl = "https://dl.google.com/android/repository/build-tools_r33.0.2-windows.zip";
    private const string BackendName = "google-emulator";
    private const int LeanRamMb = 2048;
    private const int EmulatorToolbarMaxWidthPx = 140;
    private const int EmulatorToolbarMinHeightPx = 180;
    private const int EmbeddedEmulatorOverscanPx = 0;
    private const int LeanCpuCores = 2;
    private const int ResizablePhoneWidth = 1080;
    private const int ResizablePhoneHeight = 2400;
    private const int ResizableLandscapeWidth = 1920;
    private const int ResizableLandscapeHeight = 1200;
    private readonly string _baseDir;
    private readonly string _runtimeDir;
    private readonly string _googleDir;
    private readonly string _downloadsDir;
    private readonly string _sdkRoot;
    private readonly string _officialEmulatorDir;
    private readonly string _personaEmulatorDir;
    private readonly string _systemImageDir;
    private readonly string _playStoreSystemImageDir;
    private readonly string _patchedPixelSystemImageDir;
    private readonly string _avdHome;
    private readonly string _avdDir;
    private readonly string _resizableAvdDir;
    private readonly string _instancesDir;
    private readonly string _logsDir;
    private readonly string _logPath;
    private readonly string[] _baselineDisabledPackages;
    private readonly Dictionary<string, Process> _processes = new();
    private readonly HashSet<string> _startingInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _instanceStateGate = new();
    private readonly Dictionary<string, OfficialEmulatorJob> _containmentJobs = new();
    private readonly Dictionary<string, OfficialEmulatorRunContext> _runContexts = new();
    private readonly object _runGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _priorityBoostTokens = new();
    private readonly object _priorityBoostGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _networkPolicyMonitorTokens = new();
    private readonly object _networkPolicyMonitorGate = new();
    private readonly ConcurrentDictionary<string, IntPtr> _embeddedWindows = new();
    private readonly ConcurrentDictionary<string, bool> _embeddedWindowVisibility = new();
    private readonly ConcurrentDictionary<string, bool> _nativeWindowEmbedded = new();
    private readonly ConcurrentDictionary<string, (IntPtr HostHandle, int Width, int Height)> _nativeWindowHosts = new();
    private readonly ConcurrentDictionary<string, (int X, int Y, int Width, int Height)> _lastHostSizes = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _displayProfileTransitions = new();

    public event Action<string, IntPtr>? WindowCreated;
    public event Action<string>? StatusChanged;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_HWNDPARENT = -8;
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
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint GA_ROOT = 2;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public GoogleEmulatorRuntimeService()
    {
        _baseDir = RevmPaths.BaseDir;
        _runtimeDir = Path.Combine(_baseDir, "runtime");
        _googleDir = Path.Combine(_runtimeDir, "google-emulator");
        _downloadsDir = Path.Combine(_googleDir, "downloads");
        _sdkRoot = Path.Combine(_googleDir, "sdk");
        _officialEmulatorDir = Path.Combine(_sdkRoot, "emulator");
        _personaEmulatorDir = Path.Combine(_googleDir, "persona-emulator-37.1.7");
        _systemImageDir = Path.Combine(_sdkRoot, "system-images", "android-34", "google_apis", "x86_64");
        _playStoreSystemImageDir = Path.Combine(_sdkRoot, "system-images", "android-30", "google_apis_playstore", "x86_64");
        _patchedPixelSystemImageDir = Path.Combine(_googleDir, "patched-images", "pixel-utility", "android-30", "google_apis", "x86_64");
        _avdHome = Path.Combine(_googleDir, "avd-home");
        _avdDir = Path.Combine(_avdHome, "ReVM.avd");
        _resizableAvdDir = Path.Combine(_avdHome, "ReVMResizable.avd");
        _instancesDir = Path.Combine(_googleDir, "instances");
        _logsDir = Path.Combine(_baseDir, "logs");
        _logPath = Path.Combine(_logsDir, "google-emulator-runtime.log");
        _baselineDisabledPackages = LoadBaselineDisabledPackages(_baseDir);
        try
        {
            var recovered = OfficialEmulatorRunRecovery.RecoverIncompleteRuns(Path.Combine(_runtimeDir, "cases"));
            if (recovered > 0) Log($"Recovered {recovered} incomplete official-emulator case run(s).");
        }
        catch (Exception ex)
        {
            Log("Incomplete case recovery failed: " + ex.Message);
        }
    }

    private static string[] LoadBaselineDisabledPackages(string baseDir)
    {
        var candidates = new[]
        {
            Path.Combine(baseDir, "android-tools", "replayer-customizer", "baseline-disabled-packages.txt"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Android", "baseline-disabled-packages.txt")
        };
        var policyPath = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Canonical Android package policy is missing.");
        var packages = File.ReadLines(policyPath)
            .Select(line =>
            {
                var comment = line.IndexOf('#');
                return (comment >= 0 ? line[..comment] : line).Trim();
            })
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (packages.Length == 0)
            throw new InvalidOperationException("Canonical Android package policy is empty.");
        return packages;
    }

    private string ActiveBootProfile => (LoadBackendSettings().BootProfile ?? "api34-persona").Trim().ToLowerInvariant();
    private bool UsesPlayCompatibilityProfile => string.Equals(ActiveBootProfile, "play-compat", StringComparison.OrdinalIgnoreCase);
    private bool UsesPatchedPixelUtilityProfile => string.Equals(ActiveBootProfile, "patched-pixel-utility", StringComparison.OrdinalIgnoreCase);
    private string ActiveSystemImageDir => UsesPlayCompatibilityProfile
        ? _playStoreSystemImageDir
        : UsesPatchedPixelUtilityProfile ? _patchedPixelSystemImageDir : _systemImageDir;
    private string ActiveSystemImageUrl => UsesPlayCompatibilityProfile ? PlayStoreSystemImageUrl : SystemImageUrl;
    private string ActiveImageZipName => UsesPlayCompatibilityProfile
        ? "x86_64-30_r10-playstore-windows.zip"
        : UsesPatchedPixelUtilityProfile ? "x86_64-30_r16-patched-pixel-utility.zip" : "x86_64-34_r14.zip";
    private string ActiveTagId => UsesPlayCompatibilityProfile ? "google_apis_playstore" : "google_apis";
    private string ActiveTagDisplay => UsesPlayCompatibilityProfile ? "Google Play" : UsesPatchedPixelUtilityProfile ? "Google APIs (Patched Utility; SDK hardware identity)" : "Google APIs";

    public bool CheckEngine()
    {
        HardenExistingAvdColdBootPolicy();
        try
        {
            if (File.Exists(OfficialEmulatorExe) && !GfxstreamPersonaRuntime.IsReady(_personaEmulatorDir))
                GfxstreamPersonaRuntime.Ensure(_officialEmulatorDir, _personaEmulatorDir, Log);
        }
        catch (Exception ex)
        {
            Log("gfxstream persona runtime is not ready: " + ex.Message);
        }
        var emulatorOk = File.Exists(EmulatorExe);
        var adbOk = File.Exists(AdbExe);
        var imageOk = SystemImageReady;
        var baselineOk = PublishedBaselineReady;

        if (emulatorOk && adbOk && imageOk && !AvdReady)
        {
            try
            {
                Log("AVD profile missing/incomplete while runtime files exist; rewriting REPlayer AVD profile.");
                WriteAvdProfile();
                WriteRuntimeInfo();
            }
            catch (Exception ex)
            {
                Log("AVD profile self-repair failed: " + ex.Message);
            }
        }

        var avdOk = AvdReady;
        var ok = emulatorOk && adbOk && imageOk && avdOk && baselineOk;
        if (!ok)
            Log($"CheckEngine false: emulator={emulatorOk}, adb={adbOk}, image={imageOk}, avd={avdOk}, publishedBaseline={baselineOk}, base={_baseDir}");
        return ok;
    }

    public bool IsBaseImageReady() => CheckEngine();

    public async Task<(bool success, string message)> EnsureBaseImageAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureBootstrapLayout();
            StageManagedRuntimeFiles();

            if (!PublishedBaselineReady)
            {
                return (false,
                    $"The prepublished API 34 baseline is missing for {ActiveBootProfile}. " +
                    "Run setup.bat from the complete REPlayer release package; stock SDK downloads cannot replace the published security baseline.");
            }

            if (CheckEngine())
            {
                Status(100, "Google Android Emulator runtime already ready");
                return (true, "Google Android Emulator runtime is ready.");
            }

            Status(5, "Preparing Google Android Emulator runtime");
            await DownloadAndExtractAsync("Android Emulator", EmulatorUrl,
                Path.Combine(_downloadsDir, "emulator-windows_x64-15769812.zip"),
                _sdkRoot,
                () => File.Exists(OfficialEmulatorExe),
                10,
                34,
                ct);

            Status(34, "Preparing privacy-safe gfxstream runtime");
            GfxstreamPersonaRuntime.Ensure(_officialEmulatorDir, _personaEmulatorDir, Log);

            Status(36, "Preparing Android platform-tools / ADB");
            await DownloadAndExtractAsync("Android platform-tools", PlatformToolsUrl,
                Path.Combine(_downloadsDir, "platform-tools_r37.0.1-win.zip"),
                _sdkRoot,
                () => File.Exists(AdbExe),
                36,
                44,
                ct);

            Status(44, "Preparing Android signing/build tools");
            await EnsureAndroidBuildToolsAsync(ct);

            Status(45, "Preparing Android 14 / API 34 Google APIs x86_64 image");
            if (!SystemImageReady)
            {
                await DownloadAndExtractAsync("Android system image", ActiveSystemImageUrl,
                    Path.Combine(_downloadsDir, ActiveImageZipName),
                    ActiveSystemImageDir,
                    () => SystemImageReady,
                    45,
                    82,
                    ct);
            }
            else
            {
                Status(82, "Android system image already installed");
            }

            Status(84, "Writing REPlayer AVD profile");
            WriteAvdProfile();
            WriteRuntimeInfo();
            Status(100, "Google Android Emulator runtime ready");
            return (true, "Google Android Emulator runtime is ready.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Google Android Emulator setup cancelled.");
        }
        catch (Exception ex)
        {
            Log("Setup failed: " + ex);
            return (false, "Google Android Emulator setup failed: " + ex.Message);
        }
    }

    private string SettingsPath => Path.Combine(_runtimeDir, "backend-settings.json");
    private string InstanceConfigPath(string id) => Path.Combine(_instancesDir, id, "instance.json");
    private string CasesRoot => Path.Combine(_runtimeDir, "cases");


    private BackendSettings LoadBackendSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
            var settings = JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(SettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BackendSettings();
            return BackendSettingsValidation.ValidateAndNormalize(settings);
        }
        catch
        {
            return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
        }
    }

    private string GetInstanceRuntimeStatus(string vmId)
    {
        lock (_instanceStateGate)
        {
            if (_startingInstances.Contains(vmId)) return "preparing";
        }
        if (_processes.ContainsKey(vmId) || IsContainmentTreeActive(vmId)) return "running";
        return "stopped";
    }

    public List<VmInstance> GetInstances()
    {
        if (!CheckEngine()) return new List<VmInstance>();
        Directory.CreateDirectory(_instancesDir);
        EnsureDefaultInstance();
        return Directory.EnumerateFiles(_instancesDir, "instance.json", SearchOption.AllDirectories)
            .Select(ReadInstanceConfig)
            .Where(i => i != null)
            .Select(i => new VmInstance
            {
                Id = i!.Id,
                Name = i.Name,
                CpuCount = Math.Clamp(i.CpuCount, 1, Environment.ProcessorCount),
                RamMB = Math.Clamp(i.RamMB, 1024, GetSafeMaxRamMb()),
                StorageGB = Math.Clamp(i.StorageGB, 8, 4096),
                BootProfile = NormalizeBootProfile(i.BootProfile),
                CreatedAt = i.CreatedAt,
                Status = GetInstanceRuntimeStatus(i.Id)
            })
            .OrderBy(i => i.CreatedAt == default ? DateTime.MaxValue : i.CreatedAt)
            .ToList();
    }

    public void CreateInstance(string name, int cpuCount, int ramMB, int storageGB) =>
        CreateInstance(name, cpuCount, ramMB, storageGB, LoadBackendSettings().BootProfile);

    public void CreateInstance(string name, int cpuCount, int ramMB, int storageGB, string bootProfile)
    {
        if (!CheckEngine()) throw new InvalidOperationException("Google Android Emulator runtime is not installed yet.");
        Directory.CreateDirectory(_instancesDir);
        var id = "revm-" + SanitizeId(name) + "-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cfg = new GoogleInstanceConfig
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "Android" : name.Trim(),
            CpuCount = Math.Clamp(cpuCount, 1, Environment.ProcessorCount),
            RamMB = Math.Clamp(ramMB, 1024, GetSafeMaxRamMb()),
            StorageGB = Math.Clamp(storageGB, 8, 4096),
            BootProfile = NormalizeBootProfile(bootProfile),
            CreatedAt = DateTime.UtcNow,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(InstanceConfigPath(id))!);
        File.WriteAllText(InstanceConfigPath(id), JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        WriteAvdProfile(InstanceAvdName(cfg.Id), InstanceAvdDir(cfg.Id), cfg.CpuCount, cfg.RamMB, cfg.StorageGB, cfg.BootProfile);
        WriteRuntimeInfo();
    }

    public void UpdateInstance(string vmId, string name, int cpuCount, int ramMB, int storageGB, string bootProfile)
    {
        if (string.IsNullOrWhiteSpace(vmId)) throw new ArgumentException("Missing instance id.", nameof(vmId));
        var cfg = LoadInstanceConfig(vmId) ?? throw new InvalidOperationException("Instance not found: " + vmId);
        cfg.Name = string.IsNullOrWhiteSpace(name) ? cfg.Name : name.Trim();
        cfg.CpuCount = Math.Clamp(cpuCount, 1, Environment.ProcessorCount);
        cfg.RamMB = Math.Clamp(ramMB, 1024, GetSafeMaxRamMb());
        cfg.StorageGB = Math.Clamp(storageGB, 8, 4096);
        cfg.BootProfile = NormalizeBootProfile(bootProfile);
        SaveInstanceConfig(cfg);
        WriteAvdProfile(InstanceAvdName(cfg.Id), InstanceAvdDir(cfg.Id), cfg.CpuCount, cfg.RamMB, cfg.StorageGB, cfg.BootProfile);
        WriteRuntimeInfo();
    }

    public (bool success, string debug) StartInstanceWithDebug(string vmId, IntPtr hostHandle, int hostX, int hostY, int hostW, int hostH)
    {
        if (!CheckEngine()) return (false, "Google Android Emulator runtime missing. Run setup first.");

        var settings = LoadBackendSettings();
        OfficialEmulatorNetworkPlan networkPlan;
        try { networkPlan = OfficialEmulatorNetworkIsolation.BuildPlan(settings, _baseDir); }
        catch (Exception ex) { return (false, "Network policy is invalid: " + ex.Message); }
        var networkPolicy = OfficialEmulatorNetworkIsolation.VerifyPlan(networkPlan);
        if (settings.SecureIsolationEnabled && !networkPolicy.Success)
            return (false, "Secure isolation blocked launch: " + networkPolicy.Message + " Open Settings > Network and apply the host policy.");

        Log($"StartInstanceWithDebug begin vmId={vmId} host=0x{hostHandle.ToInt64():X} size={hostW}x{hostH} network={networkPlan.Mode} policyVerified={networkPolicy.Success}");
        Status(45, "Preparing disposable Android run storage");
        lock (_instanceStateGate) _startingInstances.Add(vmId);
        _ = Task.Run(() => StartInstanceCore(vmId, hostHandle, Math.Max(1, hostW), Math.Max(1, hostH)));
        return (true, "Google Android Emulator launch scheduled; waiting for ADB and renderer.");
    }

    private void StartInstanceCore(string vmId, IntPtr hostHandle, int hostW, int hostH)
    {
        try
        {
            StopInstance(vmId);
            lock (_instanceStateGate) _startingInstances.Add(vmId);
            Directory.CreateDirectory(_logsDir);

            var settings = LoadBackendSettings();
            var networkPlan = OfficialEmulatorNetworkIsolation.BuildPlan(settings, _baseDir);
            var networkPolicy = OfficialEmulatorNetworkIsolation.VerifyPlan(networkPlan);
            if (settings.SecureIsolationEnabled && !networkPolicy.Success)
            {
                StatusChanged?.Invoke("Secure isolation blocked launch: " + networkPolicy.Message);
                return;
            }
            var instance = LoadInstanceConfig(vmId) ?? DefaultInstanceConfig(settings);
            // The selected global performance/boot profile is authoritative.
            // Persist it into the active instance so restart does not silently
            // revert to stale per-instance CPU/RAM/profile values.
            settings.BootProfile = NormalizeBootProfile(settings.BootProfile);
            settings.CpuCores = Math.Clamp(settings.CpuCores, 1, Environment.ProcessorCount);
            settings.RamMb = Math.Clamp(settings.RamMb, 1024, GetSafeMaxRamMb());
            settings.StorageGb = Math.Clamp(instance.StorageGB, 8, 4096);
            if (settings.RamMb != instance.RamMB || settings.CpuCores != instance.CpuCount ||
                settings.StorageGb != instance.StorageGB ||
                !string.Equals(settings.BootProfile, NormalizeBootProfile(instance.BootProfile), StringComparison.OrdinalIgnoreCase))
            {
                instance.CpuCount = settings.CpuCores;
                instance.RamMB = settings.RamMb;
                instance.StorageGB = settings.StorageGb;
                instance.BootProfile = settings.BootProfile;
                SaveInstanceConfig(instance);
                StatusChanged?.Invoke($"Applied {settings.EmulationPerformanceMode} profile to {instance.Name}: {settings.CpuCores} CPU / {settings.RamMb} MB RAM / {settings.BootProfile}");
            }
            var avdName = InstanceAvdName(instance.Id);
            var avdDir = InstanceAvdDir(instance.Id);
            WriteAvdProfile(avdName, avdDir, settings.CpuCores, settings.RamMb, settings.StorageGb, settings.BootProfile);
            Status(10, "Hashing immutable emulator baseline");
            var systemImageDir = SystemImageDirectoryForProfile(settings.BootProfile);
            var run = OfficialEmulatorRunContext.Create(CasesRoot, vmId, settings.BootProfile, avdDir,
                systemImageDir, settings.AdbPort, settings.OfficialEmulatorDisposableMode);
            lock (_runGate) _runContexts[vmId] = run;
            run.Manifest.NetworkPolicy = networkPlan.ToManifest(networkPolicy.Success);
            if (settings.PersonaEnabled)
            {
                run.PersonaPlan = OfficialEmulatorPersona.Build(settings, vmId, run.CaseId);
                run.Manifest.Persona = run.PersonaPlan.ManifestEvidence;
            }
            run.BaselineReadLease = OfficialEmulatorBaseline.AcquireReadLease(avdDir, systemImageDir);
            run.Manifest.BaselineHashesBefore = OfficialEmulatorBaseline.ComputeHashes(
                avdDir,
                systemImageDir,
                Path.Combine(_runtimeDir, "baseline-integrity"));
            VerifyPublishedBaselineHashes(avdDir, run.Manifest.BaselineHashesBefore, settings.BootProfile);
            run.Manifest.StartState = "baseline-verified";
            run.WriteManifest();
            Status(12, "Cloning immutable baseline into disposable run storage");
            OfficialEmulatorBaseline.Clone(avdDir, run.RunAvdDirectory, QemuImgExe,
                checked((long)Math.Clamp(settings.StorageGb, 8, 4096) << 30), relocateExternalBacking: true);
            WriteAvdProfile(run.RunAvdName, run.RunAvdDirectory, settings.CpuCores, settings.RamMb,
                settings.StorageGb, settings.BootProfile, run.RunAvdHome);

            var env = new Dictionary<string, string>
            {
                ["ANDROID_SDK_ROOT"] = _sdkRoot,
                ["ANDROID_HOME"] = _sdkRoot,
                ["ANDROID_AVD_HOME"] = run.RunAvdHome
            };

            LogAccelerationStatus();
            var frameW = Math.Clamp(settings.DisplayWidth, 320, 3840);
            var frameH = Math.Clamp(settings.DisplayHeight, 240, 2160);
            if (string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase) && frameW > frameH)
                (frameW, frameH) = (frameH, frameW);
            else if (!string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase) && frameH > frameW)
                (frameW, frameH) = (frameH, frameW);
            var gpuMode = NormalizeGpuMode(settings.GpuMode);
            var systemUiRendererArg = NormalizeSystemUiRenderer(settings.SystemUiRenderer);
            var launchArgs = new List<string>();
            AddArguments(launchArgs, "-avd", run.RunAvdName);
            launchArgs.Add("-no-boot-anim");
            AddArguments(launchArgs, "-gpu", gpuMode);
            AddRendererFeatureArguments(launchArgs, settings.RendererApi);
            AddArguments(launchArgs, "-accel", string.Equals(settings.Acceleration, "software", StringComparison.OrdinalIgnoreCase) ? "off" : "on");
            AddArguments(launchArgs, "-ports", $"{run.ConsolePort},{run.AdbPort}");
            AddArguments(launchArgs, "-tcpdump", run.PacketCapturePath);
            foreach (var argument in networkPlan.EmulatorArguments) launchArgs.Add(argument);
            if (run.PersonaPlan is not null)
                foreach (var argument in run.PersonaPlan.EmulatorArguments) launchArgs.Add(argument);
            if (settings.OfficialEmulatorDisposableMode)
            {
                // Each case already runs from a private clone of the immutable AVD.
                // The API 34 persona lives in Android overlayfs and is mounted only
                // when the emulator starts with -writable-system; -read-only would
                // suppress the published identity layer.
                launchArgs.Add("-writable-system");
                launchArgs.Add("-no-snapshot-load");
                launchArgs.Add("-no-snapshot-save");
                launchArgs.Add("-no-snapstorage");
            }
            else if (!settings.ReadOnlyBaseImage)
            {
                launchArgs.Add("-writable-system");
            }
            AddArguments(launchArgs, "-memory", Math.Clamp(settings.RamMb, 1024, GetSafeMaxRamMb()).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddArguments(launchArgs, "-cores", Math.Clamp(settings.CpuCores, 1, Environment.ProcessorCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (string.Equals(NormalizeBootProfile(settings.BootProfile), "play-compat", StringComparison.OrdinalIgnoreCase))
            {
                AddArguments(launchArgs, "-skin", $"{frameW}x{frameH}");
                AddArguments(launchArgs, "-lcd-scaling-factor", "1.0");
            }
            if (settings.LowLatencyInput) { launchArgs.Add("-no-mouse-reposition"); launchArgs.Add("-use-keycode-forwarding"); }
            if (Math.Clamp(settings.RamMb, 1024, GetSafeMaxRamMb()) <= 1536) launchArgs.Add("-lowram");
            launchArgs.Add("-no-sim");
            launchArgs.Add("-no-passive-gps");
            if (settings.NatNetworking && !networkPlan.IsOffline) launchArgs.Add("-netfast");
            AddArguments(launchArgs, "-camera-back", "none");
            AddArguments(launchArgs, "-camera-front", "none");
            if (!settings.SpeakerOutput) launchArgs.Add("-no-audio");
            if (!string.IsNullOrWhiteSpace(systemUiRendererArg)) AddArguments(launchArgs, "-systemui-renderer", systemUiRendererArg);
            launchArgs.Add("-no-metrics");
            AddArguments(launchArgs, "-crash-report-mode", "never");
            run.Manifest.LaunchArguments = launchArgs.ToList();
            run.Manifest.StartState = "launching";
            run.WriteManifest();
            Log("Launching emulator.exe " + string.Join(' ', launchArgs.Select(QuoteLogArgument)));

            var psi = new ProcessStartInfo(EmulatorExe)
            {
                WorkingDirectory = ActiveEmulatorDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in launchArgs) psi.ArgumentList.Add(argument);
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            ApplyWindowsGpuPreference(settings.PreferDiscreteGpu);
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
            var stdoutLog = run.StdoutPath;
            var stderrLog = run.StderrPath;
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Append(stdoutLog, e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Append(stderrLog, e.Data); };
            proc.Exited += (_, _) => HandleEmulatorExited(vmId, proc);

            var currentLaunchPlan = OfficialEmulatorNetworkIsolation.BuildPlan(LoadBackendSettings(), _baseDir);
            if (!string.Equals(currentLaunchPlan.Fingerprint, networkPlan.Fingerprint, StringComparison.Ordinal))
            {
                StatusChanged?.Invoke("Secure isolation blocked process start because network settings changed during preparation.");
                FinalizeRun(vmId, "aborted", "Network settings changed before emulator process start.");
                proc.Dispose();
                return;
            }
            var launchPolicy = OfficialEmulatorNetworkIsolation.VerifyPlan(networkPlan);
            run.Manifest.NetworkPolicy = networkPlan.ToManifest(launchPolicy.Success);
            run.WriteManifest();
            if (settings.SecureIsolationEnabled && !launchPolicy.Success)
            {
                StatusChanged?.Invoke("Secure isolation blocked process start: " + launchPolicy.Message);
                FinalizeRun(vmId, "aborted", "Network policy changed before emulator process start: " + launchPolicy.Message);
                proc.Dispose();
                return;
            }

            if (!proc.Start())
            {
                StatusChanged?.Invoke("Failed to start emulator.exe");
                FinalizeRun(vmId, "aborted", "emulator.exe returned false from Process.Start");
                return;
            }
            try
            {
                var containmentJob = OfficialEmulatorJob.Attach(proc);
                lock (_runGate) _containmentJobs[vmId] = containmentJob;
                proc.EnableRaisingEvents = true;
                _ = MonitorContainmentJobAsync(vmId, containmentJob, proc.Id);
            }
            catch (Exception ex)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                StatusChanged?.Invoke("Containment launch failed: " + ex.Message);
                FinalizeRun(vmId, "containment-failure", ex.Message);
                proc.Dispose();
                return;
            }
            run.Manifest.EmulatorProcessId = proc.Id;
            run.Manifest.StartState = "running";
            run.WriteManifest();
            _processes[vmId] = proc;
            if (settings.SecureIsolationEnabled) StartNetworkPolicyMonitor(vmId, networkPlan);
            TrySetEmulatorPerformancePriority(proc, settings.EmulationPerformanceMode);
            StartPriorityBoost(vmId, proc.Id, TimeSpan.FromSeconds(90), settings.EmulationPerformanceMode);
            try
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log("Could not attach emulator output readers: " + ex.Message);
            }
            _ = Task.Run(() => HideStartupPopupsForProcess(vmId, proc.Id, TimeSpan.FromMinutes(10)));
            if (proc.WaitForExit(1200))
            {
                var treeActive = false;
                try { treeActive = TryGetContainmentJob(vmId, out var job) && job.HasActiveProcesses; }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke("Containment process-tree check failed: " + ex.Message);
                    CompleteContainmentJob(vmId, "containment-failure", ex.Message);
                    return;
                }
                if (!treeActive)
                {
                    var tail = TailFile(stderrLog, 24);
                    Log($"Google emulator process tree exited immediately ({SafeExitCode(proc)}): {tail}");
                    StatusChanged?.Invoke($"Android launch failed: {SummarizeLaunchFailure(tail)}");
                    CompleteContainmentJob(vmId, "aborted", SummarizeLaunchFailure(tail));
                    return;
                }
                Log("emulator.exe launcher exited after starting a contained child process; continuing with the Job Object process tree.");
            }
            Status(15, "Google emulator process started");
            AttachNativeEmulatorWindow(vmId, hostHandle, hostW, hostH, proc, TimeSpan.FromSeconds(45));
            _ = Task.Run(() => WaitForAndroidReadyAsync(vmId, hostHandle, hostW, hostH, CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log("Failed to start Google emulator runtime: " + ex);
            StatusChanged?.Invoke("Failed to start Google emulator runtime: " + ex.Message);
            CompleteContainmentJob(vmId, "aborted", ex.Message);
        }
        finally
        {
            lock (_instanceStateGate) _startingInstances.Remove(vmId);
        }
    }

    public void StartInstance(string vmId, IntPtr hostHandle, int x, int y, int w, int h)
    {
        var (success, debug) = StartInstanceWithDebug(vmId, hostHandle, x, y, w, h);
        if (!success) throw new InvalidOperationException(debug);
    }

    public void ResizeEmbeddedWindow(string vmId, int x, int y, int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        var unchanged = _lastHostSizes.TryGetValue(vmId, out var previous) &&
                        previous.X == x && previous.Y == y &&
                        previous.Width == w && previous.Height == h;
        var parentWasStable = false;
        if (_embeddedWindows.TryGetValue(vmId, out var existingHwnd) && existingHwnd != IntPtr.Zero &&
            _nativeWindowEmbedded.TryGetValue(vmId, out var embedded) && embedded &&
            _nativeWindowHosts.TryGetValue(vmId, out var existingHost))
        {
            var owner = GetAncestor(existingHost.HostHandle, GA_ROOT);
            parentWasStable = owner != IntPtr.Zero && GetParent(existingHwnd) == owner;
        }
        _lastHostSizes[vmId] = (x, y, w, h);
        if (_embeddedWindows.TryGetValue(vmId, out var hwnd) && hwnd != IntPtr.Zero)
        {
            var show = !_embeddedWindowVisibility.TryGetValue(vmId, out var visible) || visible;
            if (!show)
            {
                ShowWindow(hwnd, SW_HIDE);
                return;
            }
            if (!EnsureNativeWindowEmbedded(vmId, hwnd)) return;
            var left = x;
            var top = y;
            if (_nativeWindowHosts.TryGetValue(vmId, out var host) && GetWindowRect(host.HostHandle, out var viewport))
            {
                left = viewport.Left;
                top = viewport.Top;
                w = Math.Max(1, viewport.Right - viewport.Left);
                h = Math.Max(1, viewport.Bottom - viewport.Top);
            }
            var desiredLeft = left - EmbeddedEmulatorOverscanPx;
            var desiredTop = top - EmbeddedEmulatorOverscanPx;
            var desiredWidth = w + (EmbeddedEmulatorOverscanPx * 2);
            var desiredHeight = h + (EmbeddedEmulatorOverscanPx * 2);
            var actualMatches = GetWindowRect(hwnd, out var actual) &&
                                Math.Abs(actual.Left - desiredLeft) <= 1 &&
                                Math.Abs(actual.Top - desiredTop) <= 1 &&
                                Math.Abs((actual.Right - actual.Left) - desiredWidth) <= 1 &&
                                Math.Abs((actual.Bottom - actual.Top) - desiredHeight) <= 1;
            // Qt can resize an owned top-level surface after SetWindowPos. Only
            // suppress an update when both the requested and actual rectangles
            // still match; otherwise the containment timer repairs the drift.
            if (unchanged && parentWasStable && actualMatches) return;
            SetWindowPos(hwnd, HWND_TOP,
                desiredLeft,
                desiredTop,
                desiredWidth,
                desiredHeight,
                SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            HideEmulatorToolbarChildren(hwnd);
        }
    }

    public void SetEmbeddedWindowVisible(string vmId, bool visible)
    {
        if (!visible)
        {
            _embeddedWindowVisibility[vmId] = false;
            if (_embeddedWindows.TryGetValue(vmId, out var hiddenHwnd) && hiddenHwnd != IntPtr.Zero)
                ShowWindow(hiddenHwnd, SW_HIDE);
            return;
        }

        if (!_embeddedWindows.TryGetValue(vmId, out var hwnd) || hwnd == IntPtr.Zero) return;
        if (!EnsureNativeWindowEmbedded(vmId, hwnd)) return;
        _embeddedWindowVisibility[vmId] = true;
        if (_nativeWindowHosts.TryGetValue(vmId, out var host) && GetWindowRect(host.HostHandle, out var viewport))
        {
            // Re-establish the screen-space viewport while Qt is still hidden;
            // showing first allows its orientation handler to flash/move the child.
            SetWindowPos(hwnd, HWND_TOP,
                viewport.Left - EmbeddedEmulatorOverscanPx,
                viewport.Top - EmbeddedEmulatorOverscanPx,
                Math.Max(1, viewport.Right - viewport.Left + (EmbeddedEmulatorOverscanPx * 2)),
                Math.Max(1, viewport.Bottom - viewport.Top + (EmbeddedEmulatorOverscanPx * 2)),
                SWP_FRAMECHANGED | SWP_NOACTIVATE);
        }
        ShowWindow(hwnd, SW_SHOW);
        HideEmulatorToolbarChildren(hwnd);
    }

    private bool EnsureNativeWindowEmbedded(string vmId, IntPtr hwnd)
    {
        if (!_nativeWindowHosts.TryGetValue(vmId, out var host) || host.HostHandle == IntPtr.Zero) return false;
        var targetOwner = GetAncestor(host.HostHandle, GA_ROOT);
        if (targetOwner == IntPtr.Zero) return false;
        var currentOwner = GetParent(hwnd);
        if (_nativeWindowEmbedded.TryGetValue(vmId, out var embedded) && embedded && currentOwner == targetOwner)
            return true;
        if (embedded && currentOwner != targetOwner)
        {
            Log($"Qt ownership changed; restoring REPlayer containment vmId={vmId} hwnd=0x{hwnd.ToInt64():X} owner=0x{currentOwner.ToInt64():X}");
            _nativeWindowEmbedded[vmId] = false;
        }
        ShowWindow(hwnd, SW_HIDE);
        EmbedWindow(vmId, hwnd, host.HostHandle, host.Width, host.Height);
        if (GetParent(hwnd) != targetOwner)
        {
            Log($"Native emulator HWND ownership failed vmId={vmId} hwnd=0x{hwnd.ToInt64():X} owner=0x{targetOwner.ToInt64():X}");
            ShowWindow(hwnd, SW_HIDE);
            return false;
        }
        _nativeWindowEmbedded[vmId] = true;
        return true;
    }

    public void StopInstance(string vmId)
    {
        lock (_instanceStateGate) _startingInstances.Remove(vmId);
        CancelNetworkPolicyMonitor(vmId);
        CancelPriorityBoost(vmId);
        _embeddedWindows.TryRemove(vmId, out _);
        _embeddedWindowVisibility.TryRemove(vmId, out _);
        _nativeWindowEmbedded.TryRemove(vmId, out _);
        _nativeWindowHosts.TryRemove(vmId, out _);
        _lastHostSizes.TryRemove(vmId, out _);
        if (TryGetRunContext(vmId, out var stoppingRun)) stoppingRun.StopRequested = true;

        if (_processes.Remove(vmId, out var proc))
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
                proc.WaitForExit(1500);
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
            proc.Dispose();
        }
        DisposeContainmentJob(vmId);
        FinalizeRun(vmId, "completed", null);
    }

    public async Task PauseInstanceAsync(string vmId)
    {
        if (!File.Exists(AdbExe) || !_processes.ContainsKey(vmId)) return;
        try
        {
            await RunAdbAsync(vmId, 4_000, "emu", "avd", "pause");
            Log("Paused emulator for modal dialog: " + vmId);
        }
        catch (Exception ex)
        {
            Log("Emulator pause skipped: " + ex.Message);
        }
    }

    public async Task ResumeInstanceAsync(string vmId)
    {
        if (!File.Exists(AdbExe) || !_processes.ContainsKey(vmId)) return;
        try
        {
            await RunAdbAsync(vmId, 4_000, "emu", "avd", "resume");
            Log("Resumed emulator after modal dialog: " + vmId);
        }
        catch (Exception ex)
        {
            Log("Emulator resume skipped: " + ex.Message);
        }
    }

    public void StopAll()
    {
        foreach (var id in _processes.Keys.ToArray()) StopInstance(id);
    }

    public void DeleteInstance(string vmId)
    {
        StopInstance(vmId);
        var dir = Path.Combine(_instancesDir, vmId);
        var cfg = InstanceConfigPath(vmId);
        try { if (File.Exists(cfg)) File.Delete(cfg); } catch { }
        TryDeleteDirectory(dir);
        TryDeleteDirectory(InstanceAvdDir(vmId));
        try
        {
            var ini = Path.Combine(_avdHome, InstanceAvdName(vmId) + ".ini");
            if (File.Exists(ini)) File.Delete(ini);
        }
        catch { }
    }

    public async Task SetAndroidResolutionAsync(string vmId, int w, int h)
    {
        if (!File.Exists(AdbExe)) return;
        var landscape = w > h;
        var profileIndex = landscape ? 2 : 0;
        if (landscape && TryGetRunContext(vmId, out var activeRun) &&
            string.Equals(NormalizeBootProfile(activeRun.Manifest.Profile), "api34-persona", StringComparison.Ordinal))
            throw new InvalidOperationException("Physical landscape profiles require the Resizable Analysis boot profile; Release Observation intentionally keeps ro.debuggable=0 and root denied.");
        var expectedWidth = landscape ? ResizableLandscapeWidth : ResizablePhoneWidth;
        var expectedHeight = landscape ? ResizableLandscapeHeight : ResizablePhoneHeight;
        var transitionGate = _displayProfileTransitions.GetOrAdd(vmId, _ => new SemaphoreSlim(1, 1));
        await transitionGate.WaitAsync();
        var previousProfileIndex = 0;
        try
        {
            var previousSize = await RunAdbAsync(vmId, 4_000, "shell", "wm", "size");
            previousProfileIndex = previousSize.Contains(
                $"Physical size: {ResizableLandscapeWidth}x{ResizableLandscapeHeight}",
                StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            Log($"Applying native resizable display profile {profileIndex} ({expectedWidth}x{expectedHeight})");
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var response = await RunAdbAsync(vmId, 6_000, "emu", "resize-display", profileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Android emulator rejected its native display profile: " + response.Trim());

                if (await WaitForDisplayProfileAsync(vmId, expectedWidth, expectedHeight, CancellationToken.None))
                {
                    // Qt acknowledges before the final scanout/host geometry event.
                    // A short settle keeps a second click from racing that event.
                    await Task.Delay(180);
                    return;
                }

                if (attempt < 2) await Task.Delay(550);
            }
            throw new InvalidOperationException($"Android display did not switch to {expectedWidth}x{expectedHeight}.");
        }
        catch
        {
            if (previousProfileIndex != profileIndex)
            {
                try
                {
                    Log($"Rolling native display host back to verified profile {previousProfileIndex}");
                    await RunAdbAsync(vmId, 6_000, "emu", "resize-display",
                        previousProfileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await Task.Delay(350);
                }
                catch (Exception rollbackEx)
                {
                    Log("Native display rollback failed: " + rollbackEx.Message);
                }
            }
            throw;
        }
        finally
        {
            transitionGate.Release();
        }
    }

    private async Task<bool> WaitForDisplayProfileAsync(string vmId, int width, int height, CancellationToken ct)
    {
        var expected = $"{width}x{height}";
        for (var attempt = 0; attempt < 18; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var current = await RunAdbAsync(vmId, 4_000, "shell", "wm", "size");
            if (current.Contains("Physical size: " + expected, StringComparison.OrdinalIgnoreCase)) return true;
            await Task.Delay(40, ct);
        }
        return false;
    }

    public async Task SetAndroidRotationAsync(string vmId, int rotation)
    {
        if (!File.Exists(AdbExe)) return;
        rotation = Math.Clamp(rotation, 0, 3);

        if (!UsesPlayCompatibilityProfile && !UsesPatchedPixelUtilityProfile)
        {
            // Native resizable profiles change the physical display dimensions;
            // rotating that new surface again would turn landscape back to portrait.
            await RunAdbAsync(vmId, 4_000, "shell", "settings", "put", "system", "accelerometer_rotation", "0");
            await RunAdbAsync(vmId, 4_000, "shell", "settings", "put", "system", "user_rotation", "0");
            return;
        }

        try
        {
            Log($"Flipping Android guest orientation user_rotation={rotation}");
            await RunAdbAsync(vmId, 4_000, "shell", "settings", "put", "system", "accelerometer_rotation", "0");
            await RunAdbAsync(vmId, 4_000, "shell", "settings", "put", "system", "user_rotation", rotation.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Do not use repeated `adb emu rotate`: it is relative and accumulates
            // across toggles. The absolute wm-size profile plus user_rotation is
            // repeatable in both directions on Android 11.
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var applied = (await RunAdbAsync(vmId, 4_000, "shell", "settings", "get", "system", "user_rotation")).Trim();
                if (string.Equals(applied, rotation.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)) break;
                if (attempt == 15) throw new InvalidOperationException("Android did not accept the requested rotation.");
                await Task.Delay(100);
            }

            await RunAdbAsync(vmId, 4_000, "shell", "cmd", "window", "dismiss-keyguard");
        }
        catch (Exception ex)
        {
            Log("Android rotation failed: " + ex.Message);
            throw;
        }
    }

    public Task SendAndroidTouchAsync(string vmId, int fromX, int fromY, int toX, int toY, int durationMs)
    {
        return Task.CompletedTask;
    }

    public string GetAdbPath() => AdbExe;

    public string? GetAdbSerial(string vmId) => TryGetRunContext(vmId, out var run) ? run.AdbSerial : null;

    public string? GetRunArtifactsDirectory(string vmId) => TryGetRunContext(vmId, out var run) ? run.ArtifactsDirectory : null;

    public void RegisterRunArtifact(string vmId, string kind, string path)
    {
        if (TryGetRunContext(vmId, out var run)) run.RegisterArtifact(kind, path);
    }

    private string ActiveEmulatorDir => _personaEmulatorDir;
    private string OfficialEmulatorExe => Path.Combine(_officialEmulatorDir, "emulator.exe");
    private string EmulatorExe => Path.Combine(ActiveEmulatorDir, "emulator.exe");
    private string QemuImgExe => Path.Combine(ActiveEmulatorDir, "qemu-img.exe");
    private string AdbExe => Path.Combine(_sdkRoot, "platform-tools", "adb.exe");
    private string BuildToolsDir => Path.Combine(_sdkRoot, "build-tools", "33.0.2");
    private string ApkSigner => Path.Combine(BuildToolsDir, "apksigner.bat");
    private string ZipAlign => Path.Combine(BuildToolsDir, "zipalign.exe");
    private bool SystemImageReady => File.Exists(Path.Combine(ActiveSystemImageDir, "system.img")) &&
                                     File.Exists(Path.Combine(ActiveSystemImageDir, "ramdisk.img")) &&
                                     (File.Exists(Path.Combine(ActiveSystemImageDir, "kernel-ranchu")) ||
                                      File.Exists(Path.Combine(ActiveSystemImageDir, "kernel-ranchu-64")));
    private bool AvdReady => File.Exists(Path.Combine(_avdHome, "ReVM.ini")) && File.Exists(Path.Combine(_avdDir, "config.ini"));
    private bool PublishedBaselineReady => File.Exists(Path.Combine(
        BaselineAvdDirectoryForProfile(ActiveBootProfile), "replayer-baseline.json"));

    private void EnsureBootstrapLayout()
    {
        foreach (var dir in new[]
        {
            _runtimeDir,
            _googleDir,
            _downloadsDir,
            _sdkRoot,
            _officialEmulatorDir,
            _personaEmulatorDir,
            _avdHome,
            _instancesDir,
            _logsDir,
            Path.Combine(_runtimeDir, "sessions"),
            Path.Combine(_runtimeDir, "evidence"),
            CasesRoot,
            Path.Combine(_runtimeDir, "apk-install-staging"),
            Path.Combine(_runtimeDir, "apk-signing"),
            Path.Combine(_runtimeDir, "tools"),
            Path.Combine(_runtimeDir, "tools", "bin"),
            Path.Combine(_runtimeDir, "tools", "cache"),
            Path.Combine(_runtimeDir, "captures"),
            Path.Combine(_runtimeDir, "reports")
        })
            Directory.CreateDirectory(dir);
    }

    private void StageManagedRuntimeFiles()
    {
        try
        {
            var managedDir = Path.Combine(_runtimeDir, "replayer", "bin");
            Directory.CreateDirectory(managedDir);
            var sourceDir = AppContext.BaseDirectory;
            foreach (var name in new[] { "REPlayer.dll", "REPlayer.exe", "REPlayer.deps.json", "REPlayer.runtimeconfig.json", "revm-render-host.dll" })
            {
                var src = Path.Combine(sourceDir, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(managedDir, name), overwrite: true);
            }

            var manifest = new
            {
                product = "REPlayer",
                bootstrapVersion = 1,
                updatedAtUtc = DateTime.UtcNow,
                baseDir = _baseDir,
                runtimeDir = _runtimeDir,
                sdkRoot = _sdkRoot,
                packages = new[]
                {
                    new { id = "google-emulator", path = EmulatorExe, required = true },
                    new { id = "platform-tools-adb", path = AdbExe, required = true },
                    new { id = "android-build-tools-apksigner", path = ApkSigner, required = true },
                    new { id = "android-build-tools-zipalign", path = ZipAlign, required = true },
                    new { id = "android-system-image", path = ActiveSystemImageDir, required = true }
                }
            };
            File.WriteAllText(Path.Combine(_runtimeDir, "bootstrap-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log("Managed runtime staging skipped: " + ex.Message);
        }
    }

    private async Task EnsureAndroidBuildToolsAsync(CancellationToken ct)
    {
        if (File.Exists(ApkSigner) && File.Exists(ZipAlign))
        {
            Status(44, "Android signing/build tools already installed");
            return;
        }

        var zipPath = Path.Combine(_downloadsDir, "build-tools_r33.0.2-windows.zip");
        var extractRoot = Path.Combine(_downloadsDir, "build-tools-33.0.2-extract");
        await DownloadAndExtractAsync("Android build-tools", BuildToolsUrl, zipPath, extractRoot,
            () => Directory.Exists(extractRoot) || (File.Exists(ApkSigner) && File.Exists(ZipAlign)),
            44, 50, ct);

        var apksigner = Directory.GetFiles(extractRoot, "apksigner.bat", SearchOption.AllDirectories).FirstOrDefault();
        if (apksigner == null) throw new InvalidOperationException("Android build-tools package did not contain apksigner.bat.");
        var extractedToolDir = Path.GetDirectoryName(apksigner)!;
        if (Directory.Exists(BuildToolsDir)) Directory.Delete(BuildToolsDir, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(BuildToolsDir)!);
        DirectoryCopy(extractedToolDir, BuildToolsDir, overwrite: true);
        try { Directory.Delete(extractRoot, recursive: true); } catch { }

        if (!File.Exists(ApkSigner) || !File.Exists(ZipAlign))
            throw new InvalidOperationException("Android build-tools install did not produce apksigner/zipalign.");
        Status(50, "Android signing/build tools installed");
    }

    private async Task DownloadAndExtractAsync(string label, string url, string zipPath, string extractRoot,
        Func<bool> ready, int startPct, int endPct, CancellationToken ct)
    {
        if (ready())
        {
            Status(endPct, label + " already installed");
            return;
        }

        if (!File.Exists(zipPath))
        {
            Status(startPct, "Downloading " + label);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(zipPath);
            var buffer = new byte[1024 * 1024];
            long readTotal = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (total > 0)
                {
                    var pct = startPct + (int)((endPct - startPct - 6) * (readTotal / (double)total));
                    Status(Math.Clamp(pct, startPct, endPct - 6), $"Downloading {label} {readTotal / 1024 / 1024}/{total / 1024 / 1024} MB");
                }
            }
        }

        Status(Math.Max(startPct, endPct - 5), "Extracting " + label);
        Directory.CreateDirectory(extractRoot);
        ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

        if (!ready())
        {
            // System image zip contains x86_64/ as top-level. Normalize into SDK layout.
            var nested = Path.Combine(extractRoot, "x86_64");
            if (Directory.Exists(nested) && !ReferenceEquals(extractRoot, _systemImageDir))
                DirectoryCopy(nested, ActiveSystemImageDir, true);
        }

        if (!ready()) throw new InvalidOperationException(label + " extraction did not produce expected files.");
        Status(endPct, label + " installed");
    }

    private void EnsureDefaultInstance()
    {
        if (Directory.EnumerateFiles(_instancesDir, "instance.json", SearchOption.AllDirectories).Any()) return;
        var settings = LoadBackendSettings();
        var cfg = DefaultInstanceConfig(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(InstanceConfigPath(cfg.Id))!);
        File.WriteAllText(InstanceConfigPath(cfg.Id), JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private GoogleInstanceConfig DefaultInstanceConfig(BackendSettings settings) => new()
    {
        Id = "google-avd-0",
        Name = "Android",
        CpuCount = Math.Clamp(settings.CpuCores, 1, 64),
        RamMB = Math.Clamp(settings.RamMb, 1024, 524288),
        StorageGB = Math.Clamp(settings.StorageGb, 8, 4096),
        BootProfile = NormalizeBootProfile(settings.BootProfile),
        CreatedAt = DateTime.UtcNow,
    };

    private GoogleInstanceConfig? LoadInstanceConfig(string id) =>
        File.Exists(InstanceConfigPath(id)) ? ReadInstanceConfig(InstanceConfigPath(id)) : null;

    private GoogleInstanceConfig? ReadInstanceConfig(string path)
    {
        try { return JsonSerializer.Deserialize<GoogleInstanceConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return null; }
    }

    private static string NormalizeBootProfile(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "api34-resizable" => "api34-resizable",
        "play-compat" => "play-compat",
        _ => "api34-persona"
    };

    private static void VerifyPublishedBaselineHashes(
        string avdDirectory,
        IReadOnlyDictionary<string, string> actualHashes,
        string? bootProfile)
    {
        var markerPath = Path.Combine(avdDirectory, "replayer-baseline.json");
        if (!File.Exists(markerPath))
            throw new InvalidOperationException("The immutable Android baseline has no publication marker: " + avdDirectory);

        using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
        var root = marker.RootElement;
        var profile = NormalizeBootProfile(bootProfile);
        var expectedMode = profile == "api34-resizable" ? "compatibility" : "stealth";
        var expectedTarget = profile == "api34-resizable" ? "ReVMResizable" : "ReVM";
        if (!root.TryGetProperty("mode", out var mode) || !string.Equals(mode.GetString(), expectedMode, StringComparison.Ordinal) ||
            !root.TryGetProperty("targetAvd", out var target) || !string.Equals(target.GetString(), expectedTarget, StringComparison.Ordinal) ||
            !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"The immutable Android publication marker does not match {profile}.");

        var verifiedImages = 0;
        foreach (var publishedFile in files.EnumerateObject())
        {
            // config.ini is intentionally rewritten in each immutable instance
            // baseline for its AVD name and selected CPU/RAM settings.
            if (string.Equals(publishedFile.Name, "config.ini", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(publishedFile.Name, "cache.img", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(publishedFile.Name, "cache.img.qcow2", StringComparison.OrdinalIgnoreCase)) continue;
            if (!publishedFile.Value.TryGetProperty("sha256", out var shaElement) ||
                !publishedFile.Value.TryGetProperty("bytes", out var bytesElement) ||
                !bytesElement.TryGetInt64(out var publishedBytes))
                throw new InvalidOperationException("The publication marker has incomplete file evidence for " + publishedFile.Name);
            var hashKey = "avd/" + publishedFile.Name.Replace('\\', '/');
            var filePath = Path.Combine(avdDirectory, publishedFile.Name);
            if (!actualHashes.TryGetValue(hashKey, out var actualSha) || !File.Exists(filePath) ||
                !string.Equals(actualSha, shaElement.GetString(), StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(filePath).Length != publishedBytes)
                throw new InvalidOperationException($"Immutable baseline integrity failed for {publishedFile.Name}; republish {expectedTarget} before launch.");
            verifiedImages++;
        }
        if (verifiedImages < 2)
            throw new InvalidOperationException("The publication marker does not contain enough immutable disk-image evidence.");
    }

    private static void AddRendererFeatureArguments(List<string> arguments, string? rendererApi)
    {
        // Keep host auto-selection unchanged unless the user explicitly chooses a
        // guest Vulkan policy. Each token is separate so no dynamic value is
        // interpreted by a command shell.
        if (string.Equals(rendererApi, "vulkan", StringComparison.OrdinalIgnoreCase))
            AddArguments(arguments, "-feature", "Vulkan");
        else if (string.Equals(rendererApi, "opengl", StringComparison.OrdinalIgnoreCase))
            AddArguments(arguments, "-feature", "-Vulkan");
    }

    private static void AddArguments(List<string> arguments, params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) arguments.Add(value);
    }

    private void ApplyWindowsGpuPreference(bool preferDiscreteGpu)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string ownedValue = "GpuPreference=2;";
        const string missingSentinel = "__REPLAYER_MISSING__";
        try
        {
            const string preferenceSubkey = @"Software\Microsoft\DirectX\UserGpuPreferences";
            const string backupSubkey = @"Software\REPlayer\GpuPreferenceBackup";
            using var preferences = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(preferenceSubkey, writable: true);
            using var backups = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(backupSubkey, writable: true);
            if (preferences is null || backups is null) return;
            var qemuExe = Path.Combine(ActiveEmulatorDir, "qemu", "windows-x86_64", "qemu-system-x86_64.exe");
            foreach (var executable in new[] { EmulatorExe, qemuExe })
            {
                if (!File.Exists(executable)) continue;
                var current = preferences.GetValue(executable) as string;
                if (preferDiscreteGpu)
                {
                    if (backups.GetValue(executable) is null)
                        backups.SetValue(executable, current ?? missingSentinel, Microsoft.Win32.RegistryValueKind.String);
                    preferences.SetValue(executable, ownedValue, Microsoft.Win32.RegistryValueKind.String);
                }
                else if (string.Equals(current, ownedValue, StringComparison.Ordinal))
                {
                    var previous = backups.GetValue(executable) as string;
                    if (string.Equals(previous, missingSentinel, StringComparison.Ordinal) || previous is null)
                        preferences.DeleteValue(executable, throwOnMissingValue: false);
                    else
                        preferences.SetValue(executable, previous, Microsoft.Win32.RegistryValueKind.String);
                    backups.DeleteValue(executable, throwOnMissingValue: false);
                }
            }
            Log(preferDiscreteGpu
                ? "Requested Windows high-performance GPU for emulator.exe and qemu-system-x86_64.exe"
                : "Restored prior Windows GPU preferences where REPlayer owned the current value");
        }
        catch (Exception ex)
        {
            Log("Could not update Windows GPU preference: " + ex.Message);
        }
    }

    private static string NormalizeGpuMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => "auto",
        "software" => "software",
        "swiftshader" => "swiftshader",
        "lavapipe" => "lavapipe",
        "swangle" => "swangle",
        _ => "host"
    };

    private static string NormalizeSystemUiRenderer(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "skiagl" => "skiagl",
        "skiavk" => "skiavk",
        _ => string.Empty
    };

    private static string InstanceAvdName(string id) => "ReVM_" + new string(id.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    private string InstanceAvdDir(string id) => Path.Combine(_avdHome, InstanceAvdName(id) + ".avd");

    private static string SanitizeId(string value)
    {
        var chars = (string.IsNullOrWhiteSpace(value) ? "android" : value.ToLowerInvariant())
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var id = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(id) ? "android" : id;
    }

    private void HardenExistingAvdColdBootPolicy()
    {
        if (!Directory.Exists(_avdHome)) return;
        foreach (var configPath in Directory.EnumerateFiles(_avdHome, "config.ini", SearchOption.AllDirectories))
        {
            try
            {
                var lines = File.ReadAllLines(configPath).ToList();
                var changed = UpsertIniValue(lines, "fastboot.forceColdBoot", "yes");
                changed |= UpsertIniValue(lines, "fastboot.forceFastBoot", "no");
                changed |= UpsertIniValue(lines, "snapshot.present", "no");
                if (!changed) continue;
                File.WriteAllLines(configPath, lines);
                Log("Hardened AVD cold-boot policy: " + configPath);
            }
            catch (Exception ex)
            {
                Log($"Could not harden AVD policy at {configPath}: {ex.Message}");
            }
        }
    }

    private static bool UpsertIniValue(List<string> lines, string key, string value)
    {
        var expected = key + "=" + value;
        var changed = false;
        var found = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            if (string.Equals(lines[index], expected, StringComparison.Ordinal)) continue;
            lines[index] = expected;
            changed = true;
        }
        if (found) return changed;
        lines.Add(expected);
        return true;
    }

    private void WriteAvdProfile(int cpuCount = LeanCpuCores, int ramMb = LeanRamMb, int storageGb = 8) =>
        WriteAvdProfile("ReVM", _avdDir, cpuCount, ramMb, storageGb);

    private void WriteAvdProfile(string avdName, string avdDir, int cpuCount = LeanCpuCores, int ramMb = LeanRamMb, int storageGb = 8, string? bootProfileOverride = null, string? avdHomeOverride = null)
    {
        var settings = LoadBackendSettings();
        var bootProfile = NormalizeBootProfile(bootProfileOverride ?? settings.BootProfile);
        var usesPlayCompatibilityProfile = string.Equals(bootProfile, "play-compat", StringComparison.OrdinalIgnoreCase);
        var usesPatchedPixelUtilityProfile = string.Equals(bootProfile, "patched-pixel-utility", StringComparison.OrdinalIgnoreCase);
        var usesResizableProfile = !usesPlayCompatibilityProfile && !usesPatchedPixelUtilityProfile;
        var canonicalAvdDir = BaselineAvdDirectoryForProfile(bootProfile);
        var activeSystemImageDir = usesPlayCompatibilityProfile
            ? _playStoreSystemImageDir
            : usesPatchedPixelUtilityProfile ? _patchedPixelSystemImageDir : _systemImageDir;
        var activeTagId = usesPlayCompatibilityProfile ? "google_apis_playstore" : "google_apis";
        var activeTagDisplay = usesPlayCompatibilityProfile ? "Google Play" : usesPatchedPixelUtilityProfile ? "Google APIs (Patched Utility; SDK hardware identity)" : "Google APIs";
        var lcdWidth = usesResizableProfile ? ResizablePhoneWidth : Math.Clamp(settings.DisplayWidth, 320, 3840);
        var lcdHeight = usesResizableProfile ? ResizablePhoneHeight : Math.Clamp(settings.DisplayHeight, 240, 2160);
        if (!usesResizableProfile)
        {
            if (string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase) && lcdWidth > lcdHeight)
                (lcdWidth, lcdHeight) = (lcdHeight, lcdWidth);
            else if (!string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase) && lcdHeight > lcdWidth)
                (lcdWidth, lcdHeight) = (lcdHeight, lcdWidth);
        }
        var portraitMode = usesResizableProfile || string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase);
        var lcdDpi = usesResizableProfile ? 420 : Math.Clamp(settings.DisplayDpi, 120, 640);
        var targetFps = Math.Clamp(settings.Fps, 30, 240);
        var displayVsyncRate = settings.VsyncEnabled ? targetFps : 240;
        if (!string.Equals(Path.GetFullPath(avdDir), Path.GetFullPath(canonicalAvdDir), StringComparison.OrdinalIgnoreCase))
        {
            var canonicalMarker = Path.Combine(canonicalAvdDir, "replayer-baseline.json");
            var instanceMarker = Path.Combine(avdDir, "replayer-baseline.json");
            if (File.Exists(canonicalMarker) &&
                (!File.Exists(instanceMarker) || !string.Equals(File.ReadAllText(instanceMarker), File.ReadAllText(canonicalMarker), StringComparison.Ordinal)))
            {
                OfficialEmulatorBaseline.Clone(canonicalAvdDir, avdDir, QemuImgExe,
                    checked((long)Math.Clamp(storageGb, 8, 4096) << 30));
                Log($"Refreshed instance AVD from immutable {bootProfile} baseline: {avdDir}");
            }
            else if (!File.Exists(canonicalMarker))
                throw new InvalidOperationException($"The {bootProfile} baseline is not published: {canonicalAvdDir}");
        }
        Directory.CreateDirectory(avdDir);
        var avdHome = string.IsNullOrWhiteSpace(avdHomeOverride) ? _avdHome : avdHomeOverride;
        Directory.CreateDirectory(avdHome);
        File.WriteAllText(Path.Combine(avdHome, avdName + ".ini"),
            $"avd.ini.encoding=UTF-8\r\npath={EscapePath(avdDir)}\r\npath.rel=avd/{avdName}.avd\r\ntarget=android-{(usesResizableProfile ? 34 : 30)}\r\n");

        var imageSysDir = EscapePath(activeSystemImageDir) + Path.DirectorySeparatorChar;
        var configPath = Path.Combine(avdDir, "config.ini");
        if (usesResizableProfile && File.Exists(configPath))
        {
            // Preserve Google's complete API 34 resizable hardware profile. Replacing it with a
            // reduced generic config leaves the personalized userdata offline/black during boot.
            var lines = File.ReadAllLines(configPath).ToList();
            UpsertIniValue(lines, "AvdId", avdName);
            UpsertIniValue(lines, "avd.ini.displayname", avdName);
            UpsertIniValue(lines, "PlayStore.enabled", "false");
            UpsertIniValue(lines, "disk.dataPartition.size", "6G");
            UpsertIniValue(lines, "fastboot.forceColdBoot", "yes");
            UpsertIniValue(lines, "fastboot.forceFastBoot", "no");
            UpsertIniValue(lines, "snapshot.present", "no");
            UpsertIniValue(lines, "hw.cpu.ncore", cpuCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpsertIniValue(lines, "hw.ramSize", ramMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpsertIniValue(lines, "hw.audioInput", settings.MicrophoneInput ? "yes" : "no");
            UpsertIniValue(lines, "hw.audioOutput", settings.SpeakerOutput ? "yes" : "no");
            UpsertIniValue(lines, "hw.gpu.enabled", "yes");
            UpsertIniValue(lines, "hw.gpu.mode", NormalizeGpuMode(settings.GpuMode));
            UpsertIniValue(lines, "hw.gltransport", "asg");
            UpsertIniValue(lines, "hw.gltransport.asg.writeBufferSize", "1048576");
            UpsertIniValue(lines, "hw.gltransport.asg.writeStepSize", "4096");
            UpsertIniValue(lines, "hw.gltransport.asg.dataRingSize", "65536");
            UpsertIniValue(lines, "hw.accelerometer", "no");
            UpsertIniValue(lines, "hw.accelerometer_uncalibrated", "no");
            UpsertIniValue(lines, "hw.battery", "no");
            UpsertIniValue(lines, "hw.dPad", "no");
            UpsertIniValue(lines, "hw.gyroscope", "no");
            UpsertIniValue(lines, "hw.gyroscope_uncalibrated", "no");
            UpsertIniValue(lines, "hw.sensors.gyroscope_uncalibrated", "no");
            UpsertIniValue(lines, "hw.sensors.humidity", "no");
            UpsertIniValue(lines, "hw.sensors.light", "no");
            UpsertIniValue(lines, "hw.sensors.magnetic_field", "no");
            UpsertIniValue(lines, "hw.sensors.magnetic_field_uncalibrated", "no");
            UpsertIniValue(lines, "hw.sensors.orientation", "no");
            UpsertIniValue(lines, "hw.sensors.pressure", "no");
            UpsertIniValue(lines, "hw.sensors.proximity", "no");
            UpsertIniValue(lines, "hw.sensors.temperature", "no");
            UpsertIniValue(lines, "hw.trackBall", "no");
            UpsertIniValue(lines, "hw.initialOrientation", "Portrait");
            UpsertIniValue(lines, "hw.lcd.width", ResizablePhoneWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpsertIniValue(lines, "hw.lcd.height", ResizablePhoneHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpsertIniValue(lines, "hw.lcd.density", "420");
            UpsertIniValue(lines, "hw.lcd.vsync", displayVsyncRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpsertIniValue(lines, "image.sysdir.1", imageSysDir);
            UpsertIniValue(lines, "tag.display", "Google APIs");
            UpsertIniValue(lines, "tag.id", "google_apis");
            UpsertIniValue(lines, "target", "android-34");
            File.WriteAllLines(configPath, lines);
            return;
        }
        File.WriteAllText(configPath,
            "AvdId=" + avdName + "\r\n" +
            "PlayStore.enabled=" + (usesPlayCompatibilityProfile ? "true" : "false") + "\r\n" +
            "abi.type=x86_64\r\n" +
            "avd.ini.displayname=" + avdName + "\r\n" +
            (usesResizableProfile ?
                "hw.device.name=resizable\r\n" +
                "hw.device.manufacturer=Generic\r\n" +
                "hw.device.hash2=MD5:6ea1f37386c6ff2bb09825b1a05114ee\r\n" +
                "hw.resizable.configs=phone-0-1080-2400-420, foldable-1-1768-2208-420, tablet-2-1920-1200-240\r\n" +
                "hw.displayRegion.0.1.width=1080\r\n" +
                "hw.displayRegion.0.1.height=2092\r\n" +
                "hw.displayRegion.0.1.xOffset=0\r\n" +
                "hw.displayRegion.0.1.yOffset=0\r\n" +
                "hw.sensor.hinge=yes\r\n" +
                "hw.sensor.hinge.count=1\r\n" +
                "hw.sensor.hinge.type=1\r\n" +
                "hw.sensor.hinge.sub_type=1\r\n" +
                "hw.sensor.hinge.resizable.config=1\r\n" +
                "hw.sensor.hinge.ranges=0-180\r\n" +
                "hw.sensor.hinge.defaults=180\r\n" +
                "hw.sensor.hinge.areas=1080-0-0-1840\r\n" +
                "hw.sensor.hinge.fold_to_displayRegion.0.1_at_posture=1\r\n" +
                "hw.sensor.posture_list=1, 2, 3\r\n" +
                "hw.sensor.hinge_angles_posture_definitions=0-30, 30-150, 150-180\r\n" : string.Empty) +
            (usesPatchedPixelUtilityProfile ? "hw.device.name=pixel_5\r\nhw.device.manufacturer=Google\r\nhw.device.hash2=MD5:3274126e0242a0d86339850416b5684b\r\n" : string.Empty) +
            "disk.dataPartition.size=" + storageGb + "G\r\n" +
            "fastboot.forceColdBoot=yes\r\n" +
            "fastboot.forceFastBoot=no\r\n" +
            "snapshot.present=no\r\n" +
            "disk.cachePartition=" + (settings.FastDisk ? "yes" : "no") + "\r\n" +
            "hw.accelerometer=no\r\n" +
            "hw.accelerometer_uncalibrated=no\r\n" +
            "hw.arc=false\r\n" +
            "hw.audioInput=" + (settings.MicrophoneInput ? "yes" : "no") + "\r\n" +
            "hw.audioOutput=" + (settings.SpeakerOutput ? "yes" : "no") + "\r\n" +
            "hw.battery=no\r\n" +
            "hw.cpu.arch=x86_64\r\n" +
            "hw.cpu.ncore=" + cpuCount + "\r\n" +
            "hw.dPad=no\r\n" +
            "hw.gltransport=asg\r\n" +
            "hw.gltransport.asg.writeBufferSize=1048576\r\n" +
            "hw.gltransport.asg.writeStepSize=4096\r\n" +
            "hw.gltransport.asg.dataRingSize=65536\r\n" +
            "hw.gltransport.drawFlushInterval=800\r\n" +
            "hw.gpu.enabled=yes\r\n" +
            "hw.gpu.mode=" + NormalizeGpuMode(settings.GpuMode) + "\r\n" +
            "hw.gyroscope=no\r\n" +
            "hw.gyroscope_uncalibrated=no\r\n" +
            "hw.keyboard=yes\r\n" +
            "hw.initialOrientation=" + (portraitMode ? "Portrait" : "Landscape") + "\r\n" +
            "hw.gsmModem=no\r\n" +
            "hw.camera.back=none\r\n" +
            "hw.camera.front=none\r\n" +
            "hw.gps=no\r\n" +
            "hw.lcd.density=" + lcdDpi + "\r\n" +
            "hw.lcd.height=" + lcdHeight + "\r\n" +
            "hw.lcd.vsync=" + displayVsyncRate + "\r\n" +
            "hw.lcd.width=" + lcdWidth + "\r\n" +
            "hw.mainKeys=no\r\n" +
            "hw.ramSize=" + ramMb + "\r\n" +
            "hw.sdCard=no\r\n" +
            "hw.screen=multi-touch\r\n" +
            "hw.sensors.orientation=no\r\n" +
            "hw.sensors.proximity=no\r\n" +
            "hw.sensors.light=no\r\n" +
            "hw.sensors.pressure=no\r\n" +
            "hw.sensors.humidity=no\r\n" +
            "hw.sensors.magnetic_field=no\r\n" +
            "hw.sensors.magnetic_field_uncalibrated=no\r\n" +
            "hw.sensors.temperature=no\r\n" +
            "hw.sensors.rgbclight=no\r\n" +
            "hw.sensors.heart_rate=no\r\n" +
            "hw.sensors.wrist_tilt=no\r\n" +
            "hw.trackBall=no\r\n" +
            "image.sysdir.1=" + imageSysDir + "\r\n" +
            "runtime.network.latency=none\r\n" +
            "runtime.network.speed=full\r\n" +
            "skin.dynamic=yes\r\n" +
            "skin.name=" + lcdWidth + "x" + lcdHeight + "\r\n" +
            "showDeviceFrame=no\r\n" +
            "tag.display=" + activeTagDisplay + "\r\n" +
            "tag.id=" + activeTagId + "\r\n" +
            "target=android-" + (usesResizableProfile ? "34" : "30") + "\r\n");
    }

    private void WriteRuntimeInfo()
    {
        var info = new
        {
            backend = BackendName,
            emulator = EmulatorExe,
            adb = AdbExe,
            systemImage = ActiveSystemImageDir,
            bootProfile = LoadBackendSettings().BootProfile,
            avdHome = _avdHome,
            avd = "ReVM",
            ports = new { dynamicPerRun = true, preferredAdb = LoadBackendSettings().AdbPort },
            containment = new { defaultMode = "disposable-detonation", enabled = LoadBackendSettings().OfficialEmulatorDisposableMode },
            gpu = NormalizeGpuMode(LoadBackendSettings().GpuMode),
            systemUiRenderer = NormalizeSystemUiRenderer(LoadBackendSettings().SystemUiRenderer),
            preferDiscreteGpu = LoadBackendSettings().PreferDiscreteGpu,
            leanGuest = LoadBackendSettings().LeanGuestEnabled,
            graphics = "gfxstream/ASG + selectable official emulator GPU mode (guest Vulkan only when RendererApi=vulkan)"
        };
        File.WriteAllText(Path.Combine(_googleDir, "runtime-info.json"),
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task WaitForAndroidReadyAsync(string vmId, IntPtr hostHandle, int hostW, int hostH, CancellationToken ct)
    {
        try
        {
            Status(25, "Waiting for ADB device");
            var serial = GetRequiredAdbSerial(vmId);
            var adbReady = false;
            for (var i = 0; i < 90; i++)
            {
                ct.ThrowIfCancellationRequested();
                var devices = await RunAsync(AdbExe, "devices", 5_000);
                if (devices.Contains(serial + "\tdevice", StringComparison.OrdinalIgnoreCase))
                {
                    adbReady = true;
                    break;
                }
                await Task.Delay(1000, ct);
            }
            if (!adbReady)
            {
                StatusChanged?.Invoke("Emulator started, but its isolated ADB endpoint did not become ready.");
                return;
            }

            var networkSettings = LoadBackendSettings();
            if (networkSettings.AdbRoot)
            {
                try
                {
                    await RunAdbAsync(vmId, 8_000, "root");
                    await WaitForStableAdbDeviceAsync(vmId, ct);
                }
                catch (Exception ex) { Log("Early adb root unavailable for guest network filter: " + ex.Message); }
            }
            await ApplyGuestNetworkPolicyAsync(vmId, networkSettings, ct);

            Status(55, "Waiting for Android boot_completed");
            for (var i = 0; i < 180; i++)
            {
                ct.ThrowIfCancellationRequested();
                var state = await RunAdbAsync(vmId, 5_000, "shell", "getprop", "sys.boot_completed");
                if (state.Trim() == "1")
                {
                    Status(90, "Android boot_completed=1");
                    Status(92, "Verifying ADB control channel");
                    if (!await WaitForStableAdbDeviceAsync(vmId, ct))
                    {
                        StatusChanged?.Invoke("Android booted, but ADB control channel did not become stable.");
                        return;
                    }
                    if (!await WaitForAndroidFrameworkServicesAsync(vmId, ct))
                    {
                        StatusChanged?.Invoke("Android booted, but package/settings/window services did not become stable.");
                        return;
                    }

                    var startupProfile = NormalizeBootProfile(GetRequiredRunContext(vmId).Manifest.Profile);
                    if (string.Equals(startupProfile, "api34-resizable", StringComparison.Ordinal))
                    {
                        Status(93, "Enabling debuggable ADB for Resizable Analysis");
                        await RunAdbAsync(vmId, 8_000, "root");
                        if (!await WaitForStableAdbDeviceAsync(vmId, ct) || !await WaitForAndroidFrameworkServicesAsync(vmId, ct))
                            throw new InvalidOperationException("Resizable Analysis restarted ADB as root, but Android did not reconnect cleanly.");
                        var rootUid = (await RunAdbAsync(vmId, 5_000, "shell", "id", "-u")).Trim();
                        if (!string.Equals(rootUid, "0", StringComparison.Ordinal))
                            throw new InvalidOperationException($"Resizable Analysis requires root-capable ADB, but shell uid is {rootUid}.");
                        BootDebug("Resizable Analysis ADB root accepted; privileged persona controls are available.");
                    }

                    Status(94, "Applying and verifying Android persona");
                    var personaVerified = await ApplyAndVerifyPersonaAsync(vmId, ct);
                    if (!personaVerified && LoadBackendSettings().PersonaFailClosed)
                    {
                        StatusChanged?.Invoke("Persona verification failed closed; stopping Android.");
                        StopInstance(vmId);
                        return;
                    }

                    if (!_embeddedWindows.ContainsKey(vmId) && _processes.TryGetValue(vmId, out var proc))
                        AttachNativeEmulatorWindow(vmId, hostHandle, hostW, hostH, proc, TimeSpan.FromSeconds(10));

                    await ApplyBasePhoneProvisioningAsync(vmId, ct);
                    var readySettings = LoadBackendSettings();
                    var readyProfile = NormalizeBootProfile(GetRequiredRunContext(vmId).Manifest.Profile);
                    var readyWidth = Math.Clamp(readySettings.DisplayWidth, 320, 3840);
                    var readyHeight = Math.Clamp(readySettings.DisplayHeight, 240, 2160);
                    var readyPortrait = string.Equals(readyProfile, "api34-persona", StringComparison.Ordinal) ||
                                        string.Equals(readySettings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase);
                    if (readyPortrait && readyWidth > readyHeight) (readyWidth, readyHeight) = (readyHeight, readyWidth);
                    if (!readyPortrait && readyHeight > readyWidth) (readyWidth, readyHeight) = (readyHeight, readyWidth);
                    await SetAndroidResolutionAsync(vmId, readyWidth, readyHeight);
                    await SetAndroidRotationAsync(vmId, 0);
                    if (_embeddedWindows.TryGetValue(vmId, out var hwnd)) HideEmulatorToolbarChildren(hwnd);
                    Status(100, "Android ready: native gfxstream emulator window attached");
                    return;
                }
                await Task.Delay(1000, ct);
            }

            StatusChanged?.Invoke("Google emulator started, but Android did not report boot_completed before timeout.");
        }
        catch (Exception ex)
        {
            Log("ADB/readiness/renderer failed: " + ex);
            StatusChanged?.Invoke("ADB/readiness/renderer failed: " + ex.Message);
        }
    }

    private async Task<bool> ApplyAndVerifyPersonaAsync(string vmId, CancellationToken ct)
    {
        if (!TryGetRunContext(vmId, out var run) || run.PersonaPlan is null) return true;
        var plan = run.PersonaPlan;
        var commandErrors = new List<string>();
        foreach (var command in plan.PostBootCommands)
        {
            ct.ThrowIfCancellationRequested();
            var completed = false;
            var lastFailure = string.Empty;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    var output = await RunAdbAsync(vmId, 8_000, "shell", "sh", "-c", QuoteRemoteShellScript(command));
                    if (IsTransientAndroidServiceFailure(output))
                    {
                        lastFailure = SummarizePolicyOutput(output);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(output) && output.Contains("error", StringComparison.OrdinalIgnoreCase))
                            commandErrors.Add(SummarizePolicyOutput(output));
                        completed = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lastFailure = ex.Message;
                }

                if (attempt < 5)
                {
                    var frameworkRecovered = await WaitForAndroidFrameworkServicesAsync(vmId, ct);
                    if (!frameworkRecovered) await Task.Delay(1000, ct);
                }
            }

            if (!completed)
                commandErrors.Add("Persona write failed after retries: " + lastFailure);
        }

        if (!await WaitForAndroidFrameworkServicesAsync(vmId, ct))
            commandErrors.Add("Android framework services did not stabilize after persona writes.");
        else
        {
            // Emulator -change-locale can restart SettingsProvider after boot_completed.
            // Reassert controlled values only after that framework transition settles.
            foreach (var command in plan.PostBootCommands)
            {
                var reasserted = false;
                var lastFailure = string.Empty;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var output = await RunAdbAsync(vmId, 8_000, "shell", "sh", "-c", QuoteRemoteShellScript(command));
                        if (!IsTransientAndroidServiceFailure(output) &&
                            !(output.Contains("error", StringComparison.OrdinalIgnoreCase) || output.Contains("exception", StringComparison.OrdinalIgnoreCase)))
                        {
                            reasserted = true;
                            break;
                        }
                        lastFailure = SummarizePolicyOutput(output);
                    }
                    catch (Exception ex)
                    {
                        lastFailure = ex.Message;
                    }
                    if (attempt < 3) await Task.Delay(750, ct);
                }
                if (!reasserted) commandErrors.Add("Post-stability persona write failed: " + lastFailure);
            }
            if (!await WaitForAndroidFrameworkServicesAsync(vmId, ct))
                commandErrors.Add("Android framework services did not remain stable after persona reassertion.");
        }

        async Task<string> Read(string field, params string[] arguments)
        {
            var lastFailure = string.Empty;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    var output = (await RunAdbAsync(vmId, 8_000, arguments)).Trim();
                    if (!ShouldRetryPersonaRead(output)) return output;
                    lastFailure = string.IsNullOrWhiteSpace(output) ? "empty response" : SummarizePolicyOutput(output);
                }
                catch (Exception ex)
                {
                    lastFailure = ex.Message;
                }

                if (attempt < 5)
                {
                    var frameworkRecovered = await WaitForAndroidFrameworkServicesAsync(vmId, ct);
                    if (!frameworkRecovered) await Task.Delay(1000, ct);
                }
            }

            commandErrors.Add(field + " read failed after retries: " + lastFailure);
            return string.Empty;
        }

        var actualModel = await Read("hardware model", "shell", "getprop", "ro.product.model");
        var actualMaker = await Read("hardware maker", "shell", "getprop", "ro.product.manufacturer");
        var actualBrand = await Read("hardware brand", "shell", "getprop", "ro.product.brand");
        var actualDevice = await Read("hardware device", "shell", "getprop", "ro.product.device");
        var actualSerial = await Read("serial", "shell", "getprop", "ro.serialno");
        var actualLocale = await Read("locale", "shell", "getprop", "ro.product.locale");
        var actualTimezone = await Read("timezone", "shell", "getprop", "persist.sys.timezone");
        var actualAndroidId = await Read("Android ID", "shell", "settings", "get", "secure", "android_id");
        var actualDeviceName = await Read("device name", "shell", "settings", "get", "global", "device_name");
        string actualCountryRaw;
        string actualCountry;
        if (plan.CountryControl == "wifi-service")
        {
            actualCountryRaw = await Read("country code", "shell", "cmd", "wifi", "get-country-code");
            var actualCountryMatches = Regex.Matches(actualCountryRaw, "\\b[A-Za-z]{2}\\b");
            actualCountry = actualCountryMatches.Count > 0
                ? actualCountryMatches[^1].Value.ToUpperInvariant()
                : actualCountryRaw;
        }
        else
        {
            actualCountryRaw = actualLocale;
            var separator = actualLocale.LastIndexOf('-');
            actualCountry = separator >= 0 && separator + 1 < actualLocale.Length
                ? actualLocale[(separator + 1)..].ToUpperInvariant()
                : string.Empty;
        }
        var mismatches = new List<string>();
        if (!string.Equals(actualModel, plan.ExpectedHardwareModel, StringComparison.Ordinal)) mismatches.Add("hardware model mismatch");
        if (!string.Equals(actualMaker, plan.ExpectedHardwareMaker, StringComparison.Ordinal)) mismatches.Add("hardware maker mismatch");
        if (!string.Equals(actualBrand, plan.ExpectedHardwareBrand, StringComparison.Ordinal)) mismatches.Add("hardware brand mismatch");
        if (!string.Equals(actualDevice, plan.ExpectedHardwareDevice, StringComparison.Ordinal)) mismatches.Add("hardware device mismatch");
        if (!actualSerial.StartsWith(plan.ExpectedSerialPrefix, StringComparison.OrdinalIgnoreCase)) mismatches.Add("serial prefix mismatch");
        if (!string.Equals(actualLocale, plan.Locale, StringComparison.OrdinalIgnoreCase)) mismatches.Add("locale mismatch");
        if (!string.Equals(actualTimezone, plan.Timezone, StringComparison.OrdinalIgnoreCase)) mismatches.Add("timezone mismatch");
        if (!string.Equals(actualAndroidId, plan.AndroidId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("Android ID mismatch");
        if (!string.Equals(actualDeviceName, plan.DeviceModel, StringComparison.Ordinal)) mismatches.Add("device name mismatch");
        if (!string.Equals(actualCountry, plan.CountryCode, StringComparison.OrdinalIgnoreCase)) mismatches.Add("country code mismatch");
        var success = commandErrors.Count == 0 && mismatches.Count == 0;

        run.Manifest.PersonaVerification = new
        {
            success,
            fingerprint = plan.PersonaFingerprint,
            actual = new
            {
                model = actualModel,
                maker = actualMaker,
                brand = actualBrand,
                device = actualDevice,
                serial = actualSerial,
                locale = actualLocale,
                timezone = actualTimezone,
                androidId = actualAndroidId,
                deviceName = actualDeviceName,
                countryCode = actualCountry,
                countryCodeRaw = actualCountryRaw,
                countryControl = plan.CountryControl
            },
            phoneNumber = new { planned = plan.PhoneNumber, verified = false, reason = "Official emulator radio APIs do not expose a stable non-privileged verification surface." },
            errors = commandErrors.Concat(mismatches).ToArray()
        };
        run.WriteManifest();
        var diagnostics = commandErrors.Concat(mismatches).ToArray();
        Log(success
            ? "Phase 3 persona verified: " + plan.PersonaFingerprint
            : "Phase 3 persona verification failed: " + string.Join(" | ", diagnostics));
        return success;
    }

    private async Task ApplyGuestNetworkPolicyAsync(string vmId, BackendSettings settings, CancellationToken ct)
    {
        if (!TryGetRunContext(vmId, out var run)) return;
        try
        {
            var plan = OfficialEmulatorNetworkIsolation.BuildPlan(settings, _baseDir);
            if (plan.GuestFilterScripts.Count == 0)
            {
                run.Manifest.GuestNetworkFilterApplied = true;
                run.Manifest.GuestNetworkFilterStatus = plan.IsOffline ? "Offline mode enforced by emulator and host policy." : "Guest filter not required for this mode.";
                run.WriteManifest();
                return;
            }

            var outputs = new List<string>();
            var allApplied = true;
            foreach (var script in plan.GuestFilterScripts)
            {
                ct.ThrowIfCancellationRequested();
                var markedScript = script + "; rc=$?; echo REPLAYER_NETWORK_RC:$rc";
                var output = await RunAdbAsync(vmId, 12_000, "shell", "sh", "-c", QuoteRemoteShellScript(markedScript));
                outputs.Add(output.Trim());
                if (!output.Contains("REPLAYER_NETWORK_RC:0", StringComparison.Ordinal)) allApplied = false;
            }

            run.Manifest.GuestNetworkFilterApplied = allApplied;
            run.Manifest.GuestNetworkFilterStatus = allApplied
                ? "Guest IPv4/IPv6 destination filters applied before boot completion."
                : "Host policy verified, but one or more guest defense-in-depth filters were unavailable: " + string.Join(" | ", outputs.Select(SummarizePolicyOutput));
            run.WriteManifest();
            Log(run.Manifest.GuestNetworkFilterStatus);
        }
        catch (Exception ex)
        {
            run.Manifest.GuestNetworkFilterApplied = false;
            run.Manifest.GuestNetworkFilterStatus = "Host policy verified, but guest filter application failed: " + ex.Message;
            run.WriteManifest();
            Log(run.Manifest.GuestNetworkFilterStatus);
        }
    }

    private static string SummarizePolicyOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "no output";
        var compact = string.Join(' ', output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 240 ? compact : compact[..240] + "...";
    }

    private async Task ApplyBasePhoneProvisioningAsync(string vmId, CancellationToken ct)
    {
        if (!File.Exists(AdbExe)) return;
        var settings = LoadBackendSettings();
        Status(94, "Applying runtime Android settings");

        if (settings.AdbRoot)
        {
            try { await RunAdbAsync(vmId, 8_000, "root"); }
            catch (Exception ex) { Log("adb root unavailable/skipped: " + ex.Message); }
            if (!await WaitForStableAdbDeviceAsync(vmId, ct) || !await WaitForAndroidFrameworkServicesAsync(vmId, ct))
                throw new InvalidOperationException("ADB restarted for root access, but the Android framework did not reconnect cleanly.");
            if (!settings.ReadOnlyBaseImage)
            {
                try { await RunAdbAsync(vmId, 8_000, "remount"); }
                catch (Exception ex) { Log("adb remount unavailable/skipped: " + ex.Message); }
            }
        }

        var installedPackages = ParsePackageList(await RunStablePackageCommandAsync(vmId, ct, "shell", "pm", "list", "packages"));
        foreach (var packageName in _baselineDisabledPackages.Where(installedPackages.Contains))
        {
            ct.ThrowIfCancellationRequested();
            var output = await RunStablePackageCommandAsync(vmId, ct,
                "shell", "pm", "disable-user", "--user", "0", packageName);
            if (output.Contains("Failure", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                Log("Package disable failed: " + packageName + " :: " + SummarizePolicyOutput(output));
        }

        await VerifyFixedAndroidBaselineAsync(vmId, ct);

        var profileRefreshRate = Math.Clamp(settings.Fps, 30, 240);
        var peakRefreshRate = settings.VsyncEnabled ? profileRefreshRate : 240;
        var minRefreshRate = settings.FreesyncEnabled ? Math.Min(60, peakRefreshRate) : peakRefreshRate;
        var mediaVolume = settings.AudioMuted || !settings.SpeakerOutput
            ? 0
            : Math.Clamp((int)Math.Round(settings.AudioVolumePercent * 15.0 / 100.0), 0, 15);
        var commands = new[]
        {
            "settings put global stay_on_while_plugged_in 3",
            "settings put secure location_mode 0",
            "settings put secure screensaver_enabled 0",
            "settings put system screen_off_timeout 2147483647",
            "settings put system accelerometer_rotation 0",
            "settings put system user_rotation 0",
            $"settings put global window_animation_scale {(settings.LeanGuestEnabled ? 0 : 1)}",
            $"settings put global transition_animation_scale {(settings.LeanGuestEnabled ? 0 : 1)}",
            $"settings put global animator_duration_scale {(settings.LeanGuestEnabled ? 0 : 1)}",
            settings.LeanGuestEnabled ? "settings put global adaptive_battery_management_enabled 0" : "settings delete global adaptive_battery_management_enabled",
            settings.LeanGuestEnabled ? "settings put global wifi_scan_always_enabled 0" : "settings delete global wifi_scan_always_enabled",
            $"settings put system peak_refresh_rate {peakRefreshRate}",
            $"settings put system min_refresh_rate {minRefreshRate}",
            $"settings put global peak_refresh_rate {peakRefreshRate}",
            $"settings put global min_refresh_rate {minRefreshRate}",
            "settings put global development_settings_enabled 0",
            "settings put global adb_enabled 1",
            "settings put global verifier_verify_adb_installs 0",
            "stop console",
            "cmd appops set android POST_NOTIFICATION ignore",
            "cmd appops set com.android.emulator.multidisplay POST_NOTIFICATION ignore",
            $"settings put system volume_music_speaker {mediaVolume}",
            $"settings put system volume_music_headset {mediaVolume}",
            "svc power stayon true",
            "cmd notification set_dnd on"
        };

        foreach (var command in commands)
        {
            ct.ThrowIfCancellationRequested();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var output = await RunAdbAsync(vmId, 6_000, "shell", command);
                    if (!IsTransientAndroidServiceFailure(output)) break;
                    if (attempt == 3)
                    {
                        Log("Runtime guest setting failed after framework retries: " + command + " :: " + SummarizePolicyOutput(output));
                        break;
                    }
                    await WaitForAndroidFrameworkServicesAsync(vmId, ct);
                }
                catch (Exception ex)
                {
                    if (attempt == 3)
                    {
                        Log("Runtime guest setting skipped: " + command + " :: " + ex.Message);
                        break;
                    }
                    await WaitForStableAdbDeviceAsync(vmId, ct);
                    await WaitForAndroidFrameworkServicesAsync(vmId, ct);
                }
            }
        }
    }

    private async Task<string> RunStablePackageCommandAsync(string vmId, CancellationToken ct, params string[] arguments)
    {
        string output = string.Empty;
        var command = string.Join(' ', arguments);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            BootDebug($"Package Manager command {attempt}/5: {command}");
            try
            {
                output = await RunAdbAsync(vmId, 10_000, arguments);
            }
            catch (Exception ex)
            {
                BootDebug($"Package Manager command failed: {command} :: {ex.Message}");
                throw;
            }
            if (!IsTransientAndroidServiceFailure(output)) return output;
            var summary = SummarizePolicyOutput(output);
            Log($"Package Manager command transiently unavailable (attempt {attempt}/5): {command} :: {summary}");
            BootDebug($"Package Manager unavailable; waiting for framework recovery: {summary}");
            if (attempt == 5) break;
            await WaitForStableAdbDeviceAsync(vmId, ct);
            await WaitForAndroidFrameworkServicesAsync(vmId, ct);
            await Task.Delay(500, ct);
        }
        return output;
    }

    private async Task VerifyFixedAndroidBaselineAsync(string vmId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Status(95, "Verifying fixed Android image");
        var run = GetRequiredRunContext(vmId);
        var activeProfile = NormalizeBootProfile(run.Manifest.Profile);
        var expectedTarget = activeProfile == "api34-resizable" ? "ReVMResizable" : "ReVM";
        var expectedMode = activeProfile == "api34-resizable" ? "compatibility" : "stealth";
        var markerPath = Path.Combine(run.RunAvdDirectory, "replayer-baseline.json");
        if (!File.Exists(markerPath))
            throw new InvalidOperationException("The immutable Android baseline is not personalized. Publish it with scripts/android/Publish-Api34Persona.sh.");
        try
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = marker.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
                !root.TryGetProperty("persona", out var persona) ||
                !string.Equals(persona.GetString(), "replayer-api34", StringComparison.Ordinal) ||
                !root.TryGetProperty("mode", out var mode) ||
                !string.Equals(mode.GetString(), expectedMode, StringComparison.Ordinal) ||
                !root.TryGetProperty("targetAvd", out var targetAvd) ||
                !string.Equals(targetAvd.GetString(), expectedTarget, StringComparison.Ordinal) ||
                !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object ||
                !files.TryGetProperty("system.img.qcow2", out _) ||
                !files.TryGetProperty("userdata-qemu.img.qcow2", out _))
                throw new InvalidOperationException($"The immutable Android baseline marker does not match {activeProfile}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The immutable Android baseline marker is malformed.", ex);
        }

        BootDebug("Baseline marker accepted; checking ADB and Android framework stability.");
        if (!await WaitForStableAdbDeviceAsync(vmId, ct) || !await WaitForAndroidFrameworkServicesAsync(vmId, ct))
            throw new InvalidOperationException("Android framework services were not stable while verifying the immutable baseline.");
        var actualDebuggable = (await RunAdbAsync(vmId, 5_000, "shell", "getprop", "ro.debuggable")).Trim();
        var actualAdbSecure = (await RunAdbAsync(vmId, 5_000, "shell", "getprop", "ro.adb.secure")).Trim();
        var expectedDebuggable = activeProfile == "api34-resizable" ? "1" : "0";
        if (!string.Equals(actualDebuggable, expectedDebuggable, StringComparison.Ordinal) ||
            !string.Equals(actualAdbSecure, "1", StringComparison.Ordinal))
            throw new InvalidOperationException($"The {activeProfile} guest security properties are invalid: ro.debuggable={actualDebuggable}, ro.adb.secure={actualAdbSecure}.");
        BootDebug($"Security lane accepted: {activeProfile}, ro.debuggable={actualDebuggable}, authenticated ADB enabled.");
        Status(96, "Checking Android system applications");
        var customizerPath = await RunStablePackageCommandAsync(vmId, ct, "shell", "pm", "path", "com.replayer.utility");
        if (!customizerPath.Contains("package:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixed Android image does not contain the REPlayer wallpaper/tools companion package.");
        var chromePath = await RunStablePackageCommandAsync(vmId, ct, "shell", "pm", "path", "com.android.chrome");
        if (!chromePath.Contains("package:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixed Android image does not contain Chrome.");
        string selectedHome = string.Empty;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            selectedHome = await RunStablePackageCommandAsync(vmId, ct, "shell", "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.HOME");
            if (selectedHome.Contains("com.google.android.apps.nexuslauncher/.NexusLauncherActivity", StringComparison.OrdinalIgnoreCase))
                break;
            Log($"Nexus HOME role has not settled yet (attempt {attempt}/8): {selectedHome.Trim()}");
            if (attempt < 8) await Task.Delay(750, ct);
        }
        if (!selectedHome.Contains("com.google.android.apps.nexuslauncher/.NexusLauncherActivity", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixed Android image does not select the stock Android 14 Nexus Launcher as HOME.");
        Status(97, "Checking Android personalization");
        var dynamicTheme = await RunStablePackageCommandAsync(vmId, ct, "shell", "settings", "get", "secure", "theme_customization_overlay_packages");
        var themedIcons = (await RunStablePackageCommandAsync(vmId, ct, "shell", "settings", "get", "secure", "themed_icon")).Trim();
        var launcherThemedIcons = (await RunStablePackageCommandAsync(vmId, ct, "shell", "settings", "get", "secure", "themed_icons")).Trim();
        var nightService = (await RunStablePackageCommandAsync(vmId, ct, "shell", "cmd", "uimode", "night")).Trim();
        var nightSetting = (await RunStablePackageCommandAsync(vmId, ct, "shell", "settings", "get", "secure", "ui_night_mode")).Trim();
        var nightState = await RunStablePackageCommandAsync(vmId, ct, "shell", "dumpsys", "uimode");
        if (!dynamicTheme.Contains("MONOCHROMATIC", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(themedIcons, "1", StringComparison.Ordinal) ||
            !string.Equals(launcherThemedIcons, "1", StringComparison.Ordinal) ||
            !string.Equals(nightService, "Night mode: yes", StringComparison.Ordinal) ||
            !string.Equals(nightSetting, "2", StringComparison.Ordinal) ||
            !nightState.Contains("mComputedNightMode=true", StringComparison.Ordinal) ||
            !nightState.Contains("mCurUiMode=0x21", StringComparison.Ordinal))
            throw new InvalidOperationException("The fixed Android baseline is missing its dark monochromatic palette or themed-icon setting.");

        Status(98, "Checking Android package policy");
        var installed = ParsePackageList(await RunStablePackageCommandAsync(vmId, ct, "shell", "pm", "list", "packages"));
        var disabled = ParsePackageList(await RunStablePackageCommandAsync(vmId, ct, "shell", "pm", "list", "packages", "-d"));
        var enabledBloat = _baselineDisabledPackages.Where(packageName => installed.Contains(packageName) && !disabled.Contains(packageName)).ToArray();
        if (enabledBloat.Length > 0)
            throw new InvalidOperationException("The fixed Android baseline still enables unwanted packages: " + string.Join(", ", enabledBloat));
        var requiredLauncherPackages = new[]
        {
            "com.google.android.apps.nexuslauncher",
            "com.google.android.googlequicksearchbox"
        };
        var missingLauncherPackages = requiredLauncherPackages
            .Where(packageName => !installed.Contains(packageName) || disabled.Contains(packageName))
            .ToArray();
        if (missingLauncherPackages.Length > 0)
            throw new InvalidOperationException("The fixed Android baseline does not enable the stock launcher/search packages: " + string.Join(", ", missingLauncherPackages));
        Status(99, "Finalizing Android startup");
        Log($"Immutable Android 14 {activeProfile} baseline verified; neutral REPlayer identity, stock Nexus HOME, persistent dark monochromatic palette, themed icons, wallpaper, and font were not patched at launch.");
    }

    private static bool ShouldRetryPersonaRead(string output) =>
        string.IsNullOrWhiteSpace(output) || IsTransientAndroidServiceFailure(output);

    private static bool IsTransientAndroidServiceFailure(string output) =>
        output.Contains("Can't find service", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("device offline", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("error: closed", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Failure calling service", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("PackageInstallerSession", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("PackageManagerInternal", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("StorageManagerService", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> WaitForAndroidFrameworkServicesAsync(string vmId, CancellationToken ct)
    {
        var consecutiveHealthySamples = 0;
        for (var attempt = 0; attempt < 45; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt == 0 || attempt % 5 == 0)
                BootDebug($"Android framework stability sample {attempt + 1}/45; healthy sequence {consecutiveHealthySamples}/3.");
            try
            {
                var boot = (await RunAdbAsync(vmId, 5_000, "shell", "getprop", "sys.boot_completed")).Trim();
                var package = await RunAdbAsync(vmId, 5_000, "shell", "service", "check", "package");
                var settings = await RunAdbAsync(vmId, 5_000, "shell", "service", "check", "settings");
                var window = await RunAdbAsync(vmId, 5_000, "shell", "service", "check", "window");
                var wallpaper = await RunAdbAsync(vmId, 5_000, "shell", "service", "check", "wallpaper");
                if (boot == "1" &&
                    package.Contains("found", StringComparison.OrdinalIgnoreCase) &&
                    settings.Contains("found", StringComparison.OrdinalIgnoreCase) &&
                    window.Contains("found", StringComparison.OrdinalIgnoreCase) &&
                    wallpaper.Contains("found", StringComparison.OrdinalIgnoreCase))
                {
                    consecutiveHealthySamples++;
                    BootDebug($"Android framework healthy sample {consecutiveHealthySamples}/3.");
                    if (consecutiveHealthySamples >= 3) return true;
                }
                else
                {
                    if (consecutiveHealthySamples > 0)
                        BootDebug("Android framework healthy sequence reset; one or more services disappeared.");
                    consecutiveHealthySamples = 0;
                }
            }
            catch (Exception ex)
            {
                consecutiveHealthySamples = 0;
                if (attempt == 0 || attempt % 5 == 0)
                    BootDebug("Android framework probe failed: " + ex.Message);
            }
            await Task.Delay(1000, ct);
        }
        return false;
    }

    private static HashSet<string> ParsePackageList(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
        .Select(line => line["package:".Length..].Trim())
        .Where(packageName => packageName.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void LogAccelerationStatus()
    {
        try
        {
            var result = RunAsync(EmulatorExe, "-accel-check", 8_000).GetAwaiter().GetResult().Trim();
            Log("hardware.acceleration: " + result.Replace("\r", " ").Replace("\n", " | "));
        }
        catch (Exception ex)
        {
            Log("hardware.acceleration check skipped: " + ex.Message);
        }
    }

    private void TrySetEmulatorPerformancePriority(Process process, string? mode)
    {
        try
        {
            process.PriorityClass = SchedulerPriority(mode);
            Log($"Set emulator.exe priority to {process.PriorityClass} for {NormalizeEmulationPerformanceMode(mode)} mode");
        }
        catch (Exception ex)
        {
            Log("Could not raise emulator.exe priority: " + ex.Message);
        }
    }

    private static string NormalizeEmulationPerformanceMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "power-saver" => "power-saver",
        "high-performance" => "high-performance",
        _ => "balanced"
    };

    private static ProcessPriorityClass SchedulerPriority(string? mode) => NormalizeEmulationPerformanceMode(mode) switch
    {
        "power-saver" => ProcessPriorityClass.Normal,
        "balanced" => ProcessPriorityClass.AboveNormal,
        _ => ProcessPriorityClass.High
    };

    private static string SchedulerPriorityName(string? mode) => SchedulerPriority(mode) switch
    {
        ProcessPriorityClass.Normal => "Normal",
        ProcessPriorityClass.AboveNormal => "AboveNormal",
        _ => "High"
    };

    private static bool ShouldBoostEmulatorProcessTree(string? mode) => NormalizeEmulationPerformanceMode(mode) == "high-performance";

    private void StartNetworkPolicyMonitor(string vmId, OfficialEmulatorNetworkPlan launchPlan)
    {
        CancelNetworkPolicyMonitor(vmId);
        var cts = new CancellationTokenSource();
        lock (_networkPolicyMonitorGate) _networkPolicyMonitorTokens[vmId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    var currentPlan = OfficialEmulatorNetworkIsolation.BuildPlan(LoadBackendSettings(), _baseDir);
                    var fingerprintMatches = string.Equals(currentPlan.Fingerprint, launchPlan.Fingerprint, StringComparison.Ordinal);
                    var verification = OfficialEmulatorNetworkIsolation.VerifyPlan(launchPlan);
                    if (fingerprintMatches && verification.Success) continue;

                    var reason = !fingerprintMatches
                        ? "Network settings changed while Android was running."
                        : verification.Message;
                    Log("Secure isolation runtime monitor stopped Android: " + reason);
                    StatusChanged?.Invoke("Secure isolation stopped Android: " + reason);
                    if (TryGetRunContext(vmId, out var run))
                    {
                        run.Manifest.NetworkPolicy = launchPlan.ToManifest(false);
                        run.Manifest.StartState = "network-containment-failure";
                        run.WriteManifest();
                    }
                    StopInstance(vmId);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Normal stop/restart path.
            }
            catch (Exception ex)
            {
                Log("Network policy monitor failed closed: " + ex.Message);
                StatusChanged?.Invoke("Secure isolation monitor failed; stopping Android: " + ex.Message);
                StopInstance(vmId);
            }
            finally
            {
                lock (_networkPolicyMonitorGate)
                {
                    if (_networkPolicyMonitorTokens.TryGetValue(vmId, out var current) && ReferenceEquals(current, cts))
                        _networkPolicyMonitorTokens.Remove(vmId);
                }
                cts.Dispose();
            }
        });
    }

    private void CancelNetworkPolicyMonitor(string vmId)
    {
        CancellationTokenSource? cts = null;
        lock (_networkPolicyMonitorGate)
        {
            if (_networkPolicyMonitorTokens.Remove(vmId, out var existing)) cts = existing;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
    }

    private void StartPriorityBoost(string vmId, int launcherPid, TimeSpan duration, string? mode)
    {
        CancelPriorityBoost(vmId);
        if (!ShouldBoostEmulatorProcessTree(mode)) return;
        var cts = new CancellationTokenSource();
        lock (_priorityBoostGate) _priorityBoostTokens[vmId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await BoostEmulatorProcessTreePriorityAsync(launcherPid, duration, SchedulerPriorityName(mode), cts.Token);
            }
            finally
            {
                lock (_priorityBoostGate)
                {
                    if (_priorityBoostTokens.TryGetValue(vmId, out var current) && ReferenceEquals(current, cts))
                        _priorityBoostTokens.Remove(vmId);
                }
                cts.Dispose();
            }
        });
    }

    private void CancelPriorityBoost(string vmId)
    {
        CancellationTokenSource? cts = null;
        lock (_priorityBoostGate)
        {
            if (_priorityBoostTokens.Remove(vmId, out var existing)) cts = existing;
        }
        try { cts?.Cancel(); } catch { }
    }

    private async Task BoostEmulatorProcessTreePriorityAsync(int launcherPid, TimeSpan duration, string priorityName, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + duration;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var script =
                    "$launcher=" + launcherPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" +
                    "$priority='" + priorityName + "';" +
                    "$procs=Get-CimInstance Win32_Process | Where-Object { " +
                    "($_.ProcessId -eq $launcher) -or ($_.ParentProcessId -eq $launcher) };" +
                    "foreach($p in $procs){ try { (Get-Process -Id $p.ProcessId -ErrorAction Stop).PriorityClass=$priority; Write-Output $p.ProcessId } catch {} }";
                var command = "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "`\"") + "\"";
                var changed = await RunAsync(powershell, command, 8_000);
                if (!string.IsNullOrWhiteSpace(changed))
                    Log("Raised emulator/qemu priority for PIDs: " + string.Join(",", changed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct()));
            }
            catch (Exception ex)
            {
                Log("Could not sweep emulator/qemu priority: " + ex.Message);
            }

            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> WaitForStableAdbDeviceAsync(string vmId, CancellationToken ct)
    {
        var serial = GetRequiredAdbSerial(vmId);
        for (var i = 0; i < 45; i++)
        {
            ct.ThrowIfCancellationRequested();
            var devices = await RunAsync(AdbExe, "devices", 5_000);
            if (devices.Contains(serial + "\tdevice", StringComparison.OrdinalIgnoreCase))
            {
                var probe = await RunAdbAsync(vmId, 5_000, "shell", "echo", "revm-ready");
                if (probe.Contains("revm-ready", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            await Task.Delay(1000, ct);
        }
        return false;
    }

    private bool AttachNativeEmulatorWindow(string vmId, IntPtr hostHandle, int hostW, int hostH, Process proc, TimeSpan timeout)
    {
        Status(20, "Waiting for native gfxstream emulator window");
        var hwnd = WaitForRendererWindow("Android Emulator", vmId, proc, timeout);
        if (hwnd == IntPtr.Zero)
        {
            var launcherExited = false;
            try { launcherExited = proc.HasExited; } catch { }
            var reason = launcherExited && !IsContainmentTreeActive(vmId)
                ? $"emulator process tree exited before creating its native gfxstream window ({SafeExitCode(proc)})"
                : "emulator process tree started, but its native window was not found";
            StatusChanged?.Invoke(reason + ".");
            return false;
        }

        _lastHostSizes[vmId] = (0, 0, Math.Max(1, hostW), Math.Max(1, hostH));
        _nativeWindowHosts[vmId] = (hostHandle, Math.Max(1, hostW), Math.Max(1, hostH));
        _nativeWindowEmbedded[vmId] = false;
        _embeddedWindowVisibility[vmId] = false;
        _embeddedWindows[vmId] = hwnd;
        ShowWindow(hwnd, SW_HIDE);
        WindowCreated?.Invoke(vmId, hwnd);
        Status(24, "Native gfxstream emulator window captured and hidden until Android is ready");
        return true;
    }


    private IntPtr WaitForRendererWindow(string title, string vmId, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var launcherExited = false;
            try { launcherExited = process.HasExited; } catch { launcherExited = true; }
            if (launcherExited && !IsContainmentTreeActive(vmId)) return IntPtr.Zero;
            var hwnd = FindWindowByTitleOrProcess(title, process.Id);
            if (hwnd != IntPtr.Zero) return hwnd;
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindWindowByTitleOrProcess(string title, int processId)
    {
        IntPtr candidate = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var text = GetWindowText(hwnd);
            GetWindowRect(hwnd, out var rect);
            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            if (IsStartupPopupWindow(text, width, height)) return true;
            var titleMatch = !string.IsNullOrWhiteSpace(title) &&
                             (text.Equals(title, StringComparison.OrdinalIgnoreCase) ||
                              text.Contains(title, StringComparison.OrdinalIgnoreCase));
            var processMatch = pid == (uint)processId;
            if (titleMatch || processMatch)
            {
                candidate = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return candidate;
    }

    private static bool IsStartupPopupWindow(string title, int width, int height) =>
        title.Contains("full startup", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("performing a full startup", StringComparison.OrdinalIgnoreCase) ||
        (title.Contains("Android Emulator", StringComparison.OrdinalIgnoreCase) && width is > 0 and <= 520 && height is > 0 and <= 260);

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private void EmbedWindow(string vmId, IntPtr hwnd, IntPtr hostHandle, int w, int h)
    {
        var owner = GetAncestor(hostHandle, GA_ROOT);
        if (owner == IntPtr.Zero)
            throw new InvalidOperationException("The REPlayer top-level window was unavailable for native emulator ownership.");
        Log($"Owning native emulator window vmId={vmId} hwnd=0x{hwnd.ToInt64():X} owner=0x{owner.ToInt64():X} viewport=0x{hostHandle.ToInt64():X} size={w}x{h}");

        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_CHILD | WS_VISIBLE);
        style |= WS_POPUP;
        SetWindowLong(hwnd, GWL_STYLE, style);

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, owner);

        var left = 0;
        var top = 0;
        if (GetWindowRect(hostHandle, out var viewport))
        {
            left = viewport.Left;
            top = viewport.Top;
            w = Math.Max(1, viewport.Right - viewport.Left);
            h = Math.Max(1, viewport.Bottom - viewport.Top);
        }
        SetWindowPos(hwnd, HWND_TOP,
            left - EmbeddedEmulatorOverscanPx,
            top - EmbeddedEmulatorOverscanPx,
            Math.Max(1, w + (EmbeddedEmulatorOverscanPx * 2)),
            Math.Max(1, h + (EmbeddedEmulatorOverscanPx * 2)),
            SWP_FRAMECHANGED | SWP_NOACTIVATE);
        ShowWindow(hwnd, SW_HIDE);
        HideEmulatorToolbarChildren(hwnd);
    }

    private void HideStartupPopupsForProcess(string vmId, int processId, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            if (_embeddedWindowVisibility.TryGetValue(vmId, out var visible) && visible) return;
            try
            {
                IReadOnlySet<int> descendants;
                try { descendants = OfficialEmulatorJob.GetDescendantProcessIds(processId); }
                catch { descendants = new HashSet<int>(); }
                EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hwnd)) return true;
                        GetWindowThreadProcessId(hwnd, out var pid);
                        if (!IsBundledEmulatorProcess(pid, processId, descendants)) return true;
                        if (!GetWindowRect(hwnd, out var rect)) return true;
                        var width = Math.Max(0, rect.Right - rect.Left);
                        var height = Math.Max(0, rect.Bottom - rect.Top);
                        var title = GetWindowText(hwnd);
                        var startupPopup = IsStartupPopupWindow(title, width, height);
                        var suppressedEmulatorWindow = title.Contains("Android Emulator", StringComparison.OrdinalIgnoreCase) &&
                                                       IsNativeWindowSuppressed(vmId, hwnd);
                        if (startupPopup || suppressedEmulatorWindow)
                        {
                            Log($"Hiding emulator startup window hwnd=0x{hwnd.ToInt64():X} title='{title}' size={width}x{height}");
                            ShowWindow(hwnd, SW_HIDE);
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            Thread.Sleep(75);
        }
    }

    private bool IsNativeWindowSuppressed(string vmId, IntPtr hwnd)
    {
        if (!_embeddedWindows.TryGetValue(vmId, out var registered) || registered != hwnd) return true;
        if (!_embeddedWindowVisibility.TryGetValue(vmId, out var visible) || !visible) return true;
        return !_nativeWindowEmbedded.TryGetValue(vmId, out var embedded) || !embedded;
    }

    private bool IsBundledEmulatorProcess(uint processId, int launcherProcessId, IReadOnlySet<int> descendants)
    {
        int candidateProcessId;
        try { candidateProcessId = checked((int)processId); }
        catch { return false; }
        if (candidateProcessId != launcherProcessId && !descendants.Contains(candidateProcessId)) return false;
        try
        {
            using var process = Process.GetProcessById(candidateProcessId);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return false;
            var fullPath = Path.GetFullPath(path);
            var bundledRoot = Path.GetFullPath(ActiveEmulatorDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(bundledRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void HideEmulatorToolbarChildren(IntPtr emulatorRoot)
    {
        try
        {
            bool MaybeHideChrome(IntPtr child)
            {
                try
                {
                    if (child == IntPtr.Zero || child == emulatorRoot || !IsWindowVisible(child)) return true;
                    if (!GetWindowRect(child, out var rect)) return true;
                    var width = Math.Max(0, rect.Right - rect.Left);
                    var height = Math.Max(0, rect.Bottom - rect.Top);
                    var title = GetWindowText(child);
                    var parent = GetParent(child);
                    var looksLikeToolbar = width > 0 && width <= EmulatorToolbarMaxWidthPx && height >= EmulatorToolbarMinHeightPx;
                    var looksLikeStartupPopup = IsStartupPopupWindow(title, width, height);
                    var belongsToRoot = parent == emulatorRoot;
                    if ((looksLikeToolbar && (belongsToRoot || title.Contains("Emulator", StringComparison.OrdinalIgnoreCase))) || looksLikeStartupPopup)
                    {
                        Log($"Hiding emulator toolbar/popup hwnd=0x{child.ToInt64():X} parent=0x{parent.ToInt64():X} title='{title}' size={width}x{height}");
                        ShowWindow(child, SW_HIDE);
                    }
                }
                catch { }
                return true;
            }

            EnumChildWindows(emulatorRoot, (child, _) => MaybeHideChrome(child), IntPtr.Zero);
            EnumWindows((child, _) => MaybeHideChrome(child), IntPtr.Zero);
        }
        catch { }
    }


    private string SystemImageDirectoryForProfile(string? bootProfile) => NormalizeBootProfile(bootProfile) switch
    {
        "play-compat" => _playStoreSystemImageDir,
        "patched-pixel-utility" => _patchedPixelSystemImageDir,
        _ => _systemImageDir
    };

    private string BaselineAvdDirectoryForProfile(string? bootProfile) => NormalizeBootProfile(bootProfile) switch
    {
        "api34-resizable" => _resizableAvdDir,
        _ => _avdDir
    };

    private bool TryGetRunContext(string vmId, out OfficialEmulatorRunContext run)
    {
        lock (_runGate) return _runContexts.TryGetValue(vmId, out run!);
    }

    private OfficialEmulatorRunContext GetRequiredRunContext(string vmId) =>
        TryGetRunContext(vmId, out var run)
            ? run
            : throw new InvalidOperationException("No active official-emulator run exists for " + vmId + ".");

    private string GetRequiredAdbSerial(string vmId) => GetRequiredRunContext(vmId).AdbSerial;

    private Task<string> RunAdbAsync(string vmId, int timeoutMs, params string[] arguments)
    {
        var run = GetRequiredRunContext(vmId);
        var adbArguments = new List<string> { "-s", run.AdbSerial };
        adbArguments.AddRange(arguments);
        return RunAsync(AdbExe, adbArguments, timeoutMs, run.RunAvdHome);
    }

    private void HandleEmulatorExited(string vmId, Process process)
    {
        var exitCode = SafeExitCode(process);
        Log($"emulator.exe launcher exited ({exitCode}); containment monitor is following the trusted child process tree.");
        if (TryGetContainmentJob(vmId, out _)) return;
        CompleteContainmentJob(vmId, null, $"emulator process tree exited after launcher code {exitCode}");
    }

    private async Task MonitorContainmentJobAsync(string vmId, OfficialEmulatorJob job, int launcherProcessId)
    {
        var trustedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(EmulatorExe),
            Path.GetFullPath(Path.Combine(ActiveEmulatorDir, "qemu", "windows-x86_64", "qemu-system-x86_64.exe")),
            Path.GetFullPath(Path.Combine(ActiveEmulatorDir, "qemu", "windows-x86_64", "qemu-system-x86_64-headless.exe"))
        };
        DateTime? emptySinceUtc = null;
        var consecutiveMonitorErrors = 0;
        while (IsCurrentContainmentJob(vmId, job))
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (!IsCurrentContainmentJob(vmId, job)) return;
            try
            {
                var attached = job.AttachTrustedDescendants(launcherProcessId, trustedPaths);
                consecutiveMonitorErrors = 0;
                if (attached > 0) Log($"Attached {attached} trusted emulator child process(es) to containment.");
                if (job.HasActiveProcesses)
                {
                    emptySinceUtc = null;
                    continue;
                }

                emptySinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - emptySinceUtc < TimeSpan.FromSeconds(5)) continue;
                CompleteContainmentJob(vmId, null, "emulator process tree exited");
                return;
            }
            catch (Exception ex)
            {
                consecutiveMonitorErrors++;
                if (consecutiveMonitorErrors <= 30)
                {
                    if (consecutiveMonitorErrors is 1 or 10 or 20 or 30)
                        Log($"Transient containment descendant scan failure ({consecutiveMonitorErrors}/30): {ex.Message}");
                    continue;
                }
                CompleteContainmentJob(vmId, "containment-failure", "Could not monitor/attach emulator process tree: " + ex.Message);
                return;
            }
        }
    }

    private bool TryGetContainmentJob(string vmId, out OfficialEmulatorJob job)
    {
        lock (_runGate) return _containmentJobs.TryGetValue(vmId, out job!);
    }

    private bool IsContainmentTreeActive(string vmId)
    {
        try { return TryGetContainmentJob(vmId, out var job) && job.HasActiveProcesses; }
        catch { return false; }
    }

    private bool IsCurrentContainmentJob(string vmId, OfficialEmulatorJob job)
    {
        lock (_runGate) return _containmentJobs.TryGetValue(vmId, out var current) && ReferenceEquals(current, job);
    }

    private void CompleteContainmentJob(string vmId, string? forcedState, string detail)
    {
        lock (_instanceStateGate) _startingInstances.Remove(vmId);
        if (_processes.Remove(vmId, out var process))
        {
            try { process.Dispose(); } catch { }
        }
        DisposeContainmentJob(vmId);
        var requested = TryGetRunContext(vmId, out var run) && run.StopRequested;
        var state = forcedState ?? (requested ? "completed" : "aborted");
        FinalizeRun(vmId, state, requested && forcedState == null ? null : detail);
    }

    private void DisposeContainmentJob(string vmId)
    {
        OfficialEmulatorJob? job = null;
        lock (_runGate)
        {
            if (_containmentJobs.Remove(vmId, out var existing)) job = existing;
        }
        try { job?.Dispose(); }
        catch (Exception ex) { Log("Could not close emulator containment job: " + ex.Message); }
    }

    private void FinalizeRun(string vmId, string requestedState, string? failure)
    {
        if (!TryGetRunContext(vmId, out var run) || !run.TryBeginFinalization()) return;

        try
        {
            run.Manifest.BaselineHashesAfter = OfficialEmulatorBaseline.ComputeHashes(
                run.BaselineAvdDirectory,
                run.SystemImageDirectory,
                Path.Combine(_runtimeDir, "baseline-integrity"));
            run.Manifest.BaselineHashMatch = HashesEqual(run.Manifest.BaselineHashesBefore, run.Manifest.BaselineHashesAfter);
            if (run.Manifest.BaselineHashMatch != true)
            {
                requestedState = "containment-failure";
                failure = "Immutable emulator baseline hash changed during the run.";
            }
        }
        catch (Exception ex)
        {
            requestedState = "containment-failure";
            failure = "Could not verify immutable emulator baseline after the run: " + ex.Message;
            run.Manifest.BaselineHashMatch = false;
        }
        finally
        {
            try { run.BaselineReadLease?.Dispose(); } catch (Exception ex) { Log("Could not release baseline read lease: " + ex.Message); }
            run.BaselineReadLease = null;
        }

        try
        {
            TryDeleteDirectory(run.RunAvdHome);
            run.Manifest.RunStorageDisposed = !Directory.Exists(run.RunAvdHome);
            if (!run.Manifest.RunStorageDisposed)
            {
                requestedState = "containment-failure";
                failure = "Disposable run storage could not be removed.";
            }
        }
        catch (Exception ex)
        {
            requestedState = "containment-failure";
            failure = "Disposable run storage cleanup failed: " + ex.Message;
            run.Manifest.RunStorageDisposed = false;
        }

        run.Manifest.EndedAtUtc = DateTime.UtcNow;
        run.Manifest.EndState = requestedState;
        run.Manifest.Failure = string.IsNullOrWhiteSpace(failure) ? null : failure;
        try { run.WriteManifest(); }
        catch (Exception ex) { Log("Could not finalize case manifest: " + ex.Message); }
        OfficialEmulatorPortAllocator.Release(run.ConsolePort);

        if (string.Equals(requestedState, "containment-failure", StringComparison.OrdinalIgnoreCase))
        {
            Log("Containment failure for " + run.CaseId + ": " + failure);
            StatusChanged?.Invoke("Containment failure: " + failure);
        }
        else
        {
            Log($"Case {run.CaseId} {requestedState}; manifest={run.ManifestPath}");
        }
    }

    private static bool HashesEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase));

    private static string QuoteRemoteShellScript(string script) =>
        "'" + script.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string QuoteLogArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;

    private Task<string> RunAsync(string exe, IReadOnlyList<string> arguments, int timeoutMs, string? avdHome = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe) ?? _baseDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        psi.Environment["ANDROID_SDK_ROOT"] = _sdkRoot;
        psi.Environment["ANDROID_HOME"] = _sdkRoot;
        psi.Environment["ANDROID_AVD_HOME"] = string.IsNullOrWhiteSpace(avdHome) ? _avdHome : avdHome;
        return RunProcessAsync(psi, timeoutMs);
    }

    private async Task<string> RunProcessAsync(ProcessStartInfo psi, int timeoutMs)
    {
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + psi.FileName);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(Path.GetFileName(psi.FileName) + " timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            Log(Path.GetFileName(psi.FileName) + " stderr: " + stderr.Trim());
        return stdout + stderr;
    }

    private async Task<string> RunAsync(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = Path.GetDirectoryName(exe) ?? _baseDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["ANDROID_SDK_ROOT"] = _sdkRoot;
        psi.Environment["ANDROID_HOME"] = _sdkRoot;
        psi.Environment["ANDROID_AVD_HOME"] = _avdHome;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + exe);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var exitedTask = proc.WaitForExitAsync();
        var completed = await Task.WhenAny(exitedTask, Task.Delay(timeoutMs));
        if (completed != exitedTask)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(Path.GetFileName(exe) + " timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr)) Log(Path.GetFileName(exe) + " stderr: " + stderr.Trim());
        return stdout + stderr;
    }

    private static void DirectoryCopy(string sourceDir, string destDir, bool overwrite)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            DirectoryCopy(dir, Path.Combine(destDir, Path.GetFileName(dir)), overwrite);
    }

    private void BootDebug(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Log("boot.debug: " + message);
        StatusChanged?.Invoke("boot-debug:|" + message);
    }

    private void Status(int pct, string msg)
    {
        Log($"{pct}% {msg}");
        StatusChanged?.Invoke($"progress:|{pct}|{msg}");
    }

    private static void TryDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 23)
            {
                Thread.Sleep(125);
            }
            catch (UnauthorizedAccessException) when (attempt < 23)
            {
                Thread.Sleep(125);
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        try
        {
            var tombstone = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".deleted-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Directory.Move(dir, tombstone);
        }
        catch { }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private void SaveInstanceConfig(GoogleInstanceConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstanceConfigPath(cfg.Id))!);
        File.WriteAllText(InstanceConfigPath(cfg.Id), JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int GetSafeMaxRamMb()
    {
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                var availCommitMb = (long)(mem.ullAvailPageFile / 1024UL / 1024UL);
                var safe = (int)Math.Max(1024, Math.Min(524288, availCommitMb - 2048));
                return safe;
            }
        }
        catch { }
        return 8192;
    }

    private static string TailFile(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            return string.Join(" | ", File.ReadLines(path).TakeLast(lines));
        }
        catch { return string.Empty; }
    }

    private static string SummarizeLaunchFailure(string text)
    {
        if (text.Contains("Insufficient RAM", StringComparison.OrdinalIgnoreCase))
            return "not enough host RAM/commit for the selected instance RAM allocation";
        if (text.Contains("PANIC", StringComparison.OrdinalIgnoreCase))
            return "emulator panic during startup";
        if (text.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            return text.Split("ERROR:").Last().Split('|').First().Trim();
        return string.IsNullOrWhiteSpace(text) ? "emulator exited during startup" : text;
    }

    private void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(_logsDir);
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    private static void Append(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + "\r\n");
        }
        catch { }
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -999; } }

    private static string SanitizePropValue(string? value, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_').ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static string EscapePath(string path) => path.Replace("\\", "\\\\");
}


internal sealed class GoogleInstanceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Android";
    public int CpuCount { get; set; } = 2;
    public int RamMB { get; set; } = 2048;
    public int StorageGB { get; set; } = 8;
    public string BootProfile { get; set; } = "api34-persona";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
