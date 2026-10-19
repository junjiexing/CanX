// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Manifest;

public static class PluginCapabilities
{
    public const string ToolsInvoke = "tools.invoke";
    public const string ServicesProvide = "services.provide";
    public const string ServicesInvoke = "services.invoke";
    public const string UiPage = "ui.page";
    public const string UiContribute = "ui.contribute";
    public const string UiConfiguration = "ui.configuration";
    public const string UiRemoteImages = "ui.remoteImages";
    public const string PlaybackRead = "playback.read";
    public const string PlaybackControl = "playback.control";
    public const string DownloadsRead = "downloads.read";
    public const string DownloadsManage = "downloads.manage";
    public const string DownloadsPrompt = "downloads.prompt";
    public const string MediaStream = "media.stream";
    public const string Http = "http";
    public const string Storage = "storage";
    public const string Secrets = "secrets";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ToolsInvoke,
        ServicesProvide,
        ServicesInvoke,
        UiPage,
        UiContribute,
        UiConfiguration,
        UiRemoteImages,
        PlaybackRead,
        PlaybackControl,
        DownloadsRead,
        DownloadsManage,
        DownloadsPrompt,
        MediaStream,
        Http,
        Storage,
        Secrets,
    };

    internal static bool RequiresScope(string capability) =>
        capability is ToolsInvoke or ServicesProvide or ServicesInvoke or UiRemoteImages or Http or Secrets;

    internal static bool AllowsScope(string capability) =>
        capability is ToolsInvoke or ServicesProvide or ServicesInvoke or UiRemoteImages or Http or Secrets;
}
