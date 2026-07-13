using System;
using System.IO;
using System.Text.Json;

namespace ReVM;

public static class RuntimeBackendFactory
{
    public static IAndroidRuntimeBackend Create()
    {
        var baseDir = GetBaseDir();
        var settingsPath = Path.Combine(baseDir, "runtime", "backend-settings.json");
        var settings = LoadSettings(settingsPath);

        if (string.Equals(settings.PreferredBackend, "ldplayer", StringComparison.OrdinalIgnoreCase) &&
            settings.AllowLdPlayerBridge)
        {
            return new LdPlayerEngineService();
        }

        if (string.Equals(settings.PreferredBackend, "google-emulator", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.PreferredBackend, "official-emulator", StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleEmulatorRuntimeService();
        }

        // Product default: native REPlayer-owned runtime. If missing, setup explains what
        // to install/build instead of silently depending on LDPlayer.
        return new RevmNativeRuntimeService();
    }

    public static string GetBaseDir() => RevmPaths.BaseDir;

    private static BackendSettings LoadSettings(string path)
    {
        try
        {
            if (!File.Exists(path)) return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
            var settings = JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BackendSettings();
            return BackendSettingsValidation.ValidateAndNormalize(settings);
        }
        catch
        {
            return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
        }
    }
}

public sealed class BackendSettings
{
    public string PreferredBackend { get; set; } = "official-emulator";
    public bool AllowLdPlayerBridge { get; set; } = false;
    public string RendererApi { get; set; } = "host";
    public string GpuMode { get; set; } = "host";
    public string SystemUiRenderer { get; set; } = "";
    public string EmulationPerformanceMode { get; set; } = "balanced";
    public bool VsyncEnabled { get; set; } = true;
    public bool FreesyncEnabled { get; set; } = false;
    public bool LeanGuestEnabled { get; set; } = false;
    public bool PreferDiscreteGpu { get; set; } = false;
    public string BootProfile { get; set; } = "api34-persona";

    // Display / window
    public string ResolutionMode { get; set; } = "viewport";
    public int DisplayWidth { get; set; } = 1280;
    public int DisplayHeight { get; set; } = 720;
    public int DisplayDpi { get; set; } = 240;
    public bool AutoRotate { get; set; } = true;
    public bool LockWindowSize { get; set; } = true;
    public int Fps { get; set; } = 60;

    // VM / emulator launch profile. Restart required.
    public int CpuCores { get; set; } = 3;
    public int RamMb { get; set; } = 3072;
    public int StorageGb { get; set; } = 8;
    public string Acceleration { get; set; } = "auto";
    public string RuntimePath { get; set; } = "runtime/";

    // Device identity / Phase 3 reproducible persona. The seed is used only
    // as deterministic input and is never written into case evidence.
    public string DeviceModel { get; set; } = "REPlayer Virtual Device";
    public string DeviceMaker { get; set; } = "REPlayer";
    public string AndroidIdMode { get; set; } = "Stable per instance";
    public bool PersonaEnabled { get; set; } = true;
    public bool PersonaFailClosed { get; set; } = true;
    public string PersonaMode { get; set; } = "stable-instance";
    public string PersonaSeed { get; set; } = "";
    public string PersonaLocale { get; set; } = "en-US";
    public string PersonaTimezone { get; set; } = "America/New_York";
    public string PersonaCountryCode { get; set; } = "US";
    public bool PhoneProfile { get; set; } = true;
    // Legacy settings compatibility only. Validation always forces this false;
    // api34-resizable derives root capability from its immutable baseline.
    public bool AdbRoot { get; set; } = false;
    public bool ProvisionUtilityProfile { get; set; } = true;

    // Hardware/features
    public bool FastDisk { get; set; } = true;
    public bool ReadOnlyBaseImage { get; set; } = true;
    public bool OfficialEmulatorDisposableMode { get; set; } = true;
    public bool SpeakerOutput { get; set; } = false;
    public bool MicrophoneInput { get; set; } = false;
    public int AudioVolumePercent { get; set; } = 70;
    public bool AudioMuted { get; set; } = false;
    public bool NatNetworking { get; set; } = true;

    // Network containment. Disabled by default so first launch stays non-elevated;
    // users explicitly opt in to host firewall enforcement from Settings.
    public bool SecureIsolationEnabled { get; set; } = false;
    public bool NetworkIsolationEnabled { get; set; } = false;
    public string NetworkIsolationMode { get; set; } = "internet-only";
    public bool NetworkBlockHostServices { get; set; } = true;
    public bool NetworkBlockPrivateNetworks { get; set; } = true;
    public bool NetworkBlockLinkLocal { get; set; } = true;
    public bool NetworkBlockMulticast { get; set; } = true;
    public bool NetworkUseSafeDns { get; set; } = true;
    public string NetworkDnsServers { get; set; } = "1.1.1.1,9.9.9.9";
    public bool NetworkAllowHostProxy { get; set; } = false;
    public int NetworkHostProxyPort { get; set; } = 8080;

    public int AdbPort { get; set; } = 5555;
    public int FridaPort { get; set; } = 27042;
    public int MitmProxyPort { get; set; } = 8080;

    // Input/UI behavior
    public bool LowLatencyInput { get; set; } = true;
    public bool AutoStartAndroid { get; set; } = true;
    public bool MiniModeTopMost { get; set; } = true;
    public bool CompactRightToolbar { get; set; } = true;
    public bool ColdBootLauncherEnabled { get; set; } = true;
    public string BackHotkey { get; set; } = "Esc";
    public string HomeHotkey { get; set; } = "Home";
    public string AppSwitcherHotkey { get; set; } = "Ctrl+Tab";
    public string FullscreenHotkey { get; set; } = "F11";
}
