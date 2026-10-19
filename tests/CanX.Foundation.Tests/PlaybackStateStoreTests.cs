// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;
using CanX.App.ViewModels;
using CanX.Infrastructure.Playback;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackStateStoreTests
{
    [Fact]
    public async Task State_RoundTripsPlaylistRecentItemsAndBookmarks()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonPlaybackStateStore(Path.Combine(directory.Path, "playback-state.json"));
        var expected = new PlaybackPersistedState
        {
            PlaylistSources = ["/media/one.mkv", "https://example.test/two.mp4"],
            SelectedPlaylistIndex = 1,
            RecentItems =
            [
                new PlaybackRecentItem
                {
                    Source = "/media/one.mkv",
                    Title = "One",
                    PositionSeconds = 12.5,
                    DurationSeconds = 100,
                    LastPlayedAt = DateTimeOffset.Parse("2026-09-10T08:00:00Z"),
                    Preferences = new PlaybackMediaPreferences
                    {
                        AudioTrack = new PlaybackTrackPreference { Id = 2, Language = "ja", Codec = "flac" },
                        SubtitleTrack = new PlaybackTrackPreference
                        {
                            Id = 4,
                            ExternalSubtitle = new PlaybackExternalSubtitleSource { Source = "/media/chosen.ass" },
                        },
                        ExternalSubtitles = [new PlaybackExternalSubtitleSource { Source = "/media/chosen.ass" }],
                        SubtitleDelaySeconds = -0.5,
                    },
                },
                new PlaybackRecentItem
                {
                    Source = "smb://server.example/media/two.mkv",
                    Title = "Two",
                    IsMediaSource = true,
                    MediaSourceProfileId = "saved-smb",
                    PositionSeconds = 15,
                    DurationSeconds = 120,
                    LastPlayedAt = DateTimeOffset.Parse("2026-09-10T08:01:00Z"),
                },
            ],
            Bookmarks =
            [
                new PlaybackBookmark
                {
                    Id = "bookmark-1",
                    Source = "/media/one.mkv",
                    Title = "Favorite scene",
                    PositionSeconds = 25,
                    CreatedAt = DateTimeOffset.Parse("2026-09-10T08:05:00Z"),
                },
            ],
        };

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        Assert.Equal(expected.PlaylistSources, actual.PlaylistSources);
        Assert.Equal(1, actual.SelectedPlaylistIndex);
        Assert.Equal(2, actual.RecentItems.Count);
        var local = Assert.Single(actual.RecentItems, static item => !item.IsMediaSource);
        Assert.Equal("One", local.Title);
        Assert.Equal(12.5, local.PositionSeconds);
        Assert.Equal("ja", local.Preferences!.AudioTrack!.Language);
        Assert.Equal("/media/chosen.ass", local.Preferences.SubtitleTrack!.ExternalSubtitle!.Source);
        Assert.Equal("/media/chosen.ass", Assert.Single(local.Preferences.ExternalSubtitles).Source);
        Assert.Equal(-0.5, local.Preferences.SubtitleDelaySeconds);
        var network = Assert.Single(actual.RecentItems, static item => item.IsMediaSource);
        Assert.Equal("smb://server.example/media/two.mkv", network.Source);
        Assert.Equal("saved-smb", network.MediaSourceProfileId);
        var bookmark = Assert.Single(actual.Bookmarks);
        Assert.Equal("bookmark-1", bookmark.Id);
        Assert.Equal("Favorite scene", bookmark.Title);
    }

    [Fact]
    public async Task InvalidState_IsIgnoredWithoutPreventingPlaybackStartup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "playback-state.json");
        await File.WriteAllTextAsync(path, "{ invalid json");
        var store = new JsonPlaybackStateStore(path);

        var state = await store.LoadAsync();

        Assert.Empty(state.PlaylistSources);
        Assert.Empty(state.RecentItems);
    }

    [Fact]
    public async Task TemporarySubtitleSources_AreDiscardedOnLoad()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonPlaybackStateStore(Path.Combine(directory.Path, "playback-state.json"));
        var source = new PlaybackExternalSubtitleSource { Source = "canx://stream/secret/subtitle.ass" };
        await store.SaveAsync(new PlaybackPersistedState
        {
            RecentItems = [new PlaybackRecentItem
            {
                Source = "/media/movie.mkv",
                Preferences = new PlaybackMediaPreferences
                {
                    SubtitleTrack = new PlaybackTrackPreference { Id = 3, ExternalSubtitle = source },
                    ExternalSubtitles = [source],
                },
            }],
        });

        var preferences = Assert.Single((await store.LoadAsync()).RecentItems).Preferences!;
        Assert.Null(preferences.SubtitleTrack);
        Assert.Empty(preferences.ExternalSubtitles);
    }

    [Fact]
    public async Task DownloadedSubtitleCache_ProducesReusableLocalFile()
    {
        using var directory = new TemporaryDirectory();
        var cache = new RememberedSubtitleCache(Path.Combine(directory.Path, "subtitles"));
        var content = "1\n00:00:00,000 --> 00:00:01,000\nHello\n"u8.ToArray();

        var first = await cache.StoreAsync(content, "chosen.srt");
        var second = await cache.StoreAsync(content, "chosen.srt");

        Assert.Equal(first, second);
        Assert.Equal(content, await File.ReadAllBytesAsync(first));
        Assert.EndsWith(".srt", first, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-playback-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
