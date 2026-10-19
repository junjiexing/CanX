// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.MediaAccess;
using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class RetryingMediaByteStreamTests
{
    [Fact]
    public void TransientClassification_ExcludesCancellationAndPermanentFileFailures()
    {
        Assert.True(NetworkMediaRetry.IsTransient(new IOException("connection reset")));
        Assert.True(NetworkMediaRetry.IsTransient(
            new InvalidOperationException("wrapped", new TimeoutException())));
        Assert.False(NetworkMediaRetry.IsTransient(new FileNotFoundException()));
        Assert.False(NetworkMediaRetry.IsTransient(new UnauthorizedAccessException()));
        Assert.False(NetworkMediaRetry.IsTransient(new OperationCanceledException()));
    }

    [Fact]
    public void Read_ReconnectsSeekableStreamAtLastDeliveredOffset()
    {
        byte[] payload = [0, 1, 2, 3, 4, 5];
        var streams = new List<ScriptedStream>();
        using var stream = new RetryingMediaByteStream(
            () =>
            {
                var created = new ScriptedStream(
                    payload,
                    canSeek: true,
                    failOnRead: streams.Count == 0 ? 2 : null,
                    failure: new IOException("connection reset"));
                streams.Add(created);
                return created;
            },
            payload.Length,
            canSeek: true,
            retryDelay: TimeSpan.Zero);

        var first = new byte[2];
        Assert.Equal(2, stream.Read(first, CancellationToken.None));
        Assert.Equal([0, 1], first);
        var second = new byte[2];
        Assert.Equal(2, stream.Read(second, CancellationToken.None));

        Assert.Equal([2, 3], second);
        Assert.Equal(2, streams.Count);
        Assert.True(streams[0].IsDisposed);
        Assert.Equal([2], streams[1].SeekOffsets);
    }

    [Fact]
    public void Read_RetriesNonSeekableStreamOnlyBeforeDeliveringData()
    {
        byte[] payload = [1, 2, 3, 4];
        var initialAttempts = 0;
        using var initialFailure = new RetryingMediaByteStream(
            () => new ScriptedStream(
                payload,
                canSeek: false,
                failOnRead: ++initialAttempts == 1 ? 1 : null,
                failure: new IOException("connection reset")),
            payload.Length,
            canSeek: false,
            retryDelay: TimeSpan.Zero);

        var bytes = new byte[2];
        Assert.Equal(2, initialFailure.Read(bytes, CancellationToken.None));
        Assert.Equal(2, initialAttempts);

        var resumedAttempts = 0;
        using var failureAfterData = new RetryingMediaByteStream(
            () => new ScriptedStream(
                payload,
                canSeek: false,
                failOnRead: 2,
                failure: new IOException("connection reset"),
                onCreated: () => resumedAttempts++),
            payload.Length,
            canSeek: false,
            retryDelay: TimeSpan.Zero);

        Assert.Equal(2, failureAfterData.Read(bytes, CancellationToken.None));
        Assert.Throws<IOException>(() => failureAfterData.Read(bytes, CancellationToken.None));
        Assert.Equal(1, resumedAttempts);
    }

    [Fact]
    public void Read_DoesNotRetryPermanentFailure()
    {
        var attempts = 0;
        using var stream = new RetryingMediaByteStream(
            () => new ScriptedStream(
                [1],
                canSeek: true,
                failOnRead: 1,
                failure: new FileNotFoundException("missing resource"),
                onCreated: () => attempts++),
            length: 1,
            canSeek: true,
            retryDelay: TimeSpan.Zero);

        Assert.Throws<FileNotFoundException>(() => stream.Read(new byte[1], CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Read_RetriesPrematureEndOfSeekableStreamAndStopsAtLimit()
    {
        var attempts = 0;
        using var stream = new RetryingMediaByteStream(
            () => new ScriptedStream(
                [],
                canSeek: true,
                failOnRead: null,
                failure: new IOException(),
                onCreated: () => attempts++),
            length: 4,
            canSeek: true,
            retryDelay: TimeSpan.Zero);

        Assert.Throws<IOException>(() => stream.Read(new byte[4], CancellationToken.None));
        Assert.Equal(NetworkMediaRetry.MaximumAttempts, attempts);
    }

    private sealed class ScriptedStream : IMediaByteStream
    {
        private readonly byte[] _payload;
        private readonly int? _failOnRead;
        private readonly Exception _failure;
        private int _readCount;
        private long _position;

        public ScriptedStream(
            byte[] payload,
            bool canSeek,
            int? failOnRead,
            Exception failure,
            Action? onCreated = null)
        {
            _payload = payload;
            CanSeek = canSeek;
            _failOnRead = failOnRead;
            _failure = failure;
            onCreated?.Invoke();
        }

        public bool CanSeek { get; }

        public long? Length => _payload.Length;

        public bool IsDisposed { get; private set; }

        public List<long> SeekOffsets { get; } = [];

        public int Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _readCount++;
            if (_readCount == _failOnRead)
            {
                throw _failure;
            }

            var count = Math.Min(destination.Length, _payload.Length - checked((int)_position));
            _payload.AsSpan(checked((int)_position), count).CopyTo(destination);
            _position += count;
            return count;
        }

        public long Seek(long absoluteOffset, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanSeek)
            {
                throw new NotSupportedException();
            }

            SeekOffsets.Add(absoluteOffset);
            _position = absoluteOffset;
            return _position;
        }

        public void Dispose() => IsDisposed = true;
    }
}
