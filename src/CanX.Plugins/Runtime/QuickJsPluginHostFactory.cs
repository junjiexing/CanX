// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Diagnostics;
using CanX.Core.Security;
using CanX.Plugins.Packaging;
using CanX.Plugins.Downloads;
using CanX.Plugins.Media;
using CanX.Plugins.Services;
using CanX.Plugins.Ui;
using CanX.Streaming;

namespace CanX.Plugins.Runtime;

public sealed class QuickJsPluginHostFactory : IPluginRuntimeHostFactory
{
    private readonly IPluginToolDispatcher toolDispatcher;
    private readonly PluginRuntimeOptions? options;
    private readonly IApplicationLog? log;
    private readonly string? pluginDataRoot;
    private readonly ICredentialStore? credentialStore;
    private readonly Func<HttpMessageHandler>? httpHandlerFactory;

    public QuickJsPluginHostFactory(
        IPluginToolDispatcher toolDispatcher,
        PluginRuntimeOptions? options = null,
        IApplicationLog? log = null,
        string? pluginDataRoot = null,
        ICredentialStore? credentialStore = null,
        Func<HttpMessageHandler>? httpHandlerFactory = null,
        PluginServiceRegistry? serviceRegistry = null,
        PluginUiRegistry? uiRegistry = null,
        PluginDownloadPromptBroker? downloadPromptBroker = null,
        MediaStreamBroker? mediaStreamBroker = null,
        PluginMediaPlaybackBroker? mediaPlaybackBroker = null,
        Func<HttpMessageHandler>? mediaHttpHandlerFactory = null)
    {
        this.toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        this.options = options;
        this.log = log;
        this.pluginDataRoot = pluginDataRoot;
        this.credentialStore = credentialStore;
        this.httpHandlerFactory = httpHandlerFactory;
        ServiceRegistry = serviceRegistry ?? new PluginServiceRegistry();
        UiRegistry = uiRegistry ?? new PluginUiRegistry();
        DownloadPromptBroker = downloadPromptBroker ?? new PluginDownloadPromptBroker();
        MediaStreamBroker = mediaStreamBroker;
        MediaPlaybackBroker = mediaPlaybackBroker ?? new PluginMediaPlaybackBroker();
        MediaHttpHandlerFactory = mediaHttpHandlerFactory;
    }

    public PluginServiceRegistry ServiceRegistry { get; }

    public PluginUiRegistry UiRegistry { get; }

    public PluginDownloadPromptBroker DownloadPromptBroker { get; }

    public MediaStreamBroker? MediaStreamBroker { get; }

    public PluginMediaPlaybackBroker MediaPlaybackBroker { get; }

    private Func<HttpMessageHandler>? MediaHttpHandlerFactory { get; }

    public IPluginRuntimeHost Create(PluginInstallation installation) =>
        new QuickJsPluginHost(
            installation,
            toolDispatcher,
            options,
            log,
            pluginDataRoot,
            credentialStore,
            httpHandlerFactory?.Invoke(),
            UiRegistry,
            DownloadPromptBroker,
            ServiceRegistry,
            MediaStreamBroker,
            MediaPlaybackBroker,
            MediaHttpHandlerFactory);
}
