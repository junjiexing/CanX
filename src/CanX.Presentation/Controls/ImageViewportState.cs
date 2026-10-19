// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;

namespace CanX.App.Controls;

// The percentage is relative to the decoded image's pixels. Fit is a separate
// mode so resizing the viewer can keep an untouched image inside the viewport.
internal sealed class ImageViewportState
{
    private const double MinimumScale = 0.01;
    private const double MaximumScale = 5;
    private Size _imageSize;
    private Size _viewportSize;

    public int RotationDegrees { get; private set; }

    public double Scale { get; private set; } = 1;

    public Vector Offset { get; private set; }

    public bool IsFitMode { get; private set; } = true;

    public Size ImageSize => _imageSize;

    public bool HasImage => _imageSize.Width > 0 && _imageSize.Height > 0;

    public bool CanZoomOut => HasImage && Scale > MinimumScale + 0.0001;

    public bool CanZoomIn => HasImage && Scale < MaximumScale - 0.0001;

    public Size DisplayedSize => new(
        (RotationDegrees % 180 == 0 ? _imageSize.Width : _imageSize.Height) * Scale,
        (RotationDegrees % 180 == 0 ? _imageSize.Height : _imageSize.Width) * Scale);

    public bool CanPan => HasImage &&
        (DisplayedSize.Width > _viewportSize.Width + 1 || DisplayedSize.Height > _viewportSize.Height + 1);

    public void SetImage(Size imageSize)
    {
        _imageSize = imageSize;
        RotationDegrees = 0;
        IsFitMode = true;
        Offset = default;
        Fit();
    }

    public void SetViewport(Size viewportSize)
    {
        if (_viewportSize == viewportSize) return;
        _viewportSize = viewportSize;
        if (IsFitMode) Fit();
        else ClampOffset();
    }

    public void Rotate(int quarterTurns)
    {
        if (!HasImage || quarterTurns == 0) return;
        RotationDegrees = ((RotationDegrees + quarterTurns * 90) % 360 + 360) % 360;
        Offset = default;
        if (IsFitMode) Fit();
        else ClampOffset();
    }

    public void ZoomStep(int direction) => ZoomTo(Scale + Math.Sign(direction) * 0.1,
        new Point(_viewportSize.Width / 2, _viewportSize.Height / 2));

    public void ZoomToOriginalSize() => ZoomTo(1,
        new Point(_viewportSize.Width / 2, _viewportSize.Height / 2));

    public void ZoomBy(double factor, Point focus)
    {
        if (double.IsFinite(factor) && factor > 0) ZoomTo(Scale * factor, focus);
    }

    public void PanBy(Vector delta)
    {
        if (!HasImage) return;
        Offset += delta;
        ClampOffset();
    }

    private void ZoomTo(double scale, Point focus)
    {
        if (!HasImage || !double.IsFinite(scale)) return;
        var next = Math.Clamp(scale, MinimumScale, MaximumScale);
        var ratio = next / Scale;
        var center = new Point(_viewportSize.Width / 2, _viewportSize.Height / 2);
        Offset = new Vector(
            focus.X - center.X - ratio * (focus.X - center.X - Offset.X),
            focus.Y - center.Y - ratio * (focus.Y - center.Y - Offset.Y));
        Scale = next;
        IsFitMode = false;
        ClampOffset();
    }

    private void Fit()
    {
        Offset = default;
        if (!HasImage || _viewportSize.Width <= 0 || _viewportSize.Height <= 0)
        {
            Scale = 1;
            return;
        }

        var width = RotationDegrees % 180 == 0 ? _imageSize.Width : _imageSize.Height;
        var height = RotationDegrees % 180 == 0 ? _imageSize.Height : _imageSize.Width;
        Scale = Math.Min(1, Math.Min(_viewportSize.Width / width, _viewportSize.Height / height));
    }

    private void ClampOffset()
    {
        var maximumX = Math.Max(0, (DisplayedSize.Width - _viewportSize.Width) / 2);
        var maximumY = Math.Max(0, (DisplayedSize.Height - _viewportSize.Height) / 2);
        Offset = new Vector(
            Math.Clamp(Offset.X, -maximumX, maximumX),
            Math.Clamp(Offset.Y, -maximumY, maximumY));
    }
}
