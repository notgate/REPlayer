using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Windows.Input;

namespace ReVM;

public static class BackendSettingsValidation
{
    private static readonly HashSet<string> Backends = new(StringComparer.OrdinalIgnoreCase) { "official-emulator", "ldplayer" };
    private static readonly HashSet<string> Accelerators = new(StringComparer.OrdinalIgnoreCase) { "auto", "whpx", "software" };
    private static readonly HashSet<string> BootProfiles = new(StringComparer.OrdinalIgnoreCase) { "api34-persona", "api34-resizable", "play-compat" };
    private static readonly HashSet<string> ResolutionModes = new(StringComparer.OrdinalIgnoreCase) { "landscape", "portrait", "large", "custom", "viewport" };
    private static readonly HashSet<string> RendererApis = new(StringComparer.OrdinalIgnoreCase) { "host", "opengl", "vulkan" };
    private static readonly HashSet<string> GpuModes = new(StringComparer.OrdinalIgnoreCase) { "host", "auto", "software", "swiftshader", "lavapipe", "swangle" };
    private static readonly HashSet<string> SystemUiRenderers = new(StringComparer.OrdinalIgnoreCase) { "", "skiagl", "skiavk" };
    private static readonly HashSet<string> PerformanceModes = new(StringComparer.OrdinalIgnoreCase) { "power-saver", "balanced", "high-performance" };
    private static readonly HashSet<string> IsolationModes = new(StringComparer.OrdinalIgnoreCase) { "internet-only", "offline", "unrestricted", "custom" };
    private static readonly HashSet<string> PersonaModes = new(StringComparer.OrdinalIgnoreCase) { "stable-instance", "rotate-case" };

    public static BackendSettings ValidateAndNormalize(BackendSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.PreferredBackend = NormalizeBackend(settings.PreferredBackend);
        if (string.Equals(settings.ResolutionMode, "viewport", StringComparison.OrdinalIgnoreCase))
            settings.ResolutionMode = "landscape";
        settings.Acceleration = Choice(settings.Acceleration, Accelerators, "Acceleration mode");
        settings.BootProfile = Choice(settings.BootProfile, BootProfiles, "Boot profile");
        // Root is a property of the immutable baseline lane, not a mutable switch.
        // Keep the legacy JSON field read-compatible but never let it contradict
        // release-observation or resizable-analysis semantics.
        settings.AdbRoot = false;
        settings.ResolutionMode = Choice(settings.ResolutionMode, ResolutionModes, "Resolution mode");
        settings.RendererApi = Choice(settings.RendererApi, RendererApis, "Renderer API");
        settings.GpuMode = Choice(settings.GpuMode, GpuModes, "GPU mode");
        settings.SystemUiRenderer = Choice(settings.SystemUiRenderer ?? string.Empty, SystemUiRenderers, "System UI renderer", allowEmpty: true);
        settings.EmulationPerformanceMode = Choice(settings.EmulationPerformanceMode, PerformanceModes, "Performance mode");
        settings.NetworkIsolationMode = Choice(settings.NetworkIsolationMode, IsolationModes, "Network isolation mode");
        settings.PersonaMode = Choice(settings.PersonaMode, PersonaModes, "Persona mode");

        RequireRange(settings.DisplayWidth, 320, 3840, "Display width");
        RequireRange(settings.DisplayHeight, 240, 2160, "Display height");
        RequireRange(settings.DisplayDpi, 120, 640, "Display density");
        RequireRange(settings.Fps, 30, 240, "Frame rate");
        RequireRange(settings.CpuCores, 1, 64, "CPU cores");
        RequireRange(settings.RamMb, 1024, 131072, "Memory");
        RequireRange(settings.StorageGb, 8, 4096, "Storage");
        RequireRange(settings.AudioVolumePercent, 0, 100, "Audio volume");

        RequirePort(settings.AdbPort, "ADB port");
        RequirePort(settings.FridaPort, "Frida port");
        RequirePort(settings.MitmProxyPort, "mitmproxy port");
        RequirePort(settings.NetworkHostProxyPort, "Host analysis proxy port");
        if (settings.AdbPort == settings.FridaPort || settings.AdbPort == settings.MitmProxyPort)
            throw new InvalidOperationException("ADB must use a different port from Frida and mitmproxy.");

        if (settings.OfficialEmulatorDisposableMode && !settings.ReadOnlyBaseImage)
            throw new InvalidOperationException("Disposable detonation mode requires the base system image to remain read-only.");
        if (settings.SecureIsolationEnabled && !settings.NetworkIsolationEnabled)
            throw new InvalidOperationException("Secure isolation requires network isolation to remain enabled.");
        if (settings.SecureIsolationEnabled && string.Equals(settings.NetworkIsolationMode, "unrestricted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unrestricted networking cannot be combined with secure isolation.");

        if (settings.NetworkUseSafeDns && !string.Equals(settings.NetworkIsolationMode, "offline", StringComparison.OrdinalIgnoreCase))
            ValidateDns(settings.NetworkDnsServers);

        settings.RuntimePath = Printable(settings.RuntimePath, "Runtime path", 1024, "runtime/");
        settings.DeviceModel = Printable(settings.DeviceModel, "Device name", 64, "REPlayer Virtual Device");
        settings.DeviceMaker = "REPlayer";
        settings.PersonaSeed = Printable(settings.PersonaSeed, "Persona seed", 256, string.Empty, allowEmpty: true);
        settings.PersonaLocale = Printable(settings.PersonaLocale, "Persona locale", 32, "en-US");
        settings.PersonaTimezone = Printable(settings.PersonaTimezone, "Persona timezone", 64, "America/New_York");
        settings.PersonaCountryCode = Printable(settings.PersonaCountryCode, "Persona country", 2, "US").ToUpperInvariant();

        ValidateHotkeys(settings);
        if (settings.PersonaEnabled)
            _ = OfficialEmulatorPersona.Build(settings, "settings-validation", "settings-validation");

        return settings;
    }

    private static string NormalizeBackend(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "native" or "revm-native" or "google" or "google-emulator")
            normalized = "official-emulator";
        if (!Backends.Contains(normalized))
            throw new InvalidOperationException("Runtime backend must be official-emulator or ldplayer.");
        return normalized;
    }

