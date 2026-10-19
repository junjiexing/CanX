// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CanX.App;

public sealed partial class PluginUninstallConfirmationDialog : Window
{
    public PluginUninstallConfirmationDialog()
        : this("插件")
    {
    }

    public PluginUninstallConfirmationDialog(string pluginName)
    {
        InitializeComponent();
        HeadingText.Text = $"确定卸载“{pluginName}”吗？";
        var nativeFrame = OperatingSystem.IsMacOS();
        WindowDecorations = nativeFrame
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        CustomTitleBar.IsVisible = !nativeFrame;
        DialogFrame.BorderThickness = new Avalonia.Thickness(nativeFrame ? 0 : 1);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs eventArgs) => Close(false);

    private void OnConfirmClick(object? sender, RoutedEventArgs eventArgs) => Close(true);
}
