// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Configuration;

public sealed record ApplicationSettings
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? DownloadDirectory { get; init; }

    public string ColorTheme { get; init; } = "System";

    public bool EnableLogging { get; init; }

    public bool OpenMediaInNewWindow { get; init; }

    public MpvSettings Mpv { get; init; } = new();

    public AgentSettings Agent { get; init; } = new();
}

public sealed record AgentSettings
{
    public bool ShowToolCalls { get; init; }

    public bool AutoApproveToolCalls { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public double Temperature { get; init; } = 0.2;

    public int ContextWindowTokens { get; init; }

    public int MaximumDurationSeconds { get; init; } = 300;

    public int ToolTimeoutSeconds { get; init; } = 60;
}

public sealed record MpvSettings
{
    public string HardwareDecoding { get; init; } = "auto-safe";

    public string VideoSync { get; init; } = "display-resample";

    public string Deinterlace { get; init; } = "no";

    public bool SavePositionOnQuit { get; init; }

    public bool ResumePlayback { get; init; } = true;

    public bool AutoPlayNext { get; init; } = true;

    public bool SeekPreviewEnabled { get; init; } = true;

    public int SeekPreviewDelayMilliseconds { get; init; } = 180;

    public bool Interpolation { get; init; }

    public string Scale { get; init; } = "lanczos";

    public string ChromaScale { get; init; } = string.Empty;

    public string Downscale { get; init; } = "bilinear";

    public bool Deband { get; init; }

    public bool SigmoidUpscaling { get; init; } = true;

    public string TargetColorspaceHint { get; init; } = "auto";

    public string TargetColorspaceHintMode { get; init; } = "target";

    public string TargetPrimaries { get; init; } = "auto";

    public string TargetTransfer { get; init; } = "auto";

    public string TargetPeak { get; init; } = "auto";

    public string ToneMapping { get; init; } = "auto";

    public string GamutMappingMode { get; init; } = "auto";

    public string HdrComputePeak { get; init; } = "auto";

    public string AudioDevice { get; init; } = "auto";

    public string AudioChannels { get; init; } = "auto-safe";

    public bool AudioExclusive { get; init; }

    public bool AudioPitchCorrection { get; init; } = true;

    public string AudioLanguages { get; init; } = string.Empty;

    public string SubtitleLanguages { get; init; } = "zh-CN,zh-TW,en";

    public string SubtitleAuto { get; init; } = "exact";

    public string SubtitleFont { get; init; } = "sans-serif";

    public string SubtitleFontSize { get; init; } = "38";

    public bool SubtitleScaleWithWindow { get; init; } = true;

    public string Cache { get; init; } = "auto";

    public string CacheSeconds { get; init; } = "3600000";

    public string DemuxerMaxBytesMiB { get; init; } = "150";

    public string DemuxerMaxBackBytesMiB { get; init; } = "50";

    public bool CachePause { get; init; } = true;

    public bool CachePauseInitial { get; init; }

    public string CachePauseWaitSeconds { get; init; } = "1";

    public string NetworkTimeoutSeconds { get; init; } = "60";

    public string AdvancedOptions { get; init; } = string.Empty;
}
