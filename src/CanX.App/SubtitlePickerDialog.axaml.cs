// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CanX.App.ViewModels;
using CanX.Application.Discovery;
using CanX.MediaAccess;

namespace CanX.App;

internal sealed record SubtitlePickerResult(
    IReadOnlyList<string>? LocalPaths = null,
    MediaResource? NetworkResource = null,
    MediaSourceReadLease? NetworkLease = null,
    string? NetworkProfileId = null,
    OnlineSubtitleSource? OnlineSource = null);

public sealed partial class SubtitlePickerDialog : Window
{
    private sealed record LocationChoice(string? ProfileId, string Label, bool IsOnline = false)
    {
        public override string ToString() => Label;
    }

    private MediaSourceBrowserViewModel _sources = null!;
    private PlaybackViewModel _playback = null!;
    private OnlineSubtitleCatalog? _onlineSubtitles;
    private SubtitleSourceBrowser? _browser;
    private readonly CancellationTokenSource _browseCancellation = new();
    private bool _loading;

    public SubtitlePickerDialog()
    {
        InitializeComponent();
        var nativeFrame = OperatingSystem.IsMacOS();
        WindowDecorations = nativeFrame ? WindowDecorations.Full : WindowDecorations.None;
        CustomTitleBar.IsVisible = !nativeFrame;
        DialogFrame.BorderThickness = new Thickness(nativeFrame ? 0 : 1);
        Closed += async (_, _) =>
        {
            await _browseCancellation.CancelAsync();
            if (_browser is { } browser) await browser.DisposeAsync();
            _browseCancellation.Dispose();
        };
    }

    public SubtitlePickerDialog(
        MediaSourceBrowserViewModel sources,
        PlaybackViewModel playback,
        OnlineSubtitleCatalog? onlineSubtitles = null,
        bool startOnline = false)
        : this()
    {
        _sources = sources;
        _playback = playback;
        _onlineSubtitles = onlineSubtitles;
        var providers = onlineSubtitles?.Providers ?? [];
        OnlineProviderComboBox.ItemsSource = providers;
        OnlineProviderComboBox.SelectedIndex = providers.Count > 0 ? 0 : -1;

        var choices = new List<LocationChoice> { new(null, "本地文件夹"), new(null, "在线搜索", true) };
        choices.AddRange(sources.Sources.Select(source =>
            new LocationChoice(source.Id, $"{source.Name} · {source.Type}")));
        SourceComboBox.ItemsSource = choices;
        SourceComboBox.SelectedItem = startOnline
            ? choices[1]
            : choices.FirstOrDefault(choice =>
                !choice.IsOnline && choice.ProfileId == playback.CurrentMediaSourceProfileId) ?? choices[0];
        SourceComboBox.SelectionChanged += async (_, _) => await ChangeSourceAsync();
        SubtitleList.SelectionChanged += (_, _) => UpdateLoadButton();
        Opened += async (_, _) => await ChangeSourceAsync();
    }

    private async Task ChangeSourceAsync()
    {
        if (_loading) return;
        _loading = true;
        SourceComboBox.IsEnabled = false;
        SubtitleList.ItemsSource = null;
        LoadButton.IsEnabled = false;
        OnlineSearchPanel.IsVisible = false;
        OnlineResultList.ItemsSource = null;
        OnlineSourceList.ItemsSource = null;
        OnlineSourcesPanel.IsVisible = false;
        var previous = _browser;
        _browser = null;
        if (previous is not null) await previous.DisposeAsync();
        try
        {
            if (SourceComboBox.SelectedItem is LocationChoice { IsOnline: true })
            {
                NetworkNavigation.IsVisible = false;
                SubtitleList.IsVisible = false;
                LocalHint.IsVisible = false;
                OnlineSearchPanel.IsVisible = true;
                if (string.IsNullOrWhiteSpace(OnlineQueryBox.Text))
                    OnlineQueryBox.Text = OnlineSubtitleCatalog.SuggestedQuery(_playback.Title);
                StatusText.Text = _onlineSubtitles?.HasProviders == true
                    ? "输入关键词搜索在线字幕"
                    : "请先在插件页面安装并启用字幕搜索插件";
                return;
            }
            if (SourceComboBox.SelectedItem is not LocationChoice { ProfileId: { } profileId })
            {
                NetworkNavigation.IsVisible = false;
                SubtitleList.IsVisible = false;
                LocalHint.IsVisible = true;
                StatusText.Text = string.Empty;
                return;
            }

            NetworkNavigation.IsVisible = true;
            SubtitleList.IsVisible = true;
            LocalHint.IsVisible = false;
            StatusText.Text = "正在读取目录…";
            var mediaUri = profileId == _playback.CurrentMediaSourceProfileId
                ? _playback.CurrentMediaSourceUri : null;
            var browser = await _sources.OpenSubtitleSourceAsync(profileId, mediaUri,
                _browseCancellation.Token);
            if (_browseCancellation.IsCancellationRequested)
            {
                await browser.DisposeAsync();
                return;
            }
            _browser = browser;
            RefreshBrowser();
        }
        catch (OperationCanceledException) when (_browseCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SourceComboBox.IsEnabled = true;
            _loading = false;
        }
    }

