// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CanX.Plugins.Ui;

namespace CanX.App.Controls;

public sealed class PluginContributionHost : UserControl, IDisposable
{
    public static readonly StyledProperty<PluginUiRegistry?> RegistryProperty =
        AvaloniaProperty.Register<PluginContributionHost, PluginUiRegistry?>(nameof(Registry));

    public static readonly StyledProperty<string> SlotProperty =
        AvaloniaProperty.Register<PluginContributionHost, string>(nameof(Slot), string.Empty);

    private readonly StackPanel root = new() { Spacing = 10 };
    private PluginUiRegistry? subscribedRegistry;
    private bool disposed;

    public PluginContributionHost()
    {
        Content = root;
        IsVisible = false;
    }

    public PluginUiRegistry? Registry
    {
        get => GetValue(RegistryProperty);
        set => SetValue(RegistryProperty, value);
    }

    public string Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (subscribedRegistry is not null)
        {
            subscribedRegistry.Changed -= OnRegistryChanged;
            subscribedRegistry = null;
        }
        root.Children.Clear();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RegistryProperty)
        {
            AttachRegistry();
        }
        else if (change.Property == SlotProperty)
        {
            Render();
        }
    }

    private void AttachRegistry()
    {
        if (ReferenceEquals(subscribedRegistry, Registry))
        {
            return;
        }
        if (subscribedRegistry is not null)
        {
            subscribedRegistry.Changed -= OnRegistryChanged;
        }
        subscribedRegistry = Registry;
        if (subscribedRegistry is not null)
        {
            subscribedRegistry.Changed += OnRegistryChanged;
        }
        Render();
    }

    private void OnRegistryChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        if (disposed || !Dispatcher.UIThread.CheckAccess())
        {
            if (!disposed)
            {
                Dispatcher.UIThread.Post(Render);
            }
            return;
        }
        root.Children.Clear();
        var contributions = Registry?.Contributions
            .Where(contribution => string.Equals(contribution.Slot, Slot, StringComparison.Ordinal))
            .ToArray() ?? [];
        foreach (var contribution in contributions)
        {
            root.Children.Add(BuildContribution(contribution));
        }
        IsVisible = contributions.Length > 0;
    }

    private Control BuildContribution(PluginUiContribution contribution)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = contribution.Title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        });
        var status = new TextBlock
        {
            FontSize = 11,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(BuildComponent(contribution, contribution.Content, status));
        content.Children.Add(status);
        var border = new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        border.Classes.Add("panel");
        return border;
    }

    private Control BuildComponent(
        PluginUiContribution contribution,
        JsonElement node,
        TextBlock status)
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
                        stack.Children.Add(BuildComponent(contribution, child, status));
                    }
                }
                return stack;
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
            case "button":
                var button = new Button
                {
                    Content = StringProperty(properties, "label"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                if (StringProperty(properties, "variant") == "primary")
                {
                    button.Classes.Add("accent");
                }
                button.Click += async (_, _) => await InvokeAsync(
                    contribution,
                    new JsonObject { ["action"] = StringProperty(properties, "action") },
                    status);
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
                return new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = NumberProperty(properties, "value"),
                    Height = 10,
                };
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
                };
                toggle.Click += async (_, _) => await InvokeAsync(
                    contribution,
                    ChangeInput(properties, toggle.IsChecked == true),
                    status);
                return toggle;
            case "select":
                var select = new ComboBox
                {
                    ItemsSource = StringArrayProperty(properties, "options"),
                    SelectedItem = StringProperty(properties, "default"),
                    MinWidth = 160,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                select.SelectionChanged += async (_, _) =>
                {
                    if (select.SelectedItem is string value)
                    {
                        await InvokeAsync(contribution, ChangeInput(properties, value), status);
                    }
                };
                return Labeled(StringProperty(properties, "label"), select);
            case "textInput":
                var textBox = new TextBox
                {
                    Text = StringProperty(properties, "default"),
                    PlaceholderText = StringProperty(properties, "placeholder"),
                    MinWidth = 180,
                };
                textBox.LostFocus += async (_, _) => await InvokeAsync(
                    contribution,
                    ChangeInput(properties, textBox.Text ?? string.Empty),
                    status);
                return Labeled(StringProperty(properties, "label"), textBox);
            default:
                return new TextBlock { Text = $"Unsupported plugin component: {type}" };
        }
    }

    private async Task InvokeAsync(
        PluginUiContribution contribution,
        JsonObject input,
        TextBlock status)
    {
        if (!contribution.IsInteractive || Registry is null)
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(input.ToJsonString());
            await Registry.InvokeAsync(contribution.Id, document.RootElement).ConfigureAwait(true);
            status.Text = "已执行";
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
        }
        status.IsVisible = true;
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

    private static JsonObject ChangeInput(JsonElement properties, object value)
    {
        var valueNode = value switch
        {
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            _ => throw new ArgumentException("Plugin UI values must be strings or booleans.", nameof(value)),
        };
        return new JsonObject
        {
            ["key"] = StringProperty(properties, "key"),
            ["value"] = valueNode,
        };
    }

    private static string StringProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool BooleanProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static double NumberProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.TryGetDouble(out var number)
            ? number
            : 0;

    private static string[] StringArrayProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];

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
        "play" => "▶",
        "pause" => "⏸",
        "download" => "↓",
        "search" => "⌕",
        "subtitles" => "CC",
        "settings" => "⚙",
        "warning" => "!",
        "check" => "✓",
        _ => "i",
    };
}
