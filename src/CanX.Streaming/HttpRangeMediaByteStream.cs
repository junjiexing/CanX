// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace CanX.Streaming;

/// <summary>
/// Exposes an HTTP resource with byte-range support as a blocking media stream.
/// Reads are coalesced into bounded windows so libmpv does not issue one HTTP
/// request for every small demuxer read.
/// </summary>
public sealed class HttpRangeMediaByteStream : IMediaByteStream
{
    private const int DefaultBufferSize = 2 * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly Uri _source;
    private readonly long _length;
    private readonly bool _ownsClient;
    private readonly IReadOnlyDictionary<string, string> _requestHeaders;
    private readonly byte[] _buffer;
    private readonly int _bufferCapacity;
    private readonly Lock _sync = new();
    private long _position;
    private long _bufferOffset = -1;
    private int _bufferLength;
    private string? _entityTag;
    private bool _disposed;

    public HttpRangeMediaByteStream(
        HttpClient client,
        Uri source,
        long length,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        bool ownsClient = false,
        int bufferSize = DefaultBufferSize,
        string? entityTag = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (!source.IsAbsoluteUri ||
            (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The media source must be an absolute HTTP or HTTPS URI.", nameof(source));
        }

        if (!string.IsNullOrEmpty(source.UserInfo))
        {
            throw new ArgumentException("Credentials must not be embedded in the media URI.", nameof(source));
        }

        _client = client;
        _source = source;
        _length = length;
        _ownsClient = ownsClient;
        _requestHeaders = requestHeaders is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase);
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        _bufferCapacity = bufferSize;
        _entityTag = entityTag;
    }

    public bool CanSeek => true;

    public long? Length => _length;

    public int Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (destination.IsEmpty || _position >= _length)
            {
                return 0;
            }

            if (_bufferOffset < 0 ||
                _position < _bufferOffset ||
                _position >= _bufferOffset + _bufferLength)
            {
                FillBuffer(cancellationToken);
            }

            var bufferIndex = checked((int)(_position - _bufferOffset));
            var readable = Math.Min(
                destination.Length,
                _bufferLength - bufferIndex);
            if (readable <= 0)
            {
                throw new IOException($"The HTTP media source ended before its declared length {_length}.");
            }

            _buffer.AsSpan(bufferIndex, readable).CopyTo(destination);
            _position += readable;
            return readable;
        }
    }

    public long Seek(long absoluteOffset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _position = absoluteOffset;
            return _position;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
            if (_ownsClient)
            {
                _client.Dispose();
            }
        }
    }

    private void FillBuffer(CancellationToken cancellationToken)
    {
        var lastByte = Math.Min(_length - 1, checked(_position + _bufferCapacity - 1L));
        _bufferLength = ReadRangeWithRetryAsync(lastByte, cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        _bufferOffset = _position;
    }

    private async Task<int> ReadRangeWithRetryAsync(long lastByte, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadRangeOnceAsync(lastByte, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < HttpReadRetry.MaximumAttempts &&
                HttpReadRetry.IsTransient(exception, cancellationToken))
            {
                await HttpReadRetry.DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> ReadRangeOnceAsync(long lastByte, CancellationToken cancellationToken)
    {
        using var request = CreateRangeRequest(lastByte);
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (HttpReadRetry.IsTransientStatus(response.StatusCode))
        {
            throw new HttpRequestException(
                $"HTTP range request failed with status {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new NotSupportedException(
                $"The HTTP media source did not honor byte range {_position}-{lastByte} " +
                $"(status {(int)response.StatusCode}).");
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.From != _position || contentRange.To is not { } responseEnd)
        {
            throw new InvalidDataException("The HTTP media source returned an invalid Content-Range header.");
        }

        if (contentRange.Length is { } responseLength && responseLength != _length)
        {
            throw new InvalidDataException("The HTTP media resource changed while it was being read.");
        }

        var responseTag = response.Headers.ETag is { IsWeak: false } entityTag
            ? entityTag.ToString()
            : null;
        if (_entityTag is not null && responseTag is not null &&
            !string.Equals(_entityTag, responseTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The HTTP media resource changed while it was being read.");
        }

        _entityTag ??= responseTag;
        var expected = checked((int)Math.Min(_bufferCapacity, responseEnd - _position + 1));
        using var content = response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        var read = 0;
        while (read < expected)
        {
            var current = content
                .ReadAsync(_buffer.AsMemory(read, expected - read), cancellationToken)
                .AsTask()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        if (read == 0)
        {
            throw new IOException("The HTTP media source returned an empty byte range.");
        }

        return read;
    }

    private HttpRequestMessage CreateRangeRequest(long lastByte)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _source);
        try
        {
            request.Headers.Range = new RangeHeaderValue(_position, lastByte);
            if (_entityTag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Range", _entityTag);
            }

            foreach (var header in _requestHeaders)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    throw new InvalidOperationException($"'{header.Key}' is not a valid HTTP request header.");
                }
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }
}
