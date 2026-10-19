// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.ViewModels;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackValueAdjustmentTests
{
    [Fact]
    public async Task SlowWrites_ApplyDuringInteractionAndKeepOnlyTheLatestQueuedValue()
    {
        var source = 0d;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<double>();
        var adjustment = new PlaybackValueAdjustment(() => source, async (value, _) =>
        {
            writes.Add(value);
            if (value == 1) await releaseFirst.Task;
            source = value;
        }, () => { });

        var first = adjustment.SetAsync(1);
        Assert.Equal([1d], writes);
        var skipped = adjustment.SetAsync(2);
        var last = adjustment.SetAsync(3);
        adjustment.ObserveSourceValue();
        Assert.Equal(3, adjustment.Value);
        releaseFirst.SetResult();
        await Task.WhenAll(first, skipped, last);

        Assert.Equal([1d, 3d], writes);
        Assert.Equal(3, adjustment.Value);
        source = 4;
        adjustment.ObserveSourceValue();
        Assert.Equal(4, adjustment.Value);
    }

    [Fact]
    public async Task DelayedPlayerNotifications_DoNotOverwriteTheRequestedValue()
    {
        var source = 0d;
        var adjustment = new PlaybackValueAdjustment(() => source, (_, _) => Task.CompletedTask, () => { });
        await adjustment.SetAsync(2);
        await adjustment.SetAsync(5);

        foreach (var stale in new[] { 0d, 2d, 3d })
        {
            source = stale;
            adjustment.ObserveSourceValue();
            Assert.Equal(5, adjustment.Value);
        }
        source = 5;
        adjustment.ObserveSourceValue();
        source = 7;
        adjustment.ObserveSourceValue();
        Assert.Equal(7, adjustment.Value);
    }

    [Fact]
    public async Task FailedOrCancelledWrites_ReleaseThePendingValueAndAllowRetry()
    {
        var source = 10d;
        var adjustment = new PlaybackValueAdjustment(() => source, (value, token) =>
        {
            if (value == 20) throw new InvalidOperationException("Player rejected the setting.");
            source = value;
            return Task.CompletedTask;
        }, () => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => adjustment.SetAsync(20));
        Assert.Equal(10, adjustment.Value);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adjustment.SetAsync(30, new CancellationToken(true)));
        Assert.Equal(10, adjustment.Value);
        await adjustment.SetAsync(40);
        Assert.Equal(40, adjustment.Value);
    }
}
