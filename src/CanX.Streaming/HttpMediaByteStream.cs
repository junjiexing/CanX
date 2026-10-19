// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace CanX.Streaming;

/// <summary>
/// Probes an HTTP resource with a one-byte range request and selects either a
/// seekable range stream or a sequential stream without exposing its real URL
/// to the media player.
/// </summary>
public static class HttpMediaByteStream
{
    public static async ValueTask<IMediaByteStream> OpenAsync(
        HttpClient client,
        Uri source,
        long? declaredLength = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        bool ownsClient = false,
        int rangeBufferSize = 2 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateSource(source);
        if (declaredLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredLength));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(rangeBufferSize, 1);
        HttpResponseMessage? response = null;
        try
        {
            response = await HttpReadRetry.SendAsync(
                client,
                () => CreateRequest(source, requestHeaders, new RangeHeaderValue(0, 0)),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != 0 || range.To is null ||
                    (range.Length is { } totalLength && totalLength <= range.To.Value))
                {
                    throw new IOException("The HTTP media source returned an invalid Content-Range header.");
                }

                var length = range.Length ?? declaredLength;
                if (length is null)
                {
                    response.Dispose();
                    response = null;
                    response = await HttpReadRetry.SendAsync(
                        client,
                        () => CreateRequest(source, requestHeaders),
                        cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new HttpRequestException(
                            $"HTTP sequential media request failed with status {(int)response.StatusCode}.",
                            inner: null,
                            response.StatusCode);
                    }

                    var unknownLengthStream = await CreateSequentialAsync(
                        response,
                        client,
                        declaredLength: null,
                        ownsClient,
                        cancellationToken).ConfigureAwait(false);
                    response = null;
                    return unknownLengthStream;
                }

                if (declaredLength is { } expectedLength && expectedLength != length.Value)
                {
                    throw new IOException("The HTTP media resource changed before it was opened.");
                }

                var entityTag = response.Headers.ETag is { IsWeak: false } responseTag
                    ? responseTag.ToString()
                    : null;
                response.Dispose();
                response = null;
                return new HttpRangeMediaByteStream(
                    client,
                    source,
                    length.Value,
                    requestHeaders,
                    ownsClient,
                    rangeBufferSize,
                    entityTag);
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var sequential = await CreateSequentialAsync(
                    response,
                    client,
                    declaredLength,
                    ownsClient,
                    cancellationToken).ConfigureAwait(false);
                response = null;
                return sequential;
            }

            throw new HttpRequestException(
                $"HTTP media request failed with status {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }
        catch
        {
            response?.Dispose();
            if (ownsClient)
            {
                client.Dispose();
            }

            throw;
        }
    }

    private static async ValueTask<IMediaByteStream> CreateSequentialAsync(
        HttpResponseMessage response,
        HttpClient client,
        long? declaredLength,
        bool ownsClient,
        CancellationToken cancellationToken)
    {
        var responseLength = response.Content.Headers.ContentLength;
        if (declaredLength is { } expectedLength &&
            responseLength is { } actualLength &&
            expectedLength != actualLength)
        {
            throw new IOException("The HTTP media resource changed before it was opened.");
        }

        var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HttpSequentialMediaByteStream(
            response,
            stream,
            responseLength ?? declaredLength,
            client,
            ownsClient);
    }

    private static void AddHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? requestHeaders)
    {
        if (requestHeaders is null)
        {
            return;
        }

        foreach (var header in requestHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"'{header.Key}' is not a valid HTTP request header.");
            }
        }
    }

    private static HttpRequestMessage CreateRequest(
        Uri source,
        IReadOnlyDictionary<string, string>? requestHeaders,
        RangeHeaderValue? range = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, source);
        try
        {
            request.Headers.Range = range;
            AddHeaders(request, requestHeaders);
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static void ValidateSource(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri ||
            (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The media source must be an absolute HTTP or HTTPS URI.", nameof(source));
        }

        if (!string.IsNullOrEmpty(source.UserInfo))
        {
            throw new ArgumentException("Credentials must not be embedded in the media URI.", nameof(source));
        }
    }
}

internal sealed class HttpSequentialMediaByteStream : IMediaByteStream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _stream;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Lock _sync = new();
    private long? _length;
    private long _position;
    private bool _disposed;

    public HttpSequentialMediaByteStream(
        HttpResponseMessage response,
        Stream stream,
        long? length,
        HttpClient client,
        bool ownsClient)
    {
        _response = response;
        _stream = stream;
        _length = length;
        _client = client;
        _ownsClient = ownsClient;
    }

    public bool CanSeek => false;

    public long? Length
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _length;
            }
        }
    }

    public int Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        var rented = ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_length is { } length && _position >= length)
                {
                    return 0;
                }

                var requested = _length is { } knownLength
                    ? checked((int)Math.Min(destination.Length, knownLength - _position))
                    : destination.Length;
                var read = _stream
                    .ReadAsync(rented.AsMemory(0, requested), cancellationToken)
                    .AsTask()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                if (read == 0)
                {
                    if (_length is { } expectedLength && _position < expectedLength)
                    {
                        throw new IOException("The HTTP media source ended before its declared length.");
                    }

                    _length ??= _position;
                    return 0;
                }

                rented.AsSpan(0, read).CopyTo(destination);
                _position += read;
                return read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public long Seek(long absoluteOffset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("The HTTP server does not support byte-range seeking.");
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
            _response.Dispose();
            if (_ownsClient)
            {
                _client.Dispose();
            }
        }
    }
}
