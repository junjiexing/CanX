// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Collections.Specialized;
using CanX.App.ViewModels;
using CanX.Application.Playback;
using CanX.Core.Configuration;
using CanX.Core.Events;
using CanX.Core.Playback;
using CanX.Playback;
using CanX.Playback.Engine;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackControllerTests
{
    [Fact]
    public async Task SleepInhibitionTracksBackgroundPlaybackWithoutDrainingTheUiContext()
    {
        using var media = new TemporaryMediaFile();
        var context = new QueuedPlaybackContext();
        var bus = new InMemoryEventBus();
        var controller = new PlaybackController(new FakePlaybackEngine(), bus);
        await using var model = new PlaybackViewModel(controller, bus, context);
        var active = 0;
        model.ConfigureDisplaySleepInhibition(_ =>
        {
            Interlocked.Increment(ref active);
            return Task.FromResult<IDisposable>(new SleepTestLease(() => Interlocked.Decrement(ref active)));
        });
        await model.InitializeAsync();
        await model.OpenAsync(media.Path);
        await WaitUntilAsync(() => Volatile.Read(ref active) == 1);
        // The UI still has its initial snapshot, just as when Android has stopped dispatching it.
        Assert.Equal(PlaybackState.Idle, model.State);
        await controller.SetPausedAsync(true);
        await WaitUntilAsync(() => Volatile.Read(ref active) == 0);
        await controller.SetPausedAsync(false);
        await WaitUntilAsync(() => Volatile.Read(ref active) == 1);
        await model.DisposeAsync();
        Assert.Equal(0, Volatile.Read(ref active));
        context.Drain();
        Assert.Equal(0, Volatile.Read(ref active));
    }

    private sealed class SleepTestLease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    [Theory]
    [InlineData("delay", -2.5, 1)]
    [InlineData("size", 1.5, 1.25)]
    [InlineData("position", 40, 80)]
    [InlineData("volume", 73, 25)]
    [InlineData("volume", 0, 130)]
    public async Task PlaybackAdjustments_StayVisibleAcrossQueuedSnapshotsAndReachTheEngine(string setting, double target, double external)
    {
        using var media = new TemporaryMediaFile();
        var context = new QueuedPlaybackContext();
        var bus = new InMemoryEventBus();
        var engine = new FakePlaybackEngine();
        var controller = new PlaybackController(engine, bus);
        await using var model = new PlaybackViewModel(controller, bus, context);
        await model.InitializeAsync();
        await model.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        context.Drain();
        double Read() => setting switch
        {
            "volume" => model.Volume,
            "delay" => model.SubtitleDelaySeconds,
            "size" => model.SubtitleScale,
            _ => model.SubtitlePosition,
        };
        var property = setting switch { "volume" => "volume", "delay" => "sub-delay", "size" => "sub-scale", _ => "sub-pos" };
        var observed = new List<double>();
        model.PropertyChanged += (_, _) => observed.Add(Read());
        bus.Publish(new PlaybackChanged(controller.Snapshot));
        await (setting switch
        {
            "volume" => model.SetVolumeAsync(target),
            "delay" => model.SetSubtitleDelayAsync(target),
            "size" => model.SetSubtitleScaleAsync(target),
            _ => model.SetSubtitlePositionAsync(target),
        });

        Assert.Equal(target, Read());
        Assert.True(engine.TryGetDouble(property, out var actual));
        Assert.Equal(target, actual);
        context.Drain();
        Assert.All(observed, value => Assert.Equal(target, value));
        if (setting != "volume") Assert.Equal(setting switch
        {
            "delay" => "-2.5 秒",
            "size" => "150%",
            _ => "40%",
        }, setting switch
        {
            "delay" => model.SubtitleDelayText,
            "size" => model.SubtitleScaleText,
            _ => model.SubtitlePositionText,
        });

        await (setting switch
        {
            "volume" => controller.SetVolumeAsync(external),
            "delay" => controller.SetSubtitleDelayAsync(external),
            "size" => controller.SetSubtitleScaleAsync(external),
            _ => controller.SetSubtitlePositionAsync(external),
        });
        context.Drain();
        Assert.Equal(external, Read());
    }

    [Theory]
    [InlineData("no", "scale")]
    [InlineData("yes", "scale")]
    [InlineData("scale", "scale")]
    [InlineData("force", "force")]
    [InlineData("strip", "strip")]
    public async Task SubtitleScale_EnablesStyledTextScalingWithoutReplacingFontStyle(string initialOverride, string expectedOverride)
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        engine.Command("set", "sub-ass-override", initialOverride);
        engine.Command("set", "sub-font-size", "44");

        await controller.SetSubtitleScaleAsync(2);
        await controller.SetSubtitleScaleAsync(1.5);

        Assert.True(engine.TryGetString("sub-ass-override", out var actualOverride));
        Assert.Equal(expectedOverride, actualOverride);
        Assert.True(engine.TryGetDouble("sub-font-size", out var fontSize));
        Assert.Equal(44, fontSize);
        Assert.True(engine.TryGetDouble("sub-scale", out var scale));
        Assert.Equal(1.5, scale);
        Assert.Equal(1.5, controller.Snapshot.SubtitleScale);
        Assert.True(engine.TryGetFlag("sub-scale-signs", out var scalesPositionedText));
        Assert.True(scalesPositionedText);
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-scale-signs", "yes"]));
    }

    [Fact]
    public async Task SubtitleAdjustments_WorkWhenMpvLacksPositionedTextScaling()
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine(supportsSubtitleScaleSigns: false);
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        await controller.SetSubtitleScaleAsync(1.5);
        await controller.SetSubtitlePositionAsync(92);

        Assert.DoesNotContain(engine.Commands, static command => command.SequenceEqual(["set", "sub-scale-signs", "yes"]));
        Assert.True(engine.TryGetDouble("sub-scale", out var scale));
        Assert.Equal(1.5, scale);
        Assert.True(engine.TryGetDouble("sub-pos", out var position));
        Assert.Equal(92, position);
        Assert.Equal(1.5, controller.Snapshot.SubtitleScale);
        Assert.Equal(92, controller.Snapshot.SubtitlePosition);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task SubtitleScale_RejectsInvalidValuesBeforeWriting(double scale)
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await controller.SetSubtitleScaleAsync(scale));
        Assert.Empty(engine.Commands);
    }

    [Fact]
    public async Task PlaybackHistory_UnchangedSnapshotsPreserveRowsAndSelection()
    {
        var context = new QueuedPlaybackContext();
        var bus = new InMemoryEventBus();
        await using var model = new PlaybackViewModel(new PlaybackController(new FakePlaybackEngine(), bus), bus, context);
        var item = new PlaybackRecentItem { Source = Path.Combine(Path.GetTempPath(), "history", "one.mp4"), Title = "One" };
        var snapshot = new PlaybackSnapshot { State = PlaybackState.Playing, Source = item.Source, RecentItems = [item] };
        bus.Publish(new PlaybackChanged(snapshot));
        context.Drain();
        var rows = model.RecentItems;
        var row = Assert.Single(rows);
        var folders = model.RecentFolders;
        var folder = Assert.Single(folders);
        model.SelectedRecentItem = row;
        var historyNotifications = 0;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(model.RecentItems) or nameof(model.RecentFolders) or nameof(model.SelectedRecentItem))
                historyNotifications++;
        };

        for (var tick = 1; tick <= 20; tick++)
        {
            bus.Publish(new PlaybackChanged(snapshot with { PositionSeconds = tick, RecentItems = [item with { }] }));
            context.Drain();
        }

        Assert.Equal(0, historyNotifications);
        Assert.Same(rows, model.RecentItems);
        Assert.Same(row, Assert.Single(model.RecentItems));
        Assert.Same(folders, model.RecentFolders);
        Assert.Same(folder, Assert.Single(model.RecentFolders));
        Assert.Same(row, model.SelectedRecentItem);
    }

    [Fact]
    public async Task PlaybackHistory_ProgressUpdatesExistingRowsWithoutCollectionChanges()
    {
        var context = new QueuedPlaybackContext();
        var bus = new InMemoryEventBus();
        await using var model = new PlaybackViewModel(new PlaybackController(new FakePlaybackEngine(), bus), bus, context);
        var item = new PlaybackRecentItem
        {
            Source = Path.Combine(Path.GetTempPath(), "history", "one.mp4"), Title = "One",
            PositionSeconds = 1, DurationSeconds = 100, LastPlayedAt = DateTimeOffset.UtcNow,
        };
        var snapshot = new PlaybackSnapshot { State = PlaybackState.Playing, Source = item.Source, RecentItems = [item] };
        bus.Publish(new PlaybackChanged(snapshot));
        context.Drain();
        var row = Assert.Single(model.RecentItems);
        var folder = Assert.Single(model.RecentFolders);
        model.SelectedRecentItem = row;
        var collectionChanges = 0;
        ((INotifyCollectionChanged)model.RecentItems).CollectionChanged += (_, _) => collectionChanges++;
        ((INotifyCollectionChanged)model.RecentFolders).CollectionChanged += (_, _) => collectionChanges++;
        var rowChanges = new List<string?>();
        row.PropertyChanged += (_, args) => rowChanges.Add(args.PropertyName);
        var updated = item with { PositionSeconds = 25, Title = "Updated title", LastPlayedAt = item.LastPlayedAt.AddMinutes(1) };

        bus.Publish(new PlaybackChanged(snapshot with { RecentItems = [updated] }));
        context.Drain();

        Assert.Same(row, Assert.Single(model.RecentItems));
        Assert.Same(folder, Assert.Single(model.RecentFolders));
        Assert.Same(row, model.SelectedRecentItem);
        Assert.Equal(25, row.Progress);
        Assert.Equal("0:25 / 1:40", row.ProgressText);
        Assert.Equal("Updated title", row.Title);
        Assert.Equal(updated.LastPlayedAt, folder.LastPlayedAt);
        Assert.Equal("继续播放", model.SelectedRecentActionText);
        Assert.Contains(nameof(row.Progress), rowChanges);
        Assert.Contains(nameof(row.ProgressText), rowChanges);
        Assert.Contains(nameof(row.LastPlayedText), rowChanges);
        Assert.Equal(0, collectionChanges);

        bus.Publish(new PlaybackChanged(snapshot with { RecentItems = [updated with { IsCompleted = true }] }));
        context.Drain();
        Assert.Equal("重新播放", model.SelectedRecentActionText);
        Assert.Equal(100, row.Progress);
        Assert.Equal(0, collectionChanges);
    }

    [Fact]
    public async Task PlaybackHistory_MergesAddedRemovedAndReorderedRecordsWithoutReset()
    {
        var context = new QueuedPlaybackContext();
        var bus = new InMemoryEventBus();
        await using var model = new PlaybackViewModel(new PlaybackController(new FakePlaybackEngine(), bus), bus, context);
        var one = new PlaybackRecentItem
        {
            Source = "smb://server/share/one.mp4", IsMediaSource = true, MediaSourceProfileId = "alice",
            PositionSeconds = 10, DurationSeconds = 100, LastPlayedAt = DateTimeOffset.UtcNow,
        };
        var two = one with { MediaSourceProfileId = "bob", PositionSeconds = 20 };
        var snapshot = new PlaybackSnapshot { RecentItems = [one, two] };
        bus.Publish(new PlaybackChanged(snapshot));
        context.Drain();
        var first = model.RecentItems[0];
        var second = model.RecentItems[1];
        model.SelectedRecentItem = first;
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)model.RecentItems).CollectionChanged += (_, args) => changes.Add(args.Action);
        ((INotifyCollectionChanged)model.RecentFolders).CollectionChanged += (_, args) => changes.Add(args.Action);
        var third = one with { Source = "smb://server/new/three.mp4" };

        bus.Publish(new PlaybackChanged(snapshot with { RecentItems = [two with { PositionSeconds = 40 }, third, one] }));
        context.Drain();
        Assert.Same(second, model.RecentItems[0]);
        Assert.Equal(40, second.Progress);
        Assert.Same(first, model.RecentItems[2]);
        Assert.Same(first, model.SelectedRecentItem);
        Assert.Equal(10, first.Progress);

        bus.Publish(new PlaybackChanged(snapshot with { RecentItems = [two, third] }));
        context.Drain();
        Assert.Null(model.SelectedRecentItem);
        Assert.Same(second, model.RecentItems[0]);
        Assert.Equal(2, model.RecentFolders.Count);
        bus.Publish(new PlaybackChanged(snapshot with { RecentItems = [] }));
        context.Drain();
        Assert.Empty(model.RecentItems);
        Assert.Empty(model.RecentFolders);
        Assert.False(model.HasRecentItems);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    [Fact]
    public async Task IndependentPlayers_IsolateControlsAndShareHistoryAcrossClose()
    {
        using var one = new TemporaryMediaFile();
        using var two = new TemporaryMediaFile();
        var disk = new StubPlaybackStateStore();
        var shared = new CanX.Infrastructure.Playback.SharedPlaybackStateStore(disk);
        var firstState = shared.CreateSession();
        var secondState = shared.CreateSession();
        await using var first = new PlaybackController(new FakePlaybackEngine(), new InMemoryEventBus(), firstState);
        await using var second = new PlaybackController(new FakePlaybackEngine(), new InMemoryEventBus(), secondState);
        await first.StartAsync();
        await second.StartAsync();
        await first.OpenAsync(one.Path);
        await second.OpenAsync(two.Path);
        secondState.Activate();
        await WaitUntilAsync(() => first.Snapshot.State == PlaybackState.Playing && second.Snapshot.State == PlaybackState.Playing);

        await first.SetPausedAsync(true);
        await first.SetVolumeAsync(25);
        Assert.Equal(PlaybackState.Playing, second.Snapshot.State);
        Assert.NotEqual(25, second.Snapshot.Volume);
        await WaitUntilAsync(() => first.Snapshot.RecentItems.Count == 2);
        await first.DisposeAsync();
        await second.WriteWatchLaterAsync();

        Assert.Equal(PlaybackState.Playing, second.Snapshot.State);
        Assert.Equal(two.Path, second.Snapshot.Source);
        Assert.Equal(2, disk.LastSaved!.RecentItems.Count);
        Assert.Equal(two.Path, Assert.Single(disk.LastSaved.PlaylistSources));
    }

    [Fact]
    public async Task PlaybackViewModel_IgnoresQueuedStopAfterDisposal()
    {
        var context = new QueuedPlaybackContext();
        var bus = new InMemoryEventBus();
        var controller = new PlaybackController(new FakePlaybackEngine(), bus);
        await using var model = new PlaybackViewModel(controller, bus, context);
        bus.Publish(new PlaybackChanged(new PlaybackSnapshot { Source = "canx://stream/test/movie.m3u8", State = PlaybackState.Loading }));
        context.Drain();
        var source = model.Source;
        bus.Publish(new PlaybackChanged(new PlaybackSnapshot { State = PlaybackState.Idle }));

        await model.DisposeAsync();
        context.Drain();

        Assert.Equal(source, model.Source);
    }

    private sealed class QueuedPlaybackContext : SynchronizationContext
    {
        private readonly Queue<Action> _actions = new();
        public override void Post(SendOrPostCallback callback, object? state) => _actions.Enqueue(() => callback(state));
        public void Drain()
        {
            while (_actions.TryDequeue(out var action)) action();
        }
    }

    [Fact]
    public async Task PlaybackViewModel_IsPlayingTracksPlaybackState()
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine();
        var eventBus = new InMemoryEventBus();
        var controller = new PlaybackController(engine, eventBus);
        await using var viewModel = new PlaybackViewModel(controller, eventBus);
        var notifications = new ConcurrentQueue<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => notifications.Enqueue(eventArgs.PropertyName);

        await viewModel.InitializeAsync();
        Assert.False(viewModel.IsPlaying);
        Assert.Equal("1.0x", viewModel.SpeedText);

        await viewModel.SetSpeedAsync(0.1);
        await WaitUntilAsync(() => viewModel.Speed == 0.1);
        Assert.Equal("0.1x", viewModel.SpeedText);
        Assert.False(viewModel.CanDecreaseSpeed);
        Assert.True(viewModel.CanIncreaseSpeed);

        await viewModel.SetSpeedAsync(4);
        await WaitUntilAsync(() => viewModel.Speed == 4);
        Assert.Equal("4.0x", viewModel.SpeedText);
        Assert.True(viewModel.CanDecreaseSpeed);
        Assert.False(viewModel.CanIncreaseSpeed);

        await viewModel.OpenAsync(media.Path);
        await WaitUntilAsync(() => viewModel.IsPlaying);
        Assert.True(viewModel.IsPlaying);

        await viewModel.TogglePauseAsync();
        await WaitUntilAsync(() => !viewModel.IsPlaying);
        Assert.False(viewModel.IsPlaying);
        Assert.Contains(nameof(PlaybackViewModel.IsPlaying), notifications);
    }

    [Fact]
    public async Task PlaybackViewModel_KeepsPlaylistRowsWhilePlaybackSnapshotRefreshes()
    {
        var context = new QueuedPlaybackContext();
        var eventBus = new InMemoryEventBus();
        await using var controller = new PlaybackController(new FakePlaybackEngine(), eventBus);
        await using var viewModel = new PlaybackViewModel(controller, eventBus, context);
        var playlist = new List<PlaybackPlaylistItem>
        {
            new PlaybackPlaylistItem { Index = 0, Source = "/show/01.mkv", Title = "01.mkv", IsCurrent = true },
            new PlaybackPlaylistItem { Index = 1, Source = "/show/02.mkv", Title = "02.mkv" },
        };
        var snapshot = controller.Snapshot with { Playlist = playlist };
        eventBus.Publish(new PlaybackChanged(snapshot));
        context.Drain();
        var rows = viewModel.PlaylistItems;
        var firstRow = rows[0];

        eventBus.Publish(new PlaybackChanged(snapshot with
        {
            Playlist = playlist.Select(static item => item with { }).ToList(),
            PositionSeconds = 12,
        }));
        context.Drain();
        Assert.Same(rows, viewModel.PlaylistItems);
        Assert.Same(firstRow, viewModel.PlaylistItems[0]);

        eventBus.Publish(new PlaybackChanged(snapshot with
        {
            Playlist = [playlist[0], playlist[1] with { Title = "Episode 2" }],
        }));
        context.Drain();
        Assert.Equal("Episode 2", viewModel.PlaylistItems[1].Title);
    }

    [Fact]
    public async Task PlaybackViewModel_SubtitleLocationFollowsCurrentMediaSource()
    {
        using var local = new TemporaryMediaFile();
        var eventBus = new InMemoryEventBus();
        await using var controller = new PlaybackController(new FakePlaybackEngine(), eventBus);
        await using var viewModel = new PlaybackViewModel(controller, eventBus);
        await viewModel.InitializeAsync();
        const string stream =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv";
        var original = new Uri("smb://server.example/Movies/movie.mkv");

        await viewModel.OpenMediaSourceAsync(stream, original, "saved-profile");
        await WaitUntilAsync(() => viewModel.Source == stream);
        Assert.Equal(original, viewModel.CurrentMediaSourceUri);
        Assert.Equal("saved-profile", viewModel.CurrentMediaSourceProfileId);

        await viewModel.OpenAsync(local.Path);
        await WaitUntilAsync(() => viewModel.Source == local.Path);
        Assert.Null(viewModel.CurrentMediaSourceUri);
        Assert.Null(viewModel.CurrentMediaSourceProfileId);
    }

    [Fact]
    public async Task IdleRefresh_DoesNotRepublishUnchangedSnapshot()
    {
        var engine = new FakePlaybackEngine();
        var eventBus = new InMemoryEventBus();
        var published = 0;
        using var subscription = eventBus.Subscribe<PlaybackChanged>(
            _ => Interlocked.Increment(ref published));
        await using var controller = new PlaybackController(engine, eventBus);

        await controller.StartAsync();
        await Task.Delay(300);
        var stableCount = Volatile.Read(ref published);

        await Task.Delay(500);

        Assert.Equal(stableCount, Volatile.Read(ref published));
    }

    [Fact]
    public async Task StartupOptions_AreCopiedAndPassedToEngine()
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        var options = new Dictionary<string, string> { ["hwdec"] = "nvdec" };

        controller.ConfigureStartupOptions(options);
        options["hwdec"] = "no";
        await controller.StartAsync();

        Assert.Equal("nvdec", engine.InitializedOptions["hwdec"]);
        Assert.Equal("mpv 0.41.0-test", controller.Capabilities.MpvVersion);
        Assert.Equal(4, controller.Capabilities.Properties.Count);
        Assert.Contains(controller.Capabilities.Properties, static property =>
            property.Name == "path" && !property.CanRead && !property.CanWrite &&
            property.Coverage == PlaybackCapabilityCoverageKind.DedicatedTool);
        Assert.Contains(controller.Capabilities.Properties, static property =>
            property.Name == "video-sync" && property.CanRead && property.CanWrite &&
            property.Coverage == PlaybackCapabilityCoverageKind.AdvancedAccess);
        Assert.Contains(controller.Capabilities.Commands, static command =>
            command.Name == "quit" && !command.CanExecute &&
            command.Coverage == PlaybackCapabilityCoverageKind.Blocked);
        Assert.Throws<InvalidOperationException>(() => controller.ConfigureStartupOptions(options));
    }

    [Fact]
    public async Task MpvInput_IsForwardedUsingNativeInputCommands()
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();

        await controller.SendKeyDownAsync("Ctrl+RIGHT");
        await controller.SendKeyUpAsync("Ctrl+RIGHT");
        await controller.SendKeyPressAsync("WHEEL_UP", 0.5);
        await controller.SetMousePositionAsync(640, 360);
        await controller.WriteWatchLaterAsync();

        Assert.Contains(engine.Commands, command => command.SequenceEqual(["keydown", "Ctrl+RIGHT"]));
        Assert.Contains(engine.Commands, command => command.SequenceEqual(["keyup", "Ctrl+RIGHT"]));
        Assert.Contains(engine.Commands, command => command.SequenceEqual(["keypress", "WHEEL_UP", "0.5"]));
        Assert.Contains(engine.Commands, command => command.SequenceEqual(["mouse", "640", "360"]));
        Assert.Contains(engine.Commands, command => command.SequenceEqual(["write-watch-later-config"]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await controller.SendKeyDownAsync(string.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.SendKeyPressAsync("WHEEL_UP", 0));
    }

    [Fact]
    public async Task OpenAndControls_AreReflectedInPublishedState()
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine();
        var eventBus = new InMemoryEventBus();
        var published = new List<PlaybackSnapshot>();
        using var subscription = eventBus.Subscribe<PlaybackChanged>(message => published.Add(message.Snapshot));
        await using var controller = new PlaybackController(engine, eventBus);

        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        Assert.Equal(media.Path, controller.Snapshot.Source);
        Assert.Equal("Fixture video", controller.Snapshot.Title);
        Assert.Equal(120, controller.Snapshot.DurationSeconds);
        Assert.Equal(1280, controller.Snapshot.VideoWidth);
        Assert.Equal(720, controller.Snapshot.VideoHeight);
        Assert.Equal(2, controller.Snapshot.Tracks.Count);
        Assert.Contains(published, static snapshot => snapshot.State == PlaybackState.Loading);

        await controller.SetPausedAsync(true);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Paused);
        await controller.SeekAsync(42);
        await controller.SeekAsync(43, exact: false);
        await controller.SetVolumeAsync(75);
        await controller.SetBrightnessAsync(20);
        await controller.SetMutedAsync(true);
        await controller.SetSpeedAsync(1.5);
        await controller.StopAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Idle);
        await controller.SetPausedAsync(true);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
        await controller.TogglePauseAsync();

        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["seek", "42", "absolute+exact"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["seek", "43", "absolute+keyframes"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "volume", "75"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "brightness", "20"]));
        Assert.Equal(20, controller.Snapshot.Brightness);
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "mute", "yes"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "speed", "1.5"]));
        Assert.Equal(2, engine.Commands.Count(static command => command[0] == "loadfile"));
    }

    [Fact]
    public async Task ConcurrentPauseToggles_AreSerializedAgainstPublishedState()
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        await Task.WhenAll(
            controller.TogglePauseAsync().AsTask(),
            controller.TogglePauseAsync().AsTask());

        Assert.Equal(PlaybackState.Playing, controller.Snapshot.State);
        Assert.Equal(
            ["yes", "no"],
            engine.Commands
                .Where(static command => command is ["set", "pause", _])
                .Select(static command => command[2])
                .ToArray());
    }

    [Fact]
    public async Task TrackAndSubtitleControls_UpdateMpvAndPublishedState()
    {
        using var media = new TemporaryMediaFile();
        using var subtitle = new TemporaryMediaFile(".srt");
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        await controller.SelectTrackAsync(PlaybackTrackType.Audio, null);
        const string streamedSubtitle = "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.srt";
        await controller.AddSubtitleAsync(streamedSubtitle, select: false);
        await controller.AddSubtitleAsync(subtitle.Path);
        await controller.SetSubtitleVisibleAsync(false);
        await controller.SetSubtitleDelayAsync(-0.5);
        await controller.SetSubtitleFontSizeAsync(44);
        await controller.SetSubtitlePositionAsync(92);
        await controller.RestoreVideoOutputAsync(1);

        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "aid", "no"]));
        Assert.Contains(engine.Commands, command => command.SequenceEqual(["sub-add", subtitle.Path, "select"]));
        Assert.Contains(engine.Commands, static command =>
            command.SequenceEqual(["sub-add", streamedSubtitle, "auto"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-visibility", "no"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-delay", "-0.5"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-font-size", "44"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-pos", "92"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "vid", "1"]));
        Assert.False(controller.Snapshot.IsSubtitleVisible);
        Assert.Equal(-0.5, controller.Snapshot.SubtitleDelaySeconds);
        Assert.Equal(44, controller.Snapshot.SubtitleFontSize);
        Assert.Equal(92, controller.Snapshot.SubtitlePosition);
        Assert.Contains(controller.Snapshot.Tracks, static track =>
            track.Type == PlaybackTrackType.Subtitle && track.IsExternal && track.IsSelected);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await controller.SelectTrackAsync(PlaybackTrackType.Audio, 999));
    }

    [Fact]
    public async Task ReopeningVideo_RestoresManualSubtitleAudioTrackAndSubtitleAdjustments()
    {
        using var directory = new TemporaryMediaDirectory();
        var video = directory.CreateFile("Movie.mkv");
        var subtitle = directory.CreateFile("Unrelated.ass");
        var store = new StubPlaybackStateStore();
        await using (var first = new PlaybackController(new FakePlaybackEngine(), new InMemoryEventBus(), store))
        {
            await first.StartAsync();
            await first.OpenAsync(video);
            await WaitUntilAsync(() => first.Snapshot.State == PlaybackState.Playing);
            await first.SelectTrackAsync(PlaybackTrackType.Audio, null);
            await first.AddSubtitleAsync(subtitle);
            await first.SetSubtitleVisibleAsync(false);
            await first.SetSubtitleDelayAsync(-0.5);
            await first.SetSubtitleScaleAsync(1.2);
            await first.SetSubtitlePositionAsync(88);
            await first.StopAsync();
        }

        var saved = Assert.Single(store.LastSaved!.RecentItems).Preferences!;
        Assert.True(saved.AudioTrack!.IsDisabled);
        Assert.Equal(subtitle, Assert.Single(saved.ExternalSubtitles).Source);
        Assert.Equal(subtitle, saved.SubtitleTrack!.ExternalSubtitle!.Source);

        var engine = new FakePlaybackEngine();
        var bus = new InMemoryEventBus();
        await using var second = new PlaybackController(engine, bus,
            new StubPlaybackStateStore { Loaded = store.LastSaved });
        await using var model = new PlaybackViewModel(second, bus);
        model.ConfigureAutomaticSubtitles(() => new MpvSettings { SubtitleAuto = "exact" });
        await model.InitializeAsync();
        await model.OpenAsync(video);
        await WaitUntilAsync(() => engine.Commands.Any(command =>
            command.SequenceEqual(["sub-add", subtitle, "select"])));

        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "aid", "no"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-visibility", "no"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-delay", "-0.5"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-scale", "1.2"]));
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "sub-pos", "88"]));
    }

    [Fact]
    public async Task ReopeningNetworkVideo_ReloadsSubtitleFromStableSource()
    {
        const string firstStream =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/Movie.mkv";
        const string secondStream =
            "canx://stream/abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789/Movie.mkv";
        const string firstSubtitle =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/Chosen.ass";
        const string secondSubtitle =
            "canx://stream/abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789/Chosen.ass";
        const string videoUri = "smb://server.example/show/Movie.mkv";
        const string subtitleUri = "smb://server.example/show/Chosen.ass";
        var stable = new PlaybackExternalSubtitleSource
        {
            Source = subtitleUri,
            MediaSourceProfileId = "saved-smb",
        };
        var store = new StubPlaybackStateStore();
        await using (var first = new PlaybackController(new FakePlaybackEngine(), new InMemoryEventBus(), store))
        {
            await first.StartAsync();
            await first.OpenMediaSourceAsync(firstStream, videoUri, "saved-smb");
            await WaitUntilAsync(() => first.Snapshot.State == PlaybackState.Playing);
            await first.AddSubtitleAsync(firstSubtitle, persistentSource: stable);
            await first.StopAsync();
        }

        var persisted = Assert.Single(store.LastSaved!.RecentItems).Preferences!;
        Assert.Equal(stable, Assert.Single(persisted.ExternalSubtitles));
        Assert.DoesNotContain("canx://", System.Text.Json.JsonSerializer.Serialize(store.LastSaved),
            StringComparison.OrdinalIgnoreCase);

        var engine = new FakePlaybackEngine();
        var bus = new InMemoryEventBus();
        await using var second = new PlaybackController(engine, bus,
            new StubPlaybackStateStore { Loaded = store.LastSaved });
        await using var model = new PlaybackViewModel(second, bus);
        model.ConfigureAutomaticSubtitles(() => new MpvSettings { SubtitleAuto = "exact" });
        model.ConfigureRememberedSubtitleLoader((source, select, token) =>
        {
            Assert.Equal(stable, source);
            return model.AddAutomaticSubtitleAsync(secondSubtitle, select, token, source);
        });
        await model.InitializeAsync();
        await model.OpenMediaSourceAsync(secondStream, new Uri(videoUri), "saved-smb");
        await WaitUntilAsync(() => engine.Commands.Any(command =>
            command.SequenceEqual(["sub-add", secondSubtitle, "select"])));
    }

    [Fact]
    public async Task UnavailableRememberedSubtitle_FallsBackToAutomaticMatching()
    {
        using var directory = new TemporaryMediaDirectory();
        var video = directory.CreateFile("Movie.mkv");
        var automatic = directory.CreateFile("Movie.zh.ass");
        var missing = Path.Combine(Path.GetDirectoryName(video)!, "Missing.ass");
        var external = new PlaybackExternalSubtitleSource { Source = missing };
        var engine = new FakePlaybackEngine();
        var bus = new InMemoryEventBus();
        await using var controller = new PlaybackController(engine, bus, new StubPlaybackStateStore
        {
            Loaded = new PlaybackPersistedState
            {
                RecentItems = [new PlaybackRecentItem
                {
                    Source = video,
                    LastPlayedAt = DateTimeOffset.UtcNow,
                    Preferences = new PlaybackMediaPreferences
                    {
                        SubtitleTrack = new PlaybackTrackPreference { Id = 3, ExternalSubtitle = external },
                        ExternalSubtitles = [external],
                    },
                }],
            },
        });
        await using var model = new PlaybackViewModel(controller, bus);
        model.ConfigureAutomaticSubtitles(() => new MpvSettings
        {
            SubtitleAuto = "exact",
            SubtitleLanguages = "zh-CN,en",
        });
        await model.InitializeAsync();
        await model.OpenAsync(video);

        await WaitUntilAsync(() => engine.Commands.Any(command =>
            command.SequenceEqual(["sub-add", automatic, "select"])));
    }

    [Fact]
    public async Task TemporaryExternalSubtitle_IsNotRememberedAsSelectedTrack()
    {
        using var directory = new TemporaryMediaDirectory();
        var video = directory.CreateFile("Movie.mkv");
        var store = new StubPlaybackStateStore();
        await using var controller = new PlaybackController(new FakePlaybackEngine(),
            new InMemoryEventBus(), store);
        await controller.StartAsync();
        await controller.OpenAsync(video);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.AddSubtitleAsync(
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/Chosen.ass");
        await controller.SelectTrackAsync(PlaybackTrackType.Subtitle, 3);
        await controller.StopAsync();

        var preferences = Assert.Single(store.LastSaved!.RecentItems).Preferences;
        Assert.Null(preferences?.SubtitleTrack);
    }

    [Fact]
    public async Task PlaylistOperations_StaySynchronizedWithMpvAndPersistence()
    {
        using var first = new TemporaryMediaFile(".mp4");
        using var second = new TemporaryMediaFile(".mkv");
        var engine = new FakePlaybackEngine();
        var stateStore = new StubPlaybackStateStore();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus(), stateStore);
        await controller.StartAsync();

        await controller.OpenPlaylistAsync([first.Path, second.Path]);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        Assert.Equal(2, controller.Snapshot.Playlist.Count);
        Assert.Equal(0, controller.Snapshot.CurrentPlaylistIndex);
        Assert.True(controller.Snapshot.Playlist[0].IsCurrent);
        await controller.PlayNextAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == second.Path);
        Assert.Equal(1, controller.Snapshot.CurrentPlaylistIndex);

        await controller.MovePlaylistItemAsync(1, 0);
        Assert.Equal(second.Path, controller.Snapshot.Playlist[0].Source);
        Assert.True(controller.Snapshot.Playlist[0].IsCurrent);
        await controller.RemovePlaylistItemAsync(1);
        Assert.Single(controller.Snapshot.Playlist);
        await controller.ClearPlaylistAsync();
        Assert.Single(controller.Snapshot.Playlist);
        Assert.NotNull(stateStore.LastSaved);
        Assert.Single(stateStore.LastSaved!.PlaylistSources);
    }

    [Fact]
    public async Task OpeningVideo_QueuesOnlySiblingVideosInNameOrder()
    {
        using var directory = new TemporaryMediaDirectory();
        var first = directory.CreateFile("01.mp4");
        var second = directory.CreateFile("02.mkv");
        directory.CreateFile("02.zh.srt");
        directory.CreateFile("03.mp3");
        await using var controller = new PlaybackController(new FakePlaybackEngine(), new InMemoryEventBus());
        await controller.StartAsync();

        await controller.OpenAsync(second);
        await WaitUntilAsync(() => controller.Snapshot.Source == second &&
            controller.Snapshot.State == PlaybackState.Playing);
        Assert.Equal([first, second], controller.Snapshot.Playlist.Select(static item => item.Source));
        Assert.Equal(1, controller.Snapshot.CurrentPlaylistIndex);

        await controller.PlayPreviousAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == first);
        await controller.PlayNextAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == second);
    }

    [Fact]
    public async Task OpeningThirdEpisodeFromHistory_PreservesFolderOrderAndAdjacentNavigation()
    {
        using var directory = new TemporaryMediaDirectory();
        const string prefix = "[Nekomoe kissaten&VCB-Studio] Dungeon Meshi";
        var episodes = Enumerable.Range(1, 4)
            .Select(number => directory.CreateFile($"{prefix} [{number:00}][Ma10p_1080p][x265_flac].mkv"))
            .ToArray();
        var store = new StubPlaybackStateStore
        {
            Loaded = new PlaybackPersistedState
            {
                RecentItems = [new PlaybackRecentItem
                {
                    Source = episodes[2],
                    LastPlayedAt = DateTimeOffset.UtcNow,
                }],
            },
        };
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus(), store);
        await controller.StartAsync();

        await controller.OpenRecentAsync(episodes[2]);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(episodes, controller.Snapshot.Playlist.Select(static item => item.Source));
        Assert.Equal(episodes, engine.GetPlaylist().Select(static item => item.Source));
        Assert.Equal(2, controller.Snapshot.CurrentPlaylistIndex);
        Assert.Contains(engine.Commands, static command =>
            command.SequenceEqual(["playlist-move", "0", "3"]));

        await controller.PlayPreviousAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == episodes[1]);
        await controller.PlayNextAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == episodes[2]);
        await controller.PlayNextAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == episodes[3]);
    }

    [Fact]
    public async Task MovingPlaylistItemForward_UsesMpvTargetEntrySemantics()
    {
        using var directory = new TemporaryMediaDirectory();
        var files = Enumerable.Range(1, 4)
            .Select(number => directory.CreateFile($"{number:00}.mkv"))
            .ToArray();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenPlaylistAsync(files);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        await controller.MovePlaylistItemAsync(0, 3);
        Assert.Equal([files[1], files[2], files[3], files[0]],
            engine.GetPlaylist().Select(static item => item.Source));
        Assert.Equal(engine.GetPlaylist().Select(static item => item.Source),
            controller.Snapshot.Playlist.Select(static item => item.Source));
        Assert.Contains(engine.Commands, static command =>
            command.SequenceEqual(["playlist-move", "0", "4"]));

        await controller.MovePlaylistItemAsync(3, 1);
        Assert.Equal([files[1], files[0], files[2], files[3]],
            engine.GetPlaylist().Select(static item => item.Source));
    }

    [Fact]
    public async Task SiblingVideoSwitch_LoadsEachVideosMatchingSubtitle()
    {
        using var directory = new TemporaryMediaDirectory();
        var first = directory.CreateFile("01.mkv");
        var second = directory.CreateFile("02.mkv");
        var firstSubtitle = directory.CreateFile("01.zh.srt");
        var secondSubtitle = directory.CreateFile("02.zh.srt");
        var engine = new FakePlaybackEngine();
        var bus = new InMemoryEventBus();
        await using var controller = new PlaybackController(engine, bus);
        await using var model = new PlaybackViewModel(controller, bus);
        model.ConfigureAutomaticSubtitles(() => new MpvSettings
        {
            SubtitleAuto = "exact", SubtitleLanguages = "zh-CN,en",
        });
        await model.InitializeAsync();

        await model.OpenAsync(first);
        await WaitUntilAsync(() => engine.Commands.Any(command =>
            command.Length > 1 && command[0] == "sub-add" && command[1] == firstSubtitle));
        await model.PlayNextAsync();
        await WaitUntilAsync(() => model.Source == second && engine.Commands.Any(command =>
            command.Length > 1 && command[0] == "sub-add" && command[1] == secondSubtitle));
    }

    [Fact]
    public async Task MediaSourcePlaylist_UsesEachOriginalUriForHistoryAndSubtitleLocation()
    {
        const string firstStream =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/01.mkv";
        const string secondStream =
            "canx://stream/abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789/02.mkv";
        var firstUri = new Uri("smb://server.example/show/01.mkv");
        var secondUri = new Uri("smb://server.example/show/02.mkv");
        var bus = new InMemoryEventBus();
        await using var controller = new PlaybackController(new FakePlaybackEngine(), bus);
        await using var model = new PlaybackViewModel(controller, bus);
        model.ConfigureAutomaticSubtitles(() => new MpvSettings { SubtitleAuto = "exact" });
        var subtitleRequests = new List<string>();
        model.ConfigureMediaSourceSubtitleLoader((source, _) =>
        {
            subtitleRequests.Add(source);
            return Task.CompletedTask;
        });
        await model.InitializeAsync();

        await model.OpenMediaSourcePlaylistAsync(
            [new(firstStream, firstUri.AbsoluteUri), new(secondStream, secondUri.AbsoluteUri)],
            mediaSourceProfileId: "saved-profile");
        await WaitUntilAsync(() => model.Source == firstStream && subtitleRequests.Contains(firstStream));
        Assert.Equal(firstUri, model.CurrentMediaSourceUri);
        await model.PlayNextAsync();
        await WaitUntilAsync(() => model.Source == secondStream && subtitleRequests.Contains(secondStream));
        Assert.Equal(secondUri, model.CurrentMediaSourceUri);
        await controller.SeekAsync(12);
        await controller.WriteWatchLaterAsync();
        Assert.Contains(controller.Snapshot.RecentItems, item => item.Source == secondUri.AbsoluteUri &&
            item.MediaSourceProfileId == "saved-profile");
        await model.PlayPreviousAsync();
        await WaitUntilAsync(() => model.Source == firstStream);
        Assert.Equal(firstUri, model.CurrentMediaSourceUri);
    }

    [Fact]
    public async Task PlaybackEndSetting_ControlsAdvanceAndCanChangeDuringPlayback()
    {
        using var directory = new TemporaryMediaDirectory();
        var first = directory.CreateFile("01.mkv");
        var second = directory.CreateFile("02.mkv");
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        controller.ConfigureStartupOptions(MpvOptionConfiguration.Build(new MpvSettings { AutoPlayNext = false }));
        await controller.StartAsync();
        await controller.OpenAsync(first);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        engine.FinishCurrent();
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Ended);
        Assert.Equal(first, controller.Snapshot.Source);
        await controller.PlayNextAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == second &&
            controller.Snapshot.State == PlaybackState.Playing);

        await controller.SetAutoPlayNextAsync(true);
        await controller.PlayPreviousAsync();
        await WaitUntilAsync(() => controller.Snapshot.Source == first);
        engine.FinishCurrent();
        await WaitUntilAsync(() => controller.Snapshot.Source == second &&
            controller.Snapshot.State == PlaybackState.Playing);
    }

    [Fact]
    public async Task ChaptersAndBookmarks_SeekRenameAndPersist()
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine();
        var stateStore = new StubPlaybackStateStore();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus(), stateStore);
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing &&
            controller.Snapshot.Chapters.Count == 2);

        await controller.SeekToChapterAsync(1);
        Assert.Equal(1, controller.Snapshot.CurrentChapterIndex);
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "chapter", "1"]));

        await controller.AddBookmarkAsync("Favorite scene", 42);
        var bookmark = Assert.Single(controller.Snapshot.Bookmarks);
        Assert.Equal("Favorite scene", bookmark.Title);
        Assert.Equal(42, bookmark.PositionSeconds);
        Assert.Single(stateStore.LastSaved!.Bookmarks);

        await controller.RenameBookmarkAsync(bookmark.Id, "Renamed scene");
        Assert.Equal("Renamed scene", Assert.Single(controller.Snapshot.Bookmarks).Title);
        await controller.OpenBookmarkAsync(bookmark.Id);
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["seek", "42", "absolute+exact"]));
        await controller.RemoveBookmarkAsync(bookmark.Id);
        Assert.Empty(controller.Snapshot.Bookmarks);
        Assert.Empty(stateStore.LastSaved!.Bookmarks);
    }

    [Fact]
    public async Task NextChapter_FollowsPlaybackPositionAndStopsAtLastChapter()
    {
        using var media = new TemporaryMediaFile();
        var bus = new InMemoryEventBus();
        var engine = new FakePlaybackEngine();
        var controller = new PlaybackController(engine, bus);
        await using var model = new PlaybackViewModel(controller, bus);
        await model.InitializeAsync();
        await model.OpenAsync(media.Path);
        await WaitUntilAsync(() => model.State == PlaybackState.Playing && model.ChapterItems.Count == 2);

        Assert.True(model.CanSeekNextChapter);
        await model.SeekNextChapterAsync();
        await WaitUntilAsync(() => model.PositionSeconds == 60 && !model.CanSeekNextChapter);
        Assert.Equal(1, controller.Snapshot.CurrentChapterIndex);
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "chapter", "1"]));

        var chapterCommands = engine.Commands.Count(static command => command is ["set", "chapter", ..]);
        await model.SeekNextChapterAsync();
        Assert.Equal(chapterCommands, engine.Commands.Count(static command => command is ["set", "chapter", ..]));

        await model.SeekAsync(30);
        await WaitUntilAsync(() => model.PositionSeconds == 30 && model.CanSeekNextChapter);

        await model.SeekAsync(59.98);
        await WaitUntilAsync(() => model.PositionSeconds == 59.98 && model.CanSeekNextChapter);
    }

    [Fact]
    public async Task PreviousChapter_FollowsPlaybackPositionAndStopsAtFirstChapter()
    {
        using var media = new TemporaryMediaFile();
        var bus = new InMemoryEventBus();
        var engine = new FakePlaybackEngine();
        var controller = new PlaybackController(engine, bus);
        await using var model = new PlaybackViewModel(controller, bus);
        await model.InitializeAsync();
        await model.OpenAsync(media.Path);
        await WaitUntilAsync(() => model.State == PlaybackState.Playing && model.ChapterItems.Count == 2);

        Assert.False(model.CanSeekPreviousChapter);
        await model.SeekAsync(75);
        await WaitUntilAsync(() => model.PositionSeconds == 75 && model.CanSeekPreviousChapter);

        await model.SeekPreviousChapterAsync();
        await WaitUntilAsync(() => model.PositionSeconds == 0 && !model.CanSeekPreviousChapter);
        Assert.Equal(0, controller.Snapshot.CurrentChapterIndex);
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "chapter", "0"]));

        var chapterCommands = engine.Commands.Count(static command => command is ["set", "chapter", ..]);
        await model.SeekPreviousChapterAsync();
        Assert.Equal(chapterCommands, engine.Commands.Count(static command => command is ["set", "chapter", ..]));

        await model.SeekAsync(60);
        await WaitUntilAsync(() => model.PositionSeconds == 60 && model.CanSeekPreviousChapter);
        await model.SeekAsync(30);
        await WaitUntilAsync(() => model.PositionSeconds == 30 && !model.CanSeekPreviousChapter);
    }

    [Fact]
    public async Task PersistedBookmark_OpensItsMediaAtSavedPosition()
    {
        using var media = new TemporaryMediaFile();
        var stateStore = new StubPlaybackStateStore
        {
            Loaded = new PlaybackPersistedState
            {
                Bookmarks =
                [
                    new PlaybackBookmark
                    {
                        Id = "saved-bookmark",
                        Source = media.Path,
                        Title = "Saved scene",
                        PositionSeconds = 35,
                        CreatedAt = DateTimeOffset.UtcNow,
                    },
                ],
            },
        };
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus(), stateStore);
        await controller.StartAsync();

        await controller.OpenBookmarkAsync("saved-bookmark");
        await WaitUntilAsync(() => engine.Commands.Any(command =>
            command.SequenceEqual(["seek", "35", "absolute+exact"])));

        Assert.Equal(media.Path, controller.Snapshot.Source);
        Assert.Equal("saved-bookmark", Assert.Single(controller.Snapshot.Bookmarks).Id);
    }

    [Fact]
    public async Task RecentPlayback_IsLoadedPersistedAndResumedBeforeCompletion()
    {
        using var media = new TemporaryMediaFile();
        var stateStore = new StubPlaybackStateStore
        {
            Loaded = new PlaybackPersistedState
            {
                RecentItems =
                [
                    new PlaybackRecentItem
                    {
                        Source = media.Path,
                        Title = "Earlier session",
                        PositionSeconds = 42,
                        DurationSeconds = 120,
                        LastPlayedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    },
                ],
            },
        };
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus(), stateStore);
        await controller.StartAsync();

        Assert.Single(controller.Snapshot.RecentItems);
        await controller.OpenRecentAsync(media.Path);
        await WaitUntilAsync(() => engine.Commands.Any(command =>
            command.SequenceEqual(["seek", "42", "absolute+exact"])));

        await controller.RemoveRecentAsync(media.Path);
        Assert.Empty(controller.Snapshot.RecentItems);
        Assert.Empty(stateStore.LastSaved!.RecentItems);
    }

    [Fact]
    public async Task StartupHistory_ExcludesSensitiveAndTemporaryStreams()
    {
        using var media = new TemporaryMediaFile();
        var stateStore = new StubPlaybackStateStore
        {
            Loaded = new PlaybackPersistedState
            {
                RecentItems =
                [
                    new PlaybackRecentItem { Source = "https://example.test/video.mp4?token=secret" },
                    new PlaybackRecentItem { Source = "canx://stream/temporary/video.mkv" },
                    new PlaybackRecentItem { Source = media.Path, Title = "Local video" },
                ],
            },
        };
        await using var controller = new PlaybackController(
            new FakePlaybackEngine(), new InMemoryEventBus(), stateStore);

        await controller.StartAsync();

        Assert.Equal(media.Path, Assert.Single(controller.Snapshot.RecentItems).Source);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemovedOrClearedCurrentHistory_StaysDeletedUntilMediaIsOpenedAgain(bool clearAll, bool ended)
    {
        using var media = new TemporaryMediaFile();
        var stateStore = new StubPlaybackStateStore();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(
            engine, new InMemoryEventBus(), stateStore);
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.SeekAsync(42);
        await WaitUntilAsync(() => controller.Snapshot.PositionSeconds == 42);
        await controller.WriteWatchLaterAsync();
        await WaitUntilAsync(() => controller.Snapshot.RecentItems is [{ PositionSeconds: 42 }]);

        var recorded = Assert.Single(controller.Snapshot.RecentItems);
        Assert.Equal(media.Path, recorded.Source);
        Assert.Equal(42, recorded.PositionSeconds);
        Assert.Equal(120, recorded.DurationSeconds);
        Assert.Single(stateStore.LastSaved!.RecentItems);

        if (ended)
        {
            engine.FinishCurrent();
            await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Ended);
        }

        if (clearAll)
        {
            await controller.ClearRecentAsync();
        }
        else
        {
            await controller.RemoveRecentAsync(media.Path);
        }

        await controller.WriteWatchLaterAsync();
        await controller.StopAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Idle);
        Assert.Empty(controller.Snapshot.RecentItems);
        Assert.Empty(stateStore.LastSaved!.RecentItems);

        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.WriteWatchLaterAsync();
        Assert.Single(controller.Snapshot.RecentItems);
        Assert.Single(stateStore.LastSaved!.RecentItems);
    }

    [Theory]
    [InlineData("rtsp://camera.example:554/live/Stream?channel=1")]
    [InlineData("rtmp://media.example/live/stream")]
    [InlineData("udp://@239.1.2.3:1234")]
    [InlineData("srt://media.example:9000?mode=caller")]
    [InlineData("ftp://media.example/video.mkv")]
    [InlineData("dvd://")]
    [InlineData("av://lavfi:testsrc=size=320x180:rate=24")]
    [InlineData("edl://https://media.example/one.mp4;https://media.example/two.mp4")]
    public async Task NativeMpvAddresses_ArePassedThroughWithoutRewriting(string source)
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();

        await controller.OpenAsync("  " + source + "  ");
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        var command = Assert.Single(engine.Commands, static command => command[0] == "loadfile");
        Assert.Equal(source, command[1]);
        Assert.Equal(source, controller.Snapshot.Source);
        Assert.Equal(source, Assert.Single(controller.Snapshot.Playlist).Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAfterPause_StartsReplacementPlayback(bool stopBeforeOpen)
    {
        using var first = new TemporaryMediaFile();
        using var replacement = new TemporaryMediaFile();
        await using var controller = new PlaybackController(new FakePlaybackEngine(), new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenAsync(first.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.SetPausedAsync(true);
        if (stopBeforeOpen) await controller.StopAsync();

        await controller.OpenAsync(replacement.Path);

        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(replacement.Path, controller.Snapshot.Source);
    }

    [Fact]
    public async Task LocalFileAddress_StillOpensAsAFilePath()
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();

        await controller.OpenAsync(new Uri(media.Path).AbsoluteUri);

        var command = Assert.Single(engine.Commands, static command => command[0] == "loadfile");
        Assert.Equal(media.Path, command[1]);
    }

    [Fact]
    public async Task MissingLocalFile_IsRejectedBeforePlayback()
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        var missingPath = Path.Combine(Path.GetTempPath(), $"canx-missing-{Guid.NewGuid():N}.mp4");

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await controller.OpenAsync(missingPath));

        Assert.DoesNotContain(engine.Commands, static command => command[0] == "loadfile");
    }

    [Fact]
    public async Task SensitiveRemoteSources_AreNotWrittenToPlaybackState()
    {
        const string source = "https://user:password@example.test/video.mp4?token=secret";
        var stateStore = new StubPlaybackStateStore();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus(), stateStore);
        controller.ConfigureStartupOptions(new Dictionary<string, string> { ["cache"] = "auto" });
        await controller.StartAsync();

        await controller.OpenAsync(source);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.WriteWatchLaterAsync();
        Assert.Empty(controller.Snapshot.RecentItems);
        await controller.AddBookmarkAsync(positionSeconds: 10);
        await controller.StopAsync();

        Assert.NotNull(stateStore.LastSaved);
        Assert.Empty(stateStore.LastSaved!.PlaylistSources);
        Assert.Empty(stateStore.LastSaved.RecentItems);
        Assert.Empty(stateStore.LastSaved.Bookmarks);
    }

    [Fact]
    public async Task BrokerSources_ArePlayableButNeverPersistedAcrossSessions()
    {
        const string source =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv";
        var stateStore = new StubPlaybackStateStore();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus(), stateStore);
        await controller.StartAsync();

        await controller.OpenAsync(source);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.WriteWatchLaterAsync();
        Assert.Empty(controller.Snapshot.RecentItems);
        await controller.AddBookmarkAsync(positionSeconds: 10);
        await controller.StopAsync();

        Assert.Contains(engine.Commands, command =>
            command.SequenceEqual(["loadfile", source, "replace", "-1", "cache=yes"]));
        Assert.NotNull(stateStore.LastSaved);
        Assert.Empty(stateStore.LastSaved!.PlaylistSources);
        Assert.Empty(stateStore.LastSaved.RecentItems);
        Assert.Empty(stateStore.LastSaved.Bookmarks);
    }

    [Theory]
    [InlineData("smb://server.example/media/movie.mkv")]
    [InlineData("ftp://server.example/media/movie.mkv")]
    [InlineData("ftps://server.example/media/movie.mkv")]
    [InlineData("https://dav.example.test/media/movie.mkv")]
    public async Task MediaSourceHistory_PersistsOriginalAddressAndResumesWithNewStreamToken(string originalSource)
    {
        const string firstStream =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv";
        const string secondStream =
            "canx://stream/abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789/movie.mkv";
        var stateStore = new StubPlaybackStateStore();
        var engine = new FakePlaybackEngine();
        await using (var controller = new PlaybackController(engine, new InMemoryEventBus(), stateStore))
        {
            await controller.StartAsync();
            await controller.OpenMediaSourceAsync(firstStream, originalSource, "saved-profile");
            await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
            await controller.SeekAsync(42);
            await WaitUntilAsync(() => controller.Snapshot.PositionSeconds == 42);
            await controller.WriteWatchLaterAsync();

            var recent = Assert.Single(controller.Snapshot.RecentItems);
            Assert.Equal(originalSource, recent.Source);
            Assert.True(recent.IsMediaSource);
            Assert.Equal("saved-profile", recent.MediaSourceProfileId);
            Assert.Empty(stateStore.LastSaved!.PlaylistSources);
            Assert.Equal(originalSource, Assert.Single(stateStore.LastSaved.RecentItems).Source);
        }

        var resumedEngine = new FakePlaybackEngine();
        var resumedStateStore = new StubPlaybackStateStore { Loaded = stateStore.LastSaved! };
        await using var resumedController = new PlaybackController(
            resumedEngine, new InMemoryEventBus(), resumedStateStore);
        await resumedController.StartAsync();
        await resumedController.OpenMediaSourceAsync(secondStream, originalSource, "saved-profile");

        await WaitUntilAsync(() => resumedEngine.Commands.Any(command =>
            command.SequenceEqual(["seek", "42", "absolute+exact"])));
        Assert.Equal(originalSource, Assert.Single(resumedController.Snapshot.RecentItems).Source);
        Assert.DoesNotContain(firstStream, resumedStateStore.LastSaved!.RecentItems
            .Select(static item => item.Source));
    }

    [Fact]
    public async Task MediaSourceHistory_RemovesOnlyTheSelectedProfile()
    {
        const string originalSource = "smb://server.example/media/movie.mkv";
        const string firstStream =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv";
        const string secondStream =
            "canx://stream/abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789/movie.mkv";
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenMediaSourceAsync(firstStream, originalSource, "first-profile");
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.WriteWatchLaterAsync();
        await controller.OpenMediaSourceAsync(secondStream, originalSource, "second-profile");
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing &&
            controller.Snapshot.Source == secondStream);
        await controller.WriteWatchLaterAsync();

        Assert.Equal(2, controller.Snapshot.RecentItems.Count);
        await controller.RemoveRecentAsync(originalSource, true, "first-profile");

        Assert.Equal("second-profile", Assert.Single(controller.Snapshot.RecentItems).MediaSourceProfileId);
        await controller.RemoveRecentAsync(originalSource, true, "second-profile");
        await controller.WriteWatchLaterAsync();
        Assert.Empty(controller.Snapshot.RecentItems);
    }

    [Fact]
    public async Task MediaSourceHistory_DoesNotSavePrivateQueryFromResourceAddress()
    {
        const string stream =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv";
        const string privateAddress = "https://dav.example.test/media/movie.mkv?token=secret";
        var stateStore = new StubPlaybackStateStore();
        await using var controller = new PlaybackController(
            new FakePlaybackEngine(), new InMemoryEventBus(), stateStore);
        await controller.StartAsync();
        await controller.OpenMediaSourceAsync(stream, privateAddress, "saved-profile");
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.WriteWatchLaterAsync();

        Assert.Empty(controller.Snapshot.RecentItems);
        Assert.Empty(stateStore.LastSaved!.RecentItems);
    }

    [Fact]
    public async Task MediaSourceHistory_RestartingCurrentStreamKeepsStableHistoryIdentity()
    {
        const string stream =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv";
        const string originalSource = "smb://server.example/media/movie.mkv";
        await using var controller = new PlaybackController(
            new FakePlaybackEngine(), new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenMediaSourceAsync(stream, originalSource, "saved-profile");
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.StopAsync();
        await controller.TogglePauseAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);
        await controller.SeekAsync(18);
        await WaitUntilAsync(() => controller.Snapshot.PositionSeconds == 18);
        await controller.WriteWatchLaterAsync();
        await WaitUntilAsync(() => controller.Snapshot.RecentItems is [{ PositionSeconds: 18 }]);

        var recent = Assert.Single(controller.Snapshot.RecentItems);
        Assert.Equal(originalSource, recent.Source);
        Assert.Equal(18, recent.PositionSeconds);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    public async Task BrokerSources_RespectExplicitGlobalCacheMode(string cacheMode)
    {
        const string source =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv";
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        controller.ConfigureStartupOptions(new Dictionary<string, string> { ["cache"] = cacheMode });
        await controller.StartAsync();

        await controller.OpenAsync(source);

        Assert.Contains(engine.Commands, command =>
            command.SequenceEqual(["loadfile", source, "replace"]));
    }

    [Fact]
    public async Task DownloadBrokerSource_DisablesMpvCacheEvenWhenGlobalCacheIsEnabled()
    {
        const string source =
            "canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/movie.mkv?cache=no";
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        controller.ConfigureStartupOptions(new Dictionary<string, string> { ["cache"] = "yes" });
        await controller.StartAsync();

        await controller.OpenAsync(source);

        Assert.Contains(engine.Commands, command =>
            command.SequenceEqual(["loadfile", source, "replace", "-1", "cache=no"]));
    }

    [Fact]
    public async Task OpenFolder_BuildsSortedPlaylistFromSupportedMediaOnly()
    {
        using var directory = new TemporaryMediaDirectory();
        var second = directory.CreateFile("02-second.mp4");
        var first = directory.CreateFile("01-first.mkv");
        _ = directory.CreateFile("notes.txt");
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();

        await controller.OpenFolderAsync(directory.Path);

        Assert.Equal([first, second], controller.Snapshot.Playlist.Select(static item => item.Source));
        Assert.DoesNotContain(controller.Snapshot.Playlist, static item => item.Source.EndsWith(".txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdvancedPlaybackControls_AreValidatedForwardedAndPublished()
    {
        using var media = new TemporaryMediaFile();
        using var shader = new TemporaryMediaFile(".glsl");
        var screenshot = Path.Combine(Path.GetTempPath(), $"canx-shot-{Guid.NewGuid():N}.png");
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.State == PlaybackState.Playing);

        try
        {
            await controller.StepFrameAsync();
            await controller.StepFrameAsync(backward: true);
            await controller.SetAbLoopAsync(startSeconds: 10);
            await controller.SetAbLoopAsync(endSeconds: 20);
            Assert.Equal(10, controller.Snapshot.AbLoopASeconds);
            Assert.Equal(20, controller.Snapshot.AbLoopBSeconds);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await controller.SetAbLoopAsync(endSeconds: 5));
            await controller.ClearAbLoopAsync();
            Assert.Null(controller.Snapshot.AbLoopASeconds);
            Assert.Null(controller.Snapshot.AbLoopBSeconds);

            var actualScreenshot = await controller.TakeScreenshotAsync(
                screenshot,
                PlaybackScreenshotMode.Video);
            Assert.Equal(screenshot, actualScreenshot);
            Assert.True(File.Exists(screenshot));
            await Assert.ThrowsAsync<IOException>(async () =>
                await controller.TakeScreenshotAsync(screenshot));

            await controller.SelectAudioDeviceAsync("test/output");
            Assert.Equal("test/output", controller.Snapshot.AudioDevice);
            Assert.Contains(controller.Snapshot.AudioDevices, static device =>
                device.Name == "test/output" && device.IsCurrent);

            await controller.SetVideoFiltersAsync("lavfi=[hflip]");
            await controller.SetAudioFiltersAsync("lavfi=[volume=0.9]");
            Assert.Equal("lavfi=[hflip]", controller.Snapshot.VideoFilters);
            Assert.Equal("lavfi=[volume=0.9]", controller.Snapshot.AudioFilters);
            await controller.SetVideoFiltersAsync(string.Empty);
            await controller.SetAudioFiltersAsync(string.Empty);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await controller.SetVideoFiltersAsync("lavfi=[movie=/etc/passwd[m];[in][m]overlay]"));
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await controller.SetVideoFiltersAsync("lavfi=[subtitles=filename=/tmp/x.srt]"));
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await controller.SetAudioFiltersAsync("lavfi=[amovie=/tmp/a.wav]"));
            await controller.SetVideoFiltersAsync("lavfi=[unsharp=5:5:0.8]");
            await controller.SetVideoFiltersAsync(string.Empty);

            await controller.AddShaderAsync(shader.Path);
            Assert.Equal(shader.Path, Assert.Single(controller.Snapshot.Shaders));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await controller.AddShaderAsync(shader.Path));
            await controller.RemoveShaderAsync(shader.Path);
            Assert.Empty(controller.Snapshot.Shaders);
            await controller.AddShaderAsync(shader.Path);
            await controller.ClearShadersAsync();
            Assert.Empty(controller.Snapshot.Shaders);

            Assert.Contains(engine.Commands, static command => command.SequenceEqual(["frame-step"]));
            Assert.Contains(engine.Commands, static command => command.SequenceEqual(["frame-back-step"]));
            Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "ab-loop-a", "10"]));
            Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "ab-loop-b", "20"]));
            Assert.Contains(engine.Commands, command =>
                command.SequenceEqual(["screenshot-to-file", screenshot, "video"]));
            Assert.Contains(engine.Commands, static command => command.SequenceEqual(["set", "audio-device", "test/output"]));
            Assert.Contains(engine.Commands, static command => command.SequenceEqual(["vf", "set", "lavfi=[hflip]"]));
            Assert.Contains(engine.Commands, static command => command.SequenceEqual(["af", "clr", ""]));
            Assert.Contains(engine.Commands, command =>
                command.SequenceEqual(["change-list", "glsl-shaders", "append", shader.Path]));
        }
        finally
        {
            File.Delete(screenshot);
        }
    }

    [Fact]
    public async Task InvalidSourceAndControlRanges_AreRejectedBeforeNativeCommands()
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await controller.OpenAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await controller.SetVolumeAsync(131));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await controller.SetBrightnessAsync(101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await controller.SetSpeedAsync(0.09));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await controller.SetSpeedAsync(4.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await controller.SeekAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.SelectTrackAsync(PlaybackTrackType.Unknown, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.SelectTrackAsync(PlaybackTrackType.Audio, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.RestoreVideoOutputAsync(0));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.RestoreVideoOutputAsync(1));
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await controller.AddSubtitleAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.srt")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.SetSubtitleDelayAsync(3601));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.SetSubtitleFontSizeAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.SetSubtitlePositionAsync(151));

        Assert.Empty(engine.Commands);
    }

    [Fact]
    public async Task DiagnosticsColorAndAdvancedRuntime_AreReportedValidatedAndRestricted()
    {
        using var media = new TemporaryMediaFile();
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine, new InMemoryEventBus());
        await controller.StartAsync();
        await controller.OpenAsync(media.Path);
        await WaitUntilAsync(() => controller.Snapshot.Diagnostics.HardwareDecoder == "nvdec");

        var diagnostics = controller.Snapshot.Diagnostics;
        Assert.Equal("HEVC (High Efficiency Video Coding)", diagnostics.VideoCodec);
        Assert.Equal("hevc", diagnostics.VideoFormat);
        Assert.Equal("nvdec", diagnostics.HardwareDecoder);
        Assert.Equal("cuda", diagnostics.HardwareInterop);
        Assert.Equal("hardware-zero-copy-interop", diagnostics.FrameTransferPath);
        Assert.Equal("opengl", diagnostics.GpuApi);
        Assert.Equal("bt.2020", diagnostics.InputPrimaries);
        Assert.Equal(23.976, diagnostics.EstimatedVideoFps);
        Assert.Equal(2, diagnostics.OutputDroppedFrames);

        await controller.SetColorSettingsAsync(new PlaybackColorSettings
        {
            TargetColorspaceHint = "yes",
            TargetColorspaceHintMode = "source",
            TargetPrimaries = "bt.2020",
            TargetTransfer = "pq",
            TargetPeak = "1000",
            ToneMapping = "bt.2390",
            GamutMappingMode = "perceptual",
            HdrComputePeak = "yes",
        });
        Assert.Equal("bt.2390", controller.Snapshot.ColorSettings.ToneMapping);
        Assert.Equal("1000", controller.Snapshot.ColorSettings.TargetPeak);

        Assert.Equal("audio", await controller.GetRuntimePropertyAsync("video-sync"));
        await controller.SetRuntimePropertyAsync("video-sync", "display-resample");
        Assert.Equal("display-resample", await controller.GetRuntimePropertyAsync("video-sync"));
        await controller.ExecuteCommandAsync("show-text", ["CanX", "500"]);
        Assert.Contains(engine.Commands, static command => command.SequenceEqual(["show-text", "CanX", "500"]));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.GetRuntimePropertyAsync("path"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.GetRuntimePropertyAsync("options"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.GetRuntimePropertyAsync("track-list"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.GetRuntimePropertyAsync("options/http-header-fields"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.SetRuntimePropertyAsync("vo", "gpu-next"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ExecuteCommandAsync("quit", []));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ExecuteCommandAsync("keypress", ["q"]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ExecuteCommandAsync("load-config-file", ["/tmp/mpv.conf"]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ExecuteCommandAsync("load-input-conf", ["/tmp/input.conf"]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ExecuteCommandAsync("keybind", ["q", "quit"]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ExecuteCommandAsync("show-text", ["${path}"]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.SetColorSettingsAsync(new PlaybackColorSettings { TargetPeak = "1" }));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakePlaybackEngine : IPlaybackEngine
    {
        public string RenderApi => "opengl";

        public string RenderContext => "test-context";

        private readonly ConcurrentQueue<PlaybackEngineEvent> _events = new();
        private readonly List<string> _playlist = [];
        private int _playlistPosition = -1;
        private int _chapterIndex;
        private readonly List<string> _shaders = [];
        private readonly Dictionary<string, object> _properties = new(StringComparer.Ordinal)
        {
            ["idle-active"] = true,
            ["pause"] = false,
            ["volume"] = 100d,
            ["brightness"] = 0d,
            ["mute"] = false,
            ["speed"] = 1d,
            ["seekable"] = true,
            ["sub-visibility"] = true,
            ["sub-delay"] = 0d,
            ["sub-font-size"] = 38d,
            ["sub-scale"] = 1d,
            ["sub-ass-override"] = "scale",
            ["sub-pos"] = 100d,
            ["audio-device"] = "auto",
            ["vf"] = string.Empty,
            ["af"] = string.Empty,
            ["video-params/w"] = 1920L,
            ["video-params/h"] = 1080L,
            ["video-params/dw"] = 1280L,
            ["video-params/dh"] = 720L,
            ["video-codec"] = "HEVC (High Efficiency Video Coding)",
            ["video-format"] = "hevc",
            ["video-params/pixelformat"] = "cuda",
            ["video-params/hw-pixelformat"] = "p010le",
            ["hwdec-current"] = "nvdec",
            ["hwdec-interop"] = "cuda",
            ["current-vo"] = "libmpv",
            ["gpu-api"] = "opengl",
            ["current-gpu-context"] = "x11egl",
            ["video-params/colormatrix"] = "bt.2020-ncl",
            ["video-params/primaries"] = "bt.2020",
            ["video-params/gamma"] = "pq",
            ["video-params/colorlevels"] = "limited",
            ["video-params/sig-peak"] = 4.926d,
            ["video-target-params/colormatrix"] = "bt.709",
            ["video-target-params/primaries"] = "bt.709",
            ["video-target-params/gamma"] = "bt.1886",
            ["video-target-params/colorlevels"] = "full",
            ["video-target-params/sig-peak"] = 1d,
            ["container-fps"] = 24d,
            ["estimated-vf-fps"] = 23.976d,
            ["display-fps"] = 60d,
            ["avsync"] = 0.002d,
            ["total-avsync-change"] = 0.01d,
            ["decoder-frame-drop-count"] = 1L,
            ["frame-drop-count"] = 2L,
            ["mistimed-frame-count"] = 3L,
            ["vo-delayed-frame-count"] = 4L,
            ["video-bitrate"] = 8_000_000d,
            ["audio-bitrate"] = 192_000d,
            ["cache-speed"] = 2_000_000d,
            ["demuxer-cache-duration"] = 30d,
            ["target-colorspace-hint"] = "auto",
            ["target-colorspace-hint-mode"] = "target",
            ["target-prim"] = "auto",
            ["target-trc"] = "auto",
            ["target-peak"] = "auto",
            ["tone-mapping"] = "auto",
            ["gamut-mapping-mode"] = "auto",
            ["hdr-compute-peak"] = "auto",
            ["video-sync"] = "audio",
        };

        public List<string[]> Commands { get; } = [];

        public FakePlaybackEngine(bool supportsSubtitleScaleSigns = true)
        {
            if (supportsSubtitleScaleSigns)
                _properties["sub-scale-signs"] = false;
        }

        public void FinishCurrent()
        {
            _events.Enqueue(new PlaybackEngineEvent(PlaybackEngineEventKind.EndFile));
            if (_playlistPosition + 1 < _playlist.Count &&
                !string.Equals((string)_properties["keep-open"], "always", StringComparison.Ordinal))
            {
                _playlistPosition++;
                StartCurrent();
            }
            else _properties["idle-active"] = true;
        }

        private List<PlaybackTrack> Tracks { get; set; } =
        [
            new PlaybackTrack { Id = 1, Type = PlaybackTrackType.Video, Codec = "h264", IsSelected = true },
            new PlaybackTrack { Id = 2, Type = PlaybackTrackType.Audio, Language = "zh", Codec = "aac", IsSelected = true },
        ];

        public IReadOnlyDictionary<string, string> InitializedOptions { get; private set; } =
            new Dictionary<string, string>();

        public void Initialize(IReadOnlyDictionary<string, string> options)
        {
            InitializedOptions = new Dictionary<string, string>(options);
            _properties["keep-open"] = options.GetValueOrDefault("keep-open", "yes");
        }

        public void Command(params string[] arguments)
        {
            Commands.Add(arguments);
            switch (arguments[0])
            {
                case "loadfile":
                    var mode = arguments.Length > 2 ? arguments[2] : "replace";
                    if (mode == "replace")
                    {
                        _playlist.Clear();
                        _playlist.Add(arguments[1]);
                        _playlistPosition = 0;
                        StartCurrent();
                    }
                    else
                    {
                        _playlist.Add(arguments[1]);
                        if (mode == "append-play")
                        {
                            _playlistPosition = _playlist.Count - 1;
                            StartCurrent();
                        }
                    }

                    break;
                case "set" when arguments[1] == "pause":
                    _properties["pause"] = arguments[2] == "yes";
                    break;
                case "set" when arguments[1] == "volume":
                    _properties["volume"] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] == "brightness":
                    _properties["brightness"] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] == "mute":
                    _properties["mute"] = arguments[2] == "yes";
                    break;
                case "set" when arguments[1] == "speed":
                    _properties["speed"] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] is "ab-loop-a" or "ab-loop-b":
                    if (arguments[2] == "no")
                    {
                        _properties.Remove(arguments[1]);
                    }
                    else
                    {
                        _properties[arguments[1]] = double.Parse(
                            arguments[2],
                            System.Globalization.CultureInfo.InvariantCulture);
                    }

                    break;
                case "set" when arguments[1] == "audio-device":
                    _properties["audio-device"] = arguments[2];
                    break;
                case "set" when arguments[1] is "vid" or "aid" or "sid":
                    var type = arguments[1] switch
                    {
                        "vid" => PlaybackTrackType.Video,
                        "aid" => PlaybackTrackType.Audio,
                        _ => PlaybackTrackType.Subtitle,
                    };
                    var selectedId = long.TryParse(arguments[2], out var id) ? id : (long?)null;
                    Tracks = Tracks.Select(track => track.Type == type
                        ? track with { IsSelected = track.Id == selectedId }
                        : track).ToList();
                    break;
                case "sub-add":
                    Tracks = Tracks.Select(static track => track.Type == PlaybackTrackType.Subtitle
                        ? track with { IsSelected = false }
                        : track).Append(new PlaybackTrack
                        {
                            Id = 3,
                            Type = PlaybackTrackType.Subtitle,
                            Title = Path.GetFileName(arguments[1]),
                            Codec = "subrip",
                            IsSelected = arguments[2] == "select",
                            IsExternal = true,
                            ExternalFileName = arguments[1],
                        }).ToList();
                    break;
                case "set" when arguments[1] == "sub-visibility":
                    _properties["sub-visibility"] = arguments[2] == "yes";
                    break;
                case "set" when arguments[1] == "sub-delay":
                    _properties["sub-delay"] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] == "sub-font-size":
                    _properties["sub-font-size"] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] == "sub-scale":
                    _properties["sub-scale"] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] == "sub-scale-signs":
                    if (!_properties.ContainsKey("sub-scale-signs"))
                        throw new InvalidOperationException("mpv does not support sub-scale-signs.");
                    _properties["sub-scale-signs"] = arguments[2] == "yes";
                    break;
                case "set" when arguments[1] == "sub-ass-override":
                    _properties["sub-ass-override"] = arguments[2];
                    break;
                case "set" when arguments[1] == "sub-pos":
                    _properties["sub-pos"] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] == "playlist-pos":
                    _playlistPosition = int.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    StartCurrent();
                    break;
                case "seek" when arguments[2].StartsWith("absolute", StringComparison.Ordinal):
                    _properties["time-pos"] = double.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "set" when arguments[1] == "chapter":
                    _chapterIndex = int.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    _properties["time-pos"] = _chapterIndex == 0 ? 0d : 60d;
                    break;
                case "set":
                    _properties[arguments[1]] = arguments[2];
                    break;
                case "playlist-remove":
                    var removeIndex = int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture);
                    _playlist.RemoveAt(removeIndex);
                    if (_playlist.Count == 0)
                    {
                        _playlistPosition = -1;
                    }
                    else if (removeIndex < _playlistPosition)
                    {
                        _playlistPosition--;
                    }
                    else if (removeIndex == _playlistPosition)
                    {
                        _playlistPosition = Math.Min(removeIndex, _playlist.Count - 1);
                        StartCurrent();
                    }

                    break;
                case "playlist-move":
                    var oldIndex = int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture);
                    var targetIndex = int.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
                    var destinationIndex = oldIndex < targetIndex ? targetIndex - 1 : targetIndex;
                    var source = _playlist[oldIndex];
                    _playlist.RemoveAt(oldIndex);
                    _playlist.Insert(destinationIndex, source);
                    if (_playlistPosition == oldIndex)
                    {
                        _playlistPosition = destinationIndex;
                    }
                    else if (oldIndex < _playlistPosition && destinationIndex >= _playlistPosition)
                        _playlistPosition--;
                    else if (oldIndex > _playlistPosition && destinationIndex <= _playlistPosition)
                        _playlistPosition++;

                    break;
                case "playlist-clear":
                    if (_playlistPosition >= 0 && _playlistPosition < _playlist.Count)
                    {
                        var current = _playlist[_playlistPosition];
                        _playlist.Clear();
                        _playlist.Add(current);
                        _playlistPosition = 0;
                    }
                    else
                    {
                        _playlist.Clear();
                        _playlistPosition = -1;
                    }

                    break;
                case "stop":
                    _properties["idle-active"] = true;
                    _events.Enqueue(new PlaybackEngineEvent(PlaybackEngineEventKind.Stopped));
                    break;
                case "frame-step":
                    _properties["pause"] = true;
                    _properties["time-pos"] = ((double)_properties["time-pos"]) + 1d / 30;
                    break;
                case "frame-back-step":
                    _properties["pause"] = true;
                    _properties["time-pos"] = Math.Max(0, ((double)_properties["time-pos"]) - 1d / 30);
                    break;
                case "screenshot-to-file":
                    File.WriteAllBytes(arguments[1], [0x89, 0x50, 0x4e, 0x47]);
                    break;
                case "vf":
                case "af":
                    _properties[arguments[0]] = arguments[1] == "clr" ? string.Empty : arguments[2];
                    break;
                case "change-list" when arguments[1] == "glsl-shaders":
                    if (arguments[2] == "append")
                    {
                        _shaders.Add(arguments[3]);
                    }
                    else if (arguments[2] == "remove")
                    {
                        _shaders.Remove(arguments[3]);
                    }
                    else if (arguments[2] == "clr")
                    {
                        _shaders.Clear();
                    }

                    break;
            }
        }

        public bool TryGetString(string name, out string value) => TryGet(name, out value);

        public bool TryGetDouble(string name, out double value) => TryGet(name, out value);

        public bool TryGetInt64(string name, out long value) => TryGet(name, out value);

        public bool TryGetFlag(string name, out bool value) => TryGet(name, out value);

        public PlaybackEngineEvent PollEvent() =>
            _events.TryDequeue(out var value) ? value : default;

        public IReadOnlyList<PlaybackTrack> GetTracks() => Tracks.ToList();

        public IReadOnlyList<PlaybackPlaylistItem> GetPlaylist() => _playlist
            .Select((source, index) => new PlaybackPlaylistItem
            {
                Index = index,
                Source = source,
                Title = Path.GetFileName(source),
                IsCurrent = index == _playlistPosition,
            })
            .ToList();

        public IReadOnlyList<PlaybackChapter> GetChapters() =>
        [
            new PlaybackChapter { Index = 0, Title = "Opening", TimeSeconds = 0, IsCurrent = _chapterIndex == 0 },
            new PlaybackChapter { Index = 1, Title = "Main", TimeSeconds = 60, IsCurrent = _chapterIndex == 1 },
        ];

        public IReadOnlyList<PlaybackAudioDevice> GetAudioDevices() =>
        [
            new PlaybackAudioDevice
            {
                Name = "auto",
                Description = "自动选择",
                IsCurrent = string.Equals((string)_properties["audio-device"], "auto", StringComparison.Ordinal),
            },
            new PlaybackAudioDevice
            {
                Name = "test/output",
                Description = "测试音频设备",
                IsCurrent = string.Equals((string)_properties["audio-device"], "test/output", StringComparison.Ordinal),
            },
        ];

        public IReadOnlyList<string> GetShaders() => _shaders.ToList();

        public MpvRuntimeCapabilityManifest GetCapabilities() => new()
        {
            MpvVersion = "mpv 0.41.0-test",
            ClientApiVersion = "2.3",
            FfmpegVersion = "7.1-test",
            Platform = "test",
            Properties = ["path", "pause", "video-sync", "vo"],
            Commands =
            [
                new MpvRuntimeCommandCapability
                {
                    Name = "show-text",
                    Arguments =
                    [
                        new MpvRuntimeCommandArgumentCapability { Name = "text", Type = "String" },
                    ],
                },
                new MpvRuntimeCommandCapability { Name = "quit" },
            ],
        };

        public void Dispose()
        {
        }

        private bool TryGet<T>(string name, out T value)
        {
            if (_properties.TryGetValue(name, out var stored) && stored is T typed)
            {
                value = typed;
                return true;
            }

            value = default!;
            return false;
        }

        private void StartCurrent()
        {
            _chapterIndex = 0;
            _properties["idle-active"] = false;
            _properties["media-title"] = "Fixture video";
            _properties["duration"] = 120d;
            _properties["time-pos"] = 0d;
            _properties["path"] = _playlist[_playlistPosition];
            _properties["playlist-pos"] = (long)_playlistPosition;
            _events.Enqueue(new PlaybackEngineEvent(PlaybackEngineEventKind.StartFile));
            _events.Enqueue(new PlaybackEngineEvent(PlaybackEngineEventKind.FileLoaded));
        }
    }

    private sealed class StubPlaybackStateStore : IPlaybackStateStore
    {
        public PlaybackPersistedState Loaded { get; init; } = new();

        public PlaybackPersistedState? LastSaved { get; private set; }

        public ValueTask<PlaybackPersistedState> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Loaded);

        public ValueTask SaveAsync(
            PlaybackPersistedState state,
            CancellationToken cancellationToken = default)
        {
            LastSaved = state;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryMediaFile : IDisposable
    {
        public TemporaryMediaFile(string extension = ".mkv")
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-media-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, $"video{extension}");
            File.WriteAllText(Path, "fixture");
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
    }

    private sealed class TemporaryMediaDirectory : IDisposable
    {
        public TemporaryMediaDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-media-dir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, "fixture");
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
