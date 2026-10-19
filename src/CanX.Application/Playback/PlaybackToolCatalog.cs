// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using CanX.Core.Downloads;
using CanX.Core.Playback;
using CanX.Tooling;

namespace CanX.Application.Playback;

public static class PlaybackToolCatalog
{
    /// <summary>Resolves the target once per call, keeping commands on the selected playback session.</summary>
    public static IReadOnlyList<IToolHandler> Create(
        IPlaybackController initialController,
        Func<string, CancellationToken, ValueTask<IPlaybackController>> resolveController)
    {
        var cache = new ConditionalWeakTable<IPlaybackController, Dictionary<string, IToolHandler>>();
        return Create(initialController).Select(template => (IToolHandler)new DelegateTool(
            template.Descriptor, async (arguments, context, cancellationToken) =>
            {
                var controller = await resolveController(template.Descriptor.Name, cancellationToken).ConfigureAwait(false);
                var handlers = cache.GetValue(controller, static target =>
                    Create(target).ToDictionary(static handler => handler.Descriptor.Name, StringComparer.Ordinal));
                return await handlers[template.Descriptor.Name].ExecuteAsync(arguments, context, cancellationToken).ConfigureAwait(false);
            })).ToArray();
    }

    public static IReadOnlyList<IToolHandler> Create(IPlaybackController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return
        [
            new DelegateTool(
                Descriptor("playback.get", "Read the current playback state.", ToolRisk.ReadOnly, EmptySchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(ToJson(controller.Snapshot)))),
            new DelegateTool(
                Descriptor(
                    "playback.capabilities",
                    "List the versioned libmpv property and command manifest with CanX access policy and coverage.",
                    ToolRisk.ReadOnly,
                    EmptySchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(CapabilitiesToJson(controller.Capabilities)))),
            new DelegateTool(
                Descriptor("playback.chapter.list", "List chapters in the current media.", ToolRisk.ReadOnly, EmptySchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(new JsonObject
                {
                    ["currentChapterIndex"] = controller.Snapshot.CurrentChapterIndex,
                    ["chapters"] = ChaptersToJson(controller.Snapshot.Chapters),
                }))),
            new DelegateTool(
                Descriptor("playback.bookmark.list", "List bookmarks for the current media.", ToolRisk.ReadOnly, EmptySchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(new JsonObject
                {
                    ["bookmarks"] = BookmarksToJson(controller.Snapshot.Bookmarks),
                }))),
            new DelegateTool(
                Descriptor("playback.audioDevice.list", "List available audio output devices.", ToolRisk.ReadOnly, EmptySchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(new JsonObject
                {
                    ["current"] = controller.Snapshot.AudioDevice,
                    ["devices"] = AudioDevicesToJson(controller.Snapshot.AudioDevices),
                }))),
            new DelegateTool(
                Descriptor("playback.shader.list", "List active user shaders.", ToolRisk.ReadOnly, EmptySchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(new JsonObject
                {
                    ["shaders"] = new JsonArray(controller.Snapshot.Shaders
                        .Select(static shader => (JsonNode)JsonValue.Create(shader)!)
                        .ToArray()),
                }))),
            new DelegateTool(
                Descriptor(
                    "playback.diagnostics.get",
                    "Read the active decoder, render path, color metadata, and playback performance counters.",
                    ToolRisk.ReadOnly,
                    EmptySchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(new JsonObject
                {
                    ["diagnostics"] = DiagnosticsToJson(controller.Snapshot.Diagnostics),
                    ["colorSettings"] = ColorSettingsToJson(controller.Snapshot.ColorSettings),
                }))),
            CreateAction(
                "playback.color.set",
                "Set one or more runtime color-management and HDR options.",
                ToolRisk.LocalWrite,
                ColorSettingsSchema(),
                (arguments, cancellationToken) => ApplyColorSettingsAsync(controller, arguments, cancellationToken)),
            new DelegateTool(
                Descriptor(
                    "playback.property.get",
                    "Read a non-sensitive mpv runtime property by name.",
                    ToolRisk.ReadOnly,
                    StringSchema("name")),
                async (arguments, _, cancellationToken) =>
                {
                    var name = RequireString(arguments, "name");
                    var value = await controller.GetRuntimePropertyAsync(name, cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(new JsonObject { ["name"] = name, ["value"] = value });
                }),
            CreateAction(
                "playback.property.set",
                "Set an mpv runtime property, excluding host-controlled and sensitive properties.",
                ToolRisk.Destructive,
                RuntimePropertySchema(),
                (arguments, cancellationToken) => controller.SetRuntimePropertyAsync(
                    RequireString(arguments, "name"),
                    RequirePresentString(arguments, "value"),
                    cancellationToken)),
            CreateAction(
                "playback.command.execute",
                "Execute an mpv command that does not start processes, access files, mutate protected properties, or exit the host.",
                ToolRisk.Destructive,
                RuntimeCommandSchema(),
                (arguments, cancellationToken) => controller.ExecuteCommandAsync(
                    RequireString(arguments, "command"),
                    ReadStrings(arguments, "arguments"),
                    cancellationToken)),
            CreateAction(
                "playback.open",
                "Open a local media file or an address supported by mpv, following the playback window setting.",
                ToolRisk.Network,
                StringSchema("source"),
                (arguments, cancellationToken) => controller.OpenAsync(RequireString(arguments, "source"), cancellationToken)),
            CreateAction(
                "playback.playlist.open",
                "Replace the playlist and start one item.",
                ToolRisk.Network,
                SourcesSchema(("startIndex", "integer")),
                (arguments, cancellationToken) => controller.OpenPlaylistAsync(
                    RequireStrings(arguments, "sources"),
                    ReadInt32(arguments, "startIndex"),
                    cancellationToken)),
            CreateAction(
                "playback.playlist.add",
                "Append media sources to the playlist and optionally play the first appended item.",
                ToolRisk.Network,
                SourcesSchema(("playNow", "boolean")),
                (arguments, cancellationToken) => controller.AddToPlaylistAsync(
                    RequireStrings(arguments, "sources"),
                    arguments["playNow"]?.GetValue<bool>() ?? false,
                    cancellationToken)),
            CreateAction(
                "playback.folder.open",
                "Build a playlist from supported media files in a local folder.",
                ToolRisk.LocalWrite,
                StringSchema("directory", ("recursive", "boolean")),
                (arguments, cancellationToken) => controller.OpenFolderAsync(
                    RequireString(arguments, "directory"),
                    arguments["recursive"]?.GetValue<bool>() ?? false,
                    cancellationToken)),
            CreateAction(
                "playback.playlist.play",
                "Play a playlist item by zero-based index.",
                ToolRisk.LocalWrite,
                IntegerSchema("index"),
                (arguments, cancellationToken) => controller.PlayPlaylistItemAsync(
                    RequireInt32(arguments, "index"),
                    cancellationToken)),
            CreateAction(
                "playback.playlist.next",
                "Play the next playlist item.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.PlayNextAsync(cancellationToken)),
            CreateAction(
                "playback.playlist.previous",
                "Play the previous playlist item.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.PlayPreviousAsync(cancellationToken)),
            CreateAction(
                "playback.playlist.remove",
                "Remove one playlist item by zero-based index.",
                ToolRisk.LocalWrite,
                IntegerSchema("index"),
                (arguments, cancellationToken) => controller.RemovePlaylistItemAsync(
                    RequireInt32(arguments, "index"),
                    cancellationToken)),
            CreateAction(
                "playback.playlist.move",
                "Move one playlist item to a new zero-based index.",
                ToolRisk.LocalWrite,
                MovePlaylistItemSchema(),
                (arguments, cancellationToken) => controller.MovePlaylistItemAsync(
                    RequireInt32(arguments, "index"),
                    RequireInt32(arguments, "newIndex"),
                    cancellationToken)),
            CreateAction(
                "playback.playlist.clear",
                "Clear queued playlist items while retaining the currently playing item.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.ClearPlaylistAsync(cancellationToken)),
            CreateAction(
                "playback.recent.open",
                "Open an item from recent playback and restore its unfinished position when enabled.",
                ToolRisk.Network,
                StringSchema("source"),
                (arguments, cancellationToken) => controller.OpenRecentAsync(
                    RequireString(arguments, "source"),
                    cancellationToken)),
            CreateAction(
                "playback.recent.remove",
                "Remove one recent playback record.",
                ToolRisk.Destructive,
                StringSchema("source"),
                (arguments, cancellationToken) => controller.RemoveRecentAsync(
                    RequireString(arguments, "source"),
                    cancellationToken)),
            CreateAction(
                "playback.recent.clear",
                "Clear all recent playback records.",
                ToolRisk.Destructive,
                EmptySchema(),
                (_, cancellationToken) => controller.ClearRecentAsync(cancellationToken)),
            CreateAction(
                "playback.play",
                "Resume playback.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.SetPausedAsync(false, cancellationToken)),
            CreateAction(
                "playback.pause",
                "Pause playback.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.SetPausedAsync(true, cancellationToken)),
            CreateAction(
                "playback.togglePause",
                "Toggle pause state.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.TogglePauseAsync(cancellationToken)),
            CreateAction(
                "playback.stop",
                "Stop playback.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.StopAsync(cancellationToken)),
            CreateAction(
                "playback.seek",
                "Seek to an absolute position or by a relative offset in seconds.",
                ToolRisk.LocalWrite,
                NumberSchema("seconds", ("relative", "boolean")),
                (arguments, cancellationToken) => controller.SeekAsync(
                    RequireDouble(arguments, "seconds"),
                    arguments["relative"]?.GetValue<bool>() ?? false,
                    cancellationToken: cancellationToken)),
            CreateAction(
                "playback.frame.next",
                "Pause and advance by one video frame.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.StepFrameAsync(cancellationToken: cancellationToken)),
            CreateAction(
                "playback.frame.previous",
                "Pause and move backward by one video frame.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.StepFrameAsync(backward: true, cancellationToken)),
            CreateAction(
                "playback.abLoop.set",
                "Set one or both A-B loop points in seconds.",
                ToolRisk.LocalWrite,
                AbLoopSchema(),
                (arguments, cancellationToken) => controller.SetAbLoopAsync(
                    ReadOptionalDouble(arguments, "startSeconds"),
                    ReadOptionalDouble(arguments, "endSeconds"),
                    cancellationToken)),
            CreateAction(
                "playback.abLoop.clear",
                "Clear both A-B loop points.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.ClearAbLoopAsync(cancellationToken)),
            new DelegateTool(
                Descriptor(
                    "playback.screenshot",
                    "Capture the current video frame to a new image file without overwriting an existing file.",
                    ToolRisk.LocalWrite,
                    ScreenshotSchema()),
                async (arguments, _, cancellationToken) =>
                {
                    var path = await controller.TakeScreenshotAsync(
                        ReadOptionalString(arguments, "path"),
                        ReadScreenshotMode(arguments),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(new JsonObject { ["path"] = path });
                }),
            CreateAction(
                "playback.chapter.open",
                "Seek to a chapter by zero-based chapter index.",
                ToolRisk.LocalWrite,
                IntegerSchema("index"),
                (arguments, cancellationToken) => controller.SeekToChapterAsync(
                    RequireInt32(arguments, "index"),
                    cancellationToken)),
            CreateAction(
                "playback.bookmark.add",
                "Add a bookmark to the current media at the current or specified position.",
                ToolRisk.LocalWrite,
                AddBookmarkSchema(),
                (arguments, cancellationToken) => controller.AddBookmarkAsync(
                    ReadOptionalString(arguments, "title"),
                    ReadOptionalDouble(arguments, "positionSeconds"),
                    cancellationToken)),
            CreateAction(
                "playback.bookmark.open",
                "Open a bookmark by identifier, loading its media when necessary.",
                ToolRisk.Network,
                StringSchema("id"),
                (arguments, cancellationToken) => controller.OpenBookmarkAsync(
                    RequireString(arguments, "id"),
                    cancellationToken)),
            CreateAction(
                "playback.bookmark.rename",
                "Rename a bookmark.",
                ToolRisk.LocalWrite,
                RenameBookmarkSchema(),
                (arguments, cancellationToken) => controller.RenameBookmarkAsync(
                    RequireString(arguments, "id"),
                    RequireString(arguments, "title"),
                    cancellationToken)),
            CreateAction(
                "playback.bookmark.remove",
                "Remove a bookmark by identifier.",
                ToolRisk.Destructive,
                StringSchema("id"),
                (arguments, cancellationToken) => controller.RemoveBookmarkAsync(
                    RequireString(arguments, "id"),
                    cancellationToken)),
            CreateAction(
                "playback.bookmark.clear",
                "Clear bookmarks for the current media, or all bookmarks when requested.",
                ToolRisk.Destructive,
                OptionalBooleanSchema("all"),
                (arguments, cancellationToken) => controller.ClearBookmarksAsync(
                    arguments["all"]?.GetValue<bool>() ?? false,
                    cancellationToken)),
            CreateAction(
                "playback.volume.set",
                "Set playback volume from 0 to 130.",
                ToolRisk.LocalWrite,
                NumberSchema("volume"),
                (arguments, cancellationToken) => controller.SetVolumeAsync(
                    RequireDouble(arguments, "volume"),
                    cancellationToken)),
            CreateAction(
                "playback.brightness.set",
                "Set video brightness from -100 to 100.",
                ToolRisk.LocalWrite,
                NumberSchema("brightness"),
                (arguments, cancellationToken) => controller.SetBrightnessAsync(
                    RequireDouble(arguments, "brightness"),
                    cancellationToken)),
            CreateAction(
                "playback.mute.set",
                "Set playback mute state.",
                ToolRisk.LocalWrite,
                BooleanSchema("muted"),
                (arguments, cancellationToken) => controller.SetMutedAsync(
                    RequireBoolean(arguments, "muted"),
                    cancellationToken)),
            CreateAction(
                "playback.speed.set",
                "Set playback speed from 0.1 to 4.",
                ToolRisk.LocalWrite,
                NumberSchema("speed"),
                (arguments, cancellationToken) => controller.SetSpeedAsync(
                    RequireDouble(arguments, "speed"),
                    cancellationToken)),
            CreateAction(
                "playback.audioDevice.set",
                "Select an available audio output device by its mpv device name.",
                ToolRisk.LocalWrite,
                StringSchema("name"),
                (arguments, cancellationToken) => controller.SelectAudioDeviceAsync(
                    RequireString(arguments, "name"),
                    cancellationToken)),
            CreateAction(
                "playback.filter.video.set",
                "Replace the active mpv video filter chain.",
                ToolRisk.LocalWrite,
                FilterSchema(),
                (arguments, cancellationToken) => controller.SetVideoFiltersAsync(
                    RequirePresentString(arguments, "filters"),
                    cancellationToken)),
            CreateAction(
                "playback.filter.audio.set",
                "Replace the active mpv audio filter chain.",
                ToolRisk.LocalWrite,
                FilterSchema(),
                (arguments, cancellationToken) => controller.SetAudioFiltersAsync(
                    RequirePresentString(arguments, "filters"),
                    cancellationToken)),
            CreateAction(
                "playback.filter.clear",
                "Clear the video, audio, or both mpv filter chains.",
                ToolRisk.LocalWrite,
                FilterClearSchema(),
                (arguments, cancellationToken) => ClearFiltersAsync(
                    controller,
                    RequireString(arguments, "type"),
                    cancellationToken)),
            CreateAction(
                "playback.shader.add",
                "Load a local GLSL or hook shader file.",
                ToolRisk.LocalWrite,
                StringSchema("path"),
                (arguments, cancellationToken) => controller.AddShaderAsync(
                    RequireString(arguments, "path"),
                    cancellationToken)),
            CreateAction(
                "playback.shader.remove",
                "Remove one active user shader by path.",
                ToolRisk.LocalWrite,
                StringSchema("path"),
                (arguments, cancellationToken) => controller.RemoveShaderAsync(
                    RequireString(arguments, "path"),
                    cancellationToken)),
            CreateAction(
                "playback.shader.clear",
                "Clear all active user shaders.",
                ToolRisk.LocalWrite,
                EmptySchema(),
                (_, cancellationToken) => controller.ClearShadersAsync(cancellationToken)),
            CreateAction(
                "playback.track.select",
                "Select or disable a video, audio, or subtitle track.",
                ToolRisk.LocalWrite,
                TrackSelectionSchema(),
                (arguments, cancellationToken) => controller.SelectTrackAsync(
                    RequireTrackType(arguments, "type"),
                    RequireNullableInt64(arguments, "id"),
                    cancellationToken)),
            CreateAction(
                "playback.subtitle.add",
                "Load a local external subtitle file or host-managed subtitle stream.",
                ToolRisk.LocalWrite,
                StringSchema("source", ("select", "boolean")),
                (arguments, cancellationToken) => controller.AddSubtitleAsync(
                    RequireString(arguments, "source"),
                    arguments["select"]?.GetValue<bool>() ?? true,
                    cancellationToken)),
            CreateAction(
                "playback.subtitle.visibility.set",
                "Show or hide the selected subtitle track.",
                ToolRisk.LocalWrite,
                BooleanSchema("visible"),
                (arguments, cancellationToken) => controller.SetSubtitleVisibleAsync(
                    RequireBoolean(arguments, "visible"),
                    cancellationToken)),
            CreateAction(
                "playback.subtitle.delay.set",
                "Set subtitle delay in seconds.",
                ToolRisk.LocalWrite,
                NumberSchema("seconds"),
                (arguments, cancellationToken) => controller.SetSubtitleDelayAsync(
                    RequireDouble(arguments, "seconds"),
                    cancellationToken)),
            CreateAction(
                "playback.subtitle.style.set",
                "Set common runtime subtitle style values.",
                ToolRisk.LocalWrite,
                SubtitleStyleSchema(),
                (arguments, cancellationToken) => ApplySubtitleStyleAsync(
                    controller,
                    arguments,
                    cancellationToken)),
        ];
    }

    private static IToolHandler CreateAction(
        string name,
        string description,
        ToolRisk risk,
        JsonObject schema,
        Func<JsonObject, CancellationToken, ValueTask> action) =>
        new DelegateTool(
            Descriptor(name, description, risk, schema),
            async (arguments, _, cancellationToken) =>
            {
                await action(arguments, cancellationToken).ConfigureAwait(false);
                return ToolResult.Success();
            });

    private static ToolDescriptor Descriptor(
        string name,
        string description,
        ToolRisk risk,
        JsonObject inputSchema) =>
        new(name, description, risk, inputSchema, new JsonObject { ["type"] = "object" });

    private static JsonObject EmptySchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
    };

    private static JsonObject StringSchema(string name, params (string Name, string Type)[] optional) =>
        PropertySchema(name, "string", optional);

    private static JsonObject BooleanSchema(string name) => PropertySchema(name, "boolean");

    private static JsonObject OptionalBooleanSchema(string name) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { [name] = TypeSchema("boolean") },
        ["additionalProperties"] = false,
    };

    private static JsonObject NumberSchema(string name, params (string Name, string Type)[] optional) =>
        PropertySchema(name, "number", optional);

    private static JsonObject IntegerSchema(string name) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray(name),
        ["properties"] = new JsonObject
        {
            [name] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject PropertySchema(
        string requiredName,
        string requiredType,
        params (string Name, string Type)[] optional)
    {
        var properties = new JsonObject { [requiredName] = TypeSchema(requiredType) };
        foreach (var item in optional)
        {
            properties[item.Name] = item.Type == "integer"
                ? new JsonObject { ["type"] = "integer", ["minimum"] = 0 }
                : TypeSchema(item.Type);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray(requiredName),
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject TypeSchema(string type) => new() { ["type"] = type };

    private static JsonObject SourcesSchema(params (string Name, string Type)[] optional)
    {
        var properties = new JsonObject
        {
            ["sources"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 10_000,
                ["items"] = TypeSchema("string"),
            },
        };
        foreach (var item in optional)
        {
            properties[item.Name] = item.Type == "integer"
                ? new JsonObject { ["type"] = "integer", ["minimum"] = 0 }
                : TypeSchema(item.Type);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("sources"),
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject MovePlaylistItemSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("index", "newIndex"),
        ["properties"] = new JsonObject
        {
            ["index"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["newIndex"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject AddBookmarkSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 },
            ["positionSeconds"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject AbLoopSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["startSeconds"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 },
            ["endSeconds"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 },
        },
        ["minProperties"] = 1,
        ["additionalProperties"] = false,
    };

    private static JsonObject ScreenshotSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = TypeSchema("string"),
            ["mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("video", "subtitles", "window"),
            },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject FilterSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("filters"),
        ["properties"] = new JsonObject
        {
            ["filters"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32_768 },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject FilterClearSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("type"),
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("video", "audio", "all"),
            },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject ColorSettingsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["targetColorspaceHint"] = EnumSchema("auto", "yes", "no"),
            ["targetColorspaceHintMode"] = EnumSchema("target", "source", "source-dynamic"),
            ["targetPrimaries"] = EnumSchema(
                "auto", "bt.470m", "bt.601-525", "bt.601-625", "bt.709", "bt.2020", "apple", "adobe",
                "prophoto", "cie1931", "dci-p3", "display-p3", "v-gamut", "s-gamut"),
            ["targetTransfer"] = EnumSchema(
                "auto", "bt.1886", "srgb", "linear", "gamma1.8", "gamma2.0", "gamma2.2", "gamma2.4",
                "gamma2.6", "gamma2.8", "prophoto", "pq", "hlg", "v-log", "s-log1", "s-log2", "scrgb"),
            ["targetPeak"] = new JsonObject { ["type"] = new JsonArray("string", "number") },
            ["toneMapping"] = EnumSchema(
                "auto", "clip", "mobius", "reinhard", "hable", "bt.2390", "gamma", "linear", "spline",
                "bt.2446a", "st2094-40", "st2094-10"),
            ["gamutMappingMode"] = EnumSchema(
                "auto", "clip", "perceptual", "relative", "saturation", "absolute", "desaturate", "darken",
                "warn", "linear"),
            ["hdrComputePeak"] = EnumSchema("auto", "yes", "no"),
        },
        ["minProperties"] = 1,
        ["additionalProperties"] = false,
    };

    private static JsonObject RuntimePropertySchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("name", "value"),
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
            ["value"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32_768 },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject RuntimeCommandSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("command"),
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
            ["arguments"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = 64,
                ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32_768 },
            },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject EnumSchema(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(static value => (JsonNode)JsonValue.Create(value)!).ToArray()),
    };

    private static JsonObject RenameBookmarkSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("id", "title"),
        ["properties"] = new JsonObject
        {
            ["id"] = TypeSchema("string"),
            ["title"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject TrackSelectionSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("type", "id"),
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("video", "audio", "subtitle"),
            },
            ["id"] = new JsonObject
            {
                ["type"] = new JsonArray("integer", "null"),
                ["minimum"] = 1,
            },
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject SubtitleStyleSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["fontSize"] = new JsonObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 9000,
                ["description"] = "Default text font size; ASS scripts can supply their own. Use scale to resize styled subtitles." },
            ["scale"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 100,
                ["description"] = "Subtitle size multiplier, including ASS and bitmap subtitles. 1 means 100%." },
            ["position"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 150 },
        },
        ["minProperties"] = 1,
        ["additionalProperties"] = false,
    };

    private static string RequireString(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"'{name}' is required.")
            : value;
    }

    private static string RequirePresentString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() ?? throw new ArgumentException($"'{name}' is required.");

    private static PlaybackScreenshotMode ReadScreenshotMode(JsonObject arguments) =>
        arguments["mode"]?.GetValue<string>()?.ToLowerInvariant() switch
        {
            null or "subtitles" => PlaybackScreenshotMode.Subtitles,
            "video" => PlaybackScreenshotMode.Video,
            "window" => PlaybackScreenshotMode.Window,
            _ => throw new ArgumentException("'mode' must be video, subtitles, or window."),
        };

    private static double RequireDouble(JsonObject arguments, string name)
    {
        if (arguments[name] is JsonValue value)
        {
            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue<long>(out var integerValue))
            {
                return integerValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
        }

        throw new ArgumentException($"'{name}' is required and must be a number.");
    }

    private static string? ReadOptionalString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>();

    private static double? ReadOptionalDouble(JsonObject arguments, string name) =>
        arguments[name] is null ? null : RequireDouble(arguments, name);

    private static bool RequireBoolean(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<bool>() ?? throw new ArgumentException($"'{name}' is required.");

    private static int RequireInt32(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<int>() ?? throw new ArgumentException($"'{name}' is required and must be an integer.");

    private static int ReadInt32(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<int>() ?? 0;

    private static IReadOnlyList<string> RequireStrings(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonArray array || array.Count == 0)
        {
            throw new ArgumentException($"'{name}' is required and must be a non-empty array.");
        }

        return array.Select(node => node?.GetValue<string>() is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Every '{name}' item must be a non-empty string.")).ToList();
    }

    private static IReadOnlyList<string> ReadStrings(JsonObject arguments, string name)
    {
        if (arguments[name] is null)
        {
            return [];
        }

        if (arguments[name] is not JsonArray array)
        {
            throw new ArgumentException($"'{name}' must be an array of strings.");
        }

        return array.Select(node => node?.GetValue<string>() ??
            throw new ArgumentException($"Every '{name}' item must be a string.")).ToList();
    }

    private static long? RequireNullableInt64(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node))
        {
            throw new ArgumentException($"'{name}' is required.");
        }

        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
        }

        throw new ArgumentException($"'{name}' must be an integer or null.");
    }

    private static PlaybackTrackType RequireTrackType(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>()?.ToLowerInvariant() switch
        {
            "video" => PlaybackTrackType.Video,
            "audio" => PlaybackTrackType.Audio,
            "subtitle" => PlaybackTrackType.Subtitle,
            _ => throw new ArgumentException($"'{name}' must be video, audio, or subtitle."),
        };

    private static async ValueTask ApplySubtitleStyleAsync(
        IPlaybackController controller,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        double? fontSize = arguments["fontSize"] is null ? null : RequireDouble(arguments, "fontSize");
        double? scale = arguments["scale"] is null ? null : RequireDouble(arguments, "scale");
        double? position = arguments["position"] is null ? null : RequireDouble(arguments, "position");
        if (fontSize is null && scale is null && position is null)
        {
            throw new ArgumentException("At least one subtitle style value is required.", nameof(arguments));
        }

        if (fontSize is { } fontSizeValueToValidate &&
            (!double.IsFinite(fontSizeValueToValidate) || fontSizeValueToValidate is < 1 or > 9000))
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "Subtitle font size must be between 1 and 9000.");
        }

        if (scale is { } scaleValueToValidate &&
            (!double.IsFinite(scaleValueToValidate) || scaleValueToValidate is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "Subtitle scale must be between 0 and 100.");
        }

        if (position is { } positionValueToValidate &&
            (!double.IsFinite(positionValueToValidate) || positionValueToValidate is < 0 or > 150))
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "Subtitle position must be between 0 and 150.");
        }

        if (fontSize is { } fontSizeValue)
        {
            await controller.SetSubtitleFontSizeAsync(fontSizeValue, cancellationToken).ConfigureAwait(false);
        }

        if (scale is { } scaleValue)
        {
            await controller.SetSubtitleScaleAsync(scaleValue, cancellationToken).ConfigureAwait(false);
        }

        if (position is { } positionValue)
        {
            await controller.SetSubtitlePositionAsync(positionValue, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask ApplyColorSettingsAsync(
        IPlaybackController controller,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var current = controller.Snapshot.ColorSettings;
        return controller.SetColorSettingsAsync(current with
        {
            TargetColorspaceHint = ReadOptionalString(arguments, "targetColorspaceHint") ?? current.TargetColorspaceHint,
            TargetColorspaceHintMode = ReadOptionalString(arguments, "targetColorspaceHintMode") ?? current.TargetColorspaceHintMode,
            TargetPrimaries = ReadOptionalString(arguments, "targetPrimaries") ?? current.TargetPrimaries,
            TargetTransfer = ReadOptionalString(arguments, "targetTransfer") ?? current.TargetTransfer,
            TargetPeak = ReadOptionalNumberOrString(arguments, "targetPeak") ?? current.TargetPeak,
            ToneMapping = ReadOptionalString(arguments, "toneMapping") ?? current.ToneMapping,
            GamutMappingMode = ReadOptionalString(arguments, "gamutMappingMode") ?? current.GamutMappingMode,
            HdrComputePeak = ReadOptionalString(arguments, "hdrComputePeak") ?? current.HdrComputePeak,
        }, cancellationToken);
    }

    private static string? ReadOptionalNumberOrString(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var integer))
        {
            return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new ArgumentException($"'{name}' must be a string or number.");
    }

    private static async ValueTask ClearFiltersAsync(
        IPlaybackController controller,
        string type,
        CancellationToken cancellationToken)
    {
        switch (type.ToLowerInvariant())
        {
            case "video":
                await controller.SetVideoFiltersAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                break;
            case "audio":
                await controller.SetAudioFiltersAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                break;
            case "all":
                await controller.SetVideoFiltersAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                await controller.SetAudioFiltersAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException("'type' must be video, audio, or all.");
        }
    }

    private static JsonObject ToJson(PlaybackSnapshot snapshot) => new()
    {
        ["state"] = snapshot.State.ToString(),
        ["source"] = DownloadSecretClassifier.RedactUri(snapshot.Source),
        ["title"] = snapshot.Title,
        ["positionSeconds"] = snapshot.PositionSeconds,
        ["durationSeconds"] = snapshot.DurationSeconds,
        ["volume"] = snapshot.Volume,
        ["brightness"] = snapshot.Brightness,
        ["muted"] = snapshot.IsMuted,
        ["subtitleVisible"] = snapshot.IsSubtitleVisible,
        ["subtitleDelaySeconds"] = snapshot.SubtitleDelaySeconds,
        ["subtitleFontSize"] = snapshot.SubtitleFontSize,
        ["subtitleScale"] = snapshot.SubtitleScale,
        ["subtitlePosition"] = snapshot.SubtitlePosition,
        ["speed"] = snapshot.Speed,
        ["abLoopASeconds"] = snapshot.AbLoopASeconds,
        ["abLoopBSeconds"] = snapshot.AbLoopBSeconds,
        ["audioDevice"] = snapshot.AudioDevice,
        ["audioDevices"] = AudioDevicesToJson(snapshot.AudioDevices),
        ["videoFilters"] = snapshot.VideoFilters,
        ["audioFilters"] = snapshot.AudioFilters,
        ["shaders"] = new JsonArray(snapshot.Shaders
            .Select(static shader => (JsonNode)JsonValue.Create(shader)!)
            .ToArray()),
        ["colorSettings"] = ColorSettingsToJson(snapshot.ColorSettings),
        ["diagnostics"] = DiagnosticsToJson(snapshot.Diagnostics),
        ["seekable"] = snapshot.IsSeekable,
        ["videoWidth"] = snapshot.VideoWidth,
        ["videoHeight"] = snapshot.VideoHeight,
        ["errorMessage"] = snapshot.ErrorMessage,
        ["tracks"] = new JsonArray(snapshot.Tracks.Select(static track => (JsonNode)new JsonObject
        {
            ["id"] = track.Id,
            ["type"] = track.Type.ToString(),
            ["title"] = track.Title,
            ["language"] = track.Language,
            ["codec"] = track.Codec,
            ["selected"] = track.IsSelected,
            ["default"] = track.IsDefault,
            ["forced"] = track.IsForced,
            ["external"] = track.IsExternal,
            ["externalFileName"] = DownloadSecretClassifier.RedactUri(track.ExternalFileName),
        }).ToArray()),
        ["currentPlaylistIndex"] = snapshot.CurrentPlaylistIndex,
        ["playlist"] = new JsonArray(snapshot.Playlist.Select(static item => (JsonNode)new JsonObject
        {
            ["index"] = item.Index,
            ["source"] = DownloadSecretClassifier.RedactUri(item.Source),
            ["title"] = item.Title,
            ["current"] = item.IsCurrent,
        }).ToArray()),
        ["recent"] = new JsonArray(snapshot.RecentItems.Select(static item => (JsonNode)new JsonObject
        {
            ["source"] = DownloadSecretClassifier.RedactUri(item.Source),
            ["title"] = item.Title,
            ["positionSeconds"] = item.PositionSeconds,
            ["durationSeconds"] = item.DurationSeconds,
            ["lastPlayedAt"] = item.LastPlayedAt,
            ["completed"] = item.IsCompleted,
        }).ToArray()),
        ["currentChapterIndex"] = snapshot.CurrentChapterIndex,
        ["chapters"] = ChaptersToJson(snapshot.Chapters),
        ["bookmarks"] = BookmarksToJson(snapshot.Bookmarks),
    };

    private static JsonObject ColorSettingsToJson(PlaybackColorSettings settings) => new()
    {
        ["targetColorspaceHint"] = settings.TargetColorspaceHint,
        ["targetColorspaceHintMode"] = settings.TargetColorspaceHintMode,
        ["targetPrimaries"] = settings.TargetPrimaries,
        ["targetTransfer"] = settings.TargetTransfer,
        ["targetPeak"] = settings.TargetPeak,
        ["toneMapping"] = settings.ToneMapping,
        ["gamutMappingMode"] = settings.GamutMappingMode,
        ["hdrComputePeak"] = settings.HdrComputePeak,
    };

    private static JsonObject DiagnosticsToJson(PlaybackDiagnostics diagnostics) => new()
    {
        ["videoCodec"] = diagnostics.VideoCodec,
        ["videoFormat"] = diagnostics.VideoFormat,
        ["decoderPixelFormat"] = diagnostics.DecoderPixelFormat,
        ["hardwarePixelFormat"] = diagnostics.HardwarePixelFormat,
        ["hardwareDecoder"] = diagnostics.HardwareDecoder,
        ["hardwareInterop"] = diagnostics.HardwareInterop,
        ["frameTransferPath"] = diagnostics.FrameTransferPath,
        ["videoOutput"] = diagnostics.VideoOutput,
        ["gpuApi"] = diagnostics.GpuApi,
        ["gpuContext"] = diagnostics.GpuContext,
        ["inputColorMatrix"] = diagnostics.InputColorMatrix,
        ["inputPrimaries"] = diagnostics.InputPrimaries,
        ["inputTransfer"] = diagnostics.InputTransfer,
        ["inputLevels"] = diagnostics.InputLevels,
        ["inputSignalPeak"] = diagnostics.InputSignalPeak,
        ["targetColorMatrix"] = diagnostics.TargetColorMatrix,
        ["targetPrimaries"] = diagnostics.TargetPrimaries,
        ["targetTransfer"] = diagnostics.TargetTransfer,
        ["targetLevels"] = diagnostics.TargetLevels,
        ["targetSignalPeak"] = diagnostics.TargetSignalPeak,
        ["containerFps"] = diagnostics.ContainerFps,
        ["estimatedVideoFps"] = diagnostics.EstimatedVideoFps,
        ["displayFps"] = diagnostics.DisplayFps,
        ["avSyncSeconds"] = diagnostics.AvSyncSeconds,
        ["totalAvSyncChangeSeconds"] = diagnostics.TotalAvSyncChangeSeconds,
        ["decoderDroppedFrames"] = diagnostics.DecoderDroppedFrames,
        ["outputDroppedFrames"] = diagnostics.OutputDroppedFrames,
        ["mistimedFrames"] = diagnostics.MistimedFrames,
        ["delayedFrames"] = diagnostics.DelayedFrames,
        ["videoBitrateBitsPerSecond"] = diagnostics.VideoBitrateBitsPerSecond,
        ["audioBitrateBitsPerSecond"] = diagnostics.AudioBitrateBitsPerSecond,
        ["cacheSpeedBytesPerSecond"] = diagnostics.CacheSpeedBytesPerSecond,
        ["cacheDurationSeconds"] = diagnostics.CacheDurationSeconds,
    };

    private static JsonObject CapabilitiesToJson(PlaybackCapabilityManifest manifest)
    {
        var coverage = PlaybackCapabilityCoverage.Analyze(manifest);
        return new JsonObject
        {
            ["schemaVersion"] = manifest.SchemaVersion,
            ["mpvVersion"] = manifest.MpvVersion,
            ["clientApiVersion"] = manifest.ClientApiVersion,
            ["ffmpegVersion"] = manifest.FfmpegVersion,
            ["platform"] = manifest.Platform,
            ["configuration"] = manifest.Configuration,
            ["coverage"] = new JsonObject
            {
                ["total"] = coverage.Total,
                ["covered"] = coverage.Covered,
                ["propertyTotal"] = coverage.PropertyTotal,
                ["propertyDedicated"] = coverage.PropertyDedicated,
                ["propertyAdvanced"] = coverage.PropertyAdvanced,
                ["propertyBlocked"] = coverage.PropertyBlocked,
                ["commandTotal"] = coverage.CommandTotal,
                ["commandDedicated"] = coverage.CommandDedicated,
                ["commandAdvanced"] = coverage.CommandAdvanced,
                ["commandBlocked"] = coverage.CommandBlocked,
                ["uncovered"] = new JsonArray(coverage.Uncovered
                    .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            },
            ["properties"] = new JsonArray(manifest.Properties.Select(property => (JsonNode?)new JsonObject
            {
                ["name"] = property.Name,
                ["canRead"] = property.CanRead,
                ["canWrite"] = property.CanWrite,
                ["coverage"] = FormatCoverage(property.Coverage),
                ["tools"] = new JsonArray(property.Tools
                    .Select(static tool => (JsonNode?)JsonValue.Create(tool)).ToArray()),
                ["restriction"] = property.Restriction,
            }).ToArray()),
            ["commands"] = new JsonArray(manifest.Commands.Select(command => (JsonNode?)new JsonObject
            {
                ["name"] = command.Name,
                ["canExecute"] = command.CanExecute,
                ["variableArguments"] = command.VariableArguments,
                ["coverage"] = FormatCoverage(command.Coverage),
                ["tools"] = new JsonArray(command.Tools
                    .Select(static tool => (JsonNode?)JsonValue.Create(tool)).ToArray()),
                ["restriction"] = command.Restriction,
                ["arguments"] = new JsonArray(command.Arguments.Select(argument => (JsonNode?)new JsonObject
                {
                    ["name"] = argument.Name,
                    ["type"] = argument.Type,
                    ["optional"] = argument.Optional,
                    ["defaultValue"] = argument.DefaultValue,
                    ["choices"] = new JsonArray(argument.Choices
                        .Select(static choice => (JsonNode?)JsonValue.Create(choice)).ToArray()),
                }).ToArray()),
            }).ToArray()),
        };
    }

    private static string FormatCoverage(PlaybackCapabilityCoverageKind coverage) => coverage switch
    {
        PlaybackCapabilityCoverageKind.DedicatedTool => "dedicated-tool",
        PlaybackCapabilityCoverageKind.AdvancedAccess => "advanced-access",
        PlaybackCapabilityCoverageKind.Blocked => "blocked",
        _ => "unknown",
    };

    private static JsonArray ChaptersToJson(IEnumerable<PlaybackChapter> chapters) =>
        new(chapters.Select(static item => (JsonNode)new JsonObject
        {
            ["index"] = item.Index,
            ["title"] = item.Title,
            ["timeSeconds"] = item.TimeSeconds,
            ["current"] = item.IsCurrent,
        }).ToArray());

    private static JsonArray BookmarksToJson(IEnumerable<PlaybackBookmark> bookmarks) =>
        new(bookmarks.Select(static item => (JsonNode)new JsonObject
        {
            ["id"] = item.Id,
            ["source"] = DownloadSecretClassifier.RedactUri(item.Source),
            ["title"] = item.Title,
            ["positionSeconds"] = item.PositionSeconds,
            ["createdAt"] = item.CreatedAt,
        }).ToArray());

    private static JsonArray AudioDevicesToJson(IEnumerable<PlaybackAudioDevice> devices) =>
        new(devices.Select(static item => (JsonNode)new JsonObject
        {
            ["name"] = item.Name,
            ["description"] = item.Description,
            ["current"] = item.IsCurrent,
        }).ToArray());

    private sealed class DelegateTool(
        ToolDescriptor descriptor,
        Func<JsonObject, ToolCallContext, CancellationToken, ValueTask<ToolResult>> execute) : IToolHandler
    {
        public ToolDescriptor Descriptor { get; } = descriptor;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken) => execute(arguments, context, cancellationToken);
    }
}
