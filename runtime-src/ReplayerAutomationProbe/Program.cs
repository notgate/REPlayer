using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ReVM;
using ReVM.Automation;

if (args.Length >= 1 && args[0] == "--live-openrouter")
{
    await RunLiveOpenRouterProbe(args.Length >= 2 ? args[1] : "tencent/hy3:free");
    return;
}

if (args.Length >= 2 && args[0] == "--ui")
{
    await RunAgentCenterUiProbe(args[1]);
    return;
}

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

var aiProbe = await RunAiAgentTaskProbe(root, coordinator);

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
    aiTaskStatus = aiProbe.Status.ToString(),
    aiProviderTurns = aiProbe.ProviderTurns,
    evidenceRuns = allResults.Length,
    evidenceRoot = root,
}, new JsonSerializerOptions { WriteIndented = true }));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task RunLiveOpenRouterProbe(string model)
{
    var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_API_KEY is required.");
    var profile = new AiAgentProfile
    {
        Id = "openrouter-live",
        Name = "OpenRouter live probe",
        Provider = AiAgentProviderKind.OpenRouter,
        Model = model,
        BaseUrl = AgentProviderCatalog.DefaultBaseUrl(AiAgentProviderKind.OpenRouter),
        MaximumTurns = 2,
    };
    using var factory = new AiAgentProviderClientFactory();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var turn = await factory.Create(profile.Provider).CompleteAsync(profile, key,
        new[]
        {
            new AiAgentConversationMessage { Role = "system", Content = "This is a connectivity probe. Do not call tools." },
            new AiAgentConversationMessage { Role = "user", Content = "Reply with REPLAYER_OPENROUTER_READY and no other text." },
        }, timeout.Token);
    Require(turn.Assistant.ToolCalls.Count == 0, "live provider unexpectedly requested an ADB tool");
    Require(!string.IsNullOrWhiteSpace(turn.Assistant.Content), "live provider returned no text");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        verdict = "PASS",
        provider = "OpenRouter",
        model,
        response = turn.Assistant.Content.Trim(),
        reasoningContinuityPayloadPresent = turn.Assistant.ReasoningDetails.HasValue,
    }, new JsonSerializerOptions { WriteIndented = true }));
}

static Task RunAgentCenterUiProbe(string surface)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        AndroidAgentCoordinator? coordinator = null;
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "replayer-agent-ui-probe");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            var credentials = new ProbeCredentialStore();
            var profiles = new AiAgentProfileStore(Path.Combine(root, "profiles.json"), credentials);
            var profile = new AiAgentProfile
            {
                Id = "hy3-free",
                Name = "Tencent HY3 Free",
                Provider = AiAgentProviderKind.OpenRouter,
                Model = "tencent/hy3:free",
                BaseUrl = "https://openrouter.ai/api/v1",
                MaximumTurns = 12,
            };
            profiles.Save(profile, "ui-probe-placeholder");
            var tasks = new AiAgentTaskStore(Path.Combine(root, "tasks"));
            foreach (var sample in new[]
                     {
                         ("task-complete", AiAgentTaskStatus.Completed, "Observed the active Android identity and captured the requested package state.", (string?)null),
                         ("task-incomplete", AiAgentTaskStatus.Incomplete, "", "The requested package was not installed on the active device."),
                         ("task-pending", AiAgentTaskStatus.Pending, "", (string?)null),
                     })
            {
                var evidenceDirectory = Path.Combine(tasks.Path, "evidence", sample.Item1);
                Directory.CreateDirectory(evidenceDirectory);
                var evidencePath = Path.Combine(evidenceDirectory, "events.jsonl");
                File.WriteAllLines(evidencePath, new[]
                {
                    JsonSerializer.Serialize(new { type = "task.pending", taskId = sample.Item1, timestampUtc = DateTimeOffset.UtcNow.AddSeconds(-2) }),
                    JsonSerializer.Serialize(new { type = "provider.turn", taskId = sample.Item1, turn = 1, content = "Inspecting device state", toolCallCount = 1, timestampUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }),
                    JsonSerializer.Serialize(new { type = "task.completed", taskId = sample.Item1, status = sample.Item2.ToString(), report = sample.Item3, error = sample.Item4, timestampUtc = DateTimeOffset.UtcNow }),
                });
                tasks.Save(new AiAgentTaskSnapshot
                {
                    TaskId = sample.Item1,
                    AgentId = profile.Id,
                    AgentName = profile.Name,
                    Provider = profile.ProviderDisplayName,
                    Model = profile.Model,
                    DeviceSerial = "emulator-5554",
                    Prompt = "Inspect the active Android identity and report evidence.",
                    Status = sample.Item2,
                    CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                    EndedAtUtc = sample.Item2 == AiAgentTaskStatus.Pending ? null : DateTimeOffset.UtcNow,
                    Report = sample.Item3,
                    Error = sample.Item4,
                    EvidencePath = evidencePath,
                    ToolRunCount = 1,
                });
            }

            coordinator = new AndroidAgentCoordinator(new ProbeTransport(), Path.Combine(root, "adb-evidence"), 4);
            var provider = new ProbeAiProviderClient();
            var providerFactory = new ProbeAiProviderFactory(provider);
            var runner = new AiAgentTaskRunner(coordinator, credentials, providerFactory, tasks);
            Window window = surface.Trim().ToLowerInvariant() switch
            {
                "setup" => new AgentSetupDialog(profiles, providerFactory, profile.Id),
                "inbox" => new AgentInboxWindow(tasks, Path.Combine(root, "devices", "emulator-5554", "inbox")),
                "evidence" => new AgentEvidenceWindow(tasks, "task-complete"),
                _ => new AgentCenterDialog(coordinator, runner, profiles, tasks, providerFactory, "emulator-5554", Path.Combine(root, "devices", "emulator-5554", "inbox")),
            };
            window.ShowInTaskbar = true;
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(window);
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            coordinator = null;
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            try { coordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            completion.TrySetException(ex);
        }
    }) { IsBackground = true, Name = "REPlayer Agent Center UI Probe" };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}

