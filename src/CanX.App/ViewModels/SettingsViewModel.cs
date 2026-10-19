// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using CanX.Agent;
using CanX.Application.Playback;
using CanX.Core.Configuration;
using CanX.Core.Diagnostics;
using CanX.Core.Playback;
using CanX.Infrastructure.Configuration;

namespace CanX.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IApplicationSettingsStore _store;
    private readonly Action<string> _applyTheme;
    private readonly Action<string> _applyDownloadDirectory;
    private readonly IApplicationLog _applicationLog;
    private readonly IPlaybackController? _playbackController;
    private readonly IMpvOptionValidator? _mpvOptionValidator;
    private readonly Func<CancellationToken, ValueTask<bool>>? _saveDownloadSettings;
    private readonly string _defaultDownloadDirectory;
    private readonly CredentialAgentApiKeyProvider? _agentApiKeyProvider;
    private readonly IAgentModelCatalog? _agentModelCatalog;
    private readonly IAgentChatConnectionTester? _agentChatTester;
    private readonly bool _deferAgentCredentialStatusLoad;
    private Task? _agentCredentialStatusLoadTask;
    private ApplicationSettings _settings = new();
    private ThemeSettingOption _selectedTheme;
    private PlaybackWindowModeOption _selectedPlaybackWindowMode = new(false, "替换当前播放");
    private string _downloadDirectory;
    private bool _isLoggingEnabled;
    private MpvSettingsViewModel _mpv = new(new MpvSettings());
    private AgentSettingsViewModel _agent;
    private string _statusMessage = string.Empty;

    public SettingsViewModel(
        IApplicationSettingsStore store,
        string defaultDownloadDirectory,
        Action<string> applyTheme,
        Action<string> applyDownloadDirectory,
        IApplicationLog? applicationLog = null,
        IPlaybackController? playbackController = null,
        IMpvOptionValidator? mpvOptionValidator = null,
        Func<CancellationToken, ValueTask<bool>>? saveDownloadSettings = null,
        CredentialAgentApiKeyProvider? agentApiKeyProvider = null,
        bool deferAgentCredentialStatusLoad = false,
        IAgentModelCatalog? agentModelCatalog = null,
        IAgentChatConnectionTester? agentChatTester = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _defaultDownloadDirectory = Path.GetFullPath(defaultDownloadDirectory);
        _downloadDirectory = _defaultDownloadDirectory;
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _applyDownloadDirectory = applyDownloadDirectory ?? throw new ArgumentNullException(nameof(applyDownloadDirectory));
        _applicationLog = applicationLog ?? NullApplicationLog.Instance;
        _playbackController = playbackController;
        _mpvOptionValidator = mpvOptionValidator;
        _saveDownloadSettings = saveDownloadSettings;
        _agentApiKeyProvider = agentApiKeyProvider;
        _agentModelCatalog = agentModelCatalog;
        _agentChatTester = agentChatTester;
        _deferAgentCredentialStatusLoad = deferAgentCredentialStatusLoad;
        _agent = new AgentSettingsViewModel(new AgentSettings(), agentApiKeyProvider, agentModelCatalog, agentChatTester);
        _selectedTheme = ThemeOptions[0];
        SaveCommand = new AsyncCommand(SaveAsync, () => !string.IsNullOrWhiteSpace(DownloadDirectory));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Func<bool, Task>? PlaybackEndActionChanged;

    public event Action<MpvSettings>? SeekPreviewSettingsChanged;

    public IReadOnlyList<ThemeSettingOption> ThemeOptions { get; } =
    [
        new("System", "跟随系统"),
        new("Light", "浅色"),
        new("Dark", "深色"),
    ];

    public ThemeSettingOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetField(ref _selectedTheme, value);
            _applyTheme(value.Value);
        }
    }

    public IReadOnlyList<PlaybackWindowModeOption> PlaybackWindowModes { get; } =
    [
        new(false, "替换当前播放"),
        new(true, "打开多个播放窗口"),
    ];

    public PlaybackWindowModeOption SelectedPlaybackWindowMode
    {
        get => _selectedPlaybackWindowMode;
        set => SetField(ref _selectedPlaybackWindowMode, value);
    }

    public bool OpenMediaInNewWindow => _settings.OpenMediaInNewWindow;

    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set
        {
            if (SetField(ref _downloadDirectory, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoggingEnabled
    {
        get => _isLoggingEnabled;
        set
        {
            if (_isLoggingEnabled == value)
            {
                return;
            }

            try
            {
                _applicationLog.SetEnabled(value);
                SetField(ref _isLoggingEnabled, value);
                StatusMessage = string.Empty;
            }
            catch (Exception exception)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoggingEnabled)));
                StatusMessage = $"日志设置应用失败：{exception.Message}";
            }
        }
    }

    public MpvSettingsViewModel Mpv
    {
        get => _mpv;
        private set => SetField(ref _mpv, value);
    }

    public MpvSettings ActiveMpvSettings { get; private set; } = new();

    public AgentSettingsViewModel Agent
    {
        get => _agent;
        private set => SetField(ref _agent, value);
    }

    public string LogDirectory => _applicationLog.DirectoryPath;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatusMessage)));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public AsyncCommand SaveCommand { get; }

    public async ValueTask<string?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
            SelectedTheme = ThemeOptions.FirstOrDefault(option =>
                string.Equals(option.Value, _settings.ColorTheme, StringComparison.OrdinalIgnoreCase)) ?? ThemeOptions[0];
            DownloadDirectory = string.IsNullOrWhiteSpace(_settings.DownloadDirectory)
                ? _defaultDownloadDirectory
                : Path.GetFullPath(_settings.DownloadDirectory);
            IsLoggingEnabled = _settings.EnableLogging;
            SelectedPlaybackWindowMode = PlaybackWindowModes.First(option => option.OpenNewWindow == _settings.OpenMediaInNewWindow);
            Mpv = new MpvSettingsViewModel(_settings.Mpv ?? new MpvSettings());
            Agent = new AgentSettingsViewModel(_settings.Agent ?? new AgentSettings(), _agentApiKeyProvider, _agentModelCatalog, _agentChatTester);
            _agentCredentialStatusLoadTask = null;
            if (!_deferAgentCredentialStatusLoad)
            {
                await EnsureAgentCredentialStatusLoadedAsync(cancellationToken).ConfigureAwait(true);
            }
            _applyDownloadDirectory(DownloadDirectory);
            var mpvError = TryConfigureMpvStartup(Mpv.ToSettings());
            StatusMessage = string.Empty;
            return mpvError;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SelectedTheme = ThemeOptions[0];
            DownloadDirectory = _defaultDownloadDirectory;
            IsLoggingEnabled = false;
            SelectedPlaybackWindowMode = PlaybackWindowModes[0];
            Mpv = new MpvSettingsViewModel(new MpvSettings());
            Agent = new AgentSettingsViewModel(new AgentSettings(), _agentApiKeyProvider, _agentModelCatalog, _agentChatTester);
            _agentCredentialStatusLoadTask = null;
            _applyDownloadDirectory(DownloadDirectory);
            _ = TryConfigureMpvStartup(Mpv.ToSettings());
            StatusMessage = string.Empty;
            return $"设置读取失败：{exception.Message}\n\nCanX 已使用默认设置继续启动。";
        }
    }

    public async Task EnsureAgentCredentialStatusLoadedAsync(CancellationToken cancellationToken = default)
    {
        var loadTask = _agentCredentialStatusLoadTask ??= Agent.InitializeAsync(cancellationToken);
        try
        {
            await loadTask.ConfigureAwait(true);
        }
        catch
        {
            if (ReferenceEquals(_agentCredentialStatusLoadTask, loadTask))
            {
                _agentCredentialStatusLoadTask = null;
            }

            throw;
        }
    }

    private async Task SaveAsync()
    {
        var applicationSettingsSaved = false;
        try
        {
            var directory = Path.GetFullPath(DownloadDirectory.Trim());
            var mpvSettings = Mpv.ToSettings();
            var mpvOptions = MpvOptionConfiguration.Build(mpvSettings);
            _mpvOptionValidator?.Validate(mpvOptions);
            var settings = _settings with
            {
                SchemaVersion = ApplicationSettings.CurrentSchemaVersion,
                ColorTheme = SelectedTheme.Value,
                DownloadDirectory = directory,
                EnableLogging = IsLoggingEnabled,
                OpenMediaInNewWindow = SelectedPlaybackWindowMode.OpenNewWindow,
                Mpv = mpvSettings,
                Agent = Agent.ToSettings(),
            };
            await Agent.SavePendingApiKeyAsync().ConfigureAwait(true);
            await _store.SaveAsync(settings).ConfigureAwait(true);
            _settings = settings;
            applicationSettingsSaved = true;
            DownloadDirectory = directory;
            _applyDownloadDirectory(directory);
            var downloadSettingsSaved = _saveDownloadSettings is not null &&
                await _saveDownloadSettings(CancellationToken.None).ConfigureAwait(true);
            if (_playbackController?.IsStarted == false)
            {
                _playbackController.ConfigureStartupOptions(mpvOptions);
            }
            else if (_playbackController?.IsStarted == true)
            {
                await _playbackController.SetAutoPlayNextAsync(mpvSettings.AutoPlayNext).ConfigureAwait(true);
            }
            ActiveMpvSettings = mpvSettings;
            SeekPreviewSettingsChanged?.Invoke(mpvSettings);
            if (PlaybackEndActionChanged is { } changed)
                await changed(mpvSettings.AutoPlayNext).ConfigureAwait(true);

            var statusMessage = _playbackController?.IsStarted == true
                ? "设置已保存；播放完毕行为、播放窗口模式与进度预览立即生效，解码与画面设置将在下次启动时生效"
                : "设置已保存";
            StatusMessage = downloadSettingsSaved
                ? $"{statusMessage}；aria2 设置已应用并保存"
                : statusMessage;
        }
        catch (Exception exception)
        {
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "Failed to save or apply settings.",
                exception);
            StatusMessage = applicationSettingsSaved
                ? $"设置文件已保存，但部分设置应用失败：{exception.Message}"
                : $"设置保存失败：{exception.Message}";
        }
    }

    private string? TryConfigureMpvStartup(MpvSettings settings)
    {
        if (_playbackController is null)
        {
            ActiveMpvSettings = settings;
            return null;
        }

        try
        {
            var options = MpvOptionConfiguration.Build(settings);
            _mpvOptionValidator?.Validate(options);
            _playbackController.ConfigureStartupOptions(options);
            ActiveMpvSettings = settings;
            return null;
        }
        catch (Exception exception)
        {
            _applicationLog.Write(
                LogComponent.Mpv,
                ApplicationLogLevel.Error,
                "Failed to load mpv settings; using defaults.",
                exception);
            var defaults = MpvOptionConfiguration.Build(new MpvSettings());
            var fallbackSettings = new MpvSettings();
            try
            {
                _mpvOptionValidator?.Validate(defaults);
            }
            catch (Exception fallbackException)
            {
                _applicationLog.Write(
                    LogComponent.Mpv,
                    ApplicationLogLevel.Warning,
                    "Configured mpv defaults are unavailable; using libmpv defaults.",
                    fallbackException);
                defaults = new Dictionary<string, string>();
                fallbackSettings = fallbackSettings with { SubtitleAuto = "no" };
            }
            _playbackController.ConfigureStartupOptions(defaults);
            ActiveMpvSettings = fallbackSettings;
            return $"播放器设置加载失败：{exception.Message}\n\nCanX 已使用默认播放器设置继续启动。";
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed record ThemeSettingOption(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class MpvSettingsViewModel
{
    public MpvSettingsViewModel(MpvSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SelectedHardwareDecoding = Select(HardwareDecodingOptions, settings.HardwareDecoding);
        SelectedVideoSync = Select(VideoSyncOptions, settings.VideoSync);
        SelectedDeinterlace = Select(DeinterlaceOptions, settings.Deinterlace);
        SavePositionOnQuit = settings.SavePositionOnQuit;
        ResumePlayback = settings.ResumePlayback;
        SelectedPlaybackEndAction = PlaybackEndActionOptions[settings.AutoPlayNext ? 0 : 1];
        SeekPreviewEnabled = settings.SeekPreviewEnabled;
        SeekPreviewDelayMilliseconds = Math.Clamp(settings.SeekPreviewDelayMilliseconds, 0, 10000);
        Interpolation = settings.Interpolation;
        SelectedScale = Select(ScaleOptions, settings.Scale);
        SelectedChromaScale = Select(ChromaScaleOptions, settings.ChromaScale);
        SelectedDownscale = Select(ScaleOptions, settings.Downscale);
        Deband = settings.Deband;
        SigmoidUpscaling = settings.SigmoidUpscaling;
        SelectedTargetColorspaceHint = Select(TargetColorspaceHintOptions, settings.TargetColorspaceHint);
        SelectedTargetColorspaceHintMode = Select(TargetColorspaceHintModeOptions, settings.TargetColorspaceHintMode);
        SelectedTargetPrimaries = Select(TargetPrimariesOptions, settings.TargetPrimaries);
        SelectedTargetTransfer = Select(TargetTransferOptions, settings.TargetTransfer);
        TargetPeak = settings.TargetPeak;
        SelectedToneMapping = Select(ToneMappingOptions, settings.ToneMapping);
        SelectedGamutMappingMode = Select(GamutMappingOptions, settings.GamutMappingMode);
        SelectedHdrComputePeak = Select(HdrComputePeakOptions, settings.HdrComputePeak);
        AudioDevice = settings.AudioDevice;
        SelectedAudioChannels = Select(AudioChannelOptions, settings.AudioChannels);
        AudioExclusive = settings.AudioExclusive;
        AudioPitchCorrection = settings.AudioPitchCorrection;
        AudioLanguages = settings.AudioLanguages;
        SubtitleLanguages = ExternalSubtitleMatcher.EffectiveLanguages(settings.SubtitleLanguages);
        SelectedSubtitleAuto = Select(SubtitleAutoOptions, settings.SubtitleAuto);
        SubtitleFont = settings.SubtitleFont;
        SubtitleFontSize = settings.SubtitleFontSize;
        SubtitleScaleWithWindow = settings.SubtitleScaleWithWindow;
        SelectedCache = Select(CacheOptions, settings.Cache);
        CacheSeconds = settings.CacheSeconds;
        DemuxerMaxBytesMiB = settings.DemuxerMaxBytesMiB;
        DemuxerMaxBackBytesMiB = settings.DemuxerMaxBackBytesMiB;
        CachePause = settings.CachePause;
        CachePauseInitial = settings.CachePauseInitial;
        CachePauseWaitSeconds = settings.CachePauseWaitSeconds;
        NetworkTimeoutSeconds = settings.NetworkTimeoutSeconds;
        AdvancedOptions = settings.AdvancedOptions;
    }

    public IReadOnlyList<MpvSettingOption> HardwareDecodingOptions { get; } =
    [
        new("auto-safe", "自动（安全，推荐）"),
        new("auto-unsafe", "自动（尝试全部）"),
        new("auto-copy", "自动（复制回内存）"),
        new("no", "禁用硬件解码"),
        new("nvdec", "NVIDIA NVDEC"),
        new("vaapi", "VAAPI"),
        new("vulkan", "Vulkan Video"),
        new("d3d11va", "D3D11VA"),
        new("videotoolbox", "VideoToolbox"),
        new("mediacodec", "MediaCodec"),
    ];

    public IReadOnlyList<MpvSettingOption> VideoSyncOptions { get; } =
    [
        new("display-resample", "显示同步 + 音频重采样（推荐）"),
        new("audio", "音频时钟"),
        new("display-resample-vdrop", "显示同步 + 音频重采样 + 丢帧"),
        new("display-tempo", "显示同步 + 音频变速"),
        new("display-adrop", "显示同步 + 音频丢弃"),
        new("display-vdrop", "显示同步 + 视频丢帧"),
        new("display-desync", "显示同步，不校正音频"),
        new("desync", "不进行音视频同步"),
    ];

    public IReadOnlyList<MpvSettingOption> DeinterlaceOptions { get; } =
    [
        new("no", "关闭"),
        new("auto", "自动"),
        new("yes", "开启"),
    ];

    public IReadOnlyList<MpvSettingOption> ScaleOptions { get; } =
    [
        new("bilinear", "Bilinear"),
        new("lanczos", "Lanczos"),
        new("ewa_lanczos", "EWA Lanczos"),
        new("ewa_lanczossharp", "EWA Lanczos Sharp"),
        new("mitchell", "Mitchell"),
        new("catmull_rom", "Catmull-Rom"),
        new("hermite", "Hermite"),
    ];

    public IReadOnlyList<MpvSettingOption> ChromaScaleOptions { get; } =
    [
        new(string.Empty, "跟随亮度缩放（默认）"),
        new("bilinear", "Bilinear"),
        new("lanczos", "Lanczos"),
        new("ewa_lanczos", "EWA Lanczos"),
        new("ewa_lanczossharp", "EWA Lanczos Sharp"),
        new("mitchell", "Mitchell"),
        new("catmull_rom", "Catmull-Rom"),
        new("hermite", "Hermite"),
    ];

    public IReadOnlyList<MpvSettingOption> AudioChannelOptions { get; } =
    [
        new("auto-safe", "自动（安全）"),
        new("auto", "自动"),
        new("stereo", "立体声"),
        new("mono", "单声道"),
    ];

    public IReadOnlyList<MpvSettingOption> TargetColorspaceHintOptions { get; } =
    [
        new("auto", "自动（推荐）"),
        new("yes", "始终发送"),
        new("no", "不发送"),
    ];

    public IReadOnlyList<MpvSettingOption> TargetColorspaceHintModeOptions { get; } =
    [
        new("target", "目标显示能力（推荐）"),
        new("source", "源内容直通"),
        new("source-dynamic", "源动态元数据（实验）"),
    ];

    public IReadOnlyList<MpvSettingOption> TargetPrimariesOptions { get; } =
    [
        new("auto", "自动"),
        new("bt.470m", "BT.470 M"),
        new("bt.601-525", "BT.601 525"),
        new("bt.601-625", "BT.601 625"),
        new("bt.709", "BT.709"),
        new("bt.2020", "BT.2020"),
        new("display-p3", "Display P3"),
        new("dci-p3", "DCI-P3"),
        new("apple", "Apple RGB"),
        new("adobe", "Adobe RGB"),
        new("prophoto", "ProPhoto RGB"),
        new("cie1931", "CIE 1931 RGB"),
        new("v-gamut", "Panasonic V-Gamut"),
        new("s-gamut", "Sony S-Gamut"),
    ];

    public IReadOnlyList<MpvSettingOption> TargetTransferOptions { get; } =
    [
        new("auto", "自动"),
        new("bt.1886", "BT.1886"),
        new("srgb", "sRGB"),
        new("linear", "Linear"),
        new("gamma1.8", "Gamma 1.8"),
        new("gamma2.0", "Gamma 2.0"),
        new("gamma2.2", "Gamma 2.2"),
        new("gamma2.4", "Gamma 2.4"),
        new("gamma2.6", "Gamma 2.6"),
        new("gamma2.8", "Gamma 2.8"),
        new("prophoto", "ProPhoto RGB"),
        new("pq", "PQ / ST 2084"),
        new("hlg", "HLG"),
        new("v-log", "Panasonic V-Log"),
        new("s-log1", "Sony S-Log1"),
        new("s-log2", "Sony S-Log2"),
        new("scrgb", "scRGB"),
    ];

    public IReadOnlyList<MpvSettingOption> ToneMappingOptions { get; } =
    [
        new("auto", "自动（推荐）"),
        new("spline", "Spline"),
        new("bt.2390", "BT.2390"),
        new("bt.2446a", "BT.2446A"),
        new("mobius", "Mobius"),
        new("reinhard", "Reinhard"),
        new("hable", "Hable"),
        new("st2094-40", "ST 2094-40 / HDR10+"),
        new("st2094-10", "ST 2094-10"),
        new("clip", "Clip"),
        new("gamma", "Gamma"),
        new("linear", "Linear"),
    ];

    public IReadOnlyList<MpvSettingOption> GamutMappingOptions { get; } =
    [
        new("auto", "自动（推荐）"),
        new("perceptual", "感知映射"),
        new("relative", "相对色度"),
        new("saturation", "饱和度"),
        new("absolute", "绝对色度"),
        new("desaturate", "去饱和"),
        new("darken", "压暗"),
        new("clip", "裁剪"),
        new("warn", "越界警告"),
        new("linear", "线性去饱和"),
    ];

    public IReadOnlyList<MpvSettingOption> HdrComputePeakOptions { get; } =
    [
        new("auto", "自动（推荐）"),
        new("yes", "启用"),
        new("no", "禁用"),
    ];

    public IReadOnlyList<MpvSettingOption> SubtitleAutoOptions { get; } =
    [
        new("exact", "智能匹配（默认）"),
        new("fuzzy", "模糊匹配"),
        new("all", "加载全部"),
        new("no", "不自动加载"),
    ];

    public IReadOnlyList<MpvSettingOption> CacheOptions { get; } =
    [
        new("auto", "自动（网络源启用，推荐）"),
        new("yes", "始终启用"),
        new("no", "禁用"),
    ];

    public MpvSettingOption SelectedHardwareDecoding { get; set; }

    public MpvSettingOption SelectedVideoSync { get; set; }

    public MpvSettingOption SelectedDeinterlace { get; set; }

    public bool SavePositionOnQuit { get; set; }

    public bool ResumePlayback { get; set; }

    public IReadOnlyList<MpvSettingOption> PlaybackEndActionOptions { get; } =
    [
        new("next", "播放下一项"),
        new("stop", "停止"),
    ];

    public MpvSettingOption SelectedPlaybackEndAction { get; set; }

    public bool SeekPreviewEnabled { get; set; }

    public decimal SeekPreviewDelayMilliseconds { get; set; }

    public bool Interpolation { get; set; }

    public MpvSettingOption SelectedScale { get; set; }

    public MpvSettingOption SelectedChromaScale { get; set; }

    public MpvSettingOption SelectedDownscale { get; set; }

    public bool Deband { get; set; }

    public bool SigmoidUpscaling { get; set; }

    public MpvSettingOption SelectedTargetColorspaceHint { get; set; }

    public MpvSettingOption SelectedTargetColorspaceHintMode { get; set; }

    public MpvSettingOption SelectedTargetPrimaries { get; set; }

    public MpvSettingOption SelectedTargetTransfer { get; set; }

    public string TargetPeak { get; set; }

    public MpvSettingOption SelectedToneMapping { get; set; }

    public MpvSettingOption SelectedGamutMappingMode { get; set; }

    public MpvSettingOption SelectedHdrComputePeak { get; set; }

    public string AudioDevice { get; set; }

    public MpvSettingOption SelectedAudioChannels { get; set; }

    public bool AudioExclusive { get; set; }

    public bool AudioPitchCorrection { get; set; }

    public string AudioLanguages { get; set; }

    public string SubtitleLanguages { get; set; }

    public MpvSettingOption SelectedSubtitleAuto { get; set; }

    public string SubtitleFont { get; set; }

    public string SubtitleFontSize { get; set; }

    public bool SubtitleScaleWithWindow { get; set; }

    public MpvSettingOption SelectedCache { get; set; }

    public string CacheSeconds { get; set; }

    public string DemuxerMaxBytesMiB { get; set; }

    public string DemuxerMaxBackBytesMiB { get; set; }

    public bool CachePause { get; set; }

    public bool CachePauseInitial { get; set; }

    public string CachePauseWaitSeconds { get; set; }

    public string NetworkTimeoutSeconds { get; set; }

    public string AdvancedOptions { get; set; }

    public MpvSettings ToSettings() => new()
    {
        HardwareDecoding = SelectedHardwareDecoding.Value,
        VideoSync = SelectedVideoSync.Value,
        Deinterlace = SelectedDeinterlace.Value,
        SavePositionOnQuit = SavePositionOnQuit,
        ResumePlayback = ResumePlayback,
        AutoPlayNext = SelectedPlaybackEndAction.Value == "next",
        SeekPreviewEnabled = SeekPreviewEnabled,
        SeekPreviewDelayMilliseconds = (int)Math.Clamp(
            decimal.Round(SeekPreviewDelayMilliseconds, 0, MidpointRounding.AwayFromZero), 0, 10000),
        Interpolation = Interpolation,
        Scale = SelectedScale.Value,
        ChromaScale = SelectedChromaScale.Value,
        Downscale = SelectedDownscale.Value,
        Deband = Deband,
        SigmoidUpscaling = SigmoidUpscaling,
        TargetColorspaceHint = SelectedTargetColorspaceHint.Value,
        TargetColorspaceHintMode = SelectedTargetColorspaceHintMode.Value,
        TargetPrimaries = SelectedTargetPrimaries.Value,
        TargetTransfer = SelectedTargetTransfer.Value,
        TargetPeak = TargetPeak,
        ToneMapping = SelectedToneMapping.Value,
        GamutMappingMode = SelectedGamutMappingMode.Value,
        HdrComputePeak = SelectedHdrComputePeak.Value,
        AudioDevice = AudioDevice,
        AudioChannels = SelectedAudioChannels.Value,
        AudioExclusive = AudioExclusive,
        AudioPitchCorrection = AudioPitchCorrection,
        AudioLanguages = AudioLanguages,
        SubtitleLanguages = ExternalSubtitleMatcher.EffectiveLanguages(SubtitleLanguages),
        SubtitleAuto = SelectedSubtitleAuto.Value,
        SubtitleFont = SubtitleFont,
        SubtitleFontSize = SubtitleFontSize,
        SubtitleScaleWithWindow = SubtitleScaleWithWindow,
        Cache = SelectedCache.Value,
        CacheSeconds = CacheSeconds,
        DemuxerMaxBytesMiB = DemuxerMaxBytesMiB,
        DemuxerMaxBackBytesMiB = DemuxerMaxBackBytesMiB,
        CachePause = CachePause,
        CachePauseInitial = CachePauseInitial,
        CachePauseWaitSeconds = CachePauseWaitSeconds,
        NetworkTimeoutSeconds = NetworkTimeoutSeconds,
        AdvancedOptions = AdvancedOptions,
    };

    private static MpvSettingOption Select(IReadOnlyList<MpvSettingOption> options, string value) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)) ??
        options[0];
}

public sealed record PlaybackWindowModeOption(bool OpenNewWindow, string Label)
{
    public override string ToString() => Label;
}

public sealed record MpvSettingOption(string Value, string Label)
{
    public override string ToString() => Label;
}
