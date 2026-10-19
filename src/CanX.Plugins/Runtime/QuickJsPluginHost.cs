// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CanX.Core.Diagnostics;
using CanX.Core.Security;
using CanX.Plugins.Manifest;
using CanX.Plugins.Media;
using CanX.Plugins.Downloads;
using CanX.Plugins.Http;
using CanX.Plugins.Html;
using CanX.Plugins.Packaging;
using CanX.Plugins.Services;
using CanX.Plugins.Security;
using CanX.Plugins.Secrets;
using CanX.Plugins.Sdk;
using CanX.Plugins.Storage;
using CanX.Plugins.Ui;
using CanX.Streaming;

namespace CanX.Plugins.Runtime;

public sealed class QuickJsPluginHost : IPluginRuntimeHost
{
    private readonly PluginInstallation installation;
    private readonly IPluginToolDispatcher toolDispatcher;
    private readonly PluginPermissionSet permissions;
    private readonly PluginRuntimeOptions options;
    private readonly IApplicationLog log;
    private readonly IQuickJsRuntimeFactory runtimeFactory;
    private readonly PluginStorageStore storage;
    private readonly PluginSecretStore secrets;
    private readonly PluginHttpClient http;
    private readonly PluginServiceRegistry serviceRegistry;
    private readonly PluginUiRegistry uiRegistry;
    private readonly PluginDownloadPromptBroker downloadPromptBroker;
    private readonly PluginMediaPlaybackBroker mediaPlaybackBroker;
    private readonly PluginMediaStreamManager? mediaStreams;
    private readonly ConcurrentDictionary<ulong, CallbackInvocation> callbackInvocations = new();
    private readonly List<IDisposable> serviceRegistrations = [];
    private readonly List<IDisposable> uiRegistrations = [];
    private readonly Dictionary<string, PluginSubscription> subscriptions = new(StringComparer.Ordinal);
    private readonly Queue<long> consoleLogTimestamps = new();
    private readonly BlockingCollection<Action> runtimeCommands = new();
    private readonly Channel<NativeToolRequest> toolRequests;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Thread runtimeThread;
    private readonly Task toolProcessor;
    private readonly object stateLock = new();
    private IQuickJsRuntime? runtime;
    private PluginHostState state = PluginHostState.Created;
    private bool disposed;
    private long nextCallbackInvocation;

    public QuickJsPluginHost(
        PluginInstallation installation,
        IPluginToolDispatcher toolDispatcher,
        PluginRuntimeOptions? options = null,
        IApplicationLog? log = null,
        string? pluginDataRoot = null,
        ICredentialStore? credentialStore = null,
        HttpMessageHandler? httpHandler = null,
        PluginUiRegistry? uiRegistry = null,
        PluginDownloadPromptBroker? downloadPromptBroker = null,
        PluginServiceRegistry? serviceRegistry = null,
        MediaStreamBroker? mediaStreamBroker = null,
        PluginMediaPlaybackBroker? mediaPlaybackBroker = null,
        Func<HttpMessageHandler>? mediaHttpHandlerFactory = null)
        : this(
            installation,
            toolDispatcher,
            options,
            log,
            new NativeQuickJsRuntimeFactory(),
            pluginDataRoot,
            credentialStore,
            httpHandler,
            uiRegistry,
            downloadPromptBroker,
            serviceRegistry,
            mediaStreamBroker,
            mediaPlaybackBroker,
            mediaHttpHandlerFactory)
    {
    }

    internal QuickJsPluginHost(
        PluginInstallation installation,
        IPluginToolDispatcher toolDispatcher,
        PluginRuntimeOptions? options,
        IApplicationLog? log,
        IQuickJsRuntimeFactory runtimeFactory,
        string? pluginDataRoot = null,
        ICredentialStore? credentialStore = null,
        HttpMessageHandler? httpHandler = null,
        PluginUiRegistry? uiRegistry = null,
        PluginDownloadPromptBroker? downloadPromptBroker = null,
        PluginServiceRegistry? serviceRegistry = null,
        MediaStreamBroker? mediaStreamBroker = null,
        PluginMediaPlaybackBroker? mediaPlaybackBroker = null,
        Func<HttpMessageHandler>? mediaHttpHandlerFactory = null)
    {
        this.installation = installation ?? throw new ArgumentNullException(nameof(installation));
        this.toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        this.options = options ?? new PluginRuntimeOptions();
        this.options.Validate();
        this.log = log ?? NullApplicationLog.Instance;
        this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        permissions = PluginPermissionSet.FromManifest(installation.Manifest);
        var packageRoot = Path.GetDirectoryName(Path.GetFullPath(installation.DirectoryPath))
            ?? throw new ArgumentException("Plugin installation directory must have a parent.", nameof(installation));
        storage = new PluginStorageStore(
            pluginDataRoot ?? Path.Combine(packageRoot, ".data"),
            installation.Manifest.Id,
            this.options.Storage);
        secrets = new PluginSecretStore(
            installation.Manifest.Id,
            credentialStore,
            this.options.MaximumSecretBytes);
        http = new PluginHttpClient(
            permissions,
            this.options.Http,
            httpHandler,
            log: this.log,
            pluginId: installation.Manifest.Id);
        this.serviceRegistry = serviceRegistry ?? new PluginServiceRegistry();
        this.uiRegistry = uiRegistry ?? new PluginUiRegistry();
        this.downloadPromptBroker = downloadPromptBroker ?? new PluginDownloadPromptBroker();
        this.mediaPlaybackBroker = mediaPlaybackBroker ?? new PluginMediaPlaybackBroker();
        mediaStreams = mediaStreamBroker is null
            ? null
            : new PluginMediaStreamManager(
                installation.Manifest.Id,
                permissions,
                mediaStreamBroker,
                this.mediaPlaybackBroker,
                mediaHttpHandlerFactory);
        toolRequests = Channel.CreateBounded<NativeToolRequest>(new BoundedChannelOptions(this.options.MaximumPendingToolCalls)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        runtimeThread = new Thread(RuntimeLoop)
        {
            IsBackground = true,
            Name = $"CanX QuickJS {installation.Manifest.Id}",
        };
        runtimeThread.Start();
        toolProcessor = Task.Run(ProcessToolRequestsAsync);
    }

    public PluginManifest Manifest => installation.Manifest;

    public PluginHostState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (state != PluginHostState.Created)
            {
                throw new InvalidOperationException($"Plugin host cannot start from state {state}.");
            }

            state = PluginHostState.Starting;
        }

