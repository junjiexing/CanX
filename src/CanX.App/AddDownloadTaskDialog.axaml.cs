// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CanX.App.ViewModels;
using CanX.Core.Downloads;

namespace CanX.App;

public sealed partial class AddDownloadTaskDialog : UserControl
{
    private bool _isClosing;
    private bool _isDuplicateRetrying;

    public AddDownloadTaskDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public event EventHandler? CloseRequested;

    private AddDownloadTaskViewModel ViewModel => (AddDownloadTaskViewModel)DataContext!;

    public void BeginOpen()
    {
        _isClosing = false;
        InlineErrorOverlay.IsVisible = false;
        DuplicateConfirmButton.IsVisible = false;
        DialogFrame.IsEnabled = true;
        Dispatcher.UIThread.Post(() =>
        {
            DownloadSourceTextBox.Focus();
            DownloadSourceTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private async void OnContinueClick(object? sender, RoutedEventArgs eventArgs) => await ContinueAsync();

    private async Task ContinueAsync(bool allowDuplicate = false)
    {
        var commitPendingMetadata = ViewModel.CanCommitPendingMetadata;
        try
        {
            if (commitPendingMetadata)
            {
                if (await ViewModel.CommitPendingMetadataAsync(allowDuplicate))
                {
                    await CloseAsync();
                }

                return;
            }

            await ViewModel.PrepareAsync();
        }
        catch (OperationCanceledException) when (_isClosing)
        {
        }
        catch (DuplicateDownloadException exception) when (!allowDuplicate)
        {
            ShowDuplicateConfirmation(exception);
        }
        catch (Exception exception)
        {
            var title = commitPendingMetadata ? "无法添加下载任务" : "无法读取下载信息";
            ShowError(title, exception.Message);
        }
    }

    private async void OnAddClick(object? sender, RoutedEventArgs eventArgs) => await AddAsync();

    private async Task AddAsync(bool allowDuplicate = false)
    {
        try
        {
            if (await ViewModel.AddAsync(allowDuplicate))
            {
                await CloseAsync();
            }
        }
        catch (DuplicateDownloadException exception) when (!allowDuplicate)
        {
            ShowDuplicateConfirmation(exception);
        }
        catch (Exception exception)
        {
            ShowError("无法添加下载任务", exception.Message);
        }
    }

    private async void OnBackClick(object? sender, RoutedEventArgs eventArgs)
    {
        await ViewModel.BackAsync();
        DownloadSourceTextBox.Focus();
    }

    private async void OnBrowseSourceClick(object? sender, RoutedEventArgs eventArgs)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            ShowError("无法选择文件", "当前平台没有可用的文件选择器。");
            return;
        }

        var metadataFiles = new FilePickerFileType("下载元数据")
        {
            Patterns = ["*.torrent", "*.meta4", "*.metalink"],
            MimeTypes = ["application/x-bittorrent", "application/metalink4+xml", "application/metalink+xml"],
        };
        var files = await OwnedStoragePicker.OpenFilePickerAsync(topLevel, new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择 Torrent 或 Metalink 文件",
            FileTypeFilter = [metadataFiles],
        });
        if (files.Count > 0)
        {
            ViewModel.SetSource(files[0].Path.LocalPath);
        }
    }

    private async void OnBrowseDestinationClick(object? sender, RoutedEventArgs eventArgs)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            ShowError("无法选择目录", "当前平台没有可用的目录选择器。");
            return;
        }

        var folders = await OwnedStoragePicker.OpenFolderPickerAsync(topLevel, new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择下载目录",
        });
        if (folders.Count > 0)
        {
            ViewModel.DestinationDirectory = folders[0].Path.LocalPath;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        var files = eventArgs.DataTransfer.TryGetFiles();
        var text = eventArgs.DataTransfer.TryGetText();
        eventArgs.DragEffects = files?.Length == 1 || !string.IsNullOrWhiteSpace(text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs eventArgs)
    {
        var files = eventArgs.DataTransfer.TryGetFiles();
        if (files?.Length == 1)
        {
            ViewModel.SetSource(files[0].Path.LocalPath);
            eventArgs.Handled = true;
            return;
        }

        var text = eventArgs.DataTransfer.TryGetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            ViewModel.SetSource(text);
        }

        eventArgs.Handled = true;
    }

    private async void OnCancelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isClosing)
        {
            return;
        }

        await CloseAsync();
    }

    private async void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            if (InlineErrorOverlay.IsVisible)
            {
                CloseError();
            }
            else
            {
                await CloseAsync();
            }
        }
        else if (eventArgs.KeyModifiers == KeyModifiers.Control && eventArgs.Key == Key.Enter)
        {
            eventArgs.Handled = true;
            if (ViewModel.IsSourceStep)
            {
                await ContinueAsync();
            }
            else
            {
                await AddAsync();
            }
        }
    }

    private async Task CloseAsync()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        await ViewModel.DisposeAsync();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(string title, string message)
    {
        _isDuplicateRetrying = false;
        InlineErrorTitle.Text = title;
        InlineErrorMessage.Text = message;
        InlineErrorCloseButton.Content = "确定";
        InlineErrorCloseButton.IsEnabled = true;
        InlineErrorCloseButton.IsDefault = true;
        DuplicateConfirmButton.IsVisible = false;
        DuplicateConfirmButton.IsDefault = false;
        DialogFrame.IsEnabled = false;
        InlineErrorOverlay.IsVisible = true;
        InlineErrorCloseButton.Focus();
    }

    private void OnCloseErrorClick(object? sender, RoutedEventArgs eventArgs) => CloseError();

    private void ShowDuplicateConfirmation(DuplicateDownloadException exception)
    {
        _isDuplicateRetrying = false;
        InlineErrorTitle.Text = "发现重复任务";
        InlineErrorMessage.Text =
            $"任务“{exception.ExistingDisplayName}”已经存在。是否仍然添加一个重复任务？";
        InlineErrorCloseButton.Content = "取消";
        InlineErrorCloseButton.IsEnabled = true;
        InlineErrorCloseButton.IsDefault = false;
        DuplicateConfirmButton.IsEnabled = true;
        DuplicateConfirmButton.IsVisible = true;
        DuplicateConfirmButton.IsDefault = true;
        DialogFrame.IsEnabled = false;
        InlineErrorOverlay.IsVisible = true;
        DuplicateConfirmButton.Focus();
    }

    private async void OnConfirmDuplicateClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isDuplicateRetrying)
        {
            return;
        }

        _isDuplicateRetrying = true;
        InlineErrorCloseButton.IsEnabled = false;
        DuplicateConfirmButton.IsEnabled = false;
        try
        {
            CloseError();
            if (ViewModel.CanCommitPendingMetadata)
            {
                await ContinueAsync(allowDuplicate: true);
            }
            else if (ViewModel.IsDetailsStep)
            {
                await AddAsync(allowDuplicate: true);
            }
        }
        finally
        {
            _isDuplicateRetrying = false;
        }
    }

    private void CloseError()
    {
        InlineErrorOverlay.IsVisible = false;
        DuplicateConfirmButton.IsVisible = false;
        DialogFrame.IsEnabled = true;
        DownloadSourceTextBox.Focus();
    }
}
