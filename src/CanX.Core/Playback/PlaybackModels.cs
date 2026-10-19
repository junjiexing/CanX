// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Playback;

public enum PlaybackState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Ended,
    Error,
}

public enum PlaybackTrackType
{
    Video,
    Audio,
    Subtitle,
    Unknown,
}

public enum PlaybackScreenshotMode
{
    Video,
    Subtitles,
    Window,
}

public sealed record PlaybackColorSettings
{
    public string TargetColorspaceHint { get; init; } = "auto";

    public string TargetColorspaceHintMode { get; init; } = "target";

    public string TargetPrimaries { get; init; } = "auto";

    public string TargetTransfer { get; init; } = "auto";

    public string TargetPeak { get; init; } = "auto";

    public string ToneMapping { get; init; } = "auto";

    public string GamutMappingMode { get; init; } = "auto";

    public string HdrComputePeak { get; init; } = "auto";
}

public sealed record PlaybackDiagnostics
{
    public string FrameTransferPath => string.IsNullOrWhiteSpace(HardwareDecoder)
        ? "unavailable"
        : string.Equals(HardwareDecoder, "no", StringComparison.OrdinalIgnoreCase)
            ? "cpu-decode-gpu-upload"
            : HardwareDecoder.Contains("copy", StringComparison.OrdinalIgnoreCase)
                ? "hardware-decode-cpu-copy-gpu-upload"
                // Android's direct MediaCodec path exposes the decoded surface
                // through AImageReader/EGLImage but does not populate
                // video-params/hw-pixelformat. A non-empty interop is the useful
                // signal that the hardware surface reached the GPU renderer.
                : !string.IsNullOrWhiteSpace(HardwareInterop)
                    ? "hardware-zero-copy-interop"
                    : "hardware-transfer-unknown";

    public string VideoCodec { get; init; } = string.Empty;

    public string VideoFormat { get; init; } = string.Empty;

    public string DecoderPixelFormat { get; init; } = string.Empty;

    public string HardwarePixelFormat { get; init; } = string.Empty;

    public string HardwareDecoder { get; init; } = string.Empty;

    public string HardwareInterop { get; init; } = string.Empty;

    public string VideoOutput { get; init; } = string.Empty;

    public string GpuApi { get; init; } = string.Empty;

    public string GpuContext { get; init; } = string.Empty;

    public string InputColorMatrix { get; init; } = string.Empty;

    public string InputPrimaries { get; init; } = string.Empty;

    public string InputTransfer { get; init; } = string.Empty;

    public string InputLevels { get; init; } = string.Empty;

    public double? InputSignalPeak { get; init; }

    public string TargetColorMatrix { get; init; } = string.Empty;

    public string TargetPrimaries { get; init; } = string.Empty;

    public string TargetTransfer { get; init; } = string.Empty;

    public string TargetLevels { get; init; } = string.Empty;

    public double? TargetSignalPeak { get; init; }

    public double? ContainerFps { get; init; }

    public double? EstimatedVideoFps { get; init; }

    public double? DisplayFps { get; init; }

    public double? AvSyncSeconds { get; init; }

    public double? TotalAvSyncChangeSeconds { get; init; }

    public long DecoderDroppedFrames { get; init; }

    public long OutputDroppedFrames { get; init; }

    public long MistimedFrames { get; init; }

    public long DelayedFrames { get; init; }

    public double? VideoBitrateBitsPerSecond { get; init; }

    public double? AudioBitrateBitsPerSecond { get; init; }

    public double? CacheSpeedBytesPerSecond { get; init; }

    public double? CacheDurationSeconds { get; init; }
}

/// <summary>
/// Describes the host-side path from libmpv's Render API output to the UI
/// compositor. This is intentionally separate from <see cref="PlaybackDiagnostics"/>,
/// which describes decoding and the hardware-surface path into libmpv.
/// </summary>
public sealed record VideoPresentationDiagnostics
{
    public string Backend { get; init; } = "unavailable";

    public string SynchronizationMode { get; init; } = "unavailable";

    public bool UsesCpuReadback { get; init; }

    public int CanXGpuCopiesPerFrame { get; init; }

    public int CompositorGpuCopiesPerFrame { get; init; }

    public long RenderOperations { get; init; }

    public long SubmitOperations { get; init; }

    public long PresentedFrames { get; init; }

    public long CanXGpuCopies { get; init; }

    public long BlockingFinishCalls { get; init; }

    public double AverageRenderMilliseconds { get; init; }

    public double P95RenderMilliseconds { get; init; }

    public double AverageSubmitMilliseconds { get; init; }

    public double P95SubmitMilliseconds { get; init; }
}

