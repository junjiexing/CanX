// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Streaming;

public sealed class MemoryMediaByteStream(ReadOnlyMemory<byte> content) : IMediaByteStream
{
    private readonly ReadOnlyMemory<byte> _content = content;
    private readonly Lock _gate = new();
    private int _position;
    private bool _disposed;

    public bool CanSeek => true;

    public long? Length => _content.Length;

    public int Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(destination.Length, _content.Length - _position);
            if (count <= 0)
            {
                return 0;
            }
            _content.Span.Slice(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }
    }

    public long Seek(long absoluteOffset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _position = checked((int)Math.Min(absoluteOffset, _content.Length));
            return _position;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}
