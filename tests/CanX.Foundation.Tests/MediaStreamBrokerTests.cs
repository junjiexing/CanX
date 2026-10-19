// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class MediaStreamBrokerTests
{
    [Fact]
    public void Register_ProducesOpaqueSourceWithoutPathOrCredentials()
    {
        using var broker = new MediaStreamBroker();
        using var registration = broker.Register(
            "https://alice:secret@example.test/private/movie.mkv?token=credential",
            CreateEmptyStream);

        Assert.StartsWith("canx://stream/", registration.Source, StringComparison.Ordinal);
        Assert.EndsWith("/movie.mkv", registration.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", registration.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", registration.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("private", registration.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", registration.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_CreatesIndependentStreamsWithReadSeekAndSize()
    {
        using var broker = new MediaStreamBroker();
        var created = 0;
        using var registration = broker.Register(
            "fixture.bin",
            () =>
            {
                Interlocked.Increment(ref created);
                return new DotNetMediaByteStream(new MemoryStream([1, 2, 3, 4]));
            });

        Assert.True(broker.TryOpen(registration.Source, out var first));
        Assert.True(broker.TryOpen(registration.Source, out var second));
        using (first)
        using (second)
        {
            Assert.NotSame(first, second);
            Assert.Equal(2, created);
            Assert.True(first.CanSeek);
            Assert.Equal(4, first.Length);
            Span<byte> buffer = stackalloc byte[2];
            Assert.Equal(2, first.Read(buffer));
            Assert.Equal([1, 2], buffer.ToArray());
            Assert.Equal(3, first.Seek(3));
            Assert.Equal(1, first.Read(buffer));
            Assert.Equal(4, buffer[0]);

            Assert.Equal(2, second.Read(buffer));
            Assert.Equal([1, 2], buffer.ToArray());
        }
    }

    [Fact]
    public void Register_WithDisabledCache_ProducesAnOpenableCacheHint()
    {
        using var broker = new MediaStreamBroker();
        using var registration = broker.Register(
            "downloading.mkv",
            CreateEmptyStream,
            MediaStreamCachePolicy.Disabled);

        Assert.EndsWith("/downloading.mkv?cache=no", registration.Source, StringComparison.Ordinal);
        Assert.True(broker.TryOpen(registration.Source, out var lease));
        lease.Dispose();
    }

    [Fact]
    public void RegisterPair_OpensIdxAndSubThroughTheSameOpaqueSource()
    {
        using var broker = new MediaStreamBroker();
        using var registration = broker.RegisterPair(
            "Movie.idx",
            () => new DotNetMediaByteStream(new MemoryStream([1])),
            "Movie.sub",
            () => new DotNetMediaByteStream(new MemoryStream([2])));

        Assert.True(broker.TryOpen(registration.Source, out var index));
        Assert.True(broker.TryOpen(registration.Source.Replace("Movie.idx", "Movie.sub", StringComparison.Ordinal), out var data));
        Assert.False(broker.TryOpen(registration.Source.Replace("Movie.idx", "Other.sub", StringComparison.Ordinal), out _));
        using (index)
        using (data)
        {
            Span<byte> buffer = stackalloc byte[1];
            Assert.Equal(1, index.Read(buffer));
            Assert.Equal(1, buffer[0]);
            Assert.Equal(1, data.Read(buffer));
            Assert.Equal(2, buffer[0]);
        }
    }

    [Fact]
    public void RegisterPair_PreservesDistinctExtensionsForLongNames()
    {
        using var broker = new MediaStreamBroker();
        var stem = new string('A', 140);
        using var registration = broker.RegisterPair(
            $"{stem}.idx", CreateEmptyStream, $"{stem}.sub", CreateEmptyStream);

        Assert.EndsWith(".idx", registration.Source, StringComparison.Ordinal);
        Assert.True(broker.TryOpen(registration.Source.Replace(".idx", ".sub", StringComparison.Ordinal), out var companion));
        companion.Dispose();
    }

    [Fact]
    public void RegistrationDispose_RejectsNewOpenAndCancelsActiveLease()
    {
        using var broker = new MediaStreamBroker();
        var stream = new BlockingMediaByteStream();
        var registration = broker.Register("waiting.mkv", () => stream);
        Assert.True(broker.TryOpen(registration.Source, out var lease));
        using (lease)
        {
            var read = Task.Run(() => lease.Read(new byte[1]));
            Assert.True(stream.ReadStarted.Wait(TimeSpan.FromSeconds(2)));

            registration.Dispose();

            Assert.False(broker.TryOpen(registration.Source, out _));
            Assert.ThrowsAny<OperationCanceledException>(() => read.GetAwaiter().GetResult());
        }
    }

    [Fact]
    public void BrokerDispose_RejectsUnknownSourcesAndCancelsEveryActiveLease()
    {
        var broker = new MediaStreamBroker();
        var firstStream = new BlockingMediaByteStream();
        var secondStream = new BlockingMediaByteStream();
        using var firstRegistration = broker.Register("first", () => firstStream);
        using var secondRegistration = broker.Register("second", () => secondStream);
        Assert.True(broker.TryOpen(firstRegistration.Source, out var first));
        Assert.True(broker.TryOpen(secondRegistration.Source, out var second));
        using (first)
        using (second)
        {
            var firstRead = Task.Run(() => first.Read(new byte[1]));
            var secondRead = Task.Run(() => second.Read(new byte[1]));
            Assert.True(firstStream.ReadStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(secondStream.ReadStarted.Wait(TimeSpan.FromSeconds(2)));

            broker.Dispose();

            Assert.False(broker.TryOpen(firstRegistration.Source, out _));
            Assert.False(broker.TryOpen("canx://stream/not-a-token/media", out _));
            Assert.ThrowsAny<OperationCanceledException>(() => firstRead.GetAwaiter().GetResult());
            Assert.ThrowsAny<OperationCanceledException>(() => secondRead.GetAwaiter().GetResult());
            Assert.Throws<ObjectDisposedException>(() => broker.Register("late", CreateEmptyStream));
        }
    }

    [Fact]
    public void LeaseDispose_DisposesUnderlyingStreamOnlyOnce()
    {
        using var broker = new MediaStreamBroker();
        var stream = new CountingMediaByteStream();
        using var registration = broker.Register("counting", () => stream);
        Assert.True(broker.TryOpen(registration.Source, out var lease));

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://stream/0123456789abcdef/media")]
    [InlineData("canx://other/0123456789abcdef/media")]
    [InlineData("canx://stream/0123456789abcdef/media")]
    [InlineData("canx://stream/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/media?x=1")]
    public void Open_RejectsMalformedOrUnknownSource(string source)
    {
        using var broker = new MediaStreamBroker();
        Assert.False(broker.TryOpen(source, out _));
    }

    private static IMediaByteStream CreateEmptyStream() =>
        new DotNetMediaByteStream(new MemoryStream());

    private sealed class BlockingMediaByteStream : IMediaByteStream
    {
        public ManualResetEventSlim ReadStarted { get; } = new();

        public bool CanSeek => true;

        public long? Length => null;

        public int Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            ReadStarted.Set();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        public long Seek(long absoluteOffset, CancellationToken cancellationToken) => absoluteOffset;

        public void Dispose()
        {
        }
    }

    private sealed class CountingMediaByteStream : IMediaByteStream
    {
        public int DisposeCount { get; private set; }

        public bool CanSeek => true;

        public long? Length => 0;

        public int Read(Span<byte> destination, CancellationToken cancellationToken) => 0;

        public long Seek(long absoluteOffset, CancellationToken cancellationToken) => absoluteOffset;

        public void Dispose() => DisposeCount++;
    }
}
