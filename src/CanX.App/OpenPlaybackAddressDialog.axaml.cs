// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CanX.App;

public sealed partial class OpenPlaybackAddressDialog : Window
{
    public OpenPlaybackAddressDialog()
    {
        InitializeComponent();
        var nativeFrame = OperatingSystem.IsMacOS();
        WindowDecorations = nativeFrame
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        CustomTitleBar.IsVisible = !nativeFrame;
        DialogFrame.BorderThickness = new Avalonia.Thickness(nativeFrame ? 0 : 1);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => AddressTextBox.Focus(), DispatcherPriority.Input);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnAddressTextChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (OpenButton is null || ErrorText is null) return;
        OpenButton.IsEnabled = !string.IsNullOrWhiteSpace(AddressTextBox.Text);
        ErrorText.IsVisible = false;
    }

    private void OnOpenClick(object? sender, RoutedEventArgs eventArgs)
    {
        var source = AddressTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(source)) return;
        if (source.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            ErrorText.Text = "请输入单个媒体地址。";
            ErrorText.IsVisible = true;
            AddressTextBox.Focus();
            return;
        }

        Close(source);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs eventArgs) => Close();
}
