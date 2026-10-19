// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.Controls;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class MpvPresentationMetricsTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public void CompositionBufferCount_AvoidsStackingThreeAndroidCompositorUpdates(
        bool isAndroid,
        int expected)
    {
        Assert.Equal(expected, MpvCompositionRenderWorker.SelectBufferCount(isAndroid));
    }

    [Fact]
    public void Snapshot_SeparatesRenderSubmitCopyAndBlockingSynchronization()
    {
        var metrics = new MpvPresentationMetrics();
        metrics.RecordRenderMilliseconds(2, usedBlockingFinish: true);
        metrics.RecordRenderMilliseconds(8, usedBlockingFinish: true);
        metrics.RecordSubmitMilliseconds(1, performedGpuCopy: true, usedBlockingFinish: true);
        metrics.RecordPresentedFrame();

        var snapshot = metrics.Snapshot(
            "shared-worker-finish",
            "gl-finish",
            usesCpuReadback: false,
            gpuCopiesPerFrame: 1,
            compositorGpuCopiesPerFrame: 1);

        Assert.Equal("shared-worker-finish", snapshot.Backend);
        Assert.Equal("gl-finish", snapshot.SynchronizationMode);
        Assert.False(snapshot.UsesCpuReadback);
        Assert.Equal(1, snapshot.CanXGpuCopiesPerFrame);
        Assert.Equal(1, snapshot.CompositorGpuCopiesPerFrame);
        Assert.Equal(2, snapshot.RenderOperations);
        Assert.Equal(1, snapshot.SubmitOperations);
        Assert.Equal(1, snapshot.PresentedFrames);
        Assert.Equal(1, snapshot.CanXGpuCopies);
        Assert.Equal(3, snapshot.BlockingFinishCalls);
        Assert.Equal(5, snapshot.AverageRenderMilliseconds);
        Assert.Equal(8, snapshot.P95RenderMilliseconds);
        Assert.Equal(1, snapshot.AverageSubmitMilliseconds);
        Assert.Equal(1, snapshot.P95SubmitMilliseconds);
    }

    [Fact]
    public void Reset_ClearsAllCountersAndTimings()
    {
        var metrics = new MpvPresentationMetrics();
        metrics.RecordRenderMilliseconds(4, usedBlockingFinish: true);
        metrics.RecordSubmitMilliseconds(2, performedGpuCopy: true, usedBlockingFinish: true);
        metrics.RecordPresentedFrame();

        metrics.Reset();
        var snapshot = metrics.Snapshot("inline", "gl-finish", false, 0, 1);

        Assert.Equal(0, snapshot.RenderOperations);
        Assert.Equal(0, snapshot.SubmitOperations);
        Assert.Equal(0, snapshot.PresentedFrames);
        Assert.Equal(0, snapshot.CanXGpuCopies);
        Assert.Equal(0, snapshot.BlockingFinishCalls);
        Assert.Equal(0, snapshot.AverageRenderMilliseconds);
        Assert.Equal(0, snapshot.P95RenderMilliseconds);
    }
}
