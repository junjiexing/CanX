// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using CanX.Plugins.Manifest;

namespace CanX.Plugins.Packaging;

public sealed class PluginPackageInstaller
{
    private const long MaximumUiIconBytes = 2 * 1024 * 1024;
    private const string ManifestFileName = "canx-plugin.json";
    private const string InstallLockFileName = ".install.lock";
    private const string StagingDirectoryName = ".staging";
    private const string RollbackDirectoryName = ".rollback";
    private readonly string rootDirectory;
    private readonly PluginPackageLimits limits;
    private readonly PluginSignaturePolicy signaturePolicy;

    public PluginPackageInstaller(
        string rootDirectory,
        PluginPackageLimits? limits = null,
        PluginSignaturePolicy? signaturePolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        this.limits = limits ?? PluginPackageLimits.Default;
        this.signaturePolicy = signaturePolicy ?? new PluginSignaturePolicy();
        this.limits.Validate();
    }

    public PluginInstallation Install(Stream package, bool allowUpgrade = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.CanRead)
        {
            throw new ArgumentException("Package stream must be readable.", nameof(package));
        }

        Directory.CreateDirectory(rootDirectory);
        using var installLock = AcquireInstallLock();

        var operationId = Guid.NewGuid().ToString("N");
        var operationDirectory = Path.Combine(rootDirectory, StagingDirectoryName, operationId);
        var archivePath = Path.Combine(operationDirectory, "package.canxplugin");
        var contentDirectory = Path.Combine(operationDirectory, "content");
        Directory.CreateDirectory(contentDirectory);

