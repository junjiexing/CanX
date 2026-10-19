// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.MediaAccess;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class FtpMediaSourceClientTests
{
    [Fact]
    public async Task Constructor_NormalizesRootWithoutEmbeddingCredentials()
    {
        await using var client = new FtpMediaSourceClient(new Uri("ftp://example.test/media"));

        Assert.Equal(new Uri("ftp://example.test/media/"), client.Root);
        Assert.Equal(MediaSourceProtocol.Ftp, client.Protocol);
        Assert.Empty(client.Root.UserInfo);
    }

    [Theory]
    [InlineData("https://example.test/media/")]
    [InlineData("ftp://alice:secret@example.test/media/")]
    [InlineData("ftp://example.test/media/?token=secret")]
    [InlineData("ftp://example.test/media/#private")]
    public void Constructor_RejectsUnsupportedOrSensitiveEndpoint(string endpoint)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FtpMediaSourceClient(new Uri(endpoint)));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowseAsync_RejectsResourceOutsideConfiguredRootBeforeConnecting()
    {
        await using var client = new FtpMediaSourceClient(new Uri("ftp://example.test/media/"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.BrowseAsync(new Uri("ftp://example.test/private/")).AsTask());
    }
}
