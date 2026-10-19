// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using FluentFTP;

namespace CanX.MediaAccess;

/// <summary>
/// Adapts an FTP RETR data connection to the blocking random-access contract
/// used by libmpv. A seek closes the current transfer and starts a new one with
/// FTP REST, keeping credentials and the real server path outside libmpv.
/// </summary>
internal sealed class FtpMediaByteStream : CanX.Streaming.IMediaByteStream
{
    private readonly Func<AsyncFtpClient> _clientFactory;
    private readonly string _path;
    private readonly long? _length;
    private readonly bool _canSeek;
    private readonly Lock _sync = new();
    private AsyncFtpClient? _client;
    private Stream? _stream;
    private long _position;
    private bool _endOfStream;
    private bool _disposed;

    public FtpMediaByteStream(
        Func<AsyncFtpClient> clientFactory,
        string path,
        long? length,
        bool canSeek)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _clientFactory = clientFactory;
        _path = path;
        _length = length;
        _canSeek = canSeek;
    }

    public bool CanSeek => _canSeek;

    public long? Length => _length;

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
                if (_endOfStream)
                {
                    return 0;
                }

                if (_length is { } length && _position >= length)
                {
                    return 0;
                }

                try
                {
                    EnsureTransfer(cancellationToken);
                    var requested = _length is { } knownLength
                        ? checked((int)Math.Min(destination.Length, knownLength - _position))
                        : destination.Length;
                    var read = _stream!
                        .ReadAsync(rented.AsMemory(0, requested), cancellationToken)
                        .AsTask()
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                    rented.AsSpan(0, read).CopyTo(destination);
                    _position += read;
                    if (read == 0)
                    {
                        _endOfStream = true;
                        CloseTransfer();
                    }

                    return read;
                }
                catch
                {
                    CloseTransfer();
                    throw;
                }
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
            if (absoluteOffset == _position)
            {
                return _position;
            }

            if (!_canSeek)
            {
                throw new NotSupportedException("The FTP server does not support REST stream seeking.");
            }

            CloseTransfer();
            _position = absoluteOffset;
            _endOfStream = false;
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
            CloseTransfer();
        }
    }

    private void EnsureTransfer(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        var client = _clientFactory();
        try
        {
            Task.Run(
                    () => client.Connect(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            var stream = Task.Run(
                    () => client.OpenRead(
                        _path,
                        FtpDataType.Binary,
                        _position,
                        _length ?? -1,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            _client = client;
            _stream = stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void CloseTransfer()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // A seek or cancellation intentionally terminates an active RETR.
        }

        _stream = null;
        try
        {
            _client?.Dispose();
        }
        catch (Exception)
        {
            // Disposal must not mask the read/seek result seen by libmpv.
        }

        _client = null;
    }
}
