// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CanX.Plugins.Manifest;
using CanX.Plugins.Media;
using CanX.Plugins.Security;
using CanX.Streaming;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginMediaStreamManagerTests
{
    [Fact]
    public void Create_RequiresBothMediaAndHttpPermissions()
    {
        using var broker = new MediaStreamBroker();
        var playback = new PluginMediaPlaybackBroker();
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var withoutMedia = CreateManager(broker, playback, handler, [
            new PluginPermission(PluginCapabilities.Http, ["media.example.test"]),
        ]);

        var mediaDenied = Assert.Throws<PluginPermissionDeniedException>(() =>
            withoutMedia.Create("{\"url\":\"https://media.example.test/video.mp4\"}"));
        Assert.Equal(PluginCapabilities.MediaStream, mediaDenied.Capability);

        using var withoutHost = CreateManager(broker, playback, handler, [
            new PluginPermission(PluginCapabilities.MediaStream, []),
            new PluginPermission(PluginCapabilities.Http, ["other.example.test"]),
        ]);
        var hostDenied = Assert.Throws<PluginPermissionDeniedException>(() =>
            withoutHost.Create("{\"url\":\"https://media.example.test/video.mp4\"}"));
        Assert.Equal(PluginCapabilities.Http, hostDenied.Capability);
    }

    [Fact]
    public void Create_RejectsSensitiveHeadersAndUnapprovedRecipeHosts()
    {
        using var broker = new MediaStreamBroker();
        var playback = new PluginMediaPlaybackBroker();
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var manager = CreateManager(broker, playback, handler, Permissions());

        var headerError = Assert.Throws<PluginMediaStreamException>(() => manager.Create(
            "{\"url\":\"https://media.example.test/video.mp4\",\"headers\":{\"Authorization\":\"secret\"}}"));
        Assert.Equal("invalid_media_stream", headerError.Code);

        var hostError = Assert.Throws<PluginPermissionDeniedException>(() => manager.Create(
            "{\"url\":\"https://media.example.test/video.mp4\",\"allowedHosts\":[\"blocked.example.test\"]}"));
        Assert.Equal(PluginCapabilities.Http, hostError.Capability);
    }

    [Fact]
    public async Task HlsPlaylist_RewritesEveryResourceAndAppliesBoundedHeaders()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="https://cdn.example.test/key.bin"
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-STREAM-INF:BANDWIDTH=1000000
            variant.m3u8
            https://cdn.example.test/segment.ts
            """;
        var requests = new List<RequestSnapshot>();
        using var handler = new StubHandler((request, _) =>
        {
            requests.Add(RequestSnapshot.From(request));
            var body = request.RequestUri!.AbsolutePath.EndsWith("master.m3u8", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(playlist)
                : [1, 2, 3, 4];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            });
        });
        using var broker = new MediaStreamBroker();
        var playback = new PluginMediaPlaybackBroker();
        using var manager = CreateManager(broker, playback, handler, Permissions());

        var created = manager.Create("""
            {
              "url":"https://media.example.test/master.m3u8",
              "format":"hls",
              "allowedHosts":["cdn.example.test"],
              "headers":{"User-Agent":"CanX-Test"},
              "headersByHost":{"cdn.example.test":{"Referer":"https://media.example.test/"}}
            }
            """);
        using var result = JsonDocument.Parse(created);
        var id = result.RootElement.GetProperty("id").GetString()!;
        var root = result.RootElement.GetProperty("source").GetString()!;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var playbackSource = broker.GetPlaybackSource(root);
        Assert.StartsWith("http://127.0.0.1:", playbackSource, StringComparison.Ordinal);
        Assert.Equal(root, broker.GetPublicSource(playbackSource));
        Assert.True(broker.TryOpen(root, out var rootLease));
        using (rootLease)
        {
            var rewritten = ReadUtf8(rootLease);
            Assert.DoesNotContain("https://", rewritten, StringComparison.Ordinal);
            var resources = Regex.Matches(rewritten, "http://[^\\s\\\"]+")
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(4, resources.Length);

            var segment = Assert.Single(resources, source => source.EndsWith("segment.ts", StringComparison.Ordinal));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await http.GetByteArrayAsync(segment));
            var segmentRequest = Assert.Single(requests, item => item.Uri.AbsolutePath.EndsWith("segment.ts", StringComparison.Ordinal));
            Assert.Equal("CanX-Test", segmentRequest.Headers["User-Agent"]);
            Assert.Equal("https://media.example.test/", segmentRequest.Headers["Referer"]);

            Assert.True(manager.Release(id));
            Assert.False(broker.TryOpen(root, out _));
            Assert.False(broker.TryOpen(broker.GetPublicSource(segment), out _));
            Assert.Equal(root, broker.GetPublicSource(playbackSource));
        }
    }

    [Fact]
    public async Task HlsMaster_RewritesExtensionlessVariantsAndAudioPlaylists()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/master.m3u8" ? """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="Main",URI="audio?id=1"
                #EXT-X-STREAM-INF:BANDWIDTH=800000,AUDIO="audio"
                variant?id=1
                """ : """
                #EXTM3U
                #EXT-X-TARGETDURATION:6
                #EXTINF:6.0,
                segment.ts
                #EXT-X-ENDLIST
                """),
        }));
        using var broker = new MediaStreamBroker();
        using var manager = CreateManager(broker, new PluginMediaPlaybackBroker(), handler, Permissions());
        using var result = JsonDocument.Parse(manager.Create("""{"url":"https://media.example.test/master.m3u8"}"""));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var master = await http.GetStringAsync(broker.GetPlaybackSource(result.RootElement.GetProperty("source").GetString()!));
        var children = Regex.Matches(master, "http://[^\\s\\\"]+").Select(match => match.Value).ToArray();
        Assert.Equal(2, children.Length);
        foreach (var child in children)
        {
            Assert.EndsWith(".m3u8", child, StringComparison.Ordinal);
            var variant = await http.GetStringAsync(child);
            Assert.Contains("#EXTINF:6.0,", variant, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-ENDLIST", variant, StringComparison.Ordinal);
            Assert.DoesNotContain("media.example.test", variant, StringComparison.Ordinal);
            Assert.Contains("http://127.0.0.1:", variant, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("file:///C:/secret.ts")]
    [InlineData("https://blocked.example.test/video.ts")]
    public void HlsPlaylist_RejectsResourcesOutsideManagedTransport(string resource)
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"#EXTM3U\n#EXTINF:6,\n{resource}\n#EXT-X-ENDLIST"),
            RequestMessage = request,
        }));
        using var broker = new MediaStreamBroker();
        using var manager = CreateManager(broker, new PluginMediaPlaybackBroker(), handler, Permissions());
        using var result = JsonDocument.Parse(manager.Create("""{"url":"https://media.example.test/master.m3u8"}"""));
        var error = Assert.Throws<PluginMediaStreamException>(() => broker.TryOpen(result.RootElement.GetProperty("source").GetString()!, out _));
        Assert.Equal("stream_host_denied", error.Code);
    }

    [Fact]
    public void OrdinaryFile_StillUsesOnlyStreamCallback()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var broker = new MediaStreamBroker();
        using var manager = CreateManager(broker, new PluginMediaPlaybackBroker(), handler, Permissions());
        using var result = JsonDocument.Parse(manager.Create("""{"url":"https://media.example.test/video.mp4"}"""));
        var source = result.RootElement.GetProperty("source").GetString()!;
        Assert.StartsWith("canx://", source, StringComparison.Ordinal);
        Assert.Equal(source, broker.GetPlaybackSource(source));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HlsByteRanges_HaveDistinctIdentitiesAndExplicitOffsets()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = request.RequestUri!.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal) ? new StringContent("""
                #EXTM3U
                #EXT-X-TARGETDURATION:3
                #EXTINF:3,
                #EXT-X-BYTERANGE:2@1
                video.m4s
                #EXTINF:3,
                #EXT-X-BYTERANGE:2
                video.m4s
                #EXT-X-ENDLIST
                """) : new ByteArrayContent([0, 1, 2, 3, 4, 5]),
        }));
        using var broker = new MediaStreamBroker();
        using var manager = CreateManager(broker, new PluginMediaPlaybackBroker(), handler, Permissions());
        using var created = JsonDocument.Parse(manager.Create("""{"url":"https://media.example.test/video.m3u8"}"""));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var url = broker.GetPlaybackSource(created.RootElement.GetProperty("source").GetString()!);
        var playlist = await http.GetStringAsync(url);
        Assert.Contains("#EXT-X-BYTERANGE:2@1", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-BYTERANGE:2@3", playlist, StringComparison.Ordinal);
        var segments = Regex.Matches(playlist, "http://[^\\s\\\"]+").Select(match => match.Value).ToArray();
        Assert.Equal(2, segments.Length);
        Assert.NotEqual(segments[0], segments[1]);
        Assert.Equal(playlist, await http.GetStringAsync(url));
        for (var index = 0; index < segments.Length; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, segments[index]);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1 + 2 * index, 2 + 2 * index);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(new byte[] { (byte)(1 + 2 * index), (byte)(2 + 2 * index) }, await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public void HlsOpen_RejectsRedirectOutsideRecipeAndManifestScopes()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://blocked.example.test/master.m3u8") },
            RequestMessage = request,
        }));
        using var broker = new MediaStreamBroker();
        var playback = new PluginMediaPlaybackBroker();
        using var manager = CreateManager(broker, playback, handler, Permissions());
        var created = manager.Create("{\"url\":\"https://media.example.test/master.m3u8\",\"format\":\"hls\"}");
        using var result = JsonDocument.Parse(created);
        var source = result.RootElement.GetProperty("source").GetString()!;

        var error = Assert.Throws<PluginMediaStreamException>(() => broker.TryOpen(source, out _));
        Assert.Equal("stream_host_denied", error.Code);
        Assert.Single(handler.Requests);
    }

    private static PluginMediaStreamManager CreateManager(
        MediaStreamBroker broker,
        PluginMediaPlaybackBroker playback,
        HttpMessageHandler handler,
        IReadOnlyList<PluginPermission> permissions)
    {
        var manifest = new PluginManifest(
            1,
            "org.canx.media-test",
            "Media test",
            null,
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            permissions);
        return new PluginMediaStreamManager(
            manifest.Id,
            PluginPermissionSet.FromManifest(manifest),
            broker,
            playback,
            () => handler);
    }

    private static PluginPermission[] Permissions() =>
    [
        new PluginPermission(PluginCapabilities.MediaStream, []),
        new PluginPermission(PluginCapabilities.Http, ["media.example.test", "cdn.example.test"]),
    ];

    private static string ReadUtf8(MediaStreamLease lease)
    {
        using var output = new MemoryStream();
        Span<byte> buffer = stackalloc byte[256];
        while (true)
        {
            var read = lease.Read(buffer);
            if (read == 0) return Encoding.UTF8.GetString(output.ToArray());
            output.Write(buffer[..read]);
        }
    }

    private sealed record RequestSnapshot(Uri Uri, IReadOnlyDictionary<string, string> Headers)
    {
        public static RequestSnapshot From(HttpRequestMessage request) => new(
            request.RequestUri!,
            request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return callback(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // The manager owns its transport, while these tests keep one handler
            // alive across more than one negative-path manager.
        }
    }
}
