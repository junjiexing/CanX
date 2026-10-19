// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CanX.App.Controls;
using CanX.App.ViewModels;
using CanX.Core.Downloads;
using CanX.Core.Playback;
using CanX.MediaAccess;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
using Orientation = Avalonia.Layout.Orientation;
using ProgressBar = Avalonia.Controls.ProgressBar;
using RadioButton = Avalonia.Controls.RadioButton;

namespace CanX.App.Android;

internal sealed partial class MobileShellView : UserControl, IAsyncDisposable
{
    private static readonly Dictionary<string, SolidColorBrush> ThemeBrushes = [];
    private static bool _themeBrushListenerAttached;

    private readonly MobileAppHost _host;
    private readonly Grid _root = new();
    private readonly ContentControl _pageHost = new();
    private readonly Grid _navigation = new();
    private readonly TextBlock _headerTitle = new();
    private readonly Button _headerBack = new();
    private readonly Button _settingsButton = new();
    private Image? _headerLogo;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private Border? _statusBorder;
    private readonly Dictionary<string, (Button Button, Func<Control> Factory)> _pages = [];
    private readonly Dictionary<string, Control> _pageCache = [];
    private MpvVideoSurfaceHost? _videoSurface;
    private Control? _modal;
    private Func<Task>? _modalBackAction;
    private string _currentPage = "downloads";
    private string _settingsReturnPage = "downloads";
    private PluginPageHost? _activePluginPageHost;
    private PluginPageItemViewModel? _activePluginPage;
    private bool _initialized;
    private int _disposed;

    public MobileShellView(MobileAppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _host.AttachView(this);
        DataContext = host.Shell;
        host.Shell.PluginPages.ActivePageRemoved += OnActivePluginPageRemoved;
        BuildShell();
        global::Android.Util.Log.Info("CanX", "Mobile shell visual tree built");
        AttachedToVisualTree += OnAttached;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _host.Shell.PluginPages.ActivePageRemoved -= OnActivePluginPageRemoved;
        CloseMobileItemMenu();
        LeavePluginPage();
        CloseMobileImageViewer();
        _host.DetachView(this);
        await _host.DisposeAsync();
    }

    internal string CurrentPage => _currentPage;

