// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.App.ViewModels;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Plugins.Ui;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PluginPagesViewModelTests
{
    [Fact]
    public async Task ActivateAndDeactivate_SendLifecycleEventsAndUpdateSelection()
    {
        var registry = new PluginUiRegistry();
        var events = new List<string>();
        using var registration = registry.DeclarePage(
            "org.canx.tools",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            Permissions());
        registry.MountPage(
            "org.canx.tools",
            "{\"type\":\"text\",\"props\":{\"text\":\"Ready\"}}",
            Permissions(),
            (input, _) =>
            {
                using var document = JsonDocument.Parse(input);
                events.Add(document.RootElement.GetProperty("type").GetString()!);
                return ValueTask.FromResult("null");
            });
        using var viewModel = new PluginPagesViewModel(registry);

        var activated = await viewModel.ActivateAsync("org.canx.tools");
        await viewModel.DeactivateAsync();

        Assert.True(activated);
        var page = Assert.Single(viewModel.Pages);
        Assert.Equal("Tools", page.Title);
        Assert.False(page.IsSelected);
        Assert.Equal(["activate", "deactivate"], events);
    }

    private static PluginPermissionSet Permissions() => PluginPermissionSet.FromManifest(new PluginManifest(
        1,
        "org.canx.tools",
        "Tools",
        null,
        "1.0.0",
        new PluginApiVersion(2, 0),
        "main.mjs",
        [new PluginPermission(PluginCapabilities.UiPage, [])],
        new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools"))));
}
