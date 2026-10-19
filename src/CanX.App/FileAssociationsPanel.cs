// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CanX.App;

internal sealed class FileAssociationsPanel : StackPanel
{
    private readonly IFileAssociationPlatform _service;
    private readonly List<AssociationRow> _rows = [];
    private readonly Button _setAllButton;
    private readonly Button _refreshButton;
    private readonly Button? _removeButton;
    private readonly TextBlock _status;
    private bool _busy;

    public FileAssociationsPanel(IFileAssociationPlatform service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Spacing = 12;
        Margin = new Thickness(0, 14, 0, 0);

        var title = new TextBlock { Text = "文件关联", FontSize = 22, FontWeight = FontWeight.SemiBold };
        AutomationProperties.SetHeadingLevel(title, 2);
        Children.Add(title);
        Children.Add(new Separator());

        var content = new StackPanel { Spacing = 10 };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        toolbar.Children.Add(new TextBlock
        {
            Text = "支持的格式与当前默认应用",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
        });
        _refreshButton = new Button { Content = "刷新", Margin = new Thickness(8, 0, 0, 0) };
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        Grid.SetColumn(_refreshButton, 1);
        toolbar.Children.Add(_refreshButton);
        _setAllButton = new Button { Content = _service.SetAllButtonText, Margin = new Thickness(8, 0, 0, 0) };
        _setAllButton.Classes.Add("accent");
        _setAllButton.Click += async (_, _) => await SetDefaultsAsync(_rows.Select(row => row.Extension));
        Grid.SetColumn(_setAllButton, 3);
        toolbar.Children.Add(_setAllButton);
        if (_service.RemoveButtonText is { } removeText)
        {
            _removeButton = new Button { Content = removeText, Margin = new Thickness(8, 0, 0, 0) };
            _removeButton.Click += async (_, _) => await RemoveRegistrationAsync();
            Grid.SetColumn(_removeButton, 2);
            toolbar.Children.Add(_removeButton);
        }
        content.Children.Add(toolbar);

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        content.Children.Add(_status);
        if (_service.UnavailableReason is { } reason)
        {
            _status.Text = reason;
            _status.IsVisible = true;
        }

        foreach (var (name, extensions) in CanXFileFormats.Groups)
        {
            var group = new StackPanel { Spacing = 0 };
            foreach (var extension in extensions)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("100,*,Auto"),
                    Margin = new Thickness(0, 0, 0, 1),
                    MinHeight = 38,
                };
                row.Children.Add(new TextBlock
                {
                    Text = extension,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var current = new TextBlock
                {
                    Text = "读取中…",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 0, 8, 0),
                };
                Grid.SetColumn(current, 1);
                row.Children.Add(current);
                var setButton = new Button
                {
                    Content = _service.SetButtonText,
                    MinWidth = 95,
                    IsEnabled = _service.CanChange,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var selectedExtension = extension;
                setButton.Click += async (_, _) => await SetDefaultsAsync([selectedExtension]);
                Grid.SetColumn(setButton, 2);
                row.Children.Add(setButton);
                group.Children.Add(row);
                _rows.Add(new AssociationRow(extension, current, setButton));
            }
            content.Children.Add(new Expander
            {
                Header = $"{name}  ·  {extensions.Length}",
                Content = group,
                IsExpanded = false,
            });
        }

        _setAllButton.IsEnabled = _service.CanChange;
        var panel = new Border { Padding = new Thickness(14), Child = content };
        panel.Classes.Add("panel");
        Children.Add(panel);
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            await LoadStatesAsync();
        }
        catch (Exception exception)
        {
            ShowStatus($"读取文件关联失败：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SetDefaultsAsync(IEnumerable<string> extensions)
    {
        if (_busy || !_service.CanChange) return;
        var selected = extensions.ToArray();
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => _service.SetDefaults(selected));
            await LoadStatesAsync();
            ShowStatus(result.Failed.Count > 0
                ? $"未能设置：{string.Join("、", result.Failed)}"
                : result.Message ?? (selected.Length == 1 ? $"已将 {selected[0]} 设为 CanX。" : "已更新文件关联。"));
        }
        catch (Exception exception)
        {
            ShowStatus($"设置文件关联失败：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RemoveRegistrationAsync()
    {
        if (_busy || !_service.CanChange) return;
        SetBusy(true);
        try
        {
            var message = await Task.Run(_service.RemoveRegistration);
            await LoadStatesAsync();
            ShowStatus(message);
        }
        catch (Exception exception)
        {
            ShowStatus($"移除文件关联失败：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadStatesAsync()
    {
        var states = await Task.Run(() =>
            _service.ReadCurrentApplications(_rows.Select(row => row.Extension).ToArray()));
        foreach (var row in _rows)
        {
            var state = states[row.Extension];
            row.Current.Text = state.ApplicationName;
            row.SetButton.Content = state.IsCanX ? "已关联" : _service.SetButtonText;
            row.SetButton.IsEnabled = _service.CanChange && !state.IsCanX;
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        _refreshButton.IsEnabled = !value;
        _setAllButton.IsEnabled = !value && _service.CanChange;
        if (_removeButton is not null)
            _removeButton.IsEnabled = !value && _service.CanChange;
        foreach (var row in _rows)
            row.SetButton.IsEnabled = !value && _service.CanChange && row.SetButton.Content?.ToString() != "已关联";
    }

    private void ShowStatus(string value)
    {
        _status.Text = value;
        _status.IsVisible = true;
    }

    private sealed record AssociationRow(string Extension, TextBlock Current, Button SetButton);
}
