// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;

namespace CanX.App;

public enum PlaybackGestureKind
{
    None,
    Seek,
    Brightness,
    Volume,
}

public static class PlaybackGestureCalculator
{
    public const double ActivationDistance = 12;

    public static PlaybackGestureKind Detect(Point start, Point current, double surfaceWidth)
    {
        if (!double.IsFinite(surfaceWidth) || surfaceWidth <= 0)
        {
            return PlaybackGestureKind.None;
        }

        var horizontal = Math.Abs(current.X - start.X);
        var vertical = Math.Abs(current.Y - start.Y);
        if (Math.Max(horizontal, vertical) < ActivationDistance)
        {
            return PlaybackGestureKind.None;
        }

        if (horizontal >= vertical)
        {
            return PlaybackGestureKind.Seek;
        }

        return start.X < surfaceWidth / 2
            ? PlaybackGestureKind.Brightness
            : PlaybackGestureKind.Volume;
    }

    public static double CalculateSeekTarget(
        double startPosition,
        double duration,
        double horizontalDistance,
        double surfaceWidth)
    {
        if (!double.IsFinite(startPosition) || !double.IsFinite(duration) || duration <= 0 ||
            !double.IsFinite(horizontalDistance) || !double.IsFinite(surfaceWidth) || surfaceWidth <= 0)
        {
            return Math.Max(0, double.IsFinite(startPosition) ? startPosition : 0);
        }

        var fullWidthRange = Math.Min(duration, Math.Clamp(duration * 0.1, 30, 600));
        var offset = Math.Round(horizontalDistance / surfaceWidth * fullWidthRange);
        return Math.Clamp(startPosition + offset, 0, duration);
    }

    public static double CalculateVerticalValue(
        double startValue,
        double verticalDistance,
        double surfaceHeight,
        double minimum,
        double maximum)
    {
        if (!double.IsFinite(startValue) || !double.IsFinite(verticalDistance) ||
            !double.IsFinite(surfaceHeight) || surfaceHeight <= 0 ||
            !double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
        {
            return double.IsFinite(startValue) ? startValue : 0;
        }

        var value = startValue - verticalDistance / surfaceHeight * 100;
        return Math.Round(Math.Clamp(value, minimum, maximum));
    }
}
