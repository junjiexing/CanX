// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CanX.App.Automation;
using CanX.App.ViewModels;
using CanX.Application.Discovery;
using CanX.Application.Playback;
using CanX.Core.Configuration;
using CanX.Core.Downloads;
using CanX.Core.Playback;
using CanX.MediaAccess;
using CanX.Playback.Engine;
using CanX.Plugins.Downloads;
using CanX.Plugins.Media;
using CanX.Plugins.Ui;
using CanX.Streaming;

namespace CanX.App;

public sealed partial class MainWindow : Window
{
    private readonly bool _useNativeFrame;
    private readonly MediaStreamBroker? _streamBroker;
    private AddDownloadTaskViewModel? _addTaskOverlayViewModel;
    private AddPlaybackSourceViewModel? _addPlaybackSourceOverlayViewModel;
    private DownloadTaskOptionsDialog? _taskOptionsDialog;
    private ImageViewerWindow? _imageViewerWindow;
    private int _focusRegion = -1;
    private ShellPage _currentPage = ShellPage.Downloads;
    private IDisposable? _pluginDownloadPromptRegistration;
    private IDisposable? _pluginMediaPlaybackRegistration;
    private string? _configurationPluginId;
    private long _automationNotificationVersion;
    private FileAssociationsPanel? _fileAssociationsPanel;
    private readonly Queue<string> _pendingAssociatedMetadataFiles = new();

    public MainWindow()
    {
        InitializeComponent();
        AddDownloadTaskPanel.CloseRequested += OnAddTaskOverlayCloseRequested;
        AddPlaybackSourcePanel.CloseRequested += OnAddPlaybackSourceOverlayCloseRequested;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        DownloadTaskGrid.AddHandler(
            PointerPressedEvent,
            OnDownloadTaskGridPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackSourceGrid.AddHandler(
            PointerPressedEvent,
            OnPlaybackSourceGridPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        WebDavEntryGrid.AddHandler(
            TappedEvent,
            OnMediaSourceColumnHeaderTapped,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlaybackHistoryGrid.AddHandler(
            PointerPressedEvent,
            OnPlaybackHistoryGridPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        RecentFoldersGrid.AddHandler(
            TappedEvent,
            OnRecentFolderTapped,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        _useNativeFrame = OperatingSystem.IsMacOS();
        if (IFileAssociationPlatform.CreateForCurrentPlatform() is { } fileAssociations)
        {
            _fileAssociationsPanel = new FileAssociationsPanel(fileAssociations);
            FileAssociationsHost.Content = _fileAssociationsPanel;
            FileAssociationsHost.IsVisible = true;
            // Windows defaults are changed in the system Settings app; pick up the
            // user's choice when they return to CanX.
            Activated += (_, _) =>
            {
                if (_currentPage == ShellPage.Settings)
                {
                    _ = _fileAssociationsPanel.RefreshAsync();
                }
            };
        }
        WindowDecorations = _useNativeFrame
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        CustomTitleBar.IsVisible = !_useNativeFrame;
        UpdateWindowChrome();
        PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == WindowStateProperty)
            {
                UpdateWindowChrome();
            }
        };
        Closed += async (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
            {
                shell.PluginUi.ConfigurationCloseRequested -= OnPluginConfigurationCloseRequested;
                shell.Settings.PlaybackEndActionChanged -= ApplyPlaybackEndActionAsync;
                shell.Settings.SeekPreviewSettingsChanged -= ApplySeekPreviewSettings;
            }
            _pluginDownloadPromptRegistration?.Dispose();
            _pluginDownloadPromptRegistration = null;
            _pluginMediaPlaybackRegistration?.Dispose();
            _pluginMediaPlaybackRegistration = null;
            _playbackSessionsClosing = true;
            foreach (var session in _playbackSessions.ToArray()) session.Window?.Close();
            _imageViewerWindow?.Close();
            if (_addTaskOverlayViewModel is not null)
            {
                await _addTaskOverlayViewModel.DisposeAsync();
            }
        };
    }

    public MainWindow(
        ShellViewModel viewModel,
        IMpvRenderSource renderSource,
        MediaStreamBroker streamBroker,
        PluginDownloadPromptBroker? downloadPromptBroker = null,
        PluginMediaPlaybackBroker? mediaPlaybackBroker = null,
        Func<DesktopPlaybackSession>? playbackSessionFactory = null,
        Action? activatePrimarySession = null)
        : this()
    {
        DataContext = viewModel;
        PlaybackPage.DataContext = viewModel.MediaSources;
        foreach (var column in WebDavEntryGrid.Columns)
        {
            if (column.Header is Control header)
            {
                header.DataContext = viewModel.MediaSources;
            }
        }
        PlaybackHistoryPage.DataContext = viewModel.Playback;
        PluginHomePanelHost.Registry = viewModel.PluginUi;
        PluginHomeStatusHost.Registry = viewModel.PluginUi;
        PluginDownloadsDetailsHost.Registry = viewModel.PluginUi;
        PluginDownloadsStatusHost.Registry = viewModel.PluginUi;
        PluginDownloadsMenuHost.Registry = viewModel.PluginUi;
        PluginNavigationHost.Registry = viewModel.PluginUi;
        PluginCommandPaletteHost.Registry = viewModel.PluginUi;
        PluginAppMenuHost.Registry = viewModel.PluginUi;
        PluginSettingsHost.Registry = viewModel.PluginUi;
        PluginConfigurationHost.Registry = viewModel.PluginUi;
        viewModel.PluginUi.ConfigurationCloseRequested += OnPluginConfigurationCloseRequested;
        viewModel.Settings.PlaybackEndActionChanged += ApplyPlaybackEndActionAsync;
        viewModel.Settings.SeekPreviewSettingsChanged += ApplySeekPreviewSettings;
        viewModel.PluginPages.ActivePageRemoved += OnActivePluginPageRemoved;
        _pluginDownloadPromptRegistration = downloadPromptBroker?.RegisterHandler(ShowPluginDownloadPromptAsync);
        _pluginMediaPlaybackRegistration = mediaPlaybackBroker?.RegisterHandler(ShowPluginMediaAsync);
        _primaryPlaybackSession = new DesktopPlaybackSession(viewModel.Playback, renderSource, false, activatePrimarySession);
        _activePlaybackSession = _primaryPlaybackSession;
        _playbackSessions.Add(_primaryPlaybackSession);
        _playbackSessionFactory = playbackSessionFactory;
        _streamBroker = streamBroker;
    }

    private ShellViewModel ViewModel => (ShellViewModel)DataContext!;

    private async Task ApplyPlaybackEndActionAsync(bool autoPlayNext)
    {
        foreach (var session in _playbackSessions.ToArray())
        {
            if (!ReferenceEquals(session, _primaryPlaybackSession) && !session.IsClosing &&
                session.ViewModel.IsReady)
                await session.ViewModel.Controller.SetAutoPlayNextAsync(autoPlayNext);
        }
    }

    private void ApplySeekPreviewSettings(MpvSettings settings)
    {
        foreach (var session in _playbackSessions.ToArray())
            session.Window?.ApplySeekPreviewSettings(settings);
    }

    private void OnDownloadNavigationClick(object? sender, RoutedEventArgs eventArgs) => ShowPage(ShellPage.Downloads);

    private void OnPlaybackNavigationClick(object? sender, RoutedEventArgs eventArgs)
    {
        ShowPage(ShellPage.Playback);
        ShowPlaybackSources();
    }

    private void OnPlaybackHistoryNavigationClick(object? sender, RoutedEventArgs eventArgs) =>
        ShowPage(ShellPage.PlaybackHistory);

    private void OnSettingsNavigationClick(object? sender, RoutedEventArgs eventArgs) => ShowPage(ShellPage.Settings);

    private void OnAgentNavigationClick(object? sender, RoutedEventArgs eventArgs) => ShowPage(ShellPage.Agent);

    private void OnPluginsNavigationClick(object? sender, RoutedEventArgs eventArgs) => ShowPage(ShellPage.Plugins);

    private async void OnPluginPageNavigationClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: PluginPageItemViewModel page })
        {
            return;
        }
        var activation = ViewModel.PluginPages.ActivateAsync(page.PluginId);
        if (ViewModel.PluginPages.SelectedPage?.PluginId == page.PluginId)
        {
            ShowPage(ShellPage.PluginPage);
        }
        await activation.ConfigureAwait(true);
    }

