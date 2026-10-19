// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Win32.SafeHandles;
using CanX.Core.Downloads;
using CanX.Streaming;

namespace CanX.Downloads;

public sealed class Aria2MediaByteStream : IMediaByteStream
{
    private const long DefaultReadAheadBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PriorityRetryInterval = TimeSpan.FromMilliseconds(250);

    private readonly Guid _taskId;
    private readonly int _fileIndex;
    private readonly string _path;
    private readonly long _length;
    private readonly long _torrentOffset;
    private readonly long _pieceLength;
    private readonly Func<Guid, DownloadTaskSnapshot?> _snapshotProvider;
    private readonly Func<Guid, int, int, CancellationToken, ValueTask<bool>> _prioritizeRange;
    private Action<Guid>? _releasePriority;
    private readonly Lock _sync = new();
    private SafeFileHandle? _handle;
    private long _position;
    private int _lastPriorityFirst = -1;
    private int _lastPriorityLast = -1;
    private long _nextPriorityAttemptTicks;
    private bool _priorityApplied;
    private bool _disposed;

    public Aria2MediaByteStream(
        Guid taskId,
        int fileIndex,
        string path,
        long length,
        long torrentOffset,
        long pieceLength,
        Func<Guid, DownloadTaskSnapshot?> snapshotProvider,
        Func<Guid, int, int, CancellationToken, ValueTask<bool>> prioritizeRange,
        Action<Guid>? releasePriority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (fileIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(torrentOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pieceLength);
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(prioritizeRange);

        _taskId = taskId;
        _fileIndex = fileIndex;
        _path = Path.GetFullPath(path);
        _length = length;
        _torrentOffset = torrentOffset;
        _pieceLength = pieceLength;
        _snapshotProvider = snapshotProvider;
        _prioritizeRange = prioritizeRange;
        _releasePriority = releasePriority;
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

            var globalOffset = checked(_torrentOffset + _position);
            var piece = checked((int)(globalOffset / _pieceLength));
            var pieceEnd = checked((piece + 1L) * _pieceLength);
            var readableLength = checked((int)Math.Min(
                destination.Length,
                Math.Min(_length - _position, pieceEnd - globalOffset)));

            PrioritizeFrom(piece, cancellationToken);
            while (true)
            {
                var snapshot = RequireSnapshot();
                ThrowIfTerminalFailure(snapshot);
                if (snapshot.State == DownloadState.Complete || IsPieceComplete(snapshot.Bitfield, piece))
                {
                    var read = TryReadFile(destination[..readableLength], _position);
                    if (read > 0)
                    {
                        _position += read;
                        return read;
                    }

                    if (snapshot.State == DownloadState.Complete)
                    {
                        throw new IOException(
                            $"Downloaded media file '{_path}' is shorter than its declared length {_length}.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                PrioritizeFrom(piece, cancellationToken);
                cancellationToken.WaitHandle.WaitOne(PollInterval);
                cancellationToken.ThrowIfCancellationRequested();
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
            _position = absoluteOffset;
            if (_position < _length)
            {
                var globalOffset = checked(_torrentOffset + _position);
                PrioritizeFrom(
                    checked((int)(globalOffset / _pieceLength)),
                    cancellationToken,
                    force: true);
            }

            return _position;
        }
    }

    public void Dispose()
    {
        Action<Guid>? releasePriority;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle?.Dispose();
            _handle = null;
            releasePriority = Interlocked.Exchange(ref _releasePriority, null);
        }

        releasePriority?.Invoke(_taskId);
    }

    internal static bool IsPieceComplete(string bitfield, int piece)
    {
        if (piece < 0)
        {
            return false;
        }

        var nibbleIndex = piece / 4;
        if (nibbleIndex >= bitfield.Length)
        {
            return false;
        }

        var value = bitfield[nibbleIndex] switch
        {
            >= '0' and <= '9' => bitfield[nibbleIndex] - '0',
            >= 'a' and <= 'f' => bitfield[nibbleIndex] - 'a' + 10,
            >= 'A' and <= 'F' => bitfield[nibbleIndex] - 'A' + 10,
            _ => -1,
        };
        return value >= 0 && (value & (1 << (3 - piece % 4))) != 0;
    }

    private DownloadTaskSnapshot RequireSnapshot() =>
        _snapshotProvider(_taskId)
        ?? throw new IOException("The download task was removed while media was being read.");

    private void PrioritizeFrom(
        int firstPiece,
        CancellationToken cancellationToken,
        bool force = false)
    {
        var snapshot = RequireSnapshot();
        ThrowIfTerminalFailure(snapshot);
        if (snapshot.State == DownloadState.Complete)
        {
            return;
        }

        var pieceCount = snapshot.PieceCount;
        if (pieceCount <= 0 || firstPiece < 0 || firstPiece >= pieceCount)
        {
            return;
        }

        var readAheadPieces = Math.Max(1L, (DefaultReadAheadBytes + _pieceLength - 1) / _pieceLength);
        var lastPiece = checked((int)Math.Min(pieceCount - 1L, firstPiece + readAheadPieces - 1));
        if (!force && _priorityApplied &&
            firstPiece >= _lastPriorityFirst && firstPiece <= _lastPriorityLast)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_lastPriorityFirst == firstPiece && _lastPriorityLast == lastPiece && now < _nextPriorityAttemptTicks)
        {
            return;
        }

        _lastPriorityFirst = firstPiece;
        _lastPriorityLast = lastPiece;
        _nextPriorityAttemptTicks = now + (long)PriorityRetryInterval.TotalMilliseconds;
        _priorityApplied = _prioritizeRange(_taskId, firstPiece, lastPiece, cancellationToken)
            .AsTask()
            .WaitAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private int TryReadFile(Span<byte> destination, long fileOffset)
    {
        try
        {
            _handle ??= File.OpenHandle(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            return RandomAccess.Read(_handle, destination, fileOffset);
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private static void ThrowIfTerminalFailure(DownloadTaskSnapshot snapshot)
    {
        if (snapshot.State == DownloadState.Error)
        {
            throw new IOException(
                $"Download failed before the requested media range was available (aria2 error {snapshot.ErrorCode}).");
        }

        if (snapshot.State == DownloadState.Removed)
        {
            throw new IOException("The download was removed before the requested media range was available.");
        }
    }
}
