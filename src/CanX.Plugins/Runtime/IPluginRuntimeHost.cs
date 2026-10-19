// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;

namespace CanX.Plugins.Runtime;

public interface IPluginRuntimeHost : IAsyncDisposable
{
    PluginManifest Manifest { get; }

    PluginHostState State { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}

public interface IPluginRuntimeHostFactory
{
    IPluginRuntimeHost Create(PluginInstallation installation);
}
