// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using CanX.App.Controls;
using CanX.App.ViewModels;
using CanX.Plugins.Ui;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;

namespace CanX.App.Android;

internal sealed partial class MobileShellView
{
    private bool _openingPluginPage;

    private Control BuildPluginDirectoryPage()
    {
        var plugins = _host.Shell.Plugins;
        var pages = _host.Shell.PluginPages;
        var manage = ActionButton("管理插件", "CanXIconSettings");
        manage.IsEnabled = plugins is not null;
        manage.Click += (_, _) => Navigate("plugin-manager");
        var install = AccentButton("安装插件", "CanXIconAdd");
        install.IsEnabled = plugins is not null;
        install.Click += async (_, _) => await PickAndInstallPluginAsync();

        var empty = new Border
        {
            Padding = new Thickness(18),
            Background = Brush("CanXPanelBrush"),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "暂无插件页面",
                Foreground = Brush("CanXMutedTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        void RefreshEmpty(object? sender = null, NotifyCollectionChangedEventArgs? args = null) =>
            empty.IsVisible = pages.Pages.Count == 0;
        empty.AttachedToVisualTree += (_, _) =>
        {
            pages.Pages.CollectionChanged += RefreshEmpty;
            RefreshEmpty();
        };
        empty.DetachedFromVisualTree += (_, _) => pages.Pages.CollectionChanged -= RefreshEmpty;
        RefreshEmpty();

        var entries = new ItemsControl
        {
            ItemsSource = pages.Pages,
            ItemTemplate = MobileTemplate<PluginPageItemViewModel>(BuildPluginPageEntry),
        };
        return Page(
            "插件",
            string.Empty,
            MobileActions(manage, install),
            Heading("插件页面"),
            empty,
            entries,
            new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "navigation.plugins" },
            new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "command.palette" },
            new PluginContributionHost { Registry = _host.Shell.PluginUi, Slot = "menu.app" });
    }

