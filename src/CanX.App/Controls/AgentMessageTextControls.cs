// SPDX-License-Identifier: GPL-3.0-or-later

using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using ColorDocument.Avalonia;
using Markdown.Avalonia;

namespace CanX.App.Controls;

public sealed class AgentMarkdownViewer : MarkdownScrollViewer
{
    public AgentMarkdownViewer()
    {
        MarkdownStyleName = "FluentTheme";
        SelectionEnabled = true;

        var copy = new MenuItem { Header = "复制" };
        copy.Click += async (_, _) => await CopySelectionAsync();
        var selectAll = new MenuItem { Header = "全选" };
        selectAll.Click += (_, _) => SelectAllText();

        var menu = new ContextMenu { Items = { copy, selectAll } };
        menu.Opened += (_, _) => copy.IsEnabled = !string.IsNullOrEmpty(SelectedText);
        ContextMenu = menu;
    }

    public string SelectedText => GetDocument(this)?.GetSelectedText() ?? string.Empty;

    public void SelectAllText()
    {
        var document = GetDocument(this);
        if (document is null || !document.Children.Any()) return;

        document.Select(new Point(0, 0), new Point(double.PositiveInfinity, double.PositiveInfinity));
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (e.Key == Key.A)
            {
                SelectAllText();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.C)
            {
                _ = CopySelectionAsync();
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private async Task CopySelectionAsync()
    {
        var selectedText = SelectedText;
        if (!string.IsNullOrEmpty(selectedText) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(selectedText);
        }
    }

    // Markdown.Avalonia.Tight 12.0.0-a3 keeps the selection in this private field.
    // Its public control API has no selected-text or select-all operation.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_document")]
    private static extern ref DocumentElement? GetDocument(MarkdownScrollViewer viewer);
}

public sealed class AgentSelectableTextBlock : SelectableTextBlock
{
    public AgentSelectableTextBlock()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap;

        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) => Copy();
        var selectAll = new MenuItem { Header = "全选" };
        selectAll.Click += (_, _) => SelectAll();

        var menu = new ContextMenu { Items = { copy, selectAll } };
        menu.Opened += (_, _) => copy.IsEnabled = CanCopy;
        ContextMenu = menu;
    }
}
