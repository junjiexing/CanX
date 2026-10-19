// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Android.App;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CanX.Agent;
using CanX.App.Automation;
using CanX.App.ViewModels;
using CanX.Application.Automation;
using CanX.Application.Discovery;
using CanX.Application.Downloads;
using CanX.Application.Playback;
using CanX.Application.Plugins;
using CanX.Core.Configuration;
using CanX.Core.Diagnostics;
using CanX.Core.Downloads;
using CanX.Core.Events;
using CanX.Core.Playback;
using CanX.Downloads;
using CanX.Downloads.Engine;
using CanX.Downloads.Persistence;
using CanX.Infrastructure.Configuration;
using CanX.Infrastructure.Diagnostics;
using CanX.Infrastructure.MediaAccess;
using CanX.Infrastructure.Playback;
using CanX.MediaAccess;
using CanX.Playback;
using CanX.Playback.Engine;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Media;
using CanX.Plugins.Runtime;
using CanX.Plugins.Services;
using CanX.Streaming;
using CanX.Tooling;

namespace CanX.App.Android;

internal sealed class MobileAppHost : IAsyncDisposable
{
    private readonly FileApplicationLog _log;
    private readonly FileToolAuditSink _toolAudit;
    private readonly JsonLinesPluginAuditSink _pluginAudit;
    private readonly HttpClient _agentHttpClient;
    private readonly IDownloadManager _downloadManager;
    private readonly IPlaybackController _playbackController;
    private readonly RememberedSubtitleCache _subtitleCache;
    private readonly List<MediaStreamRegistration> _mediaStreamRegistrations = [];
    private readonly List<MediaSourceReadLease> _mediaSourceReadLeases = [];
    private readonly List<IStorageFile> _storageFiles = [];
    private readonly Dictionary<string, IStorageFile> _storagePlaybackFiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, DownloadState> _backgroundDownloads = new();
    private readonly List<IDisposable> _backgroundSubscriptions = [];
    private int _backgroundPlaybackState = (int)PlaybackState.Idle;
    private int _backgroundPlaybackHasMedia;
    private MobileShellView? _view;
    private int _disposed;

    private MobileAppHost(
        ShellViewModel shell,
        NativeMpvEngine mpvEngine,
        MediaStreamBroker streamBroker,
        ToolRegistry tools,
        FileApplicationLog log,
        FileToolAuditSink toolAudit,
        JsonLinesPluginAuditSink pluginAudit,
        HttpClient agentHttpClient,
        IDownloadManager downloadManager,
        IPlaybackController playbackController,
        string dataDirectory)
    {
        Shell = shell;
        MpvEngine = mpvEngine;
        StreamBroker = streamBroker;
        Tools = tools;
        _log = log;
        _toolAudit = toolAudit;
        _pluginAudit = pluginAudit;
        _agentHttpClient = agentHttpClient;
        _downloadManager = downloadManager;
        _playbackController = playbackController;
        _subtitleCache = new RememberedSubtitleCache(Path.Combine(dataDirectory, "subtitles"));
        Shell.Playback.ConfigureRememberedSubtitleLoader(RestoreRememberedNetworkSubtitleAsync);
    }

    public ShellViewModel Shell { get; }

    public NativeMpvEngine MpvEngine { get; }

    public MediaStreamBroker StreamBroker { get; }

    public ToolRegistry Tools { get; }

    public Task<IStorageFolder?> GetCurrentStorageMediaFolderAsync() =>
        _storagePlaybackFiles.TryGetValue(Shell.Playback.Source, out var file)
            ? file.GetParentAsync() : Task.FromResult<IStorageFolder?>(null);

    public static MobileAppHost Create()
    {
        var files = global::Android.App.Application.Context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android application storage is unavailable.");
        var dataDirectory = Path.Combine(files, "canx");
        Directory.CreateDirectory(dataDirectory);

        var log = new FileApplicationLog(Path.Combine(dataDirectory, "logs"));
        string? certificateBundle = null;
        try
        {
            certificateBundle = AndroidTrustedCertificateBundle.Create(dataDirectory);
            Environment.SetEnvironmentVariable("SSL_CERT_FILE", certificateBundle);
            log.Write(
                LogComponent.App,
                ApplicationLogLevel.Information,
                $"Configured Android system CA bundle: {certificateBundle}");
        }
        catch (Exception exception)
        {
            log.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "Unable to prepare the Android system CA bundle.",
                exception);
        }
        var eventBus = new InMemoryEventBus(exception => log.Write(
            LogComponent.App,
            ApplicationLogLevel.Error,
            "Application event subscriber failed.",
            exception));
        var credentialStore = new AndroidCredentialStore();
        var downloadOptions = new DownloadManagerOptions
        {
            // Avalonia Android currently shares native input and managed dispatcher
            // work on one looper. A conservative cadence avoids starving touch input;
            // DownloadManager also suppresses unchanged snapshots between polls.
            PollInterval = TimeSpan.FromSeconds(10),
            WorkerFaultHandler = exception => log.Write(
                LogComponent.Aria2,
                ApplicationLogLevel.Error,
                "Download worker skipped a failed maintenance step.",
                exception),
        };
        if (!string.IsNullOrWhiteSpace(certificateBundle))
        {
            downloadOptions.GlobalOptions["ca-certificate"] = certificateBundle;
            downloadOptions.GlobalOptions["check-certificate"] = "true";
        }
        var manager = new CanX.Downloads.DownloadManager(
            new NativeAria2Engine(log),
            new CredentialProtectingDownloadSessionStore(
                new JsonDownloadSessionStore(Path.Combine(dataDirectory, "downloads.json")),
                credentialStore),
            eventBus,
            downloadOptions);
        var streamBroker = new MediaStreamBroker();
        // Querying mpv's audio-device-list on Android probes AudioTrack by
        // repeatedly constructing real output tracks. Android owns output
        // routing, so expose only the automatic route and avoid disrupting the
        // playback clock with those probes.
        var displayRefreshRate = DetectDisplayRefreshRate();
        if (displayRefreshRate is { } detectedRefreshRate)
        {
            log.Write(
                LogComponent.App,
                ApplicationLogLevel.Information,
                $"Detected Android display refresh rate: {detectedRefreshRate:0.###} Hz.");
        }

