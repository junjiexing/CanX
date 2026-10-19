// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using CanX.Core.Security;

namespace CanX.Plugins.Lifecycle;

internal sealed class PluginStateStore(string rootDirectory)
{
    private const int CurrentFormatVersion = 1;
    private readonly string statePath = PrivateFileSystem.PreparePrivateFilePath(
        Path.Combine(rootDirectory, ".plugin-state.json"));

    public IReadOnlyDictionary<string, PluginStateEntry> Load()
    {
        if (!File.Exists(statePath))
        {
            return new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal);
        }

        try
        {
            var bytes = File.ReadAllBytes(statePath);
            var document = JsonSerializer.Deserialize(bytes, PluginStateJsonContext.Default.PluginStateDocument);
            if (document is null || document.FormatVersion != CurrentFormatVersion)
            {
                return new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal);
            }

            return document.Plugins
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
                .GroupBy(entry => entry.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal);
        }
    }

    public void Save(IEnumerable<PluginStateEntry> entries)
    {
        PrivateFileSystem.EnsurePrivateDirectory(rootDirectory);
        var document = new PluginStateDocument
        {
            FormatVersion = CurrentFormatVersion,
            Plugins = entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray(),
        };
        var temporaryPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                PrivateFileSystem.HardenFile(temporaryPath);
                JsonSerializer.Serialize(stream, document, PluginStateJsonContext.Default.PluginStateDocument);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
            PrivateFileSystem.HardenFile(statePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class PluginStateDocument
{
    public int FormatVersion { get; init; }

    public PluginStateEntry[] Plugins { get; init; } = [];
}

internal sealed class PluginStateEntry
{
    public string Id { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }

    public string ApprovedPermissionFingerprint { get; init; } = string.Empty;

    public string PackageSha256 { get; init; } = string.Empty;

    public DateTimeOffset InstalledAt { get; init; }

    public string? SignatureKeyId { get; init; }

    public bool SignatureTrusted { get; init; }

    public PluginRollbackState? Rollback { get; init; }
}

internal sealed class PluginRollbackState
{
    public string PackageSha256 { get; init; } = string.Empty;

    public DateTimeOffset InstalledAt { get; init; }

    public bool WasEnabled { get; init; }

    public string ApprovedPermissionFingerprint { get; init; } = string.Empty;

    public string? SignatureKeyId { get; init; }

    public bool SignatureTrusted { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginStateDocument))]
internal sealed partial class PluginStateJsonContext : JsonSerializerContext;
