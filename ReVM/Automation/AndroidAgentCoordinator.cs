using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.Automation;

public sealed partial class AndroidAgentCoordinator : IAsyncDisposable
{
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);
    private readonly IAndroidAgentTransport _transport;
    private readonly string _evidenceRoot;
    private readonly SemaphoreSlim _globalSlots;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceControlLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();

    public AndroidAgentCoordinator(IAndroidAgentTransport transport, string evidenceRoot, int maximumConcurrentAgents = 4)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _evidenceRoot = Path.GetFullPath(evidenceRoot ?? throw new ArgumentNullException(nameof(evidenceRoot)));
        if (maximumConcurrentAgents is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentAgents));
        _globalSlots = new SemaphoreSlim(maximumConcurrentAgents, maximumConcurrentAgents);
        Directory.CreateDirectory(_evidenceRoot);
    }

    public event Action<AndroidAgentEvent>? StateChanged;

    public async Task<AndroidAgentRunResult> RunAsync(AndroidAgentPlan plan, CancellationToken cancellationToken = default)
    {
        Validate(plan);
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var runDirectory = Path.Combine(_evidenceRoot, runId);
        Directory.CreateDirectory(runDirectory);
        var evidencePath = Path.Combine(runDirectory, "events.jsonl");
        var started = DateTimeOffset.UtcNow;
        var results = new List<AndroidAgentStepResult>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var enteredGlobal = false;

        await RecordAsync(evidencePath, new
        {
            type = "run.queued",
            runId,
            plan.AgentId,
            plan.DeviceSerial,
            plan.AppPackage,
            stepCount = plan.Steps.Count,
            timestampUtc = started,
        }).ConfigureAwait(false);
        Publish(runId, plan, AndroidAgentRunStatus.Queued, null, "Queued");

        try
        {
            await _globalSlots.WaitAsync(linked.Token).ConfigureAwait(false);
            enteredGlobal = true;
            Publish(runId, plan, AndroidAgentRunStatus.Running, null, "Running");
            await RecordAsync(evidencePath, new { type = "run.started", runId, timestampUtc = DateTimeOffset.UtcNow }).ConfigureAwait(false);

            foreach (var step in plan.Steps)
            {
                linked.Token.ThrowIfCancellationRequested();
                var deviceLock = step.Access == AndroidAgentAccess.DeviceControl
                    ? _deviceControlLocks.GetOrAdd(plan.DeviceSerial, static _ => new SemaphoreSlim(1, 1))
                    : null;
                var enteredDevice = false;
                try
                {
                    if (deviceLock is not null)
                    {
                        await deviceLock.WaitAsync(linked.Token).ConfigureAwait(false);
                        enteredDevice = true;
                    }

                    Publish(runId, plan, AndroidAgentRunStatus.Running, step.Name, "Executing");
                    await RecordAsync(evidencePath, new
                    {
                        type = "step.started",
                        runId,
                        step = step.Name,
                        access = step.Access.ToString(),
                        arguments = step.Arguments,
                        timeoutMs = (long)step.Timeout.TotalMilliseconds,
                        timestampUtc = DateTimeOffset.UtcNow,
                    }).ConfigureAwait(false);

                    var command = await _transport.ExecuteAsync(
                        plan.DeviceSerial,
                        step.Arguments,
                        step.Timeout,
                        linked.Token).ConfigureAwait(false);
                    var stepResult = new AndroidAgentStepResult
                    {
                        Name = step.Name,
                        Access = step.Access,
                        Arguments = step.Arguments,
                        Command = command,
                    };
                    results.Add(stepResult);
                    await RecordAsync(evidencePath, new { type = "step.completed", runId, result = stepResult }).ConfigureAwait(false);

                    if (command.TimedOut)
                        return await CompleteAsync(AndroidAgentRunStatus.TimedOut, $"Step timed out: {step.Name}").ConfigureAwait(false);
                    if (command.ExitCode != 0 && !step.ContinueOnFailure)
                        return await CompleteAsync(AndroidAgentRunStatus.Failed, $"Step failed ({command.ExitCode}): {step.Name}").ConfigureAwait(false);
                }
                finally
                {
                    if (enteredDevice) deviceLock!.Release();
                }
            }

            return await CompleteAsync(AndroidAgentRunStatus.Succeeded, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CompleteAsync(AndroidAgentRunStatus.Cancelled, "Cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await CompleteAsync(AndroidAgentRunStatus.Failed, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            if (enteredGlobal) _globalSlots.Release();
        }

        async Task<AndroidAgentRunResult> CompleteAsync(AndroidAgentRunStatus status, string? error)
        {
            var ended = DateTimeOffset.UtcNow;
            var result = new AndroidAgentRunResult
            {
                RunId = runId,
                AgentId = plan.AgentId,
                DeviceSerial = plan.DeviceSerial,
                AppPackage = plan.AppPackage,
                Status = status,
                StartedAtUtc = started,
                EndedAtUtc = ended,
                Steps = results,
                EvidencePath = evidencePath,
                Error = error,
            };
            await RecordAsync(evidencePath, new { type = "run.completed", result }).ConfigureAwait(false);
            Publish(runId, plan, status, null, error ?? status.ToString());
            return result;
        }
    }

    private static void Validate(AndroidAgentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IdentifierPattern().IsMatch(plan.AgentId ?? string.Empty))
            throw new ArgumentException("Agent ID may contain only letters, numbers, dot, underscore, and hyphen.", nameof(plan));
        if (!SerialPattern().IsMatch(plan.DeviceSerial ?? string.Empty))
            throw new ArgumentException("Device serial contains unsupported characters.", nameof(plan));
        if (plan.AppPackage is not null && !PackagePattern().IsMatch(plan.AppPackage))
            throw new ArgumentException("App package is not a valid Android package name.", nameof(plan));
        if (plan.Steps is null || plan.Steps.Count is < 1 or > 100)
            throw new ArgumentException("An agent plan must contain between 1 and 100 steps.", nameof(plan));

        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name) || step.Name.Length > 96 || step.Name.Any(char.IsControl))
                throw new ArgumentException("Step names must be printable and no longer than 96 characters.", nameof(plan));
            if (step.Arguments is null || step.Arguments.Count is < 1 or > 256)
                throw new ArgumentException($"Step {step.Name} must contain between 1 and 256 ADB arguments.", nameof(plan));
            if (step.Arguments.Any(argument => string.IsNullOrEmpty(argument) || argument.Length > 4096 || argument.Any(char.IsControl)))
                throw new ArgumentException($"Step {step.Name} contains an invalid ADB argument.", nameof(plan));
            if (step.Timeout < TimeSpan.FromSeconds(1) || step.Timeout > TimeSpan.FromMinutes(10))
                throw new ArgumentException($"Step {step.Name} timeout must be between 1 second and 10 minutes.", nameof(plan));
        }
    }

    private static async Task RecordAsync(string evidencePath, object payload)
    {
        var line = JsonSerializer.Serialize(payload, EvidenceJson) + Environment.NewLine;
        await File.AppendAllTextAsync(evidencePath, line, CancellationToken.None).ConfigureAwait(false);
    }

    private void Publish(string runId, AndroidAgentPlan plan, AndroidAgentRunStatus status, string? step, string? message)
    {
        var update = new AndroidAgentEvent
        {
            RunId = runId,
            AgentId = plan.AgentId,
            DeviceSerial = plan.DeviceSerial,
            Status = status,
            TimestampUtc = DateTimeOffset.UtcNow,
            Step = step,
            Message = message,
        };
        try { StateChanged?.Invoke(update); }
        catch { /* Observers cannot break execution. */ }
    }

    public ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _globalSlots.Dispose();
        foreach (var gate in _deviceControlLocks.Values) gate.Dispose();
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();
}
