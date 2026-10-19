// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.Controls;
using CanX.App.ViewModels;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackChapterMarkersTests
{
    [Fact]
    public void PositionsFollowThumbTravelAndSkipStartEndAndOverlappingMarks()
    {
        PlaybackChapterItemViewModel Chapter(double seconds) => new(0, string.Empty, seconds, false);
        var chapters = new[]
        {
            Chapter(90), Chapter(0), Chapter(30), Chapter(60), Chapter(60.2),
            Chapter(120), Chapter(-1), Chapter(double.NaN),
        };

        var positions = PlaybackChapterMarkers.CalculatePositions(chapters, 120, 10, 110);

        Assert.Equal([35d, 60d, 85d], positions);
        Assert.Empty(PlaybackChapterMarkers.CalculatePositions(chapters, 0, 10, 110));
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(35, 30)]
    [InlineData(60, 60)]
    [InlineData(110, 120)]
    [InlineData(120, 120)]
    public void HoverPositionUsesTheSameThumbTravelAsChapterMarks(double pointerX, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, PlaybackChapterMarkers.SecondsFromPosition(pointerX, 120, 10, 110));
    }
}
