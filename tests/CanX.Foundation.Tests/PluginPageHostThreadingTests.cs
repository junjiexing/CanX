// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.Controls;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Plugins.Ui;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PluginPageHostThreadingTests
{
    [Fact]
    public async Task RegistryNotifications_FromWorkerThread_DoNotAccessAvaloniaPropertiesSynchronously()
    {
        var registry = new PluginUiRegistry();
        _ = new PluginPageHost
        {
            Registry = registry,
            PluginId = "org.canx.threading",
        };

        await Task.Run(() =>
        {
            using var registration = registry.DeclarePage(
                "org.canx.threading",
                new PluginUiManifest(
                    "Threading",
                    new PluginUiIcon(PluginUiIconKind.BuiltIn, "settings")),
                ".",
                Permissions());
        });
    }

    private static PluginPermissionSet Permissions() => PluginPermissionSet.FromManifest(new PluginManifest(
        1,
        "org.canx.threading",
        "Threading",
        null,
        "1.0.0",
        new PluginApiVersion(2, 0),
        "main.mjs",
        [new PluginPermission(PluginCapabilities.UiPage, [])],
        new PluginUiManifest(
            "Threading",
            new PluginUiIcon(PluginUiIconKind.BuiltIn, "settings"))));
}
