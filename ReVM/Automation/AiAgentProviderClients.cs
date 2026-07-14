using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.Automation;

public sealed class AiAgentProviderClientFactory : IAiAgentProviderClientFactory, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public AiAgentProviderClientFactory(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = httpClient is null;
    }

    public IAiAgentProviderClient Create(AiAgentProviderKind provider) => provider == AiAgentProviderKind.Anthropic
        ? new AnthropicAgentClient(_http)
        : new OpenAiCompatibleAgentClient(_http);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public sealed class OpenAiCompatibleAgentClient(HttpClient http) : IAiAgentProviderClient
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<AiAgentProviderTurn> CompleteAsync(
        AiAgentProfile profile,
        string apiKey,
        IReadOnlyList<AiAgentConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        Validate(profile, apiKey, messages);
        var payload = new JsonObject
        {
            ["model"] = profile.Model,
            ["messages"] = BuildMessages(messages),
            ["tools"] = BuildOpenAiTools(),
            ["tool_choice"] = "auto",
        };
        if (profile.Provider == AiAgentProviderKind.OpenRouter)
            payload["reasoning"] = new JsonObject { ["enabled"] = true };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(profile.BaseUrl, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        var body = await ReadBoundedBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw ProviderError("OpenAI-compatible", response.StatusCode.ToString(), body, apiKey);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message))
            throw new InvalidOperationException("Provider response did not contain choices[0].message.");

        var content = ReadTextContent(message);
        var calls = new List<AiAgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCalls.EnumerateArray()) calls.Add(ParseOpenAiToolCall(toolCall));
        }
        JsonElement? reasoningDetails = message.TryGetProperty("reasoning_details", out var reasoning)
            ? reasoning.Clone()
            : null;
        return new AiAgentProviderTurn
        {
            Assistant = new AiAgentConversationMessage
            {
                Role = "assistant",
                Content = content,
                ToolCalls = calls,
                ReasoningDetails = reasoningDetails,
            },
        };
    }

    private static JsonArray BuildMessages(IReadOnlyList<AiAgentConversationMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject { ["role"] = message.Role, ["content"] = message.Content };
            if (message.Role == "tool") node["tool_call_id"] = message.ToolCallId;
            if (message.Role == "assistant")
            {
                if (message.ReasoningDetails is JsonElement reasoning)
                    node["reasoning_details"] = JsonNode.Parse(reasoning.GetRawText());
                if (message.ToolCalls.Count > 0)
                {
                    node["tool_calls"] = new JsonArray(message.ToolCalls.Select(call => (JsonNode)new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.RawArguments },
                    }).ToArray());
                }
            }
            result.Add(node);
        }
        return result;
    }

    private static AiAgentToolCall ParseOpenAiToolCall(JsonElement call)
    {
        var id = call.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? string.Empty : string.Empty;
        if (!call.TryGetProperty("function", out var function))
            return InvalidCall(id, string.Empty, string.Empty, "Tool call did not contain a function object.");
        var name = function.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
        var raw = function.TryGetProperty("arguments", out var argumentsNode) ? argumentsNode.GetString() ?? "{}" : "{}";
        return ParseToolCall(id, name, raw);
    }

    internal static AiAgentToolCall ParseToolCall(string id, string name, string raw)
    {
        if (string.IsNullOrWhiteSpace(id)) return InvalidCall(id, name, raw, "Tool call ID is missing.");
        if (!string.Equals(name, "execute_adb", StringComparison.Ordinal))
            return InvalidCall(id, name, raw, "Only execute_adb is available.");
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("arguments", out var argumentsNode) || argumentsNode.ValueKind != JsonValueKind.Array)
                return InvalidCall(id, name, raw, "execute_adb requires an arguments array.");
            var arguments = argumentsNode.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            if (arguments.Length is < 1 or > 256 || arguments.Any(argument => string.IsNullOrEmpty(argument) || argument.Length > 4096 || argument.Any(char.IsControl)))
                return InvalidCall(id, name, raw, "execute_adb arguments are invalid.");
            var access = AdbAgentCommandPolicy.IsObserveOnly(arguments, out _)
                ? AndroidAgentAccess.Observe
                : AndroidAgentAccess.DeviceControl;
            var timeoutSeconds = root.TryGetProperty("timeout_seconds", out var timeoutNode) && timeoutNode.TryGetInt32(out var timeout)
                ? timeout
                : 30;
            if (timeoutSeconds is < 1 or > 600) throw new InvalidOperationException("timeout_seconds must be between 1 and 600.");
            return new AiAgentToolCall
            {
                Id = id,
                Name = name,
                RawArguments = raw,
                Arguments = arguments,
                Access = access,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return InvalidCall(id, name, raw, ex.Message);
        }
    }

    private static AiAgentToolCall InvalidCall(string id, string name, string raw, string error) => new()
    {
        Id = string.IsNullOrWhiteSpace(id) ? "invalid-" + Guid.NewGuid().ToString("N") : id,
        Name = name,
        RawArguments = raw,
        Error = error,
    };

    private static JsonArray BuildOpenAiTools() => new((JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "execute_adb",
            ["description"] = "Execute bundled ADB arguments against the active REPlayer Android device. Pass arguments only; no adb executable, device selector, or host shell.",
            ["parameters"] = ToolInputSchema(),
        },
    });

    internal static JsonObject ToolInputSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["arguments"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1, ["maxItems"] = 256 },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 600 },
        },
        ["required"] = new JsonArray("arguments"),
    };

    private static string ReadTextContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Concat(content.EnumerateArray()
            .Where(part => part.TryGetProperty("type", out var type) && type.GetString() is "text" or "output_text")
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : string.Empty));
    }

    internal static Uri Endpoint(string baseUrl, string suffix)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith('/' + suffix, StringComparison.OrdinalIgnoreCase)) return new Uri(normalized, UriKind.Absolute);
        return new Uri(normalized + '/' + suffix, UriKind.Absolute);
    }

    internal static void Validate(AiAgentProfile profile, string apiKey, IReadOnlyList<AiAgentConversationMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("This agent does not have an API key in Windows Credential Manager.");
        if (messages is null || messages.Count == 0) throw new ArgumentException("Conversation cannot be empty.", nameof(messages));
    }

    internal static InvalidOperationException ProviderError(string provider, string status, string body, string apiKey)
    {
        var compact = body.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (compact.Length > 2048) compact = compact[..2048] + "…";
        return new InvalidOperationException($"{provider} request failed ({status}): {compact}");
    }

    internal static async Task<string> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new System.IO.MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes)
                throw new InvalidOperationException("Provider response exceeded the 2 MiB safety limit.");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}

