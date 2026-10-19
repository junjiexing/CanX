// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;

namespace CanX.Application.Playback;

public sealed record PlaybackMediaSource(string PlaybackSource, string HistorySource);

public interface IPlaybackController : IAsyncDisposable
{
    bool IsStarted { get; }

    PlaybackSnapshot Snapshot { get; }

    PlaybackCapabilityManifest Capabilities { get; }

    void ConfigureStartupOptions(IReadOnlyDictionary<string, string> options);

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask OpenAsync(string source, CancellationToken cancellationToken = default);

    ValueTask OpenMediaSourceAsync(
        string playbackSource,
        string historySource,
        string? mediaSourceProfileId = null,
        CancellationToken cancellationToken = default);

    ValueTask OpenMediaSourcePlaylistAsync(
        IReadOnlyList<PlaybackMediaSource> sources,
        int startIndex = 0,
        string? mediaSourceProfileId = null,
        CancellationToken cancellationToken = default);

    ValueTask OpenPlaylistAsync(
        IReadOnlyList<string> sources,
        int startIndex = 0,
        CancellationToken cancellationToken = default);

    ValueTask AddToPlaylistAsync(
        IReadOnlyList<string> sources,
        bool playNow = false,
        CancellationToken cancellationToken = default);

    ValueTask OpenFolderAsync(
        string directory,
        bool recursive = false,
        CancellationToken cancellationToken = default);

    ValueTask PlayPlaylistItemAsync(int index, CancellationToken cancellationToken = default);

    ValueTask PlayNextAsync(CancellationToken cancellationToken = default);

    ValueTask PlayPreviousAsync(CancellationToken cancellationToken = default);

    ValueTask RemovePlaylistItemAsync(int index, CancellationToken cancellationToken = default);

    ValueTask MovePlaylistItemAsync(int index, int newIndex, CancellationToken cancellationToken = default);

    ValueTask ClearPlaylistAsync(CancellationToken cancellationToken = default);

    ValueTask OpenRecentAsync(string source, CancellationToken cancellationToken = default);

    ValueTask RemoveRecentAsync(string source, CancellationToken cancellationToken = default);

    ValueTask RemoveRecentAsync(
        string source,
        bool isMediaSource,
        string? mediaSourceProfileId,
        CancellationToken cancellationToken = default);

    ValueTask ClearRecentAsync(CancellationToken cancellationToken = default);

    ValueTask SeekToChapterAsync(int index, CancellationToken cancellationToken = default);

    ValueTask AddBookmarkAsync(
        string? title = null,
        double? positionSeconds = null,
        CancellationToken cancellationToken = default);

    ValueTask OpenBookmarkAsync(string id, CancellationToken cancellationToken = default);

    ValueTask RenameBookmarkAsync(string id, string title, CancellationToken cancellationToken = default);

    ValueTask RemoveBookmarkAsync(string id, CancellationToken cancellationToken = default);

    ValueTask ClearBookmarksAsync(bool all = false, CancellationToken cancellationToken = default);

    ValueTask SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    ValueTask SetAutoPlayNextAsync(bool autoPlayNext, CancellationToken cancellationToken = default);

    ValueTask TogglePauseAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask SeekAsync(
        double seconds,
        bool relative = false,
        bool exact = true,
        CancellationToken cancellationToken = default);

    ValueTask StepFrameAsync(bool backward = false, CancellationToken cancellationToken = default);

    ValueTask SetAbLoopAsync(
        double? startSeconds = null,
        double? endSeconds = null,
        CancellationToken cancellationToken = default);

    ValueTask ClearAbLoopAsync(CancellationToken cancellationToken = default);

    ValueTask<string> TakeScreenshotAsync(
        string? outputPath = null,
        PlaybackScreenshotMode mode = PlaybackScreenshotMode.Subtitles,
        CancellationToken cancellationToken = default);

    ValueTask SetVolumeAsync(double volume, CancellationToken cancellationToken = default);

    ValueTask SetBrightnessAsync(double brightness, CancellationToken cancellationToken = default);

    ValueTask SetMutedAsync(bool muted, CancellationToken cancellationToken = default);

    ValueTask SetSpeedAsync(double speed, CancellationToken cancellationToken = default);

    ValueTask SelectAudioDeviceAsync(string name, CancellationToken cancellationToken = default);

    ValueTask SetVideoFiltersAsync(string filters, CancellationToken cancellationToken = default);

    ValueTask SetAudioFiltersAsync(string filters, CancellationToken cancellationToken = default);

    ValueTask AddShaderAsync(string path, CancellationToken cancellationToken = default);

    ValueTask RemoveShaderAsync(string path, CancellationToken cancellationToken = default);

    ValueTask ClearShadersAsync(CancellationToken cancellationToken = default);

    ValueTask SetColorSettingsAsync(
        PlaybackColorSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<string> GetRuntimePropertyAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask SetRuntimePropertyAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default);

    ValueTask ExecuteCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    ValueTask SelectTrackAsync(
        PlaybackTrackType type,
        long? trackId,
        CancellationToken cancellationToken = default,
        bool remember = true);

    ValueTask RestoreVideoOutputAsync(long trackId, CancellationToken cancellationToken = default);

    ValueTask AddSubtitleAsync(
        string source,
        bool select = true,
        CancellationToken cancellationToken = default,
        PlaybackExternalSubtitleSource? persistentSource = null,
        bool remember = true);

    ValueTask SetSubtitleVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    ValueTask SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default);

    ValueTask SetSubtitleFontSizeAsync(double size, CancellationToken cancellationToken = default);

    ValueTask SetSubtitleScaleAsync(double scale, CancellationToken cancellationToken = default);

    ValueTask SetSubtitlePositionAsync(double position, CancellationToken cancellationToken = default);

    ValueTask SendKeyDownAsync(string key, CancellationToken cancellationToken = default);

    ValueTask SendKeyUpAsync(string? key = null, CancellationToken cancellationToken = default);

    ValueTask SendKeyPressAsync(string key, double scale = 1, CancellationToken cancellationToken = default);

    ValueTask SetMousePositionAsync(int x, int y, CancellationToken cancellationToken = default);

    ValueTask WriteWatchLaterAsync(CancellationToken cancellationToken = default);
}