        try
        {
            DeclareManifestPage();
            DeclareManifestConfiguration();
            var modules = ReadModules();
            var entry = modules.Single(module =>
                string.Equals(module.Name, Manifest.Entry, StringComparison.Ordinal));
            await InvokeOnRuntimeThreadAsync(
                () =>
                {
                    runtime = runtimeFactory.Create(options, OnNativeToolRequest);
                    foreach (var module in modules)
                    {
                        runtime.AddModule(module.Name, module.Source);
                    }
                    runtime.EvaluateScript(
                        PluginSdkBootstrap.Source,
                        PluginSdkBootstrap.FileName,
                        options.ExecutionTimeout);
                    runtime.EvaluateModule(entry.Source, entry.Name, options.ExecutionTimeout);
                    PumpJobsOnce();
                },
                cancellationToken).ConfigureAwait(false);
            SetState(PluginHostState.Running);
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Information,
                $"Plugin started: {Manifest.Id} {Manifest.Version}");
        }
        catch (Exception exception)
        {
            SetState(PluginHostState.Faulted);
            WriteErrorSafely(
                LogComponent.QuickJs,
                $"Plugin failed to start: {Manifest.Id}",
                exception);
            RemoveServiceRegistrations();
            RemoveUiRegistrations();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        lifetime.Cancel();
        CancelSubscriptions();
        foreach (var invocation in callbackInvocations.Values)
        {
            invocation.Completion.TrySetCanceled(lifetime.Token);
        }
        RemoveServiceRegistrations();
        RemoveUiRegistrations();
        toolRequests.Writer.TryComplete();
        try
        {
            await toolProcessor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        http.Dispose();
        mediaStreams?.Dispose();

        await InvokeOnRuntimeThreadAsync(
            () =>
            {
                runtime?.Dispose();
                runtime = null;
            },
            CancellationToken.None).ConfigureAwait(false);
        runtimeCommands.CompleteAdding();
        await Task.Run(runtimeThread.Join).ConfigureAwait(false);
        SetState(PluginHostState.Stopped);
        lifetime.Dispose();
        runtimeCommands.Dispose();
        log.Write(
            LogComponent.QuickJs,
            ApplicationLogLevel.Information,
            $"Plugin stopped: {Manifest.Id}");
    }

    private IReadOnlyList<PluginModuleSource> ReadModules()
    {
        var root = Path.GetFullPath(installation.DirectoryPath);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PluginRuntimeException("Plugin installation directory cannot be a symbolic link or reparse point.");
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var modules = new List<PluginModuleSource>();
        var directories = new Stack<string>();
        directories.Push(root);
        long totalBytes = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(prefix, pathComparison))
                {
                    throw new PluginRuntimeException("Plugin package content escapes its installation directory.");
                }

                var attributes = File.GetAttributes(fullPath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new PluginRuntimeException("Plugin package cannot contain symbolic links or reparse points.");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    directories.Push(fullPath);
                    continue;
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".js", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetExtension(fullPath), ".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var file = new FileInfo(fullPath);
                if (file.Length > options.EntryModuleLimitBytes)
                {
                    throw new PluginRuntimeException(
                        $"Plugin module exceeds {options.EntryModuleLimitBytes} bytes: {file.Name}");
                }

                totalBytes += file.Length;
                if (totalBytes > options.TotalModuleLimitBytes)
                {
                    throw new PluginRuntimeException(
                        $"Plugin modules exceed {options.TotalModuleLimitBytes} bytes in total.");
                }

                if (modules.Count >= options.MaximumModules)
                {
                    throw new PluginRuntimeException(
                        $"Plugin contains more than {options.MaximumModules} JavaScript modules.");
                }

                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true);
                var moduleName = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
                var source = reader.ReadToEnd();
                if (source.Contains('\0'))
                {
                    throw new PluginRuntimeException($"Plugin module contains a null character: {moduleName}");
                }

                modules.Add(new PluginModuleSource(moduleName, source));
            }
        }

        if (!modules.Any(module => string.Equals(module.Name, Manifest.Entry, StringComparison.Ordinal)))
        {
            throw new PluginRuntimeException($"Plugin entry module '{Manifest.Entry}' was not found.");
        }

        return modules;
    }

    private void OnNativeToolRequest(ulong requestId, string toolName, string inputJson)
    {
        if (!toolRequests.Writer.TryWrite(new NativeToolRequest(requestId, toolName, inputJson)))
        {
            PostToolResolution(
                requestId,
                SerializeError("queue_full", "The plugin Tool queue is full."),
                isError: true);
        }
    }

    private async Task ProcessToolRequestsAsync()
    {
        var pendingHttpRequests = new List<Task>();
        try
        {
            await foreach (var request in toolRequests.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (request.ToolName == "canx:http/request")
                {
                    pendingHttpRequests.RemoveAll(static task => task.IsCompleted);
                    pendingHttpRequests.Add(ProcessToolRequestAsync(request));
                    continue;
                }

                await ProcessToolRequestAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(pendingHttpRequests).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ProcessToolRequestAsync(NativeToolRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName) || request.ToolName.Length > 256)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError("invalid_tool_name", "Tool name is empty or exceeds the configured limit."),
                isError: true);
            return;
        }

        var maximumInputBytes = request.ToolName.StartsWith("canx:ui/page/", StringComparison.Ordinal)
            ? options.MaximumUiPagePayloadBytes
            : request.ToolName == "canx:html/extract"
                ? options.MaximumHtmlPayloadBytes
                : options.MaximumToolPayloadBytes;
        if (Encoding.UTF8.GetByteCount(request.InputJson) > maximumInputBytes)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError("payload_too_large", "Tool input exceeds the configured limit."),
                isError: true);
            return;
        }

        try
        {
            if (request.ToolName == "canx:log/write")
            {
                ProcessConsoleLog(request);
                return;
            }

            if (request.ToolName.StartsWith("canx:storage/", StringComparison.Ordinal))
            {
                ProcessStorageRequest(request);
                return;
            }

            if (request.ToolName.StartsWith("canx:secrets/", StringComparison.Ordinal))
            {
                await ProcessSecretRequestAsync(request).ConfigureAwait(false);
                return;
            }

            if (request.ToolName == "canx:http/request")
            {
                var response = await http.SendAsync(request.InputJson, lifetime.Token).ConfigureAwait(false);
                PostToolResolution(request.RequestId, response, isError: false);
                return;
            }

            if (request.ToolName == "canx:http/set-default-proxy")
            {
                using var document = JsonDocument.Parse(request.InputJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("proxy", out var proxy) ||
                    proxy.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new PluginHttpException("invalid_request", "HTTP default proxy must be a string or null.");
                }
                http.SetDefaultProxy(proxy.ValueKind == JsonValueKind.String ? proxy.GetString() : null);
                PostToolResolution(request.RequestId, "null", isError: false);
                return;
            }

            if (request.ToolName == "canx:html/extract")
            {
                PostToolResolution(
                    request.RequestId,
                    PluginHtmlExtractor.Extract(request.InputJson),
                    isError: false);
                return;
            }

            if (request.ToolName == "canx:media/stream/create")
            {
                var manager = mediaStreams ?? throw new PluginMediaStreamException(
                    "stream_unavailable",
                    "The application does not provide managed plugin media streams.");
                PostToolResolution(request.RequestId, manager.Create(request.InputJson), isError: false);
                return;
            }

            if (request.ToolName == "canx:media/stream/release")
            {
                permissions.Demand(PluginCapabilities.MediaStream);
                using var document = JsonDocument.Parse(request.InputJson);
                var id = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()!
                    : throw new PluginMediaStreamException("invalid_media_stream", "Managed media release requires an id.");
                var released = mediaStreams?.Release(id) == true;
                PostToolResolution(request.RequestId, released ? "true" : "false", isError: false);
                return;
            }

            if (request.ToolName == "canx:media/stream/play")
            {
                var manager = mediaStreams ?? throw new PluginMediaStreamException(
                    "stream_unavailable",
                    "The application does not provide managed plugin media streams.");
                using var document = JsonDocument.Parse(request.InputJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("gestureToken", out var tokenElement) ||
                    !tokenElement.TryGetUInt64(out var gestureToken) ||
                    !callbackInvocations.TryGetValue(gestureToken, out var invocation) ||
                    !invocation.AllowsDownloadPrompt)
                {
                    throw new PluginMediaStreamException(
                        "user_gesture_required",
                        "Managed media playback must be requested directly from a plugin page user event.");
                }
                var playResult = await manager.RequestPlayAsync(request.InputJson, lifetime.Token).ConfigureAwait(false);
                PostToolResolution(request.RequestId, playResult, isError: false);
                return;
            }

            if (request.ToolName == "canx:callback/complete")
            {
                ProcessCallbackCompletion(request);
                return;
            }

            if (request.ToolName == "canx:services/register")
            {
                ProcessServiceRegistration(request);
                return;
            }

            if (request.ToolName == "canx:services/list")
            {
                ProcessServiceList(request);
                return;
            }

            if (request.ToolName == "canx:services/describe")
            {
                ProcessServiceDescription(request);
                return;
            }

            if (request.ToolName == "canx:services/invoke")
            {
                await ProcessServiceInvocationAsync(request).ConfigureAwait(false);
                return;
            }

            if (request.ToolName is "canx:playback/invoke" or "canx:downloads/invoke")
            {
                await ProcessSdkToolRequestAsync(request).ConfigureAwait(false);
                return;
            }

            if (request.ToolName == "canx:downloads/requestAdd")
            {
                await ProcessDownloadPromptAsync(request).ConfigureAwait(false);
                return;
            }

            if (request.ToolName == "canx:observe/subscribe")
            {
                ProcessObservationSubscription(request);
                return;
            }

            if (request.ToolName == "canx:observe/cancel")
            {
                ProcessObservationCancellation(request);
                return;
            }

            if (request.ToolName == "canx:ui/contribute")
            {
                ProcessUiContribution(request);
                return;
            }

            if (request.ToolName.StartsWith("canx:ui/page/", StringComparison.Ordinal))
            {
                ProcessUiPageRequest(request);
                return;
            }

            if (request.ToolName.StartsWith("canx:ui/configuration/", StringComparison.Ordinal))
            {
                ProcessUiConfigurationRequest(request);
                return;
            }

            permissions.Demand(PluginCapabilities.ToolsInvoke, request.ToolName);
            using (JsonDocument.Parse(request.InputJson))
            {
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(options.ToolTimeout);
            var result = await toolDispatcher.InvokeAsync(
                new PluginToolRequest(Manifest.Id, request.ToolName, request.InputJson),
                timeout.Token).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(result.Json) > options.MaximumToolPayloadBytes)
            {
                throw new PluginRuntimeException("Tool result exceeds the configured limit.");
            }

            using (JsonDocument.Parse(result.Json))
            {
            }

            PostToolResolution(request.RequestId, result.Json, isError: !result.IsSuccess);
        }
        catch (PluginPermissionDeniedException exception)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Warning,
                $"Plugin Tool permission denied: {Manifest.Id} -> {request.ToolName}");
            PostToolResolution(
                request.RequestId,
                SerializeError("permission_denied", exception.Message),
                isError: true);
        }
        catch (PluginStorageException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginSecretException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginHttpException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginHtmlException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginMediaStreamException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginServiceException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginSdkException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginUiException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (PluginDownloadPromptException exception)
        {
            PostToolResolution(
                request.RequestId,
                SerializeError(exception.Code, exception.Message),
                isError: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Warning,
                $"Plugin Tool timed out: {Manifest.Id} -> {request.ToolName}");
            PostToolResolution(
                request.RequestId,
                SerializeError("timeout", "The Tool call timed out."),
                isError: true);
        }
        catch (Exception exception)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Error,
                $"Plugin Tool failed: {Manifest.Id} -> {request.ToolName}",
                exception);
            PostToolResolution(
                request.RequestId,
                SerializeError("tool_error", "The Tool call failed."),
                isError: true);
        }
    }

    private void ProcessUiContribution(NativeToolRequest request)
    {
        permissions.Demand(PluginCapabilities.UiContribute);
        using var document = JsonDocument.Parse(request.InputJson);
        var hasHandler = document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("interactive", out var interactive) &&
            interactive.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            interactive.GetBoolean();
        var localId = document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : string.Empty;
        var (lease, contribution) = uiRegistry.Register(
            Manifest.Id,
            request.InputJson,
            permissions,
            hasHandler
                ? (input, cancellationToken) => InvokeCallbackAsync($"ui:{localId}", input, cancellationToken)
                : null);
        lock (uiRegistrations)
        {
            if (lifetime.IsCancellationRequested)
            {
                lease.Dispose();
                throw new OperationCanceledException(lifetime.Token);
            }
            uiRegistrations.Add(lease);
        }

        PostToolResolution(
            request.RequestId,
            new JsonObject { ["id"] = contribution.Id }.ToJsonString(),
            isError: false);
    }

    private void ProcessConsoleLog(NativeToolRequest request)
    {
        using var document = JsonDocument.Parse(request.InputJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("level", out var levelElement) ||
            levelElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String)
        {
            throw new PluginSdkException("invalid_log_entry", "Plugin log entry requires string level and message values.");
        }
        var level = levelElement.GetString() switch
        {
            "debug" => ApplicationLogLevel.Debug,
            "info" => ApplicationLogLevel.Information,
            "warn" => ApplicationLogLevel.Warning,
            "error" => ApplicationLogLevel.Error,
            _ => throw new PluginSdkException("invalid_log_entry", "Plugin log level is not supported."),
        };
        var now = Environment.TickCount64;
        while (consoleLogTimestamps.TryPeek(out var timestamp) && timestamp <= now - 1000)
        {
            consoleLogTimestamps.Dequeue();
        }
        if (consoleLogTimestamps.Count >= 100)
        {
            PostToolResolution(request.RequestId, "null", isError: false);
            return;
        }
        consoleLogTimestamps.Enqueue(now);
        var prefix = $"[plugin:{Manifest.Id}] ";
        var maximumMessageBytes = (16 * 1024) - Encoding.UTF8.GetByteCount(prefix);
        var message = TruncateUtf8(messageElement.GetString() ?? string.Empty, maximumMessageBytes);
        log.Write(LogComponent.QuickJs, level, prefix + message);
        PostToolResolution(request.RequestId, "null", isError: false);
    }

    private void DeclareManifestPage()
    {
        if (Manifest.Ui is null)
        {
            return;
        }

        var lease = uiRegistry.DeclarePage(
            Manifest.Id,
            Manifest.Ui,
            installation.DirectoryPath,
            permissions,
            LoadRemoteImageAsync);
        lock (uiRegistrations)
        {
            if (lifetime.IsCancellationRequested)
            {
                lease.Dispose();
                throw new OperationCanceledException(lifetime.Token);
            }
            uiRegistrations.Add(lease);
        }
    }

    private void DeclareManifestConfiguration()
    {
        if (Manifest.Configuration is null)
        {
            return;
        }
        var lease = uiRegistry.DeclareConfiguration(
            Manifest.Id,
            Manifest.Configuration,
            installation.DirectoryPath,
            permissions,
            LoadRemoteImageAsync);
        lock (uiRegistrations)
        {
            if (lifetime.IsCancellationRequested)
            {
                lease.Dispose();
                throw new OperationCanceledException(lifetime.Token);
            }
            uiRegistrations.Add(lease);
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>> LoadRemoteImageAsync(
        string source,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        return await http.LoadRemoteImageAsync(source, linkedCancellation.Token).ConfigureAwait(false);
    }

    private void ProcessUiConfigurationRequest(NativeToolRequest request)
    {
        using var document = ParseUiPageRequest(request.InputJson);
        var root = document.RootElement;
        switch (request.ToolName)
        {
            case "canx:ui/configuration/mount":
            {
                var content = RequirePageContent(root);
                uiRegistry.MountConfiguration(
                    Manifest.Id,
                    content.GetRawText(),
                    permissions,
                    (input, cancellationToken) =>
                        InvokeCallbackAsync("ui-configuration:main", input, cancellationToken));
                break;
            }
            case "canx:ui/configuration/render":
                uiRegistry.RenderConfiguration(
                    Manifest.Id,
                    RequirePageContent(root).GetRawText(),
                    permissions);
                break;
            case "canx:ui/configuration/update":
                if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
                {
                    throw new PluginUiException(
                        "invalid_ui_page",
                        "Plugin configuration update requires a patches array.");
                }
                uiRegistry.UpdateConfiguration(Manifest.Id, patches.GetRawText(), permissions);
                break;
            case "canx:ui/configuration/close":
                uiRegistry.RequestConfigurationClose(Manifest.Id, permissions);
                break;
            default:
                throw new PluginUiException(
                    "invalid_ui_page",
                    "Plugin configuration operation is not supported.");
        }
        PostToolResolution(request.RequestId, "null", isError: false);
    }

    private void ProcessUiPageRequest(NativeToolRequest request)
    {
        permissions.Demand(PluginCapabilities.UiPage);
        using var document = ParseUiPageRequest(request.InputJson);
        var root = document.RootElement;
        switch (request.ToolName)
        {
            case "canx:ui/page/mount":
            {
                var content = RequirePageContent(root);
                var interactive = root.TryGetProperty("interactive", out var interactiveElement) &&
                    interactiveElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    interactiveElement.GetBoolean();
                uiRegistry.MountPage(
                    Manifest.Id,
                    content.GetRawText(),
                    permissions,
                    interactive
                        ? (input, cancellationToken) =>
                            InvokeCallbackAsync("ui-page:main", input, cancellationToken)
                        : null);
                break;
            }
            case "canx:ui/page/render":
                uiRegistry.RenderPage(
                    Manifest.Id,
                    RequirePageContent(root).GetRawText(),
                    permissions);
                break;
            case "canx:ui/page/update":
                if (!root.TryGetProperty("patches", out var patches) ||
                    patches.ValueKind != JsonValueKind.Array)
                {
                    throw new PluginUiException(
                        "invalid_ui_page",
                        "Plugin page update requires a patches array.");
                }
                uiRegistry.UpdatePage(Manifest.Id, patches.GetRawText(), permissions);
                break;
            case "canx:ui/page/badge":
            {
                string? text = null;
                if (root.TryGetProperty("text", out var textElement))
                {
                    if (textElement.ValueKind == JsonValueKind.String)
                    {
                        text = textElement.GetString();
                    }
                    else if (textElement.ValueKind != JsonValueKind.Null)
                    {
                        throw new PluginUiException(
                            "invalid_ui_page",
                            "Plugin page badge text must be a string or null.");
                    }
                }
                var tone = root.TryGetProperty("tone", out var toneElement) &&
                    toneElement.ValueKind == JsonValueKind.String
                    ? toneElement.GetString()!
                    : "default";
                uiRegistry.SetPageBadge(Manifest.Id, text, tone, permissions);
                break;
            }
            default:
                throw new PluginUiException("invalid_ui_page", "Plugin page operation is not supported.");
        }

        PostToolResolution(request.RequestId, "null", isError: false);
    }

    private static JsonDocument ParseUiPageRequest(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new PluginUiException("invalid_ui_page", "Plugin page request must be an object.");
            }
            return document;
        }
        catch (JsonException)
        {
            throw new PluginUiException("invalid_ui_page", "Plugin page request is not valid JSON.");
        }
    }

    private static JsonElement RequirePageContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
        {
            throw new PluginUiException("invalid_ui_page", "Plugin page request requires object content.");
        }
        return content;
    }

    private async ValueTask ProcessDownloadPromptAsync(NativeToolRequest request)
    {
        permissions.Demand(PluginCapabilities.DownloadsPrompt);
        using var document = JsonDocument.Parse(request.InputJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("gestureToken", out var tokenElement) ||
            !tokenElement.TryGetUInt64(out var gestureToken))
        {
            throw new PluginDownloadPromptException(
                "invalid_download_request",
                "Download confirmation requires kind, source, and a user gesture token.");
        }
        if (!callbackInvocations.TryGetValue(gestureToken, out var invocation) ||
            !invocation.AllowsDownloadPrompt)
        {
            throw new PluginDownloadPromptException(
                "user_gesture_required",
                "Download confirmation must be requested directly from a plugin page user event.");
        }

        await downloadPromptBroker.RequestAsync(
            new PluginDownloadPromptRequest(
                Manifest.Id,
                kindElement.GetString()!,
                sourceElement.GetString()!),
            lifetime.Token).ConfigureAwait(false);
        PostToolResolution(request.RequestId, "null", isError: false);
    }

    private async ValueTask ProcessSdkToolRequestAsync(NativeToolRequest request)
    {
        using var document = JsonDocument.Parse(request.InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("action", out var actionElement) ||
            actionElement.ValueKind != JsonValueKind.String)
        {
            throw new PluginSdkException("invalid_request", "SDK action request requires a string action.");
        }

        var area = request.ToolName == "canx:playback/invoke" ? "playback" : "downloads";
        var (capability, toolName) = PluginSdkToolPolicy.Resolve(area, actionElement.GetString()!);
        permissions.Demand(capability);
        var hasInput = document.RootElement.TryGetProperty("input", out var input);
        var inputJson = hasInput
            ? input.GetRawText()
            : "{}";
        if (hasInput && input.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new PluginSdkException("invalid_request", "SDK action input must be an object or null.");
        }

        var result = await DispatchToolAsync(toolName, inputJson, lifetime.Token).ConfigureAwait(false);
        PostToolResolution(request.RequestId, result.Json, isError: !result.IsSuccess);
    }

    private void ProcessObservationSubscription(NativeToolRequest request)
    {
        using var document = JsonDocument.Parse(request.InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("area", out var areaElement) ||
            areaElement.ValueKind != JsonValueKind.String ||
            !document.RootElement.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            throw new PluginSdkException("invalid_request", "Observation requires string area and id.");
        }

        var area = areaElement.GetString()!;
        var id = idElement.GetString()!;
        if (id.Length is < 1 or > 64 || id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new PluginSdkException("invalid_request", "Observation id is invalid.");
        }
        var intervalMilliseconds = document.RootElement.TryGetProperty("intervalMs", out var intervalElement) &&
            intervalElement.TryGetInt32(out var parsedInterval)
            ? parsedInterval
            : 1000;
        var interval = TimeSpan.FromMilliseconds(intervalMilliseconds);
        if (interval < options.MinimumSubscriptionInterval || interval > options.MaximumSubscriptionInterval)
        {
            throw new PluginSdkException(
                "invalid_request",
                $"Observation interval must be between {options.MinimumSubscriptionInterval.TotalMilliseconds:0} and " +
                $"{options.MaximumSubscriptionInterval.TotalMilliseconds:0} milliseconds.");
        }

        var (capability, toolName) = PluginSdkToolPolicy.ResolveObservation(area);
        permissions.Demand(capability);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var subscription = new PluginSubscription(id, cancellation);
        lock (subscriptions)
        {
            if (subscriptions.Count >= options.MaximumSubscriptions)
            {
                cancellation.Dispose();
                throw new PluginSdkException(
                    "subscription_limit",
                    $"Plugin cannot create more than {options.MaximumSubscriptions} observations.");
            }
            if (!subscriptions.TryAdd(id, subscription))
            {
                cancellation.Dispose();
                throw new PluginSdkException("subscription_exists", "Observation id is already active.");
            }

            subscription.Task = Task.Run(() => RunObservationAsync(subscription, area, toolName, interval));
        }
        PostToolResolution(request.RequestId, "null", isError: false);
    }

    private void ProcessObservationCancellation(NativeToolRequest request)
    {
        using var document = JsonDocument.Parse(request.InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            throw new PluginSdkException("invalid_request", "Observation cancellation requires a string id.");
        }

        PluginSubscription? subscription;
        lock (subscriptions)
        {
            subscriptions.Remove(idElement.GetString()!, out subscription);
        }
        subscription?.Cancellation.Cancel();
        PostToolResolution(request.RequestId, subscription is null ? "false" : "true", isError: false);
    }

    private async Task RunObservationAsync(
        PluginSubscription subscription,
        string area,
        string toolName,
        TimeSpan interval)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(subscription.Cancellation.Token).ConfigureAwait(false))
            {
                var result = await DispatchToolAsync(toolName, "{}", subscription.Cancellation.Token)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    continue;
                }

                await InvokeCallbackAsync(
                    $"observe:{subscription.Id}",
                    result.Json,
                    subscription.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (subscription.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Warning,
                $"Plugin {area} observation stopped: {Manifest.Id}",
                exception);
        }
        finally
        {
            lock (subscriptions)
            {
                if (subscriptions.GetValueOrDefault(subscription.Id) == subscription)
                {
                    subscriptions.Remove(subscription.Id);
                }
            }
            subscription.Cancellation.Dispose();
        }
    }

    private async ValueTask<PluginToolDispatchResult> DispatchToolAsync(
        string toolName,
        string inputJson,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(inputJson) > options.MaximumToolPayloadBytes)
        {
            throw new PluginSdkException("payload_too_large", "Tool input exceeds the configured limit.");
        }
        using (JsonDocument.Parse(inputJson))
        {
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        timeout.CancelAfter(options.ToolTimeout);
        var result = await toolDispatcher.InvokeAsync(
            new PluginToolRequest(Manifest.Id, toolName, inputJson),
            timeout.Token).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(result.Json) > options.MaximumToolPayloadBytes)
        {
            throw new PluginSdkException("payload_too_large", "Tool result exceeds the configured limit.");
        }
        using (JsonDocument.Parse(result.Json))
        {
        }

        return result;
    }

    private void CancelSubscriptions()
    {
        PluginSubscription[] active;
        lock (subscriptions)
        {
            active = subscriptions.Values.ToArray();
            subscriptions.Clear();
        }
        foreach (var subscription in active)
        {
            subscription.Cancellation.Cancel();
        }
    }

    private void ProcessCallbackCompletion(NativeToolRequest request)
    {
        using var document = JsonDocument.Parse(request.InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("requestId", out var idElement) ||
            !idElement.TryGetUInt64(out var invocationId) ||
            !document.RootElement.TryGetProperty("ok", out var okElement) ||
            okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new PluginSdkException("invalid_callback_data", "Callback completion is invalid.");
        }

        if (!callbackInvocations.TryGetValue(invocationId, out var invocation))
        {
            throw new PluginSdkException("invocation_not_found", "Callback invocation is no longer active.");
        }

        if (okElement.GetBoolean())
        {
            if (!document.RootElement.TryGetProperty("result", out var result))
            {
                invocation.Completion.TrySetException(new PluginSdkException(
                    "invalid_callback_data",
                    "Callback did not return a result."));
            }
            else
            {
                invocation.Completion.TrySetResult(result.GetRawText());
            }
        }
        else
        {
            var message = document.RootElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            invocation.Completion.TrySetException(new PluginSdkException(
                "callback_failed",
                string.IsNullOrWhiteSpace(message) ? "Plugin callback failed." : Truncate(message, 512)));
        }

        PostToolResolution(request.RequestId, "null", isError: false);
    }

    private void ProcessServiceRegistration(NativeToolRequest request)
    {
        using var document = JsonDocument.Parse(request.InputJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PluginServiceException("invalid_service", "Service registration must be an object.");
        }

        var localId = RequiredServiceString(root, "id");
        var contract = RequiredServiceString(root, "contract");
        var contractVersion = RequiredServiceString(root, "contractVersion");
        var title = RequiredServiceString(root, "title");
        var description = OptionalServiceString(root, "description");
        if (!root.TryGetProperty("inputSchema", out var inputSchema) ||
            !root.TryGetProperty("outputSchema", out var outputSchema))
        {
            throw new PluginServiceException("invalid_service", "Service registration requires inputSchema and outputSchema.");
        }
        var risk = OptionalServiceString(root, "risk") switch
        {
            null or "readOnly" => PluginServiceRisk.ReadOnly,
            "network" => PluginServiceRisk.Network,
            "localWrite" => PluginServiceRisk.LocalWrite,
            "destructive" => PluginServiceRisk.Destructive,
            _ => throw new PluginServiceException("invalid_service", "Service risk is not supported."),
        };
        if (!root.TryGetProperty("audiences", out var audiencesElement) ||
            audiencesElement.ValueKind != JsonValueKind.Array)
        {
            throw new PluginServiceException("invalid_service", "Service audiences must be an array.");
        }
        var audiences = audiencesElement.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new PluginServiceException("invalid_service", "Service audiences must contain strings.");
            }
            return item.GetString() switch
            {
                "plugin" => PluginServiceAudience.Plugin,
                "ai" => PluginServiceAudience.Ai,
                _ => throw new PluginServiceException("invalid_service", "Service audience is not supported."),
            };
        }).ToArray();

        var registrationKey = $"service:{localId}";
        var lease = serviceRegistry.Register(
            Manifest.Id,
            localId,
            contract,
            contractVersion,
            title,
            description,
            risk,
            audiences,
            inputSchema,
            outputSchema,
            permissions,
            (input, context, cancellationToken) =>
                InvokeCallbackAsync(registrationKey, input, cancellationToken, context));
        lock (serviceRegistrations)
        {
            if (lifetime.IsCancellationRequested)
            {
                lease.Dispose();
                throw new OperationCanceledException(lifetime.Token);
            }
            serviceRegistrations.Add(lease);
        }

        PostToolResolution(
            request.RequestId,
            new JsonObject { ["id"] = $"{Manifest.Id}/{localId}" }.ToJsonString(),
            isError: false);
    }

    private void ProcessServiceList(NativeToolRequest request)
    {
        permissions.Demand(PluginCapabilities.ServicesInvoke);
        using var document = JsonDocument.Parse(request.InputJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PluginServiceException("invalid_service_request", "Service query must be an object.");
        }
        var contract = OptionalServiceString(root, "contract");
        int? majorVersion = null;
        if (root.TryGetProperty("majorVersion", out var majorElement))
        {
            if (!majorElement.TryGetInt32(out var value) || value < 0)
            {
                throw new PluginServiceException("invalid_service_request", "majorVersion must be a non-negative integer.");
            }
            majorVersion = value;
        }
        var services = serviceRegistry.Find(contract, majorVersion, PluginServiceAudience.Plugin)
            .Where(service => permissions.AllowsServiceInvocation(service.Id, service.Contract))
            .Select(ServiceDescriptorJson)
            .ToArray();
        PostToolResolution(
            request.RequestId,
            new JsonObject { ["services"] = new JsonArray(services) }.ToJsonString(),
            isError: false);
    }

    private void ProcessServiceDescription(NativeToolRequest request)
    {
        permissions.Demand(PluginCapabilities.ServicesInvoke);
        using var document = JsonDocument.Parse(request.InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new PluginServiceException("invalid_service_request", "Service description request must be an object.");
        }
        var serviceId = RequiredServiceString(document.RootElement, "serviceId");
        var descriptor = serviceRegistry.GetDescriptor(serviceId);
        if (!descriptor.Audiences.Contains(PluginServiceAudience.Plugin))
        {
            throw new PluginServiceException("service_not_visible", "Service is not exposed to plugins.");
        }
        permissions.DemandServiceInvocation(descriptor.Id, descriptor.Contract);
        PostToolResolution(request.RequestId, ServiceDescriptorJson(descriptor).ToJsonString(), isError: false);
    }

    private async Task ProcessServiceInvocationAsync(NativeToolRequest request)
    {
        permissions.Demand(PluginCapabilities.ServicesInvoke);
        using var document = JsonDocument.Parse(request.InputJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("input", out var input))
        {
            throw new PluginServiceException("invalid_service_request", "Service invocation requires an input value.");
        }
        var serviceId = RequiredServiceString(root, "serviceId");
        var descriptor = serviceRegistry.GetDescriptor(serviceId);
        if (!descriptor.Audiences.Contains(PluginServiceAudience.Plugin))
        {
            throw new PluginServiceException("service_not_visible", "Service is not exposed to plugins.");
        }
        permissions.DemandServiceInvocation(descriptor.Id, descriptor.Contract);

        var context = PluginServiceCallContext.ForPlugin(Manifest.Id);
        if (root.TryGetProperty("parentInvocationId", out var parentElement))
        {
            if (!parentElement.TryGetUInt64(out var parentId) ||
                !callbackInvocations.TryGetValue(parentId, out var parent) ||
                parent.ServiceContext is null)
            {
                throw new PluginServiceException("invalid_invocation_context", "Service invocation context is no longer active.");
            }
            context = parent.ServiceContext;
        }
        var result = await serviceRegistry.InvokeAsync(serviceId, input, context, lifetime.Token)
            .ConfigureAwait(false);
        PostToolResolution(request.RequestId, result.GetRawText(), isError: false);
    }

    private static JsonObject ServiceDescriptorJson(PluginServiceDescriptor descriptor) => new()
    {
        ["id"] = descriptor.Id,
        ["pluginId"] = descriptor.PluginId,
        ["localId"] = descriptor.LocalId,
        ["contract"] = descriptor.Contract,
        ["contractVersion"] = descriptor.ContractVersion,
        ["title"] = descriptor.Title,
        ["description"] = descriptor.Description,
        ["risk"] = descriptor.Risk switch
        {
            PluginServiceRisk.ReadOnly => "readOnly",
            PluginServiceRisk.Network => "network",
            PluginServiceRisk.LocalWrite => "localWrite",
            PluginServiceRisk.Destructive => "destructive",
            _ => throw new UnreachableException(),
        },
        ["audiences"] = new JsonArray(descriptor.Audiences.Select(audience =>
            (JsonNode?)JsonValue.Create(audience == PluginServiceAudience.Plugin ? "plugin" : "ai")).ToArray()),
        ["inputSchema"] = JsonNode.Parse(descriptor.InputSchema.GetRawText()),
        ["outputSchema"] = JsonNode.Parse(descriptor.OutputSchema.GetRawText()),
    };

    private static string RequiredServiceString(JsonElement root, string property)
    {
        var value = OptionalServiceString(root, property);
        return string.IsNullOrWhiteSpace(value)
            ? throw new PluginServiceException("invalid_service", $"Service property '{property}' is required.")
            : value;
    }

    private static string? OptionalServiceString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new PluginServiceException("invalid_service", $"Service property '{property}' must be a string.");
        }
        return element.GetString();
    }

    private async ValueTask<string> InvokeCallbackAsync(
        string registrationId,
        string inputJson,
        CancellationToken cancellationToken,
        PluginServiceCallContext? serviceContext = null)
    {
        if (State != PluginHostState.Running)
        {
            throw new PluginSdkException("callback_unavailable", "Plugin callback is not running.");
        }

        var requestId = checked((ulong)Interlocked.Increment(ref nextCallbackInvocation));
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = new CallbackInvocation(
            completion,
            AllowsDownloadPrompt(registrationId, inputJson),
            serviceContext);
        if (!callbackInvocations.TryAdd(requestId, invocation))
        {
            throw new PluginSdkException("callback_unavailable", "Plugin callback request id collided.");
        }

        try
        {
            await InvokeOnRuntimeThreadAsync(
                () =>
                {
                    runtime?.InvokeCallback(requestId, registrationId, inputJson, options.ExecutionTimeout);
                    PumpJobsOnce();
                },
                cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(options.ToolTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new PluginSdkException("callback_timeout", "Plugin callback timed out.");
        }
        finally
        {
            callbackInvocations.TryRemove(requestId, out _);
        }
    }

    private static bool AllowsDownloadPrompt(string registrationId, string inputJson)
    {
        if (registrationId != "ui-page:main")
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() is "action" or "change";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var lower = 0;
        var upper = value.Length;
        while (lower < upper)
        {
            var candidate = lower + ((upper - lower + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, candidate)) <= maximumBytes)
            {
                lower = candidate;
            }
            else
            {
                upper = candidate - 1;
            }
        }
        if (lower > 0 && lower < value.Length &&
            char.IsHighSurrogate(value[lower - 1]) && char.IsLowSurrogate(value[lower]))
        {
            lower--;
        }
        return value[..lower];
    }

    private void RemoveServiceRegistrations()
    {
        IDisposable[] registrations;
        lock (serviceRegistrations)
        {
            registrations = [.. serviceRegistrations];
            serviceRegistrations.Clear();
        }

        DisposeRegistrationsSafely(registrations, "service");
    }

    private void RemoveUiRegistrations()
    {
        IDisposable[] registrations;
        lock (uiRegistrations)
        {
            registrations = [.. uiRegistrations];
            uiRegistrations.Clear();
        }

        DisposeRegistrationsSafely(registrations, "UI");
    }

    private void DisposeRegistrationsSafely(IEnumerable<IDisposable> registrations, string kind)
    {
        foreach (var registration in registrations)
        {
            try
            {
                registration.Dispose();
            }
            catch (Exception exception)
            {
                WriteErrorSafely(
                    LogComponent.QuickJs,
                    $"Plugin {kind} registration cleanup failed: {Manifest.Id}",
                    exception);
            }
        }
    }

    private async ValueTask ProcessSecretRequestAsync(NativeToolRequest request)
    {
        using var document = JsonDocument.Parse(request.InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            throw new PluginSecretException("invalid_request", "Secret request requires a string name.");
        }

        var name = nameElement.GetString()!;
        permissions.Demand(PluginCapabilities.Secrets, name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(options.ToolTimeout);
        string result;
        switch (request.ToolName)
        {
            case "canx:secrets/get":
                var secret = await secrets.GetAsync(name, timeout.Token).ConfigureAwait(false);
                result = JsonSerializer.Serialize(secret, PluginRuntimeJsonContext.Default.String);
                break;
            case "canx:secrets/set":
                if (!document.RootElement.TryGetProperty("value", out var valueElement) ||
                    valueElement.ValueKind != JsonValueKind.String)
                {
                    throw new PluginSecretException("invalid_request", "Secret set requires a string value.");
                }

                await secrets.SetAsync(name, valueElement.GetString()!, timeout.Token).ConfigureAwait(false);
                result = "null";
                break;
            case "canx:secrets/remove":
                result = await secrets.RemoveAsync(name, timeout.Token).ConfigureAwait(false) ? "true" : "false";
                break;
            default:
                throw new PluginSecretException("unsupported_operation", "Secret operation is not supported.");
        }

        PostToolResolution(request.RequestId, result, isError: false);
    }

    private void ProcessStorageRequest(NativeToolRequest request)
    {
        permissions.Demand(PluginCapabilities.Storage);
        using var document = JsonDocument.Parse(request.InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new PluginStorageException("invalid_request", "Storage request must be a JSON object.");
        }

        var root = document.RootElement;
        var result = request.ToolName switch
        {
            "canx:storage/get" => storage.Get(ReadStorageKey(root)),
            "canx:storage/set" => SetStorageValue(root),
            "canx:storage/remove" => storage.Remove(ReadStorageKey(root)) ? "true" : "false",
            "canx:storage/keys" => JsonSerializer.Serialize(storage.Keys(), PluginRuntimeJsonContext.Default.IReadOnlyListString),
            "canx:storage/clear" => ClearStorage(),
            _ => throw new PluginStorageException("unsupported_operation", "Storage operation is not supported."),
        };
        PostToolResolution(request.RequestId, result, isError: false);
    }

    private string SetStorageValue(JsonElement request)
    {
        var key = ReadStorageKey(request);
        if (!request.TryGetProperty("value", out var value))
        {
            throw new PluginStorageException("invalid_request", "Storage set requires a value.");
        }

        storage.Set(key, value.GetRawText());
        return "null";
    }

    private string ClearStorage()
    {
        storage.Clear();
        return "null";
    }

    private static string ReadStorageKey(JsonElement request)
    {
        if (!request.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
        {
            throw new PluginStorageException("invalid_request", "Storage request requires a string key.");
        }

        return key.GetString()!;
    }

    private void PostToolResolution(ulong requestId, string resultJson, bool isError)
    {
        if (lifetime.IsCancellationRequested || runtimeCommands.IsAddingCompleted)
        {
            return;
        }

        try
        {
            runtimeCommands.Add(() =>
            {
                try
                {
                    runtime?.ResolveTool(requestId, resultJson, isError);
                    PumpJobsOnce();
                }
                catch (Exception exception)
                {
                    SetState(PluginHostState.Faulted);
                    WriteErrorSafely(
                        LogComponent.QuickJs,
                        $"Plugin promise resolution failed: {Manifest.Id}",
                        exception);
                    RemoveServiceRegistrations();
                    RemoveUiRegistrations();
                }
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PumpJobsOnce()
    {
        if (runtime is null)
        {
            return;
        }

        var executed = runtime.PumpJobs(options.MaximumJobsPerTurn, options.ExecutionTimeout);
        if (executed == options.MaximumJobsPerTurn &&
            !lifetime.IsCancellationRequested &&
            !runtimeCommands.IsAddingCompleted)
        {
            runtimeCommands.Add(PumpJobsOnce);
        }
    }

    private Task InvokeOnRuntimeThreadAsync(Action action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            runtimeCommands.Add(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private void RuntimeLoop()
    {
        foreach (var command in runtimeCommands.GetConsumingEnumerable())
        {
            try
            {
                command();
            }
            catch (Exception exception)
            {
                SetState(PluginHostState.Faulted);
                WriteErrorSafely(
                    LogComponent.QuickJs,
                    $"Plugin runtime command failed: {Manifest.Id}",
                    exception);
                RemoveServiceRegistrations();
                RemoveUiRegistrations();
            }
        }
    }

    private void WriteErrorSafely(LogComponent component, string message, Exception exception)
    {
        try
        {
            log.Write(component, ApplicationLogLevel.Error, message, exception);
        }
        catch (Exception)
        {
            // Fault reporting must never terminate the dedicated plugin runtime thread.
        }
    }

    private void SetState(PluginHostState newState)
    {
        lock (stateLock)
        {
            state = newState;
        }
    }

    private static string SerializeError(string code, string message) =>
        JsonSerializer.Serialize(
            new PluginBridgeError(code, message),
            PluginRuntimeJsonContext.Default.PluginBridgeError);

    private sealed record NativeToolRequest(ulong RequestId, string ToolName, string InputJson);

    private sealed record CallbackInvocation(
        TaskCompletionSource<string> Completion,
        bool AllowsDownloadPrompt,
        PluginServiceCallContext? ServiceContext);

    private sealed class PluginSubscription(string id, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? Task { get; set; }
    }

    private sealed record PluginModuleSource(string Name, string Source);
}
