// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using CanX.App.ViewModels;

namespace CanX.App.Controls;

/// <summary>Paints chapter boundaries over a Slider without intercepting seeking input.</summary>
public sealed class PlaybackChapterMarkers : Control
{
    public static readonly StyledProperty<IReadOnlyList<PlaybackChapterItemViewModel>?> ChaptersProperty =
        AvaloniaProperty.Register<PlaybackChapterMarkers, IReadOnlyList<PlaybackChapterItemViewModel>?>(nameof(Chapters));

    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<PlaybackChapterMarkers, double>(nameof(DurationSeconds));

    private static readonly Pen OutlinePen = new(Brushes.Black, 4);
    private static readonly Pen MarkerPen = new(Brushes.White, 2);
    private Slider? _targetSlider;

    static PlaybackChapterMarkers()
    {
        AffectsRender<PlaybackChapterMarkers>(ChaptersProperty, DurationSecondsProperty);
    }

    public PlaybackChapterMarkers()
    {
        IsHitTestVisible = false;
    }

    public IReadOnlyList<PlaybackChapterItemViewModel>? Chapters
    {
        get => GetValue(ChaptersProperty);
        set => SetValue(ChaptersProperty, value);
    }

    public double DurationSeconds
    {
        get => GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    public Slider? TargetSlider
    {
        get => _targetSlider;
        set
        {
            if (ReferenceEquals(_targetSlider, value)) return;
            if (_targetSlider is not null) _targetSlider.LayoutUpdated -= OnSliderLayoutUpdated;
            _targetSlider = value;
            if (_targetSlider is not null) _targetSlider.LayoutUpdated += OnSliderLayoutUpdated;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_targetSlider is null || Chapters is null || DurationSeconds <= 0) return;

        if (!TryGetTrackMetrics(out var left, out var right, out var centerY)) return;
        foreach (var x in CalculatePositions(Chapters, DurationSeconds, left, right))
        {
            var top = new Point(x, centerY - 6);
            var bottom = new Point(x, centerY + 6);
            context.DrawLine(OutlinePen, top, bottom);
            context.DrawLine(MarkerPen, top, bottom);
        }
    }

    /// <summary>Returns the actual horizontal range traveled by the slider thumb.</summary>
    public bool TryGetSeekRange(out double left, out double right) =>
        TryGetTrackMetrics(out left, out right, out _);

    private bool TryGetTrackMetrics(out double left, out double right, out double centerY)
    {
        left = right = centerY = 0;
        if (_targetSlider is null) return false;

        // The thumb can only travel between these centers. Use the templated
        // Track so marks and hover previews follow the same seek coordinates.
        var track = _targetSlider.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (track?.TranslatePoint(new Point(0, 0), this) is not { } origin) return false;
        var halfThumb = (track.Thumb?.Bounds.Width ?? 0) / 2;
        left = origin.X + halfThumb;
        right = origin.X + track.Bounds.Width - halfThumb;
        centerY = origin.Y + track.Bounds.Height / 2;
        return right > left;
    }

    internal static IReadOnlyList<double> CalculatePositions(
        IReadOnlyList<PlaybackChapterItemViewModel> chapters,
        double durationSeconds,
        double left,
        double right)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            !double.IsFinite(left) || !double.IsFinite(right) || right <= left)
        {
            return [];
        }

        var positions = new List<double>();
        foreach (var chapter in chapters.OrderBy(static chapter => chapter.TimeSeconds))
        {
            if (!double.IsFinite(chapter.TimeSeconds) ||
                chapter.TimeSeconds <= 0 || chapter.TimeSeconds >= durationSeconds)
            {
                continue;
            }

            var x = left + (right - left) * chapter.TimeSeconds / durationSeconds;
            if (positions.Count == 0 || x - positions[^1] >= 2)
            {
                positions.Add(x);
            }
        }

        return positions;
    }

    internal static double SecondsFromPosition(double x, double durationSeconds, double left, double right)
    {
        if (!double.IsFinite(x) || !double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            !double.IsFinite(left) || !double.IsFinite(right) || right <= left)
        {
            return 0;
        }

        return Math.Clamp((x - left) / (right - left), 0, 1) * durationSeconds;
    }

    private void OnSliderLayoutUpdated(object? sender, EventArgs eventArgs) => InvalidateVisual();
}
