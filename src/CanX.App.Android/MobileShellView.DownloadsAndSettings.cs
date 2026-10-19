// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CanX.App.Controls;
using CanX.App.ViewModels;
using CanX.Core.Downloads;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
using Orientation = Avalonia.Layout.Orientation;
using ProgressBar = Avalonia.Controls.ProgressBar;
using RadioButton = Avalonia.Controls.RadioButton;

namespace CanX.App.Android;

internal sealed partial class MobileShellView
{
    private Control BuildFullDownloadsPage()
    {
        var viewModel = _host.Shell.DownloadCenter;
        var list = new ListBox
        {
            ItemsSource = viewModel.Tasks,
            ItemTemplate = MobileTemplate<DownloadTaskRowViewModel>(DownloadRow),
            MaxHeight = 520,
        };
        list.SelectionChanged += (_, _) => viewModel.SelectedTask = list.SelectedItem as DownloadTaskRowViewModel;
        AttachMobileListActions<DownloadTaskRowViewModel>(
            list,
            item => viewModel.SelectedTask = item,
            _ =>
            {
                if (viewModel.CanPlaySelectedTask) return _host.PlaySelectedDownloadAsync();
                ShowDownloadTaskOptions();
                return Task.CompletedTask;
            },
            (item, anchor) => ShowMobileItemMenu(anchor,
                new MobileItemAction("边下边播", () => _host.PlaySelectedDownloadAsync(),
                    viewModel.CanPlaySelectedTask),
                new MobileItemAction("暂停", () =>
                {
                    viewModel.PauseCommand.Execute(null);
                    return Task.CompletedTask;
                }, viewModel.PauseCommand.CanExecute(null)),
                new MobileItemAction("继续 / 重试", () =>
                {
                    viewModel.ResumeCommand.Execute(null);
                    return Task.CompletedTask;
                }, viewModel.ResumeCommand.CanExecute(null)),
                new MobileItemAction("上移", () =>
                {
                    viewModel.MoveUpCommand.Execute(null);
                    return Task.CompletedTask;
                }, viewModel.MoveUpCommand.CanExecute(null)),
                new MobileItemAction("下移", () =>
                {
                    viewModel.MoveDownCommand.Execute(null);
                    return Task.CompletedTask;
                }, viewModel.MoveDownCommand.CanExecute(null)),
                new MobileItemAction("任务选项", () =>
                {
                    ShowDownloadTaskOptions();
                    return Task.CompletedTask;
                }),
                new MobileItemAction("删除任务", () =>
                {
                    ShowRemoveDownloadConfirmation();
                    return Task.CompletedTask;
                }, viewModel.CanRemoveSelectedTask, IsDanger: true)));

        var add = AccentButton("添加任务", "CanXIconAdd");
        add.Click += (_, _) => ShowFullAddDownload();

        var downloading = new RadioButton { Content = "下载中", IsChecked = viewModel.ShowDownloadingTasks };
        var completed = new RadioButton { Content = "已完成", IsChecked = viewModel.ShowCompletedTasks };
        downloading.Click += (_, _) => viewModel.ShowDownloadingTasks = true;
        completed.Click += (_, _) => viewModel.ShowCompletedTasks = true;

        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            summary.Text = $"{viewModel.StatusMessage}  {viewModel.GlobalSummary}".Trim();
            downloading.IsChecked = viewModel.ShowDownloadingTasks;
            completed.IsChecked = viewModel.ShowCompletedTasks;
        }
        viewModel.PropertyChanged += Refresh;
        Refresh();

