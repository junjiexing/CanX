// SPDX-License-Identifier: GPL-3.0-or-later

using SMBLibrary;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace CanX.MediaAccess;

internal sealed class SmbMediaByteStream : CanX.Streaming.IMediaByteStream
{
    private readonly Func<SmbConnection> _connectionFactory;
    private readonly string _path;
    private readonly long? _length;
    private readonly Lock _sync = new();
    private SmbConnection? _connection;
    private object? _handle;
    private long _position;
    private bool _disposed;

    public SmbMediaByteStream(Func<SmbConnection> connectionFactory, string path, long? length)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _connectionFactory = connectionFactory;
        _path = path;
        _length = length;
    }

    public bool CanSeek => true;

    public long? Length => _length;

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

            EnsureFileOpen();
            var connection = _connection!;
            var requested = checked((int)Math.Min(destination.Length, connection.FileStore.MaxReadSize));
            if (_length is { } knownLength)
            {
                requested = checked((int)Math.Min(requested, knownLength - _position));
            }

            using var registration = cancellationToken.UnsafeRegister(
                static state => ((SmbConnection)state!).Abort(),
                connection);
            var status = connection.FileStore.ReadFile(out var data, _handle!, _position, requested);
            cancellationToken.ThrowIfCancellationRequested();
            if (status == NTStatus.STATUS_END_OF_FILE)
            {
                return 0;
            }

            EnsureSuccess(status, "read");
            if (data.Length > requested)
            {
                throw new IOException("The SMB server returned more data than requested.");
            }

            data.AsSpan().CopyTo(destination);
            _position += data.Length;
            return data.Length;
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
            CloseConnection();
        }
    }

    private void EnsureFileOpen()
    {
        if (_handle is not null)
        {
            return;
        }

        var connection = _connectionFactory();
        try
        {
            var status = connection.FileStore.CreateFile(
                out var handle,
                out _,
                _path,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                SmbFileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            EnsureSuccess(status, "open file");
            _connection = connection;
            _handle = handle;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void CloseConnection()
    {
        if (_connection is null)
        {
            return;
        }

        if (_handle is not null)
        {
            try
            {
                _ = _connection.FileStore.CloseFile(_handle);
            }
            catch (Exception)
            {
                // A cancelled read can already have closed the connection.
            }

            _handle = null;
        }

        _connection.Dispose();
        _connection = null;
    }

    private static void EnsureSuccess(NTStatus status, string operation)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"SMB {operation} failed with status {status}.");
        }
    }
}
