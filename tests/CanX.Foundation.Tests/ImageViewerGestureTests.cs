// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using CanX.App.Controls;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class ImageViewerGestureTests
{
    [Theory]
    [InlineData(180, 150, 80, 150, 1)]
    [InlineData(80, 150, 180, 150, -1)]
    [InlineData(150, 200, 150, 100, 1)]
    [InlineData(150, 100, 150, 200, -1)]
    [InlineData(20, 150, 20, 150, -1)]
    [InlineData(280, 150, 280, 150, 1)]
    [InlineData(150, 150, 150, 150, 0)]
    public void MobileGesture_SwipesAndEdgeTapsNavigateInExpectedDirection(
        double startX, double startY, double endX, double endY, int expected)
    {
        Assert.Equal(expected, ImageViewerView.GestureDirection(
            new Point(startX, startY), new Point(endX, endY), width: 300));
    }

    [Theory]
    [InlineData(150, 150, 150, 150, true)]
    [InlineData(150, 150, 160, 155, true)]
    [InlineData(20, 150, 20, 150, false)]
    [InlineData(150, 150, 180, 150, false)]
    public void MobileGesture_OnlyCenterTapTogglesToolbar(
        double startX, double startY, double endX, double endY, bool expected)
    {
        Assert.Equal(expected, ImageViewerView.IsCenterTap(
            new Point(startX, startY), new Point(endX, endY), width: 300));
    }
}
