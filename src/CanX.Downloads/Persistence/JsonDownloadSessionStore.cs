// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Downloads.Serialization;
using CanX.Core.Security;

namespace CanX.Downloads.Persistence;

public sealed class JsonDownloadSessionStore(string path) : IDownloadSessionStore
{
    private readonly string _path = PrivateFileSystem.PreparePrivateFilePath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<DownloadSessionDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return new DownloadSessionDocument();
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync(
                stream,
                DownloadSessionJsonContext.Default.DownloadSessionDocument,
                cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                throw new InvalidDataException($"Download session '{_path}' is empty.");
            }

            if (document.SchemaVersion is not 1 && document.SchemaVersion != DownloadSessionDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Download session schema {document.SchemaVersion} is not supported; " +
                    $"expected {DownloadSessionDocument.CurrentSchemaVersion}.");
            }

            return document.SchemaVersion == DownloadSessionDocument.CurrentSchemaVersion
                ? document
                : document with { SchemaVersion = DownloadSessionDocument.CurrentSchemaVersion };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Download session '{_path}' is not valid JSON.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(DownloadSessionDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            PrivateFileSystem.EnsurePrivateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    PrivateFileSystem.HardenFile(temporaryPath);
                    await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        DownloadSessionJsonContext.Default.DownloadSessionDocument,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _path, overwrite: true);
                PrivateFileSystem.HardenFile(_path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