        var mpvEngine = new NativeMpvEngine(
            log,
            streamBroker,
            displayRefreshRate,
            enumerateAudioDevices: false,
            // Android's SurfaceView/Choreographer path is already synchronized
            // to hardware VSync. Sleeping again before mpv_report_swap delays
            // the following render until after its presentation deadline.
            paceRenderApiSwaps: false,
            // Avalonia owns eglSwapBuffers and does not expose its completion
            // signal to OpenGlControlBase. A delayed report is worse than no
            // report: once the optional API is called, libmpv waits up to
            // 200 ms for every subsequent frame and starves AudioTrack.
            reportRenderApiSwaps: false);
        var playbackController = new PlaybackController(
            mpvEngine,
            eventBus,
            new JsonPlaybackStateStore(Path.Combine(dataDirectory, "playback-state.json"), log));
        var settingsStore = new JsonApplicationSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var sourceStore = new CredentialProtectingMediaSourceProfileStore(
            Path.Combine(dataDirectory, "media-sources.json"), credentialStore);
        var pluginServices = new PluginServiceRegistry(log);
        var toolAudit = new FileToolAuditSink(Path.Combine(dataDirectory, "logs", "tool-audit.jsonl"));
        var tools = new ToolRegistry(
            new DefaultToolAuthorizer(),
            toolAudit,
            exception => log.Write(LogComponent.App, ApplicationLogLevel.Error, "Tool audit write failed.", exception));
        tools.ReplaceHandlers(
            DownloadToolCatalog.Create(manager)
                .Concat(PlaybackToolCatalog.Create(playbackController))
                .Concat(PluginServiceToolCatalog.Create(pluginServices)));

        var pluginHostFactory = new QuickJsPluginHostFactory(
            new ToolRegistryPluginDispatcher(tools),
            log: log,
            pluginDataRoot: Path.Combine(dataDirectory, "plugin-data"),
            credentialStore: credentialStore,
            serviceRegistry: pluginServices,
            mediaStreamBroker: streamBroker);
        var pluginAudit = new JsonLinesPluginAuditSink(Path.Combine(dataDirectory, "logs", "plugin-audit.jsonl"));
        var pluginManager = new PluginLifecycleManager(
            Path.Combine(dataDirectory, "plugins"),
            pluginHostFactory,
            log,
            audit: pluginAudit);
        var apiKeys = new CredentialAgentApiKeyProvider(credentialStore);
        var approvals = new AgentApprovalService();
        var agentHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        agentHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CanX-Android/0.1");
        var agentRunner = new AgentRunner(
            new OpenAiCompatibleChatProvider(agentHttpClient, apiKeys),
            tools,
            approvals);
        var downloads = global::Android.App.Application.Context.GetExternalFilesDir(global::Android.OS.Environment.DirectoryDownloads)
            ?.AbsolutePath ?? Path.Combine(files, "downloads");

