// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Application.Playback;
using CanX.Core.Playback;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackToolTests
{
    [Fact]
    public async Task Tools_ResolveTheActivePlayerForEachCall()
    {
        var first = new StubPlaybackController();
        var second = new StubPlaybackController();
        IPlaybackController active = first;
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(PlaybackToolCatalog.Create(first, (_, _) => ValueTask.FromResult(active)));
        var context = new ToolCallContext("agent", true);

        Assert.True((await registry.InvokeAsync("playback.open", new JsonObject { ["source"] = "https://media.test/first.mp4" }, context)).IsSuccess);
        active = second;
        Assert.True((await registry.InvokeAsync("playback.open", new JsonObject { ["source"] = "https://media.test/second.mp4" }, context)).IsSuccess);
        Assert.True((await registry.InvokeAsync("playback.seek", new JsonObject { ["seconds"] = 12 }, context)).IsSuccess);

        Assert.Equal("https://media.test/first.mp4", first.OpenedSource);
        Assert.Equal("https://media.test/second.mp4", second.OpenedSource);
        Assert.Null(first.LastSeek);
        Assert.Equal(12, second.LastSeek!.Value.Seconds);
    }

    [Fact]
    public async Task PlaybackTools_ExposeStateAndForwardValidatedControls()
    {
        var controller = new StubPlaybackController();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(PlaybackToolCatalog.Create(controller));

        var state = await registry.InvokeAsync("playback.get", null, new ToolCallContext("agent"));
        var capabilities = await registry.InvokeAsync(
            "playback.capabilities",
            null,
            new ToolCallContext("agent"));
        var denied = await registry.InvokeAsync(
            "playback.open",
            new JsonObject { ["source"] = "https://example.test/video.mp4" },
            new ToolCallContext("agent"));
        var opened = await registry.InvokeAsync(
            "playback.open",
            new JsonObject { ["source"] = "https://example.test/video.mp4" },
            new ToolCallContext("agent", true));
        var seeked = await registry.InvokeAsync(
            "playback.seek",
            new JsonObject { ["seconds"] = -5, ["relative"] = true },
            new ToolCallContext("agent", true));
        var brightnessChanged = await registry.InvokeAsync(
            "playback.brightness.set",
            new JsonObject { ["brightness"] = 25 },
            new ToolCallContext("agent", true));
        var trackSelected = await registry.InvokeAsync(
            "playback.track.select",
            new JsonObject { ["type"] = "audio", ["id"] = 2 },
            new ToolCallContext("agent", true));
        var subtitleAdded = await registry.InvokeAsync(
            "playback.subtitle.add",
            new JsonObject { ["source"] = "/tmp/movie.zh.srt", ["select"] = false },
            new ToolCallContext("agent", true));
        var subtitleHidden = await registry.InvokeAsync(
            "playback.subtitle.visibility.set",
            new JsonObject { ["visible"] = false },
            new ToolCallContext("agent", true));
        var subtitleDelayed = await registry.InvokeAsync(
            "playback.subtitle.delay.set",
            new JsonObject { ["seconds"] = -0.5 },
            new ToolCallContext("agent", true));
        var subtitleStyled = await registry.InvokeAsync(
            "playback.subtitle.style.set",
            new JsonObject { ["fontSize"] = 44, ["scale"] = 1.5, ["position"] = 92 },
            new ToolCallContext("agent", true));
        var playlistOpened = await registry.InvokeAsync(
            "playback.playlist.open",
            new JsonObject
            {
                ["sources"] = new JsonArray("/tmp/one.mkv", "/tmp/two.mkv"),
                ["startIndex"] = 1,
            },
            new ToolCallContext("agent", true));
        var folderOpened = await registry.InvokeAsync(
            "playback.folder.open",
            new JsonObject { ["directory"] = "/tmp/media", ["recursive"] = true },
            new ToolCallContext("agent", true));
        var chapters = await registry.InvokeAsync("playback.chapter.list", null, new ToolCallContext("agent"));
        var bookmarks = await registry.InvokeAsync("playback.bookmark.list", null, new ToolCallContext("agent"));
        var chapterOpened = await registry.InvokeAsync(
            "playback.chapter.open",
            new JsonObject { ["index"] = 1 },
            new ToolCallContext("agent", true));
        var bookmarkAdded = await registry.InvokeAsync(
            "playback.bookmark.add",
            new JsonObject { ["title"] = "Scene", ["positionSeconds"] = 25 },
            new ToolCallContext("agent", true));
        var bookmarkRenamed = await registry.InvokeAsync(
            "playback.bookmark.rename",
            new JsonObject { ["id"] = "bookmark-1", ["title"] = "New scene" },
            new ToolCallContext("agent", true));
        var previousFrame = await registry.InvokeAsync(
            "playback.frame.previous",
            null,
            new ToolCallContext("agent", true));
        var abLoopSet = await registry.InvokeAsync(
            "playback.abLoop.set",
            new JsonObject { ["startSeconds"] = 5, ["endSeconds"] = 15 },
            new ToolCallContext("agent", true));
        var screenshot = await registry.InvokeAsync(
            "playback.screenshot",
            new JsonObject { ["path"] = "/tmp/canx-tool.png", ["mode"] = "window" },
            new ToolCallContext("agent", true));
        var audioDeviceChanged = await registry.InvokeAsync(
            "playback.audioDevice.set",
            new JsonObject { ["name"] = "pulse/speakers" },
            new ToolCallContext("agent", true));
        var videoFiltersChanged = await registry.InvokeAsync(
            "playback.filter.video.set",
            new JsonObject { ["filters"] = "lavfi=[hflip]" },
            new ToolCallContext("agent", true));
        var filtersCleared = await registry.InvokeAsync(
            "playback.filter.clear",
            new JsonObject { ["type"] = "all" },
            new ToolCallContext("agent", true));
        var shaderAdded = await registry.InvokeAsync(
            "playback.shader.add",
            new JsonObject { ["path"] = "/tmp/test-shader.glsl" },
            new ToolCallContext("agent", true));
        var diagnostics = await registry.InvokeAsync(
            "playback.diagnostics.get",
            null,
            new ToolCallContext("agent"));
        var colorChanged = await registry.InvokeAsync(
            "playback.color.set",
            new JsonObject { ["toneMapping"] = "bt.2390", ["targetPeak"] = 1000 },
            new ToolCallContext("agent", true));
        var propertyRead = await registry.InvokeAsync(
            "playback.property.get",
            new JsonObject { ["name"] = "video-sync" },
            new ToolCallContext("agent"));
        var propertyDenied = await registry.InvokeAsync(
            "playback.property.set",
            new JsonObject { ["name"] = "video-sync", ["value"] = "display-resample" },
            new ToolCallContext("agent"));
        var propertyChanged = await registry.InvokeAsync(
            "playback.property.set",
            new JsonObject { ["name"] = "video-sync", ["value"] = "display-resample" },
            new ToolCallContext("agent", true));
        var commandExecuted = await registry.InvokeAsync(
            "playback.command.execute",
            new JsonObject { ["command"] = "show-text", ["arguments"] = new JsonArray("CanX", "500") },
            new ToolCallContext("agent", true));

        Assert.True(state.IsSuccess);
        Assert.True(capabilities.IsSuccess);
        var capabilityJson = Assert.IsType<JsonObject>(capabilities.Value);
        Assert.Equal(1, capabilityJson["schemaVersion"]?.GetValue<int>());
        Assert.Equal(3, Assert.IsType<JsonObject>(capabilityJson["coverage"])["total"]?.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(capabilityJson["coverage"])["uncovered"]));
        Assert.Equal(2, Assert.IsType<JsonArray>(capabilityJson["properties"]).Count);
        Assert.Single(Assert.IsType<JsonArray>(capabilityJson["commands"]));
        Assert.Equal("Paused", Assert.IsType<JsonObject>(state.Value)["state"]?.GetValue<string>());
        Assert.Equal(
            "https://example.test/video.mp4?<redacted>",
            Assert.IsType<JsonObject>(state.Value)["source"]?.GetValue<string>());
        Assert.False(Assert.IsType<JsonObject>(state.Value)["subtitleVisible"]?.GetValue<bool>());
        var tracks = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(state.Value)["tracks"]);
        var subtitleTrack = Assert.IsType<JsonObject>(Assert.Single(tracks));
        Assert.Equal("ass", subtitleTrack["codec"]?.GetValue<string>());
        Assert.True(subtitleTrack["external"]?.GetValue<bool>());
        Assert.Equal("permission_denied", denied.ErrorCode);
        Assert.True(opened.IsSuccess);
        Assert.Equal("https://example.test/video.mp4", controller.OpenedSource);
        Assert.True(seeked.IsSuccess, seeked.ErrorMessage);
        Assert.Equal((-5d, true), controller.LastSeek);
        Assert.True(brightnessChanged.IsSuccess, brightnessChanged.ErrorMessage);
        Assert.Equal(25, controller.Brightness);
        Assert.True(trackSelected.IsSuccess, trackSelected.ErrorMessage);
        Assert.Equal((PlaybackTrackType.Audio, 2L), controller.SelectedTrack.GetValueOrDefault());
        Assert.True(subtitleAdded.IsSuccess, subtitleAdded.ErrorMessage);
        Assert.Equal(("/tmp/movie.zh.srt", false), controller.AddedSubtitle);
        Assert.True(subtitleHidden.IsSuccess, subtitleHidden.ErrorMessage);
        Assert.False(controller.SubtitleVisible);
        Assert.True(subtitleDelayed.IsSuccess, subtitleDelayed.ErrorMessage);
        Assert.Equal(-0.5, controller.SubtitleDelay);
        Assert.True(subtitleStyled.IsSuccess, subtitleStyled.ErrorMessage);
        Assert.Equal(44, controller.SubtitleFontSize);
        Assert.Equal(1.5, controller.SubtitleScale);
        Assert.Equal(92, controller.SubtitlePosition);
        Assert.True(playlistOpened.IsSuccess, playlistOpened.ErrorMessage);
        Assert.Equal(1, controller.OpenedPlaylist?.StartIndex);
        Assert.Equal(["/tmp/one.mkv", "/tmp/two.mkv"], controller.OpenedPlaylist?.Sources);
        Assert.True(folderOpened.IsSuccess, folderOpened.ErrorMessage);
        Assert.Equal(("/tmp/media", true), controller.OpenedFolder);
        var playlist = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(state.Value)["playlist"]);
        Assert.Equal("https://example.test/list.m3u8?<redacted>",
            Assert.IsType<JsonObject>(Assert.Single(playlist))["source"]?.GetValue<string>());
        Assert.Single(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(state.Value)["recent"]));
        Assert.Single(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(chapters.Value)["chapters"]));
        var bookmarkJson = Assert.IsType<JsonObject>(Assert.Single(
            Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(bookmarks.Value)["bookmarks"])));
        Assert.Equal("https://example.test/video.mp4?<redacted>", bookmarkJson["source"]?.GetValue<string>());
        Assert.True(chapterOpened.IsSuccess, chapterOpened.ErrorMessage);
        Assert.Equal(1, controller.OpenedChapter);
        Assert.True(bookmarkAdded.IsSuccess, bookmarkAdded.ErrorMessage);
        Assert.Equal(("Scene", 25d), controller.AddedBookmark);
        Assert.True(bookmarkRenamed.IsSuccess, bookmarkRenamed.ErrorMessage);
        Assert.Equal(("bookmark-1", "New scene"), controller.RenamedBookmark);
        Assert.True(previousFrame.IsSuccess, previousFrame.ErrorMessage);
        Assert.True(controller.LastFrameStepBackward);
        Assert.True(abLoopSet.IsSuccess, abLoopSet.ErrorMessage);
        Assert.Equal((5d, 15d), controller.AbLoop);
        Assert.Equal("/tmp/canx-tool.png", Assert.IsType<JsonObject>(screenshot.Value)["path"]?.GetValue<string>());
        Assert.Equal(("/tmp/canx-tool.png", PlaybackScreenshotMode.Window), controller.Screenshot);
        Assert.True(audioDeviceChanged.IsSuccess, audioDeviceChanged.ErrorMessage);
        Assert.Equal("pulse/speakers", controller.AudioDevice);
        Assert.True(videoFiltersChanged.IsSuccess, videoFiltersChanged.ErrorMessage);
        Assert.True(filtersCleared.IsSuccess, filtersCleared.ErrorMessage);
        Assert.Equal(string.Empty, controller.VideoFilters);
        Assert.Equal(string.Empty, controller.AudioFilters);
        Assert.True(shaderAdded.IsSuccess, shaderAdded.ErrorMessage);
        Assert.Equal("/tmp/test-shader.glsl", controller.AddedShader);
        Assert.True(diagnostics.IsSuccess, diagnostics.ErrorMessage);
        Assert.Equal(
            "nvdec",
            Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(diagnostics.Value)["diagnostics"])["hardwareDecoder"]?.GetValue<string>());
        Assert.True(colorChanged.IsSuccess, colorChanged.ErrorMessage);
        Assert.Equal("bt.2390", controller.ColorSettings?.ToneMapping);
        Assert.Equal("1000", controller.ColorSettings?.TargetPeak);
        Assert.True(propertyRead.IsSuccess, propertyRead.ErrorMessage);
        Assert.Equal("audio", Assert.IsType<JsonObject>(propertyRead.Value)["value"]?.GetValue<string>());
        Assert.Equal("permission_denied", propertyDenied.ErrorCode);
        Assert.True(propertyChanged.IsSuccess, propertyChanged.ErrorMessage);
        Assert.Equal(("video-sync", "display-resample"), controller.RuntimeProperty);
        Assert.True(commandExecuted.IsSuccess, commandExecuted.ErrorMessage);
        Assert.Equal("show-text", controller.RuntimeCommand?.Command);
        Assert.Equal(["CanX", "500"], controller.RuntimeCommand?.Arguments);
    }

    private sealed class StubPlaybackController : IPlaybackController
    {
        public bool IsStarted => true;

        public PlaybackSnapshot Snapshot { get; } = new()
        {
            State = PlaybackState.Paused,
            Source = "https://user:password@example.test/video.mp4?token=secret",
            Title = "video.mkv",
            DurationSeconds = 60,
            IsSubtitleVisible = false,
            SubtitleDelaySeconds = -0.25,
            SubtitleFontSize = 42,
            SubtitlePosition = 94,
            Tracks =
            [
                new PlaybackTrack
                {
                    Id = 3,
                    Type = PlaybackTrackType.Subtitle,
                    Codec = "ass",
                    IsSelected = true,
                    IsExternal = true,
                    ExternalFileName = "https://user:password@example.test/sub.ass?token=secret",
                },
            ],
            Playlist =
            [
                new PlaybackPlaylistItem
                {
                    Index = 0,
                    Source = "https://user:password@example.test/list.m3u8?token=secret",
                    Title = "List item",
                    IsCurrent = true,
                },
            ],
            CurrentPlaylistIndex = 0,
            RecentItems =
            [
                new PlaybackRecentItem
                {
                    Source = "/media/earlier.mkv",
                    Title = "Earlier",
                    PositionSeconds = 30,
                    DurationSeconds = 90,
                    LastPlayedAt = DateTimeOffset.Parse("2026-09-10T08:00:00Z"),
                },
            ],
            Chapters =
            [
                new PlaybackChapter { Index = 1, Title = "Main", TimeSeconds = 30, IsCurrent = true },
            ],
            CurrentChapterIndex = 1,
            AbLoopASeconds = 5,
            AbLoopBSeconds = 15,
            AudioDevice = "pulse/speakers",
            AudioDevices =
            [
                new PlaybackAudioDevice
                {
                    Name = "pulse/speakers",
                    Description = "Speakers",
                    IsCurrent = true,
                },
            ],
            VideoFilters = "lavfi=[hflip]",
            Shaders = ["/tmp/test-shader.glsl"],
            Diagnostics = new PlaybackDiagnostics { HardwareDecoder = "nvdec", GpuApi = "opengl" },
            ColorSettings = new PlaybackColorSettings { ToneMapping = "auto" },
            Bookmarks =
            [
                new PlaybackBookmark
                {
                    Id = "bookmark-1",
                    Source = "https://user:password@example.test/video.mp4?token=secret",
                    Title = "Scene",
                    PositionSeconds = 25,
                    CreatedAt = DateTimeOffset.Parse("2026-09-10T08:05:00Z"),
                },
            ],
        };

        public PlaybackCapabilityManifest Capabilities { get; } = new()
        {
            MpvVersion = "mpv 0.41.0",
            ClientApiVersion = "2.3",
            FfmpegVersion = "7.1",
            Platform = "linux",
            Properties =
            [
                new PlaybackPropertyCapability
                {
                    Name = "volume",
                    CanRead = true,
                    CanWrite = true,
                    Coverage = PlaybackCapabilityCoverageKind.DedicatedTool,
                    Tools = ["playback.volume.set"],
                },
                new PlaybackPropertyCapability
                {
                    Name = "video-sync",
                    CanRead = true,
                    CanWrite = true,
                    Coverage = PlaybackCapabilityCoverageKind.AdvancedAccess,
                },
            ],
            Commands =
            [
                new PlaybackCommandCapability
                {
                    Name = "show-text",
                    CanExecute = true,
                    Coverage = PlaybackCapabilityCoverageKind.AdvancedAccess,
                    Arguments =
                    [
                        new PlaybackCommandArgumentCapability { Name = "text", Type = "String" },
                    ],
                },
            ],
        };

        public string? OpenedSource { get; private set; }

        public (double Seconds, bool Relative)? LastSeek { get; private set; }

        public double? Brightness { get; private set; }

        public (PlaybackTrackType Type, long? Id)? SelectedTrack { get; private set; }

        public (string Source, bool Select)? AddedSubtitle { get; private set; }

        public bool SubtitleVisible { get; private set; } = true;

        public double SubtitleDelay { get; private set; }

        public double SubtitleFontSize { get; private set; }

        public double SubtitleScale { get; private set; } = 1;

        public double SubtitlePosition { get; private set; }

        public (IReadOnlyList<string> Sources, int StartIndex)? OpenedPlaylist { get; private set; }

        public (string Directory, bool Recursive)? OpenedFolder { get; private set; }

        public int? OpenedChapter { get; private set; }

        public (string? Title, double? PositionSeconds)? AddedBookmark { get; private set; }

        public (string Id, string Title)? RenamedBookmark { get; private set; }

        public bool LastFrameStepBackward { get; private set; }

        public (double? Start, double? End)? AbLoop { get; private set; }

        public (string? Path, PlaybackScreenshotMode Mode)? Screenshot { get; private set; }

        public string? AudioDevice { get; private set; }

        public string? VideoFilters { get; private set; }

        public string? AudioFilters { get; private set; }

        public string? AddedShader { get; private set; }

        public PlaybackColorSettings? ColorSettings { get; private set; }

        public (string Name, string Value)? RuntimeProperty { get; private set; }

        public (string Command, IReadOnlyList<string> Arguments)? RuntimeCommand { get; private set; }

        public void ConfigureStartupOptions(IReadOnlyDictionary<string, string> options)
        {
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenAsync(string source, CancellationToken cancellationToken = default)
        {
            OpenedSource = source;
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenMediaSourceAsync(string playbackSource, string historySource, string? mediaSourceProfileId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenMediaSourcePlaylistAsync(IReadOnlyList<PlaybackMediaSource> sources, int startIndex = 0, string? mediaSourceProfileId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenPlaylistAsync(
            IReadOnlyList<string> sources,
            int startIndex = 0,
            CancellationToken cancellationToken = default)
        {
            OpenedPlaylist = (sources, startIndex);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddToPlaylistAsync(IReadOnlyList<string> sources, bool playNow = false, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenFolderAsync(
            string directory,
            bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            OpenedFolder = (directory, recursive);
            return ValueTask.CompletedTask;
        }

        public ValueTask PlayPlaylistItemAsync(int index, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PlayNextAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetAutoPlayNextAsync(bool autoPlayNext, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PlayPreviousAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemovePlaylistItemAsync(int index, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask MovePlaylistItemAsync(int index, int newIndex, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ClearPlaylistAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenRecentAsync(string source, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveRecentAsync(string source, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveRecentAsync(string source, bool isMediaSource, string? mediaSourceProfileId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ClearRecentAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SeekToChapterAsync(int index, CancellationToken cancellationToken = default)
        {
            OpenedChapter = index;
            return ValueTask.CompletedTask;
        }

        public ValueTask AddBookmarkAsync(string? title = null, double? positionSeconds = null, CancellationToken cancellationToken = default)
        {
            AddedBookmark = (title, positionSeconds);
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenBookmarkAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RenameBookmarkAsync(string id, string title, CancellationToken cancellationToken = default)
        {
            RenamedBookmark = (id, title);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveBookmarkAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ClearBookmarksAsync(bool all = false, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetPausedAsync(bool paused, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TogglePauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SeekAsync(
            double seconds,
            bool relative = false,
            bool exact = true,
            CancellationToken cancellationToken = default)
        {
            LastSeek = (seconds, relative);
            return ValueTask.CompletedTask;
        }

        public ValueTask StepFrameAsync(bool backward = false, CancellationToken cancellationToken = default)
        {
            LastFrameStepBackward = backward;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAbLoopAsync(double? startSeconds = null, double? endSeconds = null, CancellationToken cancellationToken = default)
        {
            AbLoop = (startSeconds, endSeconds);
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAbLoopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<string> TakeScreenshotAsync(
            string? outputPath = null,
            PlaybackScreenshotMode mode = PlaybackScreenshotMode.Subtitles,
            CancellationToken cancellationToken = default)
        {
            Screenshot = (outputPath, mode);
            return ValueTask.FromResult(outputPath ?? "/tmp/canx.png");
        }

        public ValueTask SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetBrightnessAsync(double brightness, CancellationToken cancellationToken = default)
        {
            Brightness = brightness;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetSpeedAsync(double speed, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SelectAudioDeviceAsync(string name, CancellationToken cancellationToken = default)
        {
            AudioDevice = name;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetVideoFiltersAsync(string filters, CancellationToken cancellationToken = default)
        {
            VideoFilters = filters;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAudioFiltersAsync(string filters, CancellationToken cancellationToken = default)
        {
            AudioFilters = filters;
            return ValueTask.CompletedTask;
        }

        public ValueTask AddShaderAsync(string path, CancellationToken cancellationToken = default)
        {
            AddedShader = path;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveShaderAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ClearShadersAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetColorSettingsAsync(PlaybackColorSettings settings, CancellationToken cancellationToken = default)
        {
            ColorSettings = settings;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string> GetRuntimePropertyAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(name == "video-sync" ? "audio" : string.Empty);

        public ValueTask SetRuntimePropertyAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            RuntimeProperty = (name, value);
            return ValueTask.CompletedTask;
        }

        public ValueTask ExecuteCommandAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            RuntimeCommand = (command, arguments);
            return ValueTask.CompletedTask;
        }

        public ValueTask SelectTrackAsync(
            PlaybackTrackType type,
            long? trackId,
            CancellationToken cancellationToken = default,
            bool remember = true)
        {
            SelectedTrack = (type, trackId);
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreVideoOutputAsync(long trackId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask AddSubtitleAsync(
            string source,
            bool select = true,
            CancellationToken cancellationToken = default,
            PlaybackExternalSubtitleSource? persistentSource = null,
            bool remember = true)
        {
            AddedSubtitle = (source, select);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetSubtitleVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            SubtitleVisible = visible;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default)
        {
            SubtitleDelay = seconds;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetSubtitleFontSizeAsync(double size, CancellationToken cancellationToken = default)
        {
            SubtitleFontSize = size;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetSubtitleScaleAsync(double scale, CancellationToken cancellationToken = default)
        {
            SubtitleScale = scale;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetSubtitlePositionAsync(double position, CancellationToken cancellationToken = default)
        {
            SubtitlePosition = position;
            return ValueTask.CompletedTask;
        }

        public ValueTask SendKeyDownAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendKeyUpAsync(string? key = null, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendKeyPressAsync(
            string key,
            double scale = 1,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetMousePositionAsync(int x, int y, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteWatchLaterAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