    private Control BuildPluginPageEntry(PluginPageItemViewModel page)
    {
        var assetIcon = new Image
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
        var builtinIcon = new TextBlock
        {
            FontSize = 21,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new Grid
        {
            Width = 30,
            Height = 30,
            Children = { assetIcon, builtinIcon },
        };
        var title = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var badgeText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("CanXAccentBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 80,
        };
        var badge = new Border
        {
            Padding = new Thickness(6, 3),
            Background = Brush("CanXAccentSoftBrush"),
            Child = badgeText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("30,*,Auto"),
            ColumnSpacing = 10,
            Children = { icon, AtColumn(title, 1), AtColumn(badge, 2) },
        };
        var button = new Button
        {
            Content = content,
            MinHeight = 56,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brush("CanXPanelBrush"),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1),
        };
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            assetIcon.Source = page.AssetIcon;
            assetIcon.IsVisible = page.HasAssetIcon;
            builtinIcon.Text = page.IconGlyph;
            builtinIcon.IsVisible = page.HasBuiltinIcon;
            title.Text = page.Title;
            Avalonia.Automation.AutomationProperties.SetName(button, page.Title);
            badgeText.Text = page.BadgeText;
            badge.IsVisible = page.HasBadge;
        }
        button.AttachedToVisualTree += (_, _) =>
        {
            page.PropertyChanged += Refresh;
            Refresh();
        };
        button.DetachedFromVisualTree += (_, _) => page.PropertyChanged -= Refresh;
        button.Click += async (_, _) => await RunAsync(ShowPluginPageAsync(page));
        Refresh();
        return button;
    }

    private Control BuildFullPluginsPage()
    {
        var plugins = _host.Shell.Plugins;
        if (plugins is null) return Page("插件", "插件运行时未启用");

        var list = new ListBox
        {
            ItemsSource = plugins.Plugins,
            MinHeight = 72,
            MaxHeight = 520,
            ItemTemplate = MobileTemplate<PluginItemViewModel>(item => new StackPanel
            {
                Margin = new Thickness(3, 8),
                Children =
                {
                    new TextBlock { Text = $"{item.Name}  {item.Version}", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{item.Status} · {item.SignatureStatus}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = item.Id, FontFamily = FontFamily.Parse("monospace"), Opacity = 0.72 },
                },
            }),
        };
        list.SelectionChanged += (_, _) => plugins.SelectedPlugin = list.SelectedItem as PluginItemViewModel;
        AttachMobileListActions<PluginItemViewModel>(
            list,
            item => plugins.SelectedPlugin = item,
            onTap: null,
            onHold: (item, anchor) => ShowMobileItemMenu(anchor,
                new MobileItemAction("批准并启用", plugins.EnableSelectedAsync, item.CanEnable),
                new MobileItemAction("停用", plugins.DisableSelectedAsync, item.IsEnabled),
                new MobileItemAction("重载", plugins.ReloadSelectedAsync, item.IsEnabled),
                new MobileItemAction("回滚上一版本", plugins.RollbackSelectedAsync, item.CanRollback),
                new MobileItemAction("配置", ShowPluginConfigurationAsync, item.CanConfigure),
                new MobileItemAction("卸载", () =>
                {
                    ShowPluginUninstallConfirmation();
                    return Task.CompletedTask;
                }, IsDanger: true)));
        var install = AccentButton("安装 / 升级", "CanXIconAdd");
        install.Click += async (_, _) => await PickAndInstallPluginAsync();
        var refresh = ActionButton("刷新", "CanXIconRefresh");
        refresh.Click += (_, _) => plugins.RefreshCommand.Execute(null);
        var developer = new CheckBox { Content = "开发模式", IsChecked = plugins.IsDeveloperMode };
        developer.Click += (_, _) => plugins.IsDeveloperMode = developer.IsChecked == true;

        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var permissionChanges = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        var rollbackDetails = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var console = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily.Parse("monospace"),
            MinHeight = 180,
            MaxHeight = 260,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(console, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(console, ScrollBarVisibility.Auto);
        var refreshConsole = new Button { Content = "刷新控制台" };
        refreshConsole.Click += (_, _) => plugins.RefreshConsoleCommand.Execute(null);
        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            details.Text = plugins.SelectedDetails;
            permissionChanges.Text = plugins.PermissionChanges;
            permissionChanges.IsVisible = plugins.HasPermissionChanges;
            error.Text = plugins.SelectedError;
            rollbackDetails.Text = plugins.SelectedRollbackDetails;
            status.Text = plugins.StatusMessage;
            console.Text = plugins.ConsoleText;
        }
        plugins.PropertyChanged += Refresh;
        Refresh();

        return Page(
            string.Empty,
            string.Empty,
            MobileActions(install, refresh),
            developer,
            list,
            MobileSection("权限", details, permissionChanges),
            MobileSection("运行状态", error, rollbackDetails, status),
            MobileSection("插件调试控制台", refreshConsole, console),
            new PluginContributionHost { Registry = plugins.UiRegistry, Slot = "plugins.footer" });
    }

    private void ShowPluginUninstallConfirmation()
    {
        var plugins = _host.Shell.Plugins;
        if (plugins?.SelectedPlugin is not { } selected) return;
        var confirm = new Button { Content = "卸载插件" };
        confirm.Classes.Add("danger");
        confirm.Click += async (_, _) =>
        {
            await plugins.UninstallSelectedAsync();
            CloseModal();
        };
        ShowModal(
            "确认卸载",
            new TextBlock
            {
                Text = $"确定卸载“{selected.Name}”吗？插件数据和安全凭据不会自动删除。",
                TextWrapping = TextWrapping.Wrap,
            },
            MobileActions(confirm, CancelButton()));
    }

    private async Task ShowPluginConfigurationAsync()
    {
        var plugins = _host.Shell.Plugins;
        if (plugins?.SelectedPlugin is not { CanConfigure: true } selected) return;
        var host = new PluginPageHost
        {
            Registry = plugins.UiRegistry,
            PluginId = selected.Id,
            IsConfiguration = true,
            MinHeight = 260,
        };
        async Task CloseAsync(bool notify)
        {
            try
            {
                if (notify) await host.NotifyConfigurationClosedAsync();
            }
            finally
            {
                plugins.UiRegistry.ConfigurationCloseRequested -= OnCloseRequested;
                host.Dispose();
                CloseModal();
            }
        }
        void OnCloseRequested(object? sender, PluginConfigurationCloseRequestedEventArgs args)
        {
            if (args.PluginId == selected.Id) _ = CloseAsync(notify: false);
        }
        plugins.UiRegistry.ConfigurationCloseRequested += OnCloseRequested;
        var close = new Button { Content = "关闭" };
        close.Click += async (_, _) => await CloseAsync(notify: true);
        ShowModal(selected.ConfigurationTitle ?? "插件配置", host, close);
        _modalBackAction = () => CloseAsync(notify: true);
        await host.NotifyConfigurationOpenedAsync();
    }

    private async Task ShowPluginPageAsync(PluginPageItemViewModel page)
    {
        if (_openingPluginPage || _currentPage != "plugins") return;
        _openingPluginPage = true;
        var pages = _host.Shell.PluginPages;
        try
        {
            if (!await pages.ActivateAsync(page.PluginId)) return;
            if (_currentPage != "plugins" ||
                pages.SelectedPage?.PluginId != page.PluginId)
            {
                if (pages.SelectedPage?.PluginId == page.PluginId)
                    await pages.DeactivateAsync();
                return;
            }
            var host = new PluginPageHost
            {
                Registry = pages.Registry,
                PluginId = page.PluginId,
            };
            var status = new TextBlock
            {
                Margin = new Thickness(14, 0, 14, 10),
                Foreground = Brush("CanXMutedTextBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            void RefreshStatus(object? sender = null, PropertyChangedEventArgs? args = null)
            {
                status.Text = pages.StatusMessage;
                status.IsVisible = pages.HasStatusMessage;
            }
            status.AttachedToVisualTree += (_, _) =>
            {
                pages.PropertyChanged += RefreshStatus;
                RefreshStatus();
            };
            status.DetachedFromVisualTree += (_, _) => pages.PropertyChanged -= RefreshStatus;
            RefreshStatus();
            var panel = new Border
            {
                Margin = new Thickness(14, 12, 14, 8),
                Padding = new Thickness(12),
                Background = Brush("CanXPanelBrush"),
                BorderBrush = Brush("CanXBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = host,
            };
            var content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Children = { panel, status },
            };
            Grid.SetRow(status, 1);
            _activePluginPage = page;
            _activePluginPageHost = host;
            page.PropertyChanged += OnActivePluginPageChanged;
            _currentPage = "plugin-page";
            _pageHost.Content = content;
            UpdateNavigationChrome();
        }
        finally
        {
            _openingPluginPage = false;
        }
    }

    private void LeavePluginPage()
    {
        if (_activePluginPageHost is not { } host) return;
        _activePluginPageHost = null;
        if (_activePluginPage is { } page)
        {
            page.PropertyChanged -= OnActivePluginPageChanged;
            _activePluginPage = null;
        }
        _pageHost.Content = null;
        host.Dispose();
        _ = RunAsync(_host.Shell.PluginPages.DeactivateAsync());
    }

    private void OnActivePluginPageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_currentPage == "plugin-page" &&
            args.PropertyName is nameof(PluginPageItemViewModel.Title) or null)
        {
            _headerTitle.Text = _activePluginPage?.Title ?? "插件页面";
        }
    }

    private void OnActivePluginPageRemoved(object? sender, EventArgs args)
    {
        if (_currentPage == "plugin-page") Navigate("plugins");
    }

    private Control BuildFullAgentPage()
    {
        var agent = _host.Shell.Agent;
        if (agent is null) return Page("Agent", "Agent 运行时未启用");

        var conversations = new ComboBox
        {
            ItemsSource = agent.Conversations,
            SelectedItem = agent.SelectedConversation,
            ItemTemplate = MobileTemplate<AgentConversationItemViewModel>(item =>
            {
                var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                SubscribeWhileAttached(label, item, (_, _) => label.Text = item.Title);
                return label;
            }),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        conversations.SelectionChanged += (_, _) =>
            agent.SelectedConversation = conversations.SelectedItem as AgentConversationItemViewModel;
        var newConversation = new Button { Content = "新会话" };
        newConversation.Click += (_, _) => agent.NewConversationCommand.Execute(null);
        var deleteConversation = new Button { Content = "删除会话" };
        deleteConversation.Click += (_, _) => agent.DeleteConversationCommand.Execute(null);
        var messages = new ListBox
        {
            ItemsSource = agent.VisibleMessages,
            MinHeight = 120,
            MaxHeight = 520,
            ItemTemplate = MobileTemplate<AgentChatMessageViewModel>(item => BuildAgentMessage(item)),
        };
        var approval = new ContentControl();
        var input = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 100,
            MaxHeight = 180,
            PlaceholderText = "搜索、播放、下载、设置或创建/调试插件…",
            Text = agent.InputText,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        input.TextChanged += (_, _) => agent.InputText = input.Text ?? string.Empty;
        var send = AccentButton("发送", "CanXIconSend");
        send.Click += (_, _) => agent.SendCommand.Execute(null);
        var stop = new Button { Content = "停止" };
        stop.Click += (_, _) => agent.StopCommand.Execute(null);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var usage = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };

        void RefreshApproval()
        {
            if (agent.PendingApproval is not { } pending)
            {
                approval.Content = null;
                approval.IsVisible = false;
                return;
            }
            var details = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = $"需要确认：{pending.Risk}", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = pending.ToolName },
                    new TextBlock { Text = pending.Description, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = pending.Arguments, TextWrapping = TextWrapping.Wrap, FontFamily = FontFamily.Parse("monospace") },
                },
            };
            if (pending.TrustedDetails is { } trusted)
            {
                details.Children.Add(new TextBlock { Text = trusted.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                details.Children.Add(new TextBlock { Text = $"权限\n{trusted.Permissions}\n\n检查\n{trusted.Checks}\n\n变化\n{trusted.Changes}\n\n{trusted.Integrity}", TextWrapping = TextWrapping.Wrap });
            }
            var approve = AccentButton("允许一次");
            approve.Click += (_, _) => agent.ApproveCommand.Execute(null);
            var deny = new Button { Content = "拒绝" };
            deny.Click += (_, _) => agent.DenyCommand.Execute(null);
            details.Children.Add(MobileActions(deny, approve));
            approval.Content = new Border
            {
                Padding = new Thickness(10),
                BorderBrush = Brush("CanXBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = details,
            };
            approval.IsVisible = true;
        }

        void Refresh(object? sender = null, PropertyChangedEventArgs? args = null)
        {
            conversations.SelectedItem = agent.SelectedConversation;
            status.Text = agent.StatusMessage;
            usage.Text = $"{agent.UsageText} · {_host.Tools.Descriptors.Count} 个 Tool";
            send.IsEnabled = !agent.IsRunning && !string.IsNullOrWhiteSpace(agent.InputText);
            stop.IsEnabled = agent.IsRunning;
            deleteConversation.IsEnabled = !agent.IsRunning && agent.SelectedConversation is not null;
            if (args?.PropertyName is nameof(AgentViewModel.PendingApproval) or nameof(AgentViewModel.HasPendingApproval) or null)
            {
                RefreshApproval();
            }
        }
        agent.PropertyChanged += Refresh;
        Refresh();

        return Page(
            "Agent",
            "所有用户能力均通过带 JSON Schema、权限和审计的 Tool 调用。",
            Labeled("会话", conversations),
            MobileActions(newConversation, deleteConversation),
            messages,
            approval,
            input,
            MobileActions(stop, send),
            status,
            usage);
    }

    private Control BuildAgentMessage(AgentChatMessageViewModel item)
    {
        var header = new TextBlock
        {
            Text = item.Role,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Control content;
        if (item.IsMarkdown)
        {
            var markdown = new AgentMarkdownViewer();
            SubscribeWhileAttached(markdown, item, (_, _) => markdown.Markdown = item.Content);
            content = markdown;
        }
        else
        {
            var plainText = new AgentSelectableTextBlock();
            SubscribeWhileAttached(plainText, item, (_, _) => plainText.Text = item.Content);
            content = plainText;
        }
        return new Border
        {
            Margin = new Thickness(2, 5),
            Padding = new Thickness(10),
            BorderBrush = Brush("CanXBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 4, Children = { header, content } },
        };
    }
}
