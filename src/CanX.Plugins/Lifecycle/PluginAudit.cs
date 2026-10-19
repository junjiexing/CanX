// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using CanX.Core.Security;

namespace CanX.Plugins.Lifecycle;

public enum PluginAuditPhase
{
    Started,
    Completed,
}

public sealed record PluginAuditEntry(
    DateTimeOffset Timestamp,
    string Operation,
    string PluginId,
    string Version,
    string PackageSha256,
    string PermissionFingerprint,
    string? PreviousVersion = null,
    string? PreviousPackageSha256 = null,
    IReadOnlyList<string>? PermissionChanges = null,
    string? SignatureKeyId = null,
    bool SignatureTrusted = false,
    PluginAuditPhase Phase = PluginAuditPhase.Completed);

public interface IPluginAuditSink
{
    ValueTask WriteAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed class JsonLinesPluginAuditSink : IPluginAuditSink, IDisposable
{
    private readonly string path;
    private readonly Lock gate = new();
    private bool disposed;

    public JsonLinesPluginAuditSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = PrivateFileSystem.PreparePrivateFilePath(path);
    }

    public ValueTask WriteAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var line = JsonSerializer.Serialize(entry, PluginAuditJsonContext.Default.PluginAuditEntry);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PrivateFileSystem.EnsurePrivateParentDirectory(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            PrivateFileSystem.HardenFile(path);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
    }
}

internal sealed class NullPluginAuditSink : IPluginAuditSink
{
    public static NullPluginAuditSink Instance { get; } = new();

    public ValueTask WriteAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PluginAuditEntry))]
internal sealed partial class PluginAuditJsonContext : JsonSerializerContext;