    private void OnActivePluginPageRemoved(object? sender, EventArgs eventArgs)
    {
        if (_currentPage == ShellPage.PluginPage)
        {
            ShowPage(ShellPage.Downloads);
        }
    }

    private async void OnInstallPluginClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.Plugins is null)
        {
            return;
        }

        var patterns = ViewModel.Plugins.IsDeveloperMode
            ? new[] { "*.canxplugin", "*.zip" }
            : new[] { "*.canxplugin" };
        var files = await OwnedStoragePicker.OpenFilePickerAsync(this, new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "安装或升级 CanX 插件",
            FileTypeFilter =
            [
                new FilePickerFileType("CanX 插件包") { Patterns = patterns },
            ],
        });
        if (files.Count > 0)
        {
            await ViewModel.Plugins.InstallAsync(files[0].Path.LocalPath);
        }
    }

    private async void OnUninstallPluginClick(object? sender, RoutedEventArgs eventArgs)
    {
        var plugins = ViewModel.Plugins;
        if (plugins?.SelectedPlugin is not { } selected)
        {
            return;
        }

        var confirmed = await new PluginUninstallConfirmationDialog(selected.Name).ShowDialog<bool>(this);
        if (confirmed)
        {
            await plugins.UninstallSelectedAsync();
        }
    }

    private async void OnOpenPluginLogsClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.Plugins is null)
        {
            return;
        }
        Directory.CreateDirectory(ViewModel.Plugins.LogDirectory);
        await Launcher.LaunchUriAsync(new Uri(ViewModel.Plugins.LogDirectory));
    }

    private async void OnAddTaskClick(object? sender, RoutedEventArgs eventArgs) =>
        await ShowAddTaskOverlayAsync();

    private async void OnOpenTaskOptionsClick(object? sender, RoutedEventArgs eventArgs) =>
        await ShowTaskOptionsDialogAsync();

    private static void OnDownloadTaskLoadingRow(object? sender, DataGridRowEventArgs eventArgs)
    {
        ToolTip.SetShowDelay(eventArgs.Row, 450);
        ToolTip.SetBetweenShowDelay(eventArgs.Row, 150);
        ToolTip.SetVerticalOffset(eventArgs.Row, 8);
        ToolTip.SetTip(eventArgs.Row, new DownloadTaskToolTip
        {
            DataContext = eventArgs.Row.DataContext,
        });
    }

    private static void OnDownloadTaskUnloadingRow(object? sender, DataGridRowEventArgs eventArgs) =>
        ToolTip.SetTip(eventArgs.Row, null);

    private async void OnRemoveTaskClick(object? sender, RoutedEventArgs eventArgs)
    {
        var task = ViewModel.DownloadCenter.SelectedTask;
        if (task is null)
        {
            return;
        }

        var deleteFiles = await new DeleteTaskConfirmationDialog(task.Name).ShowDialog<bool?>(this);
        if (deleteFiles is not null)
        {
            await ViewModel.DownloadCenter.RemoveSelectedAsync(deleteFiles.Value);
        }
    }

    private async void OnPlayDownloadTaskClick(object? sender, RoutedEventArgs eventArgs) =>
        await PlaySelectedDownloadAsync();

    internal async Task PlaySelectedDownloadAsync()
    {
        var downloadCenter = ViewModel.DownloadCenter;
        var task = downloadCenter.SelectedTask?.Snapshot;
        var file = downloadCenter.SelectedPlayableFile;
        if (task is null || file is null)
        {
            return;
        }

        await RunPlaybackActionAsync(async () =>
        {
            var session = await PreparePlaybackSessionAsync();
            var localPath = Path.IsPathRooted(file.Path)
                ? Path.GetFullPath(file.Path)
                : Path.GetFullPath(file.Path, task.DestinationDirectory);
            if (task.State == DownloadState.Complete ||
                (file.Length > 0 && file.CompletedLength >= file.Length))
            {
                if (!File.Exists(localPath))
                {
                    throw new FileNotFoundException("已完成的媒体文件不存在。", localPath);
                }

                session.ReleaseResources();
                var playbackWindow = session.Window!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
                await session.ViewModel.OpenAsync(localPath, timeout.Token);
                return;
            }

            if (task.State == DownloadState.Paused)
            {
                await downloadCenter.ResumeTaskAsync(task.Id);
            }

            var broker = _streamBroker ?? throw new InvalidOperationException("媒体流服务尚未初始化。");
            var registration = broker.Register(
                Path.GetFileName(file.Path),
                () => downloadCenter.OpenMediaStreamAsync(task.Id, file.Index)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult(),
                MediaStreamCachePolicy.Disabled);
            session.ReleaseResources();
            session.StreamRegistrations.Add(registration);

            try
            {
                var playbackWindow = session.Window!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
                await session.ViewModel.OpenAsync(registration.Source, timeout.Token);
            }
            catch
            {
                session.ReleaseResources();
                throw;
            }
        });
    }

    private async void OnDownloadTaskGridPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var updateKind = eventArgs.GetCurrentPoint(DownloadTaskGrid).Properties.PointerUpdateKind;
        if (updateKind == PointerUpdateKind.RightButtonPressed)
        {
            SelectTaskFromEventSource(eventArgs.Source);
        }
        else if (updateKind == PointerUpdateKind.LeftButtonPressed &&
                 eventArgs.ClickCount == 2 &&
                 SelectTaskFromEventSource(eventArgs.Source))
        {
            await ShowTaskOptionsDialogAsync();
            eventArgs.Handled = true;
        }
    }

    private async void OnBrowseDefaultDownloadDirectoryClick(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await OwnedStoragePicker.OpenFolderPickerAsync(this, new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择默认下载目录",
        });
        if (folders.Count > 0)
        {
            ViewModel.Settings.DownloadDirectory = folders[0].Path.LocalPath;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        var files = eventArgs.DataTransfer.TryGetFiles();
        var text = eventArgs.DataTransfer.TryGetText()?.Trim();
        var playbackAreaVisible = PlaybackPage.IsVisible || PlaybackHistoryPage.IsVisible;
        var canOpenPlaybackFiles = playbackAreaVisible && files is { Length: > 0 } &&
            files.All(file => IsSupportedMediaFile(file.Path.LocalPath));
        eventArgs.DragEffects = canOpenPlaybackFiles ||
            files is { Length: 1 } && IsSupportedMetadataFile(files[0].Path.LocalPath) ||
            !string.IsNullOrWhiteSpace(text) && text.IndexOfAny(['\r', '\n']) < 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs eventArgs)
    {
        var files = eventArgs.DataTransfer.TryGetFiles();
        if ((PlaybackPage.IsVisible || PlaybackHistoryPage.IsVisible) && files is { Length: > 0 } &&
            files.All(file => IsSupportedMediaFile(file.Path.LocalPath)))
        {
            await OpenSourcesInPlaybackWindowAsync(files.Select(file => file.Path.LocalPath).ToList());
            return;
        }

        if (files is { Length: 1 } && IsSupportedMetadataFile(files[0].Path.LocalPath))
        {
            await ShowAddTaskOverlayAsync(files[0].Path.LocalPath);
            return;
        }

        var text = eventArgs.DataTransfer.TryGetText()?.Trim();
        if (!string.IsNullOrWhiteSpace(text) && text.IndexOfAny(['\r', '\n']) < 0)
        {
            if (PlaybackPage.IsVisible || PlaybackHistoryPage.IsVisible)
            {
                await OpenInPlaybackWindowAsync(text);
            }
            else
            {
                await ShowAddTaskOverlayAsync(text);
            }
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (PluginConfigurationOverlay.IsVisible)
        {
            if (eventArgs.Key == Key.Escape)
            {
                eventArgs.Handled = true;
                await ClosePluginConfigurationAsync(notifyPlugin: true);
            }
            return;
        }
        if (AddDownloadOverlay.IsVisible || AddPlaybackSourceOverlay.IsVisible)
        {
            return;
        }

        if (eventArgs.KeyModifiers == KeyModifiers.Control && eventArgs.Key == Key.L)
        {
            eventArgs.Handled = true;
            ShowPage(ShellPage.Downloads);
            _focusRegion = 0;
            await ShowAddTaskOverlayAsync();
            return;
        }

        if (eventArgs.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && eventArgs.Key == Key.L)
        {
            ShowPage(ShellPage.Downloads);
            DownloadTaskGrid.Focus();
            _focusRegion = 1;
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.F6)
        {
            if (SettingsPage.IsVisible)
            {
                ThemeComboBox.Focus();
                eventArgs.Handled = true;
                return;
            }

            if (PlaybackPage.IsVisible)
            {
                Control[] playbackRegions = MediaSourceBrowserView.IsVisible
                    ?
                    [
                        BackToPlaybackSourcesButton,
                        WebDavNavigateUpButton,
                        WebDavEntryGrid,
                        WebDavSubtitleComboBox,
                        SettingsNavigationButton,
                    ]
                    :
                    [
                        OpenLocalMediaButton,
                        PlaybackSourceGrid,
                        SettingsNavigationButton,
                    ];
                var playbackOffset = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                _focusRegion = (_focusRegion + playbackOffset + playbackRegions.Length) % playbackRegions.Length;
                playbackRegions[_focusRegion].Focus();
                eventArgs.Handled = true;
                return;
            }

            if (PlaybackHistoryPage.IsVisible)
            {
                Control[] historyRegions =
                [
                    PlaybackHistoryNavigationButton,
                    PlaybackHistoryGrid,
                    SettingsNavigationButton,
                ];
                var historyOffset = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                _focusRegion = (_focusRegion + historyOffset + historyRegions.Length) % historyRegions.Length;
                historyRegions[_focusRegion].Focus();
                eventArgs.Handled = true;
                return;
            }

            var regions = new Control[]
            {
                AddDownloadTaskButton,
                DownloadTaskGrid,
                SettingsNavigationButton,
            };
            var offset = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            _focusRegion = (_focusRegion + offset + regions.Length) % regions.Length;
            regions[_focusRegion].Focus();
            eventArgs.Handled = true;
        }
    }

    private void ShowPage(ShellPage page)
    {
        if (_currentPage == ShellPage.PluginPage && page != ShellPage.PluginPage)
        {
            _ = ViewModel.PluginPages.DeactivateAsync();
        }
        _currentPage = page;
        DownloadCenterPage.IsVisible = page == ShellPage.Downloads;
        PlaybackPage.IsVisible = page == ShellPage.Playback;
        PlaybackHistoryPage.IsVisible = page == ShellPage.PlaybackHistory;
        AgentPage.IsVisible = page == ShellPage.Agent;
        PluginsPage.IsVisible = page == ShellPage.Plugins;
        PluginManifestPage.IsVisible = page == ShellPage.PluginPage;
        SettingsPage.IsVisible = page == ShellPage.Settings;
        DownloadNavigationButton.Classes.Set("selected", page == ShellPage.Downloads);
        PlaybackNavigationButton.Classes.Set("selected", page == ShellPage.Playback);
        PlaybackHistoryNavigationButton.Classes.Set("selected", page == ShellPage.PlaybackHistory);
        AgentNavigationButton.Classes.Set("selected", page == ShellPage.Agent);
        PluginsNavigationButton.Classes.Set("selected", page == ShellPage.Plugins);
        SettingsNavigationButton.Classes.Set("selected", page == ShellPage.Settings);
        Avalonia.Automation.AutomationProperties.SetName(
            this,
            page switch
            {
                ShellPage.Downloads => "CanX 下载中心",
                ShellPage.Playback => "CanX 播放器与媒体",
                ShellPage.PlaybackHistory => "CanX 播放历史",
                ShellPage.Agent => "CanX Agent",
                ShellPage.Plugins => "CanX 插件",
                ShellPage.Settings => "CanX 设置",
                ShellPage.PluginPage => $"CanX {ViewModel.PluginPages.SelectedPage?.Title}",
                _ => "CanX",
            });
        if (page == ShellPage.Settings)
        {
            if (_fileAssociationsPanel is not null)
                _ = _fileAssociationsPanel.RefreshAsync();
        }
        if (page == ShellPage.Agent) ViewModel.Agent?.RefreshContextDisplay();
    }

    internal async Task OpenAssociatedFilesAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Distinct(StringComparer.Ordinal).ToArray();
        var media = files.Where(CanXFileFormats.IsMediaFile).ToArray();
        if (media.Length > 0)
            await OpenSourcesInPlaybackWindowAsync(media);

        var metadata = files.Where(CanXFileFormats.IsDownloadMetadataFile).ToArray();
        if (metadata.Length == 0) return;

        foreach (var path in metadata)
            _pendingAssociatedMetadataFiles.Enqueue(path);
        await ShowNextAssociatedMetadataFileAsync();
    }

    private async Task ShowNextAssociatedMetadataFileAsync()
    {
        if (AddDownloadOverlay.IsVisible || _pendingAssociatedMetadataFiles.Count == 0) return;
        ShowPage(ShellPage.Downloads);
        Activate();
        await ShowAddTaskOverlayAsync(_pendingAssociatedMetadataFiles.Dequeue());
    }

    private void OnAddPlaybackSourceClick(object? sender, RoutedEventArgs eventArgs) =>
        ShowAddPlaybackSourceOverlay();

    private void ShowAddPlaybackSourceOverlay()
    {
        if (_addPlaybackSourceOverlayViewModel is not null)
        {
            AddPlaybackSourcePanel.BeginOpen();
            return;
        }

        _addPlaybackSourceOverlayViewModel = new AddPlaybackSourceViewModel(ViewModel.MediaSources);
        AddPlaybackSourcePanel.DataContext = _addPlaybackSourceOverlayViewModel;
        WindowFrame.IsEnabled = false;
        AddPlaybackSourceOverlay.IsVisible = true;
        UpdateWindowChrome();
        AddPlaybackSourcePanel.BeginOpen();
    }

    private void OnAddPlaybackSourceOverlayCloseRequested(object? sender, EventArgs eventArgs)
    {
        AddPlaybackSourceOverlay.IsVisible = false;
        AddPlaybackSourcePanel.DataContext = null;
        _addPlaybackSourceOverlayViewModel = null;
        WindowFrame.IsEnabled = true;
        UpdateWindowChrome();
        AddPlaybackSourceButton.Focus();
    }

    private async void OnPlaybackSourceDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (SelectPlaybackSourceFromEventSource(eventArgs.Source))
        {
            await OpenSelectedPlaybackSourceAsync();
        }
    }

    private void OnPlaybackSourceGridPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(PlaybackSourceGrid).Properties.PointerUpdateKind ==
            PointerUpdateKind.RightButtonPressed)
        {
            SelectPlaybackSourceFromEventSource(eventArgs.Source);
        }
    }

    private bool SelectPlaybackSourceFromEventSource(object? source)
    {
        if (source is Avalonia.Visual visual &&
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is PlaybackSourceItemViewModel item)
        {
            ViewModel.MediaSources.SelectedSource = item;
            return true;
        }

        return false;
    }

    private async void OnOpenPlaybackSourceClick(object? sender, RoutedEventArgs eventArgs) =>
        await OpenSelectedPlaybackSourceAsync();

    private async Task OpenSelectedPlaybackSourceAsync()
    {
        var source = ViewModel.MediaSources.SelectedSource;
        if (source is null)
        {
            return;
        }

        try
        {
            await ViewModel.MediaSources.OpenSelectedSourceAsync();
            PlaybackSourcesView.IsVisible = false;
            MediaSourceBrowserView.IsVisible = true;
            WebDavEntryGrid.Focus();
        }
        catch (Exception exception)
        {
            await new ErrorMessageDialog("无法打开播放源", exception.Message).ShowDialog(this);
        }
    }

    private async void OnRemovePlaybackSourceClick(object? sender, RoutedEventArgs eventArgs)
    {
        await RunMediaSourceActionAsync(() => ViewModel.MediaSources.RemoveSelectedSourceAsync());
    }

    private void OnBackToPlaybackSourcesClick(object? sender, RoutedEventArgs eventArgs) => ShowPlaybackSources();

    private void ShowPlaybackSources()
    {
        MediaSourceBrowserView.IsVisible = false;
        PlaybackSourcesView.IsVisible = true;
        PlaybackSourceGrid.Focus();
    }

    private async void OnWebDavNavigateUpClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunMediaSourceActionAsync(() => ViewModel.MediaSources.NavigateUpAsync());

    private async void OnMediaSourceNavigateBackClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunMediaSourceActionAsync(() => ViewModel.MediaSources.NavigateBackAsync());

    private async void OnMediaSourceNavigateForwardClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunMediaSourceActionAsync(() => ViewModel.MediaSources.NavigateForwardAsync());

    private async void OnOpenMediaSourceFavoriteClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunMediaSourceActionAsync(() => ViewModel.MediaSources.OpenSelectedFavoriteAsync());

    private async void OnToggleMediaSourceFavoriteClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunMediaSourceActionAsync(() => ViewModel.MediaSources.ToggleCurrentFavoriteAsync());

    private async void OnWebDavRefreshClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunMediaSourceActionAsync(() => ViewModel.MediaSources.RefreshAsync());

    private void OnMediaSourceColumnHeaderTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (eventArgs.Source is Avalonia.Visual source &&
            source.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true)?.Content is Grid { Tag: string orderName } &&
            TrySortMediaSourceEntries(orderName))
        {
            eventArgs.Handled = true;
        }
    }

    private void OnMediaSourceSortHeaderKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if ((eventArgs.Key is Key.Enter or Key.Space) &&
            sender is Grid { Tag: string orderName } &&
            TrySortMediaSourceEntries(orderName))
        {
            eventArgs.Handled = true;
        }
    }

    private bool TrySortMediaSourceEntries(string orderName)
    {
        if (!Enum.TryParse<MediaEntrySortOrder>(orderName, out var order) || !Enum.IsDefined(order))
        {
            return false;
        }

        ViewModel.MediaSources.SortEntriesBy(order);
        return true;
    }

    private async void OnWebDavEntryDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (eventArgs.Source is Avalonia.Visual visual &&
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is MediaResourceItemViewModel item)
        {
            ViewModel.MediaSources.SelectedEntry = item;
            await OpenSelectedWebDavEntryAsync();
        }
    }

    private async void OnOpenWebDavEntryClick(object? sender, RoutedEventArgs eventArgs) =>
        await OpenSelectedWebDavEntryAsync();

    private async Task OpenSelectedWebDavEntryAsync()
    {
        var selected = ViewModel.MediaSources.SelectedEntry;
        if (selected is null)
        {
            return;
        }

        if (selected.IsDirectory)
        {
            await RunMediaSourceActionAsync(() => ViewModel.MediaSources.OpenSelectedDirectoryAsync());
            return;
        }

        if (selected.IsImage)
        {
            await RunMediaSourceActionAsync(async () =>
            {
                _imageViewerWindow?.Close();
                var sequence = ViewModel.MediaSources.AcquireImageSequence(selected.Resource);
                var viewModel = new ImageViewerViewModel(sequence);
                try
                {
                    var viewer = new ImageViewerWindow(viewModel);
                    _imageViewerWindow = viewer;
                    viewer.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_imageViewerWindow, viewer)) _imageViewerWindow = null;
                    };
                    await viewer.ShowImageAsync(this);
                }
                catch
                {
                    _imageViewerWindow?.Close();
                    viewModel.Dispose();
                    throw;
                }
            });
            return;
        }

        if (!selected.IsPlayable)
        {
            return;
        }

        await PlayMediaResourceAsync(selected.Resource, ViewModel.MediaSources.SelectedSubtitle);
    }

    private async Task PlayMediaResourceAsync(MediaResource media, RemoteSubtitleChoiceViewModel? subtitleChoice)
    {
        await RunPlaybackActionAsync(async () =>
        {
            var manualSubtitle = subtitleChoice?.Resource;
            var disableSubtitles = subtitleChoice == RemoteSubtitleChoiceViewModel.None;
            var profileId = ViewModel.MediaSources.ActiveProfileId;
            var mediaLease = media.Uri.IsFile ? null : ViewModel.MediaSources.AcquireRead(media);
            DesktopPlaybackSession? session = null;
            try
            {
                session = await PreparePlaybackSessionAsync();
                if (mediaLease is not null) session.ReadLeases.Add(mediaLease);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await session.Window!.WaitForPlaybackSurfaceAsync(timeout.Token);
                if (subtitleChoice is not null && subtitleChoice != RemoteSubtitleChoiceViewModel.Auto)
                    session.ViewModel.SuppressAutomaticSubtitleForNextOpen(disableSubtitles);

                if (media.Uri.IsFile)
                {
                    var videos = ViewModel.MediaSources.GetVideoSiblings(media);
                    if (videos.Count > 1)
                    {
                        var startIndex = Array.FindIndex(videos.ToArray(), resource => resource.Uri == media.Uri);
                        await session.ViewModel.OpenPlaylistAsync(
                            videos.Select(static resource => resource.Uri.LocalPath).ToArray(),
                            startIndex, timeout.Token);
                    }
                    else
                    {
                        await session.ViewModel.OpenAsync(media.Uri.LocalPath, timeout.Token);
                    }
                    if (manualSubtitle?.Uri.IsFile == true)
                        await session.ViewModel.AddSubtitleAsync(manualSubtitle.Uri.LocalPath, cancellationToken: timeout.Token);
                    return;
                }

                var siblings = ViewModel.MediaSources.GetVideoSiblings(media);
                await OpenMediaSourcePlaylistAsync(session, siblings, media, mediaLease!, profileId, timeout.Token);
                if (manualSubtitle is not null)
                    await AddMediaSourceSubtitleAsync(session, mediaLease!, manualSubtitle, select: true, timeout.Token);
            }
            catch
            {
                session?.ReleaseResources();
                mediaLease?.Dispose();
                throw;
            }
        });
    }

    private async Task OpenMediaSourcePlaylistAsync(
        DesktopPlaybackSession session,
        IReadOnlyList<MediaResource> resources,
        MediaResource selected,
        MediaSourceReadLease selectedLease,
        string? profileId,
        CancellationToken cancellationToken)
    {
        var broker = _streamBroker ?? throw new InvalidOperationException("媒体流服务尚未初始化。");
        var playlist = new List<PlaybackMediaSource>(resources.Count);
        var leases = new Dictionary<string, MediaSourceReadLease>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var lease = resource.Uri == selected.Uri ? selectedLease : selectedLease.AcquireSibling(resource);
            if (!ReferenceEquals(lease, selectedLease)) session.ReadLeases.Add(lease);
            var registration = broker.Register(resource.Name, lease.Open);
            session.StreamRegistrations.Add(registration);
            leases.Add(registration.Source, lease);
            playlist.Add(new PlaybackMediaSource(registration.Source, resource.Uri.AbsoluteUri));
        }

        session.ViewModel.ConfigureMediaSourceSubtitleLoader((source, token) =>
            leases.TryGetValue(source, out var lease)
                ? TryAutoAddMediaSourceSubtitleAsync(session, lease, source, token)
                : Task.CompletedTask);
        var startIndex = playlist.FindIndex(source => source.HistorySource == selected.Uri.AbsoluteUri);
        await session.ViewModel.OpenMediaSourcePlaylistAsync(
            playlist, startIndex, profileId, cancellationToken);
    }

    private async Task AddMediaSourceSubtitleAsync(
        DesktopPlaybackSession session,
        MediaSourceReadLease mediaLease,
        MediaResource subtitle,
        bool select,
        CancellationToken cancellationToken,
        bool automatic = false) =>
        await LoadNetworkSubtitleAsync(session, mediaLease.AcquireSibling(subtitle), subtitle, select,
            cancellationToken, automatic);

    private Task AddSelectedNetworkSubtitleAsync(DesktopPlaybackSession session, MediaSourceReadLease lease,
        MediaResource subtitle, string? profileId) =>
        LoadNetworkSubtitleAsync(session, lease, subtitle, select: true, CancellationToken.None,
            persistentSource: new PlaybackExternalSubtitleSource
            {
                Source = subtitle.Uri.AbsoluteUri,
                MediaSourceProfileId = profileId,
            });

    private async Task RestoreRememberedNetworkSubtitleAsync(DesktopPlaybackSession session,
        PlaybackExternalSubtitleSource source, bool select, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.MediaSourceProfileId) ||
            !Uri.TryCreate(source.Source, UriKind.Absolute, out var uri))
            throw new FileNotFoundException("上次使用的网络字幕来源已不可用。", source.Source);

        await using var browser = await ViewModel.MediaSources.OpenSubtitleSourceAsync(
            source.MediaSourceProfileId, uri, cancellationToken);
        var resource = browser.Entries.FirstOrDefault(item =>
            !item.IsDirectory && item.Uri == uri);
        if (resource is null) throw new FileNotFoundException("上次使用的网络字幕已不存在。", source.Source);
        await LoadNetworkSubtitleAsync(session, browser.AcquireRead(resource), resource,
            select, cancellationToken, automatic: true, persistentSource: source);
    }

    private async Task AddOnlineSubtitleAsync(DesktopPlaybackSession session, OnlineSubtitleSource subtitle)
    {
        var playbackSource = session.ViewModel.Source;
        var content = await ViewModel.OnlineSubtitles.DownloadAsync(subtitle);
        if (!string.Equals(playbackSource, session.ViewModel.Source, StringComparison.Ordinal)) return;
        var cache = new RememberedSubtitleCache(Path.Combine(App.GetApplicationDataDirectory(), "subtitles"));
        var name = ExternalSubtitleMatcher.IsSubtitleFile(subtitle.Name)
            ? subtitle.Name : subtitle.Uri.AbsolutePath;
        var path = await cache.StoreAsync(content, name);
        if (string.Equals(playbackSource, session.ViewModel.Source, StringComparison.Ordinal))
            await session.ViewModel.AddSubtitleAsync(path);
    }

    private async Task LoadNetworkSubtitleAsync(
        DesktopPlaybackSession session,
        MediaSourceReadLease subtitleLease,
        MediaResource subtitle,
        bool select,
        CancellationToken cancellationToken,
        bool automatic = false,
        PlaybackExternalSubtitleSource? persistentSource = null)
    {
        MediaSourceReadLease? companionLease = null;
        MediaStreamRegistration? registration = null;
        try
        {
            var broker = _streamBroker ?? throw new InvalidOperationException("媒体流服务尚未初始化。");
            if (Path.GetExtension(subtitle.Name).Equals(".idx", StringComparison.OrdinalIgnoreCase))
            {
                var companion = await subtitleLease.FindSubtitleCompanionAsync(subtitle, cancellationToken)
                    ?? throw new FileNotFoundException("IDX 字幕缺少对应的 SUB 文件。", subtitle.Name);
                companionLease = subtitleLease.AcquireSibling(companion);
                registration = broker.RegisterPair(
                    subtitle.Name, subtitleLease.Open, companion.Name, companionLease.Open);
            }
            else
            {
                registration = broker.Register(subtitle.Name, subtitleLease.Open);
            }

            persistentSource ??= new PlaybackExternalSubtitleSource
            {
                Source = subtitle.Uri.AbsoluteUri,
                MediaSourceProfileId = session.ViewModel.CurrentMediaSourceProfileId,
            };
            if (automatic)
            {
                await session.ViewModel.AddAutomaticSubtitleAsync(registration.Source, select,
                    cancellationToken, persistentSource);
            }
            else
            {
                await session.ViewModel.AddSubtitleAsync(registration.Source, select,
                    cancellationToken, persistentSource);
            }

            session.ReadLeases.Add(subtitleLease);
            if (companionLease is not null) session.ReadLeases.Add(companionLease);
            session.StreamRegistrations.Add(registration);
        }
        catch
        {
            registration?.Dispose();
            companionLease?.Dispose();
            subtitleLease.Dispose();
            throw;
        }
    }

    private async Task TryAutoAddMediaSourceSubtitleAsync(
        DesktopPlaybackSession session,
        MediaSourceReadLease mediaLease,
        string playbackSource,
        CancellationToken cancellationToken)
    {
        var settings = ViewModel.Settings.ActiveMpvSettings;
        if (string.Equals(settings.SubtitleAuto, "no", StringComparison.OrdinalIgnoreCase)) return;
        var revision = session.ViewModel.ManualSubtitleRevision;
        using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var matches = await mediaLease.FindMatchingSubtitlesAsync(
                settings.SubtitleLanguages, settings.SubtitleAuto, discoveryTimeout.Token);
            if (matches.Count == 0 || discoveryTimeout.IsCancellationRequested ||
                !string.Equals(playbackSource, session.ViewModel.Source, StringComparison.Ordinal) ||
                revision != session.ViewModel.ManualSubtitleRevision) return;
            var bySource = matches.ToDictionary(static match => match.Match.Candidate.Source,
                static match => match.Resource, StringComparer.Ordinal);
            session.ViewModel.SetDiscoveredSubtitleChoices(
                playbackSource,
                matches.Skip(1).Select(static match => match.Match.Candidate).ToArray(),
                (candidate, token) => AddMediaSourceSubtitleAsync(session,
                    mediaLease, bySource[candidate.Source], select: true, token));
            var best = matches[0];
            var selectedPreferredInternal = session.ViewModel.Tracks.Any(track =>
                track.Type == PlaybackTrackType.Subtitle &&
                track.IsSelected && !track.IsExternal &&
                ExternalSubtitleMatcher.IsPreferredLanguage(track.Language, settings.SubtitleLanguages));
            await AddMediaSourceSubtitleAsync(session,
                mediaLease, best.Resource, best.Match.SelectOverEmbedded || !selectedPreferredInternal,
                discoveryTimeout.Token, automatic: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            session.ViewModel.ReportSubtitleDiscoveryFailure(exception);
        }
    }

    private async Task RunMediaSourceActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await new ErrorMessageDialog("媒体源操作失败", exception.Message).ShowDialog(this);
        }
    }

    private async void OnBrowseMediaClick(object? sender, RoutedEventArgs eventArgs)
    {
        await RunPlaybackActionAsync(async () =>
        {
            var files = await PickMediaFilesAsync("打开本地文件");
            if (files.Count > 0)
            {
                await OpenSourcesInPlaybackWindowAsync(files.Select(file => file.Path.LocalPath).ToList());
            }
        });
    }

    private async void OnOpenPlaybackAddressClick(object? sender, RoutedEventArgs eventArgs)
    {
        var source = await new OpenPlaybackAddressDialog().ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(source))
        {
            await OpenInPlaybackWindowAsync(source);
        }
    }

    private async void OnAddMediaToPlaylistClick(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await PickMediaFilesAsync("添加到播放列表");
        if (files.Count > 0)
        {
            await RunPlaybackActionAsync(() => ActivePlaybackSession.ViewModel.AddToPlaylistAsync(
                files.Select(file => file.Path.LocalPath).ToList()));
        }
    }

    private Task<IReadOnlyList<IStorageFile>> PickMediaFilesAsync(string title) =>
        OwnedStoragePicker.OpenFilePickerAsync(this, new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = title,
            FileTypeFilter =
            [
                new FilePickerFileType("媒体文件")
                {
                    Patterns = CanXFileFormats.Video
                        .Concat(CanXFileFormats.Audio)
                        .Concat(CanXFileFormats.Playlists)
                        .Select(static extension => "*" + extension)
                        .ToArray(),
                    MimeTypes = ["video/*", "audio/*", "application/vnd.apple.mpegurl"],
                },
            ],
        });

    private async void OnBrowseMediaFolderClick(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await OwnedStoragePicker.OpenFolderPickerAsync(this, new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择媒体文件夹",
        });
        if (folders.Count == 0)
        {
            return;
        }

        await RunPlaybackActionAsync(async () =>
        {
            var session = await PreparePlaybackSessionAsync();
            var playbackWindow = session.Window!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await session.ViewModel.OpenFolderAsync(folders[0].Path.LocalPath, cancellationToken: timeout.Token);
        });
    }

    private async Task OpenInPlaybackWindowAsync(string? source = null)
    {
        await RunPlaybackActionAsync(async () =>
        {
            var session = await PreparePlaybackSessionAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await session.Window!.WaitForPlaybackSurfaceAsync(timeout.Token);
            await session.ViewModel.OpenAsync(source, timeout.Token);
        });
    }

    private async Task OpenSourcesInPlaybackWindowAsync(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        if (sources.Count == 1)
        {
            await OpenInPlaybackWindowAsync(sources[0]);
            return;
        }

        await RunPlaybackActionAsync(async () =>
        {
            var session = await PreparePlaybackSessionAsync();
            var playbackWindow = session.Window!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await session.ViewModel.OpenPlaylistAsync(sources, cancellationToken: timeout.Token);
        });
    }

    private async void OnPlaylistDoubleTapped(object? sender, TappedEventArgs eventArgs) =>
        await PlaySelectedPlaylistItemAsync();

    private async void OnPlayPlaylistItemClick(object? sender, RoutedEventArgs eventArgs) =>
        await PlaySelectedPlaylistItemAsync();

    private async Task PlaySelectedPlaylistItemAsync()
    {
        var playback = ActivePlaybackSession.ViewModel;
        var item = playback.SelectedPlaylistItem;
        if (item is null)
        {
            return;
        }

        var playbackWindow = ShowPlaybackWindow();
        await RunPlaybackActionAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await playback.PlayPlaylistItemAsync(item, timeout.Token);
        });
    }

    private async void OnMovePlaylistItemUpClick(object? sender, RoutedEventArgs eventArgs)
    {
        var item = ActivePlaybackSession.ViewModel.SelectedPlaylistItem;
        if (item is not null && item.Index > 0)
        {
            await RunPlaybackActionAsync(() => ActivePlaybackSession.ViewModel.MovePlaylistItemAsync(item, item.Index - 1));
        }
    }

    private async void OnMovePlaylistItemDownClick(object? sender, RoutedEventArgs eventArgs)
    {
        var item = ActivePlaybackSession.ViewModel.SelectedPlaylistItem;
        if (item is not null && item.Index < ActivePlaybackSession.ViewModel.PlaylistItems.Count - 1)
        {
            await RunPlaybackActionAsync(() => ActivePlaybackSession.ViewModel.MovePlaylistItemAsync(item, item.Index + 1));
        }
    }

    private async void OnRemovePlaylistItemClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ActivePlaybackSession.ViewModel.SelectedPlaylistItem is { } item)
        {
            await RunPlaybackActionAsync(() => ActivePlaybackSession.ViewModel.RemovePlaylistItemAsync(item));
        }
    }

    private async void OnClearPlaylistClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ActivePlaybackSession.ViewModel.ClearPlaylistAsync());

    private void OnPlaybackHistoryGridPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(PlaybackHistoryGrid).Properties.PointerUpdateKind ==
            PointerUpdateKind.RightButtonPressed)
        {
            SelectPlaybackHistoryFromEventSource(eventArgs.Source);
        }
    }

    private async void OnRecentFolderTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (eventArgs.Source is not Avalonia.Visual visual ||
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is not
                PlaybackRecentFolderViewModel folder)
        {
            return;
        }

        eventArgs.Handled = true;
        try
        {
            await ViewModel.MediaSources.OpenRecentFolderAsync(folder);
            PlaybackSourcesView.IsVisible = false;
            MediaSourceBrowserView.IsVisible = true;
            ShowPage(ShellPage.Playback);
            WebDavEntryGrid.Focus();
        }
        catch (Exception exception)
        {
            await new ErrorMessageDialog("无法打开最近访问的文件夹", exception.Message).ShowDialog(this);
        }
    }

    private bool SelectPlaybackHistoryFromEventSource(object? source)
    {
        if (source is Avalonia.Visual visual &&
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is PlaybackRecentItemViewModel item)
        {
            ViewModel.Playback.SelectedRecentItem = item;
            return true;
        }

        return false;
    }

    private async void OnRecentDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (SelectPlaybackHistoryFromEventSource(eventArgs.Source))
        {
            await PlaySelectedRecentItemAsync();
        }
    }

    private async void OnPlayRecentClick(object? sender, RoutedEventArgs eventArgs) =>
        await PlaySelectedRecentItemAsync();

    private async Task PlaySelectedRecentItemAsync()
    {
        var item = ViewModel.Playback.SelectedRecentItem;
        if (item is null)
        {
            return;
        }

        if (item.IsMediaSource)
        {
            await PlayRecentMediaSourceAsync(item);
            return;
        }

        await RunPlaybackActionAsync(async () =>
        {
            var session = await PreparePlaybackSessionAsync();
            var playbackWindow = session.Window!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await session.ViewModel.OpenRecentAsync(item, timeout.Token);
        });
    }

    private async Task PlayRecentMediaSourceAsync(PlaybackRecentItemViewModel item)
    {
        await RunPlaybackActionAsync(async () =>
        {
            var session = await PreparePlaybackSessionAsync();
            session.ReleaseResources();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var mediaUri = new Uri(item.Source, UriKind.Absolute);
                var (resource, lease) = await ViewModel.MediaSources.AcquireHistoryReadAsync(
                    mediaUri,
                    item.MediaSourceProfileId,
                    timeout.Token);
                session.ReadLeases.Add(lease);
                IReadOnlyList<MediaResource> siblings;
                try { siblings = await lease.FindVideoSiblingsAsync(timeout.Token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    siblings = [resource];
                }

                var playbackWindow = session.Window!;
                await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
                await OpenMediaSourcePlaylistAsync(session, siblings, resource, lease,
                    item.MediaSourceProfileId, timeout.Token);
            }
            catch
            {
                session.ReleaseResources();
                throw;
            }
        });
    }

    private async void OnRemoveRecentClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.Playback.SelectedRecentItem is { } item)
        {
            await RunPlaybackActionAsync(() => ViewModel.Playback.RemoveRecentAsync(item));
        }
    }

    private async void OnClearRecentClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.Playback.HasRecentItems &&
            await new ClearPlaybackHistoryDialog().ShowDialog<bool>(this))
        {
            await RunPlaybackActionAsync(() => ViewModel.Playback.ClearRecentAsync());
        }
    }

    private async Task RunPlaybackActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await new ErrorMessageDialog("播放器操作失败", exception.Message).ShowDialog(this);
        }
    }

    internal PlaybackWindow ShowPlaybackWindow() => ShowSessionWindow(ActivePlaybackSession);

    internal string GetAutomationContext() => _currentPage switch
    {
        ShellPage.Downloads => "downloads",
        ShellPage.Playback => "playback",
        ShellPage.PlaybackHistory => "history",
        ShellPage.Agent => "agent",
        ShellPage.Plugins => "plugins",
        ShellPage.PluginPage => "plugin-page",
        ShellPage.Settings => "settings",
        _ => "unknown",
    };

    internal Task NavigateForAutomationAsync(string page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = page.Trim().ToLowerInvariant() switch
        {
            "downloads" => ShellPage.Downloads,
            "playback" => ShellPage.Playback,
            "history" => ShellPage.PlaybackHistory,
            "agent" => ShellPage.Agent,
            "plugins" => ShellPage.Plugins,
            "settings" => ShellPage.Settings,
            _ => throw new ArgumentException("Supported pages are downloads, playback, history, agent, plugins, and settings.", nameof(page)),
        };
        ShowPage(target);
        if (target == ShellPage.Playback) ShowPlaybackSources();
        return Task.CompletedTask;
    }

    internal Task ShowAutomationNotificationAsync(
        string message,
        string severity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = message.Trim();
        if (normalized.Length is 0 or > 500) throw new ArgumentException("Notification message must contain 1 to 500 characters.", nameof(message));
        AutomationNotificationText.Text = normalized;
        AutomationNotificationText.Foreground = severity switch
        {
            "error" => Avalonia.Media.Brushes.IndianRed,
            "warning" => Avalonia.Media.Brushes.DarkOrange,
            "success" => Avalonia.Media.Brushes.SeaGreen,
            "info" => Avalonia.Media.Brushes.SlateGray,
            _ => throw new ArgumentException("Unsupported notification severity.", nameof(severity)),
        };
        AutomationNotification.IsVisible = true;
        var version = ++_automationNotificationVersion;
        DispatcherTimer.RunOnce(() =>
        {
            if (_automationNotificationVersion == version) AutomationNotification.IsVisible = false;
        }, TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    internal async Task PlayConfiguredMediaAsync(
        ConfiguredMediaPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await PreparePlaybackSessionAsync(cancellationToken);
        try
        {
            string source;
            string? subtitle = null;
            if (request.IsLocal)
            {
                source = request.LocalPath!;
                subtitle = request.LocalSubtitlePath;
            }
            else
            {
                var broker = _streamBroker ?? throw new InvalidOperationException("媒体流服务尚未初始化。");
                var mediaLease = request.MediaLease ?? throw new InvalidOperationException("媒体流句柄无效。");
                session.ReadLeases.Add(mediaLease);
                var mediaRegistration = broker.Register(request.Name, mediaLease.Open);
                session.StreamRegistrations.Add(mediaRegistration);
                source = mediaRegistration.Source;
                if (request.SubtitleLease is not null)
                {
                    session.ReadLeases.Add(request.SubtitleLease);
                    var subtitleRegistration = broker.Register(
                        request.SubtitleName ?? "subtitle.srt",
                        request.SubtitleLease.Open);
                    session.StreamRegistrations.Add(subtitleRegistration);
                    subtitle = subtitleRegistration.Source;
                }
            }

            var playbackWindow = session.Window!;
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await playbackWindow.WaitForPlaybackSurfaceAsync(startupTimeout.Token);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                session.ViewModel.SuppressAutomaticSubtitleForNextOpen();
            }
            await session.ViewModel.OpenAsync(source, startupTimeout.Token);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                await session.ViewModel.AddSubtitleAsync(subtitle, cancellationToken: startupTimeout.Token);
            }
        }
        catch
        {
            session.ReleaseResources();
            throw;
        }
    }

    internal async Task PlayResolvedMediaAsync(
        Uri media,
        Uri? subtitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Scheme is not ("http" or "https" or MediaStreamBroker.Scheme) ||
            !string.IsNullOrEmpty(media.UserInfo))
        {
            throw new ArgumentException("Resolved media must use HTTP(S) without embedded credentials.", nameof(media));
        }
        if (subtitle is not null &&
            (subtitle.Scheme is not ("http" or "https" or MediaStreamBroker.Scheme) ||
             !string.IsNullOrEmpty(subtitle.UserInfo)))
        {
            throw new ArgumentException("Resolved subtitles must use HTTP(S) without embedded credentials.", nameof(subtitle));
        }

        var session = await PreparePlaybackSessionAsync(cancellationToken);
        var playbackWindow = session.Window!;
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await playbackWindow.WaitForPlaybackSurfaceAsync(startupTimeout.Token);
        await session.ViewModel.OpenAsync(media.AbsoluteUri, startupTimeout.Token);
        if (subtitle is not null)
        {
            await session.ViewModel.AddSubtitleAsync(subtitle.AbsoluteUri, cancellationToken: startupTimeout.Token);
        }
    }

    private bool SelectTaskFromEventSource(object? source)
    {
        if (source is Avalonia.Visual visual &&
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is DownloadTaskRowViewModel task)
        {
            ViewModel.DownloadCenter.SelectedTask = task;
            return true;
        }

        return false;
    }

    private Task ShowAddTaskOverlayAsync(string? initialSource = null)
    {
        if (_addTaskOverlayViewModel is not null)
        {
            if (!string.IsNullOrWhiteSpace(initialSource))
            {
                _addTaskOverlayViewModel.SetSource(initialSource);
            }

            AddDownloadTaskPanel.BeginOpen();
            return Task.CompletedTask;
        }

        var viewModel = ViewModel.DownloadCenter.CreateAddTaskViewModel();
        if (!string.IsNullOrWhiteSpace(initialSource))
        {
            viewModel.SetSource(initialSource);
        }

        _addTaskOverlayViewModel = viewModel;
        AddDownloadTaskPanel.DataContext = viewModel;
        WindowFrame.IsEnabled = false;
        AddDownloadOverlay.IsVisible = true;
        UpdateWindowChrome();
        AddDownloadTaskPanel.BeginOpen();
        return Task.CompletedTask;
    }

    private async ValueTask ShowPluginDownloadPromptAsync(
        PluginDownloadPromptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            await ShowAddTaskOverlayAsync(request.Source).ConfigureAwait(true);
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ShowAddTaskOverlayAsync(request.Source);
        });
    }

    private async ValueTask ShowPluginMediaAsync(
        PluginMediaPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(request.Source, UriKind.Absolute, out var source) ||
            source.Scheme != MediaStreamBroker.Scheme)
        {
            throw new ArgumentException("Plugin media must use a managed CanX stream.", nameof(request));
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            await PlayResolvedMediaAsync(source, null, cancellationToken).ConfigureAwait(true);
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PlayResolvedMediaAsync(source, null, cancellationToken);
        });
    }

    private void OnAddTaskOverlayCloseRequested(object? sender, EventArgs eventArgs)
    {
        AddDownloadOverlay.IsVisible = false;
        AddDownloadTaskPanel.DataContext = null;
        _addTaskOverlayViewModel = null;
        WindowFrame.IsEnabled = true;
        UpdateWindowChrome();
        AddDownloadTaskButton.Focus();
        if (_pendingAssociatedMetadataFiles.Count > 0)
            Dispatcher.UIThread.Post(async () => await ShowNextAssociatedMetadataFileAsync());
    }

    private async void OnConfigurePluginClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.Plugins?.SelectedPlugin is not { CanConfigure: true } plugin)
        {
            return;
        }
        _configurationPluginId = plugin.Id;
        PluginConfigurationTitle.Text = plugin.ConfigurationTitle ?? "插件配置";
        PluginConfigurationHost.PluginId = plugin.Id;
        WindowFrame.IsEnabled = false;
        PluginConfigurationOverlay.IsVisible = true;
        UpdateWindowChrome();
        await PluginConfigurationHost.NotifyConfigurationOpenedAsync();
    }

    private async void OnClosePluginConfigurationClick(object? sender, RoutedEventArgs eventArgs) =>
        await ClosePluginConfigurationAsync(notifyPlugin: true);

    private void OnPluginConfigurationCloseRequested(
        object? sender,
        PluginConfigurationCloseRequestedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (eventArgs.PluginId == _configurationPluginId)
            {
                _ = ClosePluginConfigurationAsync(notifyPlugin: false);
            }
        });
    }

    private async Task ClosePluginConfigurationAsync(bool notifyPlugin)
    {
        if (!PluginConfigurationOverlay.IsVisible)
        {
            return;
        }
        if (notifyPlugin)
        {
            await PluginConfigurationHost.NotifyConfigurationClosedAsync();
        }
        PluginConfigurationOverlay.IsVisible = false;
        PluginConfigurationHost.PluginId = null;
        _configurationPluginId = null;
        WindowFrame.IsEnabled = true;
        UpdateWindowChrome();
        PluginGrid.Focus();
    }

    private static bool IsSupportedMetadataFile(string path)
    {
        return CanXFileFormats.DownloadMetadata.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSupportedMediaFile(string path) => CanXFileFormats.IsMediaFile(path);

    private async Task ShowTaskOptionsDialogAsync()
    {
        if (ViewModel.DownloadCenter.SelectedTask is null)
        {
            return;
        }

        if (_taskOptionsDialog is not null)
        {
            _taskOptionsDialog.Activate();
            return;
        }

        var dialog = new DownloadTaskOptionsDialog
        {
            DataContext = ViewModel.DownloadCenter,
        };
        _taskOptionsDialog = dialog;
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            _taskOptionsDialog = null;
        }
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
            return;
        }

        BeginMoveDrag(eventArgs);
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => ToggleMaximized();

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => Close();

    private void OnResizeTop(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.North, eventArgs);

    private void OnResizeBottom(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.South, eventArgs);

    private void OnResizeLeft(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.West, eventArgs);

    private void OnResizeRight(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.East, eventArgs);

    private void OnResizeTopLeft(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.NorthWest, eventArgs);

    private void OnResizeTopRight(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.NorthEast, eventArgs);

    private void OnResizeBottomLeft(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.SouthWest, eventArgs);

    private void OnResizeBottomRight(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.SouthEast, eventArgs);

    private void UpdateWindowChrome()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        ResizeLayer.IsVisible = !_useNativeFrame &&
            WindowState == WindowState.Normal &&
            !AddDownloadOverlay.IsVisible &&
            !AddPlaybackSourceOverlay.IsVisible &&
            !PluginConfigurationOverlay.IsVisible;
        CustomTitleBar.IsVisible = !_useNativeFrame;
        WindowFrame.BorderThickness = new Avalonia.Thickness(
            _useNativeFrame || isMaximized ? 0 : 1);
        MaximizeButton.Classes.Set("restore", isMaximized);
        Avalonia.Automation.AutomationProperties.SetName(MaximizeButton, isMaximized ? "还原窗口" : "最大化窗口");
        ToolTip.SetTip(MaximizeButton, isMaximized ? "还原" : "最大化");
    }

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
        {
            BeginResizeDrag(edge, eventArgs);
        }
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private enum ShellPage
    {
        Downloads,
        Playback,
        PlaybackHistory,
        Agent,
        Plugins,
        PluginPage,
        Settings,
    }
}
