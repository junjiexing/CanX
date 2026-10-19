// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.MediaAccess;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class SmbMediaSourceClientTests
{
    [Fact]
    public async Task Constructor_NormalizesShareRootWithoutEmbeddingCredentials()
    {
        await using var client = new SmbMediaSourceClient(new Uri("smb://server.example/media/movies"));

        Assert.Equal(new Uri("smb://server.example/media/movies/"), client.Root);
        Assert.Equal(MediaSourceProtocol.Smb, client.Protocol);
        Assert.Empty(client.Root.UserInfo);
    }

    [Fact]
    public async Task Constructor_AcceptsServerRootForShareDiscovery()
    {
        await using var client = new SmbMediaSourceClient(new Uri("smb://server.example"));

        Assert.Equal(new Uri("smb://server.example/"), client.Root);
    }

    [Fact]
    public void ServerRoot_ResolvesSharesAndPathsOnConfiguredServer()
    {
        var endpoint = SmbEndpoint.Parse(new Uri("smb://server.example/"));

        var serverRoot = endpoint.Resolve(new Uri("smb://server.example/"), directory: true);
        var shareRoot = endpoint.Resolve(new Uri("smb://server.example/Media/"), directory: true);
        var file = endpoint.Resolve(new Uri("smb://server.example/Media/Shows/Episode.mkv"), directory: false);

        Assert.Null(serverRoot.ShareName);
        Assert.Empty(serverRoot.RelativePath);
        Assert.Equal("Media", shareRoot.ShareName);
        Assert.Empty(shareRoot.RelativePath);
        Assert.Equal(new Uri("smb://server.example/Media/"), shareRoot.Uri);
        Assert.Equal("Media", file.ShareName);
        Assert.Equal("Shows\\Episode.mkv", file.RelativePath);
    }

    [Fact]
    public void ServerRoot_RejectsResourcesOnAnotherServer()
    {
        var endpoint = SmbEndpoint.Parse(new Uri("smb://server.example/"));

        Assert.Throws<ArgumentException>(() =>
            endpoint.Resolve(new Uri("smb://other.example/media/"), directory: true));
    }

    [Theory]
    [InlineData("https://server.example/media/")]
    [InlineData("smb://alice:secret@server.example/media/")]
    [InlineData("smb://server.example/media/?token=secret")]
    [InlineData("smb://server.example/media/#private")]
    [InlineData("smb://server.example/media/%2Fescape")]
    public void Constructor_RejectsInvalidOrSensitiveEndpoint(string endpoint)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new SmbMediaSourceClient(new Uri(endpoint)));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowseAsync_RejectsResourceOutsideConfiguredRootBeforeConnecting()
    {
        await using var client = new SmbMediaSourceClient(new Uri("smb://server.example/media/movies/"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.BrowseAsync(new Uri("smb://server.example/media/private/")).AsTask());
    }
}
