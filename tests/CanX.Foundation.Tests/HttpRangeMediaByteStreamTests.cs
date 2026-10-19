// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class HttpRangeMediaByteStreamTests
{
    [Fact]
    public async Task OpenAsync_DiscoversLengthAndKeepsRangeSeeking()
    {
        var payload = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
        var ranges = new List<string>();
        using var client = new HttpClient(new RangeHandler(payload, ranges));
        using var stream = await HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            rangeBufferSize: 4);

        Assert.True(stream.CanSeek);
        Assert.Equal(payload.Length, stream.Length);
        Assert.Equal(10, stream.Seek(10, CancellationToken.None));
        var bytes = new byte[3];
        Assert.Equal(3, stream.Read(bytes, CancellationToken.None));
        Assert.Equal([10, 11, 12], bytes);
        Assert.Equal(["bytes=0-0", "bytes=10-13"], ranges);
    }

    [Fact]
    public async Task OpenAsync_ServerIgnoringRangeFallsBackToSameSequentialResponse()
    {
        var payload = Enumerable.Range(0, 8).Select(static value => (byte)value).ToArray();
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }));
        using var stream = await HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/movie.mkv"));

        Assert.False(stream.CanSeek);
        Assert.Equal(payload.Length, stream.Length);
        var first = new byte[3];
        Assert.Equal(3, stream.Read(first, CancellationToken.None));
        Assert.Equal([0, 1, 2], first);
        var remaining = new byte[5];
        Assert.Equal(5, stream.Read(remaining, CancellationToken.None));
        Assert.Equal([3, 4, 5, 6, 7], remaining);
        Assert.Equal(0, stream.Read(new byte[1], CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task OpenAsync_SequentialResponseCanRemainUnknownUntilEndOfStream()
    {
        byte[] payload = [4, 3, 2, 1];
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableMemoryStream(payload)),
            })));
        using var stream = await HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/live.mkv"));

        Assert.Null(stream.Length);
        var bytes = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(bytes, CancellationToken.None));
        Assert.Equal(0, stream.Read(new byte[1], CancellationToken.None));
        Assert.Equal(payload.Length, stream.Length);
    }

    [Fact]
    public async Task OpenAsync_RangeWithoutTotalLengthRetriesAsSequentialStream()
    {
        byte[] payload = [8, 6, 4, 2];
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            requests++;
            if (request.Headers.Range is not null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(payload[..1]),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }));
        using var stream = await HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/movie.mkv"));

        Assert.False(stream.CanSeek);
        var bytes = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(bytes, CancellationToken.None));
        Assert.Equal(payload, bytes);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task OpenAsync_RejectsChangedDeclaredLength()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            })));

        await Assert.ThrowsAsync<IOException>(() => HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            declaredLength: 4).AsTask());
    }

    [Fact]
    public async Task OpenAsync_RetriesTransientProbeFailures()
    {
        byte[] payload = [0, 1, 2, 3];
        var attempts = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            if (attempts < HttpReadRetry.MaximumAttempts)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var range = Assert.Single(request.Headers.Range!.Ranges);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[..1]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                range.From!.Value,
                range.To!.Value,
                payload.Length);
            return Task.FromResult(response);
        }));

        using var stream = await HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/movie.mkv"));

        Assert.True(stream.CanSeek);
        Assert.Equal(HttpReadRetry.MaximumAttempts, attempts);
    }

    [Fact]
    public async Task OpenAsync_DoesNotRetryPermanentHttpStatus()
    {
        var attempts = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/movie.mkv")).AsTask());

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RangeRead_RetriesTransientResponseAtSameOffset()
    {
        byte[] payload = [0, 1, 2, 3, 4, 5];
        var attempts = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            if (attempts == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

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
        using var stream = await HttpMediaByteStream.OpenAsync(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            rangeBufferSize: 2);

        Assert.Equal(4, stream.Seek(4, CancellationToken.None));
        var bytes = new byte[2];
        Assert.Equal(2, stream.Read(bytes, CancellationToken.None));
        Assert.Equal([4, 5], bytes);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ReadAndSeek_CoalesceReadsAndRequestTheSeekedRange()
    {
        var payload = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
        var ranges = new List<string>();
        using var client = new HttpClient(new RangeHandler(payload, ranges));
        using var stream = new HttpRangeMediaByteStream(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            payload.Length,
            bufferSize: 4);

        Span<byte> first = stackalloc byte[2];
        Assert.Equal(2, stream.Read(first, CancellationToken.None));
        Assert.Equal([0, 1], first.ToArray());

        Span<byte> second = stackalloc byte[2];
        Assert.Equal(2, stream.Read(second, CancellationToken.None));
        Assert.Equal([2, 3], second.ToArray());

        Assert.Equal(10, stream.Seek(10, CancellationToken.None));
        Span<byte> seeked = stackalloc byte[3];
        Assert.Equal(3, stream.Read(seeked, CancellationToken.None));
        Assert.Equal([10, 11, 12], seeked.ToArray());
        Assert.Equal(["bytes=0-3", "bytes=10-13"], ranges);
    }

    [Fact]
    public void Read_RejectsServersThatIgnoreRangeRequests()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            })));
        using var stream = new HttpRangeMediaByteStream(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            3,
            bufferSize: 2);

        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], CancellationToken.None));
    }

    [Fact]
    public void Read_RetriesWhenTheRangeResponseBodyDisconnects()
    {
        byte[] payload = [0, 1, 2, 3];
        var attempts = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var from = Assert.IsType<long>(range.From);
            var to = Assert.IsType<long>(range.To);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = attempts == 1
                    ? new StreamContent(new FailingReadStream(new IOException("connection reset")))
                    : new ByteArrayContent(payload[(int)from..((int)to + 1)]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
            return Task.FromResult(response);
        }));
        using var stream = new HttpRangeMediaByteStream(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            payload.Length,
            bufferSize: payload.Length);

        var bytes = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(bytes, CancellationToken.None));

        Assert.Equal(payload, bytes);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Read_StopsAfterMaximumResponseBodyAttempts()
    {
        var attempts = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new FailingReadStream(new IOException("connection reset"))),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1);
            return Task.FromResult(response);
        }));
        using var stream = new HttpRangeMediaByteStream(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            length: 1,
            bufferSize: 1);

        Assert.Throws<IOException>(() => stream.Read(new byte[1], CancellationToken.None));
        Assert.Equal(HttpReadRetry.MaximumAttempts, attempts);
    }

    [Fact]
    public void Read_DoesNotRetryCallerCancellation()
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new FailingReadStream(
                    new OperationCanceledException(cancellation.Token))),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1);
            cancellation.Cancel();
            return Task.FromResult(response);
        }));
        using var stream = new HttpRangeMediaByteStream(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            length: 1,
            bufferSize: 1);

        Assert.ThrowsAny<OperationCanceledException>(() => stream.Read(new byte[1], cancellation.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Read_DoesNotRetryAChangedEntityTag()
    {
        var attempts = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1);
            response.Headers.ETag = new EntityTagHeaderValue("\"changed\"");
            return Task.FromResult(response);
        }));
        using var stream = new HttpRangeMediaByteStream(
            client,
            new Uri("https://media.example.test/movie.mkv"),
            length: 1,
            bufferSize: 1,
            entityTag: "\"original\"");

        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[1], CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Constructor_RejectsCredentialsEmbeddedInUri()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));

        Assert.Throws<ArgumentException>(() => new HttpRangeMediaByteStream(
            client,
            new Uri("https://user:secret@example.test/movie.mkv"),
            1));
    }

    private sealed class RangeHandler(byte[] payload, List<string> ranges) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var from = Assert.IsType<long>(range.From);
            var to = Assert.IsType<long>(range.To);
            ranges.Add(request.Headers.Range.ToString());
            var bytes = payload[(int)from..((int)to + 1)];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
            return Task.FromResult(response);
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }

    private sealed class NonSeekableMemoryStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException<int>(exception);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
