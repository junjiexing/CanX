// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;

namespace CanX.Plugins.Sdk;

internal static class PluginSdkToolPolicy
{
    private static readonly IReadOnlyDictionary<string, string> PlaybackRead = Map(
        "playback",
        "get",
        "capabilities",
        "chapter.list",
        "bookmark.list",
        "audioDevice.list",
        "shader.list",
        "diagnostics.get");

    private static readonly IReadOnlyDictionary<string, string> PlaybackControl = Map(
        "playback",
        "play",
        "pause",
        "togglePause",
        "stop",
        "seek",
        "playlist.play",
        "playlist.next",
        "playlist.previous",
        "volume.set",
        "brightness.set",
        "mute.set",
        "speed.set",
        "audioDevice.set",
        "filter.video.set",
        "filter.audio.set",
        "filter.clear",
        "track.select",
        "subtitle.visibility.set",
        "subtitle.delay.set",
        "subtitle.style.set",
        "color.set");

    private static readonly IReadOnlyDictionary<string, string> DownloadsRead = Map(
        "downloads",
        "list",
        "get",
        "global.get",
        "capabilities",
        "connections");

    private static readonly IReadOnlyDictionary<string, string> DownloadsManage = Map(
        "downloads",
        "pause",
        "resume",
        "schedule.set",
        "move");

    internal static IReadOnlyDictionary<string, IReadOnlyCollection<string>> SupportedActions { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            [PluginCapabilities.PlaybackRead] = PlaybackRead.Keys.ToArray(),
            [PluginCapabilities.PlaybackControl] = PlaybackControl.Keys.ToArray(),
            [PluginCapabilities.DownloadsRead] = DownloadsRead.Keys.ToArray(),
            [PluginCapabilities.DownloadsManage] = DownloadsManage.Keys.ToArray(),
        };

    public static (string Capability, string ToolName) Resolve(string area, string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var read = area == "playback" ? PlaybackRead : area == "downloads" ? DownloadsRead : null;
        var control = area == "playback" ? PlaybackControl : area == "downloads" ? DownloadsManage : null;
        var readCapability = area == "playback" ? PluginCapabilities.PlaybackRead : PluginCapabilities.DownloadsRead;
        var controlCapability = area == "playback" ? PluginCapabilities.PlaybackControl : PluginCapabilities.DownloadsManage;
        if (read is null || control is null)
        {
            throw new PluginSdkException("unsupported_area", "SDK Tool area is not supported.");
        }
        if (read.TryGetValue(action, out var readTool))
        {
            return (readCapability, readTool);
        }
        if (control.TryGetValue(action, out var controlTool))
        {
            return (controlCapability, controlTool);
        }

        throw new PluginSdkException("unsupported_action", $"SDK action '{area}.{action}' is not supported.");
    }

    public static (string Capability, string ToolName) ResolveObservation(string area) => area switch
    {
        "playback" => (PluginCapabilities.PlaybackRead, "playback.get"),
        "downloads" => (PluginCapabilities.DownloadsRead, "downloads.list"),
        _ => throw new PluginSdkException("unsupported_area", "Observation area is not supported."),
    };

    private static IReadOnlyDictionary<string, string> Map(string area, params string[] actions) =>
        actions.ToDictionary(action => action, action => $"{area}.{action}", StringComparer.Ordinal);
}

internal sealed class PluginSdkException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
