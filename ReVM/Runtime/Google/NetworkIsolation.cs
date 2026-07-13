using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ReVM;

internal sealed record NetworkFirewallRule(
    string Name,
    string ApplicationPath,
    string RemoteAddresses,
    int Protocol = 256,
    string? RemotePorts = null);

internal sealed class OfficialEmulatorNetworkPlan
{
    public required string Mode { get; init; }
    public required string InstallationId { get; init; }
    public required string RuleGroup { get; init; }
    public required IReadOnlyList<NetworkFirewallRule> FirewallRules { get; init; }
    public required IReadOnlyList<string> DnsServers { get; init; }
    public required IReadOnlyList<string> EmulatorArguments { get; init; }
    public required IReadOnlyList<string> GuestFilterScripts { get; init; }
    public required string Fingerprint { get; init; }
    public bool SecureIsolationEnabled { get; init; }
    public bool NetworkIsolationEnabled { get; init; }
    public bool IsOffline => string.Equals(Mode, "offline", StringComparison.OrdinalIgnoreCase);

    public object ToManifest(bool hostPolicyVerified) => new
    {
        mode = Mode,
        secureIsolation = SecureIsolationEnabled,
        networkIsolation = NetworkIsolationEnabled,
        failClosed = SecureIsolationEnabled && NetworkIsolationEnabled && !string.Equals(Mode, "unrestricted", StringComparison.OrdinalIgnoreCase),
        hostPolicyVerified,
        firewallRuleGroup = RuleGroup,
        firewallRuleCount = FirewallRules.Count,
        dnsServers = DnsServers,
        guestFilterEnabled = GuestFilterScripts.Count > 0,
        fingerprint = Fingerprint
    };
}

internal sealed record NetworkPolicyOperationResult(bool Success, string Message, bool ElevationCancelled = false);

