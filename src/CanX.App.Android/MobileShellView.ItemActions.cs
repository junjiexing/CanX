// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CanX.App.Android;

internal sealed partial class MobileShellView
{
    private ContextMenu? _activeItemMenu;

    private sealed record MobileItemAction(
        string Label,
        Func<Task> Execute,
        bool IsEnabled = true,
        bool IsDanger = false);

    private void AttachMobileListActions<T>(
        ListBox list,
        Action<T> select,
        Func<T, Task>? onTap,
        Action<T, ListBoxItem>? onHold)
        where T : class
    {
        if (onHold is not null)
        {
            list.SetValue(InputElement.IsHoldingEnabledProperty, true);
            list.ContainerPrepared += (_, args) =>
                args.Container.SetValue(InputElement.IsHoldingEnabledProperty, true);
        }
        if (onTap is not null)
        {
            var activating = false;
            list.AddHandler(InputElement.TappedEvent, async (_, args) =>
            {
                if (FindMobileListContainer<T>(list, args.Source) is not { DataContext: T item }) return;
                list.SelectedItem = item;
                select(item);
                args.Handled = true;
                if (activating) return;
                activating = true;
                try { await onTap(item); }
                catch (Exception exception) { ShowError(exception); }
                finally { activating = false; }
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        if (onHold is null) return;
        list.AddHandler(InputElement.HoldingEvent, (_, args) =>
        {
            if (args.HoldingState != HoldingState.Started ||
                FindMobileListContainer<T>(list, args.Source) is not { DataContext: T item } container) return;
            list.SelectedItem = item;
            select(item);
            args.Handled = true;
            onHold(item, container);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.RightTappedEvent, (_, args) =>
        {
            // Avalonia also raises RightTapped when a touch hold is released.
            // HoldingStarted already opened that menu, so only handle mouse right clicks here.
            if (args.Pointer.Type != PointerType.Mouse) return;
            if (FindMobileListContainer<T>(list, args.Source) is not { DataContext: T item } container) return;
            list.SelectedItem = item;
            select(item);
            args.Handled = true;
            onHold(item, container);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static ListBoxItem? FindMobileListContainer<T>(ListBox list, object? source)
        where T : class
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, list)) break;
            if (visual is ListBoxItem container && container.DataContext is T) return container;
        }
        return null;
    }

    private void ShowMobileItemMenu(ListBoxItem anchor, params MobileItemAction[] actions)
    {
        if (actions.Length == 0) return;
        CloseMobileItemMenu();
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Pointer,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipY |
                PopupPositionerConstraintAdjustment.SlideX |
                PopupPositionerConstraintAdjustment.SlideY,
            HorizontalOffset = 8,
            VerticalOffset = 8,
            MinWidth = 180,
        };
        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                Header = action.Label,
                IsEnabled = action.IsEnabled,
                MinHeight = 44,
            };
            if (action.IsDanger && action.IsEnabled) item.Foreground = Brush("CanXDangerBrush");
            item.Click += async (_, _) =>
            {
                CloseMobileItemMenu();
                try { await action.Execute(); }
                catch (Exception exception) { ShowError(exception); }
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeItemMenu, menu)) _activeItemMenu = null;
        };
        _activeItemMenu = menu;
        menu.Open(anchor);
    }

    private void CloseMobileItemMenu()
    {
        var menu = _activeItemMenu;
        _activeItemMenu = null;
        menu?.Close();
    }
}
