// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Tooling;

namespace CanX.Agent;

public enum AgentMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public sealed record AgentToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string ArgumentsJson { get; init; }
}

public sealed record AgentMessage
{
    public required AgentMessageRole Role { get; init; }

    public string? Content { get; init; }

    public string? ToolCallId { get; init; }

    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = [];

    public static AgentMessage User(string content) => new() { Role = AgentMessageRole.User, Content = content };

    public static AgentMessage Assistant(string? content, IReadOnlyList<AgentToolCall>? toolCalls = null) => new()
    {
        Role = AgentMessageRole.Assistant,
        Content = content,
        ToolCalls = toolCalls ?? [],
    };

    public static AgentMessage Tool(string toolCallId, string content) => new()
    {
        Role = AgentMessageRole.Tool,
        ToolCallId = toolCallId,
        Content = content,
    };
}

public sealed record AgentProviderConfiguration
{
    public required Uri BaseUri { get; init; }

    public required string Model { get; init; }

    public double Temperature { get; init; } = 0.2;

    // Zero means the OpenAI-compatible endpoint did not publish a context size.
    public int ContextWindowTokens { get; init; }

    public void Validate()
    {
        ValidateBaseUri(BaseUri);
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200) throw new ArgumentException("LLM model is required.");
        if (Temperature is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(Temperature));
        if (ContextWindowTokens is < 0 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(ContextWindowTokens));
    }

    public static void ValidateBaseUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("LLM Base URL must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }
        if (baseUri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(baseUri.Host))
        {
            throw new ArgumentException("LLM Base URL must use HTTPS unless it targets the local loopback interface.");
        }
        if (baseUri.AbsoluteUri.Length > 2048) throw new ArgumentException("LLM Base URL is too long.");
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost.", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
}

public sealed record AgentProviderRequest(
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDescriptor> Tools,
    AgentProviderConfiguration Configuration);

public sealed record AgentUsage(int InputTokens, int OutputTokens)
{
    public int TotalTokens => checked(InputTokens + OutputTokens);
}

public abstract record AgentProviderEvent;

public sealed record AgentProviderTextDelta(string Text) : AgentProviderEvent;

public sealed record AgentProviderCompleted(
    string? Content,
    IReadOnlyList<AgentToolCall> ToolCalls,
    AgentUsage Usage) : AgentProviderEvent;

public interface IAgentChatProvider
{
    IAsyncEnumerable<AgentProviderEvent> StreamAsync(
        AgentProviderRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAgentApiKeyProvider
{
    ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the key only when it may be sent to <paramref name="endpoint"/>. Providers that bind keys
    /// to the endpoint they were saved for must throw <see cref="AgentProviderException"/> on a mismatch.
    /// </summary>
    ValueTask<string?> GetApiKeyForEndpointAsync(Uri endpoint, CancellationToken cancellationToken = default) =>
        GetApiKeyAsync(cancellationToken);
}

public sealed class EmptyAgentApiKeyProvider : IAgentApiKeyProvider
{
    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(null);
    }
}

public sealed record AgentRunOptions
{
    public bool AutoApproveToolCalls { get; init; }

    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaximumToolOutputBytes { get; init; } = 1_048_576;

    public void Validate()
    {
        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(MaximumDuration));
        if (ToolTimeout <= TimeSpan.Zero || ToolTimeout > MaximumDuration) throw new ArgumentOutOfRangeException(nameof(ToolTimeout));
        if (MaximumToolOutputBytes is < 1 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(MaximumToolOutputBytes));
    }
}

public sealed record AgentToolApprovalRequest(
    string ToolName,
    string Description,
    ToolRisk Risk,
    JsonObject Arguments,
    string CorrelationId);

public interface IAgentToolApprovalService
{
    ValueTask<bool> RequestApprovalAsync(
        AgentToolApprovalRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DenyAgentToolApprovalService : IAgentToolApprovalService
{
    public ValueTask<bool> RequestApprovalAsync(
        AgentToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

public abstract record AgentRunEvent;

public sealed record AgentTextDelta(string Text) : AgentRunEvent;

public sealed record AgentToolStarted(string CallId, string ToolName, ToolRisk Risk) : AgentRunEvent;

public sealed record AgentToolFinished(string CallId, string ToolName, ToolResult Result) : AgentRunEvent;

public sealed record AgentRunCompleted(
    string? Content,
    AgentUsage Usage,
    int Rounds,
    int ToolCalls) : AgentRunEvent;

public sealed record AgentContextUsageUpdated(int UsedTokens, bool IsEstimated) : AgentRunEvent;

public sealed record AgentContextCompressionStarted : AgentRunEvent;

public sealed record AgentContextCompressed(string Summary, int SummarizedMessages) : AgentRunEvent;

public sealed record AgentConversationContext(string? Summary, int SummarizedMessages, int LastContextTokens = 0);

public sealed record AgentRunFailed(string Code, string Message) : AgentRunEvent;
