// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Application.Playback;
using CanX.Core.Playback;
using CanX.Infrastructure.Playback;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class SharedPlaybackStateStoreTests
{
    [Fact]
    public async Task ConcurrentPlayers_KeepBothHistoriesAndOnlyPersistTheActivePlaylist()
    {
        var disk = new MemoryStore();
        var shared = new SharedPlaybackStateStore(disk);
        var first = shared.CreateSession();
        var second = shared.CreateSession();
        await first.LoadAsync();
        await second.LoadAsync();
        var one = Recent("https://media.test/one.mp4", 12);
        var two = Recent("https://media.test/two.mp4", 24);

        await Task.WhenAll(
            Task.Run(() => first.SynchronizeHistory([one], [])),
            Task.Run(() => second.SynchronizeHistory([two], [])));
        second.Activate();
        await Task.WhenAll(
            first.SaveAsync(new() { PlaylistSources = [one.Source], SelectedPlaylistIndex = 0 }).AsTask(),
            second.SaveAsync(new() { PlaylistSources = [two.Source], SelectedPlaylistIndex = 0 }).AsTask());

        Assert.Contains(one, disk.State.RecentItems);
        Assert.Contains(two, disk.State.RecentItems);
        Assert.Equal(two.Source, Assert.Single(disk.State.PlaylistSources));
        var extraWindow = await shared.CreateSession().LoadAsync();
        Assert.Empty(extraWindow.PlaylistSources);
        Assert.Equal(2, extraWindow.RecentItems.Count);
        var restarted = await shared.CreateSession(restorePlaylist: true).LoadAsync();
        Assert.Equal(two.Source, Assert.Single(restarted.PlaylistSources));
    }

    [Fact]
    public async Task StalePlayer_DoesNotRestoreDeletedHistoryOrBookmarks()
    {
        var item = Recent("https://media.test/one.mp4", 12);
        var bookmark = new PlaybackBookmark { Id = "one", Source = item.Source, Title = "Scene", PositionSeconds = 10 };
        var disk = new MemoryStore { State = new() { RecentItems = [item], Bookmarks = [bookmark] } };
        var shared = new SharedPlaybackStateStore(disk);
        var first = shared.CreateSession();
        var second = shared.CreateSession();
        await first.LoadAsync();
        var stale = await second.LoadAsync();

        first.SynchronizeHistory([], []);
        var synchronized = second.SynchronizeHistory(stale.RecentItems, stale.Bookmarks);
        await second.SaveAsync(stale);

        Assert.Empty(synchronized.RecentItems);
        Assert.Empty(synchronized.Bookmarks);
        Assert.Empty(disk.State.RecentItems);
        Assert.Empty(disk.State.Bookmarks);
    }

    [Fact]
    public async Task DifferentMediaProfiles_KeepIndependentProgress()
    {
        var shared = new SharedPlaybackStateStore(new MemoryStore());
        var first = shared.CreateSession();
        var second = shared.CreateSession();
        await first.LoadAsync();
        await second.LoadAsync();
        var one = Recent("smb://server/film.mkv", 10) with { IsMediaSource = true, MediaSourceProfileId = "first" };
        var two = one with { MediaSourceProfileId = "second", PositionSeconds = 30 };

        first.SynchronizeHistory([one], []);
        var result = second.SynchronizeHistory([two], []);

        Assert.Contains(one, result.RecentItems);
        Assert.Contains(two, result.RecentItems);
    }

    private static PlaybackRecentItem Recent(string source, double position) => new()
    {
        Source = source, PositionSeconds = position, LastPlayedAt = DateTimeOffset.UtcNow,
    };

    private sealed class MemoryStore : IPlaybackStateStore
    {
        public PlaybackPersistedState State { get; set; } = new();
        public ValueTask<PlaybackPersistedState> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(State);
        public ValueTask SaveAsync(PlaybackPersistedState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return ValueTask.CompletedTask;
        }
    }
}
