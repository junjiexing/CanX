// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;

namespace CanX.Tooling;

public enum ToolRisk
{
    ReadOnly,
    LocalWrite,
    Network,
    Destructive,
}

public enum ToolCallerKind
{
    Unknown,
    User,
    Plugin,
    Agent,
    System,
}

public enum ToolAuditPhase
{
    Started,
    Completed,
}

public sealed record ToolDescriptor(
    string Name,
    string Description,
    ToolRisk Risk,
    JsonObject InputSchema,
    JsonObject OutputSchema,
    string Version = "1.0",
    bool SupportsDryRun = false,
    bool IsCancellable = true);

public sealed record ToolCallContext(
    string Caller,
    bool HasUserApproval = false,
    ToolCallerKind CallerKind = ToolCallerKind.Unknown,
    string? CorrelationId = null,
    string? ParentCallId = null,
    string? CallId = null,
    string? IdempotencyKey = null,
    bool IsDryRun = false,
    IReadOnlySet<string>? Grants = null,
    TimeSpan? Timeout = null,
    int MaximumOutputBytes = 1_048_576)
{
    public static ToolCallContext LocalUser { get; } = new(
        "local-user",
        HasUserApproval: true,
        CallerKind: ToolCallerKind.User);

    public static ToolCallContext ForAgent(
        string sessionId,
        bool hasUserApproval = false,
        string? correlationId = null,
        string? parentCallId = null,
        string? idempotencyKey = null,
        IReadOnlySet<string>? grants = null,
        TimeSpan? timeout = null) => new(
            $"agent:{sessionId}",
            hasUserApproval,
            ToolCallerKind.Agent,
            correlationId,
            parentCallId,
            CallId: Guid.NewGuid().ToString("N"),
            idempotencyKey,
            IsDryRun: false,
            grants,
            timeout);
}

public sealed record ToolResult(bool IsSuccess, JsonNode? Value, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static ToolResult Success(JsonNode? value = null) => new(true, value);

    public static ToolResult Failure(string code, string message) => new(false, null, code, message);
}

public interface IToolHandler
{
    ToolDescriptor Descriptor { get; }

    ValueTask<ToolResult> ExecuteAsync(JsonObject arguments, ToolCallContext context, CancellationToken cancellationToken);
}

public interface IToolAuthorizer
{
    ValueTask<bool> IsAllowedAsync(ToolDescriptor descriptor, ToolCallContext context, CancellationToken cancellationToken);
}

public sealed record ToolAuditEntry(
    DateTimeOffset Timestamp,
    string ToolName,
    string Caller,
    ToolRisk Risk,
    bool Allowed,
    bool Succeeded,
    string? ErrorCode,
    ToolCallerKind CallerKind = ToolCallerKind.Unknown,
    string? CorrelationId = null,
    string? ParentCallId = null,
    string? CallId = null,
    string? IdempotencyKey = null,
    bool DryRun = false,
    long DurationMilliseconds = 0,
    int OutputBytes = 0,
    ToolAuditPhase Phase = ToolAuditPhase.Completed);

public interface IToolAuditSink
{
    ValueTask WriteAsync(ToolAuditEntry entry, CancellationToken cancellationToken);
}
