// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using CanX.App;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackGestureCalculatorTests
{
    [Theory]
    [InlineData(100, 100, 108, 104, 1000, PlaybackGestureKind.None)]
    [InlineData(100, 100, 160, 115, 1000, PlaybackGestureKind.Seek)]
    [InlineData(100, 100, 108, 160, 1000, PlaybackGestureKind.Brightness)]
    [InlineData(900, 100, 908, 160, 1000, PlaybackGestureKind.Volume)]
    public void Detect_LocksDirectionAndUsesStartingHalf(
        double startX,
        double startY,
        double currentX,
        double currentY,
        double width,
        PlaybackGestureKind expected)
    {
        Assert.Equal(expected, PlaybackGestureCalculator.Detect(
            new Point(startX, startY),
            new Point(currentX, currentY),
            width));
    }

    [Theory]
    [InlineData(300, 3600, 500, 1000, 480)]
    [InlineData(300, 3600, -500, 1000, 120)]
    [InlineData(10, 100, -1000, 1000, 0)]
    [InlineData(90, 100, 1000, 1000, 100)]
    public void CalculateSeekTarget_ScalesAndClamps(
        double start,
        double duration,
        double distance,
        double width,
        double expected)
    {
        Assert.Equal(expected, PlaybackGestureCalculator.CalculateSeekTarget(start, duration, distance, width));
    }

    [Theory]
    [InlineData(50, -250, 500, 0, 130, 100)]
    [InlineData(50, 250, 500, 0, 130, 0)]
    [InlineData(90, -500, 500, 0, 130, 130)]
    [InlineData(0, -250, 500, -100, 100, 50)]
    public void CalculateVerticalValue_UsesUpwardMotionAsIncreaseAndClamps(
        double start,
        double distance,
        double height,
        double minimum,
        double maximum,
        double expected)
    {
        Assert.Equal(expected, PlaybackGestureCalculator.CalculateVerticalValue(
            start,
            distance,
            height,
            minimum,
            maximum));
    }
}
