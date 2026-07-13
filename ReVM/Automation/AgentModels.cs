using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.Automation;

public enum AndroidAgentAccess
{
    Observe,
    DeviceControl
}

public enum AndroidAgentRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public sealed record AndroidAgentStep
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public AndroidAgentAccess Access { get; init; } = AndroidAgentAccess.Observe;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool ContinueOnFailure { get; init; }
}

public sealed record AndroidAgentPlan
{
    public required string AgentId { get; init; }
    public required string DeviceSerial { get; init; }
    public string? AppPackage { get; init; }
    public required IReadOnlyList<AndroidAgentStep> Steps { get; init; }
}

public sealed record AndroidAgentCommandResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public bool TimedOut { get; init; }
}

public sealed record AndroidAgentStepResult
{
    public required string Name { get; init; }
    public required AndroidAgentAccess Access { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required AndroidAgentCommandResult Command { get; init; }
}

public sealed record AndroidAgentRunResult
{
    public required string RunId { get; init; }
    public required string AgentId { get; init; }
    public required string DeviceSerial { get; init; }
    public string? AppPackage { get; init; }
    public required AndroidAgentRunStatus Status { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required IReadOnlyList<AndroidAgentStepResult> Steps { get; init; }
    public required string EvidencePath { get; init; }
    public string? Error { get; init; }
}

public sealed record AndroidAgentEvent
{
    public required string RunId { get; init; }
    public required string AgentId { get; init; }
    public required string DeviceSerial { get; init; }
    public required AndroidAgentRunStatus Status { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public string? Step { get; init; }
    public string? Message { get; init; }
}

public interface IAndroidAgentTransport
{
    Task<AndroidAgentCommandResult> ExecuteAsync(
        string deviceSerial,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
