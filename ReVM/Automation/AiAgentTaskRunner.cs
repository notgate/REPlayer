using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.Automation;

public sealed partial class AiAgentTaskStore
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AiAgentTaskStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public IReadOnlyList<AiAgentTaskSnapshot> Load()
    {
        var snapshots = new List<AiAgentTaskSnapshot>();
        foreach (var path in Directory.EnumerateFiles(Path, "*.task.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<AiAgentTaskSnapshot>(File.ReadAllText(path), _json);
                if (snapshot is not null) snapshots.Add(snapshot);
            }
            catch { }
        }
        return snapshots.OrderByDescending(snapshot => snapshot.CreatedAtUtc).ToArray();
    }

    public IReadOnlyList<AiAgentTaskSnapshot> RecoverInterrupted()
    {
        var snapshots = Load().ToArray();
        for (var index = 0; index < snapshots.Length; index++)
        {
            var snapshot = snapshots[index];
            if (snapshot.Status is not (AiAgentTaskStatus.Pending or AiAgentTaskStatus.Running)) continue;
            snapshot = snapshot with
            {
                Status = AiAgentTaskStatus.Incomplete,
                EndedAtUtc = DateTimeOffset.UtcNow,
                Error = "REPlayer closed before the agent reported a terminal result.",
            };
            Save(snapshot);
            snapshots[index] = snapshot;
        }
        return snapshots.OrderByDescending(snapshot => snapshot.CreatedAtUtc).ToArray();
    }

    public void Save(AiAgentTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TaskIdPattern().IsMatch(snapshot.TaskId ?? string.Empty)) throw new InvalidDataException("Task ID is invalid.");
        Directory.CreateDirectory(Path);
        var destination = System.IO.Path.Combine(Path, snapshot.TaskId + ".task.json");
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, _json) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskIdPattern();
}

