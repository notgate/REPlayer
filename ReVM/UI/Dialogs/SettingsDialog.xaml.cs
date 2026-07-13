using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace ReVM;

public partial class SettingsDialog : Window
{
    private readonly string _settingsPath;
    private bool _loadingSettings;
    public event Func<Task>? AppliedAsync;
    public Func<Task<bool>>? PrepareNetworkPolicyChangeAsync { get; set; }

    public SettingsDialog()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(RuntimeBackendFactory.GetBaseDir(), "runtime", "backend-settings.json");
        PopulatePersonaChoices();
        _loadingSettings = true;
        LoadBackendSettings();
        _loadingSettings = false;
        foreach (var rb in new[] { Res1920x1080, Res1600x900, Res1280x720, Res960x540, Res1080x1920, Res900x1600, Res720x1280, Res540x960 })
            rb.Checked += (_, _) => SyncResolutionTextBoxesFromPreset();
        ResModeLandscape.Checked += (_, _) => SyncResolutionTextBoxesFromPreset();
        ResModePortrait.Checked += (_, _) => SyncResolutionTextBoxesFromPreset();
        ResModeLarge.Checked += (_, _) => SyncResolutionTextBoxesFromPreset();
        NetworkIsolationModeCombo.SelectionChanged += (_, _) => UpdateNetworkPolicyUi();
        NetworkIsolationToggle.Checked += (_, _) => UpdateNetworkPolicyUi();
        NetworkIsolationToggle.Unchecked += (_, _) => UpdateNetworkPolicyUi();
        SafeDnsToggle.Checked += (_, _) => UpdateNetworkPolicyUi();
        SafeDnsToggle.Unchecked += (_, _) => UpdateNetworkPolicyUi();
        AllowHostProxyToggle.Checked += (_, _) => UpdateNetworkPolicyUi();
        AllowHostProxyToggle.Unchecked += (_, _) => UpdateNetworkPolicyUi();
        EmulationPerformanceModeCombo.SelectionChanged += (_, _) => SyncFpsFromPerformanceProfile();
        UpdateCaseModeUi();
        UpdateAudioUi();
        UpdateNetworkPolicyUi();
        UpdateNetworkPolicyStatus();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender == DisplayNav) SettingsTabs.SelectedIndex = 0;
        else if (sender == AdvancedNav) SettingsTabs.SelectedIndex = 1;
        else if (sender == DeviceNav) SettingsTabs.SelectedIndex = 2;
        else if (sender == DiskNav) SettingsTabs.SelectedIndex = 3;
        else if (sender == AudioNav) SettingsTabs.SelectedIndex = 4;
        else if (sender == NetworkNav) SettingsTabs.SelectedIndex = 5;
        else if (sender == HotkeysNav) SettingsTabs.SelectedIndex = 6;
        else if (sender == RenderNav) SettingsTabs.SelectedIndex = 7;
        else if (sender == OtherNav) SettingsTabs.SelectedIndex = 8;
    }

    private void BrowseRuntime_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select REPlayer Runtime Directory" };
        if (dialog.ShowDialog() == true)
            RuntimePath.Text = dialog.FolderName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildBackendSettings(out var settings)) return;
        if (!await CommitSettingsAndEnsureNetworkPolicyAsync(settings)) return;
        DialogResult = true;
        Close();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var applyButton = sender as Button;
        if (applyButton is not null) applyButton.IsEnabled = false;
        try
        {
            if (!TryBuildBackendSettings(out var settings)) return;
            if (!await CommitSettingsAndEnsureNetworkPolicyAsync(settings)) return;
            if (AppliedAsync is not null) await AppliedAsync();
        }
        finally
        {
            if (applyButton is not null) applyButton.IsEnabled = true;
        }
    }

    private async void ApplyNetworkPolicy_Click(object sender, RoutedEventArgs e)
    {
        ApplyNetworkPolicyButton.IsEnabled = false;
        try
        {
            if (!TryBuildBackendSettings(out var settings)) return;
            if (await CommitSettingsAndEnsureNetworkPolicyAsync(settings) && AppliedAsync is not null)
                await AppliedAsync();
        }
        finally
        {
            ApplyNetworkPolicyButton.IsEnabled = true;
        }
    }

    private void LoadBackendSettings()
    {
        var settings = ReadBackendSettings();

        SelectResolution(settings.DisplayWidth, settings.DisplayHeight, settings.DisplayDpi, settings.ResolutionMode);
        ResolutionWidthBox.Text = settings.DisplayWidth.ToString(CultureInfo.InvariantCulture);
        ResolutionHeightBox.Text = settings.DisplayHeight.ToString(CultureInfo.InvariantCulture);
        AutoRotateToggle.IsChecked = settings.AutoRotate;
        LockWindowToggle.IsChecked = settings.LockWindowSize;
        SelectByTag(AccelCombo, settings.Acceleration);
        RuntimePath.Text = string.IsNullOrWhiteSpace(settings.RuntimePath) ? "runtime/" : settings.RuntimePath;

        DeviceModelBox.Text = settings.DeviceModel;
        DeviceMakerBox.Text = settings.DeviceMaker;
        PersonaEnabledToggle.IsChecked = settings.PersonaEnabled;
        SelectByTag(PersonaModeCombo, settings.PersonaMode);
        PersonaSeedBox.Text = settings.PersonaSeed;
        SelectByTag(PersonaLocaleCombo, settings.PersonaLocale);
        SelectByTag(PersonaTimezoneCombo, settings.PersonaTimezone);
        SelectByTag(PersonaCountryCombo, settings.PersonaCountryCode);

        StorageSlider.Value = Math.Clamp(settings.StorageGb, 8, 128);
        FastDiskToggle.IsChecked = settings.FastDisk;
        ReadOnlyBaseToggle.IsChecked = settings.ReadOnlyBaseImage;
        DisposableCaseRadio.IsChecked = settings.OfficialEmulatorDisposableMode;
        WritableCaseRadio.IsChecked = !settings.OfficialEmulatorDisposableMode;

        SpeakerToggle.IsChecked = settings.SpeakerOutput;
        AudioMuteToggle.IsChecked = settings.AudioMuted;
        AudioVolumeSlider.Value = settings.AudioVolumePercent;
        MicToggle.IsChecked = settings.MicrophoneInput;

        AdbPort.Text = settings.AdbPort.ToString(CultureInfo.InvariantCulture);
        FridaPortBox.Text = settings.FridaPort.ToString(CultureInfo.InvariantCulture);
        MitmProxyPortBox.Text = settings.MitmProxyPort.ToString(CultureInfo.InvariantCulture);
        NatToggle.IsChecked = settings.NatNetworking;
        SecureIsolationToggle.IsChecked = settings.SecureIsolationEnabled;
        NetworkIsolationToggle.IsChecked = settings.NetworkIsolationEnabled;
        SelectByTag(NetworkIsolationModeCombo, OfficialEmulatorNetworkIsolation.NormalizeMode(settings.NetworkIsolationMode));
        BlockHostServicesToggle.IsChecked = settings.NetworkBlockHostServices;
        BlockPrivateNetworksToggle.IsChecked = settings.NetworkBlockPrivateNetworks;
        BlockLinkLocalToggle.IsChecked = settings.NetworkBlockLinkLocal;
        BlockMulticastToggle.IsChecked = settings.NetworkBlockMulticast;
        SafeDnsToggle.IsChecked = settings.NetworkUseSafeDns;
        DnsServersBox.Text = settings.NetworkDnsServers;
        AllowHostProxyToggle.IsChecked = settings.NetworkAllowHostProxy;
        HostProxyPortBox.Text = settings.NetworkHostProxyPort.ToString(CultureInfo.InvariantCulture);

        BackHotkeyBox.Text = settings.BackHotkey;
        HomeHotkeyBox.Text = settings.HomeHotkey;
        AppSwitcherHotkeyBox.Text = settings.AppSwitcherHotkey;
        FullscreenHotkeyBox.Text = settings.FullscreenHotkey;

        SelectByTag(BootProfileCombo, NormalizeBootProfile(settings.BootProfile));
        SelectByTag(RendererEngineCombo, RendererEngineFromSettings(settings));
        SelectByTag(EmulationPerformanceModeCombo, NormalizeEmulationPerformanceMode(settings.EmulationPerformanceMode));
        SelectByTag(FpsCombo, settings.Fps.ToString(CultureInfo.InvariantCulture));
        VsyncToggle.IsChecked = settings.VsyncEnabled;
        FreesyncToggle.IsChecked = settings.FreesyncEnabled;
        LeanGuestToggle.IsChecked = settings.LeanGuestEnabled;
        PreferDiscreteGpuToggle.IsChecked = settings.PreferDiscreteGpu;
        LowLatencyInputToggle.IsChecked = settings.LowLatencyInput;

        AutoStartToggle.IsChecked = settings.AutoStartAndroid;
        MiniTopMostToggle.IsChecked = settings.MiniModeTopMost;
        CompactToolbarToggle.IsChecked = settings.CompactRightToolbar;
        ColdBootLauncherToggle.IsChecked = settings.ColdBootLauncherEnabled;
    }

    private BackendSettings ReadBackendSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
            var settings = JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(_settingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BackendSettings();
            return BackendSettingsValidation.ValidateAndNormalize(settings);
        }
        catch
        {
            return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
        }
    }

    private BackendSettings BuildBackendSettings()
    {
        var settings = ReadBackendSettings();

        settings.PreferredBackend = NormalizePreferredBackend(settings.PreferredBackend);
        settings.AllowLdPlayerBridge = false;
        ApplyRendererEngine(settings, SelectedTag(RendererEngineCombo, "opengl"));
        settings.EmulationPerformanceMode = NormalizeEmulationPerformanceMode(SelectedTag(EmulationPerformanceModeCombo, "balanced"));
        settings.VsyncEnabled = IsOn(VsyncToggle);
        settings.FreesyncEnabled = IsOn(FreesyncToggle);
        settings.LeanGuestEnabled = IsOn(LeanGuestToggle);
        settings.PreferDiscreteGpu = IsOn(PreferDiscreteGpuToggle);
        settings.BootProfile = SelectedTag(BootProfileCombo, "api34-persona");
        settings.AdbRoot = false;
        settings.ProvisionUtilityProfile = settings.BootProfile is "api34-persona" or "api34-resizable";
        settings.PhoneProfile = true;

        var preset = SelectedResolutionPreset();
        var mode = SelectedResolutionMode();
        var customResolution = IsOn(ResModeCustom);
        if (customResolution)
        {
            settings.DisplayWidth = ParseRequiredInt(ResolutionWidthBox.Text, "Display width");
            settings.DisplayHeight = ParseRequiredInt(ResolutionHeightBox.Text, "Display height");
        }
        else
        {
            settings.DisplayWidth = preset.width;
            settings.DisplayHeight = preset.height;
        }
        settings.DisplayDpi = preset.dpi;

        // "Custom" is a size source, not an Android orientation. Persist only
        // portrait/landscape so the window sizing, -skin launch args, and restart
        // comparison all agree. For custom sizes, infer orientation from W/H.
        settings.ResolutionMode = customResolution
            ? (settings.DisplayHeight > settings.DisplayWidth ? "portrait" : "landscape")
            : (string.Equals(mode, "portrait", StringComparison.OrdinalIgnoreCase) ? "portrait" : "landscape");
        if (string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase) && settings.DisplayWidth > settings.DisplayHeight)
            (settings.DisplayWidth, settings.DisplayHeight) = (settings.DisplayHeight, settings.DisplayWidth);
        else if (!string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase) && settings.DisplayHeight > settings.DisplayWidth)
            (settings.DisplayWidth, settings.DisplayHeight) = (settings.DisplayHeight, settings.DisplayWidth);
        settings.AutoRotate = IsOn(AutoRotateToggle);
        settings.LockWindowSize = IsOn(LockWindowToggle);
        settings.Acceleration = SelectedTag(AccelCombo, "auto");
        settings.RuntimePath = RuntimePath.Text.Trim();

        settings.DeviceModel = DeviceModelBox.Text.Trim();
        settings.DeviceMaker = DeviceMakerBox.Text.Trim();
        settings.PersonaEnabled = IsOn(PersonaEnabledToggle);
        settings.PersonaFailClosed = settings.PersonaEnabled;
        settings.PersonaMode = SelectedTag(PersonaModeCombo, "stable-instance");
        settings.AndroidIdMode = settings.PersonaMode == "rotate-case" ? "Rotate per case" : "Stable per instance";
        settings.PersonaSeed = PersonaSeedBox.Text.Trim();
        settings.PersonaLocale = SelectedTag(PersonaLocaleCombo, "en-US");
        settings.PersonaTimezone = SelectedTag(PersonaTimezoneCombo, "America/New_York");
        settings.PersonaCountryCode = SelectedTag(PersonaCountryCombo, "US");

        settings.StorageGb = ClampInt((int)Math.Round(StorageSlider.Value), 8, 128);
        settings.FastDisk = IsOn(FastDiskToggle);
        settings.OfficialEmulatorDisposableMode = IsOn(DisposableCaseRadio);
        settings.ReadOnlyBaseImage = settings.OfficialEmulatorDisposableMode || IsOn(ReadOnlyBaseToggle);
        settings.SpeakerOutput = IsOn(SpeakerToggle);
        settings.AudioMuted = IsOn(AudioMuteToggle);
        settings.AudioVolumePercent = (int)Math.Round(AudioVolumeSlider.Value);
        settings.MicrophoneInput = IsOn(MicToggle);
        settings.NatNetworking = IsOn(NatToggle);
        settings.SecureIsolationEnabled = IsOn(SecureIsolationToggle);
        settings.NetworkIsolationEnabled = IsOn(NetworkIsolationToggle);
        settings.NetworkIsolationMode = OfficialEmulatorNetworkIsolation.NormalizeMode(SelectedTag(NetworkIsolationModeCombo, "internet-only"));
        settings.NetworkBlockHostServices = IsOn(BlockHostServicesToggle);
        settings.NetworkBlockPrivateNetworks = IsOn(BlockPrivateNetworksToggle);
        settings.NetworkBlockLinkLocal = IsOn(BlockLinkLocalToggle);
        settings.NetworkBlockMulticast = IsOn(BlockMulticastToggle);
        settings.NetworkUseSafeDns = IsOn(SafeDnsToggle);
        settings.NetworkDnsServers = DnsServersBox.Text.Trim();
        settings.NetworkAllowHostProxy = IsOn(AllowHostProxyToggle);
        settings.NetworkHostProxyPort = ParseRequiredInt(HostProxyPortBox.Text, "Host proxy port");
        settings.AdbPort = ParseRequiredInt(AdbPort.Text, "ADB port");
        settings.FridaPort = ParseRequiredInt(FridaPortBox.Text, "Frida port");
        settings.MitmProxyPort = ParseRequiredInt(MitmProxyPortBox.Text, "mitmproxy port");

        settings.BackHotkey = BackHotkeyBox.Text.Trim();
        settings.HomeHotkey = HomeHotkeyBox.Text.Trim();
        settings.AppSwitcherHotkey = AppSwitcherHotkeyBox.Text.Trim();
        settings.FullscreenHotkey = FullscreenHotkeyBox.Text.Trim();
        settings.LowLatencyInput = IsOn(LowLatencyInputToggle);
        ApplyEmulationPerformanceMode(settings);
        settings.Fps = ParseRequiredInt(SelectedTag(FpsCombo, "60"), "Frame rate");
        settings.AutoStartAndroid = IsOn(AutoStartToggle);
        settings.MiniModeTopMost = IsOn(MiniTopMostToggle);
        settings.CompactRightToolbar = IsOn(CompactToolbarToggle);
        settings.ColdBootLauncherEnabled = IsOn(ColdBootLauncherToggle);

        return BackendSettingsValidation.ValidateAndNormalize(settings);
    }

    private bool TryBuildBackendSettings(out BackendSettings settings)
    {
        try
        {
            settings = BuildBackendSettings();
            return true;
        }
        catch (Exception ex)
        {
            settings = new BackendSettings();
            MessageBox.Show(this, ex.Message, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void PopulatePersonaChoices()
    {
        PersonaLocaleCombo.Items.Clear();
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                     .Where(culture => !string.IsNullOrWhiteSpace(culture.Name))
                     .GroupBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderBy(culture => culture.EnglishName, StringComparer.OrdinalIgnoreCase))
        {
            PersonaLocaleCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{culture.EnglishName} ({culture.Name})",
                Tag = culture.Name
            });
        }

        PersonaTimezoneCombo.Items.Clear();
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var id = zone.Id;
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) && !string.IsNullOrWhiteSpace(ianaId))
                id = ianaId;
            if (PersonaTimezoneCombo.Items.OfType<ComboBoxItem>().Any(item =>
                    string.Equals(item.Tag?.ToString(), id, StringComparison.OrdinalIgnoreCase)))
                continue;
            PersonaTimezoneCombo.Items.Add(new ComboBoxItem { Content = zone.DisplayName, Tag = id });
        }

        PersonaCountryCombo.Items.Clear();
        var regions = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(culture =>
            {
                try { return new RegionInfo(culture.Name); }
                catch { return null; }
            })
            .Where(region => region is not null)
            .Cast<RegionInfo>()
            .GroupBy(region => region.TwoLetterISORegionName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(region => region.EnglishName, StringComparer.OrdinalIgnoreCase);
        foreach (var region in regions)
            PersonaCountryCombo.Items.Add(new ComboBoxItem { Content = region.EnglishName, Tag = region.TwoLetterISORegionName });
    }

    private void CaseMode_Changed(object sender, RoutedEventArgs e) => UpdateCaseModeUi();

    private void UpdateCaseModeUi()
    {
        if (ReadOnlyBaseToggle is null || DisposableCaseRadio is null) return;
        if (IsOn(DisposableCaseRadio)) ReadOnlyBaseToggle.IsChecked = true;
        ReadOnlyBaseToggle.IsEnabled = !IsOn(DisposableCaseRadio);
    }

    private void AudioState_Changed(object sender, RoutedEventArgs e) => UpdateAudioUi();

    private void UpdateAudioUi()
    {
        if (SpeakerToggle is null || AudioMuteToggle is null || AudioVolumeSlider is null) return;
        var enabled = IsOn(SpeakerToggle);
        AudioMuteToggle.IsEnabled = enabled;
        AudioVolumeSlider.IsEnabled = enabled && !IsOn(AudioMuteToggle);
    }

    private void SyncFpsFromPerformanceProfile()
    {
        if (_loadingSettings || FpsCombo is null) return;
        var fps = NormalizeEmulationPerformanceMode(SelectedTag(EmulationPerformanceModeCombo, "balanced")) switch
        {
            "power-saver" => "30",
            // 240 Hz is not a coherent mode on the API 34 resizable image
            // (the emulator advertises 160 Hz and spends compositor budget
            // without delivering 240 Hz). Keep the measured 60 Hz baseline;
            // frame rate remains independently selectable by the user.
            "high-performance" => "60",
            _ => "60"
        };
        SelectByTag(FpsCombo, fps);
    }

    private async Task<bool> CommitSettingsAndEnsureNetworkPolicyAsync(BackendSettings settings)
    {
        var baseDir = RuntimeBackendFactory.GetBaseDir();
        var previousSettings = ReadBackendSettings();
        var hostPolicyInUse = settings.SecureIsolationEnabled || settings.NetworkIsolationEnabled ||
                              previousSettings.SecureIsolationEnabled || previousSettings.NetworkIsolationEnabled;
        if (!hostPolicyInUse)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? ".");
            WriteSettingsAtomically(settings);
            NetworkPolicyStatusText.Text = "Host network isolation is off. No administrator approval is required.";
            return true;
        }

        var candidateStatus = OfficialEmulatorNetworkIsolation.VerifyConfiguredPolicy(settings, baseDir);
        if (!candidateStatus.Success && PrepareNetworkPolicyChangeAsync is not null)
        {
            NetworkPolicyStatusText.Text = "Stopping Android before changing the host network boundary...";
            if (!await PrepareNetworkPolicyChangeAsync())
            {
                NetworkPolicyStatusText.Text = "Host policy was not changed because Android could not be proven stopped.";
                return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? ".");
        var previousExists = File.Exists(_settingsPath);
        var previousBytes = previousExists ? File.ReadAllBytes(_settingsPath) : null;

        try
        {
            WriteSettingsAtomically(settings);
            NetworkPolicyStatusText.Text = "Waiting for administrator approval to apply the process-scoped host policy...";
            var result = await OfficialEmulatorNetworkIsolation.ApplyConfiguredPolicyWithElevationAsync(settings, baseDir);
            if (result.Success)
            {
                NetworkPolicyStatusText.Text = result.Message;
                return true;
            }

            RestorePreviousSettings(previousExists, previousBytes);
            NetworkPolicyStatusText.Text = result.Message + " Previous settings were restored.";
            MessageBox.Show(this,
                result.Message + "\n\nNo settings change was committed. Host network policy changes require Windows administrator approval; REPlayer itself remains non-administrator.",
                "Network isolation — administrator approval required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            RestorePreviousSettings(previousExists, previousBytes);
            NetworkPolicyStatusText.Text = "Settings were not committed: " + ex.Message;
            MessageBox.Show(this,
                "Settings were not committed. " + ex.Message,
                "Network isolation",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void WriteSettingsAtomically(BackendSettings settings)
    {
        var temporaryPath = _settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private void RestorePreviousSettings(bool previousExists, byte[]? previousBytes)
    {
        if (!previousExists)
        {
            try { if (File.Exists(_settingsPath)) File.Delete(_settingsPath); } catch { }
            return;
        }

        var temporaryPath = _settingsPath + ".rollback-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, previousBytes ?? Array.Empty<byte>());
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private void UpdateNetworkPolicyStatus()
    {
        var settings = ReadBackendSettings();
        if (!settings.SecureIsolationEnabled && !settings.NetworkIsolationEnabled)
        {
            NetworkPolicyStatusText.Text = "Host network isolation is off. No administrator approval is required.";
            return;
        }
        var result = OfficialEmulatorNetworkIsolation.VerifyConfiguredPolicy(settings, RuntimeBackendFactory.GetBaseDir());
        NetworkPolicyStatusText.Text = result.Message;
    }

    private void UpdateNetworkPolicyUi()
    {
        if (NetworkIsolationModeCombo is null) return;
        var enabled = IsOn(NetworkIsolationToggle);
        var mode = OfficialEmulatorNetworkIsolation.NormalizeMode(SelectedTag(NetworkIsolationModeCombo, "internet-only"));
        var internetOnly = mode == "internet-only";
        var custom = mode == "custom";
        var restricted = enabled && mode is not "unrestricted";
        var hasInternet = restricted && mode is not "offline";

        if (internetOnly)
        {
            BlockHostServicesToggle.IsChecked = true;
            BlockPrivateNetworksToggle.IsChecked = true;
            BlockLinkLocalToggle.IsChecked = true;
            BlockMulticastToggle.IsChecked = true;
            SafeDnsToggle.IsChecked = true;
        }

        NetworkIsolationModeCombo.IsEnabled = enabled;
        NetworkDestinationOptions.IsEnabled = enabled && custom;
        SafeDnsToggle.IsEnabled = hasInternet && custom;
        DnsServersBox.IsEnabled = hasInternet && IsOn(SafeDnsToggle);
        AllowHostProxyToggle.IsEnabled = hasInternet && IsOn(BlockHostServicesToggle);
        HostProxyPortBox.IsEnabled = AllowHostProxyToggle.IsEnabled && IsOn(AllowHostProxyToggle);
    }

    private void SelectResolution(int width, int height, int dpi, string? mode)
    {
        var key = (width, height, dpi);
        Res1920x1080.IsChecked = key == (1920, 1080, 280);
        Res1600x900.IsChecked = key == (1600, 900, 240);
        Res1280x720.IsChecked = key == (1280, 720, 240);
        Res960x540.IsChecked = key == (960, 540, 160);
        Res1080x1920.IsChecked = key == (1080, 1920, 480);
        Res900x1600.IsChecked = key == (900, 1600, 320);
        Res720x1280.IsChecked = key == (720, 1280, 320);
        Res540x960.IsChecked = key == (540, 960, 240);

        var matchedPreset = new[] { Res1920x1080, Res1600x900, Res1280x720, Res960x540, Res1080x1920, Res900x1600, Res720x1280, Res540x960 }
            .Any(r => r.IsChecked == true);
        if (!matchedPreset)
            ResModeCustom.IsChecked = true;
        else if (string.Equals(mode, "portrait", StringComparison.OrdinalIgnoreCase))
            ResModePortrait.IsChecked = true;
        else if (string.Equals(mode, "large", StringComparison.OrdinalIgnoreCase))
            ResModeLarge.IsChecked = true;
        else if (string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
            ResModeCustom.IsChecked = true;
        else if (width > height)
            ResModeLandscape.IsChecked = true;
        else
            ResModePortrait.IsChecked = true;
    }

    private (int width, int height, int dpi) SelectedResolutionPreset()
    {
        if (IsOn(Res1920x1080)) return (1920, 1080, 280);
        if (IsOn(Res1600x900)) return (1600, 900, 240);
        if (IsOn(Res960x540)) return (960, 540, 160);
        if (IsOn(Res1080x1920)) return (1080, 1920, 480);
        if (IsOn(Res900x1600)) return (900, 1600, 320);
        if (IsOn(Res720x1280)) return (720, 1280, 320);
        if (IsOn(Res540x960)) return (540, 960, 240);
        return (1280, 720, 240);
    }

    private void SyncResolutionTextBoxesFromPreset()
    {
        if (IsOn(ResModeCustom)) return;
        var preset = SelectedResolutionPreset();
        var width = preset.width;
        var height = preset.height;
        if (IsOn(ResModePortrait) && width > height)
            (width, height) = (height, width);
        else if (!IsOn(ResModePortrait) && height > width)
            (width, height) = (height, width);
        ResolutionWidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
        ResolutionHeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
    }

    private string SelectedResolutionMode()
    {
        if (IsOn(ResModePortrait)) return "portrait";
        if (IsOn(ResModeLarge)) return "large";
        if (IsOn(ResModeCustom)) return "custom";
        return "landscape";
    }

    private static void SelectByTag(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;

    private static string NormalizePreferredBackend(string? value) =>
        string.Equals(value, "native", StringComparison.OrdinalIgnoreCase)
            ? "official-emulator"
            : string.IsNullOrWhiteSpace(value) ? "official-emulator" : value!;

    private static string NormalizeBootProfile(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "api34-resizable" => "api34-resizable",
        "play-compat" => "play-compat",
        _ => "api34-persona"
    };

    private static string NormalizeEmulationPerformanceMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "power-saver" => "power-saver",
        "high-performance" => "high-performance",
        _ => "balanced"
    };

    private static string RendererEngineFromSettings(BackendSettings settings)
    {
        if (string.Equals(settings.GpuMode, "swangle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.GpuMode, "swiftshader", StringComparison.OrdinalIgnoreCase)) return "swiftgl";
        if (string.Equals(settings.RendererApi, "vulkan", StringComparison.OrdinalIgnoreCase)) return "vulkan";
        if (string.Equals(settings.RendererApi, "host", StringComparison.OrdinalIgnoreCase)) return "host";
        return "opengl";
    }

    private static void ApplyRendererEngine(BackendSettings settings, string? engine)
    {
        switch ((engine ?? "opengl").Trim().ToLowerInvariant())
        {
            case "host":
                settings.RendererApi = "host";
                settings.GpuMode = "host";
                settings.SystemUiRenderer = string.Empty;
                break;
            case "vulkan":
                settings.RendererApi = "vulkan";
                settings.GpuMode = "host";
                settings.SystemUiRenderer = "skiavk";
                break;
            case "swiftgl":
                settings.RendererApi = "host";
                settings.GpuMode = "swangle";
                settings.SystemUiRenderer = "skiagl";
                break;
            default:
                settings.RendererApi = "opengl";
                settings.GpuMode = "host";
                settings.SystemUiRenderer = "skiagl";
                break;
        }
    }

    private static void ApplyEmulationPerformanceMode(BackendSettings settings)
    {
        switch (NormalizeEmulationPerformanceMode(settings.EmulationPerformanceMode))
        {
            case "power-saver":
                settings.CpuCores = 2;
                settings.RamMb = 2048;
                settings.Fps = 30;
                break;
            case "high-performance":
                // FinalBenchmark 2: 4 vCPU scored 132.714 multi-core versus
                // 105.867 at 3 vCPU (+25.4%) on the current 4C/8T host.
                settings.CpuCores = 4;
                settings.RamMb = 3072;
                settings.Fps = 60;
                break;
            default:
                settings.CpuCores = 3;
                settings.RamMb = 3072;
                settings.Fps = 60;
                break;
        }
    }

    private static int ParseRequiredInt(string? value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException(name + " must be a whole number.");
        return parsed;
    }

    private static int ClampInt(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    private static bool IsOn(System.Windows.Controls.Primitives.ToggleButton toggle) => toggle.IsChecked == true;
}