public sealed record PlaybackAudioDevice
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool IsCurrent { get; init; }
}

public sealed record PlaybackTrack
{
    public long Id { get; init; }

    public PlaybackTrackType Type { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string Codec { get; init; } = string.Empty;

    public bool IsSelected { get; init; }

    public bool IsAlbumArt { get; init; }

    public bool IsDefault { get; init; }

    public bool IsForced { get; init; }

    public bool IsExternal { get; init; }

    public string ExternalFileName { get; init; } = string.Empty;
}

public sealed record PlaybackPlaylistItem
{
    public int Index { get; init; }

    public string Source { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public bool IsCurrent { get; init; }
}

public sealed record PlaybackRecentItem
{
    public string Source { get; init; } = string.Empty;

    public bool IsMediaSource { get; init; }

    public string? MediaSourceProfileId { get; init; }

    public string Title { get; init; } = string.Empty;

    public double PositionSeconds { get; init; }

    public double DurationSeconds { get; init; }

    public DateTimeOffset LastPlayedAt { get; init; }

    public bool IsCompleted { get; init; }

    public PlaybackMediaPreferences? Preferences { get; init; }
}

public sealed record PlaybackExternalSubtitleSource
{
    public string Source { get; init; } = string.Empty;

    public string? MediaSourceProfileId { get; init; }
}

public sealed record PlaybackTrackPreference
{
    public bool IsDisabled { get; init; }

    public long Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string Codec { get; init; } = string.Empty;

    public PlaybackExternalSubtitleSource? ExternalSubtitle { get; init; }
}

public sealed record PlaybackMediaPreferences
{
    public PlaybackTrackPreference? VideoTrack { get; init; }

    public PlaybackTrackPreference? AudioTrack { get; init; }

    public PlaybackTrackPreference? SubtitleTrack { get; init; }

    public List<PlaybackExternalSubtitleSource> ExternalSubtitles { get; init; } = [];

    public bool? SubtitleVisible { get; init; }

    public double? SubtitleDelaySeconds { get; init; }

    public double? SubtitleFontSize { get; init; }

    public double? SubtitleScale { get; init; }

    public double? SubtitlePosition { get; init; }
}

public sealed record PlaybackChapter
{
    public int Index { get; init; }

    public string Title { get; init; } = string.Empty;

    public double TimeSeconds { get; init; }

    public bool IsCurrent { get; init; }
}

public sealed record PlaybackBookmark
{
    public string Id { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public double PositionSeconds { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record PlaybackPersistedState
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public List<string> PlaylistSources { get; init; } = [];

    public int SelectedPlaylistIndex { get; init; } = -1;

    public List<PlaybackRecentItem> RecentItems { get; init; } = [];

    public List<PlaybackBookmark> Bookmarks { get; init; } = [];
}

public sealed record PlaybackSnapshot
{
    public PlaybackState State { get; init; }

    public string Source { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public double PositionSeconds { get; init; }

    public double DurationSeconds { get; init; }

    public double Volume { get; init; } = 100;

    public double Brightness { get; init; }

    public bool IsMuted { get; init; }

    public bool IsSubtitleVisible { get; init; } = true;

    public double SubtitleDelaySeconds { get; init; }

    public double SubtitleFontSize { get; init; } = 38;

    public double SubtitleScale { get; init; } = 1;

    public double SubtitlePosition { get; init; } = 100;

    public double Speed { get; init; } = 1;

    public bool IsSeekable { get; init; }

    public int VideoWidth { get; init; }

    public int VideoHeight { get; init; }

    public string ErrorMessage { get; init; } = string.Empty;

    public List<PlaybackTrack> Tracks { get; init; } = [];

    public List<PlaybackPlaylistItem> Playlist { get; init; } = [];

    public int CurrentPlaylistIndex { get; init; } = -1;

    public List<PlaybackRecentItem> RecentItems { get; init; } = [];

    public List<PlaybackChapter> Chapters { get; init; } = [];

    public int CurrentChapterIndex { get; init; } = -1;

    public List<PlaybackBookmark> Bookmarks { get; init; } = [];

    public double? AbLoopASeconds { get; init; }

    public double? AbLoopBSeconds { get; init; }

    public List<PlaybackAudioDevice> AudioDevices { get; init; } = [];

    public string AudioDevice { get; init; } = "auto";

    public string VideoFilters { get; init; } = string.Empty;

    public string AudioFilters { get; init; } = string.Empty;

    public List<string> Shaders { get; init; } = [];

    public PlaybackColorSettings ColorSettings { get; init; } = new();

    public PlaybackDiagnostics Diagnostics { get; init; } = new();
}
