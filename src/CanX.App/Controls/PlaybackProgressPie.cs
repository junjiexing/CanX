// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CanX.App.Controls;

public sealed class PlaybackProgressPie : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<PlaybackProgressPie, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<PlaybackProgressPie, IBrush?>(nameof(PlayedBrush));

    public static readonly StyledProperty<IBrush?> RemainingBrushProperty =
        AvaloniaProperty.Register<PlaybackProgressPie, IBrush?>(nameof(RemainingBrush));

    static PlaybackProgressPie()
    {
        AffectsRender<PlaybackProgressPie>(ProgressProperty, PlayedBrushProperty, RemainingBrushProperty);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush? RemainingBrush
    {
        get => GetValue(RemainingBrushProperty);
        set => SetValue(RemainingBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (RemainingBrush is not null)
        {
            context.DrawEllipse(RemainingBrush, null, center, radius, radius);
        }

        var percent = double.IsFinite(Progress) ? Math.Clamp(Progress, 0, 100) : 0;
        if (percent <= 0 || PlayedBrush is null)
        {
            return;
        }

        if (percent >= 100)
        {
            context.DrawEllipse(PlayedBrush, null, center, radius, radius);
            return;
        }

        var angle = percent / 100 * 2 * Math.PI - Math.PI / 2;
        var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(center, isFilled: true);
            path.LineTo(new Point(center.X, center.Y - radius));
            path.ArcTo(end, new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise);
            path.EndFigure(isClosed: true);
        }

        context.DrawGeometry(PlayedBrush, null, geometry);
    }
}
