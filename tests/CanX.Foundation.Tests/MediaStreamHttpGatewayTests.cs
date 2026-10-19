// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class MediaStreamHttpGatewayTests
{
    [Fact]
    public async Task HttpTransport_SupportsRangesAndHeadAndKeepsCallbackStreamsPrivate()
    {
        using var broker = new MediaStreamBroker();
        using var ordinary = broker.Register("private.bin", Bytes, MediaStreamCachePolicy.Disabled);
        using var hls = broker.Register("video.ts", Bytes, transport: MediaStreamTransport.LoopbackHttp);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var url = broker.GetPlaybackSource(hls.Source);
        Assert.Equal(ordinary.Source, broker.GetPlaybackSource(ordinary.Source));
        Assert.Equal(hls.Source, broker.GetPublicSource(url));

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 4);
        using var range = await http.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("bytes 2-4/6", range.Content.Headers.ContentRange!.ToString());
        Assert.Equal(new byte[] { 2, 3, 4 }, await range.Content.ReadAsByteArrayAsync());

        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        Assert.Equal(6, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, url);
        invalidRequest.Headers.Range = new RangeHeaderValue(6, null);
        using var invalid = await http.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        Assert.Equal("bytes */6", invalid.Content.Headers.ContentRange!.ToString());

        var privateUrl = url[..(url.LastIndexOf('/') - 64)] + new Uri(ordinary.Source).AbsolutePath.TrimStart('/');
        using var denied = await http.GetAsync(privateUrl);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var badHost = new HttpRequestMessage(HttpMethod.Get, url);
        badHost.Headers.Host = "attacker.example.test";
        using var hostDenied = await http.SendAsync(badHost);
        Assert.Equal(HttpStatusCode.NotFound, hostDenied.StatusCode);
    }

    [Fact]
    public async Task HttpTransport_UsesOnePortAndStopsAfterFinalRelease()
    {
        using var broker = new MediaStreamBroker();
        using var first = broker.Register("first.ts", Bytes, transport: MediaStreamTransport.LoopbackHttp);
        using var second = broker.Register("second.ts", Bytes, transport: MediaStreamTransport.LoopbackHttp);
        var firstUrl = broker.GetPlaybackSource(first.Source);
        var secondUrl = broker.GetPlaybackSource(second.Source);
        Assert.Equal(new Uri(firstUrl).Authority, new Uri(secondUrl).Authority);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        first.Dispose();
        using var retired = await http.GetAsync(firstUrl);
        Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, await http.GetByteArrayAsync(secondUrl));
        second.Dispose();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(secondUrl));
        Assert.Equal(first.Source, broker.GetPublicSource(firstUrl));
        Assert.Equal(second.Source, broker.GetPublicSource(secondUrl));
    }

    [Fact]
    public async Task HttpTransport_ClientDisconnectCancelsBlockedRead()
    {
        using var broker = new MediaStreamBroker();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hls = broker.Register("blocked.ts", () => new BlockingStream(reading, closed), transport: MediaStreamTransport.LoopbackHttp);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var cancellation = new CancellationTokenSource();
        var request = http.GetAsync(broker.GetPlaybackSource(hls.Source), cancellation.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HttpTransport_ClientDisconnectCancelsOpenAndDoesNotBlockRelease()
    {
        using var broker = new MediaStreamBroker();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hls = broker.Register("slow.m3u8", token =>
        {
            opening.TrySetResult();
            try
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return Bytes();
            }
            finally { cancelled.TrySetResult(); }
        }, transport: MediaStreamTransport.LoopbackHttp);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var cancellation = new CancellationTokenSource();
        var request = http.GetAsync(broker.GetPlaybackSource(hls.Source), cancellation.Token);
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(hls.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HttpTransport_HandlesByteRangesOnSequentialSources()
    {
        using var broker = new MediaStreamBroker();
        using var hls = broker.Register("single.ts", () => new SequentialStream(), transport: MediaStreamTransport.LoopbackHttp);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, broker.GetPlaybackSource(hls.Source));
        request.Headers.Range = new RangeHeaderValue(2, 3);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HttpTransport_ReleaseCancelsPendingOpen()
    {
        using var broker = new MediaStreamBroker();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hls = broker.Register("slow.m3u8", token =>
        {
            opening.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return Bytes();
        }, transport: MediaStreamTransport.LoopbackHttp);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var request = http.GetAsync(broker.GetPlaybackSource(hls.Source));
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(hls.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        try { using var response = await request.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(response.IsSuccessStatusCode); }
        catch (HttpRequestException) { }
    }

    private static IMediaByteStream Bytes() => new DotNetMediaByteStream(new MemoryStream([0, 1, 2, 3, 4, 5]));

    private sealed class SequentialStream : IMediaByteStream
    {
        private readonly MemoryStream _stream = new([0, 1, 2, 3, 4, 5]);
        public bool CanSeek => false;
        public long? Length => _stream.Length;
        public int Read(Span<byte> destination, CancellationToken cancellationToken) => _stream.Read(destination);
        public long Seek(long absoluteOffset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() => _stream.Dispose();
    }

    private sealed class BlockingStream(TaskCompletionSource reading, TaskCompletionSource closed) : IMediaByteStream
    {
        public bool CanSeek => false;
        public long? Length => null;
        public int Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            reading.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }
        public long Seek(long absoluteOffset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() => closed.TrySetResult();
    }
}
