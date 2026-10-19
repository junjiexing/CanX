// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using CanX.Core.Playback;
using CanX.Core.Security;

namespace CanX.App.ViewModels;

/// <summary>Stores subtitles from temporary URLs and Android document providers for later playback.</summary>
public sealed class RememberedSubtitleCache(string directory)
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private readonly string _directory = Path.GetFullPath(directory);

    public async Task<string> StoreAsync(byte[] content, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("字幕文件为空或超过 64 MiB。");
        cancellationToken.ThrowIfCancellationRequested();
        PrivateFileSystem.EnsurePrivateDirectory(_directory);
        var extension = SubtitleExtension(name);
        var path = Path.Combine(_directory, $"{Convert.ToHexString(SHA256.HashData(content))}{extension}");
        if (File.Exists(path)) return path;

        var temporaryPath = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            PrivateFileSystem.HardenFile(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            PrivateFileSystem.HardenFile(path);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<string> StoreAsync(Stream input, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumBytes)
                throw new InvalidDataException("字幕文件超过 64 MiB。");
            buffer.Write(chunk, 0, count);
        }
        return await StoreAsync(buffer.ToArray(), name, cancellationToken).ConfigureAwait(false);
    }

    private static string SubtitleExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return ExternalSubtitleMatcher.IsSubtitleFile($"subtitle{extension}")
            ? extension.ToLowerInvariant() : ".srt";
    }
}
