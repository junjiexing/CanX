// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Core.Configuration;
using CanX.Core.Security;

namespace CanX.Infrastructure.Configuration;

public sealed class JsonApplicationSettingsStore(string path) : IApplicationSettingsStore
{
    private readonly string _path = PrivateFileSystem.PreparePrivateFilePath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return new ApplicationSettings();
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var settings = await JsonSerializer.DeserializeAsync(
                stream,
                SettingsJsonContext.Default.ApplicationSettings,
                cancellationToken).ConfigureAwait(false);
            return settings ?? new ApplicationSettings();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Settings file '{_path}' is not valid JSON.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
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
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    PrivateFileSystem.HardenFile(temporaryPath);
                    await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        SettingsJsonContext.Default.ApplicationSettings,
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
