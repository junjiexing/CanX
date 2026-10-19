// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CanX.Core.Security;

namespace CanX.Plugins.Storage;

internal sealed class PluginStorageStore
{
    private const int CurrentFormatVersion = 1;
    private readonly string storagePath;
    private readonly PluginStorageOptions options;
    private readonly object gate = new();

    public PluginStorageStore(string dataRoot, string pluginId, PluginStorageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        this.options = options ?? new PluginStorageOptions();
        this.options.Validate();
        storagePath = PrivateFileSystem.PreparePrivateFilePath(
            Path.Combine(Path.GetFullPath(dataRoot), pluginId, "storage.json"));
    }

    public string Get(string key)
    {
        ValidateKey(key);
        lock (gate)
        {
            var entries = Load();
            return entries.TryGetValue(key, out var value) ? value : "null";
        }
    }

    public void Set(string key, string valueJson)
    {
        ValidateKey(key);
        ValidateValue(valueJson);
        lock (gate)
        {
            var entries = Load();
            if (!entries.ContainsKey(key) && entries.Count >= options.MaximumEntries)
            {
                throw new PluginStorageException(
                    "entry_limit_exceeded",
                    $"Plugin storage cannot contain more than {options.MaximumEntries} entries.");
            }

            entries[key] = valueJson;
            var totalBytes = entries.Sum(entry =>
                Encoding.UTF8.GetByteCount(entry.Key) + Encoding.UTF8.GetByteCount(entry.Value));
            if (totalBytes > options.MaximumTotalBytes)
            {
                throw new PluginStorageException(
                    "quota_exceeded",
                    $"Plugin storage exceeds its {options.MaximumTotalBytes} byte quota.");
            }

            Save(entries);
        }
    }

    public bool Remove(string key)
    {
        ValidateKey(key);
        lock (gate)
        {
            var entries = Load();
            if (!entries.Remove(key))
            {
                return false;
            }

            Save(entries);
            return true;
        }
    }

    public IReadOnlyList<string> Keys()
    {
        lock (gate)
        {
            return Load().Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Save(new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(storagePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var file = new FileInfo(storagePath);
            if (file.Length > options.MaximumTotalBytes * 2L + 64 * 1024)
            {
                throw Corrupt("Plugin storage file exceeds its safe encoded size.");
            }

            var document = JsonSerializer.Deserialize(
                File.ReadAllBytes(storagePath),
                PluginStorageJsonContext.Default.PluginStorageDocument);
            if (document is null || document.FormatVersion != CurrentFormatVersion)
            {
                throw Corrupt("Plugin storage format is not supported.");
            }

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                ValidateKey(entry.Key);
                ValidateValue(entry.ValueJson);
                if (!entries.TryAdd(entry.Key, entry.ValueJson))
                {
                    throw Corrupt("Plugin storage contains duplicate keys.");
                }
            }

            if (entries.Count > options.MaximumEntries || entries.Sum(entry =>
                    Encoding.UTF8.GetByteCount(entry.Key) + Encoding.UTF8.GetByteCount(entry.Value)) >
                options.MaximumTotalBytes)
            {
                throw Corrupt("Plugin storage exceeds its configured limits.");
            }

            return entries;
        }
        catch (PluginStorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PluginStorageException("storage_unavailable", "Plugin storage could not be read.")
            {
                Source = exception.Source,
            };
        }
    }

    private void Save(IReadOnlyDictionary<string, string> entries)
    {
        var directory = Path.GetDirectoryName(storagePath)!;
        PrivateFileSystem.EnsurePrivateDirectory(directory);
        var temporaryPath = $"{storagePath}.{Guid.NewGuid():N}.tmp";
        var document = new PluginStorageDocument
        {
            FormatVersion = CurrentFormatVersion,
            Entries = entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new PluginStorageEntry
                {
                    Key = entry.Key,
                    ValueJson = entry.Value,
                })
                .ToArray(),
        };
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                PrivateFileSystem.HardenFile(temporaryPath);
                JsonSerializer.Serialize(
                    stream,
                    document,
                    PluginStorageJsonContext.Default.PluginStorageDocument);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, storagePath, overwrite: true);
            PrivateFileSystem.HardenFile(storagePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PluginStorageException("storage_unavailable", "Plugin storage could not be saved.")
            {
                Source = exception.Source,
            };
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(key) > options.MaximumKeyBytes)
        {
            throw new PluginStorageException(
                "invalid_key",
                $"Storage key must be non-empty, contain no control characters, and fit {options.MaximumKeyBytes} UTF-8 bytes.");
        }
    }

    private void ValidateValue(string valueJson)
    {
        if (string.IsNullOrEmpty(valueJson) || Encoding.UTF8.GetByteCount(valueJson) > options.MaximumValueBytes)
        {
            throw new PluginStorageException(
                "value_too_large",
                $"Storage value must fit {options.MaximumValueBytes} UTF-8 bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
        }
        catch (JsonException exception)
        {
            throw new PluginStorageException("invalid_value", $"Storage value is not valid JSON: {exception.Message}");
        }
    }

    private static PluginStorageException Corrupt(string message) =>
        new("storage_corrupt", message);
}

internal sealed class PluginStorageDocument
{
    public int FormatVersion { get; init; }

    public PluginStorageEntry[] Entries { get; init; } = [];
}

internal sealed class PluginStorageEntry
{
    public string Key { get; init; } = string.Empty;

    public string ValueJson { get; init; } = "null";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginStorageDocument))]
internal sealed partial class PluginStorageJsonContext : JsonSerializerContext;
