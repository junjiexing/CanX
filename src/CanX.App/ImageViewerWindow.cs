// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using CanX.App.Controls;
using CanX.App.ViewModels;

namespace CanX.App;

public sealed class ImageViewerWindow : Window
{
    private readonly ImageViewerViewModel _viewModel;
    private readonly ImageViewerView _view;
    private readonly bool _useNativeFrame = OperatingSystem.IsMacOS();
    private readonly Border _windowFrame;
    private readonly Border _customTitleBar;
    private readonly Button _maximizeButton;
    private readonly Grid _resizeLayer;

    public ImageViewerWindow(ImageViewerViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Title = "CanX 图片查看器";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CanX.App/Assets/CanXLogo.png")));
        AutomationProperties.SetName(this, "CanX 图片查看器");
        Width = 1100;
        Height = 720;
        MinWidth = 360;
        MinHeight = 280;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Match PlaybackWindow: CanX draws its own title bar and border except on
        // macOS, where the native frame keeps fullscreen and traffic lights working.
        WindowDecorations = _useNativeFrame ? WindowDecorations.Full : WindowDecorations.None;
        _view = new ImageViewerView(viewModel, mobile: false, Close);

        _maximizeButton = TitleBarButton("maximize", "最大化图片查看器窗口", "最大化", (_, _) => ToggleMaximized());
        _customTitleBar = CreateTitleBar();
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(_customTitleBar);
        Grid.SetRow(_view, 1);
        layout.Children.Add(_view);
        _windowFrame = new Border { BorderThickness = new Thickness(1), Child = layout };
        _windowFrame.Bind(Border.BackgroundProperty, this.GetResourceObservable("CanXBackgroundBrush"));
        _windowFrame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CanXBorderBrush"));
        _resizeLayer = CreateResizeLayer();
        var root = new Grid();
        root.Children.Add(_windowFrame);
        root.Children.Add(_resizeLayer);
        Content = root;

        UpdateWindowChrome();
        PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == WindowStateProperty)
            {
                UpdateWindowChrome();
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            _view.Dispose();
            _viewModel.Dispose();
        };
    }

    /// <summary>
    /// Shows the viewer as an independent top-level window centered on the
    /// screen of <paramref name="origin"/>. It is deliberately not owned by the
    /// main window, so either window can be brought to the front or minimized
    /// on its own; MainWindow closes the viewer when it closes.
    /// </summary>
    public async Task ShowImageAsync(Window origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if ((Screens.ScreenFromWindow(origin) ?? Screens.ScreenFromPoint(origin.Position)) is { } screen)
        {
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            var size = PixelSize.FromSize(new Size(Width, Height), scaling);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = screen.WorkingArea.CenterRect(new PixelRect(size)).Position;
        }

        Show();
        Activate();
        await _viewModel.LoadAsync();
    }

    private Border CreateTitleBar()
    {
        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Source = new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri("avares://CanX.App/Assets/CanXLogo.png"))),
            Stretch = Stretch.Uniform,
        };
        var title = new TextBlock
        {
            Text = "CanX 图片查看器",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var buttons = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
        };
        buttons.Children.Add(TitleBarButton("minimize", "最小化图片查看器窗口", "最小化",
            (_, _) => WindowState = WindowState.Minimized));
        buttons.Children.Add(_maximizeButton);
        buttons.Children.Add(TitleBarButton("close", "关闭图片查看器窗口", "关闭", (_, _) => Close()));

        var content = new Grid();
        content.Children.Add(icon);
        content.Children.Add(title);
        content.Children.Add(buttons);
        var titleBar = new Border
        {
            Height = 30,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content,
        };
        titleBar.Bind(Border.BackgroundProperty, this.GetResourceObservable("CanXTitleBarBrush"));
        titleBar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CanXBorderBrush"));
        titleBar.PointerPressed += OnTitleBarPointerPressed;
        return titleBar;
    }

    private static Button TitleBarButton(
        string kind,
        string automationName,
        string toolTip,
        EventHandler<RoutedEventArgs> click)
    {
        var button = new Button();
        button.Classes.Add("titlebar");
        button.Classes.Add(kind);
        AutomationProperties.SetName(button, automationName);
        ToolTip.SetTip(button, toolTip);
        button.Click += click;
        return button;
    }

    private Grid CreateResizeLayer()
    {
        // Same thin hit areas as PlaybackWindow's ResizeLayer: 4 px edges and
        // 10 px tall corners so the title bar buttons remain clickable.
        var layer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("4,*,4"),
            RowDefinitions = new RowDefinitions("10,*,10"),
        };
        AddResizeArea(layer, 0, 1, WindowEdge.North, StandardCursorType.SizeNorthSouth, height: 4, VerticalAlignment.Top);
        AddResizeArea(layer, 2, 1, WindowEdge.South, StandardCursorType.SizeNorthSouth, height: 4, VerticalAlignment.Bottom);
        AddResizeArea(layer, 1, 0, WindowEdge.West, StandardCursorType.SizeWestEast);
        AddResizeArea(layer, 1, 2, WindowEdge.East, StandardCursorType.SizeWestEast);
        AddResizeArea(layer, 0, 0, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner);
        AddResizeArea(layer, 0, 2, WindowEdge.NorthEast, StandardCursorType.TopRightCorner);
        AddResizeArea(layer, 2, 0, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner);
        AddResizeArea(layer, 2, 2, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner);
        return layer;
    }

    private void AddResizeArea(
        Grid layer,
        int row,
        int column,
        WindowEdge edge,
        StandardCursorType cursor,
        double height = double.NaN,
        VerticalAlignment verticalAlignment = VerticalAlignment.Stretch)
    {
        var area = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
            Height = height,
            VerticalAlignment = verticalAlignment,
        };
        Grid.SetRow(area, row);
        Grid.SetColumn(area, column);
        area.PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
            {
                BeginResizeDrag(edge, eventArgs);
                eventArgs.Handled = true;
            }
        };
        layer.Children.Add(area);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            ToggleMaximized();
            eventArgs.Handled = true;
            return;
        }

        BeginMoveDrag(eventArgs);
        eventArgs.Handled = true;
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateWindowChrome()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        _resizeLayer.IsVisible = !_useNativeFrame && WindowState == WindowState.Normal;
        _customTitleBar.IsVisible = !_useNativeFrame && WindowState != WindowState.FullScreen;
        _windowFrame.BorderThickness = new Thickness(
            _useNativeFrame || isMaximized || WindowState == WindowState.FullScreen ? 0 : 1);
        _maximizeButton.Classes.Set("restore", isMaximized);
        AutomationProperties.SetName(_maximizeButton, isMaximized ? "还原图片查看器窗口" : "最大化图片查看器窗口");
        ToolTip.SetTip(_maximizeButton, isMaximized ? "还原" : "最大化");
    }

    private async void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Close();
            return;
        }
        if (eventArgs.KeyModifiers != KeyModifiers.None || eventArgs.Key is not (Key.Left or Key.Right)) return;
        eventArgs.Handled = true;
        await _viewModel.MoveAsync(eventArgs.Key == Key.Right ? 1 : -1);
    }
}
