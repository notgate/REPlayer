using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReVM;
using ReVM.Automation;

if (args.Length >= 3 && args[0] == "--live-adb")
{
    await RunLiveAdbProbe(args[1], args[2], args.Length >= 4 ? args[3] : Path.Combine(Path.GetTempPath(), "replayer-live-agent-probe"));
    return;
}

var root = Path.Combine(Path.GetTempPath(), "replayer-agent-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var transport = new ProbeTransport();
await using var coordinator = new AndroidAgentCoordinator(transport, root, maximumConcurrentAgents: 8);

var legacySettings = BackendSettingsValidation.ValidateAndNormalize(new BackendSettings
{
    BootProfile = "api34-persona",
    AdbRoot = true,
});
Require(!legacySettings.AdbRoot && legacySettings.BootProfile == "api34-persona",
    "legacy AdbRoot semantics were not normalized");

AndroidAgentPlan Plan(string agent, string serial, string operation, AndroidAgentAccess access, int delayMs, int timeoutMs = 2000) =>
    new()
    {
        AgentId = agent,
        DeviceSerial = serial,
        AppPackage = "com.replayer.probe",
        Steps = new[]
        {
            new AndroidAgentStep
            {
                Name = operation,
                Arguments = new[] { operation, delayMs.ToString() },
                Access = access,
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            },
        },
    };

var sameDeviceControls = await Task.WhenAll(
    coordinator.RunAsync(Plan("control-a", "device-1", "control-a", AndroidAgentAccess.DeviceControl, 180)),
    coordinator.RunAsync(Plan("control-b", "device-1", "control-b", AndroidAgentAccess.DeviceControl, 180)));
Require(sameDeviceControls.All(result => result.Status == AndroidAgentRunStatus.Succeeded), "same-device controls failed");
Require(transport.MaximumFor("device-1", "control") == 1, "same-device controls overlapped");

var observations = await Task.WhenAll(
    coordinator.RunAsync(Plan("observe-a", "device-2", "observe-a", AndroidAgentAccess.Observe, 220)),
    coordinator.RunAsync(Plan("observe-b", "device-2", "observe-b", AndroidAgentAccess.Observe, 220)));
Require(observations.All(result => result.Status == AndroidAgentRunStatus.Succeeded), "observations failed");
Require(transport.MaximumFor("device-2", "observe") >= 2, "read-only observations did not overlap");

var crossDeviceControls = await Task.WhenAll(
    coordinator.RunAsync(Plan("cross-a", "device-3", "control-cross-a", AndroidAgentAccess.DeviceControl, 220)),
    coordinator.RunAsync(Plan("cross-b", "device-4", "control-cross-b", AndroidAgentAccess.DeviceControl, 220)));
Require(crossDeviceControls.All(result => result.Status == AndroidAgentRunStatus.Succeeded), "cross-device controls failed");
Require(transport.MaximumGlobalControls >= 2, "controls on different devices did not overlap");

var timeout = await coordinator.RunAsync(Plan("timeout", "device-5", "timeout", AndroidAgentAccess.Observe, 1500, 1000));
Require(timeout.Status == AndroidAgentRunStatus.TimedOut, "timeout was not classified");

using var cancel = new CancellationTokenSource(100);
var cancelled = await coordinator.RunAsync(
    Plan("cancel", "device-6", "cancel", AndroidAgentAccess.Observe, 1000), cancel.Token);
Require(cancelled.Status == AndroidAgentRunStatus.Cancelled, "cancellation was not classified");

var inboxRoot = Path.Combine(root, "file-protocol");
await using (var inbox = new AndroidAgentInbox(coordinator, inboxRoot, "device-inbox"))
{
    foreach (var requestId in new[] { "inbox-a", "inbox-b" })
    {
        var request = new
        {
            schema = 1,
            requestId,
            agentId = requestId,
            deviceSerial = "device-inbox",
            appPackage = "com.example.app",
            steps = new[] { new { name = "inspect", arguments = new[] { requestId, "180" }, access = "observe", timeoutSeconds = 2 } },
        };
        await File.WriteAllTextAsync(Path.Combine(inbox.InboxDirectory, requestId + ".json"), JsonSerializer.Serialize(request));
    }
    var cancelledRequest = new
    {
        schema = 1,
        requestId = "cancel-file",
        agentId = "cancel-file",
        deviceSerial = "device-inbox",
        steps = new[] { new { name = "cancel", arguments = new[] { "cancel-file", "1000" }, access = "observe", timeoutSeconds = 2 } },
    };
    await File.WriteAllTextAsync(Path.Combine(inbox.InboxDirectory, "cancel-file.cancel"), string.Empty);
    await File.WriteAllTextAsync(Path.Combine(inbox.InboxDirectory, "cancel-file.json"), JsonSerializer.Serialize(cancelledRequest));
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline && Directory.EnumerateFiles(inbox.OutboxDirectory, "*.result.json").Count() < 3)
        await Task.Delay(100);
    var results = Directory.EnumerateFiles(inbox.OutboxDirectory, "*.result.json").ToArray();
    Require(results.Length == 3, "JSON inbox did not emit all results");
    foreach (var path in results)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var verdict = document.RootElement.GetProperty("verdict").GetString();
        var expected = Path.GetFileName(path).StartsWith("cancel-file", StringComparison.Ordinal) ? "Cancelled" : "Succeeded";
        if (verdict != expected) Console.Error.WriteLine(await File.ReadAllTextAsync(path));
        Require(verdict == expected, "JSON inbox result had unexpected verdict");
    }
}

