// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CanX.App;
using CanX.App.Controls;
using CanX.App.ViewModels;
using CanX.Application.Discovery;
using CanX.Core.Playback;
using CanX.MediaAccess;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
using Orientation = Avalonia.Layout.Orientation;
using RadioButton = Avalonia.Controls.RadioButton;

namespace CanX.App.Android;

internal sealed partial class MobileShellView
{
    private const double MobilePlayerDoubleTapDistance = 48;
    private static readonly TimeSpan MobilePlayerDoubleTapDelay = TimeSpan.FromMilliseconds(300);

    private ContentControl? _mobileMediaHost;
    private Control? _mobileSourceListView;
    private Control? _mobileSourceBrowserView;
    private Control? _mobilePlayerView;
    private ImageViewerView? _mobileImageViewer;
    private ImageViewerViewModel? _mobileImageViewModel;
    private Border? _mobilePlayerChrome;
    private Border? _mobilePlayerOptions;
    private TextBlock? _mobilePlayerFeedback;
    private SubtitleSourceBrowser? _subtitlePickerBrowser;
    private CancellationTokenSource? _subtitlePickerCancellation;
    private CancellationTokenSource? _mobilePlayerChromeHide;
    private DispatcherTimer? _mobilePlayerTapTimer;
    private Point? _mobilePlayerFirstTapPosition;
    private bool _mobilePlayerSecondTapInProgress;
    private IPointer? _mobileGesturePointer;
    private Point? _mobileGestureStart;
    private bool _mobileGestureHadMultiplePointers;
    private double _mobileGestureStartPosition;
    private double _mobileGestureStartVolume;
    private double _mobileGestureStartBrightness;
    private PlaybackGestureKind _mobileGestureKind;
    private PlaybackGestureKind _mobileGesturePendingAdjustmentKind;
    private double? _mobileGesturePendingAdjustment;
    private Task _mobileGestureAdjustmentTask = Task.CompletedTask;

    private Control BuildFullMediaPage()
    {
        _mobileSourceListView = BuildMobileSourceListView();
        _mobileMediaHost = new ContentControl { Content = _mobileSourceListView };
        return _mobileMediaHost;
    }

    private Control BuildFullHistoryPage()
    {
        var playback = _host.Shell.Playback;
        var empty = new Border
        {
            Padding = new Thickness(18, 36),
            Background = Brush("CanXPanelBrush"),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "暂无播放历史",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        var list = new ListBox
        {
            MaxHeight = 520,
            ItemTemplate = MobileTemplate<PlaybackRecentItemViewModel>(BuildMobileRecentRow),
        };
        AutomationProperties.SetName(list, "播放历史列表");
        var updatingSelection = false;
        list.SelectionChanged += (_, _) =>
        {
            if (!updatingSelection)
            {
                playback.SelectedRecentItem = list.SelectedItem as PlaybackRecentItemViewModel;
            }
        };
        AttachMobileListActions<PlaybackRecentItemViewModel>(
            list,
            item => playback.SelectedRecentItem = item,
            item => _host.PlayRecentAsync(item),
            (item, anchor) => ShowMobileItemMenu(anchor,
                new MobileItemAction("播放", () => _host.PlayRecentAsync(item)),
                new MobileItemAction("移除记录", () => playback.RemoveRecentAsync(item), IsDanger: true)));
        var folders = new ListBox
        {
            MaxHeight = 520,
            ItemTemplate = MobileTemplate<PlaybackRecentFolderViewModel>(folder => new Border
            {
                Padding = new Thickness(10, 9),
                BorderBrush = Brush("CanXBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 10,
                    Children =
                    {
                        AppIcon("CanXIconFolder", 20),
                        AtColumn(new StackPanel
                        {
                            Spacing = 3,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = folder.Name,
                                    FontSize = 16,
                                    FontWeight = FontWeight.SemiBold,
                                    TextTrimming = TextTrimming.CharacterEllipsis,
                                },
                                new TextBlock
                                {
                                    Text = folder.Location,
                                    FontSize = 12,
                                    Foreground = Brush("CanXMutedTextBrush"),
                                    TextTrimming = TextTrimming.CharacterEllipsis,
                                },
                            },
                        }, 1),
                    },
                },
            }),
        };
        AutomationProperties.SetName(folders, "最近访问的文件夹");
        AttachMobileListActions<PlaybackRecentFolderViewModel>(folders,
            _ => { },
            async folder =>
            {
                await _host.Shell.MediaSources.OpenRecentFolderAsync(folder);
                Navigate("media");
                ShowMobileSourceBrowserView();
            },
            onHold: null);
        var emptyFolders = new Border
        {
            Padding = new Thickness(18, 36),
            Background = Brush("CanXPanelBrush"),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "暂无访问的文件夹",
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        var recentFiles = new RadioButton
        {
            Content = "最近播放的文件",
            GroupName = "MobilePlaybackHistoryView",
            IsChecked = playback.ShowRecentFiles,
        };
        var recentFolders = new RadioButton
        {
            Content = "最近访问的文件夹",
            GroupName = "MobilePlaybackHistoryView",
            IsChecked = playback.ShowRecentFolders,
        };
        recentFiles.Click += (_, _) => playback.ShowRecentFiles = true;
        recentFolders.Click += (_, _) => playback.ShowRecentFolders = true;
        var clear = ActionButton("清空历史", "CanXIconClearAll");
        clear.Classes.Add("danger");
        AutomationProperties.SetName(clear, "清空播放历史");
        clear.Click += (_, _) =>
        {
            if (!playback.HasRecentItems) return;
            var confirm = new Button { Content = "清空" };
            confirm.Classes.Add("danger");
            confirm.Click += async (_, _) =>
            {
                try
                {
                    await playback.ClearRecentAsync();
                    CloseModal();
                }
                catch (Exception exception)
                {
                    ShowError(exception);
                }
            };
            ShowModal(
                "清空播放历史",
                new TextBlock
                {
                    Text = "全部播放记录和续播位置将被删除，媒体文件仍会保留。",
                    TextWrapping = TextWrapping.Wrap,
                },
                MobileActions(confirm, CancelButton()));
        };

        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            if (args?.PropertyName is { } propertyName &&
                propertyName is not (nameof(PlaybackViewModel.RecentItems) or
                    nameof(PlaybackViewModel.RecentFolders) or
                    nameof(PlaybackViewModel.SelectedRecentItem) or
                    nameof(PlaybackViewModel.HasRecentItems) or
                    nameof(PlaybackViewModel.HasRecentFolders) or
                    nameof(PlaybackViewModel.ShowRecentFiles) or
                    nameof(PlaybackViewModel.ShowRecentFolders) or
                    nameof(PlaybackViewModel.CanOpenSelectedRecentItem) or
                    nameof(PlaybackViewModel.CanRemoveSelectedRecentItem)))
            {
                return;
            }

            if (args?.PropertyName is null or nameof(PlaybackViewModel.RecentItems))
            {
                updatingSelection = true;
                try
                {
                    list.ItemsSource = playback.RecentItems;
                    list.SelectedItem = playback.SelectedRecentItem;
                }
                finally
                {
                    updatingSelection = false;
                }
            }

            if (args?.PropertyName == nameof(PlaybackViewModel.SelectedRecentItem))
            {
                updatingSelection = true;
                try { list.SelectedItem = playback.SelectedRecentItem; }
                finally { updatingSelection = false; }
            }

            if (args?.PropertyName is null or nameof(PlaybackViewModel.RecentFolders))
            {
                folders.ItemsSource = playback.RecentFolders;
            }

            recentFiles.IsChecked = playback.ShowRecentFiles;
            recentFolders.IsChecked = playback.ShowRecentFolders;
            emptyFolders.IsVisible = playback.ShowRecentFolders && !playback.HasRecentFolders;
            folders.IsVisible = playback.ShowRecentFolders && playback.HasRecentFolders;
            empty.IsVisible = playback.ShowRecentFiles && !playback.HasRecentItems;
            list.IsVisible = playback.ShowRecentFiles && playback.HasRecentItems;
            clear.IsEnabled = playback.HasRecentItems;
        }

