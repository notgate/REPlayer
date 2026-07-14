using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.Automation;

public enum AiAgentProviderKind
{
    OpenRouter,
    OpenAI,
    Anthropic,
    Zai,
}

public enum AiAgentTaskStatus
{
    Pending,
    Running,
    Completed,
    Incomplete,
    Failed,
    Cancelled,
}

public sealed record AiAgentProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AiAgentProviderKind Provider { get; init; }
    public required string Model { get; init; }
    public required string BaseUrl { get; init; }
    public int MaximumTurns { get; init; } = 12;

    public string ProviderDisplayName => AgentProviderCatalog.DisplayName(Provider);
    public string DisplayName => $"{Name}  ·  {ProviderDisplayName} / {Model}";
}

public static class AgentProviderCatalog
{
    public static IReadOnlyList<AiAgentProviderKind> All { get; } =
        new[] { AiAgentProviderKind.OpenRouter, AiAgentProviderKind.OpenAI, AiAgentProviderKind.Anthropic, AiAgentProviderKind.Zai };

    public static string DisplayName(AiAgentProviderKind provider) => provider switch
    {
        AiAgentProviderKind.OpenRouter => "OpenRouter",
        AiAgentProviderKind.OpenAI => "OpenAI / ChatGPT API",
        AiAgentProviderKind.Anthropic => "Anthropic / Claude",
        AiAgentProviderKind.Zai => "Z.AI / GLM",
        _ => provider.ToString(),
    };

    public static string DefaultBaseUrl(AiAgentProviderKind provider) => provider switch
    {
        AiAgentProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
        AiAgentProviderKind.OpenAI => "https://api.openai.com/v1",
        AiAgentProviderKind.Anthropic => "https://api.anthropic.com/v1",
        AiAgentProviderKind.Zai => "https://api.z.ai/api/paas/v4",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string DefaultModel(AiAgentProviderKind provider) => provider switch
    {
        AiAgentProviderKind.OpenRouter => "tencent/hy3:free",
        AiAgentProviderKind.OpenAI => "gpt-5.2",
        AiAgentProviderKind.Anthropic => "claude-sonnet-4-5",
        AiAgentProviderKind.Zai => "glm-5.2",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}

public sealed record AiAgentTaskRequest
{
    public required string TaskId { get; init; }
    public required string AgentId { get; init; }
    public required string DeviceSerial { get; init; }
    public required string Prompt { get; init; }
    public string? AppPackage { get; init; }
    public AndroidAgentAccess Access { get; init; } = AndroidAgentAccess.Observe;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumTurns { get; init; } = 12;
}

public sealed record AiAgentToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string RawArguments { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public AndroidAgentAccess Access { get; init; } = AndroidAgentAccess.Observe;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string? Error { get; init; }
}

public sealed record AiAgentConversationMessage
{
    public required string Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? ToolCallId { get; init; }
    public IReadOnlyList<AiAgentToolCall> ToolCalls { get; init; } = Array.Empty<AiAgentToolCall>();
    public JsonElement? ReasoningDetails { get; init; }
}

public sealed record AiAgentProviderTurn
{
    public required AiAgentConversationMessage Assistant { get; init; }
}

public interface IAiAgentProviderClient
{
    Task<AiAgentProviderTurn> CompleteAsync(
        AiAgentProfile profile,
        string apiKey,
        IReadOnlyList<AiAgentConversationMessage> messages,
        CancellationToken cancellationToken);
}

public interface IAiAgentProviderClientFactory
{
    IAiAgentProviderClient Create(AiAgentProviderKind provider);
}

public sealed record AiAgentTaskSnapshot
{
    public required string TaskId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentName { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string DeviceSerial { get; init; }
    public required string Prompt { get; init; }
    public required AiAgentTaskStatus Status { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public string Report { get; init; } = string.Empty;
    public string? Error { get; init; }
    public string EvidencePath { get; init; } = string.Empty;
    public int ToolRunCount { get; init; }
}

public sealed record AiAgentTaskResult
{
    public required string TaskId { get; init; }
    public required AiAgentTaskStatus Status { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required string EvidencePath { get; init; }
    public required IReadOnlyList<AndroidAgentRunResult> ToolRuns { get; init; }
    public string Report { get; init; } = string.Empty;
    public string? Error { get; init; }
}
