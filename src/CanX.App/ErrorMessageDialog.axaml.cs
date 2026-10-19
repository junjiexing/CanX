// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CanX.App;

public sealed partial class ErrorMessageDialog : Window
{
    private readonly bool _useNativeFrame;

    public ErrorMessageDialog()
        : this("错误", string.Empty)
    {
    }

    public ErrorMessageDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        _useNativeFrame = OperatingSystem.IsMacOS();
        WindowDecorations = _useNativeFrame
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        CustomTitleBar.IsVisible = !_useNativeFrame;
        DialogFrame.BorderThickness = new Avalonia.Thickness(_useNativeFrame ? 0 : 1);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is Key.Enter or Key.Escape)
        {
            Close();
            eventArgs.Handled = true;
        }
    }
}
