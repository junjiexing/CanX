// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.ViewModels;
using CanX.MediaAccess;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackHistoryProgressLookupTests
{
    [Fact]
    public void LocalPathMatchesFileResourceUri()
    {
        var path = Path.Combine(Path.GetTempPath(), "CanX progress", "movie.mkv");
        var resource = Resource(new Uri(path));
        var lookup = new PlaybackHistoryProgressLookup([Recent(path, 25, 100)]);

        Assert.Equal(25, lookup.Find(resource, null)?.Progress);
    }

    [Fact]
    public void NetworkProgressUsesMatchingProfileAndAnonymousFallback()
    {
        var source = "smb://server.example/share/movie.mkv";
        var resource = Resource(new Uri(source));
        var lookup = new PlaybackHistoryProgressLookup(
        [
            Recent(source, 20, 100, "alice"),
            Recent(source, 80, 100, "bob"),
            Recent(source, 10, 100),
        ]);

        Assert.Equal(20, lookup.Find(resource, "alice")?.Progress);
        Assert.Equal(80, lookup.Find(resource, "bob")?.Progress);
        Assert.Equal(10, lookup.Find(resource, "charlie")?.Progress);
        Assert.Equal(10, lookup.Find(resource, null)?.Progress);
    }

    private static MediaResource Resource(Uri uri) => new()
    {
        Uri = uri,
        Name = Path.GetFileName(uri.AbsolutePath),
        IsDirectory = false,
    };

    private static PlaybackRecentItemViewModel Recent(
        string source,
        double position,
        double duration,
        string? profileId = null) => new(
            source, "movie", position, duration, DateTimeOffset.UtcNow,
            IsCompleted: false, IsMediaSource: profileId is not null,
            MediaSourceProfileId: profileId);
}
