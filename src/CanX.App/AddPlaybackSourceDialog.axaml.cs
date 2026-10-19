// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CanX.App.ViewModels;

namespace CanX.App;

public sealed partial class AddPlaybackSourceDialog : UserControl
{
    private bool _isClosing;

    public AddPlaybackSourceDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public event EventHandler? CloseRequested;

    private AddPlaybackSourceViewModel ViewModel => (AddPlaybackSourceViewModel)DataContext!;

    public void BeginOpen()
    {
        _isClosing = false;
        ErrorText.IsVisible = false;
        Dispatcher.UIThread.Post(() => SourceTypeComboBox.Focus(), DispatcherPriority.Input);
    }

    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs eventArgs)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            ShowError("当前平台没有可用的目录选择器。");
            return;
        }

        var folders = await OwnedStoragePicker.OpenFolderPickerAsync(topLevel, new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择本地媒体文件夹",
        });
        if (folders.Count > 0)
        {
            ViewModel.LocalFolder = folders[0].Path.LocalPath;
            ErrorText.IsVisible = false;
        }
    }

    private async void OnAddClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            ErrorText.IsVisible = false;
            await ViewModel.AddAsync();
            Close();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.IsBusy)
        {
            Close();
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape && !ViewModel.IsBusy)
        {
            eventArgs.Handled = true;
            Close();
        }
        else if (eventArgs.KeyModifiers == KeyModifiers.Control && eventArgs.Key == Key.Enter && ViewModel.CanAdd)
        {
            eventArgs.Handled = true;
            try
            {
                ErrorText.IsVisible = false;
                await ViewModel.AddAsync();
                Close();
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Close()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
