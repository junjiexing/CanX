// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CanX.MediaAccess;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class WebDavMediaSourceClientTests
{
    [Fact]
    public async Task BrowseAsync_ParsesSuccessfulChildrenAndIgnoresOutsideResources()
    {
        var requests = new List<(HttpMethod Method, string Uri, string? Depth)>();
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Depth", out var values) ? values.Single() : null));
            return Task.FromResult(MultiStatus("""
                <d:multistatus xmlns:d="DAV:">
                  <d:response>
                    <d:href>/dav/media/</d:href>
                    <d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:displayname>media</d:displayname><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat>
                  </d:response>
                  <d:response>
                    <d:href>/dav/media/Series%201/</d:href>
                    <d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:displayname>Series 1</d:displayname><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat>
                  </d:response>
                  <d:response>
                    <d:href>/dav/media/movie.mkv</d:href>
                    <d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:getcontentlength>12</d:getcontentlength><d:getcontenttype>video/x-matroska</d:getcontenttype><d:getetag>&quot;v1&quot;</d:getetag></d:prop></d:propstat>
                  </d:response>
                  <d:response>
                    <d:href>https://other.example.test/private.mkv</d:href>
                    <d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:getcontentlength>99</d:getcontentlength></d:prop></d:propstat>
                  </d:response>
                </d:multistatus>
                """));
        }));
        await using var client = new WebDavMediaSourceClient(
            httpClient,
            new Uri("https://dav.example.test/dav/media"));

        var resources = await client.BrowseAsync();

        Assert.Equal(2, resources.Count);
        Assert.True(resources[0].IsDirectory);
        Assert.Equal("Series 1", resources[0].Name);
        Assert.False(resources[1].IsDirectory);
        Assert.Equal("movie.mkv", resources[1].Name);
        Assert.Equal(12, resources[1].Length);
        Assert.Equal("video/x-matroska", resources[1].ContentType);
        Assert.Equal("\"v1\"", resources[1].EntityTag);
        Assert.Equal([(new HttpMethod("PROPFIND"), "https://dav.example.test/dav/media/", "1")], requests);
    }

    [Fact]
    public async Task OpenReadAsync_UsesRangeStreamWithoutExposingCredentialsInUri()
    {
        byte[] payload = [0, 1, 2, 3, 4, 5];
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var from = Assert.IsType<long>(range.From);
            var to = Assert.IsType<long>(range.To);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)from..((int)to + 1)]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
            return Task.FromResult(response);
        }));
        await using var client = new WebDavMediaSourceClient(
            httpClient,
            new Uri("https://dav.example.test/media/"));
        var resource = new MediaResource
        {
            Uri = new Uri("https://dav.example.test/media/movie.mp4"),
            Name = "movie.mp4",
            IsDirectory = false,
            Length = payload.Length,
        };

        using var stream = await client.OpenReadAsync(resource);
        Assert.Equal(4, stream.Seek(4, CancellationToken.None));
        Span<byte> bytes = stackalloc byte[2];
        Assert.Equal(2, stream.Read(bytes, CancellationToken.None));
        Assert.Equal([4, 5], bytes.ToArray());
    }

    [Fact]
    public async Task OpenReadAsync_StreamsUnknownLengthWhenServerIgnoresRange()
    {
        byte[] payload = [0, 1, 2, 3, 4, 5];
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("bytes=0-0", request.Headers.Range?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }));
        await using var client = new WebDavMediaSourceClient(
            httpClient,
            new Uri("https://dav.example.test/media/"));
        var resource = new MediaResource
        {
            Uri = new Uri("https://dav.example.test/media/movie.mp4"),
            Name = "movie.mp4",
            IsDirectory = false,
        };

        using var stream = await client.OpenReadAsync(resource);
        Assert.False(stream.CanSeek);
        var bytes = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(bytes, CancellationToken.None));
        Assert.Equal(payload, bytes);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task BrowseAsync_RejectsNavigationOutsideConfiguredRoot()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("No request should be sent.")));
        await using var client = new WebDavMediaSourceClient(
            httpClient,
            new Uri("https://dav.example.test/media/"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.BrowseAsync(new Uri("https://dav.example.test/private/")).AsTask());
    }

    [Fact]
    public async Task BrowseAsync_RetriesTransientServerResponse()
    {
        var attempts = 0;
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            Assert.Equal(new HttpMethod("PROPFIND"), request.Method);
            return Task.FromResult(attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : MultiStatus("""
                    <d:multistatus xmlns:d="DAV:">
                      <d:response>
                        <d:href>/media/</d:href>
                        <d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat>
                      </d:response>
                    </d:multistatus>
                    """));
        }));
        await using var client = new WebDavMediaSourceClient(
            httpClient,
            new Uri("https://dav.example.test/media/"));

        var resources = await client.BrowseAsync();

        Assert.Empty(resources);
        Assert.Equal(2, attempts);
    }

    private static HttpResponseMessage MultiStatus(string xml) => new((HttpStatusCode)207)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }
}