    private void RefreshBrowser()
    {
        if (_browser is null) return;
        SubtitleList.ItemsSource = _browser.Entries;
        LocationText.Text = _browser.CurrentDirectory.IsFile
            ? _browser.CurrentDirectory.LocalPath : _browser.CurrentDirectory.AbsoluteUri;
        UpButton.IsEnabled = _browser.CanNavigateUp;
        StatusText.Text = _browser.Entries.Count == 0 ? "此目录没有字幕文件" : string.Empty;
        UpdateLoadButton();
    }

    private void UpdateLoadButton()
    {
        if (SourceComboBox.SelectedItem is LocationChoice { IsOnline: true })
        {
            LoadButton.IsEnabled = !_loading && OnlineSourceList.SelectedItem is OnlineSubtitleSource;
            LoadButton.Content = "加载";
            return;
        }
        LoadButton.IsEnabled = !_loading && SubtitleList.SelectedItem is MediaResource;
        LoadButton.Content = SubtitleList.SelectedItem is MediaResource { IsDirectory: true }
            ? "打开" : "加载";
    }

    private async void OnUpClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_browser is null || _loading) return;
        await BrowseAsync(() => _browser.NavigateUpAsync(_browseCancellation.Token));
    }

    private async void OnSubtitleDoubleTapped(object? sender, TappedEventArgs eventArgs) =>
        await OpenSelectedAsync();

    private async void OnLoadClick(object? sender, RoutedEventArgs eventArgs) =>
        await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        if (SourceComboBox.SelectedItem is LocationChoice { IsOnline: true })
        {
            if (!_loading && OnlineSourceList.SelectedItem is OnlineSubtitleSource source)
                Close(new SubtitlePickerResult(OnlineSource: source));
            return;
        }
        if (_browser is null || _loading || SubtitleList.SelectedItem is not MediaResource resource) return;
        if (resource.IsDirectory)
        {
            await BrowseAsync(() => _browser.BrowseAsync(resource.Uri, _browseCancellation.Token));
            return;
        }

        Close(new SubtitlePickerResult(NetworkResource: resource,
            NetworkLease: _browser.AcquireRead(resource),
            NetworkProfileId: (SourceComboBox.SelectedItem as LocationChoice)?.ProfileId));
    }

    private async void OnOnlineSearchClick(object? sender, RoutedEventArgs eventArgs) =>
        await SearchOnlineAsync();

    private void OnOnlineProviderSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_loading) return;
        OnlineResultList.ItemsSource = null;
        OnlineSourceList.ItemsSource = null;
        OnlineSourcesPanel.IsVisible = false;
        LoadButton.IsEnabled = false;
        StatusText.Text = OnlineProviderComboBox.SelectedItem is OnlineSubtitleProvider
            ? "输入关键词搜索在线字幕"
            : "请先在插件页面安装并启用字幕搜索插件";
    }

    private async void OnOnlineQueryKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter) await SearchOnlineAsync();
    }

    private async Task SearchOnlineAsync()
    {
        if (_loading || _onlineSubtitles is null) return;
        if (OnlineProviderComboBox.SelectedItem is not OnlineSubtitleProvider provider)
        {
            StatusText.Text = "请先选择字幕搜索接口";
            return;
        }
        _loading = true;
        OnlineSearchButton.IsEnabled = false;
        OnlineProviderComboBox.IsEnabled = false;
        SourceComboBox.IsEnabled = false;
        OnlineResultList.ItemsSource = null;
        OnlineSourceList.ItemsSource = null;
        OnlineSourcesPanel.IsVisible = false;
        StatusText.Text = "正在搜索字幕…";
        try
        {
            var page = await _onlineSubtitles.SearchAsync(OnlineQueryBox.Text ?? string.Empty, provider.Id,
                _browseCancellation.Token);
            if (_browseCancellation.IsCancellationRequested) return;
            OnlineResultList.ItemsSource = page.Items;
            StatusText.Text = page.Errors.Count > 0
                ? string.Join("；", page.Errors)
                : page.Items.Count == 0 ? "没有找到字幕，请尝试更短的关键词" : $"找到 {page.Items.Count} 条字幕";
        }
        catch (OperationCanceledException) when (_browseCancellation.IsCancellationRequested) { }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        finally
        {
            _loading = false;
            OnlineSearchButton.IsEnabled = true;
            OnlineProviderComboBox.IsEnabled = true;
            SourceComboBox.IsEnabled = true;
            UpdateLoadButton();
        }
    }

    private async void OnOnlineResultSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_loading || _onlineSubtitles is null ||
            OnlineResultList.SelectedItem is not OnlineSubtitleResult result) return;
        _loading = true;
        OnlineResultList.IsEnabled = false;
        OnlineProviderComboBox.IsEnabled = false;
        SourceComboBox.IsEnabled = false;
        OnlineSourceList.ItemsSource = null;
        OnlineSourcesPanel.IsVisible = false;
        StatusText.Text = "正在获取字幕文件…";
        try
        {
            var sources = await _onlineSubtitles.ResolveAsync(result, _browseCancellation.Token);
            if (_browseCancellation.IsCancellationRequested) return;
            OnlineSourceList.ItemsSource = sources;
            OnlineSourcesPanel.IsVisible = true;
            OnlineSourceList.SelectedIndex = sources.Count == 1 ? 0 : -1;
            StatusText.Text = sources.Count == 1 ? "可以加载字幕" : "请选择要加载的字幕文件";
        }
        catch (OperationCanceledException) when (_browseCancellation.IsCancellationRequested) { }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        finally
        {
            _loading = false;
            OnlineResultList.IsEnabled = true;
            OnlineProviderComboBox.IsEnabled = true;
            SourceComboBox.IsEnabled = true;
            UpdateLoadButton();
        }
    }

    private void OnOnlineSourceSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs) =>
        UpdateLoadButton();

    private async Task BrowseAsync(Func<Task> browse)
    {
        _loading = true;
        try
        {
            StatusText.Text = "正在读取目录…";
            await browse();
            RefreshBrowser();
        }
        catch (OperationCanceledException) when (_browseCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            UpdateLoadButton();
        }
    }

    private async void OnPickLocalClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            IStorageFolder? start = null;
            var directory = SubtitlePickerLocation.GetLocalVideoDirectory(_playback.Source);
            if (directory is not null)
            {
                try { start = await StorageProvider.TryGetFolderFromPathAsync(directory); }
                catch (Exception) { /* The native picker can still browse every local folder. */ }
            }

            var files = await OwnedStoragePicker.OpenFilePickerAsync(this, new FilePickerOpenOptions
            {
                Title = "选择外部字幕",
                AllowMultiple = true,
                SuggestedStartLocation = start,
                FileTypeFilter = [SubtitlePickerLocation.SubtitleFileType],
            });
            var paths = files.Select(file => file.Path.LocalPath).ToArray();
            foreach (var file in files) file.Dispose();
            if (paths.Length > 0) Close(new SubtitlePickerResult(LocalPaths: paths));
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(eventArgs);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
