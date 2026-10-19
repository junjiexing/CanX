// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
using CanX.Core.Downloads;
using CanX.Core.Diagnostics;
using CanX.Core.Events;
using CanX.Core.Playback;
using CanX.Downloads;
using CanX.Downloads.Engine;
using CanX.Downloads.Persistence;
using CanX.Infrastructure.Security;
using CanX.Infrastructure.Configuration;
using CanX.Infrastructure.Downloads;
using CanX.Infrastructure.Diagnostics;
using CanX.Infrastructure.MediaAccess;
using CanX.Infrastructure.Playback;
using CanX.MediaAccess;
using CanX.Playback;
using CanX.Playback.Engine;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Services;
using CanX.Plugins.Runtime;
using CanX.Streaming;
using CanX.Tooling;

namespace CanX.App;

public sealed partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataDirectory = GetApplicationDataDirectory();
            var applicationLog = new FileApplicationLog(Path.Combine(dataDirectory, "logs"));
            ApplicationLogHost.Current = applicationLog;
            var eventBus = new InMemoryEventBus(exception => applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "Application event subscriber failed.",
                exception));
            var credentialStore = new PlatformCredentialStore();
            var manager = new DownloadManager(
                new NativeAria2Engine(applicationLog),
                new CredentialProtectingDownloadSessionStore(
                    new JsonDownloadSessionStore(Path.Combine(dataDirectory, "downloads.json")),
                    credentialStore),
                eventBus,
                new DownloadManagerOptions
                {
                    WorkerFaultHandler = exception => applicationLog.Write(
                        LogComponent.Aria2,
                        ApplicationLogLevel.Error,
                        "Download worker skipped a failed maintenance step.",
                        exception),
                });
            var completionCoordinator = new DownloadCompletionCoordinator(
                eventBus,
                new DesktopDownloadCompletionHandler());
            var streamBroker = new MediaStreamBroker();
            var mpvEngine = new NativeMpvEngine(
                applicationLog,
                streamBroker,
                DesktopDisplayRefreshRate.DetectPrimary());
            var playbackStates = new SharedPlaybackStateStore(
                new JsonPlaybackStateStore(Path.Combine(dataDirectory, "playback-state.json"), applicationLog));
            var primaryPlaybackState = playbackStates.CreateSession(restorePlaylist: true);
            var playbackController = new PlaybackController(
                mpvEngine,
                eventBus,
                primaryPlaybackState);
            var mpvOptionValidator = new NativeMpvOptionValidator();
            var settingsStore = new JsonApplicationSettingsStore(Path.Combine(dataDirectory, "settings.json"));
            var mediaSourceProfileStore = new CredentialProtectingMediaSourceProfileStore(
                Path.Combine(dataDirectory, "media-sources.json"),
                credentialStore);
            var pluginServiceRegistry = new PluginServiceRegistry(applicationLog);
            var toolAudit = new FileToolAuditSink(Path.Combine(dataDirectory, "logs", "tool-audit.jsonl"));
            var toolRegistry = new ToolRegistry(
                new DefaultToolAuthorizer(),
                toolAudit,
                exception => applicationLog.Write(
                    LogComponent.App,
                    ApplicationLogLevel.Error,
                    "Tool audit write failed.",
                    exception));
            toolRegistry.ReplaceHandlers(
                DownloadToolCatalog.Create(manager)
                    .Concat(PlaybackToolCatalog.Create(playbackController))
                    .Concat(PluginServiceToolCatalog.Create(pluginServiceRegistry)));
            var pluginHostFactory = new QuickJsPluginHostFactory(
                new ToolRegistryPluginDispatcher(toolRegistry),
                log: applicationLog,
                pluginDataRoot: Path.Combine(dataDirectory, "plugin-data"),
                credentialStore: credentialStore,
                serviceRegistry: pluginServiceRegistry,
                mediaStreamBroker: streamBroker);
            var pluginAudit = new JsonLinesPluginAuditSink(
                Path.Combine(dataDirectory, "logs", "plugin-audit.jsonl"));
            var pluginLifecycleManager = new PluginLifecycleManager(
                Path.Combine(dataDirectory, "plugins"),
                pluginHostFactory,
                applicationLog,
                audit: pluginAudit);
            var agentApiKeyProvider = new CredentialAgentApiKeyProvider(credentialStore);
            var agentApprovalService = new AgentApprovalService();
            var agentHttpClient = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            agentHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CanX/1.0");
            var agentChatProvider = new OpenAiCompatibleChatProvider(agentHttpClient, agentApiKeyProvider);
            var agentRunner = new AgentRunner(
                agentChatProvider,
                toolRegistry,
                agentApprovalService);
            var viewModel = new ShellViewModel(
                manager,
                eventBus,
                playbackController,
                mpvOptionValidator,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                settingsStore,
                ApplyTheme,
                applicationLog,
                mediaSourceProfileStore,
                completionCoordinator,
                pluginLifecycleManager,
                Path.Combine(dataDirectory, "plugin-development-mode"),
                applicationLog.DirectoryPath,
                pluginHostFactory.UiRegistry,
                pluginHostFactory.ServiceRegistry,
                agentApiKeyProvider,
                agentRunner,
                new JsonAgentConversationStore(Path.Combine(dataDirectory, "agent-conversations.json")),
                agentApprovalService,
                deferAgentCredentialStatusLoad: OperatingSystem.IsMacOS(),
                agentModelCatalog: new OpenAiCompatibleModelCatalog(agentHttpClient, agentApiKeyProvider),
                agentChatTester: new OpenAiCompatibleChatConnectionTester(
                    agentHttpClient, agentApiKeyProvider, () => toolRegistry.Descriptors));
            viewModel.Playback.ConfigureDisplaySleepInhibition(DesktopDisplaySleepInhibitor.AcquireAsync);
            var window = new MainWindow(
                viewModel,
                mpvEngine,
                streamBroker,
                pluginHostFactory.DownloadPromptBroker,
                pluginHostFactory.MediaPlaybackBroker,
                playbackSessionFactory: () =>
                {
                    var sessionBus = new InMemoryEventBus(exception => applicationLog.Write(
                        CanX.Core.Diagnostics.LogComponent.Mpv, ApplicationLogLevel.Error,
                        "Playback session event subscriber failed.", exception));
                    var sessionState = playbackStates.CreateSession();
                    var engine = new NativeMpvEngine(applicationLog, streamBroker, DesktopDisplayRefreshRate.DetectPrimary());
                    var controller = new PlaybackController(engine, sessionBus, sessionState);
                    controller.ConfigureStartupOptions(CanX.Core.Configuration.MpvOptionConfiguration.Build(viewModel.Settings.ActiveMpvSettings));
                    var playback = new PlaybackViewModel(controller, sessionBus, applicationLog: applicationLog);
                    playback.ConfigureDisplaySleepInhibition(DesktopDisplaySleepInhibitor.AcquireAsync);
                    playback.ConfigureAutomaticSubtitles(() => viewModel.Settings.ActiveMpvSettings);
                    return new DesktopPlaybackSession(playback, engine, true, sessionState.Activate);
                },
                activatePrimarySession: primaryPlaybackState.Activate);
            var automation = new DesktopAutomationService(
                mediaSourceProfileStore,
                pluginLifecycleManager,
                settingsStore,
                applicationLog,
                (request, token) => DispatchAsync(() => window.PlayConfiguredMediaAsync(request, token), token),
                token => DispatchAsync(async () =>
                {
                    var error = await viewModel.Settings.InitializeAsync(token);
                    if (!string.IsNullOrWhiteSpace(error)) throw new InvalidDataException(error);
                }, token),
                token => DispatchAsync(
                    () => viewModel.Plugins?.RefreshAsync() ?? Task.CompletedTask,
                    token),
                token => DispatchAsync(
                    () => viewModel.MediaSources.InitializeAsync(token),
                    token),
                window.GetAutomationContext,
                (page, token) => DispatchAsync(() => window.NavigateForAutomationAsync(page, token), token),
                (message, severity, token) => DispatchAsync(
                    () => window.ShowAutomationNotificationAsync(message, severity, token),
                    token));
            var searchActions = new DesktopSearchActionService(
                manager,
                () => viewModel.Settings.DownloadDirectory,
                (media, subtitle, token) => DispatchAsync(
                    () => window.PlayResolvedMediaAsync(media, subtitle, token),
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
                applicationLog,
                Path.Combine(dataDirectory, "plugin-workspaces", ".trial-data"),
                httpHandlerFactory: static () => new BlockedPluginNetworkHandler());
            var workspaceManager = new PluginWorkspaceManager(
                Path.Combine(dataDirectory, "plugin-workspaces"),
                pluginLifecycleManager,
                workspaceTrialFactory,
                FindTypeScriptCompiler(),
                token => DispatchAsync(
                    () => viewModel.Plugins?.RefreshAsync() ?? Task.CompletedTask,
                    token));
            agentApprovalService.SetTrustedDetailsProvider(async (request, token) =>
            {
                if (string.Equals(request.ToolName, "plugins.enable", StringComparison.Ordinal))
                {
                    return CreatePluginEnableApprovalDetails(pluginLifecycleManager, request.Arguments);
                }
                if (!string.Equals(request.ToolName, "plugins.workspace.install", StringComparison.Ordinal)) return null;
                var workspaceId = request.Arguments["workspaceId"]?.GetValue<string>()
                    ?? throw new InvalidDataException("Plugin workspace install request is missing its workspace ID.");
                var reviewToken = request.Arguments["reviewToken"]?.GetValue<string>()
                    ?? throw new InvalidDataException("Plugin workspace install request is missing its review token.");
                var review = await workspaceManager.GetInstallReviewAsync(workspaceId, reviewToken, token).ConfigureAwait(false);
                return CreatePluginInstallApprovalDetails(review);
            });
            toolRegistry.ReplaceHandlers(
                DownloadToolCatalog.Create(manager, () => viewModel.Settings.DownloadDirectory)
                    .Concat(PlaybackToolCatalog.Create(playbackController, async (name, token) =>
                        await Dispatcher.UIThread.InvokeAsync(() => window.ResolvePlaybackToolControllerAsync(name, token))))
                    .Concat(PluginServiceToolCatalog.Create(pluginServiceRegistry))
                    .Concat(SearchToolCatalog.Create(pluginServiceRegistry, searchActions))
                    .Concat(PluginWorkspaceToolCatalog.Create(workspaceManager))
                    .Concat(AutomationToolCatalog.Create(
                        automation,
                        automation,
                        automation,
                        automation,
                        automation,
                        toolRegistry)));
            desktop.MainWindow = window;
            var pendingOpenFiles = new List<string>();
            var readyForOpenFiles = false;
            var openingFiles = false;

            async Task FlushOpenedFilesAsync()
            {
                if (!readyForOpenFiles || openingFiles) return;
                openingFiles = true;
                try
                {
                    while (pendingOpenFiles.Count > 0)
                    {
                        var batch = pendingOpenFiles.ToArray();
                        pendingOpenFiles.Clear();
                        try
                        {
                            await window.OpenAssociatedFilesAsync(batch);
                        }
                        catch (Exception exception)
                        {
                            applicationLog.Write(LogComponent.App, ApplicationLogLevel.Error,
                                "Opening a Finder document failed.", exception);
                            await new ErrorMessageDialog("打开文件失败", exception.Message).ShowDialog(window);
                        }
                    }
                }
                finally
                {
                    openingFiles = false;
                }
            }

            if (OperatingSystem.IsWindows() && Program.InstanceGate is { } instanceGate)
            {
                instanceGate.StartServer(paths => Dispatcher.UIThread.Post(() =>
                {
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                    {
                        window.WindowState = Avalonia.Controls.WindowState.Normal;
                    }

                    window.Activate();
                    pendingOpenFiles.AddRange(paths);
                    _ = FlushOpenedFilesAsync();
                }));
            }

            if (OperatingSystem.IsMacOS() &&
                TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activation)
            {
                activation.Activated += (_, args) =>
                {
                    if (args is not FileActivatedEventArgs opened) return;
                    var paths = opened.Files.Select(file => file.Path.LocalPath).ToArray();
                    Dispatcher.UIThread.Post(() =>
                    {
                        pendingOpenFiles.AddRange(paths);
                        _ = FlushOpenedFilesAsync();
                    });
                };
            }
            desktop.Exit += (_, _) =>
            {
                applicationLog.Write(
                    LogComponent.App,
                    ApplicationLogLevel.Information,
                    "Application shutdown started.");
                try
                {
                    window.DisposePlaybackSessionsAsync().GetAwaiter().GetResult();
                    viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    applicationLog.Write(
                        LogComponent.App,
                        ApplicationLogLevel.Error,
                        "Application shutdown failed.",
                        exception);
                }
                finally
                {
                    pluginAudit.Dispose();
                    toolAudit.Dispose();
                    agentHttpClient.Dispose();
                    mpvEngine.Dispose();
                    streamBroker.Dispose();
                    ApplicationLogHost.Current = null;
                    applicationLog.Dispose();
                }
            };

            var arguments = desktop.Args ?? [];
#if CANX_SMOKE_TESTS
            var downloadFixtureSmokeIndex = Array.IndexOf(arguments, Program.DownloadFixtureSmokeArgument);
            var downloadSmokeIndex = Array.IndexOf(arguments, Program.DownloadSmokeArgument);
            var playbackSmokeIndex = Array.IndexOf(arguments, Program.PlaybackSmokeArgument);
            var playbackStreamSmokeIndex = Array.IndexOf(arguments, Program.PlaybackStreamSmokeArgument);
            var playbackHlsSmokeIndex = Array.IndexOf(arguments, Program.PlaybackHlsSmokeArgument);
            var downloadPlaybackSmokeIndex = Array.IndexOf(arguments, Program.DownloadPlaybackSmokeArgument);
            var ftpPlaybackSmokeIndex = Array.IndexOf(arguments, Program.FtpPlaybackSmokeArgument);
            var smbPlaybackSmokeIndex = Array.IndexOf(arguments, Program.SmbPlaybackSmokeArgument);
            var playbackStabilitySmokeIndex = Array.IndexOf(arguments, Program.PlaybackStabilitySmokeArgument);
            var multiWindowSmokeIndex = Array.IndexOf(arguments, Program.MultiWindowPlaybackSmokeArgument);
            var subtitleSliderSmokeIndex = Array.IndexOf(arguments, Program.SubtitleSliderSmokeArgument);
            if (subtitleSliderSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunSubtitleSliderSmokeAsync(
                    desktop, window, viewModel, mpvEngine, arguments, subtitleSliderSmokeIndex);
            }
            else if (multiWindowSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunMultiWindowPlaybackSmokeAsync(
                    desktop, window, viewModel, streamBroker, pluginHostFactory.MediaPlaybackBroker,
                    arguments, multiWindowSmokeIndex);
            }
            else if (playbackStabilitySmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunPlaybackStabilitySmokeAsync(
                    desktop,
                    window,
                    viewModel,
                    arguments,
                    playbackStabilitySmokeIndex);
            }
            else if (downloadPlaybackSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunDownloadPlaybackSmokeAsync(
                    desktop,
                    window,
                    viewModel,
                    manager,
                    mpvEngine,
                    streamBroker,
                    arguments,
                    downloadPlaybackSmokeIndex);
            }
            else if (ftpPlaybackSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunFtpPlaybackSmokeAsync(
                    desktop,
                    window,
                    viewModel,
                    mpvEngine,
                    streamBroker,
                    arguments,
                    ftpPlaybackSmokeIndex);
            }
            else if (smbPlaybackSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunSmbPlaybackSmokeAsync(
                    desktop,
                    window,
                    viewModel,
                    mpvEngine,
                    streamBroker,
                    arguments,
                    smbPlaybackSmokeIndex);
            }
            else if (playbackHlsSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunPlaybackHlsSmokeAsync(
                    desktop, window, viewModel, mpvEngine, streamBroker, arguments, playbackHlsSmokeIndex);
            }
            else if (playbackStreamSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunPlaybackStreamSmokeAsync(
                    desktop,
                    window,
                    viewModel,
                    mpvEngine,
                    streamBroker,
                    arguments,
                    playbackStreamSmokeIndex);
            }
            else if (playbackSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunPlaybackSmokeAsync(
                    desktop,
                    window,
                    viewModel,
                    mpvEngine,
                    arguments,
                    playbackSmokeIndex);
            }
            else if (downloadFixtureSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunDownloadFixtureSmokeAsync(
                    desktop,
                    manager,
                    arguments,
                    downloadFixtureSmokeIndex);
            }
            else if (downloadSmokeIndex >= 0)
            {
                window.Opened += async (_, _) => await RunDownloadSmokeAsync(
                    desktop,
                    manager,
                    arguments,
                    downloadSmokeIndex);
            }
            else if (arguments.Contains(Program.SmokeArgument, StringComparer.Ordinal))
            {
                window.Opened += (_, _) =>
                {
                    var platformHandle = window.TryGetPlatformHandle();
                    Console.WriteLine(
                        $"CANX_APP_SHELL_OK os={RuntimeInformation.OSDescription} " +
                        $"arch={RuntimeInformation.ProcessArchitecture} " +
                        $"backend={platformHandle?.HandleDescriptor ?? "unknown"} " +
                        $"decorations={window.WindowDecorations} " +
                        $"tools={toolRegistry.Descriptors.Count}");
                    DispatcherTimer.RunOnce(window.Close, TimeSpan.FromMilliseconds(150));
                };
            }
            else
#endif
            {
                window.Opened += async (_, _) =>
                {
                    var settingsLoadError = await viewModel.InitializeAsync();
                    applicationLog.Write(
                        LogComponent.App,
                        ApplicationLogLevel.Information,
                        "Application initialization completed.");
                    if (!string.IsNullOrWhiteSpace(settingsLoadError))
                    {
                        applicationLog.Write(
                            LogComponent.App,
                            ApplicationLogLevel.Error,
                            settingsLoadError);
                        await new ErrorMessageDialog("设置加载失败", settingsLoadError).ShowDialog(window);
                    }
                    pendingOpenFiles.AddRange(arguments.Where(path =>
                        CanXFileFormats.IsMediaFile(path) || CanXFileFormats.IsDownloadMetadataFile(path)));
                    readyForOpenFiles = true;
                    await FlushOpenedFilesAsync();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
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

    private static string? FindTypeScriptCompiler()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "plugin-sdk", "typescript.js"),
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "sdk", "canx-plugin-api", "node_modules", "typescript", "lib", "typescript.js"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static TrustedAgentApprovalDetails CreatePluginEnableApprovalDetails(
        PluginLifecycleManager plugins,
        System.Text.Json.Nodes.JsonObject arguments)
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

    private static TrustedAgentApprovalDetails CreatePluginInstallApprovalDetails(System.Text.Json.Nodes.JsonObject review)
    {
        var plugin = review["plugin"] as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("Trusted plugin review is missing plugin metadata.");
        var permissions = plugin["permissions"] as System.Text.Json.Nodes.JsonArray;
        var permissionLines = permissions is null || permissions.Count == 0
            ? "无（插件未申请 capability）"
            : string.Join(Environment.NewLine, permissions.OfType<System.Text.Json.Nodes.JsonObject>().Select(permission =>
            {
                var capability = permission["capability"]?.GetValue<string>() ?? "unknown";
                var scopes = permission["scopes"] as System.Text.Json.Nodes.JsonArray;
                return scopes is null || scopes.Count == 0
                    ? $"• {capability}"
                    : $"• {capability}: {string.Join(", ", scopes.Select(scope => scope?.GetValue<string>() ?? string.Empty))}";
            }));
        var diff = review["diff"] as System.Text.Json.Nodes.JsonObject;
        var changes = diff?["changes"] as System.Text.Json.Nodes.JsonArray;
        var changeLines = changes is null || changes.Count == 0
            ? "无文件变化"
            : string.Join(Environment.NewLine, changes.OfType<System.Text.Json.Nodes.JsonObject>().Select(change =>
                $"• {change["change"]?.GetValue<string>()}: {change["path"]?.GetValue<string>()}"));
        var checks = review["checks"] as System.Text.Json.Nodes.JsonObject
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

#if CANX_SMOKE_TESTS
    private static async Task RunFtpPlaybackSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        ShellViewModel viewModel,
        NativeMpvEngine mpvEngine,
        MediaStreamBroker streamBroker,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 5)
            {
                throw new ArgumentException(
                    "--ftp-playback-smoke requires endpoint, username, password, media name, and subtitle name arguments.");
            }

            var endpoint = new Uri(arguments[argumentIndex + 1], UriKind.Absolute);
            var userName = arguments[argumentIndex + 2];
            var password = arguments[argumentIndex + 3];
            var mediaName = arguments[argumentIndex + 4];
            var subtitleName = arguments[argumentIndex + 5];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var client = FtpMediaSourceClient.Create(
                endpoint,
                new System.Net.NetworkCredential(userName, password));
            var resources = await client.BrowseAsync(cancellationToken: timeout.Token);
            var media = resources.Single(resource =>
                !resource.IsDirectory && string.Equals(resource.Name, mediaName, StringComparison.Ordinal));
            var subtitle = resources.Single(resource =>
                !resource.IsDirectory && string.Equals(resource.Name, subtitleName, StringComparison.Ordinal));
            using var probe = await client.OpenReadAsync(media, timeout.Token);
            await Task.Run(() =>
            {
                Span<byte> header = stackalloc byte[16];
                EnsureSmoke(probe.Read(header, timeout.Token) > 0, "FTP media returned initial bytes");
                EnsureSmoke(probe.CanSeek, "FTP REST seeking was detected");
                var length = probe.Length.GetValueOrDefault();
                EnsureSmoke(length > 32, "FTP media returned a seekable length");
                var offset = length / 2;
                EnsureSmoke(
                    probe.Seek(offset, timeout.Token) == offset,
                    "FTP REST seek reached the requested offset");
                EnsureSmoke(probe.Read(header, timeout.Token) > 0, "FTP media returned bytes after REST seek");
            }, timeout.Token);

            using var registration = streamBroker.Register(
                media.Name,
                () => client.OpenReadAsync(media).AsTask().GetAwaiter().GetResult());
            using var subtitleRegistration = streamBroker.Register(
                subtitle.Name,
                () => client.OpenReadAsync(subtitle).AsTask().GetAwaiter().GetResult());
            var callbacksBefore = mpvEngine.StreamCallbackMetrics;
            await viewModel.InitializeAsync(timeout.Token);
            EnsureSmoke(viewModel.Playback.IsReady, viewModel.Playback.StatusMessage);
            var playbackWindow = window.ShowPlaybackWindow();
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await viewModel.Playback.OpenAsync(registration.Source, timeout.Token);
            await viewModel.Playback.AddSubtitleAsync(
                subtitleRegistration.Source,
                cancellationToken: timeout.Token);
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(24, timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.Tracks.Any(static track =>
                    track.Type == PlaybackTrackType.Subtitle && track.IsExternal && track.IsSelected),
                "FTP external subtitle selection",
                timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => mpvEngine.TryGetString("sub-text", out var text) &&
                    text.Contains("CanX FTP subtitle stream", StringComparison.Ordinal),
                "FTP external subtitle decoding",
                timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.DurationSeconds > 1,
                "FTP stream duration",
                timeout.Token);
            var seekTarget = Math.Max(0.5, viewModel.Playback.DurationSeconds / 2);
            await viewModel.Playback.SeekAsync(seekTarget, cancellationToken: timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.PositionSeconds >= seekTarget - 0.5,
                "FTP stream seek",
                timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(12, timeout.Token);
            var callbacksAfter = mpvEngine.StreamCallbackMetrics;
            EnsureSmoke(
                callbacksAfter.Reads > callbacksBefore.Reads && callbacksAfter.Seeks > callbacksBefore.Seeks,
                "libmpv used the in-process stream callbacks for FTP");
            Console.WriteLine(
                $"CANX_FTP_PLAYBACK_OK files={resources.Count} subtitles=1 " +
                $"frames={playbackWindow.RenderedFrameCount} " +
                $"reads={callbacksAfter.Reads - callbacksBefore.Reads} " +
                $"seeks={callbacksAfter.Seeks - callbacksBefore.Seeks}");
            await viewModel.Playback.StopAsync(timeout.Token);
            playbackWindow.Close();
            window.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_FTP_PLAYBACK_FAILED type={exception.GetType().Name}");
            desktop.Shutdown(1);
        }
    }

    private static async Task RunSmbPlaybackSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        ShellViewModel viewModel,
        NativeMpvEngine mpvEngine,
        MediaStreamBroker streamBroker,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 5)
            {
                throw new ArgumentException(
                    "--smb-playback-smoke requires endpoint, username, password, media name, and subtitle name arguments.");
            }

            var endpoint = new Uri(arguments[argumentIndex + 1], UriKind.Absolute);
            var userName = arguments[argumentIndex + 2];
            var password = arguments[argumentIndex + 3];
            var mediaName = arguments[argumentIndex + 4];
            var subtitleName = arguments[argumentIndex + 5];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var client = SmbMediaSourceClient.Create(
                endpoint,
                new System.Net.NetworkCredential(userName, password));
            var resources = await client.BrowseAsync(cancellationToken: timeout.Token);
            var media = resources.Single(resource =>
                !resource.IsDirectory && string.Equals(resource.Name, mediaName, StringComparison.Ordinal));
            var subtitle = resources.Single(resource =>
                !resource.IsDirectory && string.Equals(resource.Name, subtitleName, StringComparison.Ordinal));
            using var probe = await client.OpenReadAsync(media, timeout.Token);
            await Task.Run(() =>
            {
                Span<byte> header = stackalloc byte[16];
                EnsureSmoke(probe.Read(header, timeout.Token) > 0, "SMB media returned initial bytes");
                if (probe.Length is > 32)
                {
                    var offset = probe.Length.Value / 2;
                    EnsureSmoke(probe.Seek(offset, timeout.Token) == offset, "SMB seek reached the requested offset");
                    EnsureSmoke(probe.Read(header, timeout.Token) > 0, "SMB media returned bytes after seek");
                }
            }, timeout.Token);

            using var registration = streamBroker.Register(
                media.Name,
                () => client.OpenReadAsync(media).AsTask().GetAwaiter().GetResult());
            using var subtitleRegistration = streamBroker.Register(
                subtitle.Name,
                () => client.OpenReadAsync(subtitle).AsTask().GetAwaiter().GetResult());
            var callbacksBefore = mpvEngine.StreamCallbackMetrics;
            await viewModel.InitializeAsync(timeout.Token);
            EnsureSmoke(viewModel.Playback.IsReady, viewModel.Playback.StatusMessage);
            var playbackWindow = window.ShowPlaybackWindow();
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await viewModel.Playback.OpenAsync(registration.Source, timeout.Token);
            await viewModel.Playback.AddSubtitleAsync(
                subtitleRegistration.Source,
                cancellationToken: timeout.Token);
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(24, timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.Tracks.Any(static track =>
                    track.Type == PlaybackTrackType.Subtitle && track.IsExternal && track.IsSelected),
                "SMB external subtitle selection",
                timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => mpvEngine.TryGetString("sub-text", out var text) &&
                    text.Contains("CanX SMB subtitle stream", StringComparison.Ordinal),
                "SMB external subtitle decoding",
                timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.DurationSeconds > 1,
                "SMB stream duration",
                timeout.Token);
            var seekTarget = Math.Max(0.5, viewModel.Playback.DurationSeconds / 2);
            await viewModel.Playback.SeekAsync(seekTarget, cancellationToken: timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.PositionSeconds >= seekTarget - 0.5,
                "SMB stream seek",
                timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(12, timeout.Token);
            var callbacksAfter = mpvEngine.StreamCallbackMetrics;
            EnsureSmoke(
                callbacksAfter.Reads > callbacksBefore.Reads && callbacksAfter.Seeks > callbacksBefore.Seeks,
                "libmpv used the in-process stream callbacks for SMB");
            Console.WriteLine(
                $"CANX_SMB_PLAYBACK_OK files={resources.Count} subtitles=1 " +
                $"frames={playbackWindow.RenderedFrameCount} " +
                $"reads={callbacksAfter.Reads - callbacksBefore.Reads} " +
                $"seeks={callbacksAfter.Seeks - callbacksBefore.Seeks}");
            await viewModel.Playback.StopAsync(timeout.Token);
            playbackWindow.Close();
            window.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_SMB_PLAYBACK_FAILED type={exception.GetType().Name}");
            desktop.Shutdown(1);
        }
    }

    private static async Task RunDownloadPlaybackSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        ShellViewModel viewModel,
        DownloadManager manager,
        NativeMpvEngine mpvEngine,
        MediaStreamBroker streamBroker,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 2)
            {
                throw new ArgumentException(
                    "--download-playback-smoke requires URI and destination directory arguments.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await viewModel.InitializeAsync(timeout.Token);
            EnsureSmoke(viewModel.Playback.IsReady, viewModel.Playback.StatusMessage);
            EnsureSmoke(
                manager.Capabilities.Features.Contains("runtime-piece-priority", StringComparer.Ordinal),
                "the aria2 runtime piece priority API is available");
            var source = arguments[argumentIndex + 1];
            var destination = Path.GetFullPath(arguments[argumentIndex + 2]);
            var sourceKind = File.Exists(source) &&
                Path.GetExtension(source).Equals(".torrent", StringComparison.OrdinalIgnoreCase)
                ? DownloadSourceKind.Torrent
                : DownloadSourceKind.Uri;
            var outputName = sourceKind == DownloadSourceKind.Torrent
                ? null
                : arguments.Length > argumentIndex + 3
                    ? arguments[argumentIndex + 3]
                    : Path.GetFileName(new Uri(source).AbsolutePath);
            var tasks = await manager.AddAsync(new AddDownloadRequest
            {
                Kind = sourceKind,
                Source = source,
                DestinationDirectory = destination,
                OutputName = outputName,
                Options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["file-allocation"] = "none",
                    ["max-download-limit"] = "2M",
                    ["min-split-size"] = "1M",
                    ["split"] = "4",
                },
            }, timeout.Token);
            var taskId = tasks.Single().Id;
            DownloadTaskSnapshot task;
            var lastDownloadReport = DateTimeOffset.MinValue;
            do
            {
                await Task.Delay(25, timeout.Token);
                task = manager.Tasks.Single(item => item.Id == taskId);
                if (DateTimeOffset.UtcNow - lastDownloadReport >= TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(
                        $"CANX_DOWNLOAD_PLAYBACK_PROGRESS state={task.State} " +
                        $"completed={task.CompletedLength}/{task.TotalLength} " +
                        $"pieces={task.PieceCount} files={task.Files.Count}");
                    lastDownloadReport = DateTimeOffset.UtcNow;
                }
                EnsureSmoke(
                    task.State != DownloadState.Error,
                    $"aria2 download failed with error code {task.ErrorCode}");
            }
            while (task.State != DownloadState.Active ||
                   task.TotalLength <= 0 ||
                   task.Files.All(static file => file.Length <= 0) ||
                   task.PieceLength <= 0);

            EnsureSmoke(task.State != DownloadState.Complete, "the fixture completed before progressive playback began");
            var callbacksBefore = mpvEngine.StreamCallbackMetrics;
            viewModel.DownloadCenter.SelectedTask = viewModel.DownloadCenter.Tasks
                .Single(item => item.Snapshot.Id == taskId);
            await window.PlaySelectedDownloadAsync();
            var playbackWindow = window.ShowPlaybackWindow();
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(24, timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.DurationSeconds > 1,
                "progressive stream duration",
                timeout.Token);

            var seekTarget = Math.Max(1, viewModel.Playback.DurationSeconds * 0.7);
            var framesBeforeSeek = playbackWindow.RenderedFrameCount;
            var seekStopwatch = Stopwatch.StartNew();
            await viewModel.Playback.SeekAsync(seekTarget, cancellationToken: timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.PositionSeconds >= seekTarget - 1,
                "progressive stream seek",
                timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(12, timeout.Token);
            seekStopwatch.Stop();
            task = manager.Tasks.Single(item => item.Id == taskId);
            var callbacksAfter = mpvEngine.StreamCallbackMetrics;
            EnsureSmoke(
                callbacksAfter.Reads > callbacksBefore.Reads && callbacksAfter.Seeks > callbacksBefore.Seeks,
                "libmpv used the in-process stream callbacks");
            Console.WriteLine(
                $"CANX_DOWNLOAD_PLAYBACK_OK id={taskId:N} state={task.State} " +
                $"completed={task.CompletedLength}/{task.TotalLength} " +
                $"frames={playbackWindow.RenderedFrameCount} " +
                $"seekFrames={playbackWindow.RenderedFrameCount - framesBeforeSeek} " +
                $"seekMs={seekStopwatch.Elapsed.TotalMilliseconds:0} " +
                $"reads={callbacksAfter.Reads - callbacksBefore.Reads} " +
                $"seeks={callbacksAfter.Seeks - callbacksBefore.Seeks}");
            await viewModel.Playback.StopAsync(timeout.Token);
            if (arguments.Contains("--verify-completed", StringComparer.Ordinal))
            {
                do
                {
                    await Task.Delay(25, timeout.Token);
                    task = manager.Tasks.Single(item => item.Id == taskId);
                    EnsureSmoke(task.State != DownloadState.Error, "the fixture download failed before completion");
                }
                while (task.State != DownloadState.Complete);

                viewModel.DownloadCenter.ShowCompletedTasks = true;
                viewModel.DownloadCenter.SynchronizeTaskList();
                while (!viewModel.DownloadCenter.Tasks.Any(item =>
                           item.Id == taskId && item.Snapshot.State == DownloadState.Complete))
                {
                    await Task.Delay(25, timeout.Token);
                }

                viewModel.DownloadCenter.SelectedTask = viewModel.DownloadCenter.Tasks
                    .Single(item => item.Id == taskId);

                await window.PlaySelectedDownloadAsync();
                await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
                await playbackWindow.WaitForPlaybackFramesAsync(12, timeout.Token);
                Console.WriteLine($"CANX_COMPLETED_DOWNLOAD_PLAYBACK_OK frames={playbackWindow.RenderedFrameCount}");
                await viewModel.Playback.StopAsync(timeout.Token);
            }

            playbackWindow.Close();
            window.Close();
        }
        catch (Exception exception)
        {
            var callbacks = mpvEngine.StreamCallbackMetrics;
            Console.Error.WriteLine(
                $"CANX_DOWNLOAD_PLAYBACK_CALLBACKS opens={callbacks.Opens} reads={callbacks.Reads} " +
                $"seeks={callbacks.Seeks} sizes={callbacks.SizeQueries} " +
                $"cancels={callbacks.Cancellations} closes={callbacks.Closes} eof={callbacks.EndOfStreams}");
            Console.Error.WriteLine($"CANX_DOWNLOAD_PLAYBACK_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }

    private static async Task RunPlaybackStreamSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        ShellViewModel viewModel,
        NativeMpvEngine mpvEngine,
        MediaStreamBroker streamBroker,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 1)
            {
                throw new ArgumentException("--playback-stream-smoke requires a media file argument.");
            }

            var mediaPath = Path.GetFullPath(arguments[argumentIndex + 1]);
            if (!File.Exists(mediaPath))
            {
                throw new FileNotFoundException("Playback stream smoke media does not exist.", mediaPath);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            using var registration = streamBroker.Register(
                Path.GetFileName(mediaPath),
                () => new DotNetMediaByteStream(new FileStream(
                    mediaPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess)));
            var before = mpvEngine.StreamCallbackMetrics;
            await viewModel.InitializeAsync(timeout.Token);
            EnsureSmoke(viewModel.Playback.IsReady, viewModel.Playback.StatusMessage);
            var playbackWindow = window.ShowPlaybackWindow();
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await viewModel.Playback.OpenAsync(registration.Source, timeout.Token);
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(48, timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.DurationSeconds > 1,
                "stream duration",
                timeout.Token);

            var seekTarget = Math.Max(0.5, viewModel.Playback.DurationSeconds / 2);
            await viewModel.Playback.SeekAsync(seekTarget, cancellationToken: timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.PositionSeconds >= seekTarget - 0.5,
                "stream seek",
                timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(12, timeout.Token);
            await viewModel.Playback.StopAsync(timeout.Token);
            await WaitForStreamCloseAsync(mpvEngine, before.Closes, timeout.Token);

            var cancellationProbeCreated = new TaskCompletionSource<CancellationProbeMediaByteStream>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var cancellationRegistration = streamBroker.Register(
                       "cancellation-probe.bin",
                       () =>
                       {
                           var probe = new CancellationProbeMediaByteStream();
                           cancellationProbeCreated.TrySetResult(probe);
                           return probe;
                       }))
            {
                await viewModel.Playback.OpenAsync(cancellationRegistration.Source, timeout.Token);
                var cancellationProbe = await cancellationProbeCreated.Task.WaitAsync(timeout.Token);
                await cancellationProbe.ReadStarted.WaitAsync(timeout.Token);
                await viewModel.Playback.StopAsync(timeout.Token);
                await WaitForStreamCloseAsync(mpvEngine, before.Closes + 1, timeout.Token);
            }

            using (var eofRegistration = streamBroker.Register(
                       "eof-probe.mkv",
                       () => new SequentialPlaybackStream(new FileStream(
                           mediaPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           bufferSize: 64 * 1024,
                           FileOptions.Asynchronous | FileOptions.SequentialScan))))
            {
                await viewModel.Playback.OpenAsync(eofRegistration.Source, timeout.Token);
                await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
                while (mpvEngine.StreamCallbackMetrics.EndOfStreams <= before.EndOfStreams)
                {
                    await Task.Delay(25, timeout.Token);
                }

                await viewModel.Playback.StopAsync(timeout.Token);
                await WaitForStreamCloseAsync(mpvEngine, before.Closes + 2, timeout.Token);
            }

            var after = mpvEngine.StreamCallbackMetrics;
            var opens = after.Opens - before.Opens;
            var reads = after.Reads - before.Reads;
            var seeks = after.Seeks - before.Seeks;
            var sizes = after.SizeQueries - before.SizeQueries;
            var cancellations = after.Cancellations - before.Cancellations;
            var closes = after.Closes - before.Closes;
            var endOfStreams = after.EndOfStreams - before.EndOfStreams;
            EnsureSmoke(opens >= 1, "mpv did not open the canx stream");
            EnsureSmoke(reads >= 1, "mpv did not read the canx stream");
            EnsureSmoke(seeks >= 2, $"mpv stream seek callbacks were incomplete: {seeks}");
            EnsureSmoke(sizes >= 1, "mpv did not query the canx stream size");
            EnsureSmoke(cancellations >= 1, "mpv did not cancel the canx stream during stop");
            EnsureSmoke(closes == opens, $"mpv stream close mismatch: opens={opens}, closes={closes}");
            EnsureSmoke(endOfStreams >= 1, "mpv did not observe stream EOF");
            Console.WriteLine(
                $"CANX_PLAYBACK_STREAM_OK frames={playbackWindow.RenderedFrameCount} " +
                $"opens={opens} reads={reads} seeks={seeks} sizes={sizes} " +
                $"cancels={cancellations} closes={closes} eof={endOfStreams}");
            playbackWindow.Close();
            window.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_PLAYBACK_STREAM_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }

    private static async Task WaitForStreamCloseAsync(
        NativeMpvEngine mpvEngine,
        long previousCloseCount,
        CancellationToken cancellationToken)
    {
        while (mpvEngine.StreamCallbackMetrics.Closes <= previousCloseCount)
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed class CancellationProbeMediaByteStream : IMediaByteStream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public bool CanSeek => true;

        public long? Length => null;

        public int Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        public long Seek(long absoluteOffset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return absoluteOffset;
        }

        public void Dispose()
        {
        }
    }

    private sealed class SequentialPlaybackStream(Stream stream) : IMediaByteStream
    {
        private readonly DotNetMediaByteStream _inner = new(stream);

        public bool CanSeek => false;

        public long? Length => null;

        public int Read(Span<byte> destination, CancellationToken cancellationToken) =>
            _inner.Read(destination, cancellationToken);

        public long Seek(long absoluteOffset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose() => _inner.Dispose();
    }

#endif
    private void ApplyTheme(string theme) => RequestedThemeVariant = theme switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

#if CANX_SMOKE_TESTS
    private static async Task RunPlaybackStabilitySmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        ShellViewModel viewModel,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 1)
            {
                throw new ArgumentException("--playback-stability-smoke requires a media file argument.");
            }

            var durationSeconds = 120;
            if (arguments.Length > argumentIndex + 2 &&
                (!int.TryParse(arguments[argumentIndex + 2], out durationSeconds) ||
                 durationSeconds is < 15 or > 7200))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(arguments),
                    "Playback stability duration must be between 15 and 7200 seconds.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 90));
            await viewModel.InitializeAsync(timeout.Token);
            EnsureSmoke(viewModel.Playback.IsReady, viewModel.Playback.StatusMessage);
            var playbackWindow = window.ShowPlaybackWindow();
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            Console.WriteLine("CANX_PLAYBACK_STABILITY_STAGE surface-ready");
            await viewModel.Playback.OpenAsync(arguments[argumentIndex + 1], timeout.Token);
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.DurationSeconds > durationSeconds,
                "stability media duration",
                timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(12, timeout.Token);
            await playbackWindow.ExercisePresentationResizeAsync(timeout.Token);
            Console.WriteLine("CANX_PLAYBACK_STABILITY_STAGE playback-ready");

            var screensDetected = playbackWindow.Screens.All.Count;
            var screensVisited = await playbackWindow.VisitAvailableScreensAsync(timeout.Token);
            var initialFrames = playbackWindow.RenderedFrameCount;
            var initialPresentedFrames = playbackWindow.PresentedFrameCount;
            var initialManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
            var initialWorkingSetBytes = Environment.WorkingSet;
            var maximumManagedBytes = initialManagedBytes;
            var maximumWorkingSetBytes = initialWorkingSetBytes;
            var lastProgressFrames = initialFrames;
            var lastFrameProgress = TimeSpan.Zero;
            var seekCount = 0;
            var contextRecreationCount = 0;
            var presentationResumeCount = 0;
            var nextSeek = TimeSpan.FromSeconds(5);
            var contextInterval = TimeSpan.FromSeconds(Math.Max(8, durationSeconds / 3d));
            var nextContextRecreation = contextInterval;
            var presentationResumeAt = TimeSpan.FromSeconds(durationSeconds / 2d);
            var presentationResumed = false;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                await Task.Delay(250, timeout.Token);
                EnsureSmoke(
                    viewModel.Playback.State is PlaybackState.Playing or PlaybackState.Paused,
                    $"playback entered {viewModel.Playback.State}: {viewModel.Playback.StatusMessage}");

                var currentFrames = playbackWindow.RenderedFrameCount;
                if (currentFrames > lastProgressFrames)
                {
                    lastProgressFrames = currentFrames;
                    lastFrameProgress = stopwatch.Elapsed;
                }

                EnsureSmoke(
                    stopwatch.Elapsed - lastFrameProgress < TimeSpan.FromSeconds(6),
                    $"video rendering stopped at frame {currentFrames}; " +
                    $"presented={playbackWindow.PresentedFrameCount}, " +
                    $"position={viewModel.Playback.PositionSeconds:0.###}, " +
                    $"state={viewModel.Playback.State}");

                if (stopwatch.Elapsed >= nextSeek)
                {
                    var seekRange = Math.Max(2, viewModel.Playback.DurationSeconds - 5);
                    var target = 1 + seekCount * 11 % seekRange;
                    await viewModel.Playback.SeekAsync(target, cancellationToken: timeout.Token);
                    seekCount++;
                    nextSeek += TimeSpan.FromSeconds(10);
                }

                if (stopwatch.Elapsed >= nextContextRecreation)
                {
                    Console.WriteLine($"CANX_PLAYBACK_STABILITY_STAGE context-recreate-{contextRecreationCount + 1}");
                    await playbackWindow.RecreatePlaybackSurfaceAsync(timeout.Token);
                    await playbackWindow.WaitForPlaybackFramesAsync(3, timeout.Token);
                    contextRecreationCount++;
                    Console.WriteLine($"CANX_PLAYBACK_STABILITY_STAGE context-recovered-{contextRecreationCount}");
                    nextContextRecreation += contextInterval;
                }

                if (!presentationResumed && stopwatch.Elapsed >= presentationResumeAt)
                {
                    Console.WriteLine("CANX_PLAYBACK_STABILITY_STAGE presentation-suspend");
                    await playbackWindow.SuspendAndResumePresentationAsync(timeout.Token);
                    await playbackWindow.WaitForPlaybackFramesAsync(3, timeout.Token);
                    presentationResumed = true;
                    presentationResumeCount++;
                    Console.WriteLine("CANX_PLAYBACK_STABILITY_STAGE presentation-resumed");
                }

                maximumManagedBytes = Math.Max(maximumManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
                maximumWorkingSetBytes = Math.Max(maximumWorkingSetBytes, Environment.WorkingSet);
            }

            var managedGrowth = maximumManagedBytes - initialManagedBytes;
            var workingSetGrowth = maximumWorkingSetBytes - initialWorkingSetBytes;
            EnsureSmoke(managedGrowth < 128L * 1024 * 1024, $"managed memory grew by {managedGrowth} bytes");
            EnsureSmoke(workingSetGrowth < 768L * 1024 * 1024, $"working set grew by {workingSetGrowth} bytes");
            EnsureSmoke(contextRecreationCount > 0, "no render-context recreation was exercised");
            EnsureSmoke(presentationResumeCount == 1, "presentation suspend/resume was not exercised");

            var diagnostics = viewModel.Playback.Diagnostics;
            var presentation = playbackWindow.PresentationDiagnostics;
            Console.WriteLine(
                $"CANX_PLAYBACK_STABILITY_OK seconds={durationSeconds} " +
                $"frames={playbackWindow.RenderedFrameCount - initialFrames} seeks={seekCount} " +
                $"presented={playbackWindow.PresentedFrameCount - initialPresentedFrames} " +
                $"resizeCycles=1 " +
                $"contextRecreations={contextRecreationCount} presentationResumes={presentationResumeCount} " +
                $"contextGeneration={playbackWindow.RenderContextGeneration} " +
                $"screensDetected={screensDetected} screensVisited={screensVisited} " +
                $"managedGrowth={managedGrowth} workingSetGrowth={workingSetGrowth} " +
                $"containerFps={diagnostics.ContainerFps:0.###} " +
                $"estimatedFps={diagnostics.EstimatedVideoFps:0.###} displayFps={diagnostics.DisplayFps:0.###} " +
                $"decoderDrops={diagnostics.DecoderDroppedFrames} outputDrops={diagnostics.OutputDroppedFrames} " +
                $"mistimed={diagnostics.MistimedFrames} delayed={diagnostics.DelayedFrames} " +
                $"avsync={diagnostics.AvSyncSeconds:0.###} " +
                $"renderer={playbackWindow.RenderBackend} " +
                $"presentationBackend={presentation.Backend} sync={presentation.SynchronizationMode} " +
                $"cpuReadback={presentation.UsesCpuReadback.ToString().ToLowerInvariant()} " +
                $"copiesPerFrame={presentation.CanXGpuCopiesPerFrame} " +
                $"compositorCopiesPerFrame={presentation.CompositorGpuCopiesPerFrame} " +
                $"gpuCopies={presentation.CanXGpuCopies} " +
                $"blockingFinishes={presentation.BlockingFinishCalls} " +
                $"renderAverageMs={presentation.AverageRenderMilliseconds:0.###} " +
                $"renderP95Ms={presentation.P95RenderMilliseconds:0.###} " +
                $"submitAverageMs={presentation.AverageSubmitMilliseconds:0.###} " +
                $"submitP95Ms={presentation.P95SubmitMilliseconds:0.###}");
            playbackWindow.Close();
            window.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_PLAYBACK_STABILITY_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }

    private static async Task RunPlaybackSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        ShellViewModel viewModel,
        NativeMpvEngine mpvEngine,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 1)
            {
                throw new ArgumentException("--playback-smoke requires a media file argument.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            await viewModel.InitializeAsync(timeout.Token);
            EnsureSmoke(viewModel.Playback.IsReady, viewModel.Playback.StatusMessage);
            var capabilityCoverage = PlaybackCapabilityCoverage.Analyze(viewModel.Playback.Capabilities);
            EnsureSmoke(
                viewModel.Playback.Capabilities.SchemaVersion == PlaybackCapabilityManifest.CurrentSchemaVersion,
                $"unexpected playback capability schema {viewModel.Playback.Capabilities.SchemaVersion}");
            EnsureSmoke(
                capabilityCoverage.PropertyTotal >= 100 && capabilityCoverage.CommandTotal >= 50,
                $"incomplete mpv capability manifest: properties={capabilityCoverage.PropertyTotal}, " +
                $"commands={capabilityCoverage.CommandTotal}");
            EnsureSmoke(
                capabilityCoverage.Uncovered.Count == 0,
                $"mpv capability manifest has uncovered entries: {string.Join(',', capabilityCoverage.Uncovered)}");
            EnsureSmoke(
                viewModel.Playback.Capabilities.Properties.Any(static property =>
                    property.Name == "path" && !property.CanRead && !property.CanWrite) &&
                viewModel.Playback.Capabilities.Commands.Any(static command =>
                    command.Name == "quit" && !command.CanExecute) &&
                viewModel.Playback.Capabilities.Commands.Any(static command =>
                    command.Name == "show-text" && command.CanExecute),
                "mpv capability security classifications are incomplete");
            var playbackWindow = window.ShowPlaybackWindow();
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            var rendered = playbackWindow.WaitForPlaybackFramesAsync(48, timeout.Token);
            await viewModel.Playback.OpenAsync(arguments[argumentIndex + 1], timeout.Token);
            while (viewModel.Playback.State is PlaybackState.Idle or PlaybackState.Loading)
            {
                await Task.Delay(50, timeout.Token);
            }

            EnsureSmoke(viewModel.Playback.State == PlaybackState.Playing, viewModel.Playback.StatusMessage);
            await rendered;
            await viewModel.Playback.SendKeyDownAsync("SPACE", timeout.Token);
            await viewModel.Playback.SendKeyUpAsync("SPACE", timeout.Token);
            Console.WriteLine("CANX_PLAYBACK_SMOKE_STAGE input-pause");
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Paused, timeout.Token);
            await viewModel.Playback.SendKeyDownAsync("SPACE", timeout.Token);
            await viewModel.Playback.SendKeyUpAsync("SPACE", timeout.Token);
            Console.WriteLine("CANX_PLAYBACK_SMOKE_STAGE input-resume");
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await viewModel.Playback.SetBrightnessAsync(10, timeout.Token);
            await WaitForPlaybackBrightnessAsync(viewModel.Playback, 10, timeout.Token);
            await viewModel.Playback.SetBrightnessAsync(0, timeout.Token);
            await WaitForPlaybackBrightnessAsync(viewModel.Playback, 0, timeout.Token);
            var audioTracks = viewModel.Playback.Tracks
                .Where(static track => track.Type == PlaybackTrackType.Audio)
                .ToList();
            EnsureSmoke(audioTracks.Count >= 2, $"expected two audio tracks, actual {audioTracks.Count}");
            await viewModel.Playback.SelectTrackAsync(
                new PlaybackTrackChoiceViewModel(PlaybackTrackType.Audio, audioTracks[1].Id, string.Empty),
                timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.Tracks.Any(track =>
                    track.Type == PlaybackTrackType.Audio && track.Id == audioTracks[1].Id && track.IsSelected),
                "second audio track selection",
                timeout.Token);

            var externalSubtitleLoaded = false;
            if (arguments.Length > argumentIndex + 2)
            {
                await viewModel.Playback.AddSubtitleAsync(arguments[argumentIndex + 2], cancellationToken: timeout.Token);
                await WaitForPlaybackConditionAsync(
                    viewModel.Playback,
                    () => viewModel.Playback.Tracks.Any(static track =>
                        track.Type == PlaybackTrackType.Subtitle && track.IsExternal && track.IsSelected),
                    "external subtitle selection",
                    timeout.Token);
                await WaitForPlaybackConditionAsync(
                    viewModel.Playback,
                    () => mpvEngine.TryGetString("sub-text", out var text) &&
                        text.Contains("CanX external subtitle smoke test", StringComparison.Ordinal),
                    "external subtitle decoding",
                    timeout.Token);
                externalSubtitleLoaded = true;
            }

            await viewModel.Playback.SetSubtitleVisibleAsync(false, timeout.Token);
            await viewModel.Playback.SetSubtitleDelayAsync(-0.5, timeout.Token);
            await viewModel.Playback.SetSubtitleScaleAsync(1.25, timeout.Token);
            await viewModel.Playback.SetSubtitlePositionAsync(92, timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => !viewModel.Playback.IsSubtitleVisible &&
                    Math.Abs(viewModel.Playback.SubtitleDelaySeconds - -0.5) < 0.01 &&
                    Math.Abs(viewModel.Playback.SubtitleScale - 1.25) < 0.01 &&
                    Math.Abs(viewModel.Playback.SubtitlePosition - 92) < 0.01,
                "subtitle runtime controls",
                timeout.Token);
            await viewModel.Playback.SetSubtitleVisibleAsync(true, timeout.Token);
            var trackCountWithExternalSubtitle = viewModel.Playback.Tracks.Count;
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.ChapterItems.Count == 2,
                "chapter discovery",
                timeout.Token);
            await viewModel.Playback.OpenChapterAsync(viewModel.Playback.ChapterItems[1], timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.ChapterItems.Any(static chapter => chapter.Index == 1 && chapter.IsCurrent),
                "chapter navigation",
                timeout.Token);
            var chapterNavigationVerified = true;
            viewModel.Playback.BookmarkTitleText = "Smoke bookmark";
            await viewModel.Playback.AddBookmarkAsync(timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.BookmarkItems.Count == 1,
                "bookmark creation",
                timeout.Token);
            var bookmark = viewModel.Playback.BookmarkItems[0];
            viewModel.Playback.SelectedBookmarkItem = bookmark;
            viewModel.Playback.BookmarkTitleText = "Renamed smoke bookmark";
            await viewModel.Playback.RenameBookmarkAsync(timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.BookmarkItems[0].Title == "Renamed smoke bookmark",
                "bookmark rename",
                timeout.Token);
            await viewModel.Playback.OpenBookmarkAsync(viewModel.Playback.BookmarkItems[0], timeout.Token);
            var bookmarkVerified = true;
            await viewModel.Playback.StepFrameAsync(cancellationToken: timeout.Token);
            Console.WriteLine("CANX_PLAYBACK_SMOKE_STAGE frame-step-pause");
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Paused, timeout.Token);
            await viewModel.Playback.StepFrameAsync(backward: true, cancellationToken: timeout.Token);
            var frameStepVerified = true;
            await viewModel.Playback.SetAbLoopAsync(1, 3, timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => Math.Abs((viewModel.Playback.AbLoopASeconds ?? -1) - 1) < 0.01 &&
                    Math.Abs((viewModel.Playback.AbLoopBSeconds ?? -1) - 3) < 0.01,
                "A-B loop",
                timeout.Token);
            await viewModel.Playback.ClearAbLoopAsync(timeout.Token);
            var abLoopVerified = true;
            var screenshotPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(arguments[argumentIndex + 1]))!,
                "canx-playback-smoke.png");
            File.Delete(screenshotPath);
            await viewModel.Playback.TakeScreenshotAsync(
                PlaybackScreenshotMode.Subtitles,
                screenshotPath,
                timeout.Token);
            while (!File.Exists(screenshotPath) || new FileInfo(screenshotPath).Length == 0)
            {
                await Task.Delay(25, timeout.Token);
            }

            var screenshotVerified = true;
            await viewModel.Playback.ApplyVideoFiltersAsync(timeout.Token);
            viewModel.Playback.VideoFilterText = "lavfi=[hflip]";
            await viewModel.Playback.ApplyVideoFiltersAsync(timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => mpvEngine.TryGetString("vf", out var value) && !string.IsNullOrEmpty(value),
                "video filter",
                timeout.Token);
            viewModel.Playback.AudioFilterText = "lavfi=[volume=0.9]";
            await viewModel.Playback.ApplyAudioFiltersAsync(timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => mpvEngine.TryGetString("af", out var value) && !string.IsNullOrEmpty(value),
                "audio filter",
                timeout.Token);
            await viewModel.Playback.ClearVideoFiltersAsync(timeout.Token);
            await viewModel.Playback.ClearAudioFiltersAsync(timeout.Token);
            var filtersVerified = true;
            var shaderVerified = false;
            if (arguments.Length > argumentIndex + 4)
            {
                var shaderPath = Path.GetFullPath(arguments[argumentIndex + 4]);
                await viewModel.Playback.AddShaderAsync(shaderPath, timeout.Token);
                await WaitForPlaybackConditionAsync(
                    viewModel.Playback,
                    () => viewModel.Playback.ShaderPaths.Any(path =>
                        string.Equals(path, shaderPath, StringComparison.Ordinal)),
                    "user shader",
                    timeout.Token);
                await viewModel.Playback.ClearShadersAsync(timeout.Token);
                shaderVerified = true;
            }

            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => !string.IsNullOrWhiteSpace(viewModel.Playback.Diagnostics.VideoCodec) &&
                    !string.IsNullOrWhiteSpace(viewModel.Playback.Diagnostics.VideoFormat) &&
                    !string.IsNullOrWhiteSpace(viewModel.Playback.Diagnostics.HardwareDecoder),
                "decoder diagnostics",
                timeout.Token);
            var diagnosticsVerified = true;
            viewModel.Playback.SelectedToneMapping = viewModel.Playback.ToneMappingOptions
                .Single(static option => option.Value == "bt.2390");
            viewModel.Playback.TargetPeakText = "500";
            await viewModel.Playback.ApplyColorSettingsAsync(timeout.Token);
            await WaitForPlaybackConditionAsync(
                viewModel.Playback,
                () => viewModel.Playback.ColorSettings.ToneMapping == "bt.2390" &&
                    double.TryParse(
                        viewModel.Playback.ColorSettings.TargetPeak,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var targetPeak) &&
                    Math.Abs(targetPeak - 500) < 0.01,
                "runtime color settings",
                timeout.Token);
            var colorVerified = true;
            viewModel.Playback.RuntimePropertyName = "video-sync";
            viewModel.Playback.RuntimePropertyValue = "audio";
            await viewModel.Playback.WriteRuntimePropertyAsync(timeout.Token);
            await viewModel.Playback.ReadRuntimePropertyAsync(timeout.Token);
            EnsureSmoke(
                viewModel.Playback.RuntimePropertyValue == "audio",
                $"runtime property value was {viewModel.Playback.RuntimePropertyValue}");
            var runtimePropertyVerified = true;
            viewModel.Playback.RuntimeCommandName = "show-text";
            viewModel.Playback.RuntimeCommandArguments = "CanX runtime command smoke\n500";
            await viewModel.Playback.ExecuteRuntimeCommandAsync(timeout.Token);
            var runtimeCommandVerified = true;

            EnsureSmoke(
                viewModel.Playback.AudioDeviceChoices.Any(static device => device.Name == "auto"),
                "audio device discovery did not include auto");
            var audioDevicesVerified = true;
            await viewModel.Playback.TogglePauseAsync(timeout.Token);
            Console.WriteLine("CANX_PLAYBACK_SMOKE_STAGE controller-resume");
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await viewModel.Playback.SeekAsync(1, cancellationToken: timeout.Token);
            var previousRenderContextGeneration = playbackWindow.RenderContextGeneration;
            await playbackWindow.RecreatePlaybackSurfaceAsync(timeout.Token);
            _ = mpvEngine.TryGetString("vid", out var recoveredVideoTrack);
            _ = mpvEngine.TryGetString("current-vo", out var recoveredVideoOutput);
            Console.WriteLine(
                $"CANX_PLAYBACK_SMOKE_STAGE context-recreated generation={playbackWindow.RenderContextGeneration} " +
                $"vid={recoveredVideoTrack} vo={recoveredVideoOutput}");
            await playbackWindow.WaitForPlaybackFramesAsync(6, timeout.Token);
            EnsureSmoke(
                playbackWindow.RenderContextGeneration > previousRenderContextGeneration,
                "the libmpv render context was not recreated after the video surface was reattached");
            EnsureSmoke(
                viewModel.Playback.State == PlaybackState.Playing,
                $"playback did not survive render-context recreation: {viewModel.Playback.State}");
            var contextRecoveryVerified = true;
            var playlistNavigationVerified = false;
            if (arguments.Length > argumentIndex + 3)
            {
                var nextSource = Path.GetFullPath(arguments[argumentIndex + 3]);
                await viewModel.Playback.AddToPlaylistAsync([nextSource], cancellationToken: timeout.Token);
                await WaitForPlaybackConditionAsync(
                    viewModel.Playback,
                    () => viewModel.Playback.PlaylistItems.Count == 2,
                    "playlist append",
                    timeout.Token);
                await viewModel.Playback.PlayNextAsync(timeout.Token);
                await WaitForPlaybackConditionAsync(
                    viewModel.Playback,
                    () => viewModel.Playback.State == PlaybackState.Playing &&
                        string.Equals(viewModel.Playback.Source, nextSource, StringComparison.Ordinal),
                    "next playlist item",
                    timeout.Token);
                await playbackWindow.WaitForPlaybackFramesAsync(6, timeout.Token);
                playlistNavigationVerified = true;
            }

            var videoViewport = playbackWindow.VideoViewportSize;
            var playbackScreen = playbackWindow.Screens.ScreenFromWindow(playbackWindow) ?? playbackWindow.Screens.Primary;
            var expectedAspectRatio = viewModel.Playback.VideoWidth / (double)viewModel.Playback.VideoHeight;
            var actualAspectRatio = videoViewport.Width / videoViewport.Height;
            EnsureSmoke(
                viewModel.Playback.VideoWidth > 0 && viewModel.Playback.VideoHeight > 0 &&
                videoViewport.Width > 0 && videoViewport.Height > 0 &&
                Math.Abs(actualAspectRatio - expectedAspectRatio) < 0.02,
                $"video viewport {videoViewport.Width:0}x{videoViewport.Height:0} aspect ratio " +
                $"{actualAspectRatio:0.###} does not match {expectedAspectRatio:0.###}; " +
                $"window={playbackWindow.ClientSize.Width:0}x{playbackWindow.ClientSize.Height:0}, " +
                $"screen={playbackScreen?.WorkingArea.Width ?? 0}x{playbackScreen?.WorkingArea.Height ?? 0}" +
                $"@{playbackScreen?.Scaling ?? 0:0.##}");
            _ = mpvEngine.TryGetString("video-codec", out var videoCodec);
            _ = mpvEngine.TryGetString("video-format", out var videoFormat);
            _ = mpvEngine.TryGetString("hwdec-current", out var hardwareDecoder);
            _ = mpvEngine.TryGetString("hwdec-interop", out var hardwareInterop);
            var presentation = playbackWindow.PresentationDiagnostics;
            Console.WriteLine(
                $"CANX_APP_PLAYBACK_OK state={viewModel.Playback.State} " +
                $"duration={viewModel.Playback.DurationMaximum:0.###} " +
                $"frames={playbackWindow.RenderedFrameCount} api={NativeMpvEngine.ApiVersion} " +
                $"renderer={playbackWindow.RenderBackend} " +
                $"presentationBackend={presentation.Backend} sync={presentation.SynchronizationMode} " +
                $"cpuReadback={presentation.UsesCpuReadback.ToString().ToLowerInvariant()} " +
                $"copiesPerFrame={presentation.CanXGpuCopiesPerFrame} " +
                $"compositorCopiesPerFrame={presentation.CompositorGpuCopiesPerFrame} " +
                $"gpuCopies={presentation.CanXGpuCopies} " +
                $"blockingFinishes={presentation.BlockingFinishCalls} " +
                $"renderAverageMs={presentation.AverageRenderMilliseconds:0.###} " +
                $"renderP95Ms={presentation.P95RenderMilliseconds:0.###} " +
                $"submitAverageMs={presentation.AverageSubmitMilliseconds:0.###} " +
                $"submitP95Ms={presentation.P95SubmitMilliseconds:0.###} " +
                $"window={playbackWindow.ClientSize.Width:0}x{playbackWindow.ClientSize.Height:0} " +
                $"screen={playbackScreen?.WorkingArea.Width ?? 0}x{playbackScreen?.WorkingArea.Height ?? 0}@{playbackScreen?.Scaling ?? 0:0.##} " +
                $"viewport={videoViewport.Width:0}x{videoViewport.Height:0} " +
                $"tracks={trackCountWithExternalSubtitle} audioTracks={audioTracks.Count} " +
                $"externalSubtitle={externalSubtitleLoaded.ToString().ToLowerInvariant()} " +
                $"playlist={viewModel.Playback.PlaylistItems.Count} " +
                $"playlistNavigation={playlistNavigationVerified.ToString().ToLowerInvariant()} " +
                $"recent={viewModel.Playback.RecentItems.Count} " +
                $"chapters={viewModel.Playback.ChapterItems.Count} " +
                $"chapterNavigation={chapterNavigationVerified.ToString().ToLowerInvariant()} " +
                $"bookmark={bookmarkVerified.ToString().ToLowerInvariant()} " +
                $"frameStep={frameStepVerified.ToString().ToLowerInvariant()} " +
                $"abLoop={abLoopVerified.ToString().ToLowerInvariant()} " +
                $"screenshot={screenshotVerified.ToString().ToLowerInvariant()} " +
                $"audioDevices={audioDevicesVerified.ToString().ToLowerInvariant()} " +
                $"filters={filtersVerified.ToString().ToLowerInvariant()} " +
                $"shader={shaderVerified.ToString().ToLowerInvariant()} " +
                $"diagnostics={diagnosticsVerified.ToString().ToLowerInvariant()} " +
                $"color={colorVerified.ToString().ToLowerInvariant()} " +
                $"runtimeProperty={runtimePropertyVerified.ToString().ToLowerInvariant()} " +
                $"runtimeCommand={runtimeCommandVerified.ToString().ToLowerInvariant()} " +
                $"contextRecovery={contextRecoveryVerified.ToString().ToLowerInvariant()} " +
                $"contextGeneration={playbackWindow.RenderContextGeneration} " +
                $"capabilities=true propertyCapabilities={capabilityCoverage.PropertyTotal} " +
                $"propertyDedicated={capabilityCoverage.PropertyDedicated} " +
                $"propertyAdvanced={capabilityCoverage.PropertyAdvanced} " +
                $"propertyBlocked={capabilityCoverage.PropertyBlocked} " +
                $"commandCapabilities={capabilityCoverage.CommandTotal} " +
                $"commandDedicated={capabilityCoverage.CommandDedicated} " +
                $"commandAdvanced={capabilityCoverage.CommandAdvanced} " +
                $"commandBlocked={capabilityCoverage.CommandBlocked} " +
                $"capabilityUncovered={capabilityCoverage.Uncovered.Count} " +
                $"gpuApi={viewModel.Playback.Diagnostics.GpuApi} " +
                $"frameTransfer={viewModel.Playback.Diagnostics.FrameTransferPath} " +
                $"codec={videoCodec} format={videoFormat} " +
                $"hwdec={hardwareDecoder} interop={hardwareInterop}");
            playbackWindow.Close();
            window.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_APP_PLAYBACK_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }

    private static async Task WaitForPlaybackStateAsync(
        PlaybackViewModel playback,
        PlaybackState expected,
        CancellationToken cancellationToken)
    {
        while (playback.State != expected)
        {
            EnsureSmoke(
                playback.State is not PlaybackState.Error and not PlaybackState.Ended,
                $"expected playback state {expected}, actual state {playback.State}: {playback.StatusMessage}");
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForPlaybackBrightnessAsync(
        PlaybackViewModel playback,
        double expected,
        CancellationToken cancellationToken)
    {
        while (Math.Abs(playback.Brightness - expected) >= 0.01)
        {
            EnsureSmoke(
                playback.State != PlaybackState.Error,
                $"expected brightness {expected:0.###}, actual {playback.Brightness:0.###}: " +
                playback.StatusMessage);
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForPlaybackConditionAsync(
        PlaybackViewModel playback,
        Func<bool> condition,
        string description,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            EnsureSmoke(
                playback.State is not PlaybackState.Error and not PlaybackState.Ended,
                $"waiting for {description}: {playback.StatusMessage}");
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task RunDownloadSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        DownloadManager manager,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 2)
            {
                throw new ArgumentException("--download-smoke requires URI and destination directory arguments.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await manager.StartAsync(timeout.Token);
            var coverage = DownloadOptionCoverage.Analyze(manager.Capabilities.Options);
            Console.WriteLine(
                $"CANX_APP_CAPABILITIES abi={manager.Capabilities.AbiVersion} " +
                $"extended={manager.Capabilities.ExtendedApi.ToString().ToLowerInvariant()} " +
                $"features={manager.Capabilities.Features.Count} options={manager.Capabilities.Options.Count} " +
                $"uncovered={coverage.Uncovered.Count}");
            Console.WriteLine(
                $"CANX_APP_OPTION_SCOPE total={coverage.Total} initial={coverage.Initial} " +
                $"active={coverage.Active} reserved={coverage.Reserved} global={coverage.Global} " +
                $"hidden={coverage.Hidden} readOnly={coverage.ReadOnly} " +
                $"uncovered={coverage.Uncovered.Count}");
            EnsureSmoke(coverage.Uncovered.Count == 0, "the aria2 option manifest has no uncovered entries");
            var tasks = await manager.AddAsync(new AddDownloadRequest
            {
                Source = arguments[argumentIndex + 1],
                DestinationDirectory = arguments[argumentIndex + 2],
                MirrorUris = arguments.Skip(argumentIndex + 3).ToList(),
            }, timeout.Token);
            var taskId = tasks.Single().Id;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var task = manager.Tasks.Single(item => item.Id == taskId);
                if (task.State == DownloadState.Complete)
                {
                    Console.WriteLine(
                        $"CANX_APP_DOWNLOAD_OK id={task.Id:N} bytes={task.CompletedLength} file={task.Files[0].Path}");
                    desktop.Shutdown(0);
                    return;
                }

                if (task.State == DownloadState.Error)
                {
                    throw new InvalidOperationException($"aria2 download failed with error code {task.ErrorCode}.");
                }

                await Task.Delay(50, timeout.Token);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_APP_DOWNLOAD_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }

    private static async Task RunDownloadFixtureSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        DownloadManager manager,
        string[] arguments,
        int argumentIndex)
    {
        try
        {
            var scenario = DownloadFixtureScenario.Parse(arguments, argumentIndex);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(scenario.TimeoutSeconds));
            await manager.StartAsync(timeout.Token);
            EnsureSmoke(manager.Capabilities.ExtendedApi, "the extended aria2 API is enabled");

            IReadOnlyList<DownloadTaskSnapshot> tasks;
            if (scenario.CommitBeforeMetadata)
            {
                var preparationId = Guid.NewGuid();
                var pendingPreparation = manager.PrepareAsync(
                    scenario.Source,
                    timeout.Token,
                    preparationId).AsTask();
                tasks = await manager.CommitPreparationAsync(new CommitDownloadPreparationRequest
                {
                    PreparationId = preparationId,
                    DestinationDirectory = scenario.DestinationDirectory,
                    OutputName = scenario.OutputName,
                    Options = scenario.Options,
                }, timeout.Token);
                try
                {
                    await pendingPreparation;
                    throw new InvalidOperationException("Pending metadata preparation unexpectedly completed before commit.");
                }
                catch (OperationCanceledException)
                {
                }
            }
            else if (scenario.UsePreparation)
            {
                var preparation = await manager.PrepareAsync(scenario.Source, timeout.Token);
                EnsureSmoke(
                    !Directory.Exists(scenario.DestinationDirectory) ||
                    !Directory.EnumerateFileSystemEntries(scenario.DestinationDirectory).Any(),
                    "preparation did not create download files");
                var previewDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "CanX",
                    "download-preview",
                    preparation.Id.ToString("N"));
                EnsureSmoke(
                    !Directory.Exists(previewDirectory) ||
                    !Directory.EnumerateFileSystemEntries(previewDirectory).Any(),
                    "preparation kept all metadata in memory");
                EnsureSmoke(scenario.ExpectedName is null || preparation.DisplayName == scenario.ExpectedName,
                    $"expected prepared name '{scenario.ExpectedName}', got '{preparation.DisplayName}'");
                EnsureSmoke(scenario.ExpectedTotalLength is null || preparation.TotalLength == scenario.ExpectedTotalLength,
                    $"expected prepared total length {scenario.ExpectedTotalLength}, got {preparation.TotalLength}");
                var selectedIndexes = scenario.ExpectedSelectedFiles.Length == 0
                    ? preparation.Files.Select(static file => file.Index).ToHashSet()
                    : scenario.ExpectedSelectedFiles.ToHashSet();
                tasks = await manager.CommitPreparationAsync(new CommitDownloadPreparationRequest
                {
                    PreparationId = preparation.Id,
                    DestinationDirectory = scenario.DestinationDirectory,
                    OutputName = scenario.OutputName,
                    Options = scenario.Options,
                    Files = preparation.Files.Select(file => new DownloadFilePreference
                    {
                        Index = file.Index,
                        IsSelected = selectedIndexes.Contains(file.Index),
                        Priority = scenario.HighPriorityFiles.Contains(file.Index)
                            ? DownloadFilePriority.High
                            : scenario.LowPriorityFiles.Contains(file.Index)
                                ? DownloadFilePriority.Low
                                : DownloadFilePriority.Normal,
                    }).ToList(),
                }, timeout.Token);
            }
            else
            {
                tasks = await manager.AddAsync(new AddDownloadRequest
                {
                    Kind = scenario.Kind,
                    Source = scenario.Source,
                    DestinationDirectory = scenario.DestinationDirectory,
                    OutputName = scenario.OutputName,
                    Options = scenario.Options,
                }, timeout.Token);
            }
            EnsureSmoke(tasks.Count == scenario.ExpectedTaskCount, $"expected {scenario.ExpectedTaskCount} tasks, got {tasks.Count}");

            var taskIds = tasks.Select(static task => task.Id).ToHashSet();
            var maxPeerCount = 0;
            var maxServerCount = 0;
            var trackerObserved = false;
            var retried = false;
            var lastReport = DateTimeOffset.MinValue;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                tasks = manager.Tasks.Where(task => taskIds.Contains(task.Id)).ToArray();
                trackerObserved |= tasks.Any(static task =>
                    task.Torrent?.AnnounceList.SelectMany(static tier => tier).Any() == true);
                foreach (var task in tasks.Where(static task => task.State == DownloadState.Active))
                {
                    var live = await manager.GetLiveDetailsAsync(task.Id, timeout.Token);
                    maxPeerCount = Math.Max(maxPeerCount, live.Peers.Count);
                    maxServerCount = Math.Max(maxServerCount, live.Servers.Count);
                }

                var finished = scenario.ExpectAnyError
                    ? tasks.Count == scenario.ExpectedTaskCount && tasks.All(static task =>
                        task.State == DownloadState.Error && task.ErrorCode != 0)
                    : scenario.ExpectedErrorCode is { } expectedErrorCode
                    ? tasks.Count == scenario.ExpectedTaskCount && tasks.All(task =>
                        task.State == DownloadState.Error && task.ErrorCode == expectedErrorCode)
                    : scenario.ExpectSeeder
                        ? tasks.Count == scenario.ExpectedTaskCount && tasks.All(static task =>
                            task.State == DownloadState.Complete && task.IsSeeder)
                        : tasks.Count == scenario.ExpectedTaskCount && tasks.All(static task => task.State == DownloadState.Complete);
                if (finished)
                {
                    break;
                }

                var failed = tasks.FirstOrDefault(static task => task.State == DownloadState.Error);
                if (failed is not null && scenario.RetryOnError && !retried)
                {
                    await manager.ResumeAsync(failed.Id, timeout.Token);
                    retried = true;
                    continue;
                }

                if (failed is not null && scenario.ExpectedErrorCode is null)
                {
                    throw new InvalidOperationException($"aria2 fixture download failed with error code {failed.ErrorCode}.");
                }

                if (DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(
                        $"CANX_APP_FIXTURE_PROGRESS tasks={tasks.Count} " +
                        $"states={string.Join(',', tasks.Select(static task => task.State))} " +
                        $"completed={tasks.Sum(static task => task.CompletedLength)} " +
                        $"peers={maxPeerCount} servers={maxServerCount} " +
                        $"errors={string.Join(',', tasks.Select(static task => task.ErrorCode))}");
                    lastReport = DateTimeOffset.UtcNow;
                }

                await Task.Delay(50, timeout.Token);
            }

            foreach (var task in tasks)
            {
                EnsureSmoke(task.Files.Count == scenario.ExpectedFileCount, $"expected {scenario.ExpectedFileCount} files, got {task.Files.Count}");
                EnsureSmoke(scenario.ExpectedName is null || task.DisplayName == scenario.ExpectedName,
                    $"expected name '{scenario.ExpectedName}', got '{task.DisplayName}'");
                EnsureSmoke(scenario.ExpectedTotalLength is null || task.TotalLength == scenario.ExpectedTotalLength,
                    $"expected total length {scenario.ExpectedTotalLength}, got {task.TotalLength}");
                if (scenario.ExpectedSelectedFiles.Length > 0)
                {
                    var selected = task.Files.Where(static file => file.IsSelected).Select(static file => file.Index).ToArray();
                    EnsureSmoke(selected.SequenceEqual(scenario.ExpectedSelectedFiles),
                        $"selected files are {string.Join(',', selected)}");
                }

            }

            EnsureSmoke(!scenario.ExpectTracker || trackerObserved, "torrent tracker metadata was observed");
            EnsureSmoke(!scenario.ExpectPeer || maxPeerCount > 0, "at least one BitTorrent peer was observed");
            EnsureSmoke(!scenario.ExpectServer || maxServerCount > 0, "at least one HTTP/FTP server was observed");
            EnsureSmoke(!scenario.RetryOnError || retried, "the failed task was retried");
            Console.WriteLine(
                $"CANX_APP_FIXTURE_OK kind={scenario.Kind.ToString().ToLowerInvariant()} " +
                $"tasks={tasks.Count} files={tasks.Sum(static task => task.Files.Count)} " +
                $"peers={maxPeerCount} servers={maxServerCount} " +
                $"seeder={tasks.Any(static task => task.IsSeeder).ToString().ToLowerInvariant()} " +
                $"retried={retried.ToString().ToLowerInvariant()} error={tasks.Max(static task => task.ErrorCode)}");
            foreach (var task in tasks)
            {
                await manager.RemoveAsync(task.Id, force: true, cancellationToken: timeout.Token);
            }

            desktop.Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_APP_FIXTURE_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }

    private static void EnsureSmoke(bool condition, string operation)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Fixture verification failed: {operation}.");
        }
    }

    private sealed record DownloadFixtureScenario(
        DownloadSourceKind Kind,
        string Source,
        string DestinationDirectory,
        string? OutputName,
        Dictionary<string, string> Options,
        int ExpectedTaskCount,
        int ExpectedFileCount,
        string? ExpectedName,
        long? ExpectedTotalLength,
        int[] ExpectedSelectedFiles,
        int[] HighPriorityFiles,
        int[] LowPriorityFiles,
        bool ExpectTracker,
        bool ExpectPeer,
        bool ExpectServer,
        bool ExpectSeeder,
        int? ExpectedErrorCode,
        bool ExpectAnyError,
        bool RetryOnError,
        bool UsePreparation,
        bool CommitBeforeMetadata,
        int TimeoutSeconds)
    {
        public static DownloadFixtureScenario Parse(string[] arguments, int argumentIndex)
        {
            if (arguments.Length <= argumentIndex + 3)
            {
                throw new ArgumentException(
                    "--download-fixture-smoke requires kind, source and destination directory arguments.");
            }

            var kind = arguments[argumentIndex + 1].ToLowerInvariant() switch
            {
                "uri" => DownloadSourceKind.Uri,
                "torrent" => DownloadSourceKind.Torrent,
                "metalink" => DownloadSourceKind.Metalink,
                var value => throw new ArgumentException($"Unknown download fixture kind '{value}'."),
            };
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var expectedTaskCount = 1;
            var expectedFileCount = 1;
            string? expectedName = null;
            long? expectedTotalLength = null;
            var expectedSelectedFiles = Array.Empty<int>();
            var highPriorityFiles = Array.Empty<int>();
            var lowPriorityFiles = Array.Empty<int>();
            var expectTracker = false;
            var expectPeer = false;
            var expectServer = false;
            var expectSeeder = false;
            int? expectedErrorCode = null;
            var expectAnyError = false;
            var retryOnError = false;
            var usePreparation = false;
            var commitBeforeMetadata = false;
            var timeoutSeconds = 60;
            string? outputName = null;

            foreach (var argument in arguments.Skip(argumentIndex + 4))
            {
                if (argument.StartsWith("--option=", StringComparison.Ordinal))
                {
                    var pair = argument[9..];
                    var separator = pair.IndexOf('=');
                    if (separator <= 0)
                    {
                        throw new ArgumentException($"Invalid fixture option '{argument}'.");
                    }

                    options[pair[..separator]] = pair[(separator + 1)..];
                }
                else if (argument.StartsWith("--output-name=", StringComparison.Ordinal))
                {
                    outputName = argument["--output-name=".Length..];
                }
                else if (argument.StartsWith("--expect-tasks=", StringComparison.Ordinal))
                {
                    expectedTaskCount = ParsePositiveInt(argument, "--expect-tasks=");
                }
                else if (argument.StartsWith("--expect-files=", StringComparison.Ordinal))
                {
                    expectedFileCount = ParsePositiveInt(argument, "--expect-files=");
                }
                else if (argument.StartsWith("--expect-name=", StringComparison.Ordinal))
                {
                    expectedName = argument["--expect-name=".Length..];
                }
                else if (argument.StartsWith("--expect-total-length=", StringComparison.Ordinal))
                {
                    expectedTotalLength = ParsePositiveLong(argument, "--expect-total-length=");
                }
                else if (argument.StartsWith("--expect-selected=", StringComparison.Ordinal))
                {
                    expectedSelectedFiles = argument[18..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                        .ToArray();
                }
                else if (argument.StartsWith("--high-priority=", StringComparison.Ordinal))
                {
                    highPriorityFiles = ParseIndexes(argument, "--high-priority=");
                }
                else if (argument.StartsWith("--low-priority=", StringComparison.Ordinal))
                {
                    lowPriorityFiles = ParseIndexes(argument, "--low-priority=");
                }
                else if (argument.StartsWith("--timeout-seconds=", StringComparison.Ordinal))
                {
                    timeoutSeconds = ParsePositiveInt(argument, "--timeout-seconds=");
                }
                else if (argument.StartsWith("--expect-error=", StringComparison.Ordinal))
                {
                    expectedErrorCode = ParsePositiveInt(argument, "--expect-error=");
                }
                else
                {
                    switch (argument)
                    {
                        case "--expect-tracker": expectTracker = true; break;
                        case "--expect-peer": expectPeer = true; break;
                        case "--expect-server": expectServer = true; break;
                        case "--expect-seeder": expectSeeder = true; break;
                        case "--expect-any-error": expectAnyError = true; break;
                        case "--retry-on-error": retryOnError = true; break;
                        case "--prepare": usePreparation = true; break;
                        case "--commit-before-metadata": commitBeforeMetadata = true; break;
                        default: throw new ArgumentException($"Unknown fixture argument '{argument}'.");
                    }
                }
            }

            return new DownloadFixtureScenario(
                kind,
                arguments[argumentIndex + 2],
                arguments[argumentIndex + 3],
                outputName,
                options,
                expectedTaskCount,
                expectedFileCount,
                expectedName,
                expectedTotalLength,
                expectedSelectedFiles,
                highPriorityFiles,
                lowPriorityFiles,
                expectTracker,
                expectPeer,
                expectServer,
                expectSeeder,
                expectedErrorCode,
                expectAnyError,
                retryOnError,
                usePreparation,
                commitBeforeMetadata,
                timeoutSeconds);
        }

        private static int ParsePositiveInt(string argument, string prefix)
        {
            if (!int.TryParse(argument[prefix.Length..], out var value) || value <= 0)
            {
                throw new ArgumentException($"'{argument}' must contain a positive integer.");
            }

            return value;
        }

        private static long ParsePositiveLong(string argument, string prefix)
        {
            if (!long.TryParse(argument[prefix.Length..], out var value) || value <= 0)
            {
                throw new ArgumentException($"'{argument}' must contain a positive integer.");
            }

            return value;
        }

        private static int[] ParseIndexes(string argument, string prefix) =>
            argument[prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
    }

#endif
    internal static string GetApplicationDataDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CANX_DATA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "CanX");
    }
}
