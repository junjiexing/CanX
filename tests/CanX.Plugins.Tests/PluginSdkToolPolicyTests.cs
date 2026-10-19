// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;
using CanX.Plugins.Sdk;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginSdkToolPolicyTests
{
    [Theory]
    [InlineData("playback", "get", PluginCapabilities.PlaybackRead, "playback.get")]
    [InlineData("playback", "seek", PluginCapabilities.PlaybackControl, "playback.seek")]
    [InlineData("downloads", "list", PluginCapabilities.DownloadsRead, "downloads.list")]
    [InlineData("downloads", "pause", PluginCapabilities.DownloadsManage, "downloads.pause")]
    public void Resolve_MapsStableActionToCapabilityAndTool(
        string area,
        string action,
        string capability,
        string tool)
    {
        Assert.Equal((capability, tool), PluginSdkToolPolicy.Resolve(area, action));
    }

    [Theory]
    [InlineData("playback", "open")]
    [InlineData("playback", "command.execute")]
    [InlineData("downloads", "remove")]
    [InlineData("downloads", "add")]
    [InlineData("downloads", "addBatch")]
    [InlineData("downloads", "options.set")]
    [InlineData("downloads", "global.set")]
    [InlineData("downloads", "completion.set")]
    [InlineData("downloads", "unknown")]
    public void Resolve_RejectsUnstableOrDestructiveActions(string area, string action)
    {
        var exception = Assert.Throws<PluginSdkException>(() => PluginSdkToolPolicy.Resolve(area, action));

        Assert.Equal("unsupported_action", exception.Code);
    }
}
