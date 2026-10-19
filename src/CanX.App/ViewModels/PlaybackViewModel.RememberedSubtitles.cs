// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Diagnostics;
using CanX.Core.Playback;

namespace CanX.App.ViewModels;

public sealed partial class PlaybackViewModel
{
    private PlaybackMediaPreferences? FindRememberedPreferences(PlaybackSnapshot snapshot)
    {
        var mediaUri = _mediaSourceUris.GetValueOrDefault(snapshot.Source);
        var source = mediaUri?.AbsoluteUri ?? snapshot.Source;
        var mediaSource = mediaUri is not null;
        return snapshot.RecentItems.FirstOrDefault(item =>
            item.IsMediaSource == mediaSource &&
            string.Equals(item.Source, source, OperatingSystem.IsWindows() && !mediaSource
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            (!mediaSource || string.Equals(item.MediaSourceProfileId, _mediaSourceProfileId,
                StringComparison.Ordinal)))?.Preferences;
    }

    private async Task RestoreRememberedSubtitlesAsync(string playbackSource,
        PlaybackMediaPreferences preferences, long generation, long manualRevision,
        CancellationToken cancellationToken)
    {
        var selectedExternal = preferences.SubtitleTrack?.ExternalSubtitle;
        var selectedRestored = false;
        var anyRestored = false;
        var sources = preferences.ExternalSubtitles
            .Where(source => !SameSubtitleSource(source, selectedExternal))
            .ToList();
        if (selectedExternal is not null) sources.Add(selectedExternal);
        try
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _subtitleGeneration ||
                    !string.Equals(playbackSource, Source, StringComparison.Ordinal)) return;
                var select = SameSubtitleSource(source, selectedExternal);
                try
                {
                    if (TryGetLocalPath(source.Source, out var path))
                    {
                        var loaded = _controller.Snapshot.Tracks.FirstOrDefault(track =>
                            track.Type == PlaybackTrackType.Subtitle && track.IsExternal &&
                            string.Equals(track.ExternalFileName, path, OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        if (loaded is not null)
                        {
                            if (select)
                                await _controller.SelectTrackAsync(PlaybackTrackType.Subtitle,
                                    loaded.Id, cancellationToken, remember: false).ConfigureAwait(true);
                            anyRestored = true;
                            selectedRestored |= select;
                            continue;
                        }
                        if (!File.Exists(path)) throw new FileNotFoundException("上次使用的字幕文件已不存在。", path);
                        await _controller.AddSubtitleAsync(path, select, cancellationToken,
                            source, remember: false).ConfigureAwait(true);
                        anyRestored = true;
                        selectedRestored |= select;
                    }
                    else if (_rememberedSubtitleLoader is { } loader)
                    {
                        await loader(source, select, cancellationToken).ConfigureAwait(true);
                        anyRestored = true;
                        selectedRestored |= select;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _applicationLog.Write(LogComponent.Mpv, ApplicationLogLevel.Warning,
                        $"Could not restore subtitle '{source.Source}'.", exception);
                }
            }

            var internalSelectionRestored = false;
            if (generation == _subtitleGeneration &&
                string.Equals(playbackSource, Source, StringComparison.Ordinal) &&
                preferences.SubtitleTrack is { ExternalSubtitle: null } selection)
            {
                var tracks = _controller.Snapshot.Tracks;
                var matches = tracks.Where(item => item.Type == PlaybackTrackType.Subtitle &&
                    TrackMatchesPreference(item, selection)).ToArray();
                var track = matches.FirstOrDefault(item => item.Id == selection.Id) ??
                    (matches.Length == 1 ? matches[0] : null);
                if (selection.IsDisabled || track is not null)
                {
                    await _controller.SelectTrackAsync(PlaybackTrackType.Subtitle,
                        selection.IsDisabled ? null : track?.Id, cancellationToken, remember: false)
                        .ConfigureAwait(true);
                    internalSelectionRestored = true;
                }
            }

            if (generation == _subtitleGeneration && manualRevision == _manualSubtitleRevision &&
                string.Equals(playbackSource, Source, StringComparison.Ordinal) &&
                (selectedExternal is not null && !selectedRestored ||
                 preferences.SubtitleTrack is { ExternalSubtitle: null, IsDisabled: false } &&
                 !internalSelectionRestored ||
                 preferences.SubtitleTrack is null && !anyRestored))
            {
                _manualSubtitleForCurrentOpen = false;
                StartAutomaticSubtitleDiscovery(_controller.Snapshot, generation, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool SameSubtitleSource(PlaybackExternalSubtitleSource source,
        PlaybackExternalSubtitleSource? other) => other is not null &&
        string.Equals(source.Source, other.Source, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        string.Equals(source.MediaSourceProfileId, other.MediaSourceProfileId, StringComparison.Ordinal);

    private static bool TrackMatchesPreference(PlaybackTrack track, PlaybackTrackPreference preference) =>
        (string.IsNullOrEmpty(preference.Title) || string.Equals(track.Title, preference.Title, StringComparison.Ordinal)) &&
        (string.IsNullOrEmpty(preference.Language) || string.Equals(track.Language, preference.Language,
            StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(preference.Codec) || string.Equals(track.Codec, preference.Codec,
            StringComparison.OrdinalIgnoreCase));
}
