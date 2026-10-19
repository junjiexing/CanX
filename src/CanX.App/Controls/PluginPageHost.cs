// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CanX.Plugins.Ui;

namespace CanX.App.Controls;

public sealed class PluginPageHost : UserControl, IDisposable
{
    private const int MaximumInlineImageBytes = 128 * 1024;
    public static readonly StyledProperty<PluginUiRegistry?> RegistryProperty =
        AvaloniaProperty.Register<PluginPageHost, PluginUiRegistry?>(nameof(Registry));

    public static readonly StyledProperty<string?> PluginIdProperty =
        AvaloniaProperty.Register<PluginPageHost, string?>(nameof(PluginId));

    public static readonly StyledProperty<bool> IsConfigurationProperty =
        AvaloniaProperty.Register<PluginPageHost, bool>(nameof(IsConfiguration));

    private readonly StackPanel root = new() { Spacing = 10 };
    private readonly TextBlock status = new()
    {
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Dictionary<string, Bitmap> imageCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> componentControls = new(StringComparer.Ordinal);
    private CancellationTokenSource imageLoadCancellation = new();
    private PluginUiRegistry? subscribedRegistry;
    private PluginUiPage? currentPage;
    private bool disposed;

    public PluginPageHost()
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(root);
        content.Children.Add(status);
        Content = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    public PluginUiRegistry? Registry
    {
        get => GetValue(RegistryProperty);
        set => SetValue(RegistryProperty, value);
    }

    public string? PluginId
    {
        get => GetValue(PluginIdProperty);
        set => SetValue(PluginIdProperty, value);
    }

    public bool IsConfiguration
    {
        get => GetValue(IsConfigurationProperty);
        set => SetValue(IsConfigurationProperty, value);
    }

    public Task NotifyConfigurationOpenedAsync() => InvokeLifecycleAsync("open");

    public Task NotifyConfigurationClosedAsync() => InvokeLifecycleAsync("close");

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (subscribedRegistry is not null)
        {
            subscribedRegistry.PageChanged -= OnPageChanged;
            subscribedRegistry.ConfigurationChanged -= OnConfigurationChanged;
            subscribedRegistry = null;
        }
        root.Children.Clear();
        imageLoadCancellation.Cancel();
        imageLoadCancellation.Dispose();
        ClearImageCache();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RegistryProperty)
        {
            AttachRegistry();
        }
        else if (change.Property == PluginIdProperty || change.Property == IsConfigurationProperty)
        {
            Render();
        }
    }

    private Control BuildGrid(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var columnCount = Math.Max(1, IntegerProperty(properties, "columns", 1));
        var spacing = Spacing(StringProperty(properties, "spacing"));
        var grid = new Grid
        {
            ColumnSpacing = spacing,
            RowSpacing = spacing,
        };
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        if (!node.TryGetProperty("children", out var children))
        {
            return grid;
        }

        var index = 0;
        foreach (var childNode in children.EnumerateArray())
        {
            var column = LayoutInteger(childNode, "column", index % columnCount);
            var row = LayoutInteger(childNode, "row", index / columnCount);
            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            var child = BuildComponent(page, childNode);
            Grid.SetColumn(child, Math.Clamp(column, 0, columnCount - 1));
            Grid.SetRow(child, Math.Max(0, row));
            Grid.SetColumnSpan(child, Math.Clamp(LayoutInteger(childNode, "columnSpan", 1), 1, columnCount));
            Grid.SetRowSpan(child, Math.Max(1, LayoutInteger(childNode, "rowSpan", 1)));
            ApplyLayout(child, childNode);
            grid.Children.Add(child);
            index++;
        }
        return grid;
    }

    private Control BuildContainerContent(PluginUiPage page, JsonElement node)
    {
        var panel = new StackPanel { Spacing = 10 };
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                panel.Children.Add(BuildComponent(page, child));
            }
        }
        return panel;
    }

    private Control BuildCard(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var content = new StackPanel { Spacing = 10 };
        var title = StringProperty(properties, "title");
        if (!string.IsNullOrEmpty(title))
        {
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
            });
        }
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                content.Children.Add(BuildComponent(page, child));
            }
        }
        var border = new Border
        {
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        border.Classes.Add("panel");
        return border;
    }

    private Control BuildTabs(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var items = new List<TabItem>();
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var tabNode in children.EnumerateArray())
            {
                var tabProperties = tabNode.TryGetProperty("props", out var props) ? props : default;
                items.Add(new TabItem
                {
                    Header = StringProperty(tabProperties, "label"),
                    Content = BuildContainerContent(page, tabNode),
                });
            }
        }
        return new TabControl
        {
            ItemsSource = items,
            SelectedIndex = Math.Clamp(IntegerProperty(properties, "selected", 0), 0, Math.Max(0, items.Count - 1)),
        };
    }

    private Control BuildImage(PluginUiPage page, JsonElement properties)
    {
        var asset = StringProperty(properties, "asset");
        var source = StringProperty(properties, "source");
        var image = new Image
        {
            Source = string.IsNullOrEmpty(source) ? LoadPageImage(page, asset) : LoadInlineImage(source),
            Width = NumberProperty(properties, "width", double.NaN),
            Height = NumberProperty(properties, "height", double.NaN),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (IsRemoteImageSource(source))
        {
            BeginLoadRemoteImage(page, source, bitmap => image.Source = bitmap);
        }
        var alternateText = StringProperty(properties, "alt");
        if (!string.IsNullOrEmpty(alternateText))
        {
            ToolTip.SetTip(image, alternateText);
            Avalonia.Automation.AutomationProperties.SetName(image, alternateText);
        }
        return image;
    }

    private Bitmap? LoadPageImage(PluginUiPage page, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }
        var cacheKey = $"{page.PluginId}:{relativePath}";
        if (imageCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        try
        {
            var rootPath = Path.GetFullPath(page.PluginDirectory);
            var imagePath = Path.GetFullPath(Path.Combine(
                rootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
                ? rootPath
                : rootPath + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!imagePath.StartsWith(prefix, comparison))
            {
                return null;
            }
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = Bitmap.DecodeToWidth(stream, 1024, BitmapInterpolationMode.HighQuality);
            imageCache.Add(cacheKey, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Control BuildAlert(JsonElement properties)
    {
        var content = new StackPanel { Spacing = 4 };
        var title = StringProperty(properties, "title");
        if (!string.IsNullOrEmpty(title))
        {
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        }
        content.Children.Add(new TextBlock
        {
            Text = StringProperty(properties, "message"),
            TextWrapping = TextWrapping.Wrap,
        });
        return new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            Child = content,
        };
    }

    private static Control BuildEmptyState(JsonElement properties)
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
        };
        content.Children.Add(new TextBlock
        {
            Text = IconText(StringProperty(properties, "icon")),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 28,
        });
        content.Children.Add(new TextBlock
        {
            Text = StringProperty(properties, "title"),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = StringProperty(properties, "message"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return content;
    }

    private static Control BuildKeyValue(JsonElement properties)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (!properties.TryGetProperty("items", out var items))
        {
            return grid;
        }
        var row = 0;
        foreach (var item in items.EnumerateArray())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = new TextBlock
            {
                Text = StringProperty(item, "key"),
                Foreground = Brushes.Gray,
            };
            var value = new TextBlock
            {
                Text = StringProperty(item, "value"),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(key, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(key);
            grid.Children.Add(value);
            row++;
        }
        return grid;
    }

    private Control BuildTextArea(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var textArea = new TextBox
        {
            Text = StringProperty(properties, "default"),
            PlaceholderText = StringProperty(properties, "placeholder"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = Math.Max(2, IntegerProperty(properties, "rows", 4)) * 24,
            IsEnabled = !BooleanProperty(properties, "disabled"),
        };
        textArea.LostFocus += async (_, _) => await InvokeAsync(
            page,
            ChangeInput(node, properties, textArea.Text ?? string.Empty));
        return Labeled(StringProperty(properties, "label"), textArea);
    }

    private Control BuildNumberInput(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var number = new NumericUpDown
        {
            Value = DecimalProperty(properties, "default", 0),
            Minimum = DecimalProperty(properties, "min", decimal.MinValue),
            Maximum = DecimalProperty(properties, "max", decimal.MaxValue),
            Increment = DecimalProperty(properties, "step", 1),
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = !BooleanProperty(properties, "disabled"),
        };
        number.LostFocus += async (_, _) => await InvokeAsync(
            page,
            ChangeInput(node, properties, number.Value ?? 0));
        return Labeled(StringProperty(properties, "label"), number);
    }

    private Control BuildRadioGroup(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var panel = new StackPanel { Spacing = 5 };
        var label = StringProperty(properties, "label");
        if (!string.IsNullOrEmpty(label))
        {
            panel.Children.Add(new TextBlock { Text = label });
        }
        var selected = StringProperty(properties, "default");
        foreach (var option in StringArrayProperty(properties, "options"))
        {
            var radio = new RadioButton
            {
                Content = option,
                GroupName = $"plugin-{page.PluginId}-{StringProperty(properties, "key")}",
                IsChecked = option == selected,
                IsEnabled = !BooleanProperty(properties, "disabled"),
            };
            radio.Click += async (_, _) => await InvokeAsync(page, ChangeInput(node, properties, option));
            panel.Children.Add(radio);
        }
        return panel;
    }

    private Control BuildSlider(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var slider = new Slider
        {
            Minimum = NumberProperty(properties, "min", 0),
            Maximum = NumberProperty(properties, "max", 100),
            Value = NumberProperty(properties, "default", 0),
            TickFrequency = NumberProperty(properties, "step", 1),
            IsEnabled = !BooleanProperty(properties, "disabled"),
        };
        slider.LostFocus += async (_, _) => await InvokeAsync(
            page,
            ChangeInput(node, properties, slider.Value));
        return Labeled(StringProperty(properties, "label"), slider);
    }

    private Control BuildList(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var items = ReadListItems(properties);
        if (items.Length == 0)
        {
            return new TextBlock
            {
                Text = StringProperty(properties, "emptyText"),
                Foreground = Brushes.Gray,
            };
        }
        var action = StringProperty(properties, "action");
        var actionLabel = StringProperty(properties, "actionLabel");
        if (!string.IsNullOrEmpty(action) && string.IsNullOrEmpty(actionLabel))
        {
            actionLabel = "Open";
        }
        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<PluginListItem>((item, _) =>
            {
                var content = new Grid { Margin = new Thickness(6) };
                content.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(96)));
                content.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(12)));
                content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                if (!string.IsNullOrEmpty(action))
                {
                    content.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(12)));
                    content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                }
                var cover = BuildListImage(page, item?.Image, item?.Title);
                Grid.SetColumn(cover, 0);
                content.Children.Add(cover);

                var details = new StackPanel
                {
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(details, 2);
                details.Children.Add(new TextBlock
                {
                    Text = item?.Title,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                if (!string.IsNullOrEmpty(item?.Description))
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = item.Description,
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                if (!string.IsNullOrEmpty(item?.Badge))
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = item.Badge,
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                    });
                }
                content.Children.Add(details);
                if (!string.IsNullOrEmpty(action) && item is not null)
                {
                    var actionButton = new Button
                    {
                        Content = actionLabel,
                        MinWidth = 88,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    };
                    actionButton.Classes.Add("accent");
                    Grid.SetColumn(actionButton, 4);
                    actionButton.Click += async (_, _) =>
                    {
                        var input = ActionInput(node, action);
                        input["itemId"] = item.Id;
                        await InvokeAsync(page, input);
                    };
                    content.Children.Add(actionButton);
                }
                return content;
            }),
        };
        return list;
    }

    private Control BuildGallery(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var items = ReadListItems(properties);
        if (items.Length == 0)
        {
            return new TextBlock
            {
                Text = StringProperty(properties, "emptyText"),
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        var action = StringProperty(properties, "action");
        var itemWidth = NumberProperty(properties, "itemWidth", 144);
        var imageHeight = NumberProperty(properties, "imageHeight", 192);
        var spacing = Spacing(StringProperty(properties, "spacing"));
        var gallery = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var item in items)
        {
            var content = new StackPanel
            {
                Width = itemWidth,
                Spacing = 6,
            };
            content.Children.Add(BuildInlineImage(page, item.Image, item.Title, itemWidth, imageHeight));
            content.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 42,
            });
            if (!string.IsNullOrEmpty(item.Badge))
            {
                content.Children.Add(new TextBlock
                {
                    Text = item.Badge,
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            Control card;
            if (string.IsNullOrEmpty(action))
            {
                var border = new Border
                {
                    Padding = new Thickness(8),
                    Margin = new Thickness(spacing / 2),
                    Child = content,
                };
                border.Classes.Add("panel");
                card = border;
            }
            else
            {
                var button = new Button
                {
                    Content = content,
                    Padding = new Thickness(8),
                    Margin = new Thickness(spacing / 2),
                    Background = Brushes.Transparent,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Top,
                };
                if (!string.IsNullOrEmpty(item.Description))
                {
                    ToolTip.SetTip(button, item.Description);
                }
                Avalonia.Automation.AutomationProperties.SetName(button, item.Title);
                button.Click += async (_, _) =>
                {
                    var input = ActionInput(node, action);
                    input["itemId"] = item.Id;
                    await InvokeAsync(page, input);
                };
                card = button;
            }
            gallery.Children.Add(card);
        }
        return gallery;
    }

    private Control BuildListImage(PluginUiPage page, string? source, string? alternateText)
    {
        const double width = 96;
        const double height = 128;
        return BuildInlineImage(page, source, alternateText, width, height);
    }

    private Control BuildInlineImage(
        PluginUiPage page,
        string? source,
        string? alternateText,
        double width,
        double height)
    {
        var bitmap = LoadInlineImage(source);
        if (bitmap is not null)
        {
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill,
            };
            if (!string.IsNullOrEmpty(alternateText))
            {
                ToolTip.SetTip(image, alternateText);
                Avalonia.Automation.AutomationProperties.SetName(image, alternateText);
            }
            return new Border
            {
                Width = width,
                Height = height,
                ClipToBounds = true,
                Child = image,
            };
        }

        var placeholder = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 28,
                    Foreground = Brushes.Gray,
                    Text = IconText("image"),
                },
                new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Text = "暂无封面",
                },
            },
        };
        var border = new Border
        {
            Width = width,
            Height = height,
            BorderThickness = new Thickness(1),
            Child = placeholder,
        };
        border.Classes.Add("panel");
        Avalonia.Automation.AutomationProperties.SetName(border, "暂无封面");
        if (IsRemoteImageSource(source))
        {
            BeginLoadRemoteImage(page, source!, remoteBitmap =>
            {
                var image = new Image
                {
                    Source = remoteBitmap,
                    Stretch = Stretch.UniformToFill,
                };
                if (!string.IsNullOrEmpty(alternateText))
                {
                    ToolTip.SetTip(image, alternateText);
                    Avalonia.Automation.AutomationProperties.SetName(image, alternateText);
                }
                border.Child = image;
            });
        }
        return border;
    }

    private Bitmap? LoadInlineImage(string? dataUri)
    {
        if (string.IsNullOrEmpty(dataUri) || IsRemoteImageSource(dataUri))
        {
            return null;
        }
        var cacheKey = $"inline:{dataUri}";
        if (imageCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        try
        {
            var comma = dataUri.IndexOf(',');
            if (comma < 0 || !dataUri.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
            {
                return null;
            }
            var bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            if (bytes.Length is 0 or > MaximumInlineImageBytes)
            {
                return null;
            }
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
            imageCache.Add(cacheKey, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void BeginLoadRemoteImage(
        PluginUiPage page,
        string source,
        Action<Bitmap> apply)
    {
        var cacheKey = ImageSourceCacheKey(source);
        if (imageCache.TryGetValue(cacheKey, out var cached))
        {
            apply(cached);
            return;
        }

        _ = LoadRemoteImageAsync(page, source, cacheKey, apply, imageLoadCancellation.Token);
    }

    private async Task LoadRemoteImageAsync(
        PluginUiPage page,
        string source,
        string cacheKey,
        Action<Bitmap> apply,
        CancellationToken cancellationToken)
    {
        Bitmap? decoded = null;
        try
        {
            var registry = Registry;
            if (registry is null)
            {
                return;
            }
            var bytes = await registry.LoadRemoteImageAsync(
                page.PluginId,
                source,
                IsConfiguration,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            decoded = Bitmap.DecodeToWidth(stream, 1024, BitmapInterpolationMode.HighQuality);
            var bitmap = decoded;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (disposed || currentPage?.PluginId != page.PluginId ||
                    currentPage.Content is not { } content || !ContainsImageSource(content, source))
                {
                    return;
                }
                if (imageCache.TryGetValue(cacheKey, out var cached))
                {
                    apply(cached);
                    return;
                }
                imageCache.Add(cacheKey, bitmap);
                decoded = null;
                apply(bitmap);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A rejected or malformed image leaves the existing host placeholder visible.
        }
        finally
        {
            decoded?.Dispose();
        }
    }

    private Control BuildDataTable(PluginUiPage page, JsonElement node, JsonElement properties)
    {
        var rows = ReadTableRows(properties);
        if (rows.Length == 0)
        {
            return new TextBlock
            {
                Text = StringProperty(properties, "emptyText"),
                Foreground = Brushes.Gray,
            };
        }
        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserReorderColumns = false,
            CanUserSortColumns = false,
            IsReadOnly = true,
            ItemsSource = rows,
            SelectionMode = DataGridSelectionMode.Single,
        };
        foreach (var column in properties.GetProperty("columns").EnumerateArray())
        {
            var key = StringProperty(column, "key");
            var width = NumberProperty(column, "width", 0);
            table.Columns.Add(new DataGridTemplateColumn
            {
                Header = StringProperty(column, "title"),
                Width = width > 0
                    ? new DataGridLength(width)
                    : new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = new FuncDataTemplate<PluginDataRow>((row, _) => new TextBlock
                {
                    Margin = new Thickness(6, 3),
                    Text = row?.Value(key) ?? string.Empty,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }),
            });
        }
        var action = StringProperty(properties, "action");
        if (!string.IsNullOrEmpty(action))
        {
            table.SelectionChanged += async (_, _) =>
            {
                if (table.SelectedItem is PluginDataRow row)
                {
                    var input = ActionInput(node, action);
                    input["row"] = JsonNode.Parse(row.Json);
                    await InvokeAsync(page, input);
                }
            };
        }
        return table;
    }

    private void AttachRegistry()
    {
        if (ReferenceEquals(subscribedRegistry, Registry))
        {
            return;
        }
        if (subscribedRegistry is not null)
        {
            subscribedRegistry.PageChanged -= OnPageChanged;
            subscribedRegistry.ConfigurationChanged -= OnConfigurationChanged;
        }
        subscribedRegistry = Registry;
        if (subscribedRegistry is not null)
        {
            subscribedRegistry.PageChanged += OnPageChanged;
            subscribedRegistry.ConfigurationChanged += OnConfigurationChanged;
        }
        Render();
    }

    private void OnPageChanged(object? sender, PluginUiPageChangedEventArgs eventArgs)
    {
        if (eventArgs.Kind == PluginUiPageChangeKind.Badge)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsConfiguration)
            {
                ApplyPageChange(eventArgs);
            }
        });
    }

    private void OnConfigurationChanged(object? sender, PluginUiPageChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsConfiguration)
            {
                ApplyPageChange(eventArgs);
            }
        });
    }

    private void ApplyPageChange(PluginUiPageChangedEventArgs eventArgs)
    {
        if (disposed || eventArgs.PluginId != PluginId)
        {
            return;
        }
        if (eventArgs.Kind == PluginUiPageChangeKind.Updated &&
            eventArgs.Page is { Content: { } } page &&
            TryApplyIncrementalUpdate(page, eventArgs.UpdatedComponentIds))
        {
            return;
        }

        Render(eventArgs.Page);
    }

    private void Render(PluginUiPage? suppliedPage = null)
    {
        if (disposed || !Dispatcher.UIThread.CheckAccess())
        {
            if (!disposed)
            {
                Dispatcher.UIThread.Post(() => Render());
            }
            return;
        }

        imageLoadCancellation.Cancel();
        imageLoadCancellation.Dispose();
        imageLoadCancellation = new CancellationTokenSource();
        root.Children.Clear();
        componentControls.Clear();
        ClearImageCache();
        status.IsVisible = false;
        var page = suppliedPage?.PluginId == PluginId
            ? suppliedPage
            : (IsConfiguration ? Registry?.Configurations : Registry?.Pages)
                ?.FirstOrDefault(candidate => candidate.PluginId == PluginId);
        currentPage = page;
        if (page?.Content is not { } content)
        {
            root.Children.Add(new TextBlock
            {
                Text = page is null ? string.Empty : "插件页面正在初始化…",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        root.Children.Add(BuildComponent(page, content));
    }

    private bool TryApplyIncrementalUpdate(
        PluginUiPage page,
        IReadOnlyList<string> updatedComponentIds)
    {
        if (currentPage?.PluginId != page.PluginId || page.Content is not { } content ||
            updatedComponentIds.Count == 0 || root.Children.Count == 0)
        {
            return false;
        }

        var parents = IndexComponentParents(content);
        var targets = updatedComponentIds
            .OrderByDescending(target => ComponentDepth(target, parents))
            .ToArray();
        foreach (var target in targets)
        {
            if (!componentControls.TryGetValue(target, out var previous) ||
                !TryFindComponent(content, target, out var node))
            {
                return false;
            }

            if (TryUpdateControlInPlace(previous, node))
            {
                continue;
            }

            var replacement = BuildComponent(page, node);
            CopyLayout(previous, replacement);
            if (!ReplaceControl(previous, replacement))
            {
                return false;
            }
        }

        currentPage = page;
        PruneImageCache(page, content);
        return true;
    }

    private static int ComponentDepth(
        string componentId,
        IReadOnlyDictionary<string, string?> parents)
    {
        var depth = 0;
        var parent = parents.GetValueOrDefault(componentId);
        while (parent is not null)
        {
            depth++;
            parent = parents.GetValueOrDefault(parent);
        }
        return depth;
    }

    private bool TryUpdateControlInPlace(Control control, JsonElement node)
    {
        if (node.GetProperty("type").GetString() != "viewStack" || control is not Grid viewStack ||
            !node.TryGetProperty("props", out var properties))
        {
            return false;
        }

        var active = StringProperty(properties, "active");
        if (!node.TryGetProperty("children", out var children))
        {
            return false;
        }
        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("id", out var id) ||
                !componentControls.TryGetValue(id.GetString()!, out var childControl) ||
                !viewStack.Children.Contains(childControl))
            {
                return false;
            }
            childControl.IsVisible = id.GetString() == active;
        }
        return true;
    }

    private static Dictionary<string, string?> IndexComponentParents(JsonElement rootNode)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pending = new Stack<(JsonElement Node, string? ParentId)>();
        pending.Push((rootNode, null));
        while (pending.TryPop(out var item))
        {
            var id = item.Node.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;
            if (id is not null)
            {
                result[id] = item.ParentId;
            }
            if (item.Node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    pending.Push((child, id ?? item.ParentId));
                }
            }
        }
        return result;
    }

    private static bool TryFindComponent(JsonElement rootNode, string id, out JsonElement result)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(rootNode);
        while (pending.TryPop(out var node))
        {
            if (node.TryGetProperty("id", out var idElement) && idElement.GetString() == id)
            {
                result = node;
                return true;
            }
            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    pending.Push(child);
                }
            }
        }
        result = default;
        return false;
    }

    private bool ReplaceControl(Control previous, Control replacement)
    {
        if (root.Children.IndexOf(previous) is var rootIndex && rootIndex >= 0)
        {
            root.Children[rootIndex] = replacement;
            return true;
        }
        if (previous.Parent is Panel panel && panel.Children.IndexOf(previous) is var panelIndex && panelIndex >= 0)
        {
            panel.Children[panelIndex] = replacement;
            return true;
        }
        if (previous.Parent is Decorator decorator && ReferenceEquals(decorator.Child, previous))
        {
            decorator.Child = replacement;
            return true;
        }
        if (previous.Parent is ContentControl contentControl && ReferenceEquals(contentControl.Content, previous))
        {
            contentControl.Content = replacement;
            return true;
        }
        return false;
    }

    private static void CopyLayout(Control source, Control target)
    {
        target.HorizontalAlignment = source.HorizontalAlignment;
        target.VerticalAlignment = source.VerticalAlignment;
        target.Width = source.Width;
        Grid.SetColumn(target, Grid.GetColumn(source));
        Grid.SetRow(target, Grid.GetRow(source));
        Grid.SetColumnSpan(target, Grid.GetColumnSpan(source));
        Grid.SetRowSpan(target, Grid.GetRowSpan(source));
    }

    private Control BuildComponent(PluginUiPage page, JsonElement node)
    {
        var control = BuildComponentCore(page, node);
        if (node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            componentControls[id.GetString()!] = control;
        }
        return control;
    }

    private Control BuildComponentCore(PluginUiPage page, JsonElement node)
    {
        var type = node.GetProperty("type").GetString();
        var properties = node.TryGetProperty("props", out var props) ? props : default;
        switch (type)
        {
            case "stack":
                var stack = new StackPanel
                {
                    Orientation = StringProperty(properties, "orientation") == "horizontal"
                        ? Orientation.Horizontal
                        : Orientation.Vertical,
                    Spacing = Spacing(StringProperty(properties, "spacing")),
                };
                if (node.TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                    {
                        stack.Children.Add(BuildComponent(page, child));
                    }
                }
                return stack;
            case "viewStack":
                var viewStack = new Grid();
                var activeView = StringProperty(properties, "active");
                if (node.TryGetProperty("children", out var views))
                {
                    foreach (var view in views.EnumerateArray())
                    {
                        var child = BuildComponent(page, view);
                        child.IsVisible = view.GetProperty("id").GetString() == activeView;
                        viewStack.Children.Add(child);
                    }
                }
                return viewStack;
            case "grid":
                return BuildGrid(page, node, properties);
            case "scroll":
                return new ScrollViewer
                {
                    MaxHeight = NumberProperty(properties, "maxHeight", double.PositiveInfinity),
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = BuildContainerContent(page, node),
                };
            case "card":
                return BuildCard(page, node, properties);
            case "spacer":
                return new Border { Height = Spacing(StringProperty(properties, "size")) };
            case "expander":
                return new Expander
                {
                    Header = StringProperty(properties, "title"),
                    IsExpanded = BooleanProperty(properties, "expanded"),
                    Content = BuildContainerContent(page, node),
                };
            case "tabs":
                return BuildTabs(page, node, properties);
            case "tab":
                return BuildContainerContent(page, node);
            case "text":
                var text = new TextBlock
                {
                    Text = StringProperty(properties, "text"),
                    FontSize = StringProperty(properties, "style") switch
                    {
                        "caption" => 11,
                        "title" => 16,
                        "heading" => 20,
                        _ => 14,
                    },
                    FontWeight = StringProperty(properties, "style") is "title" or "heading"
                        ? FontWeight.SemiBold
                        : FontWeight.Normal,
                    TextWrapping = TextWrapping.Wrap,
                };
                if (StringProperty(properties, "style") == "mono")
                {
                    text.FontFamily = FontFamily.Parse("monospace");
                }
                return text;
            case "link":
                return BuildLink(properties);
            case "image":
                return BuildImage(page, properties);
            case "button":
                var button = new Button
                {
                    Content = StringProperty(properties, "label"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsEnabled = !BooleanProperty(properties, "disabled"),
                };
                if (StringProperty(properties, "variant") == "primary")
                {
                    button.Classes.Add("accent");
                }
                button.Click += async (_, _) => await InvokeAsync(
                    page,
                    ActionInput(node, StringProperty(properties, "action")));
                return button;
            case "badge":
                return new Border
                {
                    Padding = new Thickness(6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = StringProperty(properties, "text"), FontSize = 11 },
                };
            case "progress":
                var progress = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = NumberProperty(properties, "value"),
                    Height = 10,
                    IsIndeterminate = BooleanProperty(properties, "indeterminate"),
                };
                var progressLabel = StringProperty(properties, "label");
                return string.IsNullOrEmpty(progressLabel) ? progress : Labeled(progressLabel, progress);
            case "spinner":
                var spinner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                spinner.Children.Add(new ProgressBar
                {
                    Width = 48,
                    Height = 6,
                    IsIndeterminate = true,
                });
                var spinnerLabel = StringProperty(properties, "label");
                if (!string.IsNullOrEmpty(spinnerLabel))
                {
                    spinner.Children.Add(new TextBlock
                    {
                        Text = spinnerLabel,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                return spinner;
            case "alert":
                return BuildAlert(properties);
            case "emptyState":
                return BuildEmptyState(properties);
            case "keyValue":
                return BuildKeyValue(properties);
            case "icon":
                return new TextBlock
                {
                    Text = IconText(StringProperty(properties, "name")),
                    FontSize = StringProperty(properties, "size") switch
                    {
                        "small" => 12,
                        "large" => 24,
                        _ => 16,
                    },
                };
            case "separator":
                return new Separator();
            case "toggle":
                var toggle = new CheckBox
                {
                    Content = StringProperty(properties, "label"),
                    IsChecked = BooleanProperty(properties, "default"),
                    IsEnabled = !BooleanProperty(properties, "disabled"),
                };
                toggle.Click += async (_, _) => await InvokeAsync(
                    page,
                    ChangeInput(node, properties, toggle.IsChecked == true));
                return toggle;
            case "select":
                var select = new ComboBox
                {
                    ItemsSource = StringArrayProperty(properties, "options"),
                    SelectedItem = StringProperty(properties, "default"),
                    MinWidth = 160,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsEnabled = !BooleanProperty(properties, "disabled"),
                };
                select.SelectionChanged += async (_, _) =>
                {
                    if (select.SelectedItem is string value)
                    {
                        await InvokeAsync(page, ChangeInput(node, properties, value));
                    }
                };
                return Labeled(StringProperty(properties, "label"), select);
            case "textInput":
            case "searchInput":
                var textBox = new TextBox
                {
                    Text = StringProperty(properties, "default"),
                    PlaceholderText = StringProperty(properties, "placeholder"),
                    PasswordChar = type == "textInput" && BooleanProperty(properties, "password") ? '●' : '\0',
                    MinWidth = 180,
                    IsEnabled = !BooleanProperty(properties, "disabled"),
                };
                textBox.LostFocus += async (_, _) => await InvokeAsync(
                    page,
                    ChangeInput(node, properties, textBox.Text ?? string.Empty));
                if (type == "searchInput")
                {
                    textBox.KeyDown += async (_, eventArgs) =>
                    {
                        if (eventArgs.Key == Key.Enter)
                        {
                            await InvokeAsync(page, ChangeInput(node, properties, textBox.Text ?? string.Empty));
                        }
                    };
                }
                return Labeled(StringProperty(properties, "label"), textBox);
            case "textArea":
                return BuildTextArea(page, node, properties);
            case "numberInput":
                return BuildNumberInput(page, node, properties);
            case "radioGroup":
                return BuildRadioGroup(page, node, properties);
            case "slider":
                return BuildSlider(page, node, properties);
            case "list":
                return BuildList(page, node, properties);
            case "gallery":
                return BuildGallery(page, node, properties);
            case "dataTable":
                return BuildDataTable(page, node, properties);
            default:
                return new TextBlock { Text = $"Unsupported plugin component: {type}" };
        }
    }

    private async Task InvokeAsync(PluginUiPage page, JsonObject input)
    {
        if (!page.IsInteractive || Registry is null)
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(input.ToJsonString());
            if (IsConfiguration)
            {
                await Registry.InvokeConfigurationAsync(page.PluginId, document.RootElement).ConfigureAwait(true);
            }
            else
            {
                await Registry.InvokePageAsync(page.PluginId, document.RootElement).ConfigureAwait(true);
            }
            status.IsVisible = false;
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
            status.IsVisible = true;
        }
    }

    private Task InvokeLifecycleAsync(string eventType)
    {
        if (!IsConfiguration || currentPage is null)
        {
            return Task.CompletedTask;
        }
        return InvokeAsync(currentPage, new JsonObject { ["type"] = eventType });
    }

    private static Control Labeled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(label))
        {
            panel.Children.Add(new TextBlock { Text = label });
        }
        panel.Children.Add(control);
        return panel;
    }

    private Control BuildLink(JsonElement properties)
    {
        var url = StringProperty(properties, "url");
        var label = StringProperty(properties, "label");
        var link = new Button
        {
            Content = new TextBlock
            {
                Text = $"{(string.IsNullOrWhiteSpace(label) ? url : label)} ↗",
                TextDecorations = TextDecorations.Underline,
            },
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = Brushes.DodgerBlue,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(link, url);
        link.Click += async (_, _) =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this)
                    ?? throw new InvalidOperationException("No top-level window is available.");
                if (!await topLevel.Launcher.LaunchUriAsync(new Uri(url, UriKind.Absolute)))
                {
                    throw new InvalidOperationException("The operating system could not open the link.");
                }
                status.IsVisible = false;
            }
            catch (Exception exception)
            {
                status.Text = exception.Message;
                status.IsVisible = true;
            }
        };
        return link;
    }

    private static JsonObject ActionInput(JsonElement node, string action)
    {
        var input = new JsonObject
        {
            ["type"] = "action",
            ["action"] = action,
        };
        AddComponentId(input, node);
        return input;
    }

    private static JsonObject ChangeInput(JsonElement node, JsonElement properties, object value)
    {
        var valueNode = value switch
        {
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            decimal number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            _ => throw new ArgumentException("Plugin UI value type is not supported.", nameof(value)),
        };
        var input = new JsonObject
        {
            ["type"] = "change",
            ["key"] = StringProperty(properties, "key"),
            ["value"] = valueNode,
        };
        AddComponentId(input, node);
        return input;
    }

    private static void AddComponentId(JsonObject input, JsonElement node)
    {
        if (node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            input["id"] = id.GetString();
        }
    }

    private static void ApplyLayout(Control control, JsonElement node)
    {
        var layout = node.TryGetProperty("layout", out var value) ? value : default;
        var horizontal = StringProperty(layout, "horizontalAlignment");
        control.HorizontalAlignment = horizontal switch
        {
            "left" => HorizontalAlignment.Left,
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };
        var vertical = StringProperty(layout, "verticalAlignment");
        control.VerticalAlignment = vertical switch
        {
            "top" => VerticalAlignment.Top,
            "center" => VerticalAlignment.Center,
            "bottom" => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };
        var width = NumberProperty(layout, "width", 0);
        if (width > 0)
        {
            control.Width = width;
        }
    }

    private static int LayoutInteger(JsonElement node, string name, int fallback)
    {
        var layout = node.TryGetProperty("layout", out var value) ? value : default;
        return IntegerProperty(layout, name, fallback);
    }

    private static string StringProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool BooleanProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static double NumberProperty(JsonElement properties, string name, double fallback = 0) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.TryGetDouble(out var number)
            ? number
            : fallback;

    private static int IntegerProperty(JsonElement properties, string name, int fallback = 0) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static decimal DecimalProperty(JsonElement properties, string name, decimal fallback = 0) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.TryGetDecimal(out var number)
            ? number
            : fallback;

    private static string[] StringArrayProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];

    private static PluginListItem[] ReadListItems(JsonElement properties) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("items", out var items) &&
        items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => new PluginListItem(
                StringProperty(item, "id"),
                StringProperty(item, "title"),
                StringProperty(item, "description"),
                StringProperty(item, "badge"),
                StringProperty(item, "image"))).ToArray()
            : [];

    private static PluginDataRow[] ReadTableRows(JsonElement properties) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("rows", out var rows) &&
        rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(row => new PluginDataRow(
                row.GetRawText(),
                row.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => DisplayValue(property.Value),
                    StringComparer.Ordinal))).ToArray()
            : [];

    private static string DisplayValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };

    private static double Spacing(string value) => value switch
    {
        "xs" => 2,
        "sm" => 6,
        "md" => 10,
        "lg" => 16,
        _ => 0,
    };

    private static string IconText(string value) => value switch
    {
        "play" => "\u25b6",
        "pause" => "\u23f8",
        "download" => "\u2193",
        "search" => "\u2315",
        "subtitles" => "CC",
        "image" => "▧",
        "settings" => "\u2699",
        "warning" => "!",
        "check" => "\u2713",
        _ => "i",
    };

    private void ClearImageCache()
    {
        foreach (var image in imageCache.Values)
        {
            image.Dispose();
        }
        imageCache.Clear();
    }

    private static bool ContainsImageSource(JsonElement content, string source)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(content);
        while (pending.TryPop(out var node))
        {
            var type = node.GetProperty("type").GetString();
            var properties = node.TryGetProperty("props", out var props) ? props : default;
            if (type == "image" && StringProperty(properties, "source") == source)
            {
                return true;
            }
            if (type is "list" or "gallery" && properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("items", out var items) &&
                items.EnumerateArray().Any(item => StringProperty(item, "image") == source))
            {
                return true;
            }
            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    pending.Push(child);
                }
            }
        }
        return false;
    }

    private void PruneImageCache(PluginUiPage page, JsonElement content)
    {
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<JsonElement>();
        pending.Push(content);
        while (pending.TryPop(out var node))
        {
            var type = node.GetProperty("type").GetString();
            var properties = node.TryGetProperty("props", out var props) ? props : default;
            if (type == "image")
            {
                AddImageKey(usedKeys, page, StringProperty(properties, "asset"), StringProperty(properties, "source"));
            }
            else if (type is "list" or "gallery" && properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    AddImageKey(usedKeys, page, string.Empty, StringProperty(item, "image"));
                }
            }
            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    pending.Push(child);
                }
            }
        }

        foreach (var key in imageCache.Keys.Where(key => !usedKeys.Contains(key)).ToArray())
        {
            imageCache.Remove(key, out var bitmap);
            bitmap?.Dispose();
        }
    }

    private static void AddImageKey(
        HashSet<string> keys,
        PluginUiPage page,
        string asset,
        string source)
    {
        if (!string.IsNullOrEmpty(source))
        {
            keys.Add(ImageSourceCacheKey(source));
        }
        else if (!string.IsNullOrEmpty(asset))
        {
            keys.Add($"{page.PluginId}:{asset}");
        }
    }

    private static bool IsRemoteImageSource(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string ImageSourceCacheKey(string source) =>
        IsRemoteImageSource(source) ? $"remote:{source}" : $"inline:{source}";

    private sealed record PluginListItem(
        string Id,
        string Title,
        string Description,
        string Badge,
        string Image);

    private sealed record PluginDataRow(string Json, IReadOnlyDictionary<string, string> Values)
    {
        public string Value(string key) => Values.GetValueOrDefault(key, string.Empty);
    }
}