var allResults = sameDeviceControls.Concat(observations).Concat(crossDeviceControls).Append(timeout).Append(cancelled).ToArray();
foreach (var result in allResults)
{
    Require(File.Exists(result.EvidencePath), $"evidence missing for {result.RunId}");
    var lines = File.ReadAllLines(result.EvidencePath);
    Require(lines.Length >= 3, $"evidence incomplete for {result.RunId}");
    foreach (var line in lines) using (JsonDocument.Parse(line)) { }
    Require(lines[^1].Contains("run.completed", StringComparison.Ordinal), $"completion event missing for {result.RunId}");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    verdict = "PASS",
    sameDeviceControlMaximum = transport.MaximumFor("device-1", "control"),
    sameDeviceObserveMaximum = transport.MaximumFor("device-2", "observe"),
    crossDeviceControlMaximum = transport.MaximumGlobalControls,
    timeout = timeout.Status.ToString(),
    cancellation = cancelled.Status.ToString(),
    legacyAdbRootNormalized = !legacySettings.AdbRoot,
    inboxResults = 3,
    evidenceRuns = allResults.Length,
    evidenceRoot = root,
}, new JsonSerializerOptions { WriteIndented = true }));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task RunLiveAdbProbe(string adbPath, string serial, string evidenceRoot)
{
    await using var coordinator = new AndroidAgentCoordinator(new AdbAgentTransport(adbPath), evidenceRoot, maximumConcurrentAgents: 4);
    static AndroidAgentPlan Plan(string id, string targetSerial, AndroidAgentAccess access, params string[] arguments) => new()
    {
        AgentId = id,
        DeviceSerial = targetSerial,
        Steps = new[]
        {
            new AndroidAgentStep
            {
                Name = id,
                Arguments = arguments,
                Access = access,
                Timeout = TimeSpan.FromSeconds(30),
            },
        },
    };
    var runs = await Task.WhenAll(
        coordinator.RunAsync(Plan("live-model", serial, AndroidAgentAccess.Observe, "shell", "getprop", "ro.product.model")),
        coordinator.RunAsync(Plan("live-dark-mode", serial, AndroidAgentAccess.Observe, "shell", "settings", "get", "secure", "ui_night_mode")),
        coordinator.RunAsync(Plan("live-settings-launch", serial, AndroidAgentAccess.DeviceControl, "shell", "am", "start", "-a", "android.settings.SETTINGS")));
    Require(runs.All(run => run.Status == AndroidAgentRunStatus.Succeeded), "one or more live ADB agent runs failed");
    Require(runs[0].Steps[0].Command.StandardOutput.Trim() == "REPlayer Virtual Device", "live guest model is not neutral REPlayer");
    Require(runs[1].Steps[0].Command.StandardOutput.Trim() == "2", "live guest dark mode is not persistent");
    Require(runs.All(run => File.Exists(run.EvidencePath)), "live ADB evidence is incomplete");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        verdict = "PASS",
        serial,
        model = runs[0].Steps[0].Command.StandardOutput.Trim(),
        uiNightMode = runs[1].Steps[0].Command.StandardOutput.Trim(),
        settingsLaunchExitCode = runs[2].Steps[0].Command.ExitCode,
        evidence = runs.Select(run => run.EvidencePath).ToArray(),
    }, new JsonSerializerOptions { WriteIndented = true }));
}

internal sealed class ProbeTransport : IAndroidAgentTransport
{
    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _maximum = new(StringComparer.OrdinalIgnoreCase);
    private int _globalControls;
    private int _maximumGlobalControls;

    public int MaximumGlobalControls => Volatile.Read(ref _maximumGlobalControls);
    public int MaximumFor(string serial, string category) => _maximum.GetValueOrDefault(serial + ":" + category);

    public async Task<AndroidAgentCommandResult> ExecuteAsync(
        string deviceSerial,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var operation = arguments[0];
        var category = operation.StartsWith("control", StringComparison.Ordinal) ? "control" : "observe";
        var key = deviceSerial + ":" + category;
        var active = _active.AddOrUpdate(key, 1, static (_, current) => current + 1);
        _maximum.AddOrUpdate(key, active, (_, current) => Math.Max(current, active));
        if (category == "control")
        {
            var controls = Interlocked.Increment(ref _globalControls);
            UpdateMaximum(ref _maximumGlobalControls, controls);
        }

        var started = DateTimeOffset.UtcNow;
        var timedOut = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                await Task.Delay(int.Parse(arguments[1]), linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new AndroidAgentCommandResult
            {
                ExitCode = timedOut ? -1 : 0,
                StandardOutput = operation,
                StandardError = string.Empty,
                StartedAtUtc = started,
                EndedAtUtc = DateTimeOffset.UtcNow,
                TimedOut = timedOut,
            };
        }
        finally
        {
            _active.AddOrUpdate(key, 0, static (_, current) => current - 1);
            if (category == "control") Interlocked.Decrement(ref _globalControls);
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}
