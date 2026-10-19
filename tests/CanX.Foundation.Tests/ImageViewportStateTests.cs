// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using CanX.App.Controls;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class ImageViewportStateTests
{
    [Fact]
    public void OpeningAndChangingImageFitsEachImageToTheViewport()
    {
        var state = new ImageViewportState();
        state.SetViewport(new Size(1000, 600));
        state.SetImage(new Size(4000, 2000));

        Assert.Equal(0.25, state.Scale, 3);
        state.ZoomToOriginalSize();
        state.Rotate(1);
        state.SetImage(new Size(1000, 2000));

        Assert.Equal(0.3, state.Scale, 3);
        Assert.Equal(0, state.RotationDegrees);
        Assert.True(state.IsFitMode);
    }

    [Fact]
    public void RotatingAndResizingInFitModeKeepsTheWholeImageVisible()
    {
        var state = new ImageViewportState();
        state.SetViewport(new Size(1000, 600));
        state.SetImage(new Size(4000, 2000));
        state.Rotate(-1);

        Assert.Equal(270, state.RotationDegrees);
        Assert.Equal(0.15, state.Scale, 3);
        Assert.Equal(new Size(300, 600), state.DisplayedSize);

        state.SetViewport(new Size(800, 800));
        Assert.Equal(0.2, state.Scale, 3);
        Assert.Equal(new Size(400, 800), state.DisplayedSize);
    }

    [Fact]
    public void ZoomButtonsChangeTenPercentagePointsAndPercentButtonRestoresOriginalSize()
    {
        var state = new ImageViewportState();
        state.SetViewport(new Size(1000, 600));
        state.SetImage(new Size(4000, 2000));

        state.ZoomStep(1);
        Assert.Equal(0.35, state.Scale, 3);
        Assert.False(state.IsFitMode);
        state.ZoomStep(-1);
        Assert.Equal(0.25, state.Scale, 3);
        state.ZoomToOriginalSize();
        Assert.Equal(1, state.Scale, 3);

        state.SetViewport(new Size(600, 400));
        Assert.Equal(1, state.Scale, 3);
    }

    [Fact]
    public void WheelOrPinchZoomKeepsThePointUnderThePointerAndClampsPan()
    {
        var state = new ImageViewportState();
        state.SetViewport(new Size(1000, 600));
        state.SetImage(new Size(2000, 1000));

        state.ZoomBy(2, new Point(750, 300));
        Assert.Equal(1, state.Scale, 3);
        Assert.Equal(-250, state.Offset.X, 3);

        state.PanBy(new Vector(2000, 2000));
        Assert.Equal(500, state.Offset.X, 3);
        Assert.Equal(200, state.Offset.Y, 3);
    }
}