        var page = Page(
            "播放历史",
            "本机保存最近播放的媒体和进度，最多 100 项。",
            Horizontal(recentFiles, recentFolders),
            empty,
            list,
            emptyFolders,
            folders,
            MobileSection("管理", clear));
        SubscribeWhileAttached(page, playback, Refresh);
        return page;
    }

    private Control BuildMobileSourceListView()
    {
        var media = _host.Shell.MediaSources;
        var list = new ListBox
        {
            ItemsSource = media.Sources,
            MaxHeight = 520,
            ItemTemplate = MobileTemplate<PlaybackSourceItemViewModel>(item => new StackPanel
            {
                Margin = new Thickness(3, 8),
                Children =
                {
                    new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{item.Type} · {item.Location}", TextWrapping = TextWrapping.Wrap, Opacity = 0.72 },
                },
            }),
        };
        list.SelectionChanged += (_, _) => media.SelectedSource = list.SelectedItem as PlaybackSourceItemViewModel;
        if (media.SelectedSource is not null)
        {
            list.SelectedItem = media.SelectedSource;
        }
        else if (media.Sources.Count > 0)
        {
            list.SelectedIndex = 0;
            media.SelectedSource = list.SelectedItem as PlaybackSourceItemViewModel;
        }
        AttachMobileListActions<PlaybackSourceItemViewModel>(
            list,
            item => media.SelectedSource = item,
            _ => OpenSelectedMobileSourceAsync(),
            (item, anchor) => ShowMobileItemMenu(anchor,
                new MobileItemAction("打开", OpenSelectedMobileSourceAsync, media.CanOpenSelectedSource),
                new MobileItemAction("删除播放源", () => media.RemoveSelectedSourceAsync(),
                    media.CanRemoveSelectedSource, IsDanger: true)));
        var add = AccentButton("添加播放源", "CanXIconAdd");
        add.Click += (_, _) => ShowFullAddMediaSource();
        var player = new Button { Content = "当前播放器" };
        player.Click += (_, _) => ShowMobilePlayerView();
        return Page(
            "播放源",
            string.Empty,
            MobileActions(add, player),
            list,
            new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "media.sources" });
    }

    private Control BuildMobileSourceBrowserView()
    {
        var media = _host.Shell.MediaSources;
        var title = new TextBlock { FontSize = 22, FontWeight = FontWeight.SemiBold };
        var location = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.72 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var backToSources = ActionButton("返回播放源", "CanXIconBack");
        backToSources.Click += (_, _) => ShowMobileSourceListView();
        var back = new Button { Content = "←" };
        back.Click += async (_, _) => await RunAsync(media.NavigateBackAsync());
        var forward = new Button { Content = "→" };
        forward.Click += async (_, _) => await RunAsync(media.NavigateForwardAsync());
        var up = new Button { Content = "↑" };
        up.Click += async (_, _) => await RunAsync(media.NavigateUpAsync());
        var refresh = new Button { Content = AppIcon("CanXIconRefresh") };
        refresh.Click += async (_, _) => await RunAsync(media.RefreshAsync());
        foreach (var (button, label) in new[]
        {
            (back, "后退"), (forward, "前进"), (up, "上级目录"), (refresh, "刷新"),
        })
        {
            AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, label);
            button.MinHeight = 42;
        }
        var favorite = new Button();
        favorite.Click += async (_, _) => await RunAsync(media.ToggleCurrentFavoriteAsync());

        var filter = new TextBox { Text = media.EntryFilterText, PlaceholderText = "筛选文件名" };
        filter.TextChanged += (_, _) => media.EntryFilterText = filter.Text ?? string.Empty;
        var type = Choice(media.EntryTypeFilterOptions, media.SelectedEntryTypeFilter!, value => media.SelectedEntryTypeFilter = value);
        var sort = Choice(
            media.EntrySortOptions.Where(static option => option.Value is
                MediaEntrySortOrder.Name or MediaEntrySortOrder.Size or MediaEntrySortOrder.Modified).ToArray(),
            media.SelectedEntrySort!, value => media.SelectedEntrySort = value);
        var descending = Bool("降序", media.SortDescending, value => media.SortDescending = value);
        var favorites = new ComboBox { ItemsSource = media.Favorites, SelectedItem = media.SelectedFavorite };
        favorites.SelectionChanged += (_, _) => media.SelectedFavorite = favorites.SelectedItem as MediaSourceFavoriteViewModel;
        var openFavorite = new Button { Content = "打开收藏" };
        openFavorite.Click += async (_, _) => await RunAsync(media.OpenSelectedFavoriteAsync());

        var entries = new ListBox
        {
            ItemsSource = media.Entries,
            MaxHeight = 520,
            ItemTemplate = MobileTemplate<MediaResourceItemViewModel>(BuildMobileMediaEntryRow),
        };
        entries.SelectionChanged += (_, _) => media.SelectedEntry = entries.SelectedItem as MediaResourceItemViewModel;
        AttachMobileListActions<MediaResourceItemViewModel>(
            entries,
            item => media.SelectedEntry = item,
            _ => OpenMediaEntryAsync(),
            (item, anchor) => ShowMobileMediaEntryMenu(item, anchor));

        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            title.Text = media.PageTitle;
            location.Text = media.CurrentLocation;
            ToolTip.SetTip(location, media.CurrentLocation);
            status.Text = $"{media.EntryViewSummary} · {media.StatusMessage}";
            favorite.Content = media.FavoriteActionText;
            back.IsEnabled = media.CanNavigateBack;
            forward.IsEnabled = media.CanNavigateForward;
            up.IsEnabled = media.CanNavigateUp;
            refresh.IsEnabled = media.CanRefresh;
            favorite.IsEnabled = media.CanToggleFavorite;
            openFavorite.IsEnabled = media.CanOpenFavorite;
        }
        media.PropertyChanged += Refresh;
        Refresh();

        var browserHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*") };
        browserHeader.Children.Add(backToSources);
        browserHeader.Children.Add(AtColumn(title, 2));
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8 };
        var navigationButtons = new[] { back, forward, up, refresh };
        for (var index = 0; index < navigationButtons.Length; index++)
        {
            var button = navigationButtons[index];
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(button, index);
            navigation.Children.Add(button);
        }

        return Page(
            string.Empty,
            string.Empty,
            browserHeader,
            location,
            navigation,
            filter,
            MobileSection("筛选与排序", Labeled("类型", type), Labeled("排序", sort), descending),
            MobileSection("收藏", favorites, MobileActions(openFavorite, favorite)),
            entries,
            status);
    }

    private static Control BuildMobileRecentRow(PlaybackRecentItemViewModel item)
    {
        var title = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var progressText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var lastPlayed = new TextBlock { FontSize = 12, Foreground = Brush("CanXMutedTextBrush") };
        var progress = new PlaybackProgressPie
        {
            Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center,
            PlayedBrush = Brush("CanXAccentBrush"), RemainingBrush = Brush("CanXAccentSoftBrush"),
        };
        var row = new Border
        {
            Padding = new Thickness(10, 9),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    title,
                    new TextBlock
                    {
                        Text = item.LocationText, FontSize = 12, Foreground = Brush("CanXMutedTextBrush"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,8,*,Auto"),
                        Children = { progress, AtColumn(progressText, 2), AtColumn(lastPlayed, 3) },
                    },
                },
            },
        };
        void Refresh(object? sender, PropertyChangedEventArgs args)
        {
            title.Text = item.Title;
            progress.Progress = item.Progress;
            progressText.Text = item.ProgressText;
            lastPlayed.Text = item.LastPlayedText;
        }
        Refresh(item, new PropertyChangedEventArgs(null));
        SubscribeWhileAttached(row, item, Refresh);
        return row;
    }

    private static Control BuildMobileMediaEntryRow(MediaResourceItemViewModel item)
    {
        var pie = new PlaybackProgressPie
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            PlayedBrush = Brush("CanXAccentBrush"),
            RemainingBrush = Brush("CanXAccentSoftBrush"),
        };
        var details = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{item.Type} · {item.Size} · {item.Modified}", Opacity = 0.72 },
            },
        };
        var row = new Grid
        {
            Margin = new Thickness(2, 7),
            ColumnDefinitions = new ColumnDefinitions("22,*"),
            ColumnSpacing = 8,
            Children = { pie, AtColumn(details, 1) },
        };
        void Refresh()
        {
            pie.IsVisible = item.HasPlaybackHistory;
            pie.Progress = item.PlaybackProgress;
            ToolTip.SetTip(pie, item.PlaybackProgressText);
            AutomationProperties.SetName(pie, item.PlaybackProgressText);
        }

        PropertyChangedEventHandler changed = (_, _) => Refresh();
        var subscribed = false;
        row.AttachedToVisualTree += (_, _) =>
        {
            if (subscribed) return;
            item.PropertyChanged += changed;
            subscribed = true;
            Refresh();
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

    private void ShowMobileMediaEntryMenu(MediaResourceItemViewModel item, ListBoxItem anchor)
    {
        var media = _host.Shell.MediaSources;
        var actions = new List<MobileItemAction>();
        if (media.CanOpenSelected)
        {
            actions.Add(new MobileItemAction(media.SelectedActionText, OpenMediaEntryAsync));
        }
        if (item.IsPlayable)
        {
            actions.Add(new MobileItemAction("选择字幕播放", () =>
            {
                ShowMobileSubtitlePicker(item);
                return Task.CompletedTask;
            }));
        }
        if (actions.Count > 0) ShowMobileItemMenu(anchor, [.. actions]);
    }

    private void ShowMobileSubtitlePicker(MediaResourceItemViewModel item)
    {
        var media = _host.Shell.MediaSources;
        media.SelectedEntry = item;
        var subtitles = new ComboBox
        {
            ItemsSource = media.SubtitleChoices,
            SelectedItem = media.SelectedSubtitle,
            PlaceholderText = "自动匹配字幕",
        };
        subtitles.SelectionChanged += (_, _) =>
            media.SelectedSubtitle = subtitles.SelectedItem as RemoteSubtitleChoiceViewModel;
        var play = AccentButton("播放", "CanXIconPlay");
        play.Click += async (_, _) =>
        {
            CloseModal();
            await OpenMediaEntryAsync();
        };
        ShowModal(item.Name, Labeled("字幕", subtitles), MobileActions(play, CancelButton()));
    }

    private static Grid PlayerControlRow(params Button[] buttons)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*") };
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(button, index);
            row.Children.Add(button);
        }
        return row;
    }

    private Control BuildMobilePlayerView(Action close, Action toggleOrientation)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(toggleOrientation);

        static Button OverlayIconButton(string iconResource, string accessibleName, bool primary = false)
        {
            var button = new Button
            {
                Width = primary ? 52 : 46,
                Height = primary ? 52 : 46,
                MinWidth = primary ? 52 : 46,
                MinHeight = primary ? 52 : 46,
                Padding = new Thickness(primary ? 13 : 12),
                Content = AppIcon(iconResource, primary ? 24 : 21, Brushes.White),
            };
            button.Classes.Add("player-control");
            button.Classes.Add("icon");
            if (primary)
            {
                button.Classes.Add("primary");
            }
            AutomationProperties.SetName(button, accessibleName);
            ToolTip.SetTip(button, accessibleName);
            return button;
        }

        var playback = _host.Shell.Playback;
        var video = new MpvVideoSurfaceHost
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _videoSurface = video;
        video.Attach(_host.MpvEngine);
        video.InitializationFailed += message => SetMobilePlaybackFeedback($"视频渲染失败：{message}");
        video.BackendFallback += playback.ReportSurfaceFallback;
        video.DiagnosticsUpdated += OnMobilePresentationDiagnosticsUpdated;
        playback.UpdatePresentationDiagnostics(video.PresentationDiagnostics);

        var back = OverlayIconButton("CanXIconBack", "返回");
        back.Click += (_, _) => close();

        var previous = OverlayIconButton("CanXIconPrevious", "上一项");
        previous.Click += async (_, _) => await RunAsync(playback.PlayPreviousAsync());
        var seekBack = OverlayIconButton("CanXIconRewind", "后退 10 秒");
        seekBack.Click += async (_, _) => await RunAsync(playback.SeekAsync(-10, relative: true));
        var pause = OverlayIconButton(
            playback.IsPlaying ? "CanXIconPause" : "CanXIconPlay",
            playback.PlayPauseText,
            primary: true);
        pause.Click += async (_, _) => await RunAsync(playback.TogglePauseAsync());
        var orientation = OverlayIconButton("CanXIconRotate", "切换横屏或竖屏");
        orientation.Click += (_, _) => toggleOrientation();
        var seekForward = OverlayIconButton("CanXIconForward", "前进 10 秒");
        seekForward.Click += async (_, _) => await RunAsync(playback.SeekAsync(10, relative: true));
        var next = OverlayIconButton("CanXIconNext", "下一项");
        next.Click += async (_, _) => await RunAsync(playback.PlayNextAsync());
        var previousChapter = new Button
        {
            Width = 34,
            Height = 30,
            MinHeight = 30,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(6),
            Content = AppIcon("CanXIconPreviousChapter", 18, Brushes.White),
        };
        previousChapter.Classes.Add("player-control");
        previousChapter.Classes.Add("icon");
        AutomationProperties.SetName(previousChapter, "上一章节");
        ToolTip.SetTip(previousChapter, "上一章节");
        previousChapter.Click += async (_, _) => await RunAsync(playback.SeekPreviousChapterAsync());
        var nextChapter = new Button
        {
            Width = 34,
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = AppIcon("CanXIconNextChapter", 18, Brushes.White),
        };
        nextChapter.Classes.Add("player-control");
        nextChapter.Classes.Add("icon");
        AutomationProperties.SetName(nextChapter, "下一章节");
        ToolTip.SetTip(nextChapter, "下一章节");
        nextChapter.Click += async (_, _) => await RunAsync(playback.SeekNextChapterAsync());

        var seek = new Slider { Minimum = 0, Maximum = playback.DurationMaximum, Value = playback.PositionSeconds };
        var chapterMarkers = new PlaybackChapterMarkers
        {
            TargetSlider = seek,
            Chapters = playback.ChapterItems,
            DurationSeconds = playback.DurationSeconds,
        };
        var seekInteractionActive = false;
        var seekCommitPending = false;
        var seekInteractionValue = seek.Value;
        double? pendingSeekTarget = null;
        var positionBeforeSeek = playback.PositionSeconds;
        var seekConfirmationDeadline = 0L;
        seek.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) =>
            {
                seekInteractionActive = true;
                seekInteractionValue = seek.Value;
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        seek.ValueChanged += (_, eventArgs) =>
        {
            if (seekInteractionActive)
            {
                seekInteractionValue = eventArgs.NewValue;
            }
        };
        seek.AddHandler(
            InputElement.PointerReleasedEvent,
            async (_, _) =>
            {
                if (!seekInteractionActive || seekCommitPending)
                {
                    return;
                }

                seekCommitPending = true;
                seekInteractionActive = false;
                var targetSeconds = Math.Clamp(seekInteractionValue, seek.Minimum, seek.Maximum);
                positionBeforeSeek = playback.PositionSeconds;
                pendingSeekTarget = targetSeconds;
                seekConfirmationDeadline = Environment.TickCount64 + 2_000;
                seek.Value = targetSeconds;
                try
                {
                    await RunAsync(playback.SeekAsync(targetSeconds, exact: false));
                }
                finally
                {
                    seekCommitPending = false;
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        var time = new TextBlock
        {
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var state = new TextBlock
        {
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.72,
        };
        var volume = new Slider { Minimum = 0, Maximum = 130, Value = playback.Volume };
        AutomationProperties.SetName(volume, "音量");
        volume.ValueChanged += async (_, args) =>
        {
            if (playback.IsReady &&
                Math.Abs(args.NewValue - Math.Clamp(playback.Volume, volume.Minimum, volume.Maximum)) > 0.0001)
                await RunAsync(playback.SetVolumeAsync(args.NewValue));
        };
        var brightness = new Slider { Minimum = -100, Maximum = 100, Value = playback.Brightness };
        brightness.PointerReleased += async (_, _) => await RunAsync(playback.SetBrightnessAsync(brightness.Value));
        var speed = new Slider { Minimum = 0.1, Maximum = 4, Value = playback.Speed };
        speed.PointerReleased += async (_, _) => await RunAsync(playback.SetSpeedAsync(speed.Value));
        var mute = new Button();
        mute.Click += async (_, _) => await RunAsync(playback.ToggleMuteAsync());
        var more = OverlayIconButton("CanXIconMore", "更多播放选项");

        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            var propertyName = args?.PropertyName;
            if (propertyName is null or nameof(PlaybackViewModel.Title))
            {
                title.Text = playback.Title;
            }

            if (propertyName is null or nameof(PlaybackViewModel.State) or nameof(PlaybackViewModel.MediaDetails))
            {
                state.Text = $"{playback.State} · {playback.MediaDetails}";
            }

            if (propertyName is null or nameof(PlaybackViewModel.PlayPauseText))
            {
                pause.Content = AppIcon(
                    playback.IsPlaying ? "CanXIconPause" : "CanXIconPlay",
                    24,
                    Brushes.White);
                AutomationProperties.SetName(pause, playback.PlayPauseText);
                ToolTip.SetTip(pause, playback.PlayPauseText);
            }

            if (propertyName is null or nameof(PlaybackViewModel.MuteText))
            {
                mute.Content = playback.MuteText;
            }

            if (propertyName is null or nameof(PlaybackViewModel.PositionText) or nameof(PlaybackViewModel.DurationText))
            {
                time.Text = $"{playback.PositionText} / {playback.DurationText}";
            }

            if (propertyName is null or nameof(PlaybackViewModel.DurationMaximum))
            {
                seek.Maximum = playback.DurationMaximum;
            }

            if (propertyName is null or nameof(PlaybackViewModel.ChapterItems))
            {
                chapterMarkers.Chapters = playback.ChapterItems;
            }

            if (propertyName is null or nameof(PlaybackViewModel.DurationSeconds))
            {
                chapterMarkers.DurationSeconds = playback.DurationSeconds;
            }

            if (propertyName is null or nameof(PlaybackViewModel.HasChapters))
            {
                previousChapter.IsVisible = playback.HasChapters;
                nextChapter.IsVisible = playback.HasChapters;
            }

            if (propertyName is null or nameof(PlaybackViewModel.CanSeekPreviousChapter))
            {
                previousChapter.IsEnabled = playback.CanSeekPreviousChapter;
            }

            if (propertyName is null or nameof(PlaybackViewModel.CanSeekNextChapter))
            {
                nextChapter.IsEnabled = playback.CanSeekNextChapter;
            }

            if ((propertyName is null or nameof(PlaybackViewModel.PositionSeconds)) && !seekInteractionActive)
            {
                if (pendingSeekTarget is { } targetSeconds)
                {
                    var currentSeconds = playback.PositionSeconds;
                    var reachedTarget = Math.Abs(currentSeconds - targetSeconds) <= 1.5;
                    var movedFromOldPosition = Math.Abs(currentSeconds - positionBeforeSeek) >= 2;
                    var timedOut = Environment.TickCount64 >= seekConfirmationDeadline;
                    if (!reachedTarget && !movedFromOldPosition && !timedOut)
                    {
                        seek.Value = targetSeconds;
                        return;
                    }

                    pendingSeekTarget = null;
                }

                if (!seekCommitPending)
                {
                    seek.Value = playback.PositionSeconds;
                }
            }

            if (propertyName is null or nameof(PlaybackViewModel.Volume))
            {
                volume.Value = playback.Volume;
            }

            if (propertyName is null or nameof(PlaybackViewModel.Brightness) && !brightness.IsPointerOver)
            {
                brightness.Value = playback.Brightness;
            }

            if (propertyName is null or nameof(PlaybackViewModel.Speed) && !speed.IsPointerOver)
            {
                speed.Value = playback.Speed;
            }
        }
        Refresh();

        var topBarContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        topBarContent.Children.Add(back);
        var heading = new StackPanel
        {
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                title,
                state,
            },
        };
        topBarContent.Children.Add(AtColumn(heading, 1));
        topBarContent.Children.Add(AtColumn(orientation, 2));
        var topBar = new Border
        {
            Padding = new Thickness(12, 10),
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = topBarContent,
        };

        var chapterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        chapterRow.Children.Add(time);
        chapterRow.Children.Add(AtColumn(previousChapter, 1));
        chapterRow.Children.Add(AtColumn(nextChapter, 2));
        var bottomStack = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Grid { Children = { seek, chapterMarkers } },
                chapterRow,
                PlayerControlRow(previous, seekBack, pause, seekForward, next, more),
            },
        };
        var bottomBar = new Border
        {
            Padding = new Thickness(12, 8, 12, 12),
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = bottomStack,
        };
        var chrome = new Border
        {
            Child = new Grid
            {
                Children =
                {
                    topBar,
                    bottomBar,
                },
            },
        };
        _mobilePlayerChrome = chrome;

        var closeOptions = new Button { Content = "关闭" };
        var optionsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        optionsHeader.Children.Add(new TextBlock
        {
            Text = "播放控制",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        optionsHeader.Children.Add(AtColumn(closeOptions, 1));
        var optionsContent = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(14),
            Children =
            {
                optionsHeader,
                MobileSection("画面与声音",
                    Labeled("音量", volume),
                    Labeled("亮度", brightness),
                    Labeled("速度", speed),
                    mute),
                BuildMobileTrackSection(playback),
                BuildMobilePlaylistSection(playback),
                BuildMobileChapterBookmarkSection(playback),
                BuildMobileAdvancedPlaybackSection(playback),
                BuildMobileDiagnosticsSection(playback),
                new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "playback.overlay" },
            },
        };
        var options = new Border
        {
            IsVisible = false,
            MaxWidth = 520,
            Margin = new Thickness(42, 0, 0, 0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brush("CanXBackgroundBrush"),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = optionsContent,
            },
        };
        _mobilePlayerOptions = options;
        more.Click += (_, _) =>
        {
            options.IsVisible = true;
            RevealMobilePlayerChrome(autoHide: false);
        };
        closeOptions.Click += (_, _) =>
        {
            options.IsVisible = false;
            RevealMobilePlayerChrome();
        };

        var feedback = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 8),
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
            IsVisible = false,
        };
        _mobilePlayerFeedback = feedback;

        var root = new Grid
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            Children =
            {
                video,
                chrome,
                options,
                feedback,
            },
        };
        // Observe touch before the video renderer or a full-screen chrome host
        // becomes the hit target; no extra layer is placed over the video.
        AttachMobilePlaybackGestures(root, topBar, bottomBar, options);
        root.AttachedToVisualTree += (_, _) => RevealMobilePlayerChrome();
        root.DetachedFromVisualTree += (_, _) =>
        {
            CancelMobilePlayerChromeHide();
            CancelMobilePlayerPendingTap();
        };
        SubscribeWhileAttached(root, playback, Refresh);
        return root;
    }

    private Control BuildMobileTrackSection(PlaybackViewModel playback)
    {
        var video = new ComboBox();
        var audio = new ComboBox();
        var subtitle = new ComboBox();
        var addSubtitle = new Button { Content = AppIcon("CanXIconFolder", 20) };
        AutomationProperties.SetName(addSubtitle, "加载外部字幕");
        ToolTip.SetTip(addSubtitle, "从本地文件夹或网络源加载字幕");
        addSubtitle.Click += (_, _) => ShowMobilePlaybackSubtitlePicker();
        var searchSubtitle = new Button { Content = AppIcon("CanXIconSearch", 20) };
        AutomationProperties.SetName(searchSubtitle, "在线搜索字幕");
        ToolTip.SetTip(searchSubtitle, "通过字幕插件在线搜索");
        searchSubtitle.Click += (_, _) => ShowMobileOnlineSubtitlePicker();
        var subtitleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        subtitleRow.Children.Add(subtitle);
        subtitleRow.Children.Add(AtColumn(addSubtitle, 1));
        subtitleRow.Children.Add(AtColumn(searchSubtitle, 2));
        var discoveredSubtitle = new ComboBox { PlaceholderText = "其他匹配字幕" };
        var audioDevice = new ComboBox();
        var visible = new CheckBox { Content = "显示字幕", IsChecked = playback.IsSubtitleVisible };
        var delay = new Slider { Minimum = -10, Maximum = 10, SmallChange = 0.1, LargeChange = 1, Value = playback.SubtitleDelaySeconds };
        var size = new Slider { Minimum = 0.25, Maximum = 3, SmallChange = 0.05, LargeChange = 0.25, Value = playback.SubtitleScale };
        var position = new Slider { Minimum = 0, Maximum = 100, Value = playback.SubtitlePosition };
        var delayText = new TextBlock();
        var sizeText = new TextBlock();
        var positionText = new TextBlock();
        AutomationProperties.SetName(delay, "字幕延迟");
        AutomationProperties.SetName(size, "字幕大小");
        AutomationProperties.SetName(position, "字幕位置");
        var synchronizingControls = false;
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            synchronizingControls = true;
            try
            {
                var propertyName = args?.PropertyName;
                if (propertyName is null or nameof(PlaybackViewModel.VideoTrackChoices))
                {
                    video.ItemsSource = playback.VideoTrackChoices;
                }

                if (propertyName is null or nameof(PlaybackViewModel.VideoTrackChoices) or nameof(PlaybackViewModel.SelectedVideoTrack))
                {
                    video.SelectedItem = playback.SelectedVideoTrack;
                }

                if (propertyName is null or nameof(PlaybackViewModel.AudioTrackChoices))
                {
                    audio.ItemsSource = playback.AudioTrackChoices;
                }

                if (propertyName is null or nameof(PlaybackViewModel.AudioTrackChoices) or nameof(PlaybackViewModel.SelectedAudioTrack))
                {
                    audio.SelectedItem = playback.SelectedAudioTrack;
                }

                if (propertyName is null or nameof(PlaybackViewModel.SubtitleTrackChoices))
                {
                    subtitle.ItemsSource = playback.SubtitleTrackChoices;
                }

                if (propertyName is null or nameof(PlaybackViewModel.SubtitleTrackChoices) or nameof(PlaybackViewModel.SelectedSubtitleTrack))
                {
                    subtitle.SelectedItem = playback.SelectedSubtitleTrack;
                }

                if (propertyName is null or nameof(PlaybackViewModel.DiscoveredSubtitleChoices) or nameof(PlaybackViewModel.HasDiscoveredSubtitleChoices))
                {
                    discoveredSubtitle.ItemsSource = playback.DiscoveredSubtitleChoices;
                    discoveredSubtitle.IsVisible = playback.HasDiscoveredSubtitleChoices;
                    discoveredSubtitle.SelectedIndex = -1;
                }

                if (propertyName is null or nameof(PlaybackViewModel.AudioDeviceChoices))
                {
                    audioDevice.ItemsSource = playback.AudioDeviceChoices;
                }

                if (propertyName is null or nameof(PlaybackViewModel.AudioDeviceChoices) or nameof(PlaybackViewModel.SelectedAudioDevice))
                {
                    audioDevice.SelectedItem = playback.SelectedAudioDevice;
                }

                if (propertyName is null or nameof(PlaybackViewModel.IsSubtitleVisible))
                {
                    visible.IsChecked = playback.IsSubtitleVisible;
                }
                if (propertyName is null or nameof(PlaybackViewModel.SubtitleDelaySeconds))
                {
                    delay.Value = playback.SubtitleDelaySeconds;
                    delayText.Text = playback.SubtitleDelayText;
                }
                if (propertyName is null or nameof(PlaybackViewModel.SubtitleScale))
                {
                    size.Value = playback.SubtitleScale;
                    sizeText.Text = playback.SubtitleScaleText;
                }
                if (propertyName is null or nameof(PlaybackViewModel.SubtitlePosition))
                {
                    position.Value = playback.SubtitlePosition;
                    positionText.Text = playback.SubtitlePositionText;
                }
                if (propertyName is null or nameof(PlaybackViewModel.CanControl))
                {
                    delay.IsEnabled = size.IsEnabled = position.IsEnabled = playback.CanControl;
                }
            }
            finally
            {
                synchronizingControls = false;
            }
        }
        video.SelectionChanged += async (_, _) =>
        {
            if (synchronizingControls) return;
            if (video.SelectedItem is PlaybackTrackChoiceViewModel choice) await RunAsync(playback.SelectTrackAsync(choice));
        };
        audio.SelectionChanged += async (_, _) =>
        {
            if (synchronizingControls) return;
            if (audio.SelectedItem is PlaybackTrackChoiceViewModel choice) await RunAsync(playback.SelectTrackAsync(choice));
        };
        subtitle.SelectionChanged += async (_, _) =>
        {
            if (synchronizingControls) return;
            if (subtitle.SelectedItem is PlaybackTrackChoiceViewModel choice) await RunAsync(playback.SelectTrackAsync(choice));
        };
        discoveredSubtitle.SelectionChanged += async (_, _) =>
        {
            if (synchronizingControls || discoveredSubtitle.SelectedItem is not DiscoveredSubtitleChoiceViewModel choice) return;
            discoveredSubtitle.SelectedIndex = -1;
            await RunAsync(playback.SelectDiscoveredSubtitleAsync(choice));
        };
        audioDevice.SelectionChanged += async (_, _) =>
        {
            if (synchronizingControls) return;
            if (audioDevice.SelectedItem is PlaybackAudioDeviceChoiceViewModel choice) await RunAsync(playback.SelectAudioDeviceAsync(choice));
        };
        visible.Click += async (_, _) => await RunAsync(playback.SetSubtitleVisibleAsync(visible.IsChecked == true));
        delay.ValueChanged += async (_, args) =>
        {
            if (!synchronizingControls && playback.CanControl &&
                Math.Abs(args.NewValue - Math.Clamp(playback.SubtitleDelaySeconds, delay.Minimum, delay.Maximum)) > 0.0001)
                await RunAsync(playback.SetSubtitleDelayAsync(args.NewValue));
        };
        size.ValueChanged += async (_, args) =>
        {
            if (!synchronizingControls && playback.CanControl &&
                Math.Abs(args.NewValue - Math.Clamp(playback.SubtitleScale, size.Minimum, size.Maximum)) > 0.0001)
                await RunAsync(playback.SetSubtitleScaleAsync(args.NewValue));
        };
        position.ValueChanged += async (_, args) =>
        {
            if (!synchronizingControls && playback.CanControl &&
                Math.Abs(args.NewValue - Math.Clamp(playback.SubtitlePosition, position.Minimum, position.Maximum)) > 0.0001)
                await RunAsync(playback.SetSubtitlePositionAsync(args.NewValue));
        };
        Refresh();
        var section = MobileSection(
            "轨道与字幕",
            Labeled("视频轨", video),
            Labeled("音轨", audio),
            Labeled("字幕轨", subtitleRow),
            discoveredSubtitle,
            Labeled("音频设备", audioDevice),
            visible,
            Labeled("字幕延迟", new StackPanel { Children = { delay, delayText } }),
            Labeled("字幕大小", new StackPanel { Children = { size, sizeText } }),
            Labeled("字幕位置", new StackPanel { Children = { position, positionText } }));
        SubscribeWhileAttached(section, playback, Refresh);
        return section;
    }

    private Control BuildMobilePlaylistSection(PlaybackViewModel playback)
    {
        var playlist = new ListBox
        {
            MaxHeight = 220,
            ItemTemplate = MobileTemplate<PlaybackPlaylistItemViewModel>(item =>
                new TextBlock { Text = $"{item.NumberText} {item.Title} {item.StateText}", TextWrapping = TextWrapping.Wrap }),
        };
        playlist.SelectionChanged += (_, _) => playback.SelectedPlaylistItem = playlist.SelectedItem as PlaybackPlaylistItemViewModel;
        AttachMobileListActions<PlaybackPlaylistItemViewModel>(
            playlist,
            item => playback.SelectedPlaylistItem = item,
            item => playback.PlayPlaylistItemAsync(item),
            (item, anchor) => ShowMobileItemMenu(anchor,
                new MobileItemAction("播放", () => playback.PlayPlaylistItemAsync(item)),
                new MobileItemAction("上移", () => playback.MovePlaylistItemAsync(item, item.Index - 1),
                    item.Index > 0),
                new MobileItemAction("下移", () => playback.MovePlaylistItemAsync(item, item.Index + 1),
                    item.Index < playback.PlaylistItems.Count - 1),
                new MobileItemAction("移除", () => playback.RemovePlaylistItemAsync(item), IsDanger: true)));
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            if (args?.PropertyName is null or nameof(PlaybackViewModel.PlaylistItems))
            {
                playlist.ItemsSource = playback.PlaylistItems;
            }
        }
        var clear = new Button { Content = "清空列表" };
        clear.Click += async (_, _) => await RunAsync(playback.ClearPlaylistAsync());
        Refresh();
        var section = MobileSection(
            "播放列表",
            playlist,
            clear);
        SubscribeWhileAttached(section, playback, Refresh);
        return section;
    }

    private Control BuildMobileChapterBookmarkSection(PlaybackViewModel playback)
    {
        var chapters = new ListBox
        {
            MaxHeight = 180,
            ItemTemplate = MobileTemplate<PlaybackChapterItemViewModel>(item =>
                new TextBlock { Text = $"{item.TimeText}  {item.Title} {item.StateText}" }),
        };
        chapters.SelectionChanged += (_, _) => playback.SelectedChapterItem = chapters.SelectedItem as PlaybackChapterItemViewModel;
        AttachMobileListActions<PlaybackChapterItemViewModel>(
            chapters,
            item => playback.SelectedChapterItem = item,
            item => playback.OpenChapterAsync(item),
            onHold: null);
        var bookmarks = new ListBox
        {
            MaxHeight = 180,
            ItemTemplate = MobileTemplate<PlaybackBookmarkItemViewModel>(item =>
                new TextBlock { Text = $"{item.PositionText}  {item.Title}", TextWrapping = TextWrapping.Wrap }),
        };
        bookmarks.SelectionChanged += (_, _) => playback.SelectedBookmarkItem = bookmarks.SelectedItem as PlaybackBookmarkItemViewModel;
        AttachMobileListActions<PlaybackBookmarkItemViewModel>(
            bookmarks,
            item => playback.SelectedBookmarkItem = item,
            item => playback.OpenBookmarkAsync(item),
            (item, anchor) => ShowMobileItemMenu(anchor,
                new MobileItemAction("跳到书签", () => playback.OpenBookmarkAsync(item)),
                new MobileItemAction("重命名", () =>
                {
                    ShowMobileBookmarkRename(playback, item);
                    return Task.CompletedTask;
                }),
                new MobileItemAction("删除书签", () => playback.RemoveBookmarkAsync(item), IsDanger: true)));
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            if (args?.PropertyName is null or nameof(PlaybackViewModel.ChapterItems))
            {
                chapters.ItemsSource = playback.ChapterItems;
            }

            if (args?.PropertyName is null or nameof(PlaybackViewModel.BookmarkItems))
            {
                bookmarks.ItemsSource = playback.BookmarkItems;
            }
        }
        var title = new TextBox { Text = playback.BookmarkTitleText, PlaceholderText = "书签名称（可选）" };
        title.TextChanged += (_, _) => playback.BookmarkTitleText = title.Text ?? string.Empty;
        var add = new Button { Content = "添加书签" };
        add.Click += async (_, _) => await RunAsync(playback.AddBookmarkAsync());
        var clear = new Button { Content = "清空书签" };
        clear.Click += async (_, _) => await RunAsync(playback.ClearBookmarksAsync());
        Refresh();
        var section = MobileSection(
            "章节与书签",
            Heading("章节"),
            chapters,
            Heading("书签"),
            bookmarks,
            title,
            MobileActions(add, clear));
        SubscribeWhileAttached(section, playback, Refresh);
        return section;
    }

    private void ShowMobileBookmarkRename(PlaybackViewModel playback, PlaybackBookmarkItemViewModel item)
    {
        var name = new TextBox { Text = item.Title };
        var save = AccentButton("保存");
        save.Click += async (_, _) =>
        {
            var previous = playback.BookmarkTitleText;
            playback.SelectedBookmarkItem = item;
            playback.BookmarkTitleText = name.Text ?? string.Empty;
            try
            {
                await playback.RenameBookmarkAsync();
                CloseModal();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
            finally
            {
                playback.BookmarkTitleText = previous;
            }
        };
        ShowModal("重命名书签", Labeled("名称", name), MobileActions(save, CancelButton()));
    }

    private Control BuildMobileAdvancedPlaybackSection(PlaybackViewModel playback)
    {
        var videoFilters = MultilineTextBox(playback.VideoFilterText, "视频滤镜链", 90);
        videoFilters.TextChanged += (_, _) => playback.VideoFilterText = videoFilters.Text ?? string.Empty;
        var audioFilters = MultilineTextBox(playback.AudioFilterText, "音频滤镜链", 90);
        audioFilters.TextChanged += (_, _) => playback.AudioFilterText = audioFilters.Text ?? string.Empty;
        var shaders = new ComboBox { ItemsSource = playback.ShaderPaths, SelectedItem = playback.SelectedShaderPath };
        shaders.SelectionChanged += (_, _) => playback.SelectedShaderPath = shaders.SelectedItem as string;
        var addShader = new Button { Content = "添加 Shader" };
        addShader.Click += async (_, _) => await PickAndAddShaderAsync();
        var removeShader = new Button { Content = "移除 Shader" };
        removeShader.Click += async (_, _) => await RunAsync(playback.RemoveSelectedShaderAsync());
        var clearShaders = new Button { Content = "清空 Shader" };
        clearShaders.Click += async (_, _) => await RunAsync(playback.ClearShadersAsync());

        var colorspaceHint = Choice(
            playback.TargetColorspaceHintOptions,
            playback.SelectedTargetColorspaceHint,
            value => playback.SelectedTargetColorspaceHint = value);
        var colorspaceHintMode = Choice(
            playback.TargetColorspaceHintModeOptions,
            playback.SelectedTargetColorspaceHintMode,
            value => playback.SelectedTargetColorspaceHintMode = value);
        var primaries = Choice(
            playback.TargetPrimariesOptions,
            playback.SelectedTargetPrimaries,
            value => playback.SelectedTargetPrimaries = value);
        var transfer = Choice(
            playback.TargetTransferOptions,
            playback.SelectedTargetTransfer,
            value => playback.SelectedTargetTransfer = value);
        var targetPeak = TextSetting(playback.TargetPeakText, value => playback.TargetPeakText = value, "auto");
        var toneMapping = Choice(
            playback.ToneMappingOptions,
            playback.SelectedToneMapping,
            value => playback.SelectedToneMapping = value);
        var gamutMapping = Choice(
            playback.GamutMappingOptions,
            playback.SelectedGamutMappingMode,
            value => playback.SelectedGamutMappingMode = value);
        var computePeak = Choice(
            playback.HdrComputePeakOptions,
            playback.SelectedHdrComputePeak,
            value => playback.SelectedHdrComputePeak = value);
        var applyColor = new Button { Content = "应用色彩与 HDR" };
        applyColor.Click += async (_, _) => await RunAsync(playback.ApplyColorSettingsAsync());

        var propertyName = TextSetting(playback.RuntimePropertyName, value => playback.RuntimePropertyName = value, "mpv 属性名");
        var propertyValue = TextSetting(playback.RuntimePropertyValue, value => playback.RuntimePropertyValue = value, "属性值");
        var read = new Button { Content = "读取属性" };
        read.Click += async (_, _) => await RunAsync(playback.ReadRuntimePropertyAsync());
        var write = new Button { Content = "写入属性" };
        write.Click += async (_, _) => await RunAsync(playback.WriteRuntimePropertyAsync());
        var command = TextSetting(playback.RuntimeCommandName, value => playback.RuntimeCommandName = value, "mpv 命令");
        var arguments = MultilineTextBox(playback.RuntimeCommandArguments, "每行一个参数", 90);
        arguments.TextChanged += (_, _) => playback.RuntimeCommandArguments = arguments.Text ?? string.Empty;
        var execute = new Button { Content = "执行命令" };
        execute.Click += async (_, _) => await RunAsync(playback.ExecuteRuntimeCommandAsync());
        var result = new TextBlock { Text = playback.AdvancedResult, TextWrapping = TextWrapping.Wrap };
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            if (args?.PropertyName is null or nameof(PlaybackViewModel.AdvancedResult)) result.Text = playback.AdvancedResult;
            if (args?.PropertyName is null or nameof(PlaybackViewModel.ShaderPaths)) shaders.ItemsSource = playback.ShaderPaths;
        }

        var previousFrame = new Button { Content = "上一帧" };
        previousFrame.Click += async (_, _) => await RunAsync(playback.StepFrameAsync(backward: true));
        var nextFrame = new Button { Content = "下一帧" };
        nextFrame.Click += async (_, _) => await RunAsync(playback.StepFrameAsync());
        var loopA = new Button { Content = "设置 A 点" };
        loopA.Click += async (_, _) => await RunAsync(playback.SetAbLoopStartAsync());
        var loopB = new Button { Content = "设置 B 点" };
        loopB.Click += async (_, _) => await RunAsync(playback.SetAbLoopEndAsync());
        var clearLoop = new Button { Content = "清除 AB 循环" };
        clearLoop.Click += async (_, _) => await RunAsync(playback.ClearAbLoopAsync());
        var screenshotSub = new Button { Content = "截图（含字幕）" };
        screenshotSub.Click += async (_, _) => await RunAsync(playback.TakeScreenshotAsync(PlaybackScreenshotMode.Subtitles));
        var screenshotVideo = new Button { Content = "截图（原始视频）" };
        screenshotVideo.Click += async (_, _) => await RunAsync(playback.TakeScreenshotAsync(PlaybackScreenshotMode.Video));
        var watchLater = new Button { Content = "保存播放位置" };
        watchLater.Click += async (_, _) => await RunAsync(playback.WriteWatchLaterAsync());

        var section = MobileSection(
            "滤镜、Shader 与高级控制",
            MobileActions(previousFrame, nextFrame, loopA, loopB, clearLoop, screenshotSub, screenshotVideo, watchLater),
            Labeled("视频滤镜", videoFilters),
            MobileActions(CommandButton("应用视频滤镜", new AsyncCommand(() => playback.ApplyVideoFiltersAsync())), CommandButton("清除", new AsyncCommand(() => playback.ClearVideoFiltersAsync()))),
            Labeled("音频滤镜", audioFilters),
            MobileActions(CommandButton("应用音频滤镜", new AsyncCommand(() => playback.ApplyAudioFiltersAsync())), CommandButton("清除", new AsyncCommand(() => playback.ClearAudioFiltersAsync()))),
            Labeled("Shader", shaders),
            MobileActions(addShader, removeShader, clearShaders),
            Heading("色彩与 HDR"),
            Labeled("输出色彩空间提示", colorspaceHint),
            Labeled("提示来源", colorspaceHintMode),
            Labeled("目标色域", primaries),
            Labeled("目标传递函数", transfer),
            Labeled("目标峰值亮度", targetPeak),
            Labeled("Tone mapping", toneMapping),
            Labeled("色域映射", gamutMapping),
            Labeled("动态峰值分析", computePeak),
            applyColor,
            Heading("运行时属性"),
            propertyName,
            propertyValue,
            MobileActions(read, write),
            Heading("运行时命令"),
            command,
            arguments,
            execute,
            result);
        SubscribeWhileAttached(section, playback, Refresh);
        return section;
    }

    private Control BuildMobileDiagnosticsSection(PlaybackViewModel playback)
    {
        var diagnostics = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = FontFamily.Parse("monospace") };
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            if (args?.PropertyName is not (null or
                nameof(PlaybackViewModel.CapabilitySummary) or
                nameof(PlaybackViewModel.DecodePathText) or
                nameof(PlaybackViewModel.RenderPathText) or
                nameof(PlaybackViewModel.PresentationPathText) or
                nameof(PlaybackViewModel.PresentationPerformanceText) or
                nameof(PlaybackViewModel.InputColorText) or
                nameof(PlaybackViewModel.TargetColorText) or
                nameof(PlaybackViewModel.PerformanceText) or
                nameof(PlaybackViewModel.FrameStatisticsText) or
                nameof(PlaybackViewModel.BitrateCacheText)))
            {
                return;
            }

            diagnostics.Text =
                $"{playback.CapabilitySummary}\n\n" +
                $"解码：{playback.DecodePathText}\n\n" +
                $"渲染：{playback.RenderPathText}\n\n" +
                $"呈现：{playback.PresentationPathText}\n" +
                $"{playback.PresentationPerformanceText}\n\n" +
                $"{playback.InputColorText}\n{playback.TargetColorText}\n\n" +
                $"{playback.PerformanceText}\n{playback.FrameStatisticsText}\n{playback.BitrateCacheText}";
        }
        Refresh();
        var section = MobileSection("播放诊断", diagnostics);
        SubscribeWhileAttached(section, playback, Refresh);
        return section;
    }

    private static void SubscribeWhileAttached(
        Control control,
        INotifyPropertyChanged source,
        PropertyChangedEventHandler handler)
    {
        var subscribed = false;
        control.AttachedToVisualTree += (_, _) =>
        {
            if (subscribed) return;
            source.PropertyChanged += handler;
            subscribed = true;
            handler(source, new PropertyChangedEventArgs(null));
        };
        control.DetachedFromVisualTree += (_, _) =>
        {
            if (!subscribed) return;
            source.PropertyChanged -= handler;
            subscribed = false;
        };
    }

    private async Task OpenSelectedMobileSourceAsync()
    {
        var media = _host.Shell.MediaSources;
        var source = media.SelectedSource;
        if (source is null) return;
        try
        {
            await media.OpenSelectedSourceAsync();
            ShowMobileSourceBrowserView();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ShowMobileSourceListView()
    {
        if (_mobileMediaHost is not null)
        {
            _mobileSourceListView ??= BuildMobileSourceListView();
            _mobileMediaHost.Content = _mobileSourceListView;
        }
    }

    private void ShowMobileSourceBrowserView()
    {
        if (_mobileMediaHost is not null)
        {
            _mobileSourceBrowserView ??= BuildMobileSourceBrowserView();
            _mobileMediaHost.Content = _mobileSourceBrowserView;
        }
    }

    private void ShowMobilePlayerView()
    {
        CloseMobileItemMenu();
        var activity = MainActivity.Current
            ?? throw new InvalidOperationException("Android main Activity is unavailable.");
        PlaybackActivity.Show(activity);
    }

    private async Task ShowMobileImageViewerAsync(MediaResource selected)
    {
        CloseMobileImageViewer();
        var sequence = _host.Shell.MediaSources.AcquireImageSequence(selected);
        var viewModel = new ImageViewerViewModel(sequence, decodeWidth: 2560);
        try
        {
            var viewer = new ImageViewerView(viewModel, mobile: true, CloseMobileImageViewer);
            _mobileImageViewModel = viewModel;
            _mobileImageViewer = viewer;
            Grid.SetRowSpan(viewer, 4);
            viewer.ZIndex = 10;
            _root.Children.Add(viewer);
            await viewModel.LoadAsync();
        }
        catch
        {
            CloseMobileImageViewer();
            viewModel.Dispose();
            throw;
        }
    }

    private void CloseMobileImageViewer()
    {
        if (_mobileImageViewer is { } viewer)
        {
            _root.Children.Remove(viewer);
            viewer.Dispose();
            _mobileImageViewer = null;
        }
        _mobileImageViewModel?.Dispose();
        _mobileImageViewModel = null;
    }

    internal Control CreatePlaybackActivityView(Action close, Action toggleOrientation)
    {
        if (_mobilePlayerView is not null)
        {
            throw new InvalidOperationException("Android playback Activity already owns the player view.");
        }

        _mobilePlayerView = BuildMobilePlayerView(close, toggleOrientation);
        return _mobilePlayerView;
    }

    internal void ReleasePlaybackActivityView(Control content)
    {
        if (!ReferenceEquals(_mobilePlayerView, content))
        {
            return;
        }

        CancelMobilePlayerChromeHide();
        CancelMobilePlayerPendingTap();
        _mobilePlayerTapTimer = null;
        CancelMobilePlaybackGesture();
        if (_videoSurface is not null)
        {
            _videoSurface.DiagnosticsUpdated -= OnMobilePresentationDiagnosticsUpdated;
            _videoSurface.BackendFallback -= _host.Shell.Playback.ReportSurfaceFallback;
        }

        _mobilePlayerView = null;
        _mobilePlayerChrome = null;
        _mobilePlayerOptions = null;
        _mobilePlayerFeedback = null;
        _videoSurface = null;
    }

    private void OnMobilePresentationDiagnosticsUpdated(VideoPresentationDiagnostics diagnostics) =>
        _host.Shell.Playback.UpdatePresentationDiagnostics(diagnostics);

    private void ShowFullAddMediaSource()
    {
        var viewModel = new AddPlaybackSourceViewModel(_host.Shell.MediaSources);
        var content = new StackPanel { Spacing = 9 };
        var type = new ComboBox { ItemsSource = viewModel.SourceTypes, SelectedItem = viewModel.SelectedType };
        var name = new TextBox { PlaceholderText = "显示名称（可选）" };
        var endpoint = new TextBox();
        var user = new TextBox { PlaceholderText = "用户名（可选）" };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "密码（可选）" };
        var browse = new Button { Content = "选择文件夹" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        var add = AccentButton("添加");
        void Render()
        {
            endpoint.PlaceholderText = viewModel.IsLocalFolder ? "本地文件夹" : viewModel.EndpointPlaceholder;
            user.IsVisible = viewModel.IsNetworkSource;
            password.IsVisible = viewModel.IsNetworkSource;
            browse.IsVisible = viewModel.IsLocalFolder;
        }
        type.SelectionChanged += (_, _) =>
        {
            if (type.SelectedItem is PlaybackSourceTypeOption option) viewModel.SelectedType = option;
            Render();
        };
        name.TextChanged += (_, _) => viewModel.Name = name.Text ?? string.Empty;
        endpoint.TextChanged += (_, _) =>
        {
            if (viewModel.IsLocalFolder) viewModel.LocalFolder = endpoint.Text ?? string.Empty;
            else viewModel.EndpointText = endpoint.Text ?? string.Empty;
        };
        user.TextChanged += (_, _) => viewModel.UserName = user.Text ?? string.Empty;
        password.TextChanged += (_, _) => viewModel.Password = password.Text ?? string.Empty;
        browse.Click += async (_, _) =>
        {
            error.Text = string.Empty;
            try
            {
                await AndroidExternalStorageAccess.EnsureGrantedAsync();
                var top = TopLevel.GetTopLevel(this);
                if (top?.StorageProvider is null) return;
                var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "选择本地媒体文件夹",
                });
                var folder = folders.FirstOrDefault();
                if (folder is null) return;
                try
                {
                    endpoint.Text = AndroidExternalStorageAccess.ResolveLocalFolderPath(folder);
                }
                finally
                {
                    folder.Dispose();
                }
            }
            catch (Exception exception)
            {
                error.Text = exception.Message;
            }
        };
        add.Click += async (_, _) =>
        {
            try
            {
                await viewModel.AddAsync();
                CloseModal();
                ShowMobileSourceListView();
            }
            catch (Exception exception)
            {
                error.Text = exception.Message;
            }
        };
        content.Children.Add(Labeled("类型", type));
        content.Children.Add(Labeled("名称", name));
        content.Children.Add(Labeled("位置", endpoint));
        content.Children.Add(browse);
        content.Children.Add(user);
        content.Children.Add(password);
        content.Children.Add(error);
        Render();
        ShowModal("添加播放源", content, MobileActions(add, CancelButton()));
    }

    private async Task PickAndPlayMultipleAsync()
    {
        try
        {
            var files = await PickStorageFilesAsync("打开媒体", allowMultiple: true, MediaFileTypes());
            if (files.Count > 0) await _host.PlayStorageFilesAsync(files);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task PickAndAddPlaylistAsync()
    {
        try
        {
            var files = await PickStorageFilesAsync("添加到播放列表", allowMultiple: true, MediaFileTypes());
            if (files.Count == 0) return;
            await _host.AddStorageFilesToPlaylistAsync(files);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private sealed record SubtitleLocationChoice(string? ProfileId, string Label)
    {
        public override string ToString() => Label;
    }

    private void ShowMobilePlaybackSubtitlePicker()
    {
        var playback = _host.Shell.Playback;
        var mediaSources = _host.Shell.MediaSources;
        var choices = new List<SubtitleLocationChoice> { new(null, "本地文件夹") };
        choices.AddRange(mediaSources.Sources.Select(source =>
            new SubtitleLocationChoice(source.Id, $"{source.Name} · {source.Type}")));
        var source = new ComboBox { ItemsSource = choices };
        source.SelectedItem = choices.FirstOrDefault(choice =>
            choice.ProfileId == playback.CurrentMediaSourceProfileId) ?? choices[0];
        var up = new Button { Content = AppIcon("CanXIconBack", 20) };
        AutomationProperties.SetName(up, "字幕上级目录");
        ToolTip.SetTip(up, "上级目录");
        var location = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("CanXMutedTextBrush"),
        };
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        navigation.Children.Add(up);
        navigation.Children.Add(AtColumn(location, 1));
        var list = new ListBox
        {
            MaxHeight = 340,
            MinHeight = 190,
            ItemTemplate = MobileTemplate<MediaResource>(item => new TextBlock
            {
                Text = item.IsDirectory ? $"▣  {item.Name}" : item.Name,
                Margin = new Thickness(8, 10),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }),
        };
        AutomationProperties.SetName(list, "字幕文件列表");
        var hint = new TextBlock
        {
            Text = "从视频所在文件夹或其他本地文件夹选择字幕",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("CanXMutedTextBrush"),
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("CanXMutedTextBrush") };
        var local = ActionButton("浏览本地", "CanXIconFolder");
        ShowModal("选择字幕", Labeled("位置", source), navigation, list, hint, status,
            MobileActions(local, CancelButton()));
        _subtitlePickerCancellation = new CancellationTokenSource();
        var token = _subtitlePickerCancellation.Token;

        void RefreshBrowser(SubtitleSourceBrowser browser)
        {
            if (_subtitlePickerBrowser != browser || token.IsCancellationRequested) return;
            list.ItemsSource = browser.Entries;
            location.Text = browser.CurrentDirectory.IsFile
                ? browser.CurrentDirectory.LocalPath : browser.CurrentDirectory.AbsoluteUri;
            up.IsEnabled = browser.CanNavigateUp;
            status.Text = browser.Entries.Count == 0 ? "此目录没有字幕文件" : string.Empty;
        }

        async Task ChangeSourceAsync()
        {
            source.IsEnabled = false;
            list.IsEnabled = false;
            list.ItemsSource = null;
            var previous = _subtitlePickerBrowser;
            _subtitlePickerBrowser = null;
            if (previous is not null) await previous.DisposeAsync();
            try
            {
                if (source.SelectedItem is not SubtitleLocationChoice { ProfileId: { } profileId })
                {
                    navigation.IsVisible = false;
                    list.IsVisible = false;
                    hint.IsVisible = true;
                    status.Text = string.Empty;
                    return;
                }

                navigation.IsVisible = true;
                list.IsVisible = true;
                hint.IsVisible = false;
                status.Text = "正在读取目录…";
                var mediaUri = profileId == playback.CurrentMediaSourceProfileId
                    ? playback.CurrentMediaSourceUri : null;
                var browser = await mediaSources.OpenSubtitleSourceAsync(profileId, mediaUri, token);
                if (token.IsCancellationRequested)
                {
                    await browser.DisposeAsync();
                    return;
                }

                _subtitlePickerBrowser = browser;
                RefreshBrowser(browser);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                status.Text = exception.Message;
            }
            finally
            {
                source.IsEnabled = true;
                list.IsEnabled = true;
            }
        }

        up.Click += async (_, _) =>
        {
            var browser = _subtitlePickerBrowser;
            if (browser is null || !browser.CanNavigateUp) return;
            try
            {
                list.IsEnabled = false;
                await browser.NavigateUpAsync(token);
                RefreshBrowser(browser);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                status.Text = exception.Message;
            }
            finally { list.IsEnabled = true; }
        };
        list.SelectionChanged += async (_, _) =>
        {
            var browser = _subtitlePickerBrowser;
            if (browser is null || list.SelectedItem is not MediaResource item) return;
            list.SelectedItem = null;
            try
            {
                list.IsEnabled = false;
                if (item.IsDirectory)
                {
                    await browser.BrowseAsync(item.Uri, token);
                    RefreshBrowser(browser);
                }
                else
                {
                    var lease = browser.AcquireRead(item);
                    await _host.AddNetworkSubtitleAsync(lease, item, token);
                    CloseModal();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                status.Text = exception.Message;
            }
            finally { list.IsEnabled = true; }
        };
        local.Click += async (_, _) =>
        {
            if (await PickAndAddSubtitleAsync()) CloseModal();
        };
        source.SelectionChanged += async (_, _) => await ChangeSourceAsync();
        _ = ChangeSourceAsync();
    }

    private void ShowMobileOnlineSubtitlePicker()
    {
        var catalog = _host.Shell.OnlineSubtitles;
        var playbackSource = _host.Shell.Playback.Source;
        var providers = catalog.Providers;
        var provider = new ComboBox { ItemsSource = providers, SelectedIndex = providers.Count > 0 ? 0 : -1 };
        AutomationProperties.SetName(provider, "字幕搜索接口");
        var query = new TextBox
        {
            Text = OnlineSubtitleCatalog.SuggestedQuery(_host.Shell.Playback.Title),
            PlaceholderText = "搜索视频名称",
        };
        AutomationProperties.SetName(query, "在线字幕关键词");
        var search = new Button { Content = "搜索", IsEnabled = providers.Count > 0 };
        var results = new ListBox
        {
            MinHeight = 150,
            MaxHeight = 230,
            ItemTemplate = MobileTemplate<OnlineSubtitleResult>(item => new StackPanel
            {
                Margin = new Thickness(8, 9),
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = item.Details,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("CanXMutedTextBrush"),
                    },
                },
            }),
        };
        AutomationProperties.SetName(results, "在线字幕搜索结果");
        var files = new ListBox
        {
            MaxHeight = 130,
            IsVisible = false,
            ItemTemplate = MobileTemplate<OnlineSubtitleSource>(item => new TextBlock
            {
                Text = item.Name,
                Margin = new Thickness(8, 10),
                TextWrapping = TextWrapping.Wrap,
            }),
        };
        AutomationProperties.SetName(files, "在线字幕文件");
        var status = new TextBlock
        {
            Text = providers.Count > 0 ? "输入关键词搜索在线字幕" : "请先在插件页面安装并启用字幕搜索插件",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("CanXMutedTextBrush"),
        };
        var load = new Button { Content = "加载", IsEnabled = false };
        ShowModal("在线字幕", Labeled("搜索接口", provider), Labeled("关键词", query), search, results, files, status,
            MobileActions(load, CancelButton()));
        _subtitlePickerCancellation = new CancellationTokenSource();
        var token = _subtitlePickerCancellation.Token;
        var busy = false;

        async Task SearchAsync()
        {
            if (busy || token.IsCancellationRequested) return;
            if (provider.SelectedItem is not OnlineSubtitleProvider selectedProvider)
            {
                status.Text = "请先选择字幕搜索接口";
                return;
            }
            busy = true;
            search.IsEnabled = false;
            provider.IsEnabled = false;
            results.ItemsSource = null;
            files.ItemsSource = null;
            files.IsVisible = false;
            load.IsEnabled = false;
            status.Text = "正在搜索字幕…";
            try
            {
                var page = await catalog.SearchAsync(query.Text ?? string.Empty, selectedProvider.Id, token);
                if (token.IsCancellationRequested) return;
                results.ItemsSource = page.Items;
                status.Text = page.Errors.Count > 0
                    ? string.Join("；", page.Errors)
                    : page.Items.Count == 0 ? "没有找到字幕，请尝试更短的关键词" : $"找到 {page.Items.Count} 条字幕";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { status.Text = exception.Message; }
            finally { busy = false; search.IsEnabled = true; provider.IsEnabled = true; }
        }

        provider.SelectionChanged += (_, _) =>
        {
            if (busy) return;
            results.ItemsSource = null;
            files.ItemsSource = null;
            files.IsVisible = false;
            load.IsEnabled = false;
            status.Text = provider.SelectedItem is OnlineSubtitleProvider
                ? "输入关键词搜索在线字幕"
                : "请先选择字幕搜索接口";
        };
        search.Click += async (_, _) => await SearchAsync();
        query.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter) await SearchAsync();
        };
        results.SelectionChanged += async (_, _) =>
        {
            if (busy || results.SelectedItem is not OnlineSubtitleResult result) return;
            busy = true;
            results.IsEnabled = false;
            provider.IsEnabled = false;
            files.ItemsSource = null;
            files.IsVisible = false;
            load.IsEnabled = false;
            status.Text = "正在获取字幕文件…";
            try
            {
                var sources = await catalog.ResolveAsync(result, token);
                if (token.IsCancellationRequested) return;
                files.ItemsSource = sources;
                files.IsVisible = true;
                files.SelectedIndex = sources.Count == 1 ? 0 : -1;
                status.Text = sources.Count == 1 ? "可以加载字幕" : "请选择要加载的字幕文件";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { status.Text = exception.Message; }
            finally { busy = false; results.IsEnabled = true; provider.IsEnabled = true; }
        };
        files.SelectionChanged += (_, _) => load.IsEnabled = files.SelectedItem is OnlineSubtitleSource;
        load.Click += async (_, _) =>
        {
            if (busy || files.SelectedItem is not OnlineSubtitleSource selected) return;
            busy = true;
            load.IsEnabled = false;
            provider.IsEnabled = false;
            status.Text = "正在加载字幕…";
            try
            {
                if (!string.Equals(playbackSource, _host.Shell.Playback.Source, StringComparison.Ordinal))
                    throw new InvalidOperationException("播放的视频已改变，请重新选择字幕。");
                await _host.AddOnlineSubtitleAsync(selected, token);
                if (!token.IsCancellationRequested) CloseModal();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { status.Text = exception.Message; }
            finally { busy = false; load.IsEnabled = true; provider.IsEnabled = true; }
        };
    }

    private async Task<bool> PickAndAddSubtitleAsync()
    {
        try
        {
            var localDirectory = SubtitlePickerLocation.GetLocalVideoDirectory(_host.Shell.Playback.Source);
            IStorageFolder? storageFolder = null;
            if (localDirectory is null)
            {
                try { storageFolder = await _host.GetCurrentStorageMediaFolderAsync(); }
                catch (Exception) { /* The picker can still open at its default location. */ }
            }

            using (storageFolder)
            {
                var files = await PickStorageFilesAsync("添加字幕", allowMultiple: false,
                    [SubtitlePickerLocation.SubtitleFileType],
                    localDirectory,
                    storageFolder);
                if (files.FirstOrDefault() is not { } file) return false;
                await _host.AddStorageSubtitleAsync(file);
                return true;
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private async Task PickAndAddShaderAsync()
    {
        try
        {
            var files = await PickStorageFilesAsync("添加 GLSL Shader", false,
            [new FilePickerFileType("GLSL Shader") { Patterns = ["*.glsl", "*.hook"] }]);
            var file = files.FirstOrDefault();
            if (file is null) return;
            var path = file.TryGetLocalPath();
            var temporary = string.IsNullOrWhiteSpace(path);
            if (temporary) path = await MaterializeStorageFileAsync(file, "shaders");
            else file.Dispose();
            await _host.Shell.Playback.AddShaderAsync(path!);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task<IReadOnlyList<IStorageFile>> PickStorageFilesAsync(
        string title,
        bool allowMultiple,
        IReadOnlyList<FilePickerFileType> filters,
        string? startDirectory = null,
        IStorageFolder? startFolder = null)
    {
        var top = _videoSurface is null ? null : TopLevel.GetTopLevel(_videoSurface);
        top ??= TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return [];
        IStorageFolder? start = startFolder;
        if (startDirectory is not null)
        {
            try { start = await top.StorageProvider.TryGetFolderFromPathAsync(startDirectory); }
            catch (Exception) { /* Fall back to the system's regular document picker. */ }
        }
        return await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = allowMultiple,
            Title = title,
            FileTypeFilter = filters,
            SuggestedStartLocation = start,
        });
    }

    private static IReadOnlyList<FilePickerFileType> MediaFileTypes() =>
    [
        new FilePickerFileType("媒体文件")
        {
            Patterns =
            [
                "*.3gp", "*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi", "*.m4v", "*.ts", "*.m2ts",
                "*.mp3", "*.flac", "*.m4a", "*.aac", "*.ac3", "*.ogg", "*.wav", "*.opus", "*.m3u", "*.m3u8",
            ],
            MimeTypes = ["video/*", "audio/*", "application/vnd.apple.mpegurl"],
        },
        FilePickerFileTypes.All,
    ];

    private void AttachMobilePlaybackGestures(
        Control surface,
        Control topBar,
        Control bottomBar,
        Control options)
    {
        var tapTimer = new DispatcherTimer { Interval = MobilePlayerDoubleTapDelay };
        _mobilePlayerTapTimer = tapTimer;
        tapTimer.Tick += (_, _) =>
        {
            tapTimer.Stop();
            if (ReferenceEquals(_mobilePlayerTapTimer, tapTimer))
            {
                CommitMobilePlayerPendingTap();
            }
        };
        surface.AddHandler(InputElement.PointerPressedEvent, (_, eventArgs) =>
        {
            if (eventArgs.Pointer.Type != PointerType.Touch &&
                !eventArgs.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
            if (_mobileGesturePointer is not null)
            {
                _mobileGestureHadMultiplePointers = true;
                _mobileGesturePendingAdjustment = null;
                CancelMobilePlayerPendingTap(resumeChromeAutoHide: true);
                SetMobilePlaybackFeedback(string.Empty);
                return;
            }
            if (IsWithin(topBar, eventArgs.Source) ||
                IsWithin(bottomBar, eventArgs.Source) ||
                IsWithin(options, eventArgs.Source))
            {
                CancelMobilePlayerPendingTap();
                RevealMobilePlayerChrome();
                return;
            }
            var playback = _host.Shell.Playback;
            var position = eventArgs.GetPosition(surface);
            if (tapTimer.IsEnabled && _mobilePlayerFirstTapPosition is { } firstTap)
            {
                if (IsNearMobilePlayerTap(firstTap, position))
                {
                    tapTimer.Stop();
                    _mobilePlayerSecondTapInProgress = true;
                }
                else
                {
                    CommitMobilePlayerPendingTap();
                }
            }
            _mobileGesturePointer = eventArgs.Pointer;
            _mobileGestureHadMultiplePointers = false;
            _mobileGestureStart = position;
            _mobileGestureStartPosition = playback.PositionSeconds;
            _mobileGestureStartVolume = playback.Volume;
            _mobileGestureStartBrightness = playback.Brightness;
            _mobileGestureKind = PlaybackGestureKind.None;
            eventArgs.Pointer.Capture(surface);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        surface.AddHandler(InputElement.PointerMovedEvent, (_, eventArgs) =>
        {
            if (!ReferenceEquals(_mobileGesturePointer, eventArgs.Pointer) ||
                _mobileGestureHadMultiplePointers || _mobileGestureStart is not { } start) return;
            var current = eventArgs.GetPosition(surface);
            if (_mobileGestureKind == PlaybackGestureKind.None)
            {
                _mobileGestureKind = PlaybackGestureCalculator.Detect(start, current, surface.Bounds.Width);
                if (_mobileGestureKind != PlaybackGestureKind.None && _mobilePlayerSecondTapInProgress)
                {
                    CommitMobilePlayerPendingTap();
                }
            }
            var playback = _host.Shell.Playback;
            if (!playback.CanControl) return;
            switch (_mobileGestureKind)
            {
                case PlaybackGestureKind.Seek:
                    SetMobilePlaybackFeedback(playback.CanSeek
                        ? $"跳转到 {PlaybackGestureCalculator.CalculateSeekTarget(_mobileGestureStartPosition, playback.DurationSeconds, current.X - start.X, surface.Bounds.Width):0} 秒"
                        : "当前媒体不支持跳转");
                    break;
                case PlaybackGestureKind.Brightness:
                    var brightness = PlaybackGestureCalculator.CalculateVerticalValue(
                        _mobileGestureStartBrightness, current.Y - start.Y, surface.Bounds.Height, -100, 100);
                    SetMobilePlaybackFeedback($"亮度 {brightness:0}");
                    QueueMobilePlaybackAdjustment(PlaybackGestureKind.Brightness, brightness);
                    break;
                case PlaybackGestureKind.Volume:
                    var volume = PlaybackGestureCalculator.CalculateVerticalValue(
                        _mobileGestureStartVolume, current.Y - start.Y, surface.Bounds.Height, 0, 130);
                    SetMobilePlaybackFeedback($"音量 {volume:0}%");
                    QueueMobilePlaybackAdjustment(PlaybackGestureKind.Volume, volume);
                    break;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        surface.AddHandler(InputElement.PointerReleasedEvent, async (_, eventArgs) =>
        {
            if (!ReferenceEquals(_mobileGesturePointer, eventArgs.Pointer) ||
                _mobileGestureStart is not { } start) return;
            var current = eventArgs.GetPosition(surface);
            if (_mobileGestureKind == PlaybackGestureKind.None)
            {
                _mobileGestureKind = PlaybackGestureCalculator.Detect(start, current, surface.Bounds.Width);
            }
            var playback = _host.Shell.Playback;
            var isTap = !_mobileGestureHadMultiplePointers && _mobileGestureKind == PlaybackGestureKind.None;
            var secondTapInProgress = _mobilePlayerSecondTapInProgress;
            Task action = Task.CompletedTask;
            if (!_mobileGestureHadMultiplePointers &&
                _mobileGestureKind == PlaybackGestureKind.Brightness && playback.CanControl)
            {
                QueueMobilePlaybackAdjustment(PlaybackGestureKind.Brightness,
                    PlaybackGestureCalculator.CalculateVerticalValue(
                        _mobileGestureStartBrightness, current.Y - start.Y, surface.Bounds.Height, -100, 100));
                action = _mobileGestureAdjustmentTask;
            }
            else if (!_mobileGestureHadMultiplePointers &&
                     _mobileGestureKind == PlaybackGestureKind.Volume && playback.CanControl)
            {
                QueueMobilePlaybackAdjustment(PlaybackGestureKind.Volume,
                    PlaybackGestureCalculator.CalculateVerticalValue(
                        _mobileGestureStartVolume, current.Y - start.Y, surface.Bounds.Height, 0, 130));
                action = _mobileGestureAdjustmentTask;
            }
            else if (!_mobileGestureHadMultiplePointers &&
                     _mobileGestureKind == PlaybackGestureKind.Seek && playback.CanSeek)
            {
                action = playback.SeekAsync(PlaybackGestureCalculator.CalculateSeekTarget(
                    _mobileGestureStartPosition, playback.DurationSeconds, current.X - start.X, surface.Bounds.Width),
                    exact: false);
            }
            CancelMobilePlaybackGesture(clearPendingAdjustment: false);
            if (isTap)
            {
                if (secondTapInProgress &&
                    _mobilePlayerFirstTapPosition is { } firstTap &&
                    IsNearMobilePlayerTap(firstTap, current))
                {
                    CancelMobilePlayerPendingTap(resumeChromeAutoHide: true);
                    if (playback.CanControl)
                    {
                        action = playback.TogglePauseAsync();
                    }
                }
                else
                {
                    if (secondTapInProgress) CommitMobilePlayerPendingTap();
                    QueueMobilePlayerSingleTap(current);
                }
            }
            else if (secondTapInProgress)
            {
                CommitMobilePlayerPendingTap();
            }
            await RunAsync(action);
            SetMobilePlaybackFeedback(string.Empty);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        surface.PointerCaptureLost += (_, eventArgs) =>
        {
            if (ReferenceEquals(_mobileGesturePointer, eventArgs.Pointer))
            {
                CancelMobilePlaybackGesture();
                CancelMobilePlayerPendingTap(resumeChromeAutoHide: true);
            }
        };
    }

    private static bool IsNearMobilePlayerTap(Point first, Point second) =>
        Math.Abs(first.X - second.X) <= MobilePlayerDoubleTapDistance &&
        Math.Abs(first.Y - second.Y) <= MobilePlayerDoubleTapDistance;

    private void QueueMobilePlayerSingleTap(Point position)
    {
        _mobilePlayerFirstTapPosition = position;
        _mobilePlayerSecondTapInProgress = false;
        CancelMobilePlayerChromeHide();
        _mobilePlayerTapTimer?.Start();
    }

    private void CommitMobilePlayerPendingTap()
    {
        if (_mobilePlayerFirstTapPosition is null) return;
        CancelMobilePlayerPendingTap();
        ToggleMobilePlayerChrome();
    }

    private void CancelMobilePlayerPendingTap(bool resumeChromeAutoHide = false)
    {
        var hadPendingTap = _mobilePlayerFirstTapPosition is not null;
        _mobilePlayerTapTimer?.Stop();
        _mobilePlayerFirstTapPosition = null;
        _mobilePlayerSecondTapInProgress = false;
        if (resumeChromeAutoHide && hadPendingTap && _mobilePlayerChrome?.IsVisible == true)
        {
            RevealMobilePlayerChrome();
        }
    }

    private static bool IsWithin(Visual ancestor, object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, ancestor)) return true;
        }
        return false;
    }

    private void QueueMobilePlaybackAdjustment(PlaybackGestureKind kind, double value)
    {
        if (_mobileGesturePendingAdjustmentKind == kind &&
            _mobileGesturePendingAdjustment == value) return;
        _mobileGesturePendingAdjustmentKind = kind;
        _mobileGesturePendingAdjustment = value;
        if (_mobileGestureAdjustmentTask.IsCompleted)
        {
            _mobileGestureAdjustmentTask = ApplyMobilePlaybackAdjustmentsAsync();
        }
    }

    private async Task ApplyMobilePlaybackAdjustmentsAsync()
    {
        while (true)
        {
            await Task.Delay(33);
            if (_mobileGesturePendingAdjustment is not { } value) return;
            var kind = _mobileGesturePendingAdjustmentKind;
            _mobileGesturePendingAdjustment = null;
            var playback = _host.Shell.Playback;
            if (kind == PlaybackGestureKind.Brightness)
            {
                await RunAsync(playback.SetBrightnessAsync(value));
            }
            else if (kind == PlaybackGestureKind.Volume)
            {
                await RunAsync(playback.SetVolumeAsync(value));
            }
            if (_mobileGesturePendingAdjustment is null) return;
        }
    }

    private void CancelMobilePlaybackGesture(bool clearPendingAdjustment = true)
    {
        var pointer = _mobileGesturePointer;
        _mobileGesturePointer = null;
        _mobileGestureStart = null;
        _mobileGestureHadMultiplePointers = false;
        _mobileGestureKind = PlaybackGestureKind.None;
        if (clearPendingAdjustment) _mobileGesturePendingAdjustment = null;
        pointer?.Capture(null);
        SetMobilePlaybackFeedback(string.Empty);
    }

    private void RevealMobilePlayerChrome(bool autoHide = true)
    {
        if (_mobilePlayerChrome is null)
        {
            return;
        }

        _mobilePlayerChrome.IsVisible = true;
        CancelMobilePlayerChromeHide();
        if (!autoHide || _mobilePlayerOptions?.IsVisible == true)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _mobilePlayerChromeHide = cancellation;
        _ = HideMobilePlayerChromeAsync(cancellation);
    }

    private void ToggleMobilePlayerChrome()
    {
        if (_mobilePlayerChrome is not { } chrome) return;
        if (chrome.IsVisible)
        {
            CancelMobilePlayerChromeHide();
            chrome.IsVisible = false;
            if (_mobilePlayerOptions is not null) _mobilePlayerOptions.IsVisible = false;
        }
        else
        {
            RevealMobilePlayerChrome();
        }
    }

    private async Task HideMobilePlayerChromeAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_mobilePlayerChromeHide, cancellation) &&
                    _mobilePlayerOptions?.IsVisible != true &&
                    _mobilePlayerChrome is not null)
                {
                    _mobilePlayerChrome.IsVisible = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_mobilePlayerChromeHide, cancellation))
            {
                _mobilePlayerChromeHide = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelMobilePlayerChromeHide()
    {
        var cancellation = _mobilePlayerChromeHide;
        _mobilePlayerChromeHide = null;
        cancellation?.Cancel();
    }

    private void SetMobilePlaybackFeedback(string message)
    {
        if (_mobilePlayerFeedback is not null)
        {
            _mobilePlayerFeedback.Text = message;
            _mobilePlayerFeedback.IsVisible = !string.IsNullOrWhiteSpace(message);
        }
        if (!string.IsNullOrWhiteSpace(message))
        {
            SetShellStatus(message);
        }
    }
}
