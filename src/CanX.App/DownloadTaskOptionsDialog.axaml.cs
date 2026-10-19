// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;

namespace CanX.App;

public sealed partial class DownloadTaskOptionsDialog : Window
{
    private readonly bool _useNativeFrame;

    public DownloadTaskOptionsDialog()
    {
        InitializeComponent();
        _useNativeFrame = OperatingSystem.IsMacOS();
        WindowDecorations = _useNativeFrame
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        CustomTitleBar.IsVisible = !_useNativeFrame;
        DialogFrame.BorderThickness = new Avalonia.Thickness(_useNativeFrame ? 0 : 1);
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Close();
            eventArgs.Handled = true;
        }
    }
}
