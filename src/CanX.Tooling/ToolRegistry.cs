// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CanX.Tooling;

public sealed class ToolRegistry
{
    private readonly IToolAuthorizer _authorizer;
    private readonly IToolAuditSink _auditSink;
    private readonly Action<Exception>? _auditFailureHandler;
    private readonly ConcurrentDictionary<string, ToolResult> _idempotentResults = new(StringComparer.Ordinal);
    private FrozenDictionary<string, IToolHandler> _handlers = FrozenDictionary<string, IToolHandler>.Empty;

    public ToolRegistry(
        IToolAuthorizer authorizer,
        IToolAuditSink auditSink,
        Action<Exception>? auditFailureHandler = null)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _auditFailureHandler = auditFailureHandler;
    }

    public IReadOnlyList<ToolDescriptor> Descriptors =>
        _handlers.Values.Select(static handler => handler.Descriptor).OrderBy(static descriptor => descriptor.Name).ToArray();

    public bool TryGetDescriptor(string name, out ToolDescriptor? descriptor)
    {
        if (_handlers.TryGetValue(name, out var handler))
        {
            descriptor = handler.Descriptor;
            return true;
        }
        descriptor = null;
        return false;
    }

    public void ReplaceHandlers(IEnumerable<IToolHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToFrozenDictionary(
            static handler => handler.Descriptor.Name,
            StringComparer.Ordinal);
    }

    public async ValueTask<ToolResult> InvokeAsync(
        string name,
        JsonObject? arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(context);

        if (!_handlers.TryGetValue(name, out var handler))
        {
            return ToolResult.Failure("tool_not_found", $"Tool '{name}' is not registered.");
        }

        var stopwatch = Stopwatch.StartNew();
        var descriptor = handler.Descriptor;
        var argumentsValue = arguments ?? [];
        var allowed = false;
        var outputBytes = 0;
        ToolResult result;
        var validationErrors = ToolSchemaValidator.Validate(argumentsValue, descriptor.InputSchema);
        if (validationErrors.Count > 0)
        {
            result = ToolResult.Failure("invalid_arguments", string.Join(" ", validationErrors.Take(8)));
        }
        else if (context.IsDryRun && !descriptor.SupportsDryRun)
        {
            result = ToolResult.Failure("dry_run_unsupported", $"Tool '{name}' does not support dry-run execution.");
        }
        else if (context.MaximumOutputBytes is < 1 or > 16_777_216)
        {
            result = ToolResult.Failure("invalid_context", "MaximumOutputBytes must be between 1 and 16777216.");
        }
        else if (!await _authorizer.IsAllowedAsync(descriptor, context, cancellationToken).ConfigureAwait(false))
        {
            allowed = false;
            result = ToolResult.Failure("permission_denied", $"Caller '{context.Caller}' cannot invoke '{name}'.");
        }
        else
        {
            allowed = true;
            var idempotencyCacheKey = IdempotencyCacheKey(name, context);
            if (idempotencyCacheKey is not null && _idempotentResults.TryGetValue(idempotencyCacheKey, out var cached))
            {
                result = Clone(cached);
                outputBytes = OutputBytes(result);
                await AuditBestEffortAsync(handler, context, allowed, result, stopwatch, outputBytes).ConfigureAwait(false);
                return result;
            }

            if (!context.IsDryRun && descriptor.Risk is ToolRisk.Network or ToolRisk.Destructive)
            {
                try
                {
                    await AuditAsync(
                        handler,
                        context,
                        allowed: true,
                        ToolResult.Failure("execution_started", "Execution has started."),
                        stopwatch,
                        outputBytes: 0,
                        ToolAuditPhase.Started).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    ReportAuditFailure(exception);
                    return ToolResult.Failure(
                        "audit_unavailable",
                        $"Tool '{name}' was not executed because its required audit record could not be written.");
                }
            }

            try
            {
                using var timeout = context.Timeout.HasValue
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;
                if (timeout is not null)
                {
                    timeout.CancelAfter(context.Timeout.GetValueOrDefault());
                }
                var effectiveCancellation = timeout?.Token ?? cancellationToken;
                result = await handler.ExecuteAsync(argumentsValue, context, effectiveCancellation).ConfigureAwait(false);
                if (result.IsSuccess && result.Value is not null)
                {
                    var outputErrors = ToolSchemaValidator.Validate(result.Value, descriptor.OutputSchema);
                    if (outputErrors.Count > 0)
                    {
                        result = ToolResult.Failure("invalid_tool_output", "Tool output did not match its declared schema.");
                    }
                }
                outputBytes = OutputBytes(result);
                if (outputBytes > context.MaximumOutputBytes)
                {
                    result = ToolResult.Failure(
                        "output_too_large",
                        $"Tool output exceeded the {context.MaximumOutputBytes} byte limit.");
                    outputBytes = 0;
                }
                if (result.IsSuccess && idempotencyCacheKey is not null)
                {
                    if (_idempotentResults.Count >= 1024) _idempotentResults.Clear();
                    _idempotentResults.TryAdd(idempotencyCacheKey, Clone(result));
                }
            }
            catch (OperationCanceledException)
            {
                result = ToolResult.Failure(
                    cancellationToken.IsCancellationRequested ? "cancelled" : "timeout",
                    cancellationToken.IsCancellationRequested
                        ? "The tool call was cancelled."
                        : "The tool call exceeded its time limit.");
            }
            catch (Exception)
            {
                result = ToolResult.Failure("tool_failure", "The Tool call failed. See the application log for details.");
            }
        }

        await AuditBestEffortAsync(handler, context, allowed, result, stopwatch, outputBytes).ConfigureAwait(false);
        return result;
    }

    private async ValueTask AuditBestEffortAsync(
        IToolHandler handler,
        ToolCallContext context,
        bool allowed,
        ToolResult result,
        Stopwatch stopwatch,
        int outputBytes)
    {
        try
        {
            await AuditAsync(
                handler,
                context,
                allowed,
                result,
                stopwatch,
                outputBytes,
                ToolAuditPhase.Completed).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Never turn a completed side effect into an apparent Tool failure. Network and destructive
            // calls have a durable Started record before execution; lower-risk calls fail open here.
            ReportAuditFailure(exception);
        }
    }

    private async ValueTask AuditAsync(
        IToolHandler handler,
        ToolCallContext context,
        bool allowed,
        ToolResult result,
        Stopwatch stopwatch,
        int outputBytes,
        ToolAuditPhase phase)
    {
        if (phase == ToolAuditPhase.Completed)
        {
            stopwatch.Stop();
        }
        await _auditSink.WriteAsync(
            new ToolAuditEntry(
                DateTimeOffset.UtcNow,
                handler.Descriptor.Name,
                context.Caller,
                handler.Descriptor.Risk,
                allowed,
                result.IsSuccess,
                result.ErrorCode,
                context.CallerKind,
                context.CorrelationId,
                context.ParentCallId,
                context.CallId,
                context.IdempotencyKey,
                context.IsDryRun,
                (long)stopwatch.Elapsed.TotalMilliseconds,
                outputBytes,
                phase),
            CancellationToken.None).ConfigureAwait(false);
    }

    private void ReportAuditFailure(Exception exception)
    {
        try
        {
            _auditFailureHandler?.Invoke(exception);
        }
        catch
        {
            // Diagnostics must not change Tool execution semantics.
        }
    }

    private static string? IdempotencyCacheKey(string toolName, ToolCallContext context) =>
        string.IsNullOrWhiteSpace(context.IdempotencyKey)
            ? null
            : $"{context.Caller}\n{toolName}\n{context.IdempotencyKey}";

    private static int OutputBytes(ToolResult result) => result.Value is null
        ? 0
        : Encoding.UTF8.GetByteCount(result.Value.ToJsonString());

    private static ToolResult Clone(ToolResult result) => result with
    {
        Value = result.Value?.DeepClone(),
    };
}
