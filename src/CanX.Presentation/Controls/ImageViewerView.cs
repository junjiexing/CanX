// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CanX.App.ViewModels;

namespace CanX.App.Controls;

public sealed class ImageViewerView : UserControl, IDisposable
{
    private readonly ImageViewerViewModel _viewModel;
    private readonly bool _mobile;
    private readonly Border _imageArea;
    private readonly Border _topBar;
    private readonly Border _bottomBar;
    private readonly DispatcherTimer _hideChromeTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly ImageViewportState _viewport = new();
    private readonly Canvas _imageSurface;
    private readonly Image _image;
    private readonly TextBlock _title;
    private readonly TextBlock _position;
    private readonly Button _zoomOut;
    private readonly Button _zoomPercent;
    private readonly Button _zoomIn;
    private readonly TextBlock _status;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly RotateTransform _rotation = new();
    private readonly ScaleTransform _scale = new();
    private readonly Dictionary<IPointer, Point> _touchPoints = [];
    private Point? _gestureStart;
    private Point _lastDragPosition;
    private IPointer? _desktopDragPointer;
    private bool _touchHadMultiplePointers;
    private double _lastPinchDistance;
    private Point _lastPinchCenter;
    private bool _chromeVisible;
    private bool _pointerOverChrome;

    public ImageViewerView(ImageViewerViewModel viewModel, bool mobile, Action close)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(close);
        _mobile = mobile;

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#FF0B1018")),
        };
        var closeButton = IconButton("CanXIconBack", "返回文件列表", mobile ? 24 : 20);
        closeButton.Click += (_, _) => close();
        _title = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = mobile ? 18 : 20,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
        };
        _position = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
        };
        var rotateLeft = IconButton("CanXIconRotateRight", "向左旋转 90°", mobile ? 22 : 20, mirror: true);
        rotateLeft.Click += (_, _) =>
        {
            _viewport.Rotate(-1);
            ApplyViewport();
            ShowChrome();
        };
        var rotateRight = IconButton("CanXIconRotateRight", "向右旋转 90°", mobile ? 22 : 20);
        rotateRight.Click += (_, _) =>
        {
            _viewport.Rotate(1);
            ApplyViewport();
            ShowChrome();
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
        top.Children.Add(closeButton);
        Grid.SetColumn(_title, 1);
        top.Children.Add(_title);
        Grid.SetColumn(_position, 2);
        top.Children.Add(_position);
        Grid.SetColumn(rotateLeft, 3);
        top.Children.Add(rotateLeft);
        Grid.SetColumn(rotateRight, 4);
        top.Children.Add(rotateRight);
        _topBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#99192331")),
            Padding = new Thickness(mobile ? 12 : 16, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Child = top,
        };

        _image = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            IsVisible = false,
            RenderTransformOrigin = RelativePoint.Center,
        };
        var transform = new TransformGroup();
        transform.Children.Add(_rotation);
        transform.Children.Add(_scale);
        _image.RenderTransform = transform;
        _imageSurface = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
        _imageSurface.Children.Add(_image);
        _status = new TextBlock
        {
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
        };
        _imageArea = new Border
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            Child = new Grid { ClipToBounds = true, Children = { _imageSurface, _status } },
        };
        _imageArea.SizeChanged += (_, _) =>
        {
            _viewport.SetViewport(_imageArea.Bounds.Size);
            ApplyViewport();
        };
        root.Children.Add(_imageArea);

        _previous = IconButton("CanXIconPrevious", "上一张图片", mobile ? 27 : 22);
        _previous.Click += async (_, _) => await _viewModel.MoveAsync(-1);
        _next = IconButton("CanXIconNext", "下一张图片", mobile ? 27 : 22);
        _next.Click += async (_, _) => await _viewModel.MoveAsync(1);
        _zoomOut = IconButton("CanXIconZoomOut", "缩小 10%", mobile ? 24 : 21);
        _zoomOut.Click += (_, _) =>
        {
            _viewport.ZoomStep(-1);
            ApplyViewport();
            ShowChrome();
        };
        _zoomPercent = new Button
        {
            Width = 66,
            Height = 48,
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_zoomPercent, "显示为 100% 原始尺寸");
        _zoomPercent.Click += (_, _) =>
        {
            _viewport.ZoomToOriginalSize();
            ApplyViewport();
            ShowChrome();
        };
        _zoomIn = IconButton("CanXIconZoomIn", "放大 10%", mobile ? 24 : 21);
        _zoomIn.Click += (_, _) =>
        {
            _viewport.ZoomStep(1);
            ApplyViewport();
            ShowChrome();
        };
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto,*") };
        _previous.HorizontalAlignment = HorizontalAlignment.Right;
        _next.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(_previous, 1);
        controls.Children.Add(_previous);
        Grid.SetColumn(_zoomOut, 2);
        controls.Children.Add(_zoomOut);
        Grid.SetColumn(_zoomPercent, 3);
        controls.Children.Add(_zoomPercent);
        Grid.SetColumn(_zoomIn, 4);
        controls.Children.Add(_zoomIn);
        Grid.SetColumn(_next, 5);
        controls.Children.Add(_next);
        _bottomBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#99192331")),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = controls,
        };
        root.Children.Add(_topBar);
        root.Children.Add(_bottomBar);

        _hideChromeTimer.Tick += (_, _) =>
        {
            if (!_mobile && _pointerOverChrome) return;
            HideChrome();
        };
        if (!mobile)
        {
            root.PointerEntered += (_, eventArgs) => OnDesktopPointerMoved(eventArgs.GetPosition(root), root.Bounds.Height);
            root.PointerMoved += (_, eventArgs) => OnDesktopPointerMoved(eventArgs.GetPosition(root), root.Bounds.Height);
            root.PointerExited += (_, _) => _pointerOverChrome = false;
            _imageArea.PointerWheelChanged += OnImagePointerWheelChanged;
            _imageArea.PointerPressed += OnDesktopPointerPressed;
            _imageArea.PointerMoved += OnDesktopImagePointerMoved;
            _imageArea.PointerReleased += OnDesktopPointerReleased;
            _imageArea.PointerCaptureLost += (_, eventArgs) =>
            {
                if (ReferenceEquals(_desktopDragPointer, eventArgs.Pointer)) _desktopDragPointer = null;
            };
        }

        if (mobile)
        {
            _imageArea.PointerPressed += OnImagePointerPressed;
            _imageArea.PointerMoved += OnImagePointerMoved;
            _imageArea.PointerReleased += OnImagePointerReleased;
            _imageArea.PointerCaptureLost += (_, eventArgs) =>
            {
                if (_touchPoints.Remove(eventArgs.Pointer))
                {
                    _gestureStart = null;
                    _touchHadMultiplePointers = true;
                    _lastPinchDistance = 0;
                }
            };
        }
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        AttachedToVisualTree += (_, _) => ShowChrome();
        DetachedFromVisualTree += (_, _) => _hideChromeTimer.Stop();
        Refresh();
        Content = root;
    }

    private static Button IconButton(string iconResource, string label, double size, bool mirror = false)
    {
        var icon = new PathIcon
        {
            Width = size,
            Height = size,
            Foreground = Brushes.White,
            Data = Avalonia.Application.Current?.TryFindResource(iconResource, out var value) == true
                ? value as Geometry
                : null,
        };
        if (mirror)
        {
            icon.RenderTransformOrigin = RelativePoint.Center;
            icon.RenderTransform = new ScaleTransform(-1, 1);
        }
        var button = new Button
        {
            Content = icon,
            Width = 52,
            Height = 48,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) => Refresh();

    private void Refresh()
    {
        _title.Text = _viewModel.Title;
        _position.Text = _viewModel.PositionText;
        _previous.IsEnabled = _viewModel.CanPrevious;
        _next.IsEnabled = _viewModel.CanNext;
        if (!ReferenceEquals(_image.Source, _viewModel.Image))
        {
            _image.Source = _viewModel.Image;
            var bitmap = _viewModel.Image;
            var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            _viewport.SetImage(bitmap is null ? default : new Size(
                bitmap.PixelSize.Width / renderScaling,
                bitmap.PixelSize.Height / renderScaling));
            _touchPoints.Clear();
            _gestureStart = null;
            _desktopDragPointer = null;
            ApplyViewport();
        }
        _status.Text = _viewModel.IsLoading ? "正在打开图片…" : _viewModel.ErrorMessage;
        _status.IsVisible = _viewModel.IsLoading || _viewModel.ErrorMessage is not null;
        AutomationProperties.SetName(_image, _viewModel.Title);
    }

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!_mobile) return;
        var position = eventArgs.GetPosition(_imageArea);
        _touchPoints[eventArgs.Pointer] = position;
        eventArgs.Pointer.Capture(_imageArea);
        if (_touchPoints.Count == 1)
        {
            _gestureStart = position;
            _lastDragPosition = position;
            _touchHadMultiplePointers = false;
        }
        else
        {
            _gestureStart = null;
            _touchHadMultiplePointers = true;
            UpdatePinchBaseline();
        }
        eventArgs.Handled = true;
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_mobile || !_touchPoints.ContainsKey(eventArgs.Pointer)) return;
        var position = eventArgs.GetPosition(_imageArea);
        _touchPoints[eventArgs.Pointer] = position;
        if (_touchPoints.Count >= 2)
        {
            if (_touchPoints.Count == 2 && GetPinchGeometry(out var distance, out var center))
            {
                if (_lastPinchDistance > 0)
                {
                    _viewport.ZoomBy(distance / _lastPinchDistance, _lastPinchCenter);
                    _viewport.PanBy(center - _lastPinchCenter);
                    ApplyViewport();
                }
                _lastPinchDistance = distance;
                _lastPinchCenter = center;
            }
            else
            {
                _lastPinchDistance = 0;
            }
        }
        else if (!_touchHadMultiplePointers && _viewport.CanPan)
        {
            _viewport.PanBy(position - _lastDragPosition);
            ApplyViewport();
        }
        _lastDragPosition = position;
        eventArgs.Handled = true;
    }

    private async void OnImagePointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_mobile || !_touchPoints.Remove(eventArgs.Pointer)) return;
        var end = eventArgs.GetPosition(_imageArea);
        var start = _gestureStart;
        var wasMultiTouch = _touchHadMultiplePointers;
        _gestureStart = null;
        _lastPinchDistance = 0;
        if (_touchPoints.Count == 0) _touchHadMultiplePointers = false;
        eventArgs.Pointer.Capture(null);
        if (wasMultiTouch || start is null) return;
        var moved = Math.Max(Math.Abs(end.X - start.Value.X), Math.Abs(end.Y - start.Value.Y));
        var direction = _viewport.CanPan && moved > 14 ? 0 :
            GestureDirection(start.Value, end, _imageArea.Bounds.Width);
        if (direction != 0) await _viewModel.MoveAsync(direction);
        else if (IsCenterTap(start.Value, end, _imageArea.Bounds.Width))
        {
            if (_chromeVisible) HideChrome();
            else ShowChrome();
        }
        eventArgs.Handled = true;
    }

    private void UpdatePinchBaseline()
    {
        _lastPinchDistance = GetPinchGeometry(out var distance, out var center) ? distance : 0;
        _lastPinchCenter = center;
    }

    private bool GetPinchGeometry(out double distance, out Point center)
    {
        distance = 0;
        center = default;
        if (_touchPoints.Count < 2) return false;
        using var points = _touchPoints.Values.GetEnumerator();
        points.MoveNext();
        var first = points.Current;
        points.MoveNext();
        var second = points.Current;
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        center = new Point((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        return distance > 0;
    }

    private void OnImagePointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (_mobile || !_viewport.HasImage || eventArgs.Delta.Y == 0) return;
        _viewport.ZoomBy(Math.Pow(1.1, Math.Clamp(eventArgs.Delta.Y, -6, 6)),
            eventArgs.GetPosition(_imageArea));
        ApplyViewport();
        eventArgs.Handled = true;
    }

    private void OnDesktopPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (_mobile || !_viewport.CanPan ||
            !eventArgs.GetCurrentPoint(_imageArea).Properties.IsLeftButtonPressed) return;
        _desktopDragPointer = eventArgs.Pointer;
        _lastDragPosition = eventArgs.GetPosition(_imageArea);
        eventArgs.Pointer.Capture(_imageArea);
        eventArgs.Handled = true;
    }

    private void OnDesktopImagePointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!ReferenceEquals(_desktopDragPointer, eventArgs.Pointer)) return;
        var position = eventArgs.GetPosition(_imageArea);
        _viewport.PanBy(position - _lastDragPosition);
        _lastDragPosition = position;
        ApplyViewport();
        eventArgs.Handled = true;
    }

    private void OnDesktopPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!ReferenceEquals(_desktopDragPointer, eventArgs.Pointer)) return;
        _desktopDragPointer = null;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }

    private void ApplyViewport()
    {
        var hasImage = _viewport.HasImage;
        _image.IsVisible = hasImage;
        _zoomOut.IsEnabled = _viewport.CanZoomOut;
        _zoomIn.IsEnabled = _viewport.CanZoomIn;
        _zoomPercent.IsEnabled = hasImage;
        var percentage = $"{Math.Round(_viewport.Scale * 100):0}%";
        if (!Equals(_zoomPercent.Content, percentage))
        {
            _zoomPercent.Content = percentage;
            AutomationProperties.SetName(_zoomPercent, $"当前缩放 {percentage}，点击显示为 100%");
        }
        if (!hasImage) return;

        _image.Width = _viewport.ImageSize.Width;
        _image.Height = _viewport.ImageSize.Height;
        _rotation.Angle = _viewport.RotationDegrees;
        _scale.ScaleX = _viewport.Scale;
        _scale.ScaleY = _viewport.Scale;
        Canvas.SetLeft(_image, (_imageArea.Bounds.Width - _viewport.ImageSize.Width) / 2 + _viewport.Offset.X);
        Canvas.SetTop(_image, (_imageArea.Bounds.Height - _viewport.ImageSize.Height) / 2 + _viewport.Offset.Y);
    }

    private void OnDesktopPointerMoved(Point position, double height)
    {
        var nearTop = position.Y <= Math.Max(80, _topBar.Bounds.Height + 12);
        var nearBottom = position.Y >= height - Math.Max(80, _bottomBar.Bounds.Height + 12);
        _pointerOverChrome = nearTop || nearBottom;
        if (_pointerOverChrome) ShowChrome();
    }

    private void ShowChrome()
    {
        _chromeVisible = true;
        _topBar.IsVisible = true;
        _bottomBar.IsVisible = true;
        _hideChromeTimer.Stop();
        _hideChromeTimer.Start();
    }

    private void HideChrome()
    {
        _hideChromeTimer.Stop();
        _chromeVisible = false;
        _topBar.IsVisible = false;
        _bottomBar.IsVisible = false;
    }

    internal static int GestureDirection(Point start, Point end, double width)
    {
        var horizontal = end.X - start.X;
        var vertical = end.Y - start.Y;
        var distance = Math.Max(Math.Abs(horizontal), Math.Abs(vertical));
        return distance >= 55
            ? Math.Abs(horizontal) >= Math.Abs(vertical)
                ? horizontal < 0 ? 1 : -1
                : vertical < 0 ? 1 : -1
            : end.X < width * 0.22
                ? -1
                : end.X > width * 0.78 ? 1 : 0;
    }

    internal static bool IsCenterTap(Point start, Point end, double width) =>
        Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)) <= 14 &&
        GestureDirection(start, end, width) == 0;

    public void Dispose()
    {
        _hideChromeTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _image.Source = null;
    }
}
