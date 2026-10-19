// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.ComponentModel;
using CanX.Agent;
using CanX.Application.Discovery;
using CanX.Application.Downloads;
using CanX.Application.Playback;
using CanX.Core.Events;
using CanX.Core.Diagnostics;
using CanX.Infrastructure.Configuration;
using CanX.MediaAccess;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Ui;
using CanX.Plugins.Services;

namespace CanX.App.ViewModels;

public sealed class ShellViewModel : IAsyncDisposable
{
    private readonly IAsyncDisposable? _completionCoordinator;
    private AgentSettingsViewModel? _observedAgentSettings;

    public ShellViewModel(
        IDownloadManager downloadManager,
        IEventBus eventBus,
        IPlaybackController playbackController,
        IMpvOptionValidator mpvOptionValidator,
        string defaultDownloadDirectory,
        IApplicationSettingsStore settingsStore,
        Action<string> applyTheme,
        IApplicationLog applicationLog,
        IMediaSourceProfileStore? mediaSourceProfileStore = null,
        IAsyncDisposable? completionCoordinator = null,
        PluginLifecycleManager? pluginLifecycleManager = null,
        string? pluginDevelopmentModePath = null,
        string? logDirectory = null,
        PluginUiRegistry? pluginUiRegistry = null,
        PluginServiceRegistry? pluginServiceRegistry = null,
        CredentialAgentApiKeyProvider? agentApiKeyProvider = null,
        AgentRunner? agentRunner = null,
        IAgentConversationStore? agentConversationStore = null,
        AgentApprovalService? agentApprovalService = null,
        SynchronizationContext? synchronizationContext = null,
        bool deferAgentCredentialStatusLoad = false,
        IAgentModelCatalog? agentModelCatalog = null,
        IAgentChatConnectionTester? agentChatTester = null)
    {
        DownloadCenter = new DownloadCenterViewModel(
            downloadManager,
            eventBus,
            defaultDownloadDirectory,
            synchronizationContext,
            applicationLog: applicationLog);
        Playback = new PlaybackViewModel(
            playbackController,
            eventBus,
            synchronizationContext,
            applicationLog: applicationLog);
        MediaSources = new MediaSourceBrowserViewModel(applicationLog, mediaSourceProfileStore);
        MediaSources.SetPlaybackHistory(Playback.RecentItems);
        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        PluginUi = pluginUiRegistry ?? new PluginUiRegistry();
        OnlineSubtitles = new OnlineSubtitleCatalog(pluginServiceRegistry ?? new PluginServiceRegistry());
        PluginPages = new PluginPagesViewModel(PluginUi);
        Settings = new SettingsViewModel(
            settingsStore,
            defaultDownloadDirectory,
            applyTheme,
            directory => DownloadCenter.DestinationDirectory = directory,
            applicationLog,
            playbackController,
            mpvOptionValidator,
            DownloadCenter.SavePendingGlobalOptionsAsync,
            agentApiKeyProvider,
            deferAgentCredentialStatusLoad,
            agentModelCatalog,
            agentChatTester);
        Playback.ConfigureAutomaticSubtitles(() => Settings.ActiveMpvSettings);
        _completionCoordinator = completionCoordinator;
        if (pluginLifecycleManager is not null)
        {
            Plugins = new PluginManagerViewModel(
                pluginLifecycleManager,
                pluginDevelopmentModePath ?? Path.Combine(Path.GetTempPath(), "canx-plugin-development-mode"),
                logDirectory ?? Path.GetTempPath(),
                applicationLog,
                PluginUi,
                pluginServiceRegistry);
        }
        if (agentRunner is not null && agentConversationStore is not null && agentApprovalService is not null)
        {
            Agent = new AgentViewModel(
                agentRunner,
                agentConversationStore,
                () => Settings.Agent.CreateConfiguration(),
                () => Settings.Agent.CreateRunOptions(),
                agentApprovalService,
                () => Settings.Agent.ContextWindowTokens,
                () => Settings.Agent.ShowToolCalls);
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            ObserveAgentSettings();
        }
    }

    public string RuntimeDescription { get; } =
        $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · " +
        $"Dynamic code: {(RuntimeFeature.IsDynamicCodeSupported ? "enabled" : "disabled (AOT)")}";

    public DownloadCenterViewModel DownloadCenter { get; }

    public PlaybackViewModel Playback { get; }

    public MediaSourceBrowserViewModel MediaSources { get; }

    public SettingsViewModel Settings { get; }

    public PluginManagerViewModel? Plugins { get; }

    public PluginUiRegistry PluginUi { get; }

    public OnlineSubtitleCatalog OnlineSubtitles { get; }

    public PluginPagesViewModel PluginPages { get; }

    public AgentViewModel? Agent { get; }

    public async ValueTask<string?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settingsLoadError = await Settings.InitializeAsync(cancellationToken).ConfigureAwait(true);
        await DownloadCenter.InitializeAsync(cancellationToken).ConfigureAwait(true);
        await Playback.InitializeAsync(cancellationToken).ConfigureAwait(true);
        await MediaSources.InitializeAsync(cancellationToken).ConfigureAwait(true);
        if (Plugins is not null)
        {
            await Plugins.InitializeAsync(cancellationToken).ConfigureAwait(true);
        }
        if (Agent is not null)
        {
            await Agent.InitializeAsync(cancellationToken).ConfigureAwait(true);
        }
        return settingsLoadError;
    }

    public async ValueTask DisposeAsync()
    {
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        if (_observedAgentSettings is not null)
            _observedAgentSettings.PropertyChanged -= OnAgentSettingsPropertyChanged;
        Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        await Playback.DisposeAsync().ConfigureAwait(false);
        await MediaSources.DisposeAsync().ConfigureAwait(false);
        await DownloadCenter.DisposeAsync().ConfigureAwait(false);
        PluginPages.Dispose();
        if (Plugins is not null)
        {
            await Plugins.DisposeAsync().ConfigureAwait(false);
        }
        if (Agent is not null)
        {
            await Agent.DisposeAsync().ConfigureAwait(false);
        }
        if (_completionCoordinator is not null)
        {
            await _completionCoordinator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is null or nameof(SettingsViewModel.Agent)) ObserveAgentSettings();
    }

    private void ObserveAgentSettings()
    {
        if (_observedAgentSettings is not null)
            _observedAgentSettings.PropertyChanged -= OnAgentSettingsPropertyChanged;
        _observedAgentSettings = Settings.Agent;
        _observedAgentSettings.PropertyChanged += OnAgentSettingsPropertyChanged;
        Agent?.RefreshMessageVisibility();
    }

    private void OnAgentSettingsPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is null or nameof(AgentSettingsViewModel.ShowToolCalls))
            Agent?.RefreshMessageVisibility();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is null or nameof(PlaybackViewModel.RecentItems))
        {
            MediaSources.SetPlaybackHistory(Playback.RecentItems);
        }
    }
}