        try
        {
            var packageHash = CopyPackageAndHash(package, archivePath);
            var (manifest, signature) = ExtractValidatedPackage(archivePath, contentDirectory);
            var targetDirectory = Path.Combine(rootDirectory, manifest.Id);
            var installedAt = DateTimeOffset.UtcNow;
            Activate(contentDirectory, targetDirectory, manifest.Id, operationDirectory, operationId, allowUpgrade);
            return new PluginInstallation(manifest, targetDirectory, packageHash, installedAt, signature);
        }
        catch (PluginPackageException)
        {
            throw;
        }
        catch (PluginManifestException exception)
        {
            throw new PluginPackageException(exception.Message, exception);
        }
        catch (InvalidDataException exception)
        {
            throw new PluginPackageException("Plugin package is not a valid ZIP archive.", exception);
        }
        catch (IOException exception)
        {
            throw new PluginPackageException($"Plugin package installation failed: {exception.Message}", exception);
        }
        finally
        {
            TryDeleteDirectory(operationDirectory);
        }
    }

    internal string GetRollbackDirectory(string pluginId) =>
        Path.Combine(rootDirectory, RollbackDirectoryName, ValidatePluginId(pluginId));

    internal PluginManifest? ReadRollbackManifest(string pluginId)
    {
        var path = Path.Combine(GetRollbackDirectory(pluginId), ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var file = new FileInfo(path);
        if (file.Length > PluginManifestReader.MaximumManifestBytes)
        {
            throw new PluginPackageException("Rollback manifest exceeds the supported size limit.");
        }
        return PluginManifestReader.Parse(File.ReadAllBytes(path));
    }

    internal void SwapWithRollback(string pluginId)
    {
        pluginId = ValidatePluginId(pluginId);
        Directory.CreateDirectory(rootDirectory);
        using var installLock = AcquireInstallLock();
        var targetDirectory = Path.Combine(rootDirectory, pluginId);
        var rollbackDirectory = GetRollbackDirectory(pluginId);
        if (!Directory.Exists(targetDirectory) || !Directory.Exists(rollbackDirectory))
        {
            throw new PluginPackageException($"Plugin '{pluginId}' does not have a rollback version.");
        }

        var operationDirectory = Path.Combine(rootDirectory, StagingDirectoryName, Guid.NewGuid().ToString("N"));
        var displacedDirectory = Path.Combine(operationDirectory, "displaced");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            Directory.Move(targetDirectory, displacedDirectory);
            try
            {
                Directory.Move(rollbackDirectory, targetDirectory);
                Directory.Move(displacedDirectory, rollbackDirectory);
            }
            catch
            {
                if (Directory.Exists(targetDirectory) && !Directory.Exists(rollbackDirectory))
                {
                    Directory.Move(targetDirectory, rollbackDirectory);
                }
                if (Directory.Exists(displacedDirectory) && !Directory.Exists(targetDirectory))
                {
                    Directory.Move(displacedDirectory, targetDirectory);
                }
                throw;
            }
        }
        catch (IOException exception)
        {
            throw new PluginPackageException($"Plugin rollback failed: {exception.Message}", exception);
        }
        finally
        {
            TryDeleteDirectory(operationDirectory);
        }
    }

    internal void DeleteRollback(string pluginId) => TryDeleteDirectory(GetRollbackDirectory(pluginId));

    private FileStream AcquireInstallLock()
    {
        try
        {
            return new FileStream(
                Path.Combine(rootDirectory, InstallLockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new PluginPackageException("Another plugin installation is already in progress.", exception);
        }
    }

    private string CopyPackageAndHash(Stream source, string destinationPath)
    {
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > limits.MaximumArchiveBytes)
                {
                    throw new PluginPackageException(
                        $"Plugin package exceeds {limits.MaximumArchiveBytes} compressed bytes.");
                }

                destination.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }

            if (total == 0)
            {
                throw new PluginPackageException("Plugin package is empty.");
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private (PluginManifest Manifest, PluginSignatureInfo? Signature) ExtractValidatedPackage(
        string archivePath,
        string contentDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0)
        {
            throw new PluginPackageException("Plugin package contains no files.");
        }

        if (archive.Entries.Count > limits.MaximumEntries)
        {
            throw new PluginPackageException(
                $"Plugin package contains more than {limits.MaximumEntries} entries.");
        }

        var entries = ValidateEntries(archive);
        if (!entries.TryGetValue(ManifestFileName, out var manifestEntry) || IsDirectory(manifestEntry))
        {
            throw new PluginPackageException($"Plugin package root must contain {ManifestFileName}.");
        }

        var manifest = ReadManifest(manifestEntry);
        var signature = PluginSignatureVerifier.Verify(entries, signaturePolicy, limits.MaximumEntryBytes);
        if (!entries.TryGetValue(manifest.Entry, out var entryPoint) ||
            IsDirectory(entryPoint) ||
            !string.Equals(entryPoint.FullName, manifest.Entry, StringComparison.Ordinal))
        {
            throw new PluginPackageException($"Plugin entry module '{manifest.Entry}' does not exist in the package.");
        }
        ValidateUiIcon(manifest, entries);

        foreach (var (relativePath, entry) in entries)
        {
            ExtractEntry(entry, relativePath, contentDirectory);
        }

        return (manifest, signature);
    }

    private static void ValidateUiIcon(
        PluginManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (manifest.Ui?.Icon is not { Kind: PluginUiIconKind.Asset } icon)
        {
            return;
        }
        if (!entries.TryGetValue(icon.Value, out var entry) ||
            IsDirectory(entry) ||
            !string.Equals(entry.FullName, icon.Value, StringComparison.Ordinal))
        {
            throw new PluginPackageException($"Plugin UI icon '{icon.Value}' does not exist in the package.");
        }
        if (entry.Length > MaximumUiIconBytes)
        {
            throw new PluginPackageException($"Plugin UI icon '{icon.Value}' exceeds {MaximumUiIconBytes} bytes.");
        }

        Span<byte> header = stackalloc byte[12];
        using var stream = entry.Open();
        var read = stream.Read(header);
        var isPng = read >= 8 &&
            header[0] == 0x89 && header[1..8].SequenceEqual("PNG\r\n\x1a\n"u8);
        var isJpeg = read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
        var isWebP = read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
        if (!isPng && !isJpeg && !isWebP)
        {
            throw new PluginPackageException($"Plugin UI icon '{icon.Value}' is not a valid PNG, JPEG, or WebP image.");
        }
    }

    private Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            var relativePath = ValidateEntryPath(entry.FullName);
            if (!entries.TryAdd(relativePath, entry))
            {
                throw new PluginPackageException($"Plugin package contains duplicate path '{relativePath}'.");
            }

            if (IsSymbolicLink(entry))
            {
                throw new PluginPackageException($"Plugin package entry '{relativePath}' cannot be a symbolic link.");
            }

            if (entry.Length > limits.MaximumEntryBytes)
            {
                throw new PluginPackageException(
                    $"Plugin package entry '{relativePath}' exceeds {limits.MaximumEntryBytes} bytes.");
            }

            totalLength += entry.Length;
            if (totalLength > limits.MaximumUncompressedBytes)
            {
                throw new PluginPackageException(
                    $"Plugin package exceeds {limits.MaximumUncompressedBytes} uncompressed bytes.");
            }
        }

        return entries;
    }

    private static string ValidateEntryPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path.StartsWith('/') ||
            path.Contains('\\') || path.Contains(':') || path.Contains('\0'))
        {
            throw new PluginPackageException($"Plugin package entry '{path}' has an unsafe path.");
        }

        var normalized = path.EndsWith('/') ? path[..^1] : path;
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new PluginPackageException($"Plugin package entry '{path}' has an unsafe path.");
        }

        return normalized;
    }

    private PluginManifest ReadManifest(ZipArchiveEntry entry)
    {
        if (entry.Length > PluginManifestReader.MaximumManifestBytes)
        {
            throw new PluginPackageException(
                $"{ManifestFileName} exceeds {PluginManifestReader.MaximumManifestBytes} bytes.");
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        CopyWithLimit(stream, memory, PluginManifestReader.MaximumManifestBytes, ManifestFileName);
        return PluginManifestReader.Parse(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)));
    }

    private void ExtractEntry(ZipArchiveEntry entry, string relativePath, string contentDirectory)
    {
        var destinationPath = Path.GetFullPath(
            Path.Combine(contentDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var requiredPrefix = contentDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? contentDirectory
            : contentDirectory + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginPackageException($"Plugin package entry '{relativePath}' escapes its package root.");
        }

        if (IsDirectory(entry))
        {
            Directory.CreateDirectory(destinationPath);
            return;
        }

        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new PluginPackageException($"Plugin package entry '{relativePath}' has no parent directory.");
        Directory.CreateDirectory(parent);
        using var source = entry.Open();
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        CopyWithLimit(source, destination, limits.MaximumEntryBytes, relativePath);
    }

    private static void CopyWithLimit(Stream source, Stream destination, long limit, string entryName)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return;
                }

                total += read;
                if (total > limit)
                {
                    throw new PluginPackageException($"Plugin package entry '{entryName}' exceeds {limit} bytes.");
                }

                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Activate(
        string contentDirectory,
        string targetDirectory,
        string pluginId,
        string operationDirectory,
        string operationId,
        bool allowUpgrade)
    {
        if (!Directory.Exists(targetDirectory))
        {
            DeleteRollback(pluginId);
            Directory.Move(contentDirectory, targetDirectory);
            return;
        }

        if (!allowUpgrade)
        {
            throw new PluginPackageException(
                $"Plugin '{Path.GetFileName(targetDirectory)}' is already installed; upgrade was not authorized.");
        }

        var rollbackRoot = Path.Combine(rootDirectory, RollbackDirectoryName);
        Directory.CreateDirectory(rollbackRoot);
        var stagedRollbackDirectory = Path.Combine(
            rollbackRoot,
            $".{Path.GetFileName(targetDirectory)}-{operationId}");
        var rollbackDirectory = GetRollbackDirectory(pluginId);
        var discardedRollbackDirectory = Path.Combine(operationDirectory, "discarded-rollback");
        Directory.Move(targetDirectory, stagedRollbackDirectory);
        try
        {
            Directory.Move(contentDirectory, targetDirectory);
            if (Directory.Exists(rollbackDirectory))
            {
                Directory.Move(rollbackDirectory, discardedRollbackDirectory);
            }
            Directory.Move(stagedRollbackDirectory, rollbackDirectory);
        }
        catch
        {
            TryDeleteDirectory(targetDirectory);
            if (Directory.Exists(stagedRollbackDirectory))
            {
                Directory.Move(stagedRollbackDirectory, targetDirectory);
            }
            if (Directory.Exists(discardedRollbackDirectory) && !Directory.Exists(rollbackDirectory))
            {
                Directory.Move(discardedRollbackDirectory, rollbackDirectory);
            }
            throw;
        }
    }

    private static string ValidatePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (!string.Equals(pluginId, Path.GetFileName(pluginId), StringComparison.Ordinal) ||
            pluginId.StartsWith(".", StringComparison.Ordinal))
        {
            throw new ArgumentException("Plugin ID is not safe for a package directory.", nameof(pluginId));
        }
        return pluginId;
    }

    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith('/');

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
