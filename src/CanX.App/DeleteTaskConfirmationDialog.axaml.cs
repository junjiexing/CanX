// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CanX.App;

public sealed partial class DeleteTaskConfirmationDialog : Window
{
    private readonly bool _useNativeFrame;

    public DeleteTaskConfirmationDialog()
        : this(string.Empty)
    {
    }

    public DeleteTaskConfirmationDialog(string taskName)
    {
        InitializeComponent();
        PromptText.Text = $"确定要删除任务“{taskName}”吗？";
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

    private void OnConfirmClick(object? sender, RoutedEventArgs eventArgs) =>
        Close(DeleteFilesCheckBox.IsChecked == true);

    private void OnCancelClick(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Close(null);
            eventArgs.Handled = true;
        }
    }
}