static async Task<(AiAgentTaskStatus Status, int ProviderTurns)> RunAiAgentTaskProbe(
    string root,
    AndroidAgentCoordinator coordinator)
{
    const string probeSecret = "probe-secret-never-persist";
    var agentRoot = Path.Combine(root, "ai-agent");
    var credentials = new ProbeCredentialStore();
    var profileStore = new AiAgentProfileStore(Path.Combine(agentRoot, "profiles.json"), credentials);
    var profile = new AiAgentProfile
    {
        Id = "hy3-probe",
        Name = "Tencent HY3 probe",
        Provider = AiAgentProviderKind.OpenRouter,
        Model = "tencent/hy3:free",
        BaseUrl = "https://openrouter.ai/api/v1",
        MaximumTurns = 4,
    };
    profileStore.Save(profile, probeSecret);
    Require(credentials.Read(profile.Id) == probeSecret, "agent credential was not stored separately");
    var profileJson = await File.ReadAllTextAsync(profileStore.Path);
    Require(!profileJson.Contains("probe-secret", StringComparison.Ordinal), "agent API key leaked into profile JSON");
    Require(profileStore.Load().Single().Model == "tencent/hy3:free", "agent profile did not round-trip");
    Require(AdbAgentCommandPolicy.IsObserveOnly(new[] { "shell", "getprop", "ro.product.model" }, out _),
        "observe policy rejected a read-only property query");
    Require(!AdbAgentCommandPolicy.IsObserveOnly(new[] { "shell", "rm", "/data/local/tmp/file" }, out _),
        "observe policy allowed a mutating shell command");
    Require(!AdbAgentCommandPolicy.IsObserveOnly(new[] { "shell", "getprop; rm /data/local/tmp/file" }, out _),
        "observe policy allowed shell operator injection");
    Require(!AdbAgentCommandPolicy.IsObserveOnly(new[] { "push", "file", "/data/local/tmp/file" }, out _),
        "observe policy allowed a mutating top-level ADB command");
    Require(!AdbAgentCommandPolicy.IsObserveOnly(new[] { "shell", "screencap", "-p", "/sdcard/capture.png" }, out _),
        "observe policy allowed screencap to write an Android file");
    Require(!AdbAgentCommandPolicy.IsObserveOnly(new[] { "shell", "dumpsys", "battery", "set", "level", "1" }, out _),
        "observe policy allowed a mutating dumpsys service verb");

    var provider = new ProbeAiProviderClient();
    var taskStore = new AiAgentTaskStore(Path.Combine(agentRoot, "tasks"));
    var runner = new AiAgentTaskRunner(coordinator, credentials, new ProbeAiProviderFactory(provider), taskStore);
    var result = await runner.RunAsync(new AiAgentTaskRequest
    {
        TaskId = "hy3-task",
        AgentId = profile.Id,
        DeviceSerial = "device-ai",
        Prompt = $"Inspect the Android product model and report it. Credential marker: {probeSecret}",
        Access = AndroidAgentAccess.Observe,
        Timeout = TimeSpan.FromSeconds(3),
        MaximumTurns = 4,
    }, profile);

    Require(result.Status == AiAgentTaskStatus.Completed, "AI agent task did not reach Completed");
    Require(result.Report == "Observed REPlayer Virtual Device. Credential echo: [REDACTED]", "AI agent final report was not redacted correctly");
    Require(provider.Turns == 2, "AI agent did not continue after the ADB tool result");
    Require(result.ToolRuns.Count == 1 && result.ToolRuns[0].Status == AndroidAgentRunStatus.Succeeded,
        "AI agent ADB tool run was not coordinated");
    Require(File.Exists(result.EvidencePath), "AI agent evidence file is missing");
    var evidence = await File.ReadAllTextAsync(result.EvidencePath);
    Require(!evidence.Contains("probe-secret", StringComparison.Ordinal), "AI agent evidence leaked its API key");
    Require(!evidence.Contains("private-reasoning-marker", StringComparison.Ordinal), "provider reasoning leaked into evidence");
    var persistedTask = taskStore.Load().Single();
    Require(persistedTask.Status == AiAgentTaskStatus.Completed, "AI agent terminal state was not persisted");
    Require(!persistedTask.Prompt.Contains(probeSecret, StringComparison.Ordinal), "AI agent task snapshot leaked its API key");
    return (result.Status, provider.Turns);
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
                var delayMilliseconds = arguments.Count > 1 && int.TryParse(arguments[1], out var parsedDelay) ? parsedDelay : 10;
                await Task.Delay(delayMilliseconds, linked.Token);
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

internal sealed class ProbeCredentialStore : IAgentCredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Read(string agentId) => _values.GetValueOrDefault(agentId);
    public void Write(string agentId, string secret) => _values[agentId] = secret;
    public void Delete(string agentId) => _values.Remove(agentId);
}