    private void BuildShell()
    {
        _root.Background = Brush("CanXBackgroundBrush");
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        _headerLogo = AppLogo(26);
        _headerLogo.HorizontalAlignment = HorizontalAlignment.Center;
        _headerLogo.VerticalAlignment = VerticalAlignment.Center;
        _headerBack.Content = AppIcon("CanXIconBack", 22);
        _headerBack.IsVisible = false;
        _headerBack.Classes.Add("icon");
        _headerBack.Click += (_, _) => NavigateBack();
        ToolTip.SetTip(_headerBack, "返回");
        Avalonia.Automation.AutomationProperties.SetName(_headerBack, "返回");
        _headerTitle.Text = "CanX";
        _headerTitle.FontSize = 19;
        _headerTitle.FontWeight = FontWeight.SemiBold;
        _headerTitle.VerticalAlignment = VerticalAlignment.Center;
        _headerTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _settingsButton.Content = AppIcon("CanXIconSettings", 21);
        _settingsButton.Classes.Add("icon");
        _settingsButton.Click += (_, _) => Navigate("settings");
        ToolTip.SetTip(_settingsButton, "设置");
        Avalonia.Automation.AutomationProperties.SetName(_settingsButton, "设置");
        var leading = new Grid
        {
            Width = 40,
            Height = 40,
            Children = { _headerLogo, _headerBack },
        };
        var title = new Border
        {
            Padding = new Thickness(7, 4, 10, 4),
            Background = Brush("CanXTitleBarBrush"),
            BorderBrush = Brush("CanXAccentBrush"),
            BorderThickness = new Thickness(0, 0, 0, 3),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    leading,
                    AtColumn(_headerTitle, 1),
                    AtColumn(_settingsButton, 2),
                },
            },
        };
        _headerTitle.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetRow(title, 0);
        _root.Children.Add(title);

        _navigation.ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*");
        _navigation.Background = Brush("CanXPanelBrush");
        RegisterPage(_navigation, "downloads", "下载", "CanXIconDownload", 0, BuildFullDownloadsPage);
        RegisterPage(_navigation, "media", "媒体", "CanXIconPlay", 1, BuildFullMediaPage);
        RegisterPage(_navigation, "history", "历史", "CanXIconHistory", 2, BuildFullHistoryPage);
        RegisterPage(_navigation, "agent", "Agent", "CanXIconAgent", 3, BuildFullAgentPage);
        RegisterPage(_navigation, "plugins", "插件", "CanXIconPlugin", 4, BuildPluginDirectoryPage);
        Grid.SetRow(_navigation, 3);
        _root.Children.Add(_navigation);

        Grid.SetRow(_pageHost, 1);
        _root.Children.Add(_pageHost);
        var statusBorder = new Border
        {
            IsVisible = false,
            Padding = new Thickness(12, 6),
            Background = Brush("CanXPanelBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Brush("CanXBorderBrush"),
            Child = _status,
        };
        Grid.SetRow(statusBorder, 2);
        _root.Children.Add(statusBorder);
        _statusBorder = statusBorder;
        Content = _root;
        _pageHost.Content = new TextBlock
        {
            Text = "正在初始化 CanX…",
            Margin = new Thickness(16),
        };
    }

    private void RegisterPage(
        Grid navigation,
        string id,
        string label,
        string iconResource,
        int column,
        Func<Control> factory)
    {
        var button = new Button
        {
            Content = IconLabel(iconResource, label, vertical: true),
        };
        button.Classes.Add("mobile-nav");
        button.Click += (_, _) => Navigate(id);
        Grid.SetColumn(button, column);
        navigation.Children.Add(button);
        _pages[id] = (button, factory);
    }

    private void Navigate(string id)
    {
        if (!_pages.ContainsKey(id) && id is not ("settings" or "plugin-manager"))
        {
            return;
        }
        CloseMobileItemMenu();
        if (id == "settings" && _currentPage != "settings")
        {
            _settingsReturnPage = _currentPage == "plugin-page" ? "plugins" : _currentPage;
        }
        LeavePluginPage();
        _currentPage = id;
        if (!_pageCache.TryGetValue(id, out var page))
        {
            page = id switch
            {
                "settings" => BuildFullSettingsPage(),
                "plugin-manager" => BuildFullPluginsPage(),
                _ => _pages[id].Factory(),
            };
            _pageCache.Add(id, page);
        }
        _pageHost.Content = page;
        UpdateNavigationChrome();
    }

    private void NavigateBack()
    {
        switch (_currentPage)
        {
            case "plugin-page":
            case "plugin-manager":
                Navigate("plugins");
                break;
            case "settings":
                Navigate(_settingsReturnPage);
                break;
        }
    }

    internal bool HandleBackPressed()
    {
        if (_activeItemMenu?.IsOpen == true)
        {
            CloseMobileItemMenu();
            return true;
        }
        if (_mobileImageViewer is not null)
        {
            CloseMobileImageViewer();
            return true;
        }
        if (_modal is not null)
        {
            if (_modalBackAction is { } action) _ = RunAsync(action());
            else CloseModal();
            return true;
        }
        if (_currentPage is "plugin-page" or "plugin-manager" or "settings")
        {
            NavigateBack();
            return true;
        }
        return false;
    }

    private void UpdateNavigationChrome()
    {
        var isRoot = _pages.ContainsKey(_currentPage);
        _navigation.IsVisible = isRoot;
        _headerBack.IsVisible = !isRoot;
        if (_headerLogo is not null) _headerLogo.IsVisible = isRoot;
        _settingsButton.IsVisible = _currentPage is not ("settings" or "plugin-page");
        _headerTitle.Text = _currentPage switch
        {
            "plugin-page" => _activePluginPage?.Title ?? "插件页面",
            "plugin-manager" => "插件管理",
            "settings" => "设置",
            _ => "CanX",
        };
        foreach (var (id, entry) in _pages)
        {
            entry.Button.Classes.Set("selected", id == _currentPage);
        }
    }

    private Control BuildDownloadsPage()
    {
        var viewModel = _host.Shell.DownloadCenter;
        var list = new ListBox
        {
            ItemsSource = viewModel.Tasks,
            ItemTemplate = MobileTemplate<DownloadTaskRowViewModel>(DownloadRow),
        };
        list.SelectionChanged += (_, _) => viewModel.SelectedTask = list.SelectedItem as DownloadTaskRowViewModel;

        var add = AccentButton("添加任务");
        add.Click += (_, _) => ShowAddDownload();
        var pause = new Button { Content = "暂停" };
        pause.Click += (_, _) => viewModel.PauseCommand.Execute(null);
        var resume = new Button { Content = "继续" };
        resume.Click += (_, _) => viewModel.ResumeCommand.Execute(null);
        var remove = new Button { Content = "删除" };
        remove.Classes.Add("danger");
        remove.Click += async (_, _) =>
        {
            try { await viewModel.RemoveSelectedAsync(deleteFiles: false); }
            catch (Exception exception) { ShowError(exception); }
        };
        var filter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var downloading = new RadioButton { Content = "下载中", IsChecked = viewModel.ShowDownloadingTasks };
        var completed = new RadioButton { Content = "已完成", IsChecked = viewModel.ShowCompletedTasks };
        downloading.Click += (_, _) => viewModel.ShowDownloadingTasks = true;
        completed.Click += (_, _) => viewModel.ShowCompletedTasks = true;
        filter.Children.Add(downloading);
        filter.Children.Add(completed);

        var root = Page("下载", "libaria2 下载中心", add, pause, resume, remove, filter, list);
        return root;
    }

    private Control BuildMediaPage()
    {
        var media = _host.Shell.MediaSources;
        var playback = _host.Shell.Playback;
        var video = new MpvVideoSurfaceHost
        {
            Height = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _videoSurface = video;
        video.Attach(_host.MpvEngine);
        video.InitializationFailed += message => SetShellStatus($"视频渲染失败：{message}");

        var openFile = AccentButton("打开本地视频");
        openFile.Click += async (_, _) => await PickAndPlayAsync();
        var playPause = new Button { Content = "播放 / 暂停" };
        playPause.Click += async (_, _) => await RunAsync(playback.TogglePauseAsync());
        var stop = new Button { Content = "停止" };
        stop.Click += async (_, _) => await RunAsync(_host.StopPlaybackAsync());

        var seek = new Slider { Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        void RefreshSeek(object? _, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName is nameof(PlaybackViewModel.PositionSeconds) or nameof(PlaybackViewModel.DurationSeconds) or null)
            {
                seek.Maximum = playback.DurationMaximum;
                seek.Value = playback.PositionSeconds;
            }
        }
        playback.PropertyChanged += RefreshSeek;
        seek.PointerReleased += async (_, _) => await RunAsync(playback.SeekAsync(seek.Value, exact: false));

        var sources = new ListBox
        {
            ItemsSource = media.Sources,
            MaxHeight = 180,
            ItemTemplate = MobileTemplate<PlaybackSourceItemViewModel>(item =>
                new StackPanel
                {
                    Margin = new Thickness(2, 5),
                    Children =
                    {
                        new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"{item.Type} · {item.Location}", TextWrapping = TextWrapping.Wrap },
                    },
                }),
        };
        sources.SelectionChanged += (_, _) => media.SelectedSource = sources.SelectedItem as PlaybackSourceItemViewModel;
        var addSource = new Button { Content = "添加播放源" };
        addSource.Click += (_, _) => ShowAddMediaSource();
        var openSource = new Button { Content = "打开播放源" };
        openSource.Click += async (_, _) =>
        {
            try { await media.OpenSelectedSourceAsync(); Navigate("media"); }
            catch (Exception exception) { ShowError(exception); }
        };

        var entries = new ListBox
        {
            ItemsSource = media.Entries,
            MaxHeight = 260,
            ItemTemplate = MobileTemplate<MediaResourceItemViewModel>(item =>
                new TextBlock { Text = $"{item.Type}  {item.Name}  {item.Size}", Margin = new Thickness(2, 6) }),
        };
        entries.SelectionChanged += (_, _) => media.SelectedEntry = entries.SelectedItem as MediaResourceItemViewModel;
        var subtitles = new ComboBox
        {
            ItemsSource = media.SubtitleChoices,
            SelectedItem = media.SelectedSubtitle,
            PlaceholderText = "自动匹配字幕",
        };
        subtitles.SelectionChanged += (_, _) =>
            media.SelectedSubtitle = subtitles.SelectedItem as RemoteSubtitleChoiceViewModel;
        var openEntry = AccentButton("打开所选项目");
        openEntry.Click += async (_, _) => await OpenMediaEntryAsync();

        return Page(
            "播放器与媒体源",
            "本地文件、SMB、FTP / FTPS、WebDAV",
            video,
            Horizontal(openFile, playPause, stop),
            seek,
            Heading("播放源"),
            Horizontal(addSource, openSource),
            sources,
            Heading("文件"),
            Labeled("字幕", subtitles),
            openEntry,
            entries);
    }

    private Control BuildPluginsPage()
    {
        var plugins = _host.Shell.Plugins;
        if (plugins is null)
        {
            return Page("插件", "插件运行时未启用");
        }
        var list = new ListBox
        {
            ItemsSource = plugins.Plugins,
            ItemTemplate = MobileTemplate<PluginItemViewModel>(item =>
                new StackPanel
                {
                    Margin = new Thickness(2, 6),
                    Children =
                    {
                        new TextBlock { Text = $"{item.Name}  {item.Version}", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = item.Status, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = item.PermissionSummary, TextWrapping = TextWrapping.Wrap },
                    },
                }),
        };
        list.SelectionChanged += (_, _) => plugins.SelectedPlugin = list.SelectedItem as PluginItemViewModel;
        var install = AccentButton("安装插件包");
        install.Click += async (_, _) => await PickAndInstallPluginAsync();
        var enable = new Button { Content = "启用" };
        enable.Click += async (_, _) => await RunAsync(plugins.EnableSelectedAsync());
        var disable = new Button { Content = "停用" };
        disable.Click += async (_, _) => await RunAsync(plugins.DisableSelectedAsync());
        var reload = new Button { Content = "重载" };
        reload.Click += async (_, _) => await RunAsync(plugins.ReloadSelectedAsync());
        var uninstall = new Button { Content = "卸载" };
        uninstall.Classes.Add("danger");
        uninstall.Click += async (_, _) => await RunAsync(plugins.UninstallSelectedAsync());
        return Page(
            "插件",
            "QuickJS 沙箱、权限、声明式 UI 与服务",
            Horizontal(install, enable, disable, reload, uninstall),
            list,
            new PluginContributionHost { Registry = plugins.UiRegistry, Slot = "plugins.footer" });
    }

    private Control BuildAgentPage()
    {
        var agent = _host.Shell.Agent;
        if (agent is null)
        {
            return Page("Agent", "Agent 运行时未启用");
        }
        var messages = new ListBox
        {
            ItemsSource = agent.VisibleMessages,
            ItemTemplate = MobileTemplate<AgentChatMessageViewModel>(item =>
                new StackPanel
                {
                    Margin = new Thickness(2, 8),
                    Children =
                    {
                        new TextBlock { Text = item.Role, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = item.Content, TextWrapping = TextWrapping.Wrap },
                    },
                }),
        };
        var input = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 86,
            PlaceholderText = "告诉 CanX 你想下载、播放、搜索或管理什么…",
            Text = agent.InputText,
        };
        input.TextChanged += (_, _) => agent.InputText = input.Text ?? string.Empty;
        var send = AccentButton("发送");
        send.Click += (_, _) => agent.SendCommand.Execute(null);
        var stop = new Button { Content = "停止" };
        stop.Click += (_, _) => agent.StopCommand.Execute(null);
        var approve = new Button { Content = "批准当前操作" };
        approve.Click += (_, _) => agent.ApproveCommand.Execute(null);
        var deny = new Button { Content = "拒绝" };
        deny.Click += (_, _) => agent.DenyCommand.Execute(null);
        return Page(
            "Agent",
            $"当前已注册 {_host.Tools.Descriptors.Count} 个 Tool",
            messages,
            input,
            Horizontal(send, stop, approve, deny));
    }

    private Control BuildSettingsPage()
    {
        var settings = _host.Shell.Settings;
        var theme = new ComboBox { ItemsSource = settings.ThemeOptions, SelectedItem = settings.SelectedTheme };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is ThemeSettingOption option) settings.SelectedTheme = option;
        };
        var logging = new CheckBox { Content = "启用日志记录", IsChecked = settings.IsLoggingEnabled };
        logging.Click += (_, _) => settings.IsLoggingEnabled = logging.IsChecked == true;
        var directory = new TextBox { Text = settings.DownloadDirectory, PlaceholderText = "下载目录" };
        directory.LostFocus += (_, _) => settings.DownloadDirectory = directory.Text ?? string.Empty;
        var apiBase = new TextBox { Text = settings.Agent.BaseUrl, PlaceholderText = "OpenAI 兼容 API Base URL" };
        apiBase.LostFocus += (_, _) => settings.Agent.BaseUrl = apiBase.Text ?? string.Empty;
        var model = new TextBox { Text = settings.Agent.Model, PlaceholderText = "模型" };
        model.LostFocus += (_, _) => settings.Agent.Model = model.Text ?? string.Empty;
        var apiKey = new TextBox { PasswordChar = '●', PlaceholderText = "API Key（保存到 Android 安全存储）" };
        apiKey.LostFocus += (_, _) => settings.Agent.ApiKeyInput = apiKey.Text ?? string.Empty;
        var save = AccentButton("保存设置");
        save.Click += (_, _) => settings.SaveCommand.Execute(null);
        return Page(
            "设置",
            "通用",
            Labeled("主题", theme),
            logging,
            Labeled("下载目录", directory),
            Heading("播放器（mpv）"),
            new TextBlock { Text = "硬件解码、缓存、同步、缩放、字幕和高级 mpv.conf 均使用与桌面端相同的设置模型。", TextWrapping = TextWrapping.Wrap },
            Heading("Agent"),
            Labeled("API 地址", apiBase),
            Labeled("模型", model),
            Labeled("API Key", apiKey),
            save);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        // Do not mutate UI-bound collections from an async continuation while
        // Avalonia is still completing the Android visual-tree attachment. The
        // view can render successfully in that state but its native input event
        // never gets acknowledged afterwards, which Android reports as an ANR.
        Dispatcher.UIThread.Post(InitializeAfterAttachAsync, DispatcherPriority.Background);
    }

    private async void InitializeAfterAttachAsync()
    {
        global::Android.Util.Log.Info("CanX", "Mobile initialization begin");
        SetShellStatus("正在初始化 CanX…");
        try
        {
            var errors = await _host.InitializeAsync();
            SetShellStatus(errors.Count == 0
                ? string.Empty
                : $"部分模块未就绪：{string.Join("；", errors)}");
            global::Android.Util.Log.Info("CanX", $"Mobile initialization complete, errors={errors.Count}");
        }
        catch (Exception exception)
        {
            SetShellStatus($"初始化失败：{exception.Message}");
            global::Android.Util.Log.Error("CanX", $"Mobile initialization failed: {exception}");
        }
        finally
        {
            // Construct the task list after initialization so its first population
            // is part of initial binding, rather than a live collection mutation.
            Navigate("downloads");
        }
    }

    private void ShowAddDownload()
    {
        var viewModel = _host.Shell.DownloadCenter.CreateAddTaskViewModel();
        string? temporarySource = null;
        var source = new TextBox
        {
            PlaceholderText = "URL、magnet 链接或 torrent / metalink 文件",
            Text = viewModel.SourceText,
        };
        source.TextChanged += (_, _) => viewModel.SourceText = source.Text ?? string.Empty;
        var destination = new TextBox { Text = viewModel.DestinationDirectory };
        destination.TextChanged += (_, _) => viewModel.DestinationDirectory = destination.Text ?? string.Empty;
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var action = AccentButton("下一步");
        action.Click += async (_, _) =>
        {
            try
            {
                if (viewModel.IsSourceStep)
                {
                    await viewModel.PrepareAsync();
                    details.Text = $"{viewModel.DisplayName}\n{viewModel.SourceType} · {viewModel.TotalSize}\n文件数：{viewModel.Files.Count}";
                    action.Content = "添加";
                    return;
                }
                if (await viewModel.AddAsync())
                {
                    await viewModel.DisposeAsync();
                    DeleteTemporaryFile(temporarySource);
                    CloseModal();
                }
            }
            catch (Exception exception)
            {
                details.Text = exception.Message;
            }
        };
        var browse = new Button { Content = "浏览" };
        browse.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "选择 Torrent 或 Metalink 文件",
                FileTypeFilter =
                [
                    new FilePickerFileType("下载描述文件") { Patterns = ["*.torrent", "*.metalink", "*.meta4"] },
                ],
            });
            var file = files.FirstOrDefault();
            if (file is null) return;
            DeleteTemporaryFile(temporarySource);
            temporarySource = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(temporarySource))
            {
                temporarySource = await MaterializeStorageFileAsync(file, "download-imports");
            }
            source.Text = temporarySource;
            viewModel.SourceText = temporarySource;
        };
        ShowModal(
            "添加下载任务",
            source,
            browse,
            Labeled("保存到", destination),
            details,
            Horizontal(action, CancelButton(async () =>
            {
                await viewModel.DisposeAsync();
                DeleteTemporaryFile(temporarySource);
            })));
    }

    private void ShowAddMediaSource()
    {
        var viewModel = new AddPlaybackSourceViewModel(_host.Shell.MediaSources);
        var type = new ComboBox { ItemsSource = viewModel.SourceTypes, SelectedItem = viewModel.SelectedType };
        type.SelectionChanged += (_, _) =>
        {
            if (type.SelectedItem is PlaybackSourceTypeOption option) viewModel.SelectedType = option;
        };
        var name = new TextBox { PlaceholderText = "显示名称" };
        name.TextChanged += (_, _) => viewModel.Name = name.Text ?? string.Empty;
        var endpoint = new TextBox { PlaceholderText = "路径或 smb:// / ftp:// / https:// 地址" };
        endpoint.TextChanged += (_, _) => viewModel.EndpointText = endpoint.Text ?? string.Empty;
        var user = new TextBox { PlaceholderText = "用户名（可选）" };
        user.TextChanged += (_, _) => viewModel.UserName = user.Text ?? string.Empty;
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "密码（可选）" };
        password.TextChanged += (_, _) => viewModel.Password = password.Text ?? string.Empty;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var add = AccentButton("添加");
        add.Click += async (_, _) =>
        {
            try { await viewModel.AddAsync(); CloseModal(); Navigate("media"); }
            catch (Exception exception) { error.Text = exception.Message; }
        };
        ShowModal("添加播放源", type, name, endpoint, user, password, error, Horizontal(add, CancelButton()));
    }

    private async Task PickAndPlayAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "打开视频",
                FileTypeFilter =
                [
                    new FilePickerFileType("视频文件") { Patterns = ["*.mkv", "*.mp4", "*.webm", "*.avi", "*.mov", "*.m4v", "*.ts", "*.m2ts"] },
                    FilePickerFileTypes.All,
                ],
            });
            var file = files.FirstOrDefault();
            if (file is null) return;
            var source = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(source))
            {
                file.Dispose();
                await _host.PlayLocalMediaAsync(source);
            }
            else
            {
                await _host.PlayStorageFileAsync(file);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task PickAndInstallPluginAsync()
    {
        try
        {
            var plugins = _host.Shell.Plugins;
            var top = TopLevel.GetTopLevel(this);
            if (plugins is null || top?.StorageProvider is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "安装 CanX 插件",
                FileTypeFilter = [new FilePickerFileType("CanX 插件包") { Patterns = ["*.canxplugin", "*.zip"] }],
            });
            var file = files.FirstOrDefault();
            if (file is null) return;
            var path = file.TryGetLocalPath();
            var temporary = string.IsNullOrWhiteSpace(path);
            if (temporary) path = await MaterializeStorageFileAsync(file, "plugin-imports");
            else file.Dispose();
            try
            {
                await plugins.InstallAsync(path!);
            }
            finally
            {
                if (temporary) DeleteTemporaryFile(path);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task OpenMediaEntryAsync()
    {
        var media = _host.Shell.MediaSources;
        try
        {
            if (media.SelectedEntry is null) return;
            if (media.SelectedEntry.IsDirectory)
            {
                await media.OpenSelectedDirectoryAsync();
                return;
            }

            if (media.SelectedEntry.IsImage)
            {
                await ShowMobileImageViewerAsync(media.SelectedEntry.Resource);
                return;
            }

            if (!media.SelectedEntry.IsPlayable)
            {
                return;
            }

            if (media.SelectedEntry.Resource.Uri.IsFile)
            {
                ShowMobilePlayerView();
                var subtitleChoice = media.SelectedSubtitle;
                var subtitle = subtitleChoice?.Resource;
                if (subtitleChoice is not null && subtitleChoice != RemoteSubtitleChoiceViewModel.Auto)
                {
                    _host.Shell.Playback.SuppressAutomaticSubtitleForNextOpen(
                        disableAllSubtitles: subtitleChoice == RemoteSubtitleChoiceViewModel.None);
                }
                await _host.PlayLocalMediaAsync(
                    media.SelectedEntry.Resource.Uri.LocalPath,
                    subtitle?.Uri.IsFile == true ? subtitle.Uri.LocalPath : null);
                return;
            }

            await _host.PlayRemoteMediaAsync(
                media.SelectedEntry.Resource,
                media.SelectedSubtitle);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    internal void NavigateForAutomation(string page)
    {
        var target = page.Trim().ToLowerInvariant() switch
        {
            "downloads" => "downloads",
            "playback" or "media" => "media",
            "history" => "history",
            "plugins" => "plugins",
            "agent" => "agent",
            "settings" => "settings",
            _ => throw new ArgumentException(
                "Supported pages are downloads, playback, history, agent, plugins, and settings.",
                nameof(page)),
        };
        Navigate(target);
    }

    internal void ShowAutomationNotification(string message, string severity)
    {
        var normalized = message.Trim();
        if (normalized.Length is 0 or > 500)
        {
            throw new ArgumentException(
                "Notification message must contain 1 to 500 characters.",
                nameof(message));
        }
        SetShellStatus(normalized);
        _status.Foreground = severity switch
        {
            "error" => Brushes.IndianRed,
            "warning" => Brushes.DarkOrange,
            "success" => Brushes.SeaGreen,
            "info" => Brush("CanXTextBrush"),
            _ => throw new ArgumentException("Unsupported notification severity.", nameof(severity)),
        };
    }

    internal async Task WaitForPlaybackSurfaceAsync(
        CancellationToken cancellationToken,
        bool navigateToMedia = true)
    {
        if (navigateToMedia)
        {
            Navigate("media");
        }
        ShowMobilePlayerView();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (_videoSurface is null)
        {
            await Task.Delay(20, timeout.Token);
        }

        var surface = _videoSurface
            ?? throw new InvalidOperationException("Android playback surface is unavailable.");
        if (surface.HasRenderContext) return;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(int _) => ready.TrySetResult();
        void OnFailed(string message) => ready.TrySetException(
            new InvalidOperationException($"视频渲染失败：{message}"));
        surface.RenderContextReady += OnReady;
        surface.InitializationFailed += OnFailed;
        try
        {
            if (surface.HasRenderContext) return;
            await ready.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            surface.RenderContextReady -= OnReady;
            surface.InitializationFailed -= OnFailed;
        }
    }

    private void ShowModal(string title, params Control[] controls)
    {
        CloseMobileItemMenu();
        CloseModal();
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Heading(title));
        foreach (var control in controls) stack.Children.Add(control);
        var panel = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(18),
            Background = Brush("CanXPanelBrush"),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { Content = stack, MaxHeight = 620 },
        };
        _modal = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            Child = panel,
        };
        Grid.SetRowSpan(_modal, 4);
        _root.Children.Add(_modal);
    }

    private static async Task<string> MaterializeStorageFileAsync(IStorageFile file, string category)
    {
        ArgumentNullException.ThrowIfNull(file);
        var cache = global::Android.App.Application.Context.CacheDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android cache directory is unavailable.");
        var directory = Path.Combine(cache, "canx", category);
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(file.Name);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
        try
        {
            await using var input = await file.OpenReadAsync();
            await using var output = File.Create(path);
            await input.CopyToAsync(output);
            return path;
        }
        catch
        {
            DeleteTemporaryFile(path);
            throw;
        }
        finally
        {
            file.Dispose();
        }
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var cache = global::Android.App.Application.Context.CacheDir?.AbsolutePath;
            if (cache is not null && Path.GetFullPath(path).StartsWith(
                    Path.GetFullPath(cache) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Android may still hold an imported file briefly; the OS owns cache cleanup.
        }
    }

    private Button CancelButton(Func<Task>? beforeClose = null)
    {
        var button = new Button { Content = "取消" };
        button.Click += async (_, _) =>
        {
            if (beforeClose is not null) await beforeClose();
            CloseModal();
        };
        return button;
    }

    private void CloseModal()
    {
        if (_modal is null) return;
        _subtitlePickerCancellation?.Cancel();
        _subtitlePickerCancellation?.Dispose();
        _subtitlePickerCancellation = null;
        if (_subtitlePickerBrowser is { } browser)
        {
            _subtitlePickerBrowser = null;
            _ = browser.DisposeAsync();
        }
        _root.Children.Remove(_modal);
        _modal = null;
        _modalBackAction = null;
    }

    private async Task RunAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowError(Exception exception)
    {
        SetShellStatus(exception.Message);
        SetMobilePlaybackFeedback(exception.Message);
    }

    private void SetShellStatus(string message)
    {
        _status.Text = message;
        if (_statusBorder is not null)
        {
            _statusBorder.IsVisible = !string.IsNullOrWhiteSpace(message);
        }
    }

    private static ScrollViewer Page(string title, string subtitle, params Control[] controls)
    {
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("5,12,*"),
            Margin = new Thickness(0, 2, 0, 2),
            Children =
            {
                new Border { Background = Brush("CanXAccentBrush") },
                AtColumn(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeight.SemiBold }, 2),
            },
        };
        if (!string.IsNullOrWhiteSpace(subtitle)) ToolTip.SetTip(heading, subtitle);
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(14, 10, 14, 18) };
        if (!string.IsNullOrWhiteSpace(title)) stack.Children.Add(heading);
        foreach (var control in controls) stack.Children.Add(control);
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };
    }

    private static StackPanel Horizontal(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static StackPanel Labeled(string label, Control control) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 12, Foreground = Brush("CanXMutedTextBrush") }, control },
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 0),
    };

    private static Button AccentButton(string text, string? iconResource = null)
    {
        var button = new Button
        {
            Content = iconResource is null ? text : IconLabel(iconResource, text),
        };
        button.Classes.Add("accent");
        return button;
    }

    private static Button ActionButton(string text, string iconResource)
    {
        var button = new Button { Content = IconLabel(iconResource, text) };
        return button;
    }

    private static PathIcon AppIcon(string resourceKey, double size = 20, IBrush? foreground = null)
    {
        var icon = new PathIcon
        {
            Width = size,
            Height = size,
            Data = Avalonia.Application.Current?.TryFindResource(resourceKey, out var value) == true
                ? value as Geometry
                : null,
        };
        if (foreground is not null)
        {
            icon.Foreground = foreground;
        }
        return icon;
    }

    private static Image AppLogo(double size)
    {
        using var stream = AssetLoader.Open(new Uri("avares://CanX.App.Android/Assets/CanXLogo.png"));
        return new Image
        {
            Width = size,
            Height = size,
            Source = new Bitmap(stream),
            Stretch = Stretch.Uniform,
        };
    }

    private static StackPanel IconLabel(string iconResource, string text, bool vertical = false) => new()
    {
        Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = vertical ? 2 : 7,
        Children =
        {
            AppIcon(iconResource, vertical ? 21 : 17),
            new TextBlock
            {
                Text = text,
                FontSize = vertical ? 11 : 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        },
    };

    private static FuncDataTemplate<T> MobileTemplate<T>(Func<T, Control> build)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(build);
        return new FuncDataTemplate<T>((item, _) =>
            item is null ? new Border { MinHeight = 1 } : build(item));
    }

    private static Control DownloadRow(DownloadTaskRowViewModel? item)
    {
        // Avalonia's Android ListBox virtualization can invoke a typed data
        // template with a transient null item while recycling its container.
        // Rendering an empty placeholder keeps that framework transition from
        // escaping as an unhandled exception on Android's main looper.
        if (item is null)
        {
            return new Border { MinHeight = 1 };
        }

        var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5 };
        var progressText = new TextBlock();
        void Refresh()
        {
            name.Text = item.Name;
            state.Text = item.State;
            progress.Value = item.Progress;
            progressText.Text = item.ProgressText;
        }
        PropertyChangedEventHandler changed = (_, _) => Refresh();
        var subscribed = false;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(2, 6),
        };
        var details = new StackPanel { Spacing = 3, Children = { name, state, progress } };
        row.Children.Add(details);
        row.Children.Add(AtColumn(progressText, 1));
        row.AttachedToVisualTree += (_, _) =>
        {
            if (subscribed) return;
            item.PropertyChanged += changed;
            subscribed = true;
        };
        row.DetachedFromVisualTree += (_, _) =>
        {
            if (!subscribed) return;
            item.PropertyChanged -= changed;
            subscribed = false;
        };
        Refresh();
        return row;
    }

    private static T AtColumn<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static IBrush Brush(string key)
    {
        var application = Avalonia.Application.Current;
        if (!ThemeBrushes.TryGetValue(key, out var result))
        {
            result = new SolidColorBrush(ResolveThemeColor(application, key));
            ThemeBrushes.Add(key, result);
        }

        if (!_themeBrushListenerAttached && application is not null)
        {
            application.ActualThemeVariantChanged += (_, _) => RefreshThemeBrushes(application);
            _themeBrushListenerAttached = true;
        }

        return result;
    }

    private static void RefreshThemeBrushes(Avalonia.Application application)
    {
        foreach (var (key, brush) in ThemeBrushes)
        {
            brush.Color = ResolveThemeColor(application, key);
        }
    }

    private static Color ResolveThemeColor(Avalonia.Application? application, string key) =>
        application?.TryGetResource(key, application.ActualThemeVariant, out var value) == true &&
        value is ISolidColorBrush brush
            ? brush.Color
            : Colors.Transparent;
}
