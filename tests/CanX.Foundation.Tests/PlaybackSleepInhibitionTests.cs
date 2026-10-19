// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Application.Playback;
using CanX.Core.Playback;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackSleepInhibitionTests
{
    private static PlaybackSnapshot Video(PlaybackState state = PlaybackState.Playing) => new()
    {
        State = state,
        Source = "video.mkv",
        Tracks = [new PlaybackTrack { Type = PlaybackTrackType.Video, IsSelected = true }],
    };

    [Theory]
    [InlineData(PlaybackState.Idle)]
    [InlineData(PlaybackState.Loading)]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Ended)]
    [InlineData(PlaybackState.Error)]
    public async Task OnlyActivePlaybackHoldsAnInhibitor(PlaybackState state)
    {
        var backend = new RecordingBackend();
        using var inhibition = new PlaybackSleepInhibition(backend.AcquireAsync);
        await inhibition.UpdateAsync(Video());
        Assert.Equal(1, backend.Active);
        await inhibition.UpdateAsync(Video(state));
        Assert.Equal(0, backend.Active);
        await inhibition.UpdateAsync(Video());
        Assert.Equal(1, backend.Active);
    }

    [Fact]
    public async Task PositionUpdatesAndBackgroundVideoDoNotReleaseOrDuplicateTheRequest()
    {
        var backend = new RecordingBackend();
        using var inhibition = new PlaybackSleepInhibition(backend.AcquireAsync);
        await inhibition.UpdateAsync(Video());
        for (var position = 1; position <= 20; position++)
            await inhibition.UpdateAsync(Video() with { PositionSeconds = position });
        // Losing the native surface in the background can deselect the video track.
        await inhibition.UpdateAsync(Video() with
        {
            Tracks = [new PlaybackTrack { Type = PlaybackTrackType.Video, IsSelected = false }],
        });
        Assert.Equal(1, backend.Acquisitions);
        Assert.Equal(1, backend.Active);
    }

    [Fact]
    public async Task AudioAndAlbumArtDoNotKeepTheDisplayAwake()
    {
        var backend = new RecordingBackend();
        using var inhibition = new PlaybackSleepInhibition(backend.AcquireAsync);
        await inhibition.UpdateAsync(Video());
        await inhibition.UpdateAsync(Video() with
        {
            Tracks = [new PlaybackTrack { Type = PlaybackTrackType.Audio, IsSelected = true }],
        });
        Assert.Equal(0, backend.Active);
        await inhibition.UpdateAsync(Video() with
        {
            Tracks = [new PlaybackTrack { Type = PlaybackTrackType.Video, IsSelected = true, IsAlbumArt = true }],
        });
        Assert.Equal(0, backend.Active);
        Assert.Equal(1, backend.Acquisitions);
    }

    [Fact]
    public async Task ClosingOneSessionDoesNotReleaseAnotherSession()
    {
        var backend = new RecordingBackend();
        using var first = new PlaybackSleepInhibition(backend.AcquireAsync);
        using var second = new PlaybackSleepInhibition(backend.AcquireAsync);
        await first.UpdateAsync(Video());
        await second.UpdateAsync(Video());
        Assert.Equal(2, backend.Active);
        first.Dispose();
        first.Dispose();
        await first.UpdateAsync(Video());
        Assert.Equal(1, backend.Active);
        await second.UpdateAsync(Video(PlaybackState.Paused));
        Assert.Equal(0, backend.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestCompletingAfterPauseOrDisposeIsImmediatelyReleased(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new RecordingBackend();
        using var inhibition = new PlaybackSleepInhibition(async _ =>
        {
            entered.SetResult();
            await complete.Task;
            return await backend.AcquireAsync(default);
        });
        var pending = inhibition.UpdateAsync(Video());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) inhibition.Dispose();
        else _ = inhibition.UpdateAsync(Video(PlaybackState.Paused));
        complete.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, backend.Active);
        Assert.Equal(1, backend.Acquisitions);
    }

    [Fact]
    public async Task ResumeDuringCancelledAcquisitionStartsANewRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new RecordingBackend();
        var attempt = 0;
        using var inhibition = new PlaybackSleepInhibition(async token =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                entered.SetResult();
                await finishCancellation.Task;
                token.ThrowIfCancellationRequested();
            }
            return await backend.AcquireAsync(token);
        });
        var pending = inhibition.UpdateAsync(Video());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = inhibition.UpdateAsync(Video(PlaybackState.Paused));
        _ = inhibition.UpdateAsync(Video());
        finishCancellation.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await inhibition.UpdateAsync(Video()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempt);
        Assert.Equal(1, backend.Active);
    }

    [Fact]
    public async Task BackendFailureDoesNotFailPlaybackOrRetryOnEveryProgressUpdate()
    {
        var attempts = 0;
        using var inhibition = new PlaybackSleepInhibition(_ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("Power service unavailable.");
        });
        await inhibition.UpdateAsync(Video());
        for (var i = 0; i < 20; i++) await inhibition.UpdateAsync(Video());
        Assert.Equal(1, attempts);
        await inhibition.UpdateAsync(Video(PlaybackState.Paused));
        await inhibition.UpdateAsync(Video());
        Assert.Equal(2, attempts);
    }

    private sealed class RecordingBackend
    {
        public int Active;
        public int Acquisitions;

        public Task<IDisposable> AcquireAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Active);
            Interlocked.Increment(ref Acquisitions);
            return Task.FromResult<IDisposable>(new Lease(this));
        }

        private sealed class Lease(RecordingBackend backend) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                Assert.Equal(0, Interlocked.Exchange(ref _disposed, 1));
                Interlocked.Decrement(ref backend.Active);
            }
        }
    }
}
