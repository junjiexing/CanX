// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;
using CanX.Downloads;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class Aria2MediaByteStreamTests
{
    [Fact]
    public async Task Read_BlocksUntilRequestedPieceIsComplete()
    {
        using var fixture = new MediaFixture([0, 1, 2, 3, 4, 5, 6, 7]);
        var priorityRequested = new TaskCompletionSource<(int First, int Last)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = CreateSnapshot(fixture.Path, bitfield: "00");
        using var stream = new Aria2MediaByteStream(
            snapshot.Id,
            fileIndex: 1,
            fixture.Path,
            length: 8,
            torrentOffset: 0,
            pieceLength: 4,
            _ => Volatile.Read(ref snapshot),
            (_, first, last, _) =>
            {
                priorityRequested.TrySetResult((first, last));
                return ValueTask.FromResult(true);
            });

        Assert.Equal(4, stream.Seek(4, CancellationToken.None));
        var buffer = new byte[4];
        var readTask = Task.Run(() => stream.Read(buffer, CancellationToken.None));
        Assert.Equal((1, 1), await priorityRequested.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        await Task.Delay(100);
        Assert.False(readTask.IsCompleted);

        Volatile.Write(ref snapshot, snapshot with { Bitfield = "40" });
        Assert.Equal(4, await readTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, buffer);
    }

    [Fact]
    public void Read_MapsFileOffsetsToTorrentPiecesAndStopsAtPieceBoundary()
    {
        using var fixture = new MediaFixture([10, 11, 12, 13, 14]);
        var snapshot = CreateSnapshot(fixture.Path, bitfield: "c0") with
        {
            TotalLength = 8,
            Files =
            [
                new DownloadFileSnapshot
                {
                    Index = 1,
                    Path = "preceding.bin",
                    Length = 3,
                    CompletedLength = 3,
                    IsSelected = true,
                },
                new DownloadFileSnapshot
                {
                    Index = 2,
                    Path = fixture.Path,
                    Length = 5,
                    CompletedLength = 5,
                    IsSelected = true,
                },
            ],
        };
        using var stream = new Aria2MediaByteStream(
            snapshot.Id,
            fileIndex: 2,
            fixture.Path,
            length: 5,
            torrentOffset: 3,
            pieceLength: 4,
            _ => snapshot,
            (_, _, _, _) => ValueTask.FromResult(true));

        var buffer = new byte[5];
        Assert.Equal(1, stream.Read(buffer, CancellationToken.None));
        Assert.Equal(4, stream.Read(buffer.AsSpan(1), CancellationToken.None));
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14 }, buffer);
        Assert.Equal(0, stream.Read(buffer, CancellationToken.None));
    }

    [Fact]
    public async Task Read_MissingPieceHonorsCancellationWithoutReturningFalseEof()
    {
        using var fixture = new MediaFixture([0, 1, 2, 3]);
        var snapshot = CreateSnapshot(fixture.Path, bitfield: "00");
        using var stream = new Aria2MediaByteStream(
            snapshot.Id,
            fileIndex: 1,
            fixture.Path,
            length: 4,
            torrentOffset: 0,
            pieceLength: 4,
            _ => snapshot,
            (_, _, _, _) => ValueTask.FromResult(false));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Run(() => stream.Read(new byte[4], cancellation.Token)));
    }

    [Fact]
    public void Read_ReusesAcceptedReadAheadRangeUntilSeekMovesIt()
    {
        using var fixture = new MediaFixture(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
        var snapshot = CreateSnapshot(fixture.Path, bitfield: "ff") with
        {
            TotalLength = 32,
            PieceLength = 4,
            PieceCount = 8,
            Files =
            [
                new DownloadFileSnapshot
                {
                    Index = 1,
                    Path = fixture.Path,
                    Length = 32,
                    CompletedLength = 32,
                    IsSelected = true,
                },
            ],
        };
        var requests = new List<(int First, int Last)>();
        using var stream = new Aria2MediaByteStream(
            snapshot.Id,
            fileIndex: 1,
            fixture.Path,
            length: 32,
            torrentOffset: 0,
            pieceLength: 4,
            _ => snapshot,
            (_, first, last, _) =>
            {
                requests.Add((first, last));
                return ValueTask.FromResult(true);
            });

        var buffer = new byte[8];
        Assert.Equal(4, stream.Read(buffer, CancellationToken.None));
        Assert.Equal(4, stream.Read(buffer.AsSpan(4), CancellationToken.None));
        Assert.Equal([(0, 7)], requests);

        Assert.Equal(16, stream.Seek(16, CancellationToken.None));
        Assert.Equal(4, stream.Read(buffer, CancellationToken.None));
        Assert.Equal([(0, 7), (4, 7)], requests);
    }

    private static DownloadTaskSnapshot CreateSnapshot(string path, string bitfield) => new()
    {
        Id = Guid.NewGuid(),
        EngineGid = "0000000000000001",
        Kind = DownloadSourceKind.Uri,
        Source = "https://example.test/video.bin",
        DestinationDirectory = Path.GetDirectoryName(path)!,
        DisplayName = Path.GetFileName(path),
        State = DownloadState.Active,
        EngineStatus = 0,
        TotalLength = 8,
        PieceLength = 4,
        PieceCount = 2,
        Bitfield = bitfield,
        Files =
        [
            new DownloadFileSnapshot
            {
                Index = 1,
                Path = path,
                Length = 8,
                IsSelected = true,
            },
        ],
    };

    private sealed class MediaFixture : IDisposable
    {
        private readonly string _directory;

        public MediaFixture(byte[] contents)
        {
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CanX.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "video.bin");
            File.WriteAllBytes(Path, contents);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