    private static string Choice(string? value, HashSet<string> choices, string name, bool allowEmpty = false)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (allowEmpty && normalized.Length == 0) return string.Empty;
        if (!choices.Contains(normalized))
            throw new InvalidOperationException($"{name} has an unsupported value: {value}");
        return normalized;
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
    }

    private static void RequirePort(int port, string name) => RequireRange(port, 1024, 65535, name);

    private static string Printable(string? value, string name, int maximumLength, string fallback, bool allowEmpty = false)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!allowEmpty && normalized.Length == 0)
            throw new InvalidOperationException(name + " is required.");
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
            throw new InvalidOperationException($"{name} must be printable and no longer than {maximumLength} characters.");
        return normalized;
    }

    private static void ValidateDns(string? value)
    {
        var servers = (value ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (servers.Length is < 1 or > 4 || servers.Any(server => !IPAddress.TryParse(server, out _)))
            throw new InvalidOperationException("DNS servers must contain one to four literal IP addresses.");
    }

    private static void ValidateHotkeys(BackendSettings settings)
    {
        var gestures = new[]
        {
            ("Back hotkey", settings.BackHotkey),
            ("Home hotkey", settings.HomeHotkey),
            ("App switcher hotkey", settings.AppSwitcherHotkey),
            ("Full-screen hotkey", settings.FullscreenHotkey)
        };
        var converter = new KeyGestureConverter();
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in gestures)
        {
            var text = Printable(value, name, 48, string.Empty);
            try
            {
                if (converter.ConvertFromString(null, CultureInfo.InvariantCulture, text) is not KeyGesture)
                    throw new FormatException();
            }
            catch
            {
                throw new InvalidOperationException(name + " is not a valid key gesture.");
            }
            if (!normalized.Add(text))
                throw new InvalidOperationException("Hotkey assignments must be unique.");
        }
    }
}
