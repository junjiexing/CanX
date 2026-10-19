// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Application.Playback;
using CanX.Core.Diagnostics;
using CanX.Core.Downloads;
using CanX.Core.Playback;
using CanX.Core.Security;

namespace CanX.Infrastructure.Playback;

public sealed class JsonPlaybackStateStore : IPlaybackStateStore
{
    private readonly string _path;
    private readonly IApplicationLog _applicationLog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonPlaybackStateStore(string path, IApplicationLog? applicationLog = null)
    {
        _path = PrivateFileSystem.PreparePrivateFilePath(path);
        _applicationLog = applicationLog ?? NullApplicationLog.Instance;
    }

    public async ValueTask<PlaybackPersistedState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return new PlaybackPersistedState();
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync(
                stream,
                PlaybackStateJsonContext.Default.PlaybackPersistedState,
                cancellationToken).ConfigureAwait(false);
            return Sanitize(state ?? new PlaybackPersistedState());
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Warning,
                $"Playback state '{_path}' could not be read and was ignored.",
                exception);
            return new PlaybackPersistedState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        PlaybackPersistedState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
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
                        state with { SchemaVersion = PlaybackPersistedState.CurrentSchemaVersion },
                        PlaybackStateJsonContext.Default.PlaybackPersistedState,
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"Playback state '{_path}' could not be saved.",
                exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PlaybackPersistedState Sanitize(PlaybackPersistedState state)
    {
        var playlist = state.PlaylistSources
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .Take(10_000)
            .ToList();
        var recent = state.RecentItems
            .Where(static item => !string.IsNullOrWhiteSpace(item.Source))
            .Select(static item => item with { Preferences = SanitizePreferences(item.Preferences) })
            .OrderByDescending(static item => item.LastPlayedAt)
            .Take(100)
            .ToList();
        var bookmarks = state.Bookmarks
            .Where(static item =>
                !string.IsNullOrWhiteSpace(item.Id) &&
                !string.IsNullOrWhiteSpace(item.Source) &&
                !string.IsNullOrWhiteSpace(item.Title) &&
                double.IsFinite(item.PositionSeconds) &&
                item.PositionSeconds >= 0)
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderByDescending(static item => item.CreatedAt)
            .Take(5_000)
            .ToList();
        return state with
        {
            SchemaVersion = PlaybackPersistedState.CurrentSchemaVersion,
            PlaylistSources = playlist,
            SelectedPlaylistIndex = playlist.Count == 0
                ? -1
                : Math.Clamp(state.SelectedPlaylistIndex, 0, playlist.Count - 1),
            RecentItems = recent,
            Bookmarks = bookmarks,
        };
    }

    private static PlaybackMediaPreferences? SanitizePreferences(PlaybackMediaPreferences? preferences)
    {
        if (preferences is null) return null;
        return preferences with
        {
            VideoTrack = SanitizeTrack(preferences.VideoTrack),
            AudioTrack = SanitizeTrack(preferences.AudioTrack),
            SubtitleTrack = SanitizeTrack(preferences.SubtitleTrack),
            ExternalSubtitles = (preferences.ExternalSubtitles ?? [])
                .Select(SanitizeExternalSubtitle)
                .Where(static source => source is not null)
                .Select(static source => source!)
                .Distinct()
                .TakeLast(16)
                .ToList(),
            SubtitleDelaySeconds = InRange(preferences.SubtitleDelaySeconds, -3600, 3600),
            SubtitleFontSize = InRange(preferences.SubtitleFontSize, 1, 9000),
            SubtitleScale = InRange(preferences.SubtitleScale, 0, 100),
            SubtitlePosition = InRange(preferences.SubtitlePosition, 0, 150),
        };
    }

    private static PlaybackTrackPreference? SanitizeTrack(PlaybackTrackPreference? track)
    {
        if (track is null) return null;
        var external = SanitizeExternalSubtitle(track.ExternalSubtitle);
        if (track.ExternalSubtitle is not null && external is null) return null;
        return track with
        {
            Id = Math.Max(0, track.Id),
            Title = Trim(track.Title, 256),
            Language = Trim(track.Language, 64),
            Codec = Trim(track.Codec, 64),
            ExternalSubtitle = track.IsDisabled ? null : external,
        };
    }

    private static PlaybackExternalSubtitleSource? SanitizeExternalSubtitle(
        PlaybackExternalSubtitleSource? source) =>
        source is { Source: { Length: > 0 } path } && path.Length <= 4096 &&
        !DownloadSecretClassifier.IsSensitiveUri(path) &&
        (!Uri.TryCreate(path, UriKind.Absolute, out var uri) ||
         !string.Equals(uri.Scheme, "canx", StringComparison.OrdinalIgnoreCase))
            ? source with { MediaSourceProfileId = Trim(source.MediaSourceProfileId, 256) }
            : null;

    private static double? InRange(double? value, double minimum, double maximum) =>
        value is { } number && double.IsFinite(number) && number >= minimum && number <= maximum
            ? number : null;

    private static string Trim(string? value, int limit) =>
        value is null ? string.Empty : value.Length <= limit ? value : value[..limit];
}
