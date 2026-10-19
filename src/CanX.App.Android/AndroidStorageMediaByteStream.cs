// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using Avalonia.Platform.Storage;
using CanX.Streaming;

namespace CanX.App.Android;

/// <summary>
/// Exposes an Android Storage Access Framework document to libmpv. Providers
/// that do not expose a seekable descriptor are reopened and skipped forward,
/// preserving correctness for tail-indexed MP4 and Matroska sources.
/// </summary>
internal sealed class AndroidStorageMediaByteStream : IMediaByteStream
{
    private readonly IStorageFile _file;
    private readonly Lock _gate = new();
    private Stream _stream;
    private readonly long? _length;
    private long _position;
    private bool _disposed;

    public AndroidStorageMediaByteStream(IStorageFile file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _stream = file.OpenReadAsync().GetAwaiter().GetResult();
        var size = file.GetBasicPropertiesAsync().GetAwaiter().GetResult().Size;
        _length = size <= long.MaxValue ? (long)size : null;
    }

    public bool CanSeek => !_disposed;

    public long? Length => _disposed
        ? throw new ObjectDisposedException(nameof(AndroidStorageMediaByteStream))
        : _length;

    public int Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty) return 0;
        var buffer = ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var read = _stream.ReadAsync(
                        buffer.AsMemory(0, destination.Length),
                        cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                buffer.AsSpan(0, read).CopyTo(destination);
                _position += read;
                return read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public long Seek(long absoluteOffset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_stream.CanSeek)
            {
                _position = _stream.Seek(absoluteOffset, SeekOrigin.Begin);
                return _position;
            }

            _stream.Dispose();
            _stream = _file.OpenReadAsync().GetAwaiter().GetResult();
            _position = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                while (_position < absoluteOffset)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var wanted = (int)Math.Min(buffer.Length, absoluteOffset - _position);
                    var read = _stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    if (read == 0) throw new EndOfStreamException("The document ended before the requested seek offset.");
                    _position += read;
                }
                return _position;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }
}