        var completionCoordinator = new DownloadCompletionCoordinator(
            eventBus,
            new AndroidDownloadCompletionHandler());
        // The Android activity can expose a platform SynchronizationContext rather
        // than Avalonia's dispatcher context while the composition root is built.
        // Explicitly marshal background engine events to Avalonia; otherwise the
        // first aria2 refresh mutates UI-bound collections from its worker thread.
        var uiContext = new AvaloniaSynchronizationContext(
            Dispatcher.UIThread,
            DispatcherPriority.Normal);
        var shell = new ShellViewModel(
            manager,
            eventBus,
            playbackController,
            new NativeMpvOptionValidator(),
            downloads,
            settingsStore,
            ApplyTheme,
            log,
            sourceStore,
            completionCoordinator,
            pluginLifecycleManager: pluginManager,
            pluginDevelopmentModePath: Path.Combine(dataDirectory, "plugin-development-mode"),
            logDirectory: log.DirectoryPath,
            pluginUiRegistry: pluginHostFactory.UiRegistry,
            pluginServiceRegistry: pluginServices,
            agentApiKeyProvider: apiKeys,
            agentRunner: agentRunner,
            agentConversationStore: new JsonAgentConversationStore(Path.Combine(dataDirectory, "agent-conversations.json")),
            agentApprovalService: approvals,
            synchronizationContext: uiContext,
            agentModelCatalog: new OpenAiCompatibleModelCatalog(agentHttpClient, apiKeys),
            agentChatTester: new OpenAiCompatibleChatConnectionTester(
                agentHttpClient, apiKeys, () => tools.Descriptors));
        var host = new MobileAppHost(
            shell,
            mpvEngine,
            streamBroker,
            tools,
            log,
            toolAudit,
            pluginAudit,
            agentHttpClient,
            manager,
            playbackController,
            dataDirectory);
        host._backgroundSubscriptions.Add(
            pluginHostFactory.MediaPlaybackBroker.RegisterHandler(
                (request, token) => new ValueTask(DispatchAsync(
                    () => host.PlayResolvedMediaAsync(new Uri(request.Source), null, token),
                    token))));
        var automation = new DesktopAutomationService(
            sourceStore,
            pluginManager,
            settingsStore,
            log,
            (request, token) => DispatchAsync(() => host.PlayConfiguredMediaAsync(request, token), token),
            token => DispatchAsync(async () =>
            {
                var error = await shell.Settings.InitializeAsync(token);
                if (!string.IsNullOrWhiteSpace(error)) throw new InvalidDataException(error);
            }, token),
            token => DispatchAsync(
                () => shell.Plugins?.RefreshAsync() ?? Task.CompletedTask,
                token),
            token => DispatchAsync(
                () => shell.MediaSources.InitializeAsync(token),
                token),
            host.GetAutomationContext,
            (page, token) => DispatchAsync(() => host.NavigateForAutomationAsync(page, token), token),
            (message, severity, token) => DispatchAsync(
                () => host.ShowAutomationNotificationAsync(message, severity, token),
                token));
        var searchActions = new DesktopSearchActionService(
            manager,
            () => shell.Settings.DownloadDirectory,
            (media, subtitle, token) => DispatchAsync(
                () => host.PlayResolvedMediaAsync(media, subtitle, token),
                token));
        var workspaceTrialFactory = new QuickJsPluginHostFactory(
            new DenyingPluginToolDispatcher(),
            new PluginRuntimeOptions
            {
                MemoryLimitBytes = 16 * 1024 * 1024,
                StackLimitBytes = 512 * 1024,
                ExecutionTimeout = TimeSpan.FromMilliseconds(250),
                ToolTimeout = TimeSpan.FromSeconds(2),
            },
            log,
            Path.Combine(dataDirectory, "plugin-workspaces", ".trial-data"),
            httpHandlerFactory: static () => new BlockedPluginNetworkHandler());
        var workspaceManager = new PluginWorkspaceManager(
            Path.Combine(dataDirectory, "plugin-workspaces"),
            pluginManager,
            workspaceTrialFactory,
            refreshPlugins: token => DispatchAsync(
                () => shell.Plugins?.RefreshAsync() ?? Task.CompletedTask,
                token));
        approvals.SetTrustedDetailsProvider(async (request, token) =>
        {
            if (string.Equals(request.ToolName, "plugins.enable", StringComparison.Ordinal))
            {
                return CreatePluginEnableApprovalDetails(pluginManager, request.Arguments);
            }
            if (!string.Equals(request.ToolName, "plugins.workspace.install", StringComparison.Ordinal)) return null;
            var workspaceId = request.Arguments["workspaceId"]?.GetValue<string>()
                ?? throw new InvalidDataException("Plugin workspace install request is missing its workspace ID.");
            var reviewToken = request.Arguments["reviewToken"]?.GetValue<string>()
                ?? throw new InvalidDataException("Plugin workspace install request is missing its review token.");
            var review = await workspaceManager.GetInstallReviewAsync(workspaceId, reviewToken, token).ConfigureAwait(false);
            return CreatePluginInstallApprovalDetails(review);
        });
        tools.ReplaceHandlers(
            DownloadToolCatalog.Create(manager, () => shell.Settings.DownloadDirectory)
                .Concat(PlaybackToolCatalog.Create(playbackController))
                .Concat(PluginServiceToolCatalog.Create(pluginServices))
                .Concat(SearchToolCatalog.Create(pluginServices, searchActions))
                .Concat(PluginWorkspaceToolCatalog.Create(workspaceManager))
                .Concat(AutomationToolCatalog.Create(
                    automation,
                    automation,
                    automation,
                    automation,
                    automation,
                    tools)));
        shell.Playback.ConfigureDisplaySleepInhibition(AndroidDisplaySleepInhibitor.AcquireAsync);
        host.TrackBackgroundState(eventBus);
        return host;
    }

    private static double? DetectDisplayRefreshRate()
    {
        try
        {
#pragma warning disable CS0618 // DefaultDisplay remains the API 23-compatible path.
            var refreshRate = MainActivity.Current?.WindowManager?.DefaultDisplay?.RefreshRate;
#pragma warning restore CS0618
            return refreshRate is >= 20 and <= 1000 ? refreshRate : null;
        }
        catch
        {
            return null;
        }
    }

    internal async Task SynchronizeDisplayRefreshRateAsync()
    {
        var refreshRate = DetectDisplayRefreshRate();
        if (!MpvEngine.IsInitialized || refreshRate is null)
        {
            return;
        }

        await _playbackController.SetRuntimePropertyAsync(
            "display-fps-override",
            refreshRate.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        _log.Write(
            LogComponent.App,
            ApplicationLogLevel.Information,
            $"Synchronized mpv to Android display refresh rate: {refreshRate:0.###} Hz.");
    }

    public bool RequiresBackgroundExecution =>
        _backgroundDownloads.Values.Any(static state => state is DownloadState.Active or DownloadState.Waiting) ||
        (Volatile.Read(ref _backgroundPlaybackHasMedia) != 0 &&
         (PlaybackState)Volatile.Read(ref _backgroundPlaybackState) is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused);

    public void AttachView(MobileShellView view) =>
        _view = view ?? throw new ArgumentNullException(nameof(view));

    public void DetachView(MobileShellView view)
    {
        if (ReferenceEquals(_view, view)) _view = null;
    }

    internal Control CreatePlaybackActivityView(Action close, Action toggleOrientation) =>
        RequireView().CreatePlaybackActivityView(close, toggleOrientation);

    internal void ReleasePlaybackActivityView(Control content) =>
        _view?.ReleasePlaybackActivityView(content);

    public async Task StopBackgroundExecutionAsync(CancellationToken cancellationToken = default)
    {
        foreach (var task in _backgroundDownloads.ToArray())
        {
            if (task.Value is not (DownloadState.Active or DownloadState.Waiting)) continue;
            try
            {
                await _downloadManager.PauseAsync(task.Key, force: true, cancellationToken);
            }
            catch (Exception exception)
            {
                _log.Write(
                    LogComponent.App,
                    ApplicationLogLevel.Warning,
                    $"Unable to pause Android background download {task.Key}.",
                    exception);
            }
        }

        if (Volatile.Read(ref _backgroundPlaybackHasMedia) != 0)
        {
            await StopPlaybackAsync(cancellationToken);
        }
    }

    public async Task PlayLocalMediaAsync(
        string source,
        string? subtitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ReleaseMediaStreams();
        await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            Shell.Playback.SuppressAutomaticSubtitleForNextOpen();
        }
        await Shell.Playback.OpenAsync(source, cancellationToken);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            await Shell.Playback.AddSubtitleAsync(subtitle, cancellationToken: cancellationToken);
        }
    }

    public async Task PlayRecentAsync(
        PlaybackRecentItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsMediaSource)
        {
            ReleaseMediaStreams();
            try
            {
                var mediaUri = new Uri(item.Source, UriKind.Absolute);
                var (resource, lease) = await Shell.MediaSources.AcquireHistoryReadAsync(
                    mediaUri,
                    item.MediaSourceProfileId,
                    cancellationToken);
                _mediaSourceReadLeases.Add(lease);
                IReadOnlyList<MediaResource> siblings;
                try { siblings = await lease.FindVideoSiblingsAsync(cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    siblings = [resource];
                }
                await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken, navigateToMedia: false);
                await OpenMediaSourcePlaylistAsync(siblings, resource, lease,
                    item.MediaSourceProfileId, cancellationToken);
            }
            catch
            {
                ReleaseMediaStreams();
                throw;
            }

            return;
        }

        ReleaseMediaStreams();
        await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken, navigateToMedia: false);
        await Shell.Playback.OpenRecentAsync(item, cancellationToken);
    }

    public async Task PlayRemoteMediaAsync(
        MediaResource media,
        RemoteSubtitleChoiceViewModel? subtitleChoice = null,
        CancellationToken cancellationToken = default)
    {
        var manualSubtitle = subtitleChoice?.Resource;
        var disableSubtitles = subtitleChoice == RemoteSubtitleChoiceViewModel.None;
        ReleaseMediaStreams();
        if (media.Uri.IsFile)
        {
            await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
            if (subtitleChoice is not null && subtitleChoice != RemoteSubtitleChoiceViewModel.Auto)
            {
                Shell.Playback.SuppressAutomaticSubtitleForNextOpen(disableSubtitles);
            }
            var videos = Shell.MediaSources.GetVideoSiblings(media);
            if (videos.Count > 1)
            {
                var startIndex = Array.FindIndex(videos.ToArray(), resource => resource.Uri == media.Uri);
                await Shell.Playback.OpenPlaylistAsync(
                    videos.Select(static resource => resource.Uri.LocalPath).ToArray(),
                    startIndex, cancellationToken);
            }
            else
            {
                await Shell.Playback.OpenAsync(media.Uri.LocalPath, cancellationToken);
            }
            if (manualSubtitle?.Uri.IsFile == true)
            {
                await Shell.Playback.AddSubtitleAsync(manualSubtitle.Uri.LocalPath,
                    cancellationToken: cancellationToken);
            }
            return;
        }

        try
        {
            var mediaLease = Shell.MediaSources.AcquireRead(media);
            _mediaSourceReadLeases.Add(mediaLease);

            await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
            if (subtitleChoice is not null && subtitleChoice != RemoteSubtitleChoiceViewModel.Auto)
            {
                Shell.Playback.SuppressAutomaticSubtitleForNextOpen(disableSubtitles);
            }
            var siblings = Shell.MediaSources.GetVideoSiblings(media);
            await OpenMediaSourcePlaylistAsync(siblings, media, mediaLease,
                Shell.MediaSources.ActiveProfileId, cancellationToken);
            if (manualSubtitle is not null)
            {
                await AddMediaSourceSubtitleAsync(mediaLease, manualSubtitle, select: true, cancellationToken);
            }
        }
        catch
        {
            ReleaseMediaStreams();
            throw;
        }
    }

    private async Task OpenMediaSourcePlaylistAsync(
        IReadOnlyList<MediaResource> resources,
        MediaResource selected,
        MediaSourceReadLease selectedLease,
        string? profileId,
        CancellationToken cancellationToken)
    {
        var playlist = new List<PlaybackMediaSource>(resources.Count);
        var leases = new Dictionary<string, MediaSourceReadLease>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var lease = resource.Uri == selected.Uri ? selectedLease : selectedLease.AcquireSibling(resource);
            if (!ReferenceEquals(lease, selectedLease)) _mediaSourceReadLeases.Add(lease);
            var registration = StreamBroker.Register(resource.Name, lease.Open);
            _mediaStreamRegistrations.Add(registration);
            leases.Add(registration.Source, lease);
            playlist.Add(new PlaybackMediaSource(registration.Source, resource.Uri.AbsoluteUri));
        }

        Shell.Playback.ConfigureMediaSourceSubtitleLoader((source, token) =>
            leases.TryGetValue(source, out var lease)
                ? TryAutoAddMediaSourceSubtitleAsync(lease, source, token)
                : Task.CompletedTask);
        var startIndex = playlist.FindIndex(source => source.HistorySource == selected.Uri.AbsoluteUri);
        await Shell.Playback.OpenMediaSourcePlaylistAsync(playlist, startIndex, profileId, cancellationToken);
    }

    private async Task AddMediaSourceSubtitleAsync(
        MediaSourceReadLease mediaLease,
        MediaResource subtitle,
        bool select,
        CancellationToken cancellationToken,
        bool automatic = false) =>
        await LoadNetworkSubtitleAsync(mediaLease.AcquireSibling(subtitle), subtitle, select,
            cancellationToken, automatic);

    public Task AddNetworkSubtitleAsync(
        MediaSourceReadLease lease,
        MediaResource subtitle,
        CancellationToken cancellationToken = default) =>
        LoadNetworkSubtitleAsync(lease, subtitle, select: true, cancellationToken);

    private async Task RestoreRememberedNetworkSubtitleAsync(
        PlaybackExternalSubtitleSource source, bool select, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.MediaSourceProfileId) ||
            !Uri.TryCreate(source.Source, UriKind.Absolute, out var uri))
            throw new FileNotFoundException("上次使用的网络字幕来源已不可用。", source.Source);

        await using var browser = await Shell.MediaSources.OpenSubtitleSourceAsync(
            source.MediaSourceProfileId, uri, cancellationToken);
        var resource = browser.Entries.FirstOrDefault(item => !item.IsDirectory && item.Uri == uri);
        if (resource is null) throw new FileNotFoundException("上次使用的网络字幕已不存在。", source.Source);
        await LoadNetworkSubtitleAsync(browser.AcquireRead(resource), resource, select,
            cancellationToken, automatic: true, persistentSource: source);
    }

    public async Task AddOnlineSubtitleAsync(
        OnlineSubtitleSource subtitle,
        CancellationToken cancellationToken = default)
    {
        var playbackSource = Shell.Playback.Source;
        var content = await Shell.OnlineSubtitles.DownloadAsync(subtitle, cancellationToken);
        if (!string.Equals(playbackSource, Shell.Playback.Source, StringComparison.Ordinal)) return;
        var name = ExternalSubtitleMatcher.IsSubtitleFile(subtitle.Name)
            ? subtitle.Name : subtitle.Uri.AbsolutePath;
        var path = await _subtitleCache.StoreAsync(content, name, cancellationToken);
        if (string.Equals(playbackSource, Shell.Playback.Source, StringComparison.Ordinal))
            await Shell.Playback.AddSubtitleAsync(path, cancellationToken: cancellationToken);
    }

    private async Task LoadNetworkSubtitleAsync(
        MediaSourceReadLease subtitleLease,
        MediaResource subtitle,
        bool select,
        CancellationToken cancellationToken,
        bool automatic = false,
        PlaybackExternalSubtitleSource? persistentSource = null)
    {
        MediaSourceReadLease? companionLease = null;
        MediaStreamRegistration? registration = null;
        try
        {
            if (Path.GetExtension(subtitle.Name).Equals(".idx", StringComparison.OrdinalIgnoreCase))
            {
                var companion = await subtitleLease.FindSubtitleCompanionAsync(subtitle, cancellationToken)
                    ?? throw new FileNotFoundException("IDX 字幕缺少对应的 SUB 文件。", subtitle.Name);
                companionLease = subtitleLease.AcquireSibling(companion);
                registration = StreamBroker.RegisterPair(
                    subtitle.Name, subtitleLease.Open, companion.Name, companionLease.Open);
            }
            else
            {
                registration = StreamBroker.Register(subtitle.Name, subtitleLease.Open);
            }

            persistentSource ??= new PlaybackExternalSubtitleSource
            {
                Source = subtitle.Uri.AbsoluteUri,
                MediaSourceProfileId = Shell.Playback.CurrentMediaSourceProfileId,
            };
            if (automatic)
            {
                await Shell.Playback.AddAutomaticSubtitleAsync(registration.Source, select,
                    cancellationToken, persistentSource);
            }
            else
            {
                await Shell.Playback.AddSubtitleAsync(registration.Source, select,
                    cancellationToken, persistentSource);
            }

            _mediaSourceReadLeases.Add(subtitleLease);
            if (companionLease is not null) _mediaSourceReadLeases.Add(companionLease);
            _mediaStreamRegistrations.Add(registration);
        }
        catch
        {
            registration?.Dispose();
            companionLease?.Dispose();
            subtitleLease.Dispose();
            throw;
        }
    }

    private async Task TryAutoAddMediaSourceSubtitleAsync(
        MediaSourceReadLease mediaLease,
        string playbackSource,
        CancellationToken cancellationToken)
    {
        var settings = Shell.Settings.ActiveMpvSettings;
        if (string.Equals(settings.SubtitleAuto, "no", StringComparison.OrdinalIgnoreCase)) return;
        var revision = Shell.Playback.ManualSubtitleRevision;
        using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var matches = await mediaLease.FindMatchingSubtitlesAsync(
                settings.SubtitleLanguages, settings.SubtitleAuto, discoveryTimeout.Token);
            if (matches.Count == 0 || discoveryTimeout.IsCancellationRequested ||
                !string.Equals(playbackSource, Shell.Playback.Source, StringComparison.Ordinal) ||
                revision != Shell.Playback.ManualSubtitleRevision) return;
            var bySource = matches.ToDictionary(static match => match.Match.Candidate.Source,
                static match => match.Resource, StringComparer.Ordinal);
            Shell.Playback.SetDiscoveredSubtitleChoices(
                playbackSource,
                matches.Skip(1).Select(static match => match.Match.Candidate).ToArray(),
                (candidate, token) => AddMediaSourceSubtitleAsync(
                    mediaLease, bySource[candidate.Source], select: true, token));
            var best = matches[0];
            var selectedPreferredInternal = Shell.Playback.Tracks.Any(track =>
                track.Type == PlaybackTrackType.Subtitle && track.IsSelected && !track.IsExternal &&
                ExternalSubtitleMatcher.IsPreferredLanguage(track.Language, settings.SubtitleLanguages));
            await AddMediaSourceSubtitleAsync(
                mediaLease, best.Resource, best.Match.SelectOverEmbedded || !selectedPreferredInternal,
                discoveryTimeout.Token, automatic: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Shell.Playback.ReportSubtitleDiscoveryFailure(exception);
        }
    }

    public async Task PlayStorageFileAsync(
        IStorageFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ReleaseMediaStreams();
        try
        {
            var localPath = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                file.Dispose();
                await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
                await Shell.Playback.OpenAsync(localPath, cancellationToken);
                return;
            }

            var (videos, subtitles) = await BrowseStorageSiblingsAsync(file);
            _storageFiles.Add(file);
            _storageFiles.AddRange(videos.Where(sibling => !ReferenceEquals(sibling, file)));
            _storageFiles.AddRange(subtitles.Select(static subtitle => subtitle.File));
            var sources = new List<string>(videos.Count);
            var videoBySource = new Dictionary<string, IStorageFile>(StringComparer.Ordinal);
            foreach (var video in videos)
            {
                var registration = StreamBroker.Register(
                    video.Name, () => new AndroidStorageMediaByteStream(video));
                _mediaStreamRegistrations.Add(registration);
                _storagePlaybackFiles[registration.Source] = video;
                videoBySource.Add(registration.Source, video);
                sources.Add(registration.Source);
            }
            Shell.Playback.ConfigureMediaSourceSubtitleLoader((source, token) =>
                videoBySource.TryGetValue(source, out var video)
                    ? TryAutoAddStorageSubtitleAsync(source, video, videos.Count, subtitles, token)
                    : Task.CompletedTask);
            await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
            await Shell.Playback.OpenPlaylistAsync(sources,
                sources.FindIndex(source => ReferenceEquals(videoBySource[source], file)), cancellationToken);
        }
        catch
        {
            ReleaseMediaStreams();
            throw;
        }
    }

    private static async Task<(IReadOnlyList<IStorageFile> Videos,
        IReadOnlyList<(IStorageFile File, bool SameDirectory)> Subtitles)> BrowseStorageSiblingsAsync(
        IStorageFile selected)
    {
        var videos = new List<IStorageFile> { selected };
        var subtitles = new List<(IStorageFile File, bool SameDirectory)>();
        IStorageFolder? folder = null;
        try
        {
            folder = await selected.GetParentAsync();
            if (folder is null || !ExternalSubtitleMatcher.IsVideoFile(selected.Name))
                return (videos, subtitles);

            await foreach (var item in folder.GetItemsAsync())
            {
                if (item is IStorageFile sibling)
                {
                    if (sibling.Path == selected.Path)
                    {
                        if (!ReferenceEquals(sibling, selected)) sibling.Dispose();
                    }
                    else if (ExternalSubtitleMatcher.IsVideoFile(sibling.Name)) videos.Add(sibling);
                    else if (ExternalSubtitleMatcher.IsSubtitleFile(sibling.Name)) subtitles.Add((sibling, true));
                    else sibling.Dispose();
                }
                else if (item is IStorageFolder subtitleFolder &&
                    (subtitleFolder.Name.Equals("Subs", StringComparison.OrdinalIgnoreCase) ||
                     subtitleFolder.Name.Equals("Subtitles", StringComparison.OrdinalIgnoreCase) ||
                     subtitleFolder.Name.Equals("字幕", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        await foreach (var nested in subtitleFolder.GetItemsAsync())
                        {
                            if (nested is IStorageFile subtitle &&
                                ExternalSubtitleMatcher.IsSubtitleFile(subtitle.Name))
                                subtitles.Add((subtitle, false));
                            else nested.Dispose();
                        }
                    }
                    catch { /* An optional subtitle folder may be inaccessible. */ }
                    subtitleFolder.Dispose();
                }
                else item.Dispose();
            }
            videos.Sort((left, right) =>
            {
                var result = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                return result != 0 ? result : StringComparer.Ordinal.Compare(left.Name, right.Name);
            });
            return (videos, subtitles);
        }
        catch
        {
            foreach (var video in videos.Where(video => !ReferenceEquals(video, selected))) video.Dispose();
            foreach (var (subtitle, _) in subtitles) subtitle.Dispose();
            return ([selected], []);
        }
        finally { folder?.Dispose(); }
    }

    private async Task TryAutoAddStorageSubtitleAsync(
        string source,
        IStorageFile video,
        int videoCount,
        IReadOnlyList<(IStorageFile File, bool SameDirectory)> subtitles,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = Shell.Settings.ActiveMpvSettings;
            var revision = Shell.Playback.ManualSubtitleRevision;
            var candidates = subtitles.Select(subtitle => new ExternalSubtitleCandidate(
                subtitle.File.Path.AbsoluteUri, subtitle.File.Name, subtitle.SameDirectory)).ToArray();
            var matches = ExternalSubtitleMatcher.Rank(video.Name, candidates,
                settings.SubtitleLanguages, settings.SubtitleAuto, videoCount);
            if (matches.Count == 0 || cancellationToken.IsCancellationRequested ||
                !string.Equals(source, Shell.Playback.Source, StringComparison.Ordinal) ||
                revision != Shell.Playback.ManualSubtitleRevision) return;

            var files = subtitles.ToDictionary(static subtitle => subtitle.File.Path.AbsoluteUri,
                static subtitle => subtitle.File, StringComparer.Ordinal);
            Shell.Playback.SetDiscoveredSubtitleChoices(source,
                matches.Skip(1).Select(static match => match.Candidate).ToArray(),
                (candidate, token) => AddMatchedStorageSubtitleAsync(
                    candidate, files, select: true, automatic: false, token));
            var best = matches[0];
            var preferredEmbedded = Shell.Playback.Tracks.Any(track =>
                track.Type == PlaybackTrackType.Subtitle && track.IsSelected && !track.IsExternal &&
                ExternalSubtitleMatcher.IsPreferredLanguage(track.Language, settings.SubtitleLanguages));
            await AddMatchedStorageSubtitleAsync(best.Candidate, files,
                best.SelectOverEmbedded || !preferredEmbedded, automatic: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Shell.Playback.ReportSubtitleDiscoveryFailure(exception); }
    }

    private async Task AddMatchedStorageSubtitleAsync(
        ExternalSubtitleCandidate candidate,
        IReadOnlyDictionary<string, IStorageFile> files,
        bool select,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var file = files[candidate.Source];
        MediaStreamRegistration registration;
        if (Path.GetExtension(file.Name).Equals(".idx", StringComparison.OrdinalIgnoreCase))
        {
            var companion = files.Values.FirstOrDefault(other =>
                Path.GetExtension(other.Name).Equals(".sub", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(other.Name).Equals(
                    Path.GetFileNameWithoutExtension(file.Name), StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("IDX 字幕缺少对应的 SUB 文件。", file.Name);
            registration = StreamBroker.RegisterPair(file.Name,
                () => new AndroidStorageMediaByteStream(file), companion.Name,
                () => new AndroidStorageMediaByteStream(companion));
        }
        else registration = StreamBroker.Register(file.Name, () => new AndroidStorageMediaByteStream(file));

        try
        {
            if (automatic)
                await Shell.Playback.AddAutomaticSubtitleAsync(registration.Source, select, cancellationToken);
            else
                await Shell.Playback.AddSubtitleAsync(registration.Source, select, cancellationToken);
            _mediaStreamRegistrations.Add(registration);
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    public async Task PlayStorageFilesAsync(
        IReadOnlyList<IStorageFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return;
        ReleaseMediaStreams();
        try
        {
            var sources = new List<string>(files.Count);
            foreach (var file in files)
            {
                var localPath = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    file.Dispose();
                    sources.Add(localPath);
                    continue;
                }

                _storageFiles.Add(file);
                var registration = StreamBroker.Register(
                    file.Name,
                    () => new AndroidStorageMediaByteStream(file));
                _mediaStreamRegistrations.Add(registration);
                _storagePlaybackFiles[registration.Source] = file;
                sources.Add(registration.Source);
            }

            await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
            if (sources.Count == 1)
            {
                await Shell.Playback.OpenAsync(sources[0], cancellationToken);
            }
            else
            {
                await Shell.Playback.OpenPlaylistAsync(sources, cancellationToken: cancellationToken);
            }
        }
        catch
        {
            ReleaseMediaStreams();
            throw;
        }
    }

    public async Task AddStorageSubtitleAsync(
        IStorageFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            var localPath = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                file.Dispose();
                await Shell.Playback.AddSubtitleAsync(localPath, cancellationToken: cancellationToken);
                return;
            }

            await using var input = await file.OpenReadAsync();
            var path = await _subtitleCache.StoreAsync(input, file.Name, cancellationToken);
            file.Dispose();
            await Shell.Playback.AddSubtitleAsync(path, cancellationToken: cancellationToken);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public async Task AddStorageFilesToPlaylistAsync(
        IReadOnlyList<IStorageFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return;
        var sources = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var localPath = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    file.Dispose();
                    sources.Add(localPath);
                    continue;
                }

                _storageFiles.Add(file);
                var registration = StreamBroker.Register(
                    file.Name,
                    () => new AndroidStorageMediaByteStream(file));
                _mediaStreamRegistrations.Add(registration);
                sources.Add(registration.Source);
            }
            await Shell.Playback.AddToPlaylistAsync(sources, cancellationToken: cancellationToken);
        }
        catch
        {
            foreach (var file in files)
            {
                if (!_storageFiles.Contains(file)) file.Dispose();
            }
            throw;
        }
    }

    public async Task StopPlaybackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Shell.Playback.StopAsync(cancellationToken);
        }
        finally
        {
            ReleaseMediaStreams();
        }
    }

    public async Task PlaySelectedDownloadAsync(CancellationToken cancellationToken = default)
    {
        var downloads = Shell.DownloadCenter;
        var task = downloads.SelectedTask?.Snapshot;
        var file = downloads.SelectedPlayableFile;
        if (task is null || file is null)
        {
            throw new InvalidOperationException("请先选择包含可播放媒体的下载任务。");
        }

        var localPath = Path.IsPathRooted(file.Path)
            ? Path.GetFullPath(file.Path)
            : Path.GetFullPath(file.Path, task.DestinationDirectory);
        if (task.State == DownloadState.Complete ||
            (file.Length > 0 && file.CompletedLength >= file.Length))
        {
            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException("已完成的媒体文件不存在。", localPath);
            }

            await PlayLocalMediaAsync(localPath, cancellationToken: cancellationToken);
            return;
        }

        if (task.State == DownloadState.Paused)
        {
            await downloads.ResumeTaskAsync(task.Id, cancellationToken);
        }

        ReleaseMediaStreams();
        try
        {
            var registration = StreamBroker.Register(
                Path.GetFileName(file.Path),
                () => downloads.OpenMediaStreamAsync(task.Id, file.Index)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult(),
                MediaStreamCachePolicy.Disabled);
            _mediaStreamRegistrations.Add(registration);
            await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
            await Shell.Playback.OpenAsync(registration.Source, cancellationToken);
        }
        catch
        {
            ReleaseMediaStreams();
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        await InitializeModuleAsync("设置", () => Shell.Settings.InitializeAsync(cancellationToken).AsTask(), errors);
        ApplyAndroidPlaybackStartupPolicy();
        await InitializeModuleAsync("下载引擎", () => Shell.DownloadCenter.InitializeAsync(cancellationToken).AsTask(), errors);
        await InitializeModuleAsync("播放引擎", () => Shell.Playback.InitializeAsync(cancellationToken).AsTask(), errors);
        await InitializeModuleAsync("媒体源", () => Shell.MediaSources.InitializeAsync(cancellationToken), errors);
        if (Shell.Plugins is not null)
        {
            await InitializeModuleAsync("插件", () => Shell.Plugins.InitializeAsync(cancellationToken), errors);
        }
        if (Shell.Agent is not null)
        {
            await InitializeModuleAsync("Agent", () => Shell.Agent.InitializeAsync(cancellationToken), errors);
        }
        return errors;
    }

    private void ApplyAndroidPlaybackStartupPolicy()
    {
        var settings = Shell.Settings.Mpv.ToSettings();
        IReadOnlyDictionary<string, string> configured;
        try
        {
            configured = MpvOptionConfiguration.Build(settings);
        }
        catch (InvalidDataException exception)
        {
            // Settings saved by an older build may contain advanced options that are no longer accepted.
            // SettingsViewModel already reported the problem and configured mpv with defaults.
            _log.Write(
                LogComponent.Mpv,
                ApplicationLogLevel.Error,
                "Skipping Android playback policy because the saved mpv settings are invalid.",
                exception);
            return;
        }
        var options = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
        var appliedPolicies = new List<string>();

        if (string.Equals(settings.HardwareDecoding, "auto-safe", StringComparison.OrdinalIgnoreCase))
        {
            // mpv's generic auto-safe list selects mediacodec-copy on Android.
            // Prefer the AImageReader/EGLImage interop path so decoded 4K frames
            // remain on the GPU, while retaining the copy path for devices and
            // codecs that cannot expose a hardware surface to libmpv.
            options["hwdec"] = "mediacodec,mediacodec-copy";
            appliedPolicies.Add("mediacodec zero-copy with copy fallback");
        }

        if (string.Equals(settings.AudioChannels, "auto-safe", StringComparison.OrdinalIgnoreCase))
        {
            // AudioTrack accepts multichannel masks even on handsets whose
            // physical route is stereo. Several vendor implementations then
            // repeatedly underrun while performing their own 5.1 downmix.
            // Let mpv downmix once before AudioTrack; an explicit user channel
            // layout (for example HDMI 5.1) is still respected.
            options["audio-channels"] = "stereo";
            appliedPolicies.Add("stereo AudioTrack output for auto-safe audio");
        }

        // Android's static libass build has no fontconfig (or other system font
        // provider). Without a font directory, text subtitles can have a
        // selected track and still render no glyphs; embedded MKV fonts hide
        // this for some files. Keep an explicit user font directory if set.
        const string systemFonts = "/system/fonts";
        if (Directory.Exists(systemFonts) && !options.ContainsKey("sub-fonts-dir"))
        {
            options["sub-fonts-dir"] = systemFonts;
            appliedPolicies.Add("Android system fonts for subtitles");
        }

        if (!options.ContainsKey("sub-use-margins"))
        {
            // A portrait phone has large bars above and below landscape video.
            // mpv otherwise puts plain-text subtitles at the bottom of the
            // screen, far below the picture and beneath the player controls.
            options["sub-use-margins"] = "no";
            appliedPolicies.Add("subtitles inside video frame");
        }

        // The generic "sans-serif" alias requires a system font provider.
        // Select an actual family in the directory as libass's fallback for
        // SRT and for ASS styles that request fonts not installed on the phone.
        if (string.Equals(settings.SubtitleFont, "sans-serif", StringComparison.OrdinalIgnoreCase) &&
            options.TryGetValue("sub-fonts-dir", out var subtitleFontsDirectory) &&
            string.Equals(subtitleFontsDirectory, systemFonts, StringComparison.Ordinal))
        {
            if (File.Exists(Path.Combine(systemFonts, "NotoSansCJK-Regular.ttc")))
            {
                options["sub-font"] = "Noto Sans CJK SC";
                appliedPolicies.Add("CJK subtitle fallback font");
            }
            else if (File.Exists(Path.Combine(systemFonts, "Roboto-Regular.ttf")))
            {
                options["sub-font"] = "Roboto";
                appliedPolicies.Add("Roboto subtitle fallback font");
            }
        }

        if (appliedPolicies.Count == 0)
        {
            return;
        }

        _playbackController.ConfigureStartupOptions(options);
        _log.Write(
            LogComponent.App,
            ApplicationLogLevel.Information,
            $"Applied Android playback policy: {string.Join("; ", appliedPolicies)}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await Shell.DisposeAsync();
        foreach (var subscription in _backgroundSubscriptions) subscription.Dispose();
        _backgroundSubscriptions.Clear();
        ReleaseMediaStreams();
        _pluginAudit.Dispose();
        _toolAudit.Dispose();
        _agentHttpClient.Dispose();
        MpvEngine.Dispose();
        StreamBroker.Dispose();
        _log.Dispose();
    }

    private async Task PlayConfiguredMediaAsync(
        ConfiguredMediaPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReleaseMediaStreams();
        try
        {
            string source;
            string? subtitle = null;
            if (request.IsLocal)
            {
                source = request.LocalPath!;
                subtitle = request.LocalSubtitlePath;
            }
            else
            {
                var mediaLease = request.MediaLease
                    ?? throw new InvalidOperationException("媒体流句柄无效。");
                _mediaSourceReadLeases.Add(mediaLease);
                var mediaRegistration = StreamBroker.Register(request.Name, mediaLease.Open);
                _mediaStreamRegistrations.Add(mediaRegistration);
                source = mediaRegistration.Source;
                if (request.SubtitleLease is not null)
                {
                    _mediaSourceReadLeases.Add(request.SubtitleLease);
                    var subtitleRegistration = StreamBroker.Register(
                        request.SubtitleName ?? "subtitle.srt",
                        request.SubtitleLease.Open);
                    _mediaStreamRegistrations.Add(subtitleRegistration);
                    subtitle = subtitleRegistration.Source;
                }
            }

            await RequireView().WaitForPlaybackSurfaceAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                Shell.Playback.SuppressAutomaticSubtitleForNextOpen();
            }
            await Shell.Playback.OpenAsync(source, cancellationToken);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                await Shell.Playback.AddSubtitleAsync(subtitle, cancellationToken: cancellationToken);
            }
        }
        catch
        {
            ReleaseMediaStreams();
            throw;
        }
    }

    private async Task PlayResolvedMediaAsync(
        Uri media,
        Uri? subtitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Scheme is not ("http" or "https" or MediaStreamBroker.Scheme) ||
            !string.IsNullOrEmpty(media.UserInfo))
        {
            throw new ArgumentException("Resolved media must use HTTP(S) without embedded credentials.", nameof(media));
        }
        if (subtitle is not null &&
            (subtitle.Scheme is not ("http" or "https" or MediaStreamBroker.Scheme) ||
             !string.IsNullOrEmpty(subtitle.UserInfo)))
        {
            throw new ArgumentException("Resolved subtitles must use HTTP(S) without embedded credentials.", nameof(subtitle));
        }

        await PlayLocalMediaAsync(media.AbsoluteUri, subtitle?.AbsoluteUri, cancellationToken);
    }

    private string GetAutomationContext() => _view?.CurrentPage ?? "downloads";

    private Task NavigateForAutomationAsync(string page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireView().NavigateForAutomation(page);
        return Task.CompletedTask;
    }

    private Task ShowAutomationNotificationAsync(
        string message,
        string severity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireView().ShowAutomationNotification(message, severity);
        return Task.CompletedTask;
    }

    private MobileShellView RequireView() =>
        _view ?? throw new InvalidOperationException("Android UI is not attached.");

    private void ReleaseMediaStreams()
    {
        Shell.Playback.ConfigureMediaSourceSubtitleLoader(null);
        _storagePlaybackFiles.Clear();
        foreach (var registration in _mediaStreamRegistrations) registration.Dispose();
        _mediaStreamRegistrations.Clear();
        foreach (var lease in _mediaSourceReadLeases) lease.Dispose();
        _mediaSourceReadLeases.Clear();
        foreach (var file in _storageFiles) file.Dispose();
        _storageFiles.Clear();
    }

    private void TrackBackgroundState(IEventBus eventBus)
    {
        _backgroundSubscriptions.Add(eventBus.Subscribe<DownloadTaskChanged>(message =>
        {
            if (message.Snapshot.State == DownloadState.Removed)
            {
                _backgroundDownloads.TryRemove(message.Snapshot.Id, out _);
            }
            else
            {
                _backgroundDownloads[message.Snapshot.Id] = message.Snapshot.State;
            }
        }));
        _backgroundSubscriptions.Add(eventBus.Subscribe<PlaybackChanged>(message =>
        {
            Volatile.Write(ref _backgroundPlaybackState, (int)message.Snapshot.State);
            Volatile.Write(ref _backgroundPlaybackHasMedia,
                string.IsNullOrWhiteSpace(message.Snapshot.Source) ? 0 : 1);
        }));
    }

    private static async Task DispatchAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }
        var task = await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
        await task.ConfigureAwait(false);
    }

    private static TrustedAgentApprovalDetails CreatePluginEnableApprovalDetails(
        PluginLifecycleManager plugins,
        JsonObject arguments)
    {
        var id = arguments["id"]?.GetValue<string>()
            ?? throw new InvalidDataException("Plugin enable request is missing its plugin ID.");
        var status = plugins.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Manifest.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Plugin '{id}' is not installed.");
        var permissionLines = status.Manifest.Permissions.Count == 0
            ? "无（插件未申请 capability）"
            : string.Join(Environment.NewLine, status.Manifest.Permissions.Select(permission =>
                permission.Scopes.Count == 0
                    ? $"• {permission.Capability}"
                    : $"• {permission.Capability}: {string.Join(", ", permission.Scopes)}"));
        return new TrustedAgentApprovalDetails(
            $"{status.Manifest.Name} ({status.Manifest.Id}) v{status.Manifest.Version}",
            permissionLines,
            status.Signature is null
                ? "未签名"
                : $"签名 {status.Signature.Algorithm} · {status.Signature.KeyId} · {(status.Signature.IsTrusted ? "受信" : "未受信")}",
            status.RequiresPermissionApproval ? "批准上述全部权限并启用插件" : "启用插件（权限此前已批准）",
            $"安装包 SHA-256: {status.PackageSha256}");
    }

    private static TrustedAgentApprovalDetails CreatePluginInstallApprovalDetails(JsonObject review)
    {
        var plugin = review["plugin"] as JsonObject
            ?? throw new InvalidDataException("Trusted plugin review is missing plugin metadata.");
        var permissions = plugin["permissions"] as JsonArray;
        var permissionLines = permissions is null || permissions.Count == 0
            ? "无（插件未申请 capability）"
            : string.Join(Environment.NewLine, permissions.OfType<JsonObject>().Select(permission =>
            {
                var capability = permission["capability"]?.GetValue<string>() ?? "unknown";
                var scopes = permission["scopes"] as JsonArray;
                return scopes is null || scopes.Count == 0
                    ? $"• {capability}"
                    : $"• {capability}: {string.Join(", ", scopes.Select(scope => scope?.GetValue<string>() ?? string.Empty))}";
            }));
        var changes = (review["diff"] as JsonObject)?["changes"] as JsonArray;
        var changeLines = changes is null || changes.Count == 0
            ? "无文件变化"
            : string.Join(Environment.NewLine, changes.OfType<JsonObject>().Select(change =>
                $"• {change["change"]?.GetValue<string>()}: {change["path"]?.GetValue<string>()}"));
        var checks = review["checks"] as JsonObject
            ?? throw new InvalidDataException("Trusted plugin review is missing check results.");
        return new TrustedAgentApprovalDetails(
            $"{plugin["name"]?.GetValue<string>()} ({plugin["id"]?.GetValue<string>()}) v{plugin["version"]?.GetValue<string>()}",
            permissionLines,
            $"Lint: {checks["lint"]?.GetValue<string>()} · Build: {checks["build"]?.GetValue<string>()} · " +
            $"QuickJS 隔离测试: {checks["sandboxTest"]?.GetValue<string>()} · Pack: {checks["package"]?.GetValue<string>()}",
            changeLines,
            $"工作区 SHA-256: {review["workspaceDigest"]?.GetValue<string>()}{Environment.NewLine}" +
            $"安装包 SHA-256: {review["packageSha256"]?.GetValue<string>()}");
    }

    private static async Task InitializeModuleAsync(
        string name,
        Func<Task> initialize,
        ICollection<string> errors)
    {
        try
        {
            await initialize();
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("CanX", $"{name} initialization failed: {exception}");
            errors.Add($"{name}：{exception.Message}");
        }
    }

    private static void ApplyTheme(string value)
    {
        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }
        app.RequestedThemeVariant = value.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
