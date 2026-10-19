// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;
using CanX.Playback.Engine;

namespace CanX.Playback;

internal static class PlaybackCapabilityPolicy
{
    private const string SensitiveRestriction =
        "Blocked because the property can expose media locations, credentials, headers, or private host state.";
    private const string HostRestriction =
        "Read access is allowed, but writes are blocked because CanX owns this part of the libmpv host contract.";
    private const string CommandRestriction =
        "Blocked because the command can execute processes, access files, load code/media, inject input, write output, or terminate the host.";

    private static readonly HashSet<string> AllowedAdvancedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ab-loop", "ab-loop-align-cache", "ao-reload", "audio-reload", "audio-remove", "discnav",
        "drop-buffers", "escape-ass", "flush-status-line", "frame-back-step", "frame-step", "ignore",
        "playlist-clear", "playlist-move", "playlist-next", "playlist-next-playlist", "playlist-play-index",
        "playlist-prev", "playlist-prev-playlist", "playlist-remove", "playlist-shuffle", "playlist-unshuffle",
        "revert-seek", "seek", "show-progress", "show-text", "stop", "sub-reload", "sub-remove", "sub-seek",
        "sub-step", "video-reload", "video-remove",
    };

    private static readonly HashSet<string> HostManagedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "config-dir", "gpu-api", "gpu-context", "idle", "input-builtin-bindings",
        "input-default-bindings", "input-ipc-server", "input-vo-keyboard", "keep-open", "log-file",
        "osd-bar", "record-file", "screenshot-directory", "scripts", "stream-open-filename", "stream-record",
        "terminal", "vo", "watch-later-directory", "wid",
    };

    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "cookies-file", "current-watch-later-dir", "file-local-options", "http-header-fields", "input-bindings",
        "options", "path", "playlist", "profile-list", "referrer", "stream-open-filename", "stream-path",
        "track-list", "user-agent", "user-data", "watch-later-directory", "working-directory",
    };

    private static readonly IReadOnlyDictionary<string, string[]> DedicatedPropertyTools =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ab-loop-a"] = ["playback.abLoop.set", "playback.abLoop.clear"],
            ["ab-loop-b"] = ["playback.abLoop.set", "playback.abLoop.clear"],
            ["af"] = ["playback.filter.audio.set", "playback.filter.clear"],
            ["aid"] = ["playback.track.select"],
            ["audio-device"] = ["playback.audioDevice.list", "playback.audioDevice.set"],
            ["audio-device-list"] = ["playback.audioDevice.list"],
            ["brightness"] = ["playback.brightness.set"],
            ["chapter"] = ["playback.chapter.list", "playback.chapter.open"],
            ["chapter-list"] = ["playback.chapter.list", "playback.chapter.open"],
            ["duration"] = ["playback.get"],
            ["glsl-shaders"] = ["playback.shader.list", "playback.shader.add", "playback.shader.remove", "playback.shader.clear"],
            ["idle-active"] = ["playback.get"],
            ["media-title"] = ["playback.get"],
            ["mute"] = ["playback.mute.set"],
            ["path"] = ["playback.get"],
            ["pause"] = ["playback.play", "playback.pause", "playback.togglePause"],
            ["playlist"] = ["playback.playlist.open", "playback.playlist.add", "playback.playlist.play"],
            ["playlist-count"] = ["playback.get"],
            ["playlist-pos"] = ["playback.playlist.play"],
            ["seekable"] = ["playback.get"],
            ["sid"] = ["playback.track.select"],
            ["speed"] = ["playback.speed.set"],
            ["sub-delay"] = ["playback.subtitle.delay.set"],
            ["sub-font-size"] = ["playback.subtitle.style.set"],
            ["sub-scale"] = ["playback.subtitle.style.set"],
            ["sub-pos"] = ["playback.subtitle.style.set"],
            ["sub-visibility"] = ["playback.subtitle.visibility.set"],
            ["time-pos"] = ["playback.get", "playback.seek"],
            ["track-list"] = ["playback.get", "playback.track.select"],
            ["vf"] = ["playback.filter.video.set", "playback.filter.clear"],
            ["vid"] = ["playback.track.select"],
            ["video-params"] = ["playback.get", "playback.diagnostics.get"],
            ["volume"] = ["playback.volume.set"],
        };

    private static readonly IReadOnlyDictionary<string, string[]> DedicatedCommandTools =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["af"] = ["playback.filter.audio.set", "playback.filter.clear"],
            ["change-list"] = ["playback.shader.add", "playback.shader.remove", "playback.shader.clear"],
            ["frame-back-step"] = ["playback.frame.previous"],
            ["frame-step"] = ["playback.frame.next"],
            ["loadfile"] = ["playback.open", "playback.playlist.open", "playback.playlist.add"],
            ["playlist-clear"] = ["playback.playlist.clear"],
            ["playlist-move"] = ["playback.playlist.move"],
            ["playlist-next"] = ["playback.playlist.next"],
            ["playlist-prev"] = ["playback.playlist.previous"],
            ["playlist-remove"] = ["playback.playlist.remove"],
            ["screenshot-to-file"] = ["playback.screenshot"],
            ["seek"] = ["playback.seek", "playback.chapter.open", "playback.bookmark.open"],
            ["set"] = ["playback.property.set"],
            ["stop"] = ["playback.stop"],
            ["sub-add"] = ["playback.subtitle.add"],
            ["vf"] = ["playback.filter.video.set", "playback.filter.clear"],
        };

    private static readonly HashSet<string> DiagnosticProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio-bitrate", "avsync", "cache-speed", "container-fps", "current-gpu-context", "current-vo",
        "decoder-frame-drop-count", "demuxer-cache-duration", "display-fps", "estimated-vf-fps",
        "ffmpeg-version", "frame-drop-count", "gpu-api", "hwdec-current", "hwdec-interop", "libass-version",
        "libplacebo-version", "mistimed-frame-count", "mpv-configuration", "mpv-version", "platform",
        "total-avsync-change", "video-bitrate", "video-format", "video-target-params", "vo-delayed-frame-count",
    };

    private static readonly HashSet<string> ColorProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "gamut-mapping-mode", "hdr-compute-peak", "target-colorspace-hint", "target-colorspace-hint-mode",
        "target-peak", "target-prim", "target-trc", "tone-mapping",
    };

    public static PlaybackCapabilityManifest CreateManifest(MpvRuntimeCapabilityManifest discovered) => new()
    {
        MpvVersion = discovered.MpvVersion,
        ClientApiVersion = discovered.ClientApiVersion,
        FfmpegVersion = discovered.FfmpegVersion,
        Platform = discovered.Platform,
        Configuration = discovered.Configuration,
        Properties = discovered.Properties
            .Select(CreateProperty)
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToList(),
        Commands = discovered.Commands
            .Select(CreateCommand)
            .OrderBy(static command => command.Name, StringComparer.Ordinal)
            .ToList(),
    };

    public static string? GetPropertyReadRestriction(string name)
    {
        if (MatchesAny(name, SensitiveProperties) ||
            name.EndsWith("/external-filename", StringComparison.OrdinalIgnoreCase))
        {
            return SensitiveRestriction;
        }

        return null;
    }

    public static string? GetPropertyWriteRestriction(string name) =>
        GetPropertyReadRestriction(name) ??
        (MatchesAny(name, HostManagedProperties) ||
         CanX.Core.Configuration.MpvOptionConfiguration.IsCodeOrFileOption(name) ||
         MatchesAny(name, FilterGraphProperties)
            ? HostRestriction
            : null);

    // Filter graphs can reference files (movie/amovie sources, subtitles/ass filters, sendcmd) and have
    // dedicated, confirmed Tools; generic property writes must not bypass that path.
    private static readonly HashSet<string> FilterGraphProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "vf", "af", "lavfi-complex",
    };

    public static string? GetCommandRestriction(string name) =>
        AllowedAdvancedCommands.Contains(name) ? null : CommandRestriction;

    private static PlaybackPropertyCapability CreateProperty(string name)
    {
        var readRestriction = GetPropertyReadRestriction(name);
        var writeRestriction = GetPropertyWriteRestriction(name);
        var tools = GetPropertyTools(name);
        var coverage = tools.Count > 0
            ? PlaybackCapabilityCoverageKind.DedicatedTool
            : readRestriction is null || writeRestriction is null
                ? PlaybackCapabilityCoverageKind.AdvancedAccess
                : PlaybackCapabilityCoverageKind.Blocked;
        return new PlaybackPropertyCapability
        {
            Name = name,
            CanRead = readRestriction is null,
            CanWrite = writeRestriction is null,
            Coverage = coverage,
            Tools = tools,
            Restriction = readRestriction ?? writeRestriction ?? string.Empty,
        };
    }

    private static PlaybackCommandCapability CreateCommand(MpvRuntimeCommandCapability command)
    {
        var restriction = GetCommandRestriction(command.Name);
        var tools = DedicatedCommandTools.TryGetValue(command.Name, out var values)
            ? values.ToList()
            : [];
        return new PlaybackCommandCapability
        {
            Name = command.Name,
            CanExecute = restriction is null,
            VariableArguments = command.VariableArguments,
            Coverage = tools.Count > 0
                ? PlaybackCapabilityCoverageKind.DedicatedTool
                : restriction is null
                    ? PlaybackCapabilityCoverageKind.AdvancedAccess
                    : PlaybackCapabilityCoverageKind.Blocked,
            Tools = tools,
            Restriction = restriction ?? string.Empty,
            Arguments = command.Arguments.Select(static argument => new PlaybackCommandArgumentCapability
            {
                Name = argument.Name,
                Type = argument.Type,
                Optional = argument.Optional,
                DefaultValue = argument.DefaultValue,
                Choices = argument.Choices.ToList(),
            }).ToList(),
        };
    }

    private static List<string> GetPropertyTools(string name)
    {
        if (DedicatedPropertyTools.TryGetValue(name, out var tools))
        {
            return tools.ToList();
        }

        if (DiagnosticProperties.Contains(name))
        {
            return ["playback.diagnostics.get"];
        }

        if (ColorProperties.Contains(name))
        {
            return ["playback.color.set", "playback.diagnostics.get"];
        }

        return [];
    }

    private static bool MatchesAny(string name, IEnumerable<string> properties) => properties.Any(property =>
        string.Equals(name, property, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(property + "/", StringComparison.OrdinalIgnoreCase));
}