internal static class OfficialEmulatorNetworkIsolation
{
    internal const string WorkerArgument = "--replayer-network-policy-worker";
    private const string ProtectedWorkerFolderName = "REPlayer Security";
    private const string ProtectedWorkerSubdirectory = "NetworkPolicyWorker";
    private const string ProtectedWorkerConfigName = "worker-installation.json";
    private static readonly string[] ProtectedWorkerFiles =
    [
        "REPlayer.exe",
        "REPlayer.dll",
        "REPlayer.deps.json",
        "REPlayer.runtimeconfig.json",
        "revm-render-host.dll"
    ];
    private const string RuleGroupPrefix = "REPlayer Network Isolation";
    private const int FirewallDirectionOutbound = 2;
    private const int FirewallActionBlock = 0;
    private const int FirewallProfilesAll = int.MaxValue;
    private const int ProtocolAny = 256;
    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int ProtocolIcmpV4 = 1;
    private const int ProtocolIcmpV6 = 58;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string NormalizeMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "offline" => "offline",
        "unrestricted" => "unrestricted",
        "custom" => "custom",
        _ => "internet-only"
    };

    public static OfficialEmulatorNetworkPlan BuildPlan(BackendSettings settings, string baseDir)
    {
        var runtimeDir = Path.Combine(baseDir, "runtime", "google-emulator", "sdk", "emulator");
        var emulatorPath = Path.GetFullPath(Path.Combine(runtimeDir, "emulator.exe"));
        var qemuPath = Path.GetFullPath(Path.Combine(runtimeDir, "qemu", "windows-x86_64", "qemu-system-x86_64.exe"));
        var qemuHeadlessPath = Path.GetFullPath(Path.Combine(runtimeDir, "qemu", "windows-x86_64", "qemu-system-x86_64-headless.exe"));
        var installationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(baseDir).ToUpperInvariant())))[..10].ToLowerInvariant();
        var group = $"{RuleGroupPrefix} [{installationId}]";
        var mode = NormalizeMode(settings.NetworkIsolationMode);
        var rules = new List<NetworkFirewallRule>();
        var emulatorArguments = new List<string>();
        var guestScripts = new List<string>();
        IReadOnlyList<string> dnsServers = Array.Empty<string>();

        if (!settings.NetworkIsolationEnabled) mode = "unrestricted";

        if (mode == "offline")
        {
            AddRuleForExecutables(rules, group, "Offline", "*", ProtocolAny, null, emulatorPath, qemuPath, qemuHeadlessPath);
            dnsServers = Array.Empty<string>();
        }
        else if (mode != "unrestricted")
        {
            var blockHost = mode == "internet-only" || settings.NetworkBlockHostServices;
            var blockPrivate = mode == "internet-only" || settings.NetworkBlockPrivateNetworks;
            var blockLinkLocal = mode == "internet-only" || settings.NetworkBlockLinkLocal;
            var blockMulticast = mode == "internet-only" || settings.NetworkBlockMulticast;
            var allowHostProxy = settings.NetworkAllowHostProxy && blockHost;
            var proxyPort = Math.Clamp(settings.NetworkHostProxyPort, 1, 65535);

            var useSafeDns = mode == "internet-only" || settings.NetworkUseSafeDns;
            if (useSafeDns)
            {
                dnsServers = ParseDnsServers(settings.NetworkDnsServers);
                if (dnsServers.Count == 0)
                    throw new InvalidOperationException("Network isolation requires at least one valid DNS server IP address.");
                emulatorArguments.Add("-dns-server");
                emulatorArguments.Add(string.Join(',', dnsServers));
            }
            else
            {
                dnsServers = Array.Empty<string>();
            }

            if (allowHostProxy)
            {
                emulatorArguments.Add("-http-proxy");
                emulatorArguments.Add($"http://127.0.0.1:{proxyPort.ToString(CultureInfo.InvariantCulture)}");
            }

            var blockedV4 = new List<string>();
            var blockedV6 = new List<string>();
            if (blockHost)
            {
                if (!allowHostProxy) blockedV4.Add("127.0.0.0/8");
                // HNetCfg.FWRule rejects ::1/128 and rejects ::/0 only when
                // Rules.Add is called. These two accepted half-spaces cover
                // all IPv6 and prevent a host/LAN bypass.
                blockedV6.AddRange(["::/1", "8000::/1"]);
            }
            if (blockPrivate)
            {
                // LocalSubnet also covers directly attached networks that use
                // publicly routed address space instead of RFC1918.
                blockedV4.AddRange(["LocalSubnet", "10.0.0.0/8", "100.64.0.0/10", "172.16.0.0/12", "192.168.0.0/16"]);
                if (!blockHost) blockedV6.Add("fc00::/7");
            }
            if (blockLinkLocal)
            {
                blockedV4.AddRange(["0.0.0.0/8", "169.254.0.0/16", "192.0.0.0/24", "198.18.0.0/15"]);
                if (!blockHost) blockedV6.Add("fe80::/10");
            }
            if (blockMulticast)
            {
                // 240/4 already includes the limited broadcast address. The
                // Firewall COM API rejects 255.255.255.255/32 as a standalone
                // token even though New-NetFirewallRule accepts it.
                blockedV4.AddRange(["224.0.0.0/4", "240.0.0.0/4"]);
                if (!blockHost) blockedV6.Add("ff00::/8");
            }

            if (blockedV4.Count > 0)
                AddRuleForExecutables(rules, group, "Blocked IPv4", string.Join(',', blockedV4.Distinct()), ProtocolAny, null, emulatorPath, qemuPath, qemuHeadlessPath);
            if (blockedV6.Count > 0)
                AddRuleForExecutables(rules, group, "Blocked IPv6", string.Join(',', blockedV6.Distinct()), ProtocolAny, null, emulatorPath, qemuPath, qemuHeadlessPath);

            if (allowHostProxy)
            {
                var tcpBlockedPorts = ExcludingPort(proxyPort);
                if (!string.IsNullOrWhiteSpace(tcpBlockedPorts))
                    AddRuleForExecutables(rules, group, "Loopback TCP except proxy", "127.0.0.0/8", ProtocolTcp, tcpBlockedPorts, emulatorPath, qemuPath, qemuHeadlessPath);
                AddRuleForExecutables(rules, group, "Loopback UDP", "127.0.0.0/8", ProtocolUdp, null, emulatorPath, qemuPath, qemuHeadlessPath);
                AddRuleForExecutables(rules, group, "Loopback ICMPv4", "127.0.0.0/8", ProtocolIcmpV4, null, emulatorPath, qemuPath, qemuHeadlessPath);
            }

            guestScripts.Add(BuildGuestIpv4Filter(blockHost, blockPrivate, blockLinkLocal, blockMulticast, allowHostProxy, proxyPort));
            guestScripts.Add(BuildGuestIpv6Filter(blockHost, blockPrivate, blockLinkLocal, blockMulticast));
        }
        else
        {
            dnsServers = Array.Empty<string>();
        }

        var canonical = JsonSerializer.Serialize(new
        {
            mode,
            settings.SecureIsolationEnabled,
            settings.NetworkIsolationEnabled,
            dnsServers,
            emulatorArguments,
            rules = rules.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray()
        });

        return new OfficialEmulatorNetworkPlan
        {
            Mode = mode,
            InstallationId = installationId,
            RuleGroup = group,
            FirewallRules = rules,
            DnsServers = dnsServers,
            EmulatorArguments = emulatorArguments,
            GuestFilterScripts = guestScripts.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(),
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
            SecureIsolationEnabled = settings.SecureIsolationEnabled,
            NetworkIsolationEnabled = settings.NetworkIsolationEnabled
        };
    }

    public static NetworkPolicyOperationResult VerifyConfiguredPolicy(BackendSettings settings, string baseDir)
    {
        try
        {
            return VerifyPlan(BuildPlan(settings, baseDir));
        }
        catch (Exception ex)
        {
            return new NetworkPolicyOperationResult(false, ex.Message);
        }
    }

    public static NetworkPolicyOperationResult VerifyPlan(OfficialEmulatorNetworkPlan plan)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;

            rulesObject = policy.Rules;
            var actual = ReadManagedRules(rulesObject, plan.RuleGroup);
            var stagingCount = CountStagingRules(rulesObject, plan.RuleGroup);
            if (stagingCount != 0)
                return new NetworkPolicyOperationResult(false, $"Host policy has {stagingCount} interrupted staging rules and requires administrator reconciliation.");
            if (actual.Count != plan.FirewallRules.Count)
                return new NetworkPolicyOperationResult(false, $"Host policy is stale: expected {plan.FirewallRules.Count} managed rules, found {actual.Count}.");

            if (plan.FirewallRules.Count > 0)
            {
                foreach (var profile in new[] { 1, 2, 4 })
                {
                    if (!(bool)policy.FirewallEnabled[profile])
                        return new NetworkPolicyOperationResult(false, "Windows Defender Firewall must be enabled for Domain, Private, and Public profiles while Secure Isolation is active.");
                }
            }

            foreach (var expected in plan.FirewallRules)
            {
                if (!actual.TryGetValue(expected.Name, out var rule))
                    return new NetworkPolicyOperationResult(false, "Missing firewall rule: " + expected.Name);
                if (!rule.Enabled || rule.Direction != FirewallDirectionOutbound || rule.Action != FirewallActionBlock || (rule.Profiles & 7) != 7)
                    return new NetworkPolicyOperationResult(false, "Firewall rule is disabled or has unsafe direction/action/profile scope: " + expected.Name);
                if (!PathEquals(rule.ApplicationName, expected.ApplicationPath) ||
                    !AddressListEquals(rule.RemoteAddresses, expected.RemoteAddresses) ||
                    rule.Protocol != expected.Protocol ||
                    !PortListEquals(rule.RemotePorts, expected.RemotePorts))
                    return new NetworkPolicyOperationResult(false, "Firewall rule does not match the configured policy: " + expected.Name);
            }

            return new NetworkPolicyOperationResult(true, plan.FirewallRules.Count == 0
                ? "REPlayer has no active host network restrictions for this mode."
                : $"Verified {plan.FirewallRules.Count} REPlayer firewall rules.");
        }
        catch (Exception ex)
        {
            return new NetworkPolicyOperationResult(false, "Could not verify Windows network policy: " + ex.Message);
        }
        finally
        {
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    public static async Task<NetworkPolicyOperationResult> ApplyConfiguredPolicyWithElevationAsync(BackendSettings settings, string baseDir)
    {
        OfficialEmulatorNetworkPlan plan;
        try { plan = BuildPlan(settings, baseDir); }
        catch (Exception ex) { return new NetworkPolicyOperationResult(false, ex.Message); }

        var current = VerifyPlan(plan);
        if (current.Success) return current;

        var protectedWorker = await EnsureProtectedWorkerInstalledAsync(baseDir).ConfigureAwait(false);
        if (!protectedWorker.Success) return protectedWorker;
        var executable = GetProtectedWorkerExecutablePath();

        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = WorkerArgument + " " + plan.Fingerprint,
                WorkingDirectory = baseDir
            };
            using var process = Process.Start(psi);
            if (process is null) return new NetworkPolicyOperationResult(false, "Windows did not start the elevated network-policy worker.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = ReadWorkerFailureMessage(baseDir);
                return new NetworkPolicyOperationResult(false,
                    $"Elevated network-policy worker failed with exit code {process.ExitCode}." +
                    (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
            }
            return VerifyConfiguredPolicy(settings, baseDir);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new NetworkPolicyOperationResult(false, "Network-policy elevation was cancelled.", true);
        }
        catch (Exception ex)
        {
            return new NetworkPolicyOperationResult(false, "Could not apply network policy: " + ex.Message);
        }
    }

    private static string GetProtectedWorkerDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProtectedWorkerFolderName, ProtectedWorkerSubdirectory);

    private static string GetProtectedWorkerExecutablePath() => Path.Combine(GetProtectedWorkerDirectory(), "REPlayer.exe");

    private static async Task<NetworkPolicyOperationResult> EnsureProtectedWorkerInstalledAsync(string baseDir)
    {
        if (ValidateProtectedWorkerInstallation(baseDir, out var validMessage))
            return new NetworkPolicyOperationResult(true, validMessage);

        var sourceDirectory = AppContext.BaseDirectory;
        var fileEntries = new List<object>();
        foreach (var name in ProtectedWorkerFiles)
        {
            var sourcePath = Path.Combine(sourceDirectory, name);
            if (!File.Exists(sourcePath))
                return new NetworkPolicyOperationResult(false, "Protected network worker cannot be installed because a build output is missing: " + name);
            fileEntries.Add(new { name, sha256 = ComputeFileSha256(sourcePath) });
        }

        var manifestJson = JsonSerializer.Serialize(new
        {
            baseDir = Path.GetFullPath(baseDir),
            files = fileEntries
        });
        var destination = GetProtectedWorkerDirectory();
        var script = $@"
$ErrorActionPreference = 'Stop'
$source = '{EscapePowerShellLiteral(sourceDirectory)}'
$destination = '{EscapePowerShellLiteral(destination)}'
$manifest = ConvertFrom-Json @'
{manifestJson}
'@
if (Test-Path -LiteralPath $destination) {{ Remove-Item -LiteralPath $destination -Recurse -Force }}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($entry in $manifest.files) {{
    $sourceFile = Join-Path $source $entry.name
    if (-not (Test-Path -LiteralPath $sourceFile)) {{ throw ""Missing worker source file: $($entry.name)"" }}
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceFile).Hash.ToLowerInvariant()
    if ($sourceHash -ne [string]$entry.sha256) {{ throw ""Worker source hash changed before protected installation: $($entry.name)"" }}
    Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $destination $entry.name) -Force
    $destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $destination $entry.name)).Hash.ToLowerInvariant()
    if ($destinationHash -ne [string]$entry.sha256) {{ throw ""Protected worker copy failed hash verification: $($entry.name)"" }}
}}
@{{ baseDir = [string]$manifest.baseDir; files = $manifest.files }} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination '{ProtectedWorkerConfigName}') -Encoding UTF8
& icacls.exe $destination /inheritance:r | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Could not remove inherited permissions from the protected worker directory.' }}
& icacls.exe $destination /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Could not apply protected worker permissions.' }}
& icacls.exe $destination /setowner '*S-1-5-32-544' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Could not assign the protected worker owner.' }}
";

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            var psi = new ProcessStartInfo(powershell)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded
            };
            using var process = Process.Start(psi);
            if (process is null)
                return new NetworkPolicyOperationResult(false, "Windows did not start the protected-worker installer.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new NetworkPolicyOperationResult(false, $"Protected network-worker installation failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new NetworkPolicyOperationResult(false, "Protected network-worker installation was cancelled.", true);
        }
        catch (Exception ex)
        {
            return new NetworkPolicyOperationResult(false, "Could not install the protected network worker: " + ex.Message);
        }

        return ValidateProtectedWorkerInstallation(baseDir, out var installedMessage)
            ? new NetworkPolicyOperationResult(true, installedMessage)
            : new NetworkPolicyOperationResult(false, "Protected network-worker installation did not pass post-install hash/configuration validation: " + installedMessage);
    }

    private static bool ValidateProtectedWorkerInstallation(string baseDir, out string message)
    {
        try
        {
            var sourceDirectory = AppContext.BaseDirectory;
            var destination = GetProtectedWorkerDirectory();
            var configPath = Path.Combine(destination, ProtectedWorkerConfigName);
            if (!File.Exists(configPath))
            {
                message = "Protected network worker is not installed.";
                return false;
            }
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!config.RootElement.TryGetProperty("baseDir", out var configuredBaseDir) ||
                !PathEquals(configuredBaseDir.GetString() ?? string.Empty, baseDir))
            {
                message = "Protected network worker is bound to a different REPlayer installation.";
                return false;
            }
            foreach (var name in ProtectedWorkerFiles)
            {
                var source = Path.Combine(sourceDirectory, name);
                var installed = Path.Combine(destination, name);
                if (!File.Exists(source) || !File.Exists(installed) ||
                    !string.Equals(ComputeFileSha256(source), ComputeFileSha256(installed), StringComparison.OrdinalIgnoreCase))
                {
                    message = "Protected network worker is missing or out of date: " + name;
                    return false;
                }
            }
            if (!ValidateProtectedWorkerAcl(destination, out message)) return false;
            message = "Protected network-policy worker verified.";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static bool ValidateProtectedWorkerAcl(string directory, out string message)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = $@"
$acl = Get-Acl -LiteralPath '{EscapePowerShellLiteral(directory)}'
$unsafeSids = @('S-1-1-0','S-1-5-11','S-1-5-32-545')
$writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
$bad = @($acl.Access | Where-Object {{
    $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
    $unsafeSids -contains $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -and
    (($_.FileSystemRights -band $writeMask) -ne 0)
}})
if ($acl.AreAccessRulesProtected -and $bad.Count -eq 0) {{ exit 0 }} else {{ exit 9 }}
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = Process.Start(new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded
        });
        if (process is null)
        {
            message = "Could not inspect protected worker permissions.";
            return false;
        }
        process.WaitForExit(10_000);
        if (process.HasExited && process.ExitCode == 0)
        {
            message = "Protected worker ACL verified.";
            return true;
        }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        message = "Protected worker directory is writable by a non-administrator identity or still inherits permissions.";
        return false;
    }

    private static bool TryResolveProtectedWorkerBaseDir(out string baseDir, out string error)
    {
        baseDir = string.Empty;
        var currentExecutable = Environment.ProcessPath ?? string.Empty;
        if (!PathEquals(currentExecutable, GetProtectedWorkerExecutablePath()))
        {
            error = "Refusing to elevate a mutable REPlayer application host. Install and use the protected network-policy worker.";
            return false;
        }
        try
        {
            var configPath = Path.Combine(GetProtectedWorkerDirectory(), ProtectedWorkerConfigName);
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            baseDir = Path.GetFullPath(config.RootElement.GetProperty("baseDir").GetString()
                ?? throw new InvalidOperationException("Protected worker base directory is missing."));
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "Protected worker configuration is invalid: " + ex.Message;
            return false;
        }
    }

    private static string ComputeFileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    public static bool TryHandleElevatedWorker(IReadOnlyList<string> arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Count == 0 || !string.Equals(arguments[0], WorkerArgument, StringComparison.Ordinal)) return false;
        if (arguments.Count != 2 || arguments[1].Length != 64 || arguments[1].Any(ch => !Uri.IsHexDigit(ch)))
        {
            exitCode = 42;
            return true;
        }
        if (!TryResolveProtectedWorkerBaseDir(out var protectedBaseDir, out var protectedError))
        {
            try { WriteWorkerFailure(RevmPaths.ExecutableBaseDir, new InvalidOperationException(protectedError)); } catch { }
            exitCode = 44;
            return true;
        }
        if (!IsAdministrator())
        {
            var error = new InvalidOperationException("The network-policy worker requires administrator privileges.");
            try { WriteWorkerFailure(protectedBaseDir, error); } catch { }
            exitCode = 41;
            return true;
        }

        try
        {
            var baseDir = protectedBaseDir;
            var settingsPath = Path.Combine(baseDir, "runtime", "backend-settings.json");
            var settings = File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(settingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BackendSettings()
                : new BackendSettings();
            var plan = BuildPlan(settings, baseDir);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(plan.Fingerprint), Convert.FromHexString(arguments[1])))
                throw new InvalidOperationException("Network settings changed while elevation was pending; reopen Settings and apply the current policy.");

            EnsureNoManagedEmulatorProcesses(baseDir);
            var previousRules = CaptureCanonicalRuleSet(plan.RuleGroup);
            try
            {
                ApplyPlanDirect(plan);
                var verified = VerifyPlan(plan);
                if (!verified.Success) throw new InvalidOperationException(verified.Message);
                WriteWorkerStatus(baseDir, plan, true, verified.Message);
            }
            catch (Exception applyException)
            {
                try
                {
                    RestoreCanonicalRuleSet(plan.RuleGroup, previousRules);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Network policy failed and the prior managed rule set could not be restored. Android remains blocked; reconcile the host policy before launch.",
                        applyException,
                        rollbackException);
                }
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            try { WriteWorkerFailure(protectedBaseDir, ex); } catch { }
            exitCode = 43;
            return true;
        }
    }

    private static void EnsureNoManagedEmulatorProcesses(string baseDir)
    {
        var runtimeDir = Path.GetFullPath(Path.Combine(baseDir, "runtime", "google-emulator", "sdk", "emulator"));
        var managedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(runtimeDir, "emulator.exe")),
            Path.GetFullPath(Path.Combine(runtimeDir, "qemu", "windows-x86_64", "qemu-system-x86_64.exe")),
            Path.GetFullPath(Path.Combine(runtimeDir, "qemu", "windows-x86_64", "qemu-system-x86_64-headless.exe"))
        };

        foreach (var processName in new[] { "emulator", "qemu-system-x86_64", "qemu-system-x86_64-headless" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string? path;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Could not verify whether emulator process {process.Id} is stopped; refusing to change host policy.", ex);
                    }
                    if (!string.IsNullOrWhiteSpace(path) && managedPaths.Contains(Path.GetFullPath(path)))
                        throw new InvalidOperationException(
                            $"Stop Android before changing host network policy. Managed emulator process {process.Id} is still active.");
                }
            }
        }
    }

    private static IReadOnlyList<NetworkFirewallRule> CaptureCanonicalRuleSet(string group)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            return ReadManagedRules(rulesObject, group).Values
                .Select(rule => new NetworkFirewallRule(
                    rule.Name,
                    rule.ApplicationName,
                    rule.RemoteAddresses,
                    rule.Protocol,
                    rule.RemotePorts))
                .ToArray();
        }
        finally
        {
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    private static void RestoreCanonicalRuleSet(string group, IReadOnlyList<NetworkFirewallRule> previousRules)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            RemoveRuleSet(rulesObject, group);
            if (previousRules.Count > 0)
                EnsureRuleSet(rulesObject, rules, group, previousRules);
            RemoveStagingRuleSets(rulesObject, group);
        }
        finally
        {
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    private static void ApplyPlanDirect(OfficialEmulatorNetworkPlan plan)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;

            if (plan.FirewallRules.Count == 0)
            {
                // Unrestricted is an explicit administrator-approved relaxation.
                RemoveRuleSet(rulesObject, plan.RuleGroup);
                RemoveStagingRuleSets(rulesObject, plan.RuleGroup);
                return;
            }

            // Stage the complete replacement first. If the worker is interrupted,
            // either the prior canonical rules or this staging set remains active.
            var stagingGroup = $"{plan.RuleGroup} [staging {plan.Fingerprint[..12]}]";
            var stagingRules = plan.FirewallRules
                .Select((spec, index) => spec with { Name = $"{stagingGroup} - {index:D2}" })
                .ToArray();
            EnsureRuleSet(rulesObject, rules, stagingGroup, stagingRules);

            // Repair/add canonical rules while staging rules enforce the same block
            // policy, then remove obsolete canonical entries only after all desired
            // entries exist and match.
            EnsureRuleSet(rulesObject, rules, plan.RuleGroup, plan.FirewallRules);
            RemoveStagingRuleSets(rulesObject, plan.RuleGroup);
        }
        finally
        {
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    private static void EnsureRuleSet(object rulesObject, dynamic rules, string group, IReadOnlyList<NetworkFirewallRule> expected)
    {
        var actual = ReadManagedRules(rulesObject, group);
        foreach (var spec in expected)
        {
            if (actual.TryGetValue(spec.Name, out var existing) && RuleMatches(existing, spec)) continue;
            if (actual.ContainsKey(spec.Name)) rules.Remove(spec.Name);
            AddFirewallRule(rules, group, spec);
        }

        actual = ReadManagedRules(rulesObject, group);
        foreach (var staleName in actual.Keys.Except(expected.Select(x => x.Name), StringComparer.OrdinalIgnoreCase).ToArray())
            rules.Remove(staleName);

        actual = ReadManagedRules(rulesObject, group);
        if (actual.Count != expected.Count || expected.Any(spec => !actual.TryGetValue(spec.Name, out var rule) || !RuleMatches(rule, spec)))
            throw new InvalidOperationException($"Windows Firewall did not retain the complete staged policy for {group}.");
    }

    private static void AddFirewallRule(dynamic rules, string group, NetworkFirewallRule spec)
    {
        object? ruleObject = null;
        try
        {
            ruleObject = CreateCom("HNetCfg.FWRule");
            dynamic rule = ruleObject;
            rule.Name = spec.Name;
            rule.Description = "Managed by REPlayer. Applies only to the bundled official Android Emulator runtime.";
            rule.Grouping = group;
            rule.ApplicationName = spec.ApplicationPath;
            rule.Direction = FirewallDirectionOutbound;
            rule.Action = FirewallActionBlock;
            rule.Enabled = true;
            rule.Profiles = FirewallProfilesAll;
            rule.Protocol = spec.Protocol;
            rule.RemoteAddresses = spec.RemoteAddresses;
            if (!string.IsNullOrWhiteSpace(spec.RemotePorts) && spec.Protocol is ProtocolTcp or ProtocolUdp)
                rule.RemotePorts = spec.RemotePorts;
            rules.Add(rule);
        }
        finally
        {
            ReleaseCom(ruleObject);
        }
    }

    private static void RemoveRuleSet(object rulesObject, string group)
    {
        dynamic rules = rulesObject;
        foreach (var name in ReadManagedRules(rulesObject, group).Keys.ToArray())
            rules.Remove(name);
    }

    private static void RemoveStagingRuleSets(object rulesObject, string canonicalGroup)
    {
        dynamic rules = rulesObject;
        var stagingPrefix = canonicalGroup + " [staging ";
        var names = new List<string>();
        foreach (var item in rules)
        {
            object? ruleObject = item;
            try
            {
                dynamic rule = item;
                var grouping = Convert.ToString(rule.Grouping, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!grouping.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Convert.ToString(rule.Name, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            finally
            {
                ReleaseCom(ruleObject);
            }
        }

        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            rules.Remove(name);
    }

    private static int CountStagingRules(object rulesObject, string canonicalGroup)
    {
        dynamic rules = rulesObject;
        var stagingPrefix = canonicalGroup + " [staging ";
        var count = 0;
        foreach (var item in rules)
        {
            object? ruleObject = item;
            try
            {
                dynamic rule = item;
                var grouping = Convert.ToString(rule.Grouping, CultureInfo.InvariantCulture) ?? string.Empty;
                if (grouping.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase)) count++;
            }
            finally
            {
                ReleaseCom(ruleObject);
            }
        }
        return count;
    }

    private static bool RuleMatches(ActualFirewallRule rule, NetworkFirewallRule expected) =>
        rule.Enabled &&
        rule.Direction == FirewallDirectionOutbound &&
        rule.Action == FirewallActionBlock &&
        (rule.Profiles & 7) == 7 &&
        PathEquals(rule.ApplicationName, expected.ApplicationPath) &&
        AddressListEquals(rule.RemoteAddresses, expected.RemoteAddresses) &&
        rule.Protocol == expected.Protocol &&
        PortListEquals(rule.RemotePorts, expected.RemotePorts);

    private static Dictionary<string, ActualFirewallRule> ReadManagedRules(object rulesObject, string group)
    {
        dynamic rules = rulesObject;
        var result = new Dictionary<string, ActualFirewallRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rules)
        {
            object? ruleObject = item;
            try
            {
                dynamic rule = item;
                var grouping = Convert.ToString(rule.Grouping, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.Equals(grouping, group, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Convert.ToString(rule.Name, CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                string? ports = null;
                try { ports = Convert.ToString(rule.RemotePorts, CultureInfo.InvariantCulture); } catch { }
                result[name] = new ActualFirewallRule(
                    name,
                    Convert.ToString(rule.ApplicationName, CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(rule.RemoteAddresses, CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToInt32(rule.Protocol, CultureInfo.InvariantCulture),
                    ports,
                    Convert.ToBoolean(rule.Enabled, CultureInfo.InvariantCulture),
                    Convert.ToInt32(rule.Direction, CultureInfo.InvariantCulture),
                    Convert.ToInt32(rule.Action, CultureInfo.InvariantCulture),
                    Convert.ToInt32(rule.Profiles, CultureInfo.InvariantCulture));
            }
            finally
            {
                ReleaseCom(ruleObject);
            }
        }
        return result;
    }

    private static void AddRuleForExecutables(List<NetworkFirewallRule> rules, string group, string label, string addresses, int protocol, string? ports, params string[] executables)
    {
        for (var index = 0; index < executables.Length; index++)
        {
            var fileName = Path.GetFileNameWithoutExtension(executables[index]);
            var role = index == 0
                ? "emulator"
                : fileName.Contains("headless", StringComparison.OrdinalIgnoreCase) ? "qemu-headless" : "qemu";
            rules.Add(new NetworkFirewallRule($"{group} - {role} - {label}", executables[index], addresses, protocol, ports));
        }
    }

    private static string BuildGuestIpv4Filter(bool blockHost, bool blockPrivate, bool blockLinkLocal, bool blockMulticast, bool allowHostProxy, int proxyPort)
    {
        var commands = new List<string>
        {
            "iptables -w 2 -N REPLAYER_OUT 2>/dev/null || true",
            "iptables -w 2 -F REPLAYER_OUT",
            "iptables -w 2 -C OUTPUT -j REPLAYER_OUT 2>/dev/null || iptables -w 2 -I OUTPUT 1 -j REPLAYER_OUT",
            "iptables -w 2 -A REPLAYER_OUT -d 10.0.2.3/32 -p udp --dport 53 -j RETURN",
            "iptables -w 2 -A REPLAYER_OUT -d 10.0.2.3/32 -p tcp --dport 53 -j RETURN"
        };
        if (allowHostProxy)
            commands.Add($"iptables -w 2 -A REPLAYER_OUT -d 10.0.2.2/32 -p tcp --dport {proxyPort} -j RETURN");
        if (blockHost) commands.Add("iptables -w 2 -A REPLAYER_OUT -d 10.0.2.2/32 -j REJECT");
        if (blockPrivate)
        {
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 10.0.0.0/8 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 100.64.0.0/10 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 172.16.0.0/12 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 192.168.0.0/16 -j REJECT");
        }
        if (blockLinkLocal)
        {
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 0.0.0.0/8 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 169.254.0.0/16 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 192.0.0.0/24 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 198.18.0.0/15 -j REJECT");
        }
        if (blockMulticast)
        {
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 224.0.0.0/4 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 240.0.0.0/4 -j REJECT");
            commands.Add("iptables -w 2 -A REPLAYER_OUT -d 255.255.255.255/32 -j REJECT");
        }
        commands.Add("iptables -w 2 -C OUTPUT -j REPLAYER_OUT");
        return string.Join(" && ", commands);
    }

    private static string BuildGuestIpv6Filter(bool blockHost, bool blockPrivate, bool blockLinkLocal, bool blockMulticast)
    {
        var commands = new List<string>
        {
            "ip6tables -w 2 -N REPLAYER_OUT 2>/dev/null || true",
            "ip6tables -w 2 -F REPLAYER_OUT",
            "ip6tables -w 2 -C OUTPUT -j REPLAYER_OUT 2>/dev/null || ip6tables -w 2 -I OUTPUT 1 -j REPLAYER_OUT"
        };
        if (blockHost)
        {
            commands.Add("ip6tables -w 2 -A REPLAYER_OUT -d ::/0 -j REJECT");
        }
        else
        {
            if (blockPrivate) commands.Add("ip6tables -w 2 -A REPLAYER_OUT -d fc00::/7 -j REJECT");
            if (blockLinkLocal) commands.Add("ip6tables -w 2 -A REPLAYER_OUT -d fe80::/10 -j REJECT");
            if (blockMulticast) commands.Add("ip6tables -w 2 -A REPLAYER_OUT -d ff00::/8 -j REJECT");
        }
        commands.Add("ip6tables -w 2 -C OUTPUT -j REPLAYER_OUT");
        return string.Join(" && ", commands);
    }

    private static IReadOnlyList<string> ParseDnsServers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var token in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(token, out var address))
                throw new InvalidOperationException($"Invalid DNS server IP address: {token}");
            var normalized = address.ToString();
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) result.Add(normalized);
            if (result.Count > 4) throw new InvalidOperationException("At most four DNS server addresses are supported.");
        }
        return result;
    }

    private static string ExcludingPort(int port)
    {
        if (port <= 1) return "2-65535";
        if (port >= 65535) return "1-65534";
        return $"1-{port - 1},{port + 1}-65535";
    }

    private static bool PathEquals(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool AddressListEquals(string? left, string? right) => SetEquals(left, right, addressList: true);
    private static bool PortListEquals(string? left, string? right) => SetEquals(left, right, addressList: false);

    private static bool SetEquals(string? left, string? right, bool addressList)
    {
        static string[] Split(string? value) => (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var a = Split(left);
        var b = Split(right);
        if (addressList)
        {
            a = a.Select(NormalizeAddressToken).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            b = b.Select(NormalizeAddressToken).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        else
        {
            a = a.Where(x => x is not "*" and not "any").ToArray();
            b = b.Where(x => x is not "*" and not "any").ToArray();
        }
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static string NormalizeAddressToken(string token)
    {
        token = token.Trim().ToLowerInvariant();
        if (token == "any") return "*";
        var slash = token.IndexOf('/');
        if (slash <= 0 || slash == token.Length - 1) return token;

        var addressText = token[..slash];
        var suffix = token[(slash + 1)..];
        if (!IPAddress.TryParse(addressText, out var address)) return token;
        if (int.TryParse(suffix, out var prefix)) return $"{address}/{prefix}";
        if (!IPAddress.TryParse(suffix, out var mask)) return token;

        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != maskBytes.Length) return token;
        var prefixLength = 0;
        var sawZero = false;
        foreach (var value in maskBytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isOne = (value & (1 << bit)) != 0;
                if (isOne)
                {
                    if (sawZero) return token;
                    prefixLength++;
                }
                else
                {
                    sawZero = true;
                }
            }
        }
        return $"{address}/{prefixLength}";
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static object CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)
                   ?? throw new InvalidOperationException("Windows Firewall COM type is unavailable: " + progId);
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException("Could not create Windows Firewall COM object: " + progId);
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    private static void WriteWorkerStatus(string baseDir, OfficialEmulatorNetworkPlan plan, bool success, string message)
    {
        var directory = Path.Combine(baseDir, "runtime", "network-policy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "status.json"), JsonSerializer.Serialize(new
        {
            updatedAtUtc = DateTime.UtcNow,
            success,
            message,
            plan.Mode,
            plan.RuleGroup,
            plan.Fingerprint,
            ruleCount = plan.FirewallRules.Count
        }, JsonOptions));
    }

    private static void WriteWorkerFailure(string baseDir, Exception exception)
    {
        var directory = Path.Combine(baseDir, "runtime", "network-policy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "status.json"), JsonSerializer.Serialize(new
        {
            updatedAtUtc = DateTime.UtcNow,
            success = false,
            message = exception.Message,
            detail = exception.ToString()
        }, JsonOptions));
    }

    private static string? ReadWorkerFailureMessage(string baseDir)
    {
        try
        {
            var path = Path.Combine(baseDir, "runtime", "network-policy", "status.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ActualFirewallRule(
        string Name,
        string ApplicationName,
        string RemoteAddresses,
        int Protocol,
        string? RemotePorts,
        bool Enabled,
        int Direction,
        int Action,
        int Profiles);
}