        return Page(
            "下载中心",
            string.Empty,
            Horizontal(downloading, completed),
            add,
            list,
            summary,
            new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "downloads.details" },
            new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "downloads.status" },
            new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "menu.downloads" });
    }

    private void ShowRemoveDownloadConfirmation()
    {
        var viewModel = _host.Shell.DownloadCenter;
        var deleteFiles = new CheckBox { Content = "同时删除本地文件" };
        var confirm = new Button { Content = "删除任务" };
        confirm.Classes.Add("danger");
        confirm.Click += async (_, _) =>
        {
            try
            {
                await viewModel.RemoveSelectedAsync(deleteFiles.IsChecked == true);
                CloseModal();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        };
        ShowModal(
            "确认删除",
            new TextBlock
            {
                Text = $"确定删除任务“{viewModel.SelectedTask?.Name}”吗？",
                TextWrapping = TextWrapping.Wrap,
            },
            deleteFiles,
            MobileActions(confirm, CancelButton()));
    }

    private void ShowFullAddDownload()
    {
        var viewModel = _host.Shell.DownloadCenter.CreateAddTaskViewModel();
        string? temporarySource = null;
        var content = new StackPanel { Spacing = 10 };
        var source = new TextBox
        {
            PlaceholderText = "URL、Magnet 或 Torrent / Metalink 文件",
            Text = viewModel.SourceText,
        };
        source.TextChanged += (_, _) => viewModel.SourceText = source.Text ?? string.Empty;
        var destination = new TextBox { Text = viewModel.DestinationDirectory, PlaceholderText = "下载目录" };
        destination.TextChanged += (_, _) => viewModel.DestinationDirectory = destination.Text ?? string.Empty;
        var destinationField = Labeled("保存到", destination);
        var output = new TextBox { PlaceholderText = "输出文件名" };
        output.TextChanged += (_, _) => viewModel.OutputName = output.Text ?? string.Empty;
        var outputField = Labeled("输出文件名", output);
        var browse = new Button { Content = "浏览 Torrent / Metalink" };
        var chooseDestination = new Button { Content = "选择下载目录" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        var duplicate = AccentButton("仍然添加重复任务");
        duplicate.IsVisible = false;
        var back = new Button { Content = "上一步", IsVisible = false };
        var action = AccentButton("确定");

        async Task CompleteAsync(bool allowDuplicate)
        {
            var committed = viewModel.CanCommitPendingMetadata
                ? await viewModel.CommitPendingMetadataAsync(allowDuplicate)
                : await viewModel.AddAsync(allowDuplicate);
            if (!committed) return;
            viewModel.PropertyChanged -= OnChanged;
            await viewModel.DisposeAsync();
            DeleteTemporaryFile(temporarySource);
            CloseModal();
        }

        async Task ExecuteAsync(bool allowDuplicate = false)
        {
            error.Text = string.Empty;
            duplicate.IsVisible = false;
            try
            {
                if (viewModel.CanCommitPendingMetadata)
                {
                    await CompleteAsync(allowDuplicate);
                }
                else if (viewModel.IsSourceStep)
                {
                    await viewModel.PrepareAsync();
                    Render();
                }
                else
                {
                    await CompleteAsync(allowDuplicate);
                }
            }
            catch (DuplicateDownloadException exception) when (!allowDuplicate)
            {
                error.Text = $"任务“{exception.ExistingDisplayName}”已经存在。";
                duplicate.IsVisible = true;
            }
            catch (Exception exception)
            {
                error.Text = exception.Message;
            }
        }

        void Render()
        {
            content.Children.Clear();
            if (viewModel.IsSourceStep)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "输入下载地址、Magnet 链接，或选择 Torrent / Metalink 文件。",
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(source);
                content.Children.Add(browse);
                if (!string.IsNullOrWhiteSpace(viewModel.BusyText))
                {
                    content.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5, MinWidth = 0 });
                    content.Children.Add(new TextBlock { Text = viewModel.BusyText, TextWrapping = TextWrapping.Wrap });
                }
                action.Content = viewModel.CanCommitPendingMetadata ? "立即添加" : "确定";
                back.IsVisible = false;
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"{viewModel.DisplayName}\n{viewModel.SourceType} · {viewModel.TotalSize}",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeight.SemiBold,
                });
                content.Children.Add(destinationField);
                content.Children.Add(chooseDestination);
                if (viewModel.ShowOutputName)
                {
                    if (output.Text != viewModel.OutputName) output.Text = viewModel.OutputName;
                    content.Children.Add(outputField);
                }
                if (viewModel.ShowFileList)
                {
                    var selectAll = new CheckBox { Content = "全选", IsChecked = viewModel.AllFilesSelected };
                    selectAll.Click += (_, _) => viewModel.AllFilesSelected = selectAll.IsChecked == true;
                    content.Children.Add(selectAll);
                    var files = new StackPanel { Spacing = 7 };
                    foreach (var file in viewModel.Files)
                    {
                        var selected = new CheckBox
                        {
                            Content = $"{file.Path}  ·  {file.Size}",
                            IsChecked = file.IsSelected,
                        };
                        selected.Click += (_, _) => file.IsSelected = selected.IsChecked == true;
                        var priority = new ComboBox
                        {
                            ItemsSource = file.Priorities,
                            SelectedItem = file.Priority,
                        };
                        priority.SelectionChanged += (_, _) =>
                        {
                            if (priority.SelectedItem is string value) file.Priority = value;
                        };
                        files.Children.Add(new StackPanel
                        {
                            Spacing = 4,
                            Children = { selected, Labeled("优先级", priority) },
                        });
                    }
                    content.Children.Add(MobileSection("文件内容", files));
                }
                action.Content = "开始下载";
                back.IsVisible = true;
            }
            content.Children.Add(error);
            content.Children.Add(duplicate);
            action.IsEnabled = viewModel.CanContinue || viewModel.CanAdd;
        }

        void OnChanged(object? sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName is nameof(AddDownloadTaskViewModel.IsBusy)
                or nameof(AddDownloadTaskViewModel.BusyText)
                or nameof(AddDownloadTaskViewModel.CanCommitPendingMetadata)
                or nameof(AddDownloadTaskViewModel.IsSourceStep)
                or nameof(AddDownloadTaskViewModel.IsDetailsStep))
            {
                Render();
                return;
            }
            if (eventArgs.PropertyName is nameof(AddDownloadTaskViewModel.CanContinue)
                or nameof(AddDownloadTaskViewModel.CanAdd))
            {
                action.IsEnabled = viewModel.CanContinue || viewModel.CanAdd;
            }
        }

        viewModel.PropertyChanged += OnChanged;
        browse.Click += async (_, _) =>
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.StorageProvider is null) return;
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "选择 Torrent 或 Metalink 文件",
                    FileTypeFilter =
                    [
                        new FilePickerFileType("下载元数据")
                        {
                            Patterns = ["*.torrent", "*.metalink", "*.meta4"],
                            MimeTypes = ["application/x-bittorrent", "application/metalink+xml", "application/metalink4+xml"],
                        },
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
                else
                {
                    file.Dispose();
                }
                source.Text = temporarySource;
            }
            catch (Exception exception)
            {
                error.Text = exception.Message;
            }
        };
        chooseDestination.Click += async (_, _) => await PickDownloadDirectoryAsync(destination);
        back.Click += async (_, _) =>
        {
            await viewModel.BackAsync();
            Render();
        };
        action.Click += async (_, _) => await ExecuteAsync();
        duplicate.Click += async (_, _) => await ExecuteAsync(allowDuplicate: true);
        Render();
        ShowModal(
            "添加下载任务",
            content,
            MobileActions(action, back, CancelButton(async () =>
            {
                viewModel.PropertyChanged -= OnChanged;
                await viewModel.DisposeAsync();
                DeleteTemporaryFile(temporarySource);
            })));
    }

    private void ShowDownloadTaskOptions()
    {
        var viewModel = _host.Shell.DownloadCenter;
        if (viewModel.SelectedTask is null) return;

        var content = new StackPanel { Spacing = 10 };
        var selectedDetails = new TextBlock { Text = viewModel.SelectedDetails, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(selectedDetails);
        content.Children.Add(MobileSection(
            "常用选项",
            BuildDownloadCommonOptionList(viewModel.TaskCommonOptions),
            CommandButton("应用常用选项", viewModel.ApplyCommonTaskOptionsCommand)));

        var advanced = MultilineTextBox(viewModel.TaskOptionsText, "每行一个 aria2 key=value", 150);
        advanced.TextChanged += (_, _) => viewModel.TaskOptionsText = advanced.Text ?? string.Empty;
        content.Children.Add(MobileSection(
            "高级选项",
            advanced,
            CommandButton("应用高级选项", viewModel.ApplyTaskOptionsCommand)));

        var pieceProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            MinWidth = 0,
            Value = viewModel.SelectedTask.Progress,
            Height = 10,
        };
        var pieceMap = new DownloadPieceMap
        {
            Height = 90,
            PieceCount = viewModel.SelectedTask.Snapshot.PieceCount,
            Bitfield = viewModel.SelectedTask.Snapshot.Bitfield,
            CompletedBrush = Brush("CanXAccentBrush"),
            PartialBrush = Brush("CanXMutedTextBrush"),
            MissingBrush = Brush("CanXSubtleBrush"),
        };
        content.Children.Add(MobileSection(
            "分片进度",
            new TextBlock { Text = viewModel.SelectedTask.PieceSummary, TextWrapping = TextWrapping.Wrap },
            pieceProgress,
            pieceMap,
            new TextBlock { Text = viewModel.SelectedTask.PieceDetails, TextWrapping = TextWrapping.Wrap }));

        var files = new ListBox
        {
            ItemsSource = viewModel.SelectedFiles,
            MaxHeight = 240,
            ItemTemplate = MobileTemplate<DownloadFileSnapshot>(file => new StackPanel
            {
                Margin = new Thickness(2, 5),
                Children =
                {
                    new TextBlock { Text = $"{file.Index}. {file.Path}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"{file.ProgressPercent:0.0}% · {file.CompletedLength:N0}/{file.Length:N0} B", Opacity = 0.72 },
                },
            }),
        };
        var fileSelection = new TextBox { Text = viewModel.FileSelectionText, PlaceholderText = "文件序号，例如 1,3-5" };
        fileSelection.TextChanged += (_, _) => viewModel.FileSelectionText = fileSelection.Text ?? string.Empty;
        content.Children.Add(MobileSection(
            "文件",
            files,
            fileSelection,
            CommandButton("应用文件选择", viewModel.ApplyFileSelectionCommand)));
        var trackerList = MultilineTextBox(viewModel.TrackerSummary, string.Empty, 140);
        trackerList.IsReadOnly = true;
        content.Children.Add(MobileSection("Tracker", trackerList));

        var notify = new CheckBox { Content = "完成时通知", IsChecked = viewModel.SelectedNotifyOnCompletion };
        notify.Click += (_, _) => viewModel.SelectedNotifyOnCompletion = notify.IsChecked == true;
        var completion = new ComboBox
        {
            ItemsSource = viewModel.CompletionActions,
            SelectedItem = viewModel.SelectedCompletionAction,
        };
        completion.SelectionChanged += (_, _) =>
        {
            if (completion.SelectedItem is DownloadCompletionAction action) viewModel.SelectedCompletionAction = action;
        };
        var schedule = new TextBox
        {
            Text = viewModel.SelectedScheduledStartText,
            PlaceholderText = "计划时间，例如 2026-09-15 22:30；留空清除",
        };
        schedule.TextChanged += (_, _) => viewModel.SelectedScheduledStartText = schedule.Text ?? string.Empty;
        content.Children.Add(MobileSection(
            "完成与计划",
            notify,
            Labeled("完成动作", completion),
            CommandButton("应用完成策略", viewModel.ApplyCompletionPolicyCommand),
            schedule,
            CommandButton("应用计划", viewModel.ApplyScheduleCommand)));

        var peers = new ListBox
        {
            ItemsSource = viewModel.Peers,
            MaxHeight = 160,
            ItemTemplate = MobileTemplate<DownloadPeerSnapshot>(peer =>
                new TextBlock { Text = $"{peer.Ip}:{peer.Port}  ↓ {peer.DownloadSpeed}  ↑ {peer.UploadSpeed}" }),
        };
        var servers = new ListBox
        {
            ItemsSource = viewModel.Servers,
            MaxHeight = 160,
            ItemTemplate = MobileTemplate<DownloadServerSnapshot>(server =>
                new TextBlock { Text = $"#{server.FileIndex} {server.DownloadSpeed}  {server.CurrentUri}", TextWrapping = TextWrapping.Wrap }),
        };
        content.Children.Add(MobileSection(
            "连接与诊断",
            new TextBlock { Text = viewModel.SelectedDiagnosticSummary, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = viewModel.SelectedErrorSummary, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed },
            CommandButton("刷新连接", viewModel.RefreshConnectionsCommand),
            Heading("Peer"),
            peers,
            Heading("服务器"),
            servers));

        var search = new TextBox { Text = viewModel.OptionSearchText, PlaceholderText = "搜索可修改的任务选项" };
        var definitions = new ListBox
        {
            ItemsSource = viewModel.TaskOptionDefinitions,
            MaxHeight = 220,
            ItemTemplate = MobileTemplate<DownloadOptionDefinition>(option => new StackPanel
            {
                Margin = new Thickness(2, 5),
                Children =
                {
                    new TextBlock { Text = option.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{option.PossibleValues} · 默认 {option.DefaultValue}", TextWrapping = TextWrapping.Wrap, Opacity = 0.72 },
                },
            }),
        };
        var definitionDetails = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var definitionValue = new TextBox { PlaceholderText = "选项值" };
        search.TextChanged += (_, _) => viewModel.OptionSearchText = search.Text ?? string.Empty;
        definitions.SelectionChanged += (_, _) =>
        {
            viewModel.SelectedOptionDefinition = definitions.SelectedItem as DownloadOptionDefinition;
            definitionDetails.Text = viewModel.SelectedOptionDetails;
            definitionValue.Text = viewModel.SelectedOptionValue;
        };
        definitionValue.TextChanged += (_, _) => viewModel.SelectedOptionValue = definitionValue.Text ?? string.Empty;
        content.Children.Add(MobileSection(
            "全部任务选项",
            new TextBlock { Text = viewModel.CapabilitySummary, TextWrapping = TextWrapping.Wrap },
            search,
            definitions,
            definitionDetails,
            definitionValue,
            CommandButton("应用所选项", viewModel.ApplySelectedTaskOptionCommand)));

        ShowModal(
            $"任务选项 · {viewModel.SelectedTask.Name}",
            content,
            new Button { Content = "关闭", Command = null }.Tap(button => button.Click += (_, _) => CloseModal()));
    }

    private Control BuildFullSettingsPage()
    {
        var settings = _host.Shell.Settings;
        var downloads = _host.Shell.DownloadCenter;
        var mpv = settings.Mpv;
        var agent = settings.Agent;
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(14, 8, 14, 18) };

        var theme = Choice(settings.ThemeOptions, settings.SelectedTheme, value => settings.SelectedTheme = value);
        var logging = Bool("启用日志记录", settings.IsLoggingEnabled, value => settings.IsLoggingEnabled = value);
        stack.Children.Add(MobileSection(
            "通用",
            Labeled("主题", theme),
            logging,
            new TextBlock { Text = $"日志目录：{settings.LogDirectory}", TextWrapping = TextWrapping.Wrap, Opacity = 0.72 }));

        var baseUrl = TextSetting(agent.BaseUrl, value => agent.BaseUrl = value, "https://gateway.example/v1");
        var apiKey = new TextBox { PasswordChar = '●', PlaceholderText = "留空则使用已保存的 Key" };
        apiKey.TextChanged += (_, _) => agent.ApiKeyInput = apiKey.Text ?? string.Empty;
        var model = new ComboBox
        {
            IsEditable = true,
            PlaceholderText = "测试后选择，或输入模型 ID",
            ItemsSource = agent.ModelIds,
            Text = agent.Model,
        };
        model.PropertyChanged += (_, args) =>
        {
            if (args.Property == ComboBox.TextProperty) agent.Model = model.Text ?? string.Empty;
        };
        model.SelectionChanged += (_, _) => agent.SelectedModel = model.SelectedItem as string;
        var context = IntSetting(agent.ContextWindowTokens, value => agent.ContextWindowTokens = value);
        var connectionStatus = new TextBlock { Text = agent.ConnectionStatus, TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        var credentialStatus = new TextBlock { Text = agent.CredentialStatus, TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        var temperature = TextSetting(agent.Temperature.ToString(CultureInfo.InvariantCulture), value =>
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) agent.Temperature = parsed;
        });
        PropertyChangedEventHandler onAgentChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(AgentSettingsViewModel.Model) && model.Text != agent.Model)
                model.Text = agent.Model;
            if (args.PropertyName == nameof(AgentSettingsViewModel.ApiKeyInput) && apiKey.Text != agent.ApiKeyInput)
                apiKey.Text = agent.ApiKeyInput;
            if (args.PropertyName == nameof(AgentSettingsViewModel.ContextWindowTokens))
                context.Text = agent.ContextWindowTokens.ToString(CultureInfo.InvariantCulture);
            if (args.PropertyName == nameof(AgentSettingsViewModel.Temperature))
                temperature.Text = agent.Temperature.ToString(CultureInfo.InvariantCulture);
            if (args.PropertyName == nameof(AgentSettingsViewModel.ConnectionStatus))
                connectionStatus.Text = agent.ConnectionStatus;
            if (args.PropertyName == nameof(AgentSettingsViewModel.CredentialStatus))
                credentialStatus.Text = agent.CredentialStatus;
        };
        var duration = IntSetting(agent.MaximumDurationSeconds, value => agent.MaximumDurationSeconds = value);
        var timeout = IntSetting(agent.ToolTimeoutSeconds, value => agent.ToolTimeoutSeconds = value);
        stack.Children.Add(MobileSection(
            "Agent",
            Labeled("API 地址", baseUrl),
            Labeled("API Key", apiKey),
            MobileActions(
                CommandButton("测试并获取模型", agent.TestConnectionCommand),
                CommandButton("删除 Key", agent.DeleteApiKeyCommand)),
            connectionStatus,
            credentialStatus,
            Labeled("模型", model),
            Labeled("上下文大小（tokens）", context),
            Labeled("Temperature", temperature),
            Labeled("总时限（秒）", duration),
            Labeled("Tool 时限（秒）", timeout),
            Bool("显示工具调用记录", agent.ShowToolCalls, value => agent.ShowToolCalls = value),
            Bool("自动批准工具调用（YOLO 模式）", agent.AutoApproveToolCalls, value => agent.AutoApproveToolCalls = value),
            new TextBlock
            {
                Text = "API Key 随设置一起保存；留空保留已保存的 Key。YOLO 模式从下次发送消息起生效，工具调用无需人工确认，包括写入和删除操作。",
                TextWrapping = TextWrapping.Wrap,
            }));

        var directory = TextSetting(settings.DownloadDirectory, value => settings.DownloadDirectory = value, "默认下载目录");
        var browseDirectory = new Button { Content = "浏览下载目录" };
        browseDirectory.Click += async (_, _) => await PickDownloadDirectoryAsync(directory);
        stack.Children.Add(MobileSection(
            "下载 · 常用",
            Labeled("默认下载目录", directory),
            browseDirectory,
            BuildDownloadCommonOptionList(downloads.GlobalCommonOptions),
            CommandButton("立即应用常用选项", downloads.ApplyCommonGlobalOptionsCommand)));

        var globalText = MultilineTextBox(downloads.GlobalOptionsText, "每行一个 aria2 key=value", 170);
        globalText.TextChanged += (_, _) => downloads.GlobalOptionsText = globalText.Text ?? string.Empty;
        stack.Children.Add(MobileSection(
            "下载 · 高级文本",
            globalText,
            CommandButton("立即应用高级选项", downloads.ApplyGlobalOptionsCommand)));
        stack.Children.Add(BuildAllAria2Options(downloads));

        stack.Children.Add(MobileSection(
            "播放 · 解码",
            Labeled("硬件解码", Choice(mpv.HardwareDecodingOptions, mpv.SelectedHardwareDecoding, value => mpv.SelectedHardwareDecoding = value)),
            Labeled("音视频同步", Choice(mpv.VideoSyncOptions, mpv.SelectedVideoSync, value => mpv.SelectedVideoSync = value)),
            Labeled("反交错", Choice(mpv.DeinterlaceOptions, mpv.SelectedDeinterlace, value => mpv.SelectedDeinterlace = value)),
            Bool("退出时写入 watch-later", mpv.SavePositionOnQuit, value => mpv.SavePositionOnQuit = value),
            Bool("自动恢复最近播放位置", mpv.ResumePlayback, value => mpv.ResumePlayback = value),
            Labeled("视频播放完毕", Choice(mpv.PlaybackEndActionOptions, mpv.SelectedPlaybackEndAction,
                value => mpv.SelectedPlaybackEndAction = value))));
        stack.Children.Add(MobileSection(
            "播放 · 画面处理",
            Labeled("亮度缩放", Choice(mpv.ScaleOptions, mpv.SelectedScale, value => mpv.SelectedScale = value)),
            Labeled("色度缩放", Choice(mpv.ChromaScaleOptions, mpv.SelectedChromaScale, value => mpv.SelectedChromaScale = value)),
            Labeled("缩小算法", Choice(mpv.ScaleOptions, mpv.SelectedDownscale, value => mpv.SelectedDownscale = value)),
            Bool("帧插值", mpv.Interpolation, value => mpv.Interpolation = value),
            Bool("去色带", mpv.Deband, value => mpv.Deband = value),
            Bool("Sigmoid 放大", mpv.SigmoidUpscaling, value => mpv.SigmoidUpscaling = value)));
        stack.Children.Add(MobileSection(
            "播放 · 色彩与 HDR",
            Labeled("输出色彩空间提示", Choice(mpv.TargetColorspaceHintOptions, mpv.SelectedTargetColorspaceHint, value => mpv.SelectedTargetColorspaceHint = value)),
            Labeled("提示来源", Choice(mpv.TargetColorspaceHintModeOptions, mpv.SelectedTargetColorspaceHintMode, value => mpv.SelectedTargetColorspaceHintMode = value)),
            Labeled("目标色域", Choice(mpv.TargetPrimariesOptions, mpv.SelectedTargetPrimaries, value => mpv.SelectedTargetPrimaries = value)),
            Labeled("目标传递函数", Choice(mpv.TargetTransferOptions, mpv.SelectedTargetTransfer, value => mpv.SelectedTargetTransfer = value)),
            Labeled("目标峰值亮度", TextSetting(mpv.TargetPeak, value => mpv.TargetPeak = value, "auto")),
            Labeled("Tone mapping", Choice(mpv.ToneMappingOptions, mpv.SelectedToneMapping, value => mpv.SelectedToneMapping = value)),
            Labeled("色域映射", Choice(mpv.GamutMappingOptions, mpv.SelectedGamutMappingMode, value => mpv.SelectedGamutMappingMode = value)),
            Labeled("动态 HDR 峰值分析", Choice(mpv.HdrComputePeakOptions, mpv.SelectedHdrComputePeak, value => mpv.SelectedHdrComputePeak = value))));
        stack.Children.Add(MobileSection(
            "播放 · 音频",
            Labeled("输出设备", TextSetting(mpv.AudioDevice, value => mpv.AudioDevice = value, "auto 或 ao/device")),
            Labeled("声道布局", Choice(mpv.AudioChannelOptions, mpv.SelectedAudioChannels, value => mpv.SelectedAudioChannels = value)),
            Bool("独占输出", mpv.AudioExclusive, value => mpv.AudioExclusive = value),
            Bool("变速音调校正", mpv.AudioPitchCorrection, value => mpv.AudioPitchCorrection = value),
            Labeled("音轨语言优先级", TextSetting(mpv.AudioLanguages, value => mpv.AudioLanguages = value, "zh-CN,zh,en"))));
        stack.Children.Add(MobileSection(
            "播放 · 字幕",
            Labeled("首选字幕语言（从左到右）", TextSetting(mpv.SubtitleLanguages, value => mpv.SubtitleLanguages = value, "zh-CN,zh-TW,en")),
            Labeled("自动加载外部字幕", Choice(mpv.SubtitleAutoOptions, mpv.SelectedSubtitleAuto, value => mpv.SelectedSubtitleAuto = value)),
            Labeled("字体", TextSetting(mpv.SubtitleFont, value => mpv.SubtitleFont = value)),
            Labeled("字号", TextSetting(mpv.SubtitleFontSize, value => mpv.SubtitleFontSize = value)),
            Bool("字幕随窗口缩放", mpv.SubtitleScaleWithWindow, value => mpv.SubtitleScaleWithWindow = value)));
        stack.Children.Add(MobileSection(
            "播放 · 网络与缓存",
            Labeled("网络缓存", Choice(mpv.CacheOptions, mpv.SelectedCache, value => mpv.SelectedCache = value)),
            Labeled("预读时长上限（秒）", TextSetting(mpv.CacheSeconds, value => mpv.CacheSeconds = value)),
            Labeled("向前缓存上限（MiB）", TextSetting(mpv.DemuxerMaxBytesMiB, value => mpv.DemuxerMaxBytesMiB = value)),
            Labeled("向后缓存上限（MiB）", TextSetting(mpv.DemuxerMaxBackBytesMiB, value => mpv.DemuxerMaxBackBytesMiB = value)),
            Bool("缓存耗尽时自动暂停", mpv.CachePause, value => mpv.CachePause = value),
            Bool("播放前先缓冲", mpv.CachePauseInitial, value => mpv.CachePauseInitial = value),
            Labeled("恢复播放缓存（秒）", TextSetting(mpv.CachePauseWaitSeconds, value => mpv.CachePauseWaitSeconds = value)),
            Labeled("网络超时（秒）", TextSetting(mpv.NetworkTimeoutSeconds, value => mpv.NetworkTimeoutSeconds = value))));
        var mpvAdvanced = MultilineTextBox(mpv.AdvancedOptions, "mpv.conf：每行一个 option=value", 220);
        mpvAdvanced.TextChanged += (_, _) => mpv.AdvancedOptions = mpvAdvanced.Text ?? string.Empty;
        stack.Children.Add(MobileSection("播放 · 高级 mpv.conf", mpvAdvanced));
        if (_host.Shell.Plugins is not null)
        {
            stack.Children.Add(new PluginContributionHost
            {
                Registry = _host.Shell.Plugins.UiRegistry,
                Slot = "settings.plugins",
            });
        }

        var save = AccentButton("保存全部设置");
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.Click += (_, _) => settings.SaveCommand.Execute(null);
        stack.Children.Add(save);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        SubscribeWhileAttached(scroll, agent, onAgentChanged);
        return scroll;
    }

    private Control BuildAllAria2Options(DownloadCenterViewModel downloads)
    {
        var search = new TextBox { Text = downloads.OptionSearchText, PlaceholderText = "搜索可全局修改的选项" };
        var definitions = new ListBox
        {
            ItemsSource = downloads.GlobalOptionDefinitions,
            MaxHeight = 260,
            ItemTemplate = MobileTemplate<DownloadOptionDefinition>(option => new StackPanel
            {
                Margin = new Thickness(2, 5),
                Children =
                {
                    new TextBlock { Text = option.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{option.PossibleValues} · 默认 {option.DefaultValue}", TextWrapping = TextWrapping.Wrap, Opacity = 0.72 },
                },
            }),
        };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var value = new TextBox { PlaceholderText = "选项值" };
        search.TextChanged += (_, _) => downloads.OptionSearchText = search.Text ?? string.Empty;
        definitions.SelectionChanged += (_, _) =>
        {
            downloads.SelectedGlobalOptionDefinition = definitions.SelectedItem as DownloadOptionDefinition;
            details.Text = downloads.SelectedGlobalOptionDetails;
            value.Text = downloads.SelectedGlobalOptionValue;
        };
        value.TextChanged += (_, _) => downloads.SelectedGlobalOptionValue = value.Text ?? string.Empty;
        return MobileSection(
            "下载 · 全部 aria2 选项",
            new TextBlock { Text = downloads.CapabilitySummary, TextWrapping = TextWrapping.Wrap },
            search,
            definitions,
            details,
            value,
            CommandButton("应用所选项", downloads.ApplySelectedGlobalOptionCommand));
    }

    private async Task PickDownloadDirectoryAsync(TextBox target)
    {
        try
        {
            await AndroidExternalStorageAccess.EnsureGrantedAsync();
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "选择默认下载目录",
            });
            var folder = folders.FirstOrDefault();
            if (folder is null) return;
            try
            {
                target.Text = AndroidExternalStorageAccess.ResolveLocalFolderPath(folder);
            }
            finally
            {
                folder.Dispose();
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static Control BuildDownloadCommonOptionList(IEnumerable<DownloadCommonOptionViewModel> options)
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var option in options)
        {
            stack.Children.Add(Labeled($"{option.Label} · {option.Name}", BuildDownloadOptionEditor(option)));
        }
        return stack;
    }

    private static Control BuildDownloadOptionEditor(DownloadCommonOptionViewModel option)
    {
        if (option.IsBooleanEditor)
        {
            var check = new CheckBox { IsThreeState = true, IsChecked = option.BooleanValue, Content = option.BooleanStateText };
            check.Click += (_, _) =>
            {
                option.BooleanValue = check.IsChecked;
                check.Content = option.BooleanStateText;
            };
            return check;
        }
        if (option.IsChoiceEditor)
        {
            var combo = new ComboBox { ItemsSource = option.ChoiceItems, SelectedItem = option.SelectedChoice };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is DownloadOptionChoice value) option.SelectedChoice = value;
            };
            return combo;
        }
        var text = new TextBox
        {
            Text = option.Value,
            PlaceholderText = option.Placeholder,
            AcceptsReturn = option.IsMultilineTextEditor,
            TextWrapping = option.IsMultilineTextEditor ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = option.IsMultilineTextEditor ? 90 : 0,
            MaxHeight = option.IsMultilineTextEditor ? 90 : double.PositiveInfinity,
        };
        if (option.IsMultilineTextEditor)
        {
            ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        }
        text.TextChanged += (_, _) => option.Value = text.Text ?? string.Empty;
        return option.IsUnitNumberEditor
            ? Horizontal(text, new TextBlock { Text = option.UnitLabel, VerticalAlignment = VerticalAlignment.Center })
            : text;
    }

    private static Expander MobileSection(string title, params Control[] controls)
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
        foreach (var control in controls) stack.Children.Add(control);
        return new Expander
        {
            Header = title,
            Content = stack,
            MinHeight = 48,
            CornerRadius = new CornerRadius(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brush("CanXPanelBrush"),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    private static Grid MobileActions(params Control[] controls)
    {
        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 8,
            RowSpacing = 8,
        };
        for (var index = 0; index < controls.Length; index++)
        {
            var control = controls[index];
            if (index % 2 == 0) panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(control, index / 2);
            Grid.SetColumn(control, index % 2);
            if (index == controls.Length - 1 && index % 2 == 0) Grid.SetColumnSpan(control, 2);
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (control is Button button)
            {
                button.MinHeight = 42;
            }
            else if (control is CheckBox)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
            }
            panel.Children.Add(control);
        }
        return panel;
    }

    private static Button CommandButton(string text, System.Windows.Input.ICommand command) => new()
    {
        Content = text,
        Command = command,
    };

    private static ComboBox Choice<T>(IEnumerable<T> choices, T selected, Action<T> changed)
    {
        var combo = new ComboBox { ItemsSource = choices, SelectedItem = selected };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is T value) changed(value);
        };
        return combo;
    }

    private static CheckBox Bool(string text, bool value, Action<bool> changed)
    {
        var check = new CheckBox { Content = text, IsChecked = value };
        check.Click += (_, _) => changed(check.IsChecked == true);
        return check;
    }

    private static TextBox TextSetting(string value, Action<string> changed, string? placeholder = null)
    {
        var text = new TextBox { Text = value, PlaceholderText = placeholder };
        text.TextChanged += (_, _) => changed(text.Text ?? string.Empty);
        return text;
    }

    private static TextBox IntSetting(int value, Action<int> changed)
    {
        var text = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture) };
        text.LostFocus += (_, _) =>
        {
            if (int.TryParse(text.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) changed(parsed);
        };
        return text;
    }

    private static TextBox MultilineTextBox(string value, string placeholder, double height) => new()
    {
        Text = value,
        PlaceholderText = placeholder,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        MinHeight = height,
        MaxHeight = height,
        FontFamily = FontFamily.Parse("monospace"),
    };
}

internal static class MobileControlExtensions
{
    public static T Tap<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
