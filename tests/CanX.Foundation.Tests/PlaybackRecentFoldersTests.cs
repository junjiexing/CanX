// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.ViewModels;
using CanX.Core.Playback;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackRecentFoldersTests
{
    // Plain local paths must be fully qualified for the host: "/foo" has no
    // drive on Windows and is ignored there, like any other relative path.
    private static readonly string LocalRoot = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string Local(string relative) =>
        LocalRoot + relative.Replace('/', Path.DirectorySeparatorChar);

    private static string LocalDirectory(string relative) =>
        Local(relative) + Path.DirectorySeparatorChar;

    private static Uri LocalDirectoryUri(string relative) => new(LocalDirectory(relative));

    [Fact]
    public void Build_GroupsVideoParentsAndSortsByLatestPlayback()
    {
        var older = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(2);
        var latest = newer.AddHours(1);
        var folders = PlaybackRecentFolders.Build(
        [
            Item(Local("foo/bar/first.mp4"), older),
            Item("smb://1.2.3.4/x/y/z.mp4", newer, "smb-source"),
            Item(Local("foo/bar/second.mkv"), latest),
            Item(Local("foo/other/song.flac"), latest),
            Item("canx://stream/123", latest, "smb-source"),
        ]);

        Assert.Equal(["bar", "y"], folders.Select(static folder => folder.Name));
        Assert.Equal(LocalDirectoryUri("foo/bar"), folders[0].Directory);
        Assert.Equal(LocalDirectory("foo/bar"), folders[0].Location);
        Assert.Equal(latest, folders[0].LastPlayedAt);
        Assert.Equal(new Uri("smb://1.2.3.4/x/y/"), folders[1].Directory);
        Assert.Equal("smb-source", folders[1].MediaSourceProfileId);
    }

    [Fact]
    public void Build_AcceptsFileUrisAndKeepsSeparateNetworkProfiles()
    {
        var when = DateTimeOffset.UtcNow;
        var folders = PlaybackRecentFolders.Build(
        [
            Item(new Uri(Local("foo/My Films/one.ogm")).AbsoluteUri, when),
            Item(Local("foo/My Films/two.asf"), when.AddMinutes(1)),
            Item("https://dav.example.test/media/movie.mp4?download=1", when, "alice"),
            Item("https://dav.example.test/media/other.mkv", when, "bob"),
            Item("https://example.test/not-a-source.mp4", when),
        ]);

        Assert.Equal(3, folders.Count);
        Assert.Equal("My Films", folders[0].Name);
        Assert.Equal(LocalDirectory("foo/My Films"), folders[0].Location);
        Assert.Equal(["alice", "bob"], folders.Skip(1).Select(static folder => folder.MediaSourceProfileId));
    }

    private static PlaybackRecentItem Item(string source, DateTimeOffset when, string? profileId = null) =>
        new()
        {
            Source = source,
            LastPlayedAt = when,
            IsMediaSource = profileId is not null,
            MediaSourceProfileId = profileId,
        };
}
