using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReVM;

internal sealed class OfficialEmulatorPersonaPlan
{
    public required string Mode { get; init; }
    public required string PersonaFingerprint { get; init; }
    public required string DeviceModel { get; init; }
    public required string ExpectedHardwareModel { get; init; }
    public required string ExpectedHardwareMaker { get; init; }
    public required string ExpectedHardwareBrand { get; init; }
    public required string ExpectedHardwareDevice { get; init; }
    public required string AndroidId { get; init; }
    public required string ExpectedSerialPrefix { get; init; }
    public required string PhoneNumber { get; init; }
    public required string Locale { get; init; }
    public required string Timezone { get; init; }
    public required string CountryCode { get; init; }
    public required string CountryControl { get; init; }
    public required IReadOnlyList<string> EmulatorArguments { get; init; }
    public required IReadOnlyList<string> PostBootCommands { get; init; }

    public object ManifestEvidence => new
    {
        mode = Mode,
        fingerprint = PersonaFingerprint,
        deviceName = DeviceModel,
        bakedHardware = new
        {
            model = ExpectedHardwareModel,
            maker = ExpectedHardwareMaker,
            brand = ExpectedHardwareBrand,
            device = ExpectedHardwareDevice,
            serial = "observed-only"
        },
        androidId = AndroidId,

        phoneNumber = PhoneNumber,
        locale = Locale,
        timezone = Timezone,
        countryCode = CountryCode,
        controls = CountryControl == "wifi-service"
            ? new[] { "device-name", "timezone", "locale", "wifi-country-code", "phone-number", "secure-android-id" }
            : new[] { "device-name", "timezone", "locale-country", "phone-number", "secure-android-id" }
    };
}

internal static partial class OfficialEmulatorPersona
{
    private const string DefaultSeed = "replayer-persona-v1";

    public static OfficialEmulatorPersonaPlan Build(BackendSettings settings, string instanceId, string caseId)
    {
        var mode = NormalizeMode(settings.PersonaMode, settings.AndroidIdMode);
        var model = ValidateText(settings.DeviceModel, "device model", 64, "REPlayer Virtual Device");
        _ = ValidateText(settings.DeviceMaker, "device maker", 64, "REPlayer");
        var locale = NormalizeLocale(settings.PersonaLocale);
        var timezone = NormalizeTimezone(settings.PersonaTimezone);
        var country = NormalizeCountryCode(settings.PersonaCountryCode);
        var userSeed = string.IsNullOrWhiteSpace(settings.PersonaSeed) ? DefaultSeed : settings.PersonaSeed.Trim();
        if (userSeed.Any(char.IsControl) || userSeed.Length > 256)
            throw new InvalidOperationException("Persona seed must be printable and no longer than 256 characters.");
        var scope = mode == "rotate-case" ? instanceId + "\n" + caseId : instanceId;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(userSeed + "\n" + mode + "\n" + scope));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        var androidId = hex[..16];

        var phoneSuffix = 100 + (digest[8] % 100);
        var phone = "1202555" + phoneSuffix.ToString("D4", CultureInfo.InvariantCulture);
        const string hardwareModel = "REPlayer Virtual Device";
        const string hardwareMaker = "REPlayer";
        const string hardwareBrand = "replayer";
        const string hardwareDevice = "replayer_x86_64";
        const string serialPrefix = "EMULATOR";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', mode, model, hardwareModel, hardwareMaker, hardwareBrand, hardwareDevice, androidId, phone, locale, timezone, country)))).ToLowerInvariant();

        var emulatorArguments = new List<string>
        {
            "-timezone", timezone,
            "-change-locale", locale,
            "-phone-number", phone
        };
        var countryControl = string.Equals(settings.BootProfile, "api34-resizable", StringComparison.OrdinalIgnoreCase)
            ? "wifi-service"
            : "locale";
        var postBootCommands = new List<string>
        {
            "settings put secure android_id " + androidId,
            "settings put global device_name " + ShellQuote(model)
        };
        if (countryControl == "wifi-service")
            postBootCommands.Add("cmd wifi force-country-code enabled " + country);

        return new OfficialEmulatorPersonaPlan
        {
            Mode = mode,
            PersonaFingerprint = fingerprint,
            DeviceModel = model,
            ExpectedHardwareModel = hardwareModel,
            ExpectedHardwareMaker = hardwareMaker,
            ExpectedHardwareBrand = hardwareBrand,
            ExpectedHardwareDevice = hardwareDevice,
            AndroidId = androidId,
            ExpectedSerialPrefix = serialPrefix,
            PhoneNumber = phone,
            Locale = locale,
            Timezone = timezone,
            CountryCode = country,
            CountryControl = countryControl,
            EmulatorArguments = emulatorArguments,
            PostBootCommands = postBootCommands
        };
    }

    private static string NormalizeMode(string? personaMode, string? legacyAndroidIdMode)
    {
        if (string.Equals(personaMode, "rotate-case", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(legacyAndroidIdMode, "Rotate per case", StringComparison.OrdinalIgnoreCase))
            return "rotate-case";
        return "stable-instance";
    }

    private static string ValidateText(string? value, string field, int maxLength, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
            throw new InvalidOperationException($"Persona {field} must be printable and no longer than {maxLength} characters.");
        return normalized;
    }

    private static string NormalizeLocale(string? value)
    {
        var locale = string.IsNullOrWhiteSpace(value) ? "en-US" : value.Trim();
        if (!LocalePattern().IsMatch(locale))
            throw new InvalidOperationException("Persona locale must use language or language-COUNTRY form, for example en-US.");
        return locale;
    }

    private static string NormalizeTimezone(string? value)
    {
        var timezone = string.IsNullOrWhiteSpace(value) ? "America/New_York" : value.Trim();
        if (timezone.Length > 64 || timezone.Any(ch => char.IsControl(ch) || !(char.IsLetterOrDigit(ch) || ch is '/' or '_' or '-' or '+')))
            throw new InvalidOperationException("Persona timezone contains unsupported characters.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch { throw new InvalidOperationException("Persona timezone is not recognized by this Windows host: " + timezone); }
        return timezone;
    }

    private static string NormalizeCountryCode(string? value)
    {
        var country = string.IsNullOrWhiteSpace(value) ? "US" : value.Trim().ToUpperInvariant();
        if (country.Length != 2 || country.Any(ch => ch is < 'A' or > 'Z'))
            throw new InvalidOperationException("Persona country code must contain exactly two ASCII letters.");
        return country;
    }


    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[a-z]{2,3}(?:-[A-Z]{2})?$")]
    private static partial Regex LocalePattern();
}