public sealed class AnthropicAgentClient(HttpClient http) : IAiAgentProviderClient
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<AiAgentProviderTurn> CompleteAsync(
        AiAgentProfile profile,
        string apiKey,
        IReadOnlyList<AiAgentConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        OpenAiCompatibleAgentClient.Validate(profile, apiKey, messages);
        var system = string.Join("\n\n", messages.Where(message => message.Role == "system").Select(message => message.Content));
        var payload = new JsonObject
        {
            ["model"] = profile.Model,
            ["max_tokens"] = 4096,
            ["system"] = system,
            ["messages"] = BuildMessages(messages.Where(message => message.Role != "system")),
            ["tools"] = new JsonArray((JsonNode)new JsonObject
            {
                ["name"] = "execute_adb",
                ["description"] = "Execute bundled ADB arguments against the active REPlayer Android device. No host shell is available.",
                ["input_schema"] = OpenAiCompatibleAgentClient.ToolInputSchema(),
            }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleAgentClient.Endpoint(profile.BaseUrl, "messages"));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        var body = await OpenAiCompatibleAgentClient.ReadBoundedBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw OpenAiCompatibleAgentClient.ProviderError("Anthropic", response.StatusCode.ToString(), body, apiKey);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Anthropic response did not contain a content array.");
        var text = new StringBuilder();
        var calls = new List<AiAgentToolCall>();
        var reasoningBlocks = new JsonArray();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : string.Empty;
            if (type == "text" && block.TryGetProperty("text", out var textNode)) text.Append(textNode.GetString());
            else if (type == "tool_use")
            {
                var id = block.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? string.Empty : string.Empty;
                var name = block.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
                var raw = block.TryGetProperty("input", out var inputNode) ? inputNode.GetRawText() : "{}";
                calls.Add(OpenAiCompatibleAgentClient.ParseToolCall(id, name, raw));
            }
            else if (type is "thinking" or "redacted_thinking")
                reasoningBlocks.Add(JsonNode.Parse(block.GetRawText()));
        }
        JsonElement? reasoningDetails = null;
        if (reasoningBlocks.Count > 0)
        {
            using var reasoningDocument = JsonDocument.Parse(reasoningBlocks.ToJsonString());
            reasoningDetails = reasoningDocument.RootElement.Clone();
        }
        return new AiAgentProviderTurn
        {
            Assistant = new AiAgentConversationMessage
            {
                Role = "assistant",
                Content = text.ToString(),
                ToolCalls = calls,
                ReasoningDetails = reasoningDetails,
            },
        };
    }

    private static JsonArray BuildMessages(IEnumerable<AiAgentConversationMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            var role = message.Role == "assistant" ? "assistant" : "user";
            var blocks = new JsonArray();
            if (message.Role == "assistant")
            {
                if (message.ReasoningDetails is JsonElement reasoning && reasoning.ValueKind == JsonValueKind.Array)
                    foreach (var block in reasoning.EnumerateArray()) blocks.Add(JsonNode.Parse(block.GetRawText()));
                if (!string.IsNullOrEmpty(message.Content)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                foreach (var call in message.ToolCalls)
                    blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = JsonNode.Parse(call.RawArguments) });
            }
            else if (message.Role == "tool")
            {
                blocks.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = message.ToolCallId, ["content"] = message.Content });
            }
            else
            {
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
            }
            AppendOrMerge(result, role, blocks);
        }
        return result;
    }

    private static void AppendOrMerge(JsonArray messages, string role, JsonArray blocks)
    {
        if (messages.Count > 0 && messages[^1] is JsonObject previous && previous["role"]?.GetValue<string>() == role && previous["content"] is JsonArray previousBlocks)
        {
            foreach (var block in blocks.ToArray()) previousBlocks.Add(block?.DeepClone());
            return;
        }
        messages.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
    }
}
