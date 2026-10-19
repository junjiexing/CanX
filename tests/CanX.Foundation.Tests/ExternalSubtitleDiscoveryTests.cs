// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.MediaAccess;
using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class ExternalSubtitleDiscoveryTests
{
    [Fact]
    public async Task FindLocalAsync_ScansOnlySiblingAndNamedSubtitleFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-subtitles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var video = Path.Combine(directory, "Movie.mkv");
            await File.WriteAllTextAsync(video, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, "Other.mkv"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, "Movie.srt"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, "sc.srt"), string.Empty);
            var subtitleFolder = Path.Combine(directory, "subs");
            Directory.CreateDirectory(subtitleFolder);
            await File.WriteAllTextAsync(Path.Combine(subtitleFolder, "Movie.zh.ass"), string.Empty);

            var matches = await ExternalSubtitleDiscovery.FindLocalAsync(video, "zh-CN,en");

            Assert.Equal(["Movie.zh.ass", "Movie.srt"],
                matches.Select(static match => match.Candidate.FileName));
            File.Delete(Path.Combine(directory, "Other.mkv"));
            matches = await ExternalSubtitleDiscovery.FindLocalAsync(video, "zh-CN,en");
            Assert.Contains(matches, static match => match.Candidate.FileName == "sc.srt");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FindLocalAsync_LoadsDungeonMeshiLanguageSuffixes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-subtitles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string stem = "[Nekomoe kissaten&VCB-Studio] Dungeon Meshi [05][Ma10p_1080p][x265_flac]";
            var video = Path.Combine(directory, $"{stem}.mkv");
            await File.WriteAllTextAsync(video, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{stem}.JPSC.ass"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{stem}.JPTC.ass"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory,
                stem.Replace("[05]", "[06]", StringComparison.Ordinal) + ".JPSC.ass"), string.Empty);

            var matches = await ExternalSubtitleDiscovery.FindLocalAsync(video, "zh-CN,zh-TW,en");

            Assert.Equal([$"{stem}.JPSC.ass", $"{stem}.JPTC.ass"],
                matches.Select(static match => match.Candidate.FileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FindAsync_RanksSubfolderLanguageAndRejectsOtherEpisode()
    {
        var root = new Uri("https://dav.example.test/series/");
        var video = Resource(root, "Show.S01E02.mkv");
        var folder = Resource(root, "Subs/", isDirectory: true);
        var client = new FakeClient(root, new Dictionary<Uri, IReadOnlyList<MediaResource>>
        {
            [root] =
            [
                video,
                Resource(root, "Show.S01E02.srt"),
                Resource(root, "Show.S01E03.zh.srt"),
                folder,
            ],
            [folder.Uri] = [Resource(folder.Uri, "Show.S01E02.sc.ass")],
        });

        var matches = await ExternalSubtitleDiscovery.FindAsync(client, video, "zh-CN,en");

        Assert.Equal("Show.S01E02.sc.ass", matches[0].Resource.Name);
        Assert.Equal("Show.S01E02.srt", matches[1].Resource.Name);
        Assert.Equal(2, matches.Count);
        Assert.Equal(2, client.BrowseCount);
    }

    [Fact]
    public async Task FindAsync_DisabledModeDoesNotBrowse()
    {
        var root = new Uri("https://dav.example.test/series/");
        var video = Resource(root, "Movie.mkv");
        var client = new FakeClient(root, new Dictionary<Uri, IReadOnlyList<MediaResource>>());

        Assert.Empty(await ExternalSubtitleDiscovery.FindAsync(client, video, null, "no"));
        Assert.Equal(0, client.BrowseCount);
    }

    private static MediaResource Resource(Uri parent, string name, bool isDirectory = false) => new()
    {
        Uri = new Uri(parent, name),
        Name = name.TrimEnd('/'),
        IsDirectory = isDirectory,
    };

    private sealed class FakeClient(
        Uri root,
        IReadOnlyDictionary<Uri, IReadOnlyList<MediaResource>> listings) : IMediaSourceClient
    {
        public MediaSourceProtocol Protocol => MediaSourceProtocol.WebDav;
        public Uri Root => root;
        public int BrowseCount { get; private set; }

        public ValueTask<IReadOnlyList<MediaResource>> BrowseAsync(
            Uri? directory = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrowseCount++;
            return ValueTask.FromResult(listings.TryGetValue(directory ?? Root, out var resources)
                ? resources : (IReadOnlyList<MediaResource>)[]);
        }

        public ValueTask<MediaResource> GetAsync(Uri resource, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IMediaByteStream> OpenReadAsync(
            MediaResource resource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
