// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Playback.Engine;

public sealed record MpvStreamCallbackMetrics(
    long Opens,
    long Reads,
    long Seeks,
    long SizeQueries,
    long Cancellations,
    long Closes,
    long EndOfStreams);

internal sealed class MpvStreamCallbackCounters
{
    private long _opens;
    private long _reads;
    private long _seeks;
    private long _sizeQueries;
    private long _cancellations;
    private long _closes;
    private long _endOfStreams;

    public MpvStreamCallbackMetrics Snapshot => new(
        Interlocked.Read(ref _opens),
        Interlocked.Read(ref _reads),
        Interlocked.Read(ref _seeks),
        Interlocked.Read(ref _sizeQueries),
        Interlocked.Read(ref _cancellations),
        Interlocked.Read(ref _closes),
        Interlocked.Read(ref _endOfStreams));

    public void IncrementOpen() => Interlocked.Increment(ref _opens);

    public void IncrementRead() => Interlocked.Increment(ref _reads);

    public void IncrementSeek() => Interlocked.Increment(ref _seeks);

    public void IncrementSize() => Interlocked.Increment(ref _sizeQueries);

    public void IncrementCancel() => Interlocked.Increment(ref _cancellations);

    public void IncrementClose() => Interlocked.Increment(ref _closes);

    public void IncrementEndOfStream() => Interlocked.Increment(ref _endOfStreams);
}
