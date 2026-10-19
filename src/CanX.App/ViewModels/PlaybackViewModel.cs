// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using CanX.Application.Playback;
using CanX.Core.Configuration;
using CanX.Core.Diagnostics;
using CanX.Core.Events;
using CanX.Core.Playback;
using CanX.MediaAccess;

namespace CanX.App.ViewModels;

public sealed partial class PlaybackViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IPlaybackController _controller;
    private readonly IApplicationLog _applicationLog;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly IDisposable _subscription;
    private readonly IEventBus _eventBus;
    private IDisposable? _sleepSubscription;
    private PlaybackSleepInhibition? _sleepInhibition;
    private PlaybackSnapshot _snapshot;
    private readonly PlaybackValueAdjustment _volume;
    private readonly PlaybackValueAdjustment _subtitleDelay;
    private readonly PlaybackValueAdjustment _subtitleScale;
    private readonly PlaybackValueAdjustment _subtitlePosition;
    private readonly Dictionary<string, Uri> _mediaSourceUris = new(StringComparer.Ordinal);
    private string? _mediaSourceProfileId;
    private Func<string, CancellationToken, Task>? _mediaSourceSubtitleLoader;
    private VideoPresentationDiagnostics _presentationDiagnostics = new();
    private IReadOnlyList<PlaybackTrackChoiceViewModel> _videoTrackChoices = [];
    private IReadOnlyList<PlaybackTrackChoiceViewModel> _audioTrackChoices = [];
    private IReadOnlyList<PlaybackTrackChoiceViewModel> _subtitleTrackChoices = [];
    private PlaybackTrackChoiceViewModel? _selectedVideoTrack;
    private PlaybackTrackChoiceViewModel? _selectedAudioTrack;
    private PlaybackTrackChoiceViewModel? _selectedSubtitleTrack;
    private IReadOnlyList<PlaybackPlaylistItemViewModel> _playlistItems = [];
    private readonly ObservableCollection<PlaybackRecentItemViewModel> _recentItems = [];
    private readonly ObservableCollection<PlaybackRecentFolderViewModel> _recentFolders = [];
    private bool _showRecentFolders;
    private IReadOnlyList<PlaybackChapterItemViewModel> _chapterItems = [];
    private IReadOnlyList<PlaybackBookmarkItemViewModel> _bookmarkItems = [];
    private IReadOnlyList<PlaybackAudioDeviceChoiceViewModel> _audioDeviceChoices = [];
    private IReadOnlyList<string> _shaderPaths = [];
    private PlaybackPlaylistItemViewModel? _selectedPlaylistItem;
    private PlaybackRecentItemViewModel? _selectedRecentItem;
    private PlaybackChapterItemViewModel? _selectedChapterItem;
    private PlaybackBookmarkItemViewModel? _selectedBookmarkItem;
    private PlaybackAudioDeviceChoiceViewModel? _selectedAudioDevice;
    private string? _selectedShaderPath;
    private string _bookmarkTitleText = string.Empty;
    private string _videoFilterText = string.Empty;
    private string _audioFilterText = string.Empty;
    private PlaybackRuntimeOption _selectedTargetColorspaceHint = null!;
    private PlaybackRuntimeOption _selectedTargetColorspaceHintMode = null!;
    private PlaybackRuntimeOption _selectedTargetPrimaries = null!;
    private PlaybackRuntimeOption _selectedTargetTransfer = null!;
    private PlaybackRuntimeOption _selectedToneMapping = null!;
    private PlaybackRuntimeOption _selectedGamutMappingMode = null!;
    private PlaybackRuntimeOption _selectedHdrComputePeak = null!;
    private string _targetPeakText = "auto";
    private string _runtimePropertyName = "video-sync";
    private string _runtimePropertyValue = string.Empty;
    private string _runtimeCommandName = string.Empty;
    private string _runtimeCommandArguments = string.Empty;
    private string _advancedResult = string.Empty;
    private string _sourceText = string.Empty;
    private string _statusMessage = "正在启动播放器…";
    private bool _isReady;
    private bool _isDisposed;
    private long _lastPresentationDiagnosticsLogTimestamp;
    private Func<MpvSettings>? _subtitleSettingsProvider;
    private Func<PlaybackExternalSubtitleSource, bool, CancellationToken, Task>? _rememberedSubtitleLoader;
    private CancellationTokenSource? _subtitleDiscoveryCancellation;
    private long _subtitleGeneration;
    private long _subtitleStartedGeneration = -1;
    private bool _suppressSubtitleForNextOpen;
    private bool _disableSubtitlesForNextOpen;
    private bool _manualSubtitleForCurrentOpen;
    private bool _disableSubtitlesForCurrentOpen;
    private long _manualSubtitleRevision;
    private IReadOnlyList<DiscoveredSubtitleChoiceViewModel> _discoveredSubtitleChoices = [];
    private Func<ExternalSubtitleCandidate, CancellationToken, Task>? _discoveredSubtitleLoader;
    private string? _pendingDiscoveredSource;
    private IReadOnlyList<ExternalSubtitleCandidate>? _pendingDiscoveredCandidates;
    private Func<ExternalSubtitleCandidate, CancellationToken, Task>? _pendingDiscoveredLoader;

    public PlaybackViewModel(
        IPlaybackController controller,
        IEventBus eventBus,
        SynchronizationContext? synchronizationContext = null,
        IApplicationLog? applicationLog = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _applicationLog = applicationLog ?? NullApplicationLog.Instance;
        ArgumentNullException.ThrowIfNull(eventBus);
        _eventBus = eventBus;
        _snapshot = controller.Snapshot;
        _volume = new PlaybackValueAdjustment(() => _snapshot.Volume,
            (value, token) => _controller.SetVolumeAsync(value, token).AsTask(),
            () => OnPropertyChanged(nameof(Volume)));
        _subtitleDelay = new PlaybackValueAdjustment(() => _snapshot.SubtitleDelaySeconds,
            (value, token) => _controller.SetSubtitleDelayAsync(value, token).AsTask(),
            () => { OnPropertyChanged(nameof(SubtitleDelaySeconds)); OnPropertyChanged(nameof(SubtitleDelayText)); });
        _subtitleScale = new PlaybackValueAdjustment(() => _snapshot.SubtitleScale,
            (value, token) => _controller.SetSubtitleScaleAsync(value, token).AsTask(),
            () => { OnPropertyChanged(nameof(SubtitleScale)); OnPropertyChanged(nameof(SubtitleScaleText)); });
        _subtitlePosition = new PlaybackValueAdjustment(() => _snapshot.SubtitlePosition,
            (value, token) => _controller.SetSubtitlePositionAsync(value, token).AsTask(),
            () => { OnPropertyChanged(nameof(SubtitlePosition)); OnPropertyChanged(nameof(SubtitlePositionText)); });
        UpdateTrackChoices(_snapshot.Tracks);
        UpdatePlaylist(_snapshot.Playlist);
        UpdateRecentItems(_snapshot.RecentItems);
        UpdateChapters(_snapshot.Chapters);
        UpdateBookmarks(_snapshot.Bookmarks);
        UpdateAudioDevices(_snapshot.AudioDevices, _snapshot.AudioDevice);
        UpdateShaders(_snapshot.Shaders);
        _videoFilterText = _snapshot.VideoFilters;
        _audioFilterText = _snapshot.AudioFilters;
        InitializeColorControls(_snapshot.ColorSettings);
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
        _subscription = eventBus.Subscribe<PlaybackChanged>(message => Post(() => Apply(message.Snapshot)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ConfigureDisplaySleepInhibition(Func<CancellationToken, Task<IDisposable>> acquire)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _sleepSubscription?.Dispose();
        _sleepInhibition?.Dispose();
        var inhibition = new PlaybackSleepInhibition(acquire, _applicationLog);
        _sleepInhibition = inhibition;
        // Subscribe directly: mobile UI dispatching may be suspended in the background.
        _sleepSubscription = _eventBus.Subscribe<PlaybackChanged>(message =>
            _ = inhibition.UpdateAsync(message.Snapshot));
        _ = inhibition.UpdateAsync(_controller.Snapshot);
    }

    internal IPlaybackController Controller => _controller;

    public long ManualSubtitleRevision => _manualSubtitleRevision;

    public IReadOnlyList<DiscoveredSubtitleChoiceViewModel> DiscoveredSubtitleChoices => _discoveredSubtitleChoices;

    public bool HasDiscoveredSubtitleChoices => _discoveredSubtitleChoices.Count > 0;

    public void SetDiscoveredSubtitleChoices(
        string playbackSource,
        IReadOnlyList<ExternalSubtitleCandidate> candidates,
        Func<ExternalSubtitleCandidate, CancellationToken, Task> loader)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(loader);
        if (!string.Equals(playbackSource, Source, StringComparison.Ordinal))
        {
            _pendingDiscoveredSource = playbackSource;
            _pendingDiscoveredCandidates = candidates;
            _pendingDiscoveredLoader = loader;
            return;
        }

        ApplyDiscoveredSubtitleChoices(candidates, loader);
    }

    private void ApplyDiscoveredSubtitleChoices(
        IReadOnlyList<ExternalSubtitleCandidate> candidates,
        Func<ExternalSubtitleCandidate, CancellationToken, Task> loader)
    {
        _discoveredSubtitleLoader = loader;
        _discoveredSubtitleChoices = candidates.Take(20)
            .Select(static candidate => new DiscoveredSubtitleChoiceViewModel(candidate))
            .ToArray();
        OnPropertyChanged(nameof(DiscoveredSubtitleChoices));
        OnPropertyChanged(nameof(HasDiscoveredSubtitleChoices));
    }

    public async Task SelectDiscoveredSubtitleAsync(
        DiscoveredSubtitleChoiceViewModel choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!_discoveredSubtitleChoices.Contains(choice) || _discoveredSubtitleLoader is null)
        {
            throw new InvalidOperationException("字幕候选已失效，请重新打开视频。");
        }

        await _discoveredSubtitleLoader(choice.Candidate, cancellationToken).ConfigureAwait(true);
    }

    public void ConfigureAutomaticSubtitles(Func<MpvSettings> settingsProvider) =>
        _subtitleSettingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));

    public void ConfigureMediaSourceSubtitleLoader(Func<string, CancellationToken, Task>? loader) =>
        _mediaSourceSubtitleLoader = loader;

    public void ConfigureRememberedSubtitleLoader(
        Func<PlaybackExternalSubtitleSource, bool, CancellationToken, Task>? loader) =>
        _rememberedSubtitleLoader = loader;

    public void ReportSubtitleDiscoveryFailure(Exception exception) =>
        _applicationLog.Write(LogComponent.Mpv, ApplicationLogLevel.Warning,
            "Automatic external subtitle loading failed.", exception);

    public void SuppressAutomaticSubtitleForNextOpen(bool disableAllSubtitles = false)
    {
        _subtitleDiscoveryCancellation?.Cancel();
        _manualSubtitleForCurrentOpen = true;
        _suppressSubtitleForNextOpen = true;
        _disableSubtitlesForNextOpen = disableAllSubtitles;
    }

    public string SourceText
    {
        get => _sourceText;
        set => SetField(ref _sourceText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetField(ref _isReady, value))
            {
                OnPropertyChanged(nameof(CanDecreaseSpeed));
                OnPropertyChanged(nameof(CanIncreaseSpeed));
                OnPropertyChanged(nameof(CanOpenSelectedRecentItem));
            }
        }
    }

    public string CapabilitySummary
    {
        get
        {
            var manifest = _controller.Capabilities;
            if (manifest.Properties.Count == 0 && manifest.Commands.Count == 0)
            {
                return "mpv 能力清单尚未加载";
            }

            var coverage = PlaybackCapabilityCoverage.Analyze(manifest);
            return $"Manifest v{manifest.SchemaVersion} · {manifest.MpvVersion} · " +
                $"属性 {coverage.PropertyTotal} · 命令 {coverage.CommandTotal} · " +
                $"覆盖 {coverage.Covered}/{coverage.Total}";
        }
    }

    public PlaybackState State => _snapshot.State;

    public PlaybackCapabilityManifest Capabilities => _controller.Capabilities;

    public string Source => _snapshot.Source;

    public Uri? CurrentMediaSourceUri => _mediaSourceUris.GetValueOrDefault(Source);

    public string? CurrentMediaSourceProfileId => CurrentMediaSourceUri is null
        ? null : _mediaSourceProfileId;

    public string Title => string.IsNullOrWhiteSpace(_snapshot.Title) ? "尚未播放" : _snapshot.Title;

    public string PlayPauseText => _snapshot.State is PlaybackState.Paused or PlaybackState.Idle or PlaybackState.Ended
        ? "播放"
        : "暂停";

    public bool IsPlaying => _snapshot.State == PlaybackState.Playing;

    public bool HasMedia => !string.IsNullOrWhiteSpace(_snapshot.Source);

    public bool CanControl => IsReady && HasMedia && _snapshot.State != PlaybackState.Error;

    public double PositionSeconds => _snapshot.PositionSeconds;

    public double DurationSeconds => _snapshot.DurationSeconds;

    public double DurationMaximum => Math.Max(1, _snapshot.DurationSeconds);

    public string PositionText => FormatTime(_snapshot.PositionSeconds);

    public string DurationText => FormatTime(_snapshot.DurationSeconds);

    public double Volume => _volume.Value;

    public double Brightness => _snapshot.Brightness;

    public bool IsSeekable => _snapshot.IsSeekable;

    public bool CanSeek => CanControl && IsSeekable;

    public bool IsMuted => _snapshot.IsMuted;

    public bool IsSubtitleVisible => _snapshot.IsSubtitleVisible;

    public double SubtitleDelaySeconds => _subtitleDelay.Value;

    public string SubtitleDelayText => $"{SubtitleDelaySeconds:+0.0;-0.0;0.0} 秒";

    public double SubtitleScale => _subtitleScale.Value;

    public string SubtitleScaleText => $"{SubtitleScale * 100:0}%";

    public double SubtitlePosition => _subtitlePosition.Value;

    public string SubtitlePositionText => $"{SubtitlePosition:0}%";

    public string MuteText => _snapshot.IsMuted ? "取消静音" : "静音";

    public double Speed => _snapshot.Speed;

    public string SpeedText => $"{Speed.ToString("0.0", CultureInfo.InvariantCulture)}x";

    public bool CanDecreaseSpeed => IsReady &&
        Math.Round(Speed * 10, MidpointRounding.AwayFromZero) > 1;

    public bool CanIncreaseSpeed => IsReady &&
        Math.Round(Speed * 10, MidpointRounding.AwayFromZero) < 40;

    public double? AbLoopASeconds => _snapshot.AbLoopASeconds;

    public double? AbLoopBSeconds => _snapshot.AbLoopBSeconds;

    public int VideoWidth => _snapshot.VideoWidth;

    public int VideoHeight => _snapshot.VideoHeight;

    public IReadOnlyList<PlaybackTrack> Tracks => _snapshot.Tracks;

    public PlaybackDiagnostics Diagnostics => _snapshot.Diagnostics;

    public VideoPresentationDiagnostics PresentationDiagnostics => _presentationDiagnostics;

    public PlaybackColorSettings ColorSettings => _snapshot.ColorSettings;

    public string MediaDetails => _snapshot.VideoWidth > 0 && _snapshot.VideoHeight > 0
        ? $"{_snapshot.VideoWidth} × {_snapshot.VideoHeight} · {_snapshot.Tracks.Count} 条轨道"
        : _snapshot.Tracks.Count > 0 ? $"{_snapshot.Tracks.Count} 条轨道" : string.Empty;

    public IReadOnlyList<PlaybackTrackChoiceViewModel> VideoTrackChoices => _videoTrackChoices;

    public IReadOnlyList<PlaybackTrackChoiceViewModel> AudioTrackChoices => _audioTrackChoices;

    public IReadOnlyList<PlaybackTrackChoiceViewModel> SubtitleTrackChoices => _subtitleTrackChoices;

    public PlaybackTrackChoiceViewModel? SelectedVideoTrack => _selectedVideoTrack;

    public PlaybackTrackChoiceViewModel? SelectedAudioTrack => _selectedAudioTrack;

    public PlaybackTrackChoiceViewModel? SelectedSubtitleTrack => _selectedSubtitleTrack;

    public IReadOnlyList<PlaybackPlaylistItemViewModel> PlaylistItems => _playlistItems;

    public IReadOnlyList<PlaybackRecentItemViewModel> RecentItems => _recentItems;

    public IReadOnlyList<PlaybackRecentFolderViewModel> RecentFolders => _recentFolders;

    public bool ShowRecentFiles
    {
        get => !_showRecentFolders;
        set
        {
            if (value)
            {
                SetRecentFolderView(false);
            }
        }
    }

    public bool ShowRecentFolders
    {
        get => _showRecentFolders;
        set
        {
            if (value)
            {
                SetRecentFolderView(true);
            }
        }
    }

    public IReadOnlyList<PlaybackChapterItemViewModel> ChapterItems => _chapterItems;

    public IReadOnlyList<PlaybackBookmarkItemViewModel> BookmarkItems => _bookmarkItems;

    public PlaybackPlaylistItemViewModel? SelectedPlaylistItem
    {
        get => _selectedPlaylistItem;
        set
        {
            if (SetField(ref _selectedPlaylistItem, value))
            {
                OnPropertyChanged(nameof(CanPlaySelectedPlaylistItem));
                OnPropertyChanged(nameof(CanMoveSelectedPlaylistItemUp));
                OnPropertyChanged(nameof(CanMoveSelectedPlaylistItemDown));
            }
        }
    }

    public PlaybackRecentItemViewModel? SelectedRecentItem
    {
        get => _selectedRecentItem;
        set
        {
            if (SetField(ref _selectedRecentItem, value))
            {
                OnPropertyChanged(nameof(CanOpenSelectedRecentItem));
                OnPropertyChanged(nameof(CanRemoveSelectedRecentItem));
                OnPropertyChanged(nameof(SelectedRecentActionText));
            }
        }
    }

    public PlaybackChapterItemViewModel? SelectedChapterItem
    {
        get => _selectedChapterItem;
        set
        {
            if (SetField(ref _selectedChapterItem, value))
            {
                OnPropertyChanged(nameof(CanOpenSelectedChapter));
            }
        }
    }

    public PlaybackBookmarkItemViewModel? SelectedBookmarkItem
    {
        get => _selectedBookmarkItem;
        set
        {
            if (SetField(ref _selectedBookmarkItem, value))
            {
                BookmarkTitleText = value?.Title ?? string.Empty;
                OnPropertyChanged(nameof(CanOpenSelectedBookmark));
                OnPropertyChanged(nameof(CanRenameSelectedBookmark));
            }
        }
    }

    public string BookmarkTitleText
    {
        get => _bookmarkTitleText;
        set
        {
            if (SetField(ref _bookmarkTitleText, value))
            {
                OnPropertyChanged(nameof(CanRenameSelectedBookmark));
            }
        }
    }

    public string VideoFilterText
    {
        get => _videoFilterText;
        set => SetField(ref _videoFilterText, value);
    }

    public string AudioFilterText
    {
        get => _audioFilterText;
        set => SetField(ref _audioFilterText, value);
    }

    public IReadOnlyList<PlaybackAudioDeviceChoiceViewModel> AudioDeviceChoices => _audioDeviceChoices;

    public PlaybackAudioDeviceChoiceViewModel? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set => SetField(ref _selectedAudioDevice, value);
    }

    public IReadOnlyList<string> ShaderPaths => _shaderPaths;

    public string? SelectedShaderPath
    {
        get => _selectedShaderPath;
        set
        {
            if (SetField(ref _selectedShaderPath, value))
            {
                OnPropertyChanged(nameof(CanRemoveShader));
            }
        }
    }

    public IReadOnlyList<PlaybackRuntimeOption> TargetColorspaceHintOptions { get; } =
    [
        new("auto", "自动"), new("yes", "始终发送"), new("no", "不发送"),
    ];

    public IReadOnlyList<PlaybackRuntimeOption> TargetColorspaceHintModeOptions { get; } =
    [
        new("target", "目标显示能力"), new("source", "源内容直通"), new("source-dynamic", "源动态元数据"),
    ];

    public IReadOnlyList<PlaybackRuntimeOption> TargetPrimariesOptions { get; } =
    [
        new("auto", "自动"), new("bt.470m", "BT.470 M"), new("bt.601-525", "BT.601 525"),
        new("bt.601-625", "BT.601 625"), new("bt.709", "BT.709"), new("bt.2020", "BT.2020"),
        new("display-p3", "Display P3"), new("dci-p3", "DCI-P3"), new("apple", "Apple RGB"),
        new("adobe", "Adobe RGB"), new("prophoto", "ProPhoto RGB"), new("cie1931", "CIE 1931 RGB"),
        new("v-gamut", "V-Gamut"), new("s-gamut", "S-Gamut"),
    ];

    public IReadOnlyList<PlaybackRuntimeOption> TargetTransferOptions { get; } =
    [
        new("auto", "自动"), new("bt.1886", "BT.1886"), new("srgb", "sRGB"), new("linear", "Linear"),
        new("gamma1.8", "Gamma 1.8"), new("gamma2.0", "Gamma 2.0"), new("gamma2.2", "Gamma 2.2"),
        new("gamma2.4", "Gamma 2.4"), new("gamma2.6", "Gamma 2.6"), new("gamma2.8", "Gamma 2.8"),
        new("prophoto", "ProPhoto RGB"), new("pq", "PQ"), new("hlg", "HLG"), new("v-log", "V-Log"),
        new("s-log1", "S-Log1"), new("s-log2", "S-Log2"), new("scrgb", "scRGB"),
    ];

    public IReadOnlyList<PlaybackRuntimeOption> ToneMappingOptions { get; } =
    [
        new("auto", "自动"), new("spline", "Spline"), new("bt.2390", "BT.2390"),
        new("bt.2446a", "BT.2446A"), new("mobius", "Mobius"), new("reinhard", "Reinhard"),
        new("hable", "Hable"), new("st2094-40", "ST 2094-40"), new("st2094-10", "ST 2094-10"),
        new("clip", "Clip"), new("gamma", "Gamma"), new("linear", "Linear"),
    ];

    public IReadOnlyList<PlaybackRuntimeOption> GamutMappingOptions { get; } =
    [
        new("auto", "自动"), new("perceptual", "感知"), new("relative", "相对色度"),
        new("saturation", "饱和度"), new("absolute", "绝对色度"), new("desaturate", "去饱和"),
        new("darken", "压暗"), new("clip", "裁剪"), new("warn", "越界警告"), new("linear", "线性"),
    ];

    public IReadOnlyList<PlaybackRuntimeOption> HdrComputePeakOptions { get; } =
    [
        new("auto", "自动"), new("yes", "启用"), new("no", "禁用"),
    ];

    public PlaybackRuntimeOption SelectedTargetColorspaceHint
    {
        get => _selectedTargetColorspaceHint;
        set => SetField(ref _selectedTargetColorspaceHint, value);
    }

    public PlaybackRuntimeOption SelectedTargetColorspaceHintMode
    {
        get => _selectedTargetColorspaceHintMode;
        set => SetField(ref _selectedTargetColorspaceHintMode, value);
    }

    public PlaybackRuntimeOption SelectedTargetPrimaries
    {
        get => _selectedTargetPrimaries;
        set => SetField(ref _selectedTargetPrimaries, value);
    }

    public PlaybackRuntimeOption SelectedTargetTransfer
    {
        get => _selectedTargetTransfer;
        set => SetField(ref _selectedTargetTransfer, value);
    }

    public string TargetPeakText
    {
        get => _targetPeakText;
        set => SetField(ref _targetPeakText, value);
    }

    public PlaybackRuntimeOption SelectedToneMapping
    {
        get => _selectedToneMapping;
        set => SetField(ref _selectedToneMapping, value);
    }

    public PlaybackRuntimeOption SelectedGamutMappingMode
    {
        get => _selectedGamutMappingMode;
        set => SetField(ref _selectedGamutMappingMode, value);
    }

    public PlaybackRuntimeOption SelectedHdrComputePeak
    {
        get => _selectedHdrComputePeak;
        set => SetField(ref _selectedHdrComputePeak, value);
    }

    public string RuntimePropertyName
    {
        get => _runtimePropertyName;
        set => SetField(ref _runtimePropertyName, value);
    }

    public string RuntimePropertyValue
    {
        get => _runtimePropertyValue;
        set => SetField(ref _runtimePropertyValue, value);
    }

    public string RuntimeCommandName
    {
        get => _runtimeCommandName;
        set => SetField(ref _runtimeCommandName, value);
    }

    public string RuntimeCommandArguments
    {
        get => _runtimeCommandArguments;
        set => SetField(ref _runtimeCommandArguments, value);
    }

    public string AdvancedResult
    {
        get => _advancedResult;
        private set
        {
            if (SetField(ref _advancedResult, value))
            {
                OnPropertyChanged(nameof(HasAdvancedResult));
            }
        }
    }

    public bool HasAdvancedResult => !string.IsNullOrWhiteSpace(AdvancedResult);

    public string DecodePathText
    {
        get
        {
            var diagnostics = _snapshot.Diagnostics;
            var decoder = string.Equals(diagnostics.HardwareDecoder, "no", StringComparison.OrdinalIgnoreCase)
                ? "软件解码"
                : string.IsNullOrWhiteSpace(diagnostics.HardwareDecoder)
                    ? "解码器尚不可用"
                    : $"硬解 {diagnostics.HardwareDecoder}";
            var transfer = diagnostics.FrameTransferPath switch
            {
                "cpu-decode-gpu-upload" => "CPU 解码后上传 GPU",
                "hardware-decode-cpu-copy-gpu-upload" => "硬解后复制回 CPU 再上传",
                "hardware-zero-copy-interop" => "硬解 surface 通过 GPU interop 直达渲染",
                "hardware-transfer-unknown" => "硬解传输路径未知",
                _ => "帧传输路径尚不可用",
            };
            return $"{ValueOrDash(diagnostics.VideoCodec)} · {ValueOrDash(diagnostics.VideoFormat)} · {decoder} · " +
                $"{ValueOrDash(diagnostics.DecoderPixelFormat)} → {ValueOrDash(diagnostics.HardwarePixelFormat)} · {transfer}";
        }
    }

    public string RenderPathText
    {
        get
        {
            var diagnostics = _snapshot.Diagnostics;
            return $"VO {ValueOrDash(diagnostics.VideoOutput)} · API {ValueOrDash(diagnostics.GpuApi)} · " +
                $"Context {ValueOrDash(diagnostics.GpuContext)} · Interop {ValueOrDash(diagnostics.HardwareInterop)}";
        }
    }

    public string PresentationPathText =>
        $"Surface {ValueOrDash(_presentationDiagnostics.Backend)} · " +
        $"同步 {ValueOrDash(_presentationDiagnostics.SynchronizationMode)} · " +
        $"CPU 回读 {(_presentationDiagnostics.UsesCpuReadback ? "是" : "否")} · " +
        $"CanX GPU copy {_presentationDiagnostics.CanXGpuCopiesPerFrame}/帧 · " +
        $"Avalonia snapshot {_presentationDiagnostics.CompositorGpuCopiesPerFrame}/帧";

    public string PresentationPerformanceText =>
        $"mpv render 平均 {_presentationDiagnostics.AverageRenderMilliseconds:0.###} ms / " +
        $"P95 {_presentationDiagnostics.P95RenderMilliseconds:0.###} ms · " +
        $"提交平均 {_presentationDiagnostics.AverageSubmitMilliseconds:0.###} ms / " +
        $"P95 {_presentationDiagnostics.P95SubmitMilliseconds:0.###} ms · " +
        $"操作 render {_presentationDiagnostics.RenderOperations} / " +
        $"submit {_presentationDiagnostics.SubmitOperations} / " +
        $"present {_presentationDiagnostics.PresentedFrames} · " +
        $"GPU copy {_presentationDiagnostics.CanXGpuCopies} · " +
        $"glFinish {_presentationDiagnostics.BlockingFinishCalls}";

    public string InputColorText => FormatColor("输入", _snapshot.Diagnostics.InputColorMatrix,
        _snapshot.Diagnostics.InputPrimaries, _snapshot.Diagnostics.InputTransfer,
        _snapshot.Diagnostics.InputLevels, _snapshot.Diagnostics.InputSignalPeak);

    public string TargetColorText => FormatColor("输出", _snapshot.Diagnostics.TargetColorMatrix,
        _snapshot.Diagnostics.TargetPrimaries, _snapshot.Diagnostics.TargetTransfer,
        _snapshot.Diagnostics.TargetLevels, _snapshot.Diagnostics.TargetSignalPeak);

    public string PerformanceText
    {
        get
        {
            var diagnostics = _snapshot.Diagnostics;
            return $"FPS 容器 {FormatNumber(diagnostics.ContainerFps, "0.###")} / 解码 {FormatNumber(diagnostics.EstimatedVideoFps, "0.###")} / 显示 {FormatNumber(diagnostics.DisplayFps, "0.###")} · " +
                $"A/V {FormatMilliseconds(diagnostics.AvSyncSeconds)} · 累计校正 {FormatMilliseconds(diagnostics.TotalAvSyncChangeSeconds)}";
        }
    }

    public string FrameStatisticsText
    {
        get
        {
            var diagnostics = _snapshot.Diagnostics;
            return $"丢帧 解码 {diagnostics.DecoderDroppedFrames} / 输出 {diagnostics.OutputDroppedFrames} · " +
                $"错时 {diagnostics.MistimedFrames} · 延迟 {diagnostics.DelayedFrames}";
        }
    }

    public string BitrateCacheText
    {
        get
        {
            var diagnostics = _snapshot.Diagnostics;
            return $"码率 视频 {FormatRate(diagnostics.VideoBitrateBitsPerSecond, false)} / 音频 {FormatRate(diagnostics.AudioBitrateBitsPerSecond, false)} · " +
                $"缓存速度 {FormatRate(diagnostics.CacheSpeedBytesPerSecond, true)} · 缓存 {FormatNumber(diagnostics.CacheDurationSeconds, "0.0")} 秒";
        }
    }

    public bool HasPlaylistItems => _playlistItems.Count > 0;

    public bool HasRecentItems => _recentItems.Count > 0;

    public bool HasRecentFolders => _recentFolders.Count > 0;

    public bool HasChapters => _chapterItems.Count > 0;

    public bool CanSeekPreviousChapter => CanSeek && FindPreviousChapter() is not null;

    public bool CanSeekNextChapter => CanSeek && FindNextChapter() is not null;

    public bool HasBookmarks => _bookmarkItems.Count > 0;

    public bool CanPlayPrevious => _snapshot.CurrentPlaylistIndex > 0;

    public bool CanPlayNext => _snapshot.CurrentPlaylistIndex >= 0 &&
        _snapshot.CurrentPlaylistIndex < _snapshot.Playlist.Count - 1;

    public bool CanPlaySelectedPlaylistItem => _selectedPlaylistItem is not null;

    public bool CanMoveSelectedPlaylistItemUp => _selectedPlaylistItem?.Index > 0;

    public bool CanMoveSelectedPlaylistItemDown => _selectedPlaylistItem is { } item &&
        item.Index < _playlistItems.Count - 1;

    public bool CanOpenSelectedRecentItem => IsReady && _selectedRecentItem is not null;

    public bool CanRemoveSelectedRecentItem => _selectedRecentItem is not null;

    public string SelectedRecentActionText => _selectedRecentItem switch
    {
        { IsCompleted: true } => "重新播放",
        { PositionSeconds: >= 5 } => "继续播放",
        _ => "播放",
    };

    public bool CanOpenSelectedChapter => CanControl && _selectedChapterItem is not null;

    public bool CanAddBookmark => CanControl && IsSeekable;

    public bool CanOpenSelectedBookmark => CanControl && _selectedBookmarkItem is not null;

    public bool CanRenameSelectedBookmark => _selectedBookmarkItem is not null &&
        !string.IsNullOrWhiteSpace(_bookmarkTitleText);

    public bool CanRemoveShader => CanControl && _selectedShaderPath is not null;

    public string AbLoopText => (_snapshot.AbLoopASeconds, _snapshot.AbLoopBSeconds) switch
    {
        ({ } start, { } end) => $"A {FormatTime(start)}  →  B {FormatTime(end)}",
        ({ } start, null) => $"A {FormatTime(start)}  →  B 未设置",
        (null, { } end) => $"A 未设置  →  B {FormatTime(end)}",
        _ => "A-B 循环未设置",
    };

    public string PlaylistSummary => _playlistItems.Count == 0
        ? "播放列表为空"
        : $"{_playlistItems.Count} 项";

    public string RecentSummary => _recentItems.Count == 0
        ? "暂无播放记录"
        : $"最近 {_recentItems.Count} 项";

    public string ChapterSummary => _chapterItems.Count == 0
        ? "当前媒体没有章节"
        : $"{_chapterItems.Count} 个章节";

    public string BookmarkSummary => _bookmarkItems.Count == 0
        ? "暂无书签"
        : $"{_bookmarkItems.Count} 个书签";

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _controller.StartAsync(cancellationToken).ConfigureAwait(true);
            IsReady = true;
            Apply(_controller.Snapshot);
            OnPropertyChanged(nameof(CapabilitySummary));
            StatusMessage = "libmpv 已就绪";
        }
        catch (Exception exception)
        {
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "The playback engine failed to start.",
                exception);
            IsReady = false;
            StatusMessage = $"播放器启动失败：{exception.Message}";
        }
    }

    public async Task OpenAsync(string? source = null, CancellationToken cancellationToken = default)
    {
        var value = string.IsNullOrWhiteSpace(source) ? SourceText : source;
        _mediaSourceUris.Clear();
        _mediaSourceProfileId = null;
        try
        {
            await _controller.OpenAsync(value, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            _suppressSubtitleForNextOpen = false;
            _disableSubtitlesForNextOpen = false;
            throw;
        }
        SourceText = value.Trim();
        StatusMessage = "正在打开媒体…";
    }

    public async Task OpenMediaSourceAsync(
        string playbackSource,
        Uri mediaUri,
        string? mediaSourceProfileId,
        CancellationToken cancellationToken = default) =>
        await OpenMediaSourcePlaylistAsync(
            [new PlaybackMediaSource(playbackSource, mediaUri.AbsoluteUri)],
            0, mediaSourceProfileId, cancellationToken).ConfigureAwait(true);

    public async Task OpenMediaSourcePlaylistAsync(
        IReadOnlyList<PlaybackMediaSource> sources,
        int startIndex = 0,
        string? mediaSourceProfileId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _mediaSourceUris.Clear();
        foreach (var source in sources)
            _mediaSourceUris.Add(source.PlaybackSource, new Uri(source.HistorySource, UriKind.Absolute));
        _mediaSourceProfileId = mediaSourceProfileId;
        try
        {
            await _controller.OpenMediaSourcePlaylistAsync(
                sources, startIndex, mediaSourceProfileId, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            _suppressSubtitleForNextOpen = false;
            _disableSubtitlesForNextOpen = false;
            _mediaSourceUris.Clear();
            throw;
        }
        SourceText = sources[startIndex].PlaybackSource;
        StatusMessage = "正在打开媒体…";
    }

    public async Task OpenPlaylistAsync(
        IReadOnlyList<string> sources,
        int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        _mediaSourceUris.Clear();
        _mediaSourceProfileId = null;
        await _controller.OpenPlaylistAsync(sources, startIndex, cancellationToken).ConfigureAwait(true);
        SourceText = sources[startIndex];
        StatusMessage = "正在打开播放列表…";
    }

    public Task AddToPlaylistAsync(
        IReadOnlyList<string> sources,
        bool playNow = false,
        CancellationToken cancellationToken = default) =>
        _controller.AddToPlaylistAsync(sources, playNow, cancellationToken).AsTask();

    public Task OpenFolderAsync(
        string directory,
        bool recursive = false,
        CancellationToken cancellationToken = default) =>
        _controller.OpenFolderAsync(directory, recursive, cancellationToken).AsTask();

    public Task PlayPlaylistItemAsync(
        PlaybackPlaylistItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.PlayPlaylistItemAsync(item.Index, cancellationToken).AsTask();
    }

    public Task PlayNextAsync(CancellationToken cancellationToken = default) =>
        _controller.PlayNextAsync(cancellationToken).AsTask();

    public Task PlayPreviousAsync(CancellationToken cancellationToken = default) =>
        _controller.PlayPreviousAsync(cancellationToken).AsTask();

    public Task RemovePlaylistItemAsync(
        PlaybackPlaylistItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.RemovePlaylistItemAsync(item.Index, cancellationToken).AsTask();
    }

    public Task MovePlaylistItemAsync(
        PlaybackPlaylistItemViewModel item,
        int newIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.MovePlaylistItemAsync(item.Index, newIndex, cancellationToken).AsTask();
    }

    public Task ClearPlaylistAsync(CancellationToken cancellationToken = default) =>
        _controller.ClearPlaylistAsync(cancellationToken).AsTask();

    public Task OpenRecentAsync(
        PlaybackRecentItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.OpenRecentAsync(item.Source, cancellationToken).AsTask();
    }

    public Task RemoveRecentAsync(
        PlaybackRecentItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.RemoveRecentAsync(
            item.Source,
            item.IsMediaSource,
            item.MediaSourceProfileId,
            cancellationToken).AsTask();
    }

    public Task ClearRecentAsync(CancellationToken cancellationToken = default) =>
        _controller.ClearRecentAsync(cancellationToken).AsTask();

    public Task OpenChapterAsync(
        PlaybackChapterItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.SeekToChapterAsync(item.Index, cancellationToken).AsTask();
    }

    public Task SeekNextChapterAsync(CancellationToken cancellationToken = default)
    {
        var chapter = CanSeek ? FindNextChapter() : null;
        return chapter is null
            ? Task.CompletedTask
            : _controller.SeekToChapterAsync(chapter.Index, cancellationToken).AsTask();
    }

    public Task SeekPreviousChapterAsync(CancellationToken cancellationToken = default)
    {
        var chapter = CanSeek ? FindPreviousChapter() : null;
        return chapter is null
            ? Task.CompletedTask
            : _controller.SeekToChapterAsync(chapter.Index, cancellationToken).AsTask();
    }

    private PlaybackChapterItemViewModel? FindPreviousChapter()
    {
        var current = _chapterItems
            .Where(chapter => double.IsFinite(chapter.TimeSeconds) &&
                chapter.TimeSeconds <= PositionSeconds + 0.001)
            .MaxBy(static chapter => chapter.TimeSeconds);
        return current is null
            ? null
            : _chapterItems
                .Where(chapter => double.IsFinite(chapter.TimeSeconds) &&
                    chapter.TimeSeconds < current.TimeSeconds)
                .MaxBy(static chapter => chapter.TimeSeconds);
    }

    private PlaybackChapterItemViewModel? FindNextChapter()
    {
        var position = PositionSeconds;
        return _chapterItems
            .Where(chapter => double.IsFinite(chapter.TimeSeconds) && chapter.TimeSeconds > position + 0.001)
            .MinBy(static chapter => chapter.TimeSeconds);
    }

    public async Task AddBookmarkAsync(CancellationToken cancellationToken = default)
    {
        await _controller.AddBookmarkAsync(BookmarkTitleText, cancellationToken: cancellationToken).ConfigureAwait(true);
        BookmarkTitleText = string.Empty;
    }

    public Task OpenBookmarkAsync(
        PlaybackBookmarkItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.OpenBookmarkAsync(item.Id, cancellationToken).AsTask();
    }

    public async Task RenameBookmarkAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedBookmarkItem is not { } item)
        {
            return;
        }

        await _controller.RenameBookmarkAsync(item.Id, BookmarkTitleText, cancellationToken).ConfigureAwait(true);
    }

    public Task RemoveBookmarkAsync(
        PlaybackBookmarkItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _controller.RemoveBookmarkAsync(item.Id, cancellationToken).AsTask();
    }

    public Task ClearBookmarksAsync(CancellationToken cancellationToken = default) =>
        _controller.ClearBookmarksAsync(cancellationToken: cancellationToken).AsTask();

    public Task TogglePauseAsync(CancellationToken cancellationToken = default) =>
        _controller.TogglePauseAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _controller.StopAsync(cancellationToken).AsTask();

    public Task SeekAsync(
        double seconds,
        bool relative = false,
        bool exact = true,
        CancellationToken cancellationToken = default) =>
        _controller.SeekAsync(seconds, relative, exact, cancellationToken).AsTask();

    public Task StepFrameAsync(bool backward = false, CancellationToken cancellationToken = default) =>
        _controller.StepFrameAsync(backward, cancellationToken).AsTask();

    public Task SetAbLoopStartAsync(CancellationToken cancellationToken = default) =>
        _controller.SetAbLoopAsync(startSeconds: PositionSeconds, cancellationToken: cancellationToken).AsTask();

    public Task SetAbLoopEndAsync(CancellationToken cancellationToken = default) =>
        _controller.SetAbLoopAsync(endSeconds: PositionSeconds, cancellationToken: cancellationToken).AsTask();

    public Task SetAbLoopAsync(
        double? startSeconds = null,
        double? endSeconds = null,
        CancellationToken cancellationToken = default) =>
        _controller.SetAbLoopAsync(startSeconds, endSeconds, cancellationToken).AsTask();

    public Task ClearAbLoopAsync(CancellationToken cancellationToken = default) =>
        _controller.ClearAbLoopAsync(cancellationToken).AsTask();

    public async Task<string> TakeScreenshotAsync(
        PlaybackScreenshotMode mode = PlaybackScreenshotMode.Subtitles,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        var path = await _controller.TakeScreenshotAsync(outputPath, mode, cancellationToken).ConfigureAwait(true);
        StatusMessage = $"截图已保存：{path}";
        return path;
    }

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) =>
        _volume.SetAsync(volume, cancellationToken);

    public Task SetBrightnessAsync(double brightness, CancellationToken cancellationToken = default) =>
        _controller.SetBrightnessAsync(brightness, cancellationToken).AsTask();

    public Task ToggleMuteAsync(CancellationToken cancellationToken = default) =>
        _controller.SetMutedAsync(!IsMuted, cancellationToken).AsTask();

    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default) =>
        _controller.SetSpeedAsync(speed, cancellationToken).AsTask();

    public Task SelectAudioDeviceAsync(
        PlaybackAudioDeviceChoiceViewModel choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return string.Equals(choice.Name, _snapshot.AudioDevice, StringComparison.Ordinal)
            ? Task.CompletedTask
            : _controller.SelectAudioDeviceAsync(choice.Name, cancellationToken).AsTask();
    }

    public Task ApplyVideoFiltersAsync(CancellationToken cancellationToken = default) =>
        _controller.SetVideoFiltersAsync(VideoFilterText, cancellationToken).AsTask();

    public Task ApplyAudioFiltersAsync(CancellationToken cancellationToken = default) =>
        _controller.SetAudioFiltersAsync(AudioFilterText, cancellationToken).AsTask();

    public Task ClearVideoFiltersAsync(CancellationToken cancellationToken = default)
    {
        VideoFilterText = string.Empty;
        return _controller.SetVideoFiltersAsync(string.Empty, cancellationToken).AsTask();
    }

    public Task ClearAudioFiltersAsync(CancellationToken cancellationToken = default)
    {
        AudioFilterText = string.Empty;
        return _controller.SetAudioFiltersAsync(string.Empty, cancellationToken).AsTask();
    }

    public Task AddShaderAsync(string path, CancellationToken cancellationToken = default) =>
        _controller.AddShaderAsync(path, cancellationToken).AsTask();

    public Task RemoveSelectedShaderAsync(CancellationToken cancellationToken = default) =>
        SelectedShaderPath is { } path
            ? _controller.RemoveShaderAsync(path, cancellationToken).AsTask()
            : Task.CompletedTask;

    public Task ClearShadersAsync(CancellationToken cancellationToken = default) =>
        _controller.ClearShadersAsync(cancellationToken).AsTask();

    public async Task ApplyColorSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _controller.SetColorSettingsAsync(new PlaybackColorSettings
        {
            TargetColorspaceHint = SelectedTargetColorspaceHint.Value,
            TargetColorspaceHintMode = SelectedTargetColorspaceHintMode.Value,
            TargetPrimaries = SelectedTargetPrimaries.Value,
            TargetTransfer = SelectedTargetTransfer.Value,
            TargetPeak = TargetPeakText,
            ToneMapping = SelectedToneMapping.Value,
            GamutMappingMode = SelectedGamutMappingMode.Value,
            HdrComputePeak = SelectedHdrComputePeak.Value,
        }, cancellationToken).ConfigureAwait(true);
        InitializeColorControls(_controller.Snapshot.ColorSettings);
        AdvancedResult = "色彩与 HDR 选项已应用。";
    }

    public async Task ReadRuntimePropertyAsync(CancellationToken cancellationToken = default)
    {
        RuntimePropertyValue = await _controller.GetRuntimePropertyAsync(RuntimePropertyName, cancellationToken)
            .ConfigureAwait(true);
        AdvancedResult = $"已读取属性 {RuntimePropertyName.Trim()}。";
    }

    public async Task WriteRuntimePropertyAsync(CancellationToken cancellationToken = default)
    {
        await _controller.SetRuntimePropertyAsync(RuntimePropertyName, RuntimePropertyValue, cancellationToken)
            .ConfigureAwait(true);
        AdvancedResult = $"已写入属性 {RuntimePropertyName.Trim()}。";
    }

    public async Task ExecuteRuntimeCommandAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> arguments = string.IsNullOrEmpty(RuntimeCommandArguments)
            ? Array.Empty<string>()
            : RuntimeCommandArguments
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();
        await _controller.ExecuteCommandAsync(RuntimeCommandName, arguments, cancellationToken).ConfigureAwait(true);
        AdvancedResult = $"mpv 命令 {RuntimeCommandName.Trim()} 执行成功。";
    }

    public Task SelectTrackAsync(
        PlaybackTrackChoiceViewModel choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.Type == PlaybackTrackType.Subtitle)
        {
            _subtitleDiscoveryCancellation?.Cancel();
            _manualSubtitleForCurrentOpen = true;
            _manualSubtitleRevision++;
        }
        var selected = _snapshot.Tracks.FirstOrDefault(track => track.Type == choice.Type && track.IsSelected);
        return selected?.Id == choice.Id || selected is null && choice.Id is null
            ? Task.CompletedTask
            : _controller.SelectTrackAsync(choice.Type, choice.Id, cancellationToken).AsTask();
    }

    internal Task RestoreVideoOutputAsync(long trackId, CancellationToken cancellationToken = default) =>
        _controller.RestoreVideoOutputAsync(trackId, cancellationToken).AsTask();

    public Task AddSubtitleAsync(
        string source,
        bool select = true,
        CancellationToken cancellationToken = default,
        PlaybackExternalSubtitleSource? persistentSource = null,
        bool remember = true)
    {
        _subtitleDiscoveryCancellation?.Cancel();
        _manualSubtitleForCurrentOpen = true;
        _manualSubtitleRevision++;
        return _controller.AddSubtitleAsync(source, select, cancellationToken, persistentSource, remember).AsTask();
    }

    public async Task AddAutomaticSubtitleAsync(
        string source,
        bool select,
        CancellationToken cancellationToken = default,
        PlaybackExternalSubtitleSource? persistentSource = null)
    {
        var preferredEmbeddedId = select ? null : _snapshot.Tracks.FirstOrDefault(static track =>
            track.Type == PlaybackTrackType.Subtitle && track.IsSelected && !track.IsExternal)?.Id;
        await _controller.AddSubtitleAsync(source, select, cancellationToken,
            persistentSource, remember: false).ConfigureAwait(true);
        if (preferredEmbeddedId is { } id)
        {
            await _controller.SelectTrackAsync(PlaybackTrackType.Subtitle, id, cancellationToken, remember: false)
                .ConfigureAwait(true);
        }
    }

    public Task SetSubtitleVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        _controller.SetSubtitleVisibleAsync(visible, cancellationToken).AsTask();

    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default) =>
        _subtitleDelay.SetAsync(seconds, cancellationToken);

    public Task SetSubtitleScaleAsync(double scale, CancellationToken cancellationToken = default) =>
        _subtitleScale.SetAsync(scale, cancellationToken);

    public Task SetSubtitlePositionAsync(double position, CancellationToken cancellationToken = default) =>
        _subtitlePosition.SetAsync(position, cancellationToken);

    public Task SendKeyDownAsync(string key, CancellationToken cancellationToken = default) =>
        _controller.SendKeyDownAsync(key, cancellationToken).AsTask();

    public Task SendKeyUpAsync(string? key = null, CancellationToken cancellationToken = default) =>
        _controller.SendKeyUpAsync(key, cancellationToken).AsTask();

    public Task SendKeyPressAsync(
        string key,
        double scale = 1,
        CancellationToken cancellationToken = default) =>
        _controller.SendKeyPressAsync(key, scale, cancellationToken).AsTask();

    public Task SetMousePositionAsync(int x, int y, CancellationToken cancellationToken = default) =>
        _controller.SetMousePositionAsync(x, y, cancellationToken).AsTask();

    public Task WriteWatchLaterAsync(CancellationToken cancellationToken = default) =>
        _controller.WriteWatchLaterAsync(cancellationToken).AsTask();

    public void ReportSurfaceError(string message)
    {
        _applicationLog.Write(
            LogComponent.App,
            ApplicationLogLevel.Error,
            $"Video rendering initialization failed: {message}");
        StatusMessage = $"视频渲染初始化失败：{message}";
    }

    public void ReportSurfaceFallback(string message)
    {
        _applicationLog.Write(
            LogComponent.Mpv,
            ApplicationLogLevel.Warning,
            $"Video presentation backend fallback: {message}");
    }

    public void UpdatePresentationDiagnostics(VideoPresentationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (_presentationDiagnostics != diagnostics)
        {
            _presentationDiagnostics = diagnostics;
            OnPropertyChanged(nameof(PresentationDiagnostics));
            OnPropertyChanged(nameof(PresentationPathText));
            OnPropertyChanged(nameof(PresentationPerformanceText));
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastPresentationDiagnosticsLogTimestamp != 0 &&
            now - _lastPresentationDiagnosticsLogTimestamp < Stopwatch.Frequency * 10)
        {
            return;
        }

        _lastPresentationDiagnosticsLogTimestamp = now;
        var playbackDiagnostics = _snapshot.Diagnostics;
        _applicationLog.Write(
            LogComponent.Mpv,
            ApplicationLogLevel.Debug,
            $"Presentation decoder={playbackDiagnostics.HardwareDecoder} " +
            $"frameTransfer={playbackDiagnostics.FrameTransferPath} " +
            $"decoderDrops={playbackDiagnostics.DecoderDroppedFrames} " +
            $"outputDrops={playbackDiagnostics.OutputDroppedFrames} " +
            $"mistimed={playbackDiagnostics.MistimedFrames} delayed={playbackDiagnostics.DelayedFrames} " +
            $"backend={diagnostics.Backend} sync={diagnostics.SynchronizationMode} " +
            $"cpuReadback={diagnostics.UsesCpuReadback} copiesPerFrame={diagnostics.CanXGpuCopiesPerFrame} " +
            $"compositorCopiesPerFrame={diagnostics.CompositorGpuCopiesPerFrame} " +
            $"render={diagnostics.RenderOperations} submit={diagnostics.SubmitOperations} " +
            $"present={diagnostics.PresentedFrames} gpuCopies={diagnostics.CanXGpuCopies} " +
            $"blockingFinishes={diagnostics.BlockingFinishCalls} " +
            $"renderAverageMs={diagnostics.AverageRenderMilliseconds:0.###} " +
            $"renderP95Ms={diagnostics.P95RenderMilliseconds:0.###} " +
            $"submitAverageMs={diagnostics.AverageSubmitMilliseconds:0.###} " +
            $"submitP95Ms={diagnostics.P95SubmitMilliseconds:0.###}.");
    }

    public void ReportOperationError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _applicationLog.Write(
            LogComponent.App,
            ApplicationLogLevel.Error,
            "A playback operation failed.",
            exception);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _sleepSubscription?.Dispose();
        _sleepInhibition?.Dispose();
        _subtitleDiscoveryCancellation?.Cancel();
        _subtitleDiscoveryCancellation?.Dispose();
        _subscription.Dispose();
        await _controller.DisposeAsync().ConfigureAwait(false);
    }

    private void Apply(PlaybackSnapshot snapshot)
    {
        // Unsubscribing cannot remove playback notifications already queued on
        // the UI dispatcher. In particular, Stop can arrive after DisposeAsync.
        if (_isDisposed) return;
        var previousSnapshot = _snapshot;
        var tracksChanged = !ReferenceEquals(_snapshot.Tracks, snapshot.Tracks);
        // mpv returns a fresh playlist every second during playback. Rebinding
        // identical rows closes their active tooltips and starts hover again.
        var playlistChanged = !ReferenceEquals(_snapshot.Playlist, snapshot.Playlist) &&
            !_snapshot.Playlist.SequenceEqual(snapshot.Playlist);
        var recentChanged = !_snapshot.RecentItems.SequenceEqual(snapshot.RecentItems);
        var chaptersChanged = !ReferenceEquals(_snapshot.Chapters, snapshot.Chapters);
        var bookmarksChanged = !ReferenceEquals(_snapshot.Bookmarks, snapshot.Bookmarks);
        var audioDevicesChanged = !ReferenceEquals(_snapshot.AudioDevices, snapshot.AudioDevices) ||
            !string.Equals(_snapshot.AudioDevice, snapshot.AudioDevice, StringComparison.Ordinal);
        var shadersChanged = !ReferenceEquals(_snapshot.Shaders, snapshot.Shaders);
        _snapshot = snapshot;
        _volume.ObserveSourceValue();
        _subtitleDelay.ObserveSourceValue();
        _subtitleScale.ObserveSourceValue();
        _subtitlePosition.ObserveSourceValue();
        HandleAutomaticSubtitle(snapshot, previousSnapshot);
        if (tracksChanged)
        {
            UpdateTrackChoices(snapshot.Tracks);
        }

        if (playlistChanged)
        {
            UpdatePlaylist(snapshot.Playlist);
        }

        if (recentChanged)
        {
            UpdateRecentItems(snapshot.RecentItems);
        }

        if (chaptersChanged)
        {
            UpdateChapters(snapshot.Chapters);
        }

        if (bookmarksChanged)
        {
            UpdateBookmarks(snapshot.Bookmarks);
        }

        if (audioDevicesChanged)
        {
            UpdateAudioDevices(snapshot.AudioDevices, snapshot.AudioDevice);
        }

        if (shadersChanged)
        {
            UpdateShaders(snapshot.Shaders);
        }

        if (string.Equals(_videoFilterText, previousSnapshot.VideoFilters, StringComparison.Ordinal) &&
            !string.Equals(previousSnapshot.VideoFilters, snapshot.VideoFilters, StringComparison.Ordinal))
        {
            VideoFilterText = snapshot.VideoFilters;
        }

        if (string.Equals(_audioFilterText, previousSnapshot.AudioFilters, StringComparison.Ordinal) &&
            !string.Equals(previousSnapshot.AudioFilters, snapshot.AudioFilters, StringComparison.Ordinal))
        {
            AudioFilterText = snapshot.AudioFilters;
        }

        SynchronizeColorControls(previousSnapshot.ColorSettings, snapshot.ColorSettings);

        StatusMessage = snapshot.State switch
        {
            PlaybackState.Idle => "播放器已就绪",
            PlaybackState.Loading => "正在打开媒体…",
            PlaybackState.Playing => "正在播放",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Ended => "播放结束",
            PlaybackState.Error => $"播放失败：{snapshot.ErrorMessage}",
            _ => StatusMessage,
        };
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PlayPauseText));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(DurationMaximum));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(IsSeekable));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanSeekPreviousChapter));
        OnPropertyChanged(nameof(CanSeekNextChapter));
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(IsSubtitleVisible));
        OnPropertyChanged(nameof(SubtitleDelaySeconds));
        OnPropertyChanged(nameof(SubtitleDelayText));
        OnPropertyChanged(nameof(SubtitleScale));
        OnPropertyChanged(nameof(SubtitleScaleText));
        OnPropertyChanged(nameof(SubtitlePosition));
        OnPropertyChanged(nameof(SubtitlePositionText));
        OnPropertyChanged(nameof(MuteText));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(CanDecreaseSpeed));
        OnPropertyChanged(nameof(CanIncreaseSpeed));
        OnPropertyChanged(nameof(AbLoopASeconds));
        OnPropertyChanged(nameof(AbLoopBSeconds));
        OnPropertyChanged(nameof(VideoWidth));
        OnPropertyChanged(nameof(VideoHeight));
        OnPropertyChanged(nameof(Tracks));
        OnPropertyChanged(nameof(Diagnostics));
        OnPropertyChanged(nameof(ColorSettings));
        OnPropertyChanged(nameof(MediaDetails));
        OnPropertyChanged(nameof(CanPlayPrevious));
        OnPropertyChanged(nameof(CanPlayNext));
        OnPropertyChanged(nameof(CanOpenSelectedChapter));
        OnPropertyChanged(nameof(CanAddBookmark));
        OnPropertyChanged(nameof(CanOpenSelectedBookmark));
        OnPropertyChanged(nameof(AbLoopText));
        OnPropertyChanged(nameof(CanRemoveShader));
        OnPropertyChanged(nameof(DecodePathText));
        OnPropertyChanged(nameof(RenderPathText));
        OnPropertyChanged(nameof(InputColorText));
        OnPropertyChanged(nameof(TargetColorText));
        OnPropertyChanged(nameof(PerformanceText));
        OnPropertyChanged(nameof(FrameStatisticsText));
        OnPropertyChanged(nameof(BitrateCacheText));
    }

    private void InitializeColorControls(PlaybackColorSettings settings)
    {
        SelectedTargetColorspaceHint = SelectRuntimeOption(TargetColorspaceHintOptions, settings.TargetColorspaceHint);
        SelectedTargetColorspaceHintMode = SelectRuntimeOption(
            TargetColorspaceHintModeOptions,
            settings.TargetColorspaceHintMode);
        SelectedTargetPrimaries = SelectRuntimeOption(TargetPrimariesOptions, settings.TargetPrimaries);
        SelectedTargetTransfer = SelectRuntimeOption(TargetTransferOptions, settings.TargetTransfer);
        TargetPeakText = settings.TargetPeak;
        SelectedToneMapping = SelectRuntimeOption(ToneMappingOptions, settings.ToneMapping);
        SelectedGamutMappingMode = SelectRuntimeOption(GamutMappingOptions, settings.GamutMappingMode);
        SelectedHdrComputePeak = SelectRuntimeOption(HdrComputePeakOptions, settings.HdrComputePeak);
    }

    private void SynchronizeColorControls(PlaybackColorSettings previous, PlaybackColorSettings current)
    {
        if (string.Equals(SelectedTargetColorspaceHint.Value, previous.TargetColorspaceHint, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTargetColorspaceHint = SelectRuntimeOption(TargetColorspaceHintOptions, current.TargetColorspaceHint);
        }

        if (string.Equals(SelectedTargetColorspaceHintMode.Value, previous.TargetColorspaceHintMode, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTargetColorspaceHintMode = SelectRuntimeOption(
                TargetColorspaceHintModeOptions,
                current.TargetColorspaceHintMode);
        }

        if (string.Equals(SelectedTargetPrimaries.Value, previous.TargetPrimaries, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTargetPrimaries = SelectRuntimeOption(TargetPrimariesOptions, current.TargetPrimaries);
        }

        if (string.Equals(SelectedTargetTransfer.Value, previous.TargetTransfer, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTargetTransfer = SelectRuntimeOption(TargetTransferOptions, current.TargetTransfer);
        }

        if (string.Equals(TargetPeakText, previous.TargetPeak, StringComparison.OrdinalIgnoreCase))
        {
            TargetPeakText = current.TargetPeak;
        }

        if (string.Equals(SelectedToneMapping.Value, previous.ToneMapping, StringComparison.OrdinalIgnoreCase))
        {
            SelectedToneMapping = SelectRuntimeOption(ToneMappingOptions, current.ToneMapping);
        }

        if (string.Equals(SelectedGamutMappingMode.Value, previous.GamutMappingMode, StringComparison.OrdinalIgnoreCase))
        {
            SelectedGamutMappingMode = SelectRuntimeOption(GamutMappingOptions, current.GamutMappingMode);
        }

        if (string.Equals(SelectedHdrComputePeak.Value, previous.HdrComputePeak, StringComparison.OrdinalIgnoreCase))
        {
            SelectedHdrComputePeak = SelectRuntimeOption(HdrComputePeakOptions, current.HdrComputePeak);
        }
    }

    private void HandleAutomaticSubtitle(PlaybackSnapshot snapshot, PlaybackSnapshot previous)
    {
        if (snapshot.State is PlaybackState.Idle or PlaybackState.Ended or PlaybackState.Error)
        {
            if (previous.State != snapshot.State)
            {
                _subtitleDiscoveryCancellation?.Cancel();
                _discoveredSubtitleLoader = null;
                _discoveredSubtitleChoices = [];
                _pendingDiscoveredSource = null;
                _pendingDiscoveredCandidates = null;
                _pendingDiscoveredLoader = null;
                OnPropertyChanged(nameof(DiscoveredSubtitleChoices));
                OnPropertyChanged(nameof(HasDiscoveredSubtitleChoices));
            }

            return;
        }

        var opened = snapshot.State == PlaybackState.Loading &&
            (previous.State != PlaybackState.Loading ||
             !string.Equals(snapshot.Source, previous.Source, StringComparison.Ordinal));
        var changedWhilePlaying = snapshot.State is PlaybackState.Playing or PlaybackState.Paused &&
            !string.Equals(snapshot.Source, previous.Source, StringComparison.Ordinal);
        if (opened || changedWhilePlaying)
        {
            _subtitleDiscoveryCancellation?.Cancel();
            _subtitleDiscoveryCancellation?.Dispose();
            _subtitleDiscoveryCancellation = new CancellationTokenSource();
            _subtitleGeneration++;
            _manualSubtitleForCurrentOpen = _suppressSubtitleForNextOpen;
            _disableSubtitlesForCurrentOpen = _disableSubtitlesForNextOpen;
            _suppressSubtitleForNextOpen = false;
            _disableSubtitlesForNextOpen = false;
            _discoveredSubtitleLoader = null;
            _discoveredSubtitleChoices = [];
            OnPropertyChanged(nameof(DiscoveredSubtitleChoices));
            OnPropertyChanged(nameof(HasDiscoveredSubtitleChoices));
            if (_pendingDiscoveredSource is not null &&
                !string.Equals(snapshot.Source, _pendingDiscoveredSource, StringComparison.Ordinal))
            {
                _pendingDiscoveredSource = null;
                _pendingDiscoveredCandidates = null;
                _pendingDiscoveredLoader = null;
            }
        }

        if (_pendingDiscoveredSource is not null &&
            string.Equals(snapshot.Source, _pendingDiscoveredSource, StringComparison.Ordinal) &&
            _pendingDiscoveredCandidates is { } pendingCandidates &&
            _pendingDiscoveredLoader is { } pendingLoader)
        {
            ApplyDiscoveredSubtitleChoices(pendingCandidates, pendingLoader);
            _pendingDiscoveredSource = null;
            _pendingDiscoveredCandidates = null;
            _pendingDiscoveredLoader = null;
        }

        if (snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused) ||
            string.IsNullOrWhiteSpace(snapshot.Source) ||
            _subtitleStartedGeneration == _subtitleGeneration)
        {
            return;
        }

        _subtitleStartedGeneration = _subtitleGeneration;
        var generation = _subtitleGeneration;
        var token = _subtitleDiscoveryCancellation?.Token ?? CancellationToken.None;
        if (_disableSubtitlesForCurrentOpen)
        {
            _ = DisableSubtitlesAsync(snapshot.Source, generation, token);
            return;
        }

        if (_manualSubtitleForCurrentOpen) return;
        var remembered = FindRememberedPreferences(snapshot);
        if (remembered is { } preferences &&
            (preferences.SubtitleTrack is not null || preferences.ExternalSubtitles.Count > 0))
        {
            _manualSubtitleForCurrentOpen = true;
            if (preferences.ExternalSubtitles.Count > 0)
                _ = RestoreRememberedSubtitlesAsync(snapshot.Source, preferences, generation,
                    _manualSubtitleRevision, token);
            return;
        }

        StartAutomaticSubtitleDiscovery(snapshot, generation, token);
    }

    private void StartAutomaticSubtitleDiscovery(PlaybackSnapshot snapshot, long generation,
        CancellationToken token)
    {
        if (_subtitleSettingsProvider?.Invoke() is not { } settings)
        {
            return;
        }

        if (_mediaSourceSubtitleLoader is { } loader)
        {
            if (!string.Equals(settings.SubtitleAuto, "no", StringComparison.OrdinalIgnoreCase))
                _ = loader(snapshot.Source, token);
            return;
        }

        if (!string.Equals(settings.SubtitleAuto, "exact", StringComparison.OrdinalIgnoreCase) ||
            !TryGetLocalPath(snapshot.Source, out var localPath)) return;

        _ = LoadLocalSubtitleAsync(localPath, settings.SubtitleLanguages, generation, token);
    }

    private async Task DisableSubtitlesAsync(string source, long generation, CancellationToken cancellationToken)
    {
        try
        {
            if (generation == _subtitleGeneration &&
                string.Equals(source, Source, StringComparison.Ordinal))
            {
                await _controller.SelectTrackAsync(PlaybackTrackType.Subtitle, null, cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _applicationLog.Write(LogComponent.Mpv, ApplicationLogLevel.Warning,
                "Failed to disable subtitles selected by the user.", exception);
        }
    }

    private async Task LoadLocalSubtitleAsync(
        string videoPath,
        string preferredLanguages,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var matches = await ExternalSubtitleDiscovery.FindLocalAsync(
                videoPath, preferredLanguages, cancellationToken).ConfigureAwait(true);
            if (matches.Count == 0 || cancellationToken.IsCancellationRequested ||
                generation != _subtitleGeneration || _manualSubtitleForCurrentOpen ||
                !string.Equals(videoPath, Source, StringComparison.Ordinal))
            {
                return;
            }

            SetDiscoveredSubtitleChoices(videoPath,
                matches.Skip(1).Select(static match => match.Candidate).ToArray(),
                (candidate, token) => AddSubtitleAsync(candidate.Source, cancellationToken: token));

            var best = matches[0];
            var selectedPreferredInternal = _snapshot.Tracks.Any(track =>
                track.Type == PlaybackTrackType.Subtitle && track.IsSelected && !track.IsExternal &&
                ExternalSubtitleMatcher.IsPreferredLanguage(track.Language, preferredLanguages));
            var select = best.SelectOverEmbedded || !selectedPreferredInternal;
            var loaded = _snapshot.Tracks.FirstOrDefault(track => track.Type == PlaybackTrackType.Subtitle &&
                track.IsExternal && string.Equals(track.ExternalFileName, best.Candidate.Source, StringComparison.OrdinalIgnoreCase));
            if (loaded is not null)
            {
                if (select && !loaded.IsSelected)
                {
                    await _controller.SelectTrackAsync(PlaybackTrackType.Subtitle, loaded.Id, cancellationToken,
                        remember: false)
                        .ConfigureAwait(true);
                }
                return;
            }

            await AddAutomaticSubtitleAsync(best.Candidate.Source, select, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Subtitle discovery must never interrupt video playback.
            _applicationLog.Write(LogComponent.Mpv, ApplicationLogLevel.Warning,
                "Automatic local subtitle loading failed.", exception);
        }
    }

    private static bool TryGetLocalPath(string source, out string path)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                path = string.Empty;
                return false;
            }

            path = uri.LocalPath;
            return true;
        }

        path = source;
        return Path.IsPathFullyQualified(path);
    }

    private void UpdateTrackChoices(IReadOnlyList<PlaybackTrack> tracks)
    {
        _videoTrackChoices = BuildTrackChoices(PlaybackTrackType.Video, "关闭视频", tracks);
        _audioTrackChoices = BuildTrackChoices(PlaybackTrackType.Audio, "关闭音频", tracks);
        _subtitleTrackChoices = BuildTrackChoices(PlaybackTrackType.Subtitle, "关闭字幕轨道", tracks);
        _selectedVideoTrack = SelectCurrentChoice(_videoTrackChoices, tracks, PlaybackTrackType.Video);
        _selectedAudioTrack = SelectCurrentChoice(_audioTrackChoices, tracks, PlaybackTrackType.Audio);
        _selectedSubtitleTrack = SelectCurrentChoice(_subtitleTrackChoices, tracks, PlaybackTrackType.Subtitle);
        OnPropertyChanged(nameof(VideoTrackChoices));
        OnPropertyChanged(nameof(AudioTrackChoices));
        OnPropertyChanged(nameof(SubtitleTrackChoices));
        OnPropertyChanged(nameof(SelectedVideoTrack));
        OnPropertyChanged(nameof(SelectedAudioTrack));
        OnPropertyChanged(nameof(SelectedSubtitleTrack));
    }

    private void UpdatePlaylist(IReadOnlyList<PlaybackPlaylistItem> items)
    {
        var selectedIndex = _selectedPlaylistItem?.Index;
        _playlistItems = items.Select(static item => new PlaybackPlaylistItemViewModel(
            item.Index,
            item.Source,
            item.Title,
            item.IsCurrent)).ToList();
        _selectedPlaylistItem = selectedIndex is { } index
            ? _playlistItems.FirstOrDefault(item => item.Index == index)
            : _playlistItems.FirstOrDefault(static item => item.IsCurrent);
        OnPropertyChanged(nameof(PlaylistItems));
        OnPropertyChanged(nameof(SelectedPlaylistItem));
        OnPropertyChanged(nameof(HasPlaylistItems));
        OnPropertyChanged(nameof(PlaylistSummary));
        OnPropertyChanged(nameof(CanPlaySelectedPlaylistItem));
        OnPropertyChanged(nameof(CanMoveSelectedPlaylistItemUp));
        OnPropertyChanged(nameof(CanMoveSelectedPlaylistItemDown));
    }

    private void UpdateRecentItems(IReadOnlyList<PlaybackRecentItem> items)
    {
        var selected = _selectedRecentItem;
        var existing = _recentItems.ToDictionary(static item => (item.Source, item.IsMediaSource, item.MediaSourceProfileId));
        var updated = items.Select(item =>
        {
            if (!existing.TryGetValue((item.Source, item.IsMediaSource, item.MediaSourceProfileId), out var row))
                return new PlaybackRecentItemViewModel(item);
            row.Update(item);
            return row;
        }).ToArray();
        SynchronizeHistoryRows(_recentItems, updated);

        var existingFolders = _recentFolders.ToDictionary(static folder => (folder.Directory.AbsoluteUri, folder.MediaSourceProfileId));
        var folders = PlaybackRecentFolders.Build(items).Select(folder =>
        {
            if (!existingFolders.TryGetValue((folder.Directory.AbsoluteUri, folder.MediaSourceProfileId), out var row))
                return folder;
            row.UpdateLastPlayedAt(folder.LastPlayedAt);
            return row;
        }).ToArray();
        SynchronizeHistoryRows(_recentFolders, folders);
        SelectedRecentItem = selected is not null && _recentItems.Contains(selected) ? selected : null;

        // Keep the collection and row identities stable. Consumers such as the
        // media browser still need to know when history values have changed.
        OnPropertyChanged(nameof(RecentItems));
        OnPropertyChanged(nameof(RecentFolders));
        OnPropertyChanged(nameof(HasRecentItems));
        OnPropertyChanged(nameof(HasRecentFolders));
        OnPropertyChanged(nameof(RecentSummary));
        OnPropertyChanged(nameof(CanOpenSelectedRecentItem));
        OnPropertyChanged(nameof(CanRemoveSelectedRecentItem));
        OnPropertyChanged(nameof(SelectedRecentActionText));
    }

    private static void SynchronizeHistoryRows<T>(ObservableCollection<T> rows, IReadOnlyList<T> updated) where T : class
    {
        var retained = updated.ToHashSet();
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!retained.Contains(rows[index])) rows.RemoveAt(index);

        for (var index = 0; index < updated.Count; index++)
        {
            if (index < rows.Count && ReferenceEquals(rows[index], updated[index])) continue;
            var previousIndex = rows.IndexOf(updated[index]);
            if (previousIndex < 0) rows.Insert(index, updated[index]);
            else rows.Move(previousIndex, index);
        }
    }

    private void SetRecentFolderView(bool showFolders)
    {
        if (_showRecentFolders == showFolders)
        {
            return;
        }

        _showRecentFolders = showFolders;
        OnPropertyChanged(nameof(ShowRecentFiles));
        OnPropertyChanged(nameof(ShowRecentFolders));
    }

    private void UpdateChapters(IReadOnlyList<PlaybackChapter> items)
    {
        var selectedIndex = _selectedChapterItem?.Index;
        _chapterItems = items.Select(static item => new PlaybackChapterItemViewModel(
            item.Index,
            item.Title,
            item.TimeSeconds,
            item.IsCurrent)).ToList();
        _selectedChapterItem = selectedIndex is { } index
            ? _chapterItems.FirstOrDefault(item => item.Index == index)
            : _chapterItems.FirstOrDefault(static item => item.IsCurrent);
        OnPropertyChanged(nameof(ChapterItems));
        OnPropertyChanged(nameof(SelectedChapterItem));
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(CanSeekPreviousChapter));
        OnPropertyChanged(nameof(CanSeekNextChapter));
        OnPropertyChanged(nameof(ChapterSummary));
        OnPropertyChanged(nameof(CanOpenSelectedChapter));
    }

    private void UpdateBookmarks(IReadOnlyList<PlaybackBookmark> items)
    {
        var selectedId = _selectedBookmarkItem?.Id;
        _bookmarkItems = items.Select(static item => new PlaybackBookmarkItemViewModel(
            item.Id,
            item.Source,
            item.Title,
            item.PositionSeconds,
            item.CreatedAt)).ToList();
        _selectedBookmarkItem = selectedId is null
            ? null
            : _bookmarkItems.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        if (_selectedBookmarkItem is null && selectedId is not null)
        {
            _bookmarkTitleText = string.Empty;
            OnPropertyChanged(nameof(BookmarkTitleText));
        }

        OnPropertyChanged(nameof(BookmarkItems));
        OnPropertyChanged(nameof(SelectedBookmarkItem));
        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(BookmarkSummary));
        OnPropertyChanged(nameof(CanOpenSelectedBookmark));
        OnPropertyChanged(nameof(CanRenameSelectedBookmark));
    }

    private void UpdateAudioDevices(IReadOnlyList<PlaybackAudioDevice> devices, string selectedName)
    {
        _audioDeviceChoices = devices
            .Select(static device => new PlaybackAudioDeviceChoiceViewModel(
                device.Name,
                string.Equals(device.Name, "auto", StringComparison.Ordinal)
                    ? "自动选择"
                    : $"{device.Description}  ·  {device.Name}"))
            .ToList();
        if (_audioDeviceChoices.All(static choice => choice.Name != "auto"))
        {
            _audioDeviceChoices = [new PlaybackAudioDeviceChoiceViewModel("auto", "自动选择"), .. _audioDeviceChoices];
        }

        _selectedAudioDevice = _audioDeviceChoices.FirstOrDefault(choice =>
            string.Equals(choice.Name, selectedName, StringComparison.Ordinal)) ?? _audioDeviceChoices[0];
        OnPropertyChanged(nameof(AudioDeviceChoices));
        OnPropertyChanged(nameof(SelectedAudioDevice));
    }

    private void UpdateShaders(IReadOnlyList<string> shaders)
    {
        var selectedPath = _selectedShaderPath;
        _shaderPaths = shaders.ToList();
        _selectedShaderPath = selectedPath is null
            ? null
            : _shaderPaths.FirstOrDefault(path => string.Equals(path, selectedPath, StringComparison.Ordinal));
        OnPropertyChanged(nameof(ShaderPaths));
        OnPropertyChanged(nameof(SelectedShaderPath));
        OnPropertyChanged(nameof(CanRemoveShader));
    }

    private static IReadOnlyList<PlaybackTrackChoiceViewModel> BuildTrackChoices(
        PlaybackTrackType type,
        string disabledLabel,
        IReadOnlyList<PlaybackTrack> tracks)
    {
        var choices = new List<PlaybackTrackChoiceViewModel>
        {
            new(type, null, disabledLabel),
        };
        choices.AddRange(tracks.Where(track => track.Type == type).Select(track => new PlaybackTrackChoiceViewModel(
            type,
            track.Id,
            FormatTrackLabel(track))));
        return choices;
    }

    private static PlaybackTrackChoiceViewModel SelectCurrentChoice(
        IReadOnlyList<PlaybackTrackChoiceViewModel> choices,
        IReadOnlyList<PlaybackTrack> tracks,
        PlaybackTrackType type)
    {
        var selectedId = tracks.FirstOrDefault(track => track.Type == type && track.IsSelected)?.Id;
        return choices.First(choice => choice.Id == selectedId);
    }

    private static string FormatTrackLabel(PlaybackTrack track)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Language))
        {
            details.Add(track.Language);
        }

        if (!string.IsNullOrWhiteSpace(track.Title))
        {
            details.Add(track.Title);
        }

        if (!string.IsNullOrWhiteSpace(track.Codec))
        {
            details.Add(track.Codec);
        }

        if (track.IsExternal)
        {
            details.Add("外部");
        }

        if (track.IsDefault)
        {
            details.Add("默认");
        }

        if (track.IsForced)
        {
            details.Add("强制");
        }

        var suffix = details.Count == 0 ? string.Empty : $" · {string.Join(" · ", details)}";
        return $"#{track.Id}{suffix}";
    }

    private void Post(Action action)
    {
        if (_synchronizationContext is null || SynchronizationContext.Current == _synchronizationContext)
        {
            action();
        }
        else
        {
            _synchronizationContext.Post(static state => ((Action)state!).Invoke(), action);
        }
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private static PlaybackRuntimeOption SelectRuntimeOption(
        IReadOnlyList<PlaybackRuntimeOption> options,
        string value) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)) ??
        options[0];

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string FormatColor(
        string label,
        string matrix,
        string primaries,
        string transfer,
        string levels,
        double? signalPeak) =>
        $"{label} Matrix {ValueOrDash(matrix)} · Primaries {ValueOrDash(primaries)} · TRC {ValueOrDash(transfer)} · " +
        $"Levels {ValueOrDash(levels)} · Peak {FormatNumber(signalPeak, "0.###")}";

    private static string FormatNumber(double? value, string format) =>
        value is { } number && double.IsFinite(number) ? number.ToString(format) : "—";

    private static string FormatMilliseconds(double? seconds) =>
        seconds is { } value && double.IsFinite(value) ? $"{value * 1000:+0.0;-0.0;0.0} ms" : "—";

    private static string FormatRate(double? value, bool bytes)
    {
        if (value is not { } rate || !double.IsFinite(rate) || rate < 0)
        {
            return "—";
        }

        var unit = bytes ? "B/s" : "bit/s";
        var divisor = 1d;
        var prefix = string.Empty;
        if (rate >= 1_000_000)
        {
            divisor = 1_000_000;
            prefix = "M";
        }
        else if (rate >= 1_000)
        {
            divisor = 1_000;
            prefix = "k";
        }

        return $"{rate / divisor:0.##} {prefix}{unit}";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record PlaybackTrackChoiceViewModel(
    PlaybackTrackType Type,
    long? Id,
    string Label);

public sealed record PlaybackAudioDeviceChoiceViewModel(string Name, string Label);

public sealed record PlaybackRuntimeOption(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record PlaybackPlaylistItemViewModel(
    int Index,
    string Source,
    string Title,
    bool IsCurrent)
{
    public string NumberText => $"{Index + 1}.";

    public string StateText => IsCurrent ? "正在播放" : string.Empty;
}

public sealed class PlaybackRecentItemViewModel(PlaybackRecentItem snapshot) : INotifyPropertyChanged
{
    private PlaybackRecentItem _snapshot = snapshot;

    public PlaybackRecentItemViewModel(string Source, string Title, double PositionSeconds,
        double DurationSeconds, DateTimeOffset LastPlayedAt, bool IsCompleted,
        bool IsMediaSource, string? MediaSourceProfileId)
        : this(new PlaybackRecentItem
        {
            Source = Source, Title = Title, PositionSeconds = PositionSeconds,
            DurationSeconds = DurationSeconds, LastPlayedAt = LastPlayedAt,
            IsCompleted = IsCompleted, IsMediaSource = IsMediaSource, MediaSourceProfileId = MediaSourceProfileId,
        }) { }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Source => _snapshot.Source;
    public string Title => _snapshot.Title;
    public double PositionSeconds => _snapshot.PositionSeconds;
    public double DurationSeconds => _snapshot.DurationSeconds;
    public DateTimeOffset LastPlayedAt => _snapshot.LastPlayedAt;
    public bool IsCompleted => _snapshot.IsCompleted;
    public bool IsMediaSource => _snapshot.IsMediaSource;
    public string? MediaSourceProfileId => _snapshot.MediaSourceProfileId;

    internal void Update(PlaybackRecentItem snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;
        if (previous.Title != snapshot.Title) Notify(nameof(Title));
        if (previous.PositionSeconds != snapshot.PositionSeconds) Notify(nameof(PositionSeconds));
        if (previous.DurationSeconds != snapshot.DurationSeconds) Notify(nameof(DurationSeconds));
        if (previous.IsCompleted != snapshot.IsCompleted) Notify(nameof(IsCompleted));
        if (previous.PositionSeconds != snapshot.PositionSeconds || previous.DurationSeconds != snapshot.DurationSeconds ||
            previous.IsCompleted != snapshot.IsCompleted)
        {
            Notify(nameof(Progress));
            Notify(nameof(ProgressText));
        }
        if (previous.LastPlayedAt != snapshot.LastPlayedAt)
        {
            Notify(nameof(LastPlayedAt));
            Notify(nameof(LastPlayedText));
        }
    }

    private void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public double Progress => IsCompleted ? 100 : DurationSeconds > 0
        ? Math.Clamp(PositionSeconds / DurationSeconds * 100, 0, 100)
        : 0;

    public string ProgressText => IsCompleted
        ? "已播完"
        : DurationSeconds > 0
            ? $"{FormatTime(PositionSeconds)} / {FormatTime(DurationSeconds)}"
            : "已播放";

    public string LastPlayedText => LastPlayedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string LocationText
    {
        get
        {
            if (Uri.TryCreate(Source, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile)
                {
                    var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
                    return $"{uri.Scheme}://{uri.Host}{port}";
                }

                return Path.GetDirectoryName(uri.LocalPath) ?? uri.LocalPath;
            }

            return Path.GetDirectoryName(Source) ?? Source;
        }
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }
}

public sealed record PlaybackChapterItemViewModel(
    int Index,
    string Title,
    double TimeSeconds,
    bool IsCurrent)
{
    public string TimeText => FormatTime(TimeSeconds);

    public string StateText => IsCurrent ? "当前" : string.Empty;

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }
}

public sealed record PlaybackBookmarkItemViewModel(
    string Id,
    string Source,
    string Title,
    double PositionSeconds,
    DateTimeOffset CreatedAt)
{
    public string PositionText
    {
        get
        {
            var value = TimeSpan.FromSeconds(Math.Max(0, PositionSeconds));
            return value.TotalHours >= 1
                ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
                : $"{value.Minutes}:{value.Seconds:00}";
        }
    }

    public string CreatedAtText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record DiscoveredSubtitleChoiceViewModel(ExternalSubtitleCandidate Candidate)
{
    public string Label => Candidate.FileName;

    public override string ToString() => Label;
}
