// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Core.Security;

namespace CanX.Tooling;

public sealed class FileToolAuditSink : IToolAuditSink, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _stream;
    private bool _disposed;

    public FileToolAuditSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        PrivateFileSystem.EnsurePrivateParentDirectory(fullPath);
        _stream = new FileStream(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        PrivateFileSystem.HardenFile(fullPath);
    }

    public async ValueTask WriteAsync(ToolAuditEntry entry, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var buffer = new MemoryStream(512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("timestamp", entry.Timestamp);
                writer.WriteString("tool", entry.ToolName);
                writer.WriteString("caller", entry.Caller);
                writer.WriteString("callerKind", entry.CallerKind.ToString());
                writer.WriteString("risk", entry.Risk.ToString());
                writer.WriteBoolean("allowed", entry.Allowed);
                writer.WriteString("phase", entry.Phase.ToString().ToLowerInvariant());
                writer.WriteBoolean("succeeded", entry.Succeeded);
                if (entry.ErrorCode is not null) writer.WriteString("errorCode", entry.ErrorCode);
                WriteOptional(writer, "correlationId", entry.CorrelationId);
                WriteOptional(writer, "parentCallId", entry.ParentCallId);
                WriteOptional(writer, "callId", entry.CallId);
                if (entry.IdempotencyKey is not null)
                {
                    // Record that idempotency was used without persisting the caller-supplied key.
                    writer.WriteBoolean("idempotent", true);
                }
                writer.WriteBoolean("dryRun", entry.DryRun);
                writer.WriteNumber("durationMilliseconds", entry.DurationMilliseconds);
                writer.WriteNumber("outputBytes", entry.OutputBytes);
                writer.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            buffer.Position = 0;
            await buffer.CopyToAsync(_stream, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _gate.Dispose();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) writer.WriteString(name, value);
    }
}
