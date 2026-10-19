// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Streaming;

namespace CanX.MediaAccess;

/// <summary>
/// Recreates a network byte stream after a transient read failure. Once bytes
/// have been delivered, retry is only safe when the source supports seeking
/// back to the exact logical offset.
/// </summary>
internal sealed class RetryingMediaByteStream : IMediaByteStream
{
    private readonly Func<IMediaByteStream> _streamFactory;
    private readonly Func<Exception, bool> _isTransient;
    private readonly long? _length;
    private readonly bool _canSeek;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly Lock _sync = new();
    private IMediaByteStream? _stream;
    private long _position;
    private bool _disposed;

    public RetryingMediaByteStream(
        Func<IMediaByteStream> streamFactory,
        long? length,
        bool canSeek,
        Func<Exception, bool>? isTransient = null,
        int maximumAttempts = NetworkMediaRetry.MaximumAttempts,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        if (retryDelay is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        _streamFactory = streamFactory;
        _length = length;
        _canSeek = canSeek;
        _isTransient = isTransient ?? NetworkMediaRetry.IsTransient;
        _maximumAttempts = maximumAttempts;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
    }

    public bool CanSeek => _canSeek;

    public long? Length
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _length ?? _stream?.Length;
            }
        }
    }

    public int Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_length is { } length && _position >= length)
            {
                return 0;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var stream = EnsureStream(cancellationToken);
                    var target = _length is { } knownLength
                        ? destination[..checked((int)Math.Min(destination.Length, knownLength - _position))]
                        : destination;
                    var read = stream.Read(target, cancellationToken);
                    if (read == 0 && _length is { } expectedLength && _position < expectedLength)
                    {
                        throw new IOException("The network media source ended before its declared length.");
                    }

                    _position += read;
                    return read;
                }
                catch (Exception exception) when (ShouldRetry(exception, attempt, cancellationToken))
                {
                    CloseStream();
                    DelayBeforeRetry(attempt, cancellationToken);
                }
                catch
                {
                    CloseStream();
                    throw;
                }
            }
        }
    }

    public long Seek(long absoluteOffset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_canSeek)
            {
                throw new NotSupportedException("The network media source does not support seeking.");
            }

            if (_stream is not null)
            {
                _ = _stream.Seek(absoluteOffset, cancellationToken);
            }

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
            CloseStream();
        }
    }

    private IMediaByteStream EnsureStream(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return _stream;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stream = _streamFactory() ??
            throw new InvalidOperationException("The media stream factory returned null.");
        try
        {
            if (stream.CanSeek != _canSeek)
            {
                throw new InvalidOperationException("The recreated media stream changed its seek capability.");
            }

            if (_position > 0)
            {
                _ = stream.Seek(_position, cancellationToken);
            }

            _stream = stream;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private bool ShouldRetry(
        Exception exception,
        int attempt,
        CancellationToken cancellationToken) =>
        attempt < _maximumAttempts &&
        !cancellationToken.IsCancellationRequested &&
        (_position == 0 || _canSeek) &&
        _isTransient(exception);

    private void DelayBeforeRetry(int attempt, CancellationToken cancellationToken)
    {
        if (_retryDelay == TimeSpan.Zero)
        {
            return;
        }

        Task.Delay(_retryDelay * attempt, cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    private void CloseStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Closing a failed transport must not hide the original read error.
        }

        _stream = null;
    }
}
