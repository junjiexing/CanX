// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;

namespace CanX.Streaming;

/// <summary>
/// Adapts a managed <see cref="Stream"/> to the media byte-stream contract.
/// The temporary buffer lets cancellable Stream.ReadAsync implementations
/// unblock a native callback without retaining native memory after it returns.
/// </summary>
public sealed class DotNetMediaByteStream : IMediaByteStream
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Lock _sync = new();
    private bool _disposed;

    public DotNetMediaByteStream(Stream stream, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
    }

    public bool CanSeek => !_disposed && _stream.CanSeek;

    public long? Length
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _stream.CanSeek ? _stream.Length : null;
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
            int read;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                read = _stream
                    .ReadAsync(rented.AsMemory(0, destination.Length), cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }

            rented.AsSpan(0, read).CopyTo(destination);
            return read;
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
            if (!_stream.CanSeek)
            {
                throw new NotSupportedException("The stream does not support seeking.");
            }

            return _stream.Seek(absoluteOffset, SeekOrigin.Begin);
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
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }
    }
}