public sealed partial class AiAgentTaskRunner
{
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);
    private const string SystemInstruction =
        "You are an REPlayer Android analysis agent. Complete the assigned task against the active Android device. " +
        "Use execute_adb whenever device evidence is required. Pass only ADB arguments; REPlayer supplies the executable and serial. " +
        "Respect the granted access level. Continue until the task is complete, or clearly report why it is incomplete. " +
        "Your final response must be a concise evidence-backed report; never claim an action that the tool output did not prove.";

    private readonly AndroidAgentCoordinator _coordinator;
    private readonly IAgentCredentialStore _credentials;
    private readonly IAiAgentProviderClientFactory _providers;
    private readonly AiAgentTaskStore _store;

    public AiAgentTaskRunner(
        AndroidAgentCoordinator coordinator,
        IAgentCredentialStore credentials,
        IAiAgentProviderClientFactory providers,
        AiAgentTaskStore store)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public event Action<AiAgentTaskSnapshot>? StateChanged;

    public async Task<AiAgentTaskResult> RunAsync(
        AiAgentTaskRequest request,
        AiAgentProfile profile,
        CancellationToken cancellationToken = default)
    {
        Validate(request, profile);
        var created = DateTimeOffset.UtcNow;
        var started = created;
        var toolRuns = new List<AndroidAgentRunResult>();
        var evidenceDirectory = System.IO.Path.Combine(_store.Path, "evidence", request.TaskId);
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = System.IO.Path.Combine(evidenceDirectory, "events.jsonl");
        var apiKey = _credentials.Read(profile.Id);
        var persistedPrompt = RedactSecret(request.Prompt, apiKey);
        SaveState(AiAgentTaskStatus.Pending, created, null, null, string.Empty, null, evidencePath, 0);
        await RecordAsync(evidencePath, new
        {
            type = "task.pending",
            request.TaskId,
            request.AgentId,
            profile.Name,
            provider = AgentProviderCatalog.DisplayName(profile.Provider),
            profile.Model,
            request.DeviceSerial,
            prompt = persistedPrompt,
            access = request.Access.ToString(),
            timestampUtc = created,
        }).ConfigureAwait(false);

        var messages = new List<AiAgentConversationMessage>
        {
            new() { Role = "system", Content = SystemInstruction },
            new() { Role = "user", Content = request.Prompt },
        };
        var maximumTurns = Math.Clamp(Math.Min(request.MaximumTurns, profile.MaximumTurns), 1, 64);
        var provider = _providers.Create(profile.Provider);
        if (string.IsNullOrWhiteSpace(apiKey))
            return await FinishAsync(AiAgentTaskStatus.Failed, string.Empty, "No API key is stored for this agent.").ConfigureAwait(false);

        started = DateTimeOffset.UtcNow;
        SaveState(AiAgentTaskStatus.Running, created, started, null, string.Empty, null, evidencePath, 0);
        await RecordAsync(evidencePath, new { type = "task.started", request.TaskId, timestampUtc = started }).ConfigureAwait(false);
        using var taskLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        taskLifetime.CancelAfter(request.Timeout);
        var taskToken = taskLifetime.Token;

        try
        {
            for (var turnNumber = 1; turnNumber <= maximumTurns; turnNumber++)
            {
                taskToken.ThrowIfCancellationRequested();
                var turn = await provider.CompleteAsync(profile, apiKey, messages, taskToken).ConfigureAwait(false);
                messages.Add(turn.Assistant);
                await RecordAsync(evidencePath, new
                {
                    type = "provider.turn",
                    request.TaskId,
                    turn = turnNumber,
                    content = RedactSecret(turn.Assistant.Content, apiKey),
                    toolCallCount = turn.Assistant.ToolCalls.Count,
                    // Provider reasoning is deliberately retained only in memory for protocol continuity.
                    timestampUtc = DateTimeOffset.UtcNow,
                }).ConfigureAwait(false);

                if (turn.Assistant.ToolCalls.Count == 0)
                {
                    var report = RedactSecret(turn.Assistant.Content.Trim(), apiKey);
                    return string.IsNullOrWhiteSpace(report)
                        ? await FinishAsync(AiAgentTaskStatus.Incomplete, string.Empty, "The provider returned no final report.").ConfigureAwait(false)
                        : await FinishAsync(AiAgentTaskStatus.Completed, report, null).ConfigureAwait(false);
                }

                foreach (var call in turn.Assistant.ToolCalls)
                {
                    taskToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(call.Error))
                    {
                        var safeCallError = RedactSecret(call.Error, apiKey);
                        var rejected = JsonSerializer.Serialize(new { ok = false, error = safeCallError }, EvidenceJson);
                        messages.Add(new AiAgentConversationMessage { Role = "tool", ToolCallId = call.Id, Content = rejected });
                        await RecordAsync(evidencePath, new { type = "tool.rejected", call.Id, call.Name, error = safeCallError, timestampUtc = DateTimeOffset.UtcNow }).ConfigureAwait(false);
                        continue;
                    }
                    if (ContainsSecret(call.RawArguments, apiKey) || call.Arguments.Any(argument => ContainsSecret(argument, apiKey)))
                    {
                        const string credentialError = "Tool call contained credential material and was blocked.";
                        messages.Add(new AiAgentConversationMessage { Role = "tool", ToolCallId = call.Id, Content = JsonSerializer.Serialize(new { ok = false, error = credentialError }, EvidenceJson) });
                        await RecordAsync(evidencePath, new { type = "tool.denied", call.Id, call.Name, error = credentialError, timestampUtc = DateTimeOffset.UtcNow }).ConfigureAwait(false);
                        continue;
                    }
                    if (request.Access == AndroidAgentAccess.Observe && call.Access == AndroidAgentAccess.DeviceControl)
                    {
                        const string accessError = "Task has observe-only access; device-control ADB was denied.";
                        messages.Add(new AiAgentConversationMessage { Role = "tool", ToolCallId = call.Id, Content = JsonSerializer.Serialize(new { ok = false, error = accessError }, EvidenceJson) });
                        await RecordAsync(evidencePath, new { type = "tool.denied", call.Id, call.Name, error = accessError, timestampUtc = DateTimeOffset.UtcNow }).ConfigureAwait(false);
                        continue;
                    }
                    if (call.Access == AndroidAgentAccess.Observe && !AdbAgentCommandPolicy.IsObserveOnly(call.Arguments, out var policyError))
                    {
                        messages.Add(new AiAgentConversationMessage { Role = "tool", ToolCallId = call.Id, Content = JsonSerializer.Serialize(new { ok = false, error = policyError }, EvidenceJson) });
                        await RecordAsync(evidencePath, new { type = "tool.denied", call.Id, call.Name, error = policyError, timestampUtc = DateTimeOffset.UtcNow }).ConfigureAwait(false);
                        continue;
                    }

                    var effectiveTimeout = call.Timeout <= request.Timeout ? call.Timeout : request.Timeout;
                    var plan = new AndroidAgentPlan
                    {
                        AgentId = request.AgentId,
                        DeviceSerial = request.DeviceSerial,
                        AppPackage = request.AppPackage,
                        Steps = new[]
                        {
                            new AndroidAgentStep
                            {
                                Name = "AI execute_adb",
                                Arguments = call.Arguments,
                                Access = call.Access,
                                Timeout = effectiveTimeout,
                            },
                        },
                    };
                    var run = await _coordinator.RunAsync(plan, taskToken).ConfigureAwait(false);
                    toolRuns.Add(run);
                    var command = run.Steps.LastOrDefault()?.Command;
                    var safeStandardOutput = RedactSecret(command?.StandardOutput ?? string.Empty, apiKey);
                    var safeStandardError = RedactSecret(command?.StandardError ?? run.Error ?? string.Empty, apiKey);
                    var toolResult = JsonSerializer.Serialize(new
                    {
                        ok = run.Status == AndroidAgentRunStatus.Succeeded,
                        status = run.Status.ToString(),
                        exitCode = command?.ExitCode,
                        stdout = safeStandardOutput,
                        stderr = safeStandardError,
                        evidencePath = run.EvidencePath,
                    }, EvidenceJson);
                    messages.Add(new AiAgentConversationMessage { Role = "tool", ToolCallId = call.Id, Content = toolResult });
                    await RecordAsync(evidencePath, new
                    {
                        type = "tool.completed",
                        call.Id,
                        call.Name,
                        arguments = call.Arguments.Select(argument => RedactSecret(argument, apiKey)).ToArray(),
                        access = call.Access.ToString(),
                        run.Status,
                        exitCode = command?.ExitCode,
                        stdout = safeStandardOutput,
                        stderr = safeStandardError,
                        run.EvidencePath,
                        timestampUtc = DateTimeOffset.UtcNow,
                    }).ConfigureAwait(false);
                    SaveState(AiAgentTaskStatus.Running, created, started, null, string.Empty, null, evidencePath, toolRuns.Count);
                }
            }

            return await FinishAsync(AiAgentTaskStatus.Incomplete, string.Empty, $"Agent reached the {maximumTurns}-turn limit before a final report.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync(AiAgentTaskStatus.Cancelled, string.Empty, "Cancelled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (taskLifetime.IsCancellationRequested)
        {
            return await FinishAsync(AiAgentTaskStatus.Incomplete, string.Empty,
                $"Task exceeded the {request.Timeout.TotalSeconds:0}-second limit.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(AiAgentTaskStatus.Failed, string.Empty, "Provider or tool operation timed out.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FinishAsync(AiAgentTaskStatus.Failed, string.Empty, RedactSecret(ex.Message, apiKey)).ConfigureAwait(false);
        }
        finally
        {
            apiKey = null;
        }

        async Task<AiAgentTaskResult> FinishAsync(AiAgentTaskStatus status, string report, string? error)
        {
            report = RedactSecret(report, apiKey);
            error = RedactSecret(error, apiKey);
            var ended = DateTimeOffset.UtcNow;
            var result = new AiAgentTaskResult
            {
                TaskId = request.TaskId,
                Status = status,
                StartedAtUtc = started,
                EndedAtUtc = ended,
                EvidencePath = evidencePath,
                ToolRuns = toolRuns.ToArray(),
                Report = report,
                Error = error,
            };
            await RecordAsync(evidencePath, new
            {
                type = "task.completed",
                request.TaskId,
                status = status.ToString(),
                report,
                error,
                toolRunCount = toolRuns.Count,
                timestampUtc = ended,
            }).ConfigureAwait(false);
            SaveState(status, created, started, ended, report, error, evidencePath, toolRuns.Count);
            return result;
        }

        void SaveState(
            AiAgentTaskStatus status,
            DateTimeOffset createdAt,
            DateTimeOffset? startedAt,
            DateTimeOffset? endedAt,
            string report,
            string? error,
            string path,
            int toolRunCount)
        {
            var snapshot = new AiAgentTaskSnapshot
            {
                TaskId = request.TaskId,
                AgentId = profile.Id,
                AgentName = profile.Name,
                Provider = AgentProviderCatalog.DisplayName(profile.Provider),
                Model = profile.Model,
                DeviceSerial = request.DeviceSerial,
                Prompt = persistedPrompt,
                Status = status,
                CreatedAtUtc = createdAt,
                StartedAtUtc = startedAt,
                EndedAtUtc = endedAt,
                Report = report,
                Error = error,
                EvidencePath = path,
                ToolRunCount = toolRunCount,
            };
            _store.Save(snapshot);
            try { StateChanged?.Invoke(snapshot); } catch { }
        }
    }

    private static bool ContainsSecret(string? value, string? secret) =>
        !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(secret) && value.Contains(secret, StringComparison.Ordinal);

    private static string RedactSecret(string? value, string? secret)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return string.IsNullOrEmpty(secret) ? value : value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    }

    private static async Task RecordAsync(string evidencePath, object payload)
    {
        var line = JsonSerializer.Serialize(payload, EvidenceJson) + Environment.NewLine;
        await File.AppendAllTextAsync(evidencePath, line, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
    }

    private static void Validate(AiAgentTaskRequest request, AiAgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);
        if (!TaskIdPattern().IsMatch(request.TaskId ?? string.Empty)) throw new ArgumentException("Task ID is invalid.", nameof(request));
        if (!AgentIdPattern().IsMatch(request.AgentId ?? string.Empty) || !string.Equals(request.AgentId, profile.Id, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Task agent does not match the selected profile.", nameof(request));
        if (!SerialPattern().IsMatch(request.DeviceSerial ?? string.Empty)) throw new ArgumentException("Device serial is invalid.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32_768 || request.Prompt.Any(character => character == '\0'))
            throw new ArgumentException("Task prompt must contain between 1 and 32768 characters.", nameof(request));
        if (request.Timeout < TimeSpan.FromSeconds(1) || request.Timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Task timeout must be between 1 second and 10 minutes.", nameof(request));
        if (request.MaximumTurns is < 1 or > 64) throw new ArgumentException("Maximum turns must be between 1 and 64.", nameof(request));
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();
}