internal sealed class ProbeAiProviderFactory(IAiAgentProviderClient client) : IAiAgentProviderClientFactory
{
    public IAiAgentProviderClient Create(AiAgentProviderKind provider) => client;
}

internal sealed class ProbeAiProviderClient : IAiAgentProviderClient
{
    public int Turns { get; private set; }

    public Task<AiAgentProviderTurn> CompleteAsync(
        AiAgentProfile profile,
        string apiKey,
        IReadOnlyList<AiAgentConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        Turns++;
        if (Turns == 1)
        {
            return Task.FromResult(new AiAgentProviderTurn
            {
                Assistant = new AiAgentConversationMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ReasoningDetails = ReasoningMarker(),
                    ToolCalls = new[]
                    {
                        new AiAgentToolCall
                        {
                            Id = "tool-1",
                            Name = "execute_adb",
                            RawArguments = "{\"arguments\":[\"shell\",\"getprop\",\"ro.product.model\"],\"timeout_seconds\":2}",
                            Arguments = new[] { "shell", "getprop", "ro.product.model" },
                            Access = AndroidAgentAccess.Observe,
                            Timeout = TimeSpan.FromSeconds(2),
                        },
                    },
                },
            });
        }

        if (!messages.Any(message => message.Role == "tool" && message.Content.Contains("shell", StringComparison.Ordinal)))
            throw new InvalidOperationException("AI provider did not receive the ADB tool result");
        return Task.FromResult(new AiAgentProviderTurn
        {
            Assistant = new AiAgentConversationMessage
            {
                Role = "assistant",
                Content = "Observed REPlayer Virtual Device. Credential echo: " + apiKey,
                ReasoningDetails = ReasoningMarker(),
            },
        });
    }

    private static JsonElement ReasoningMarker()
    {
        using var document = JsonDocument.Parse("[\"private-reasoning-marker\"]");
        return document.RootElement.Clone();
    }
}
