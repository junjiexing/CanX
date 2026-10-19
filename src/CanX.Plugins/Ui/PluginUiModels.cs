// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Plugins.Manifest;

namespace CanX.Plugins.Ui;

public enum PluginUiContributionKind
{
    Page,
    Panel,
    Command,
    Menu,
    Setting,
    StatusCard,
}

public sealed record PluginUiContribution(
    string Id,
    string PluginId,
    string LocalId,
    string Title,
    PluginUiContributionKind Kind,
    string Slot,
    JsonElement Content,
    bool IsInteractive);

public sealed record PluginUiPage(
    string PluginId,
    string Title,
    PluginUiIcon Icon,
    string PluginDirectory,
    JsonElement? Content,
    bool IsMounted,
    bool IsInteractive,
    string? BadgeText,
    string BadgeTone,
    long Revision);

public enum PluginUiPageChangeKind
{
    Declared,
    Rendered,
    Updated,
    Badge,
    Removed,
}

public sealed class PluginUiPageChangedEventArgs(
    string pluginId,
    PluginUiPageChangeKind kind,
    PluginUiPage? page = null,
    IReadOnlyList<string>? updatedComponentIds = null) : EventArgs
{
    public string PluginId { get; } = pluginId;

    public PluginUiPageChangeKind Kind { get; } = kind;

    public PluginUiPage? Page { get; } = page;

    public IReadOnlyList<string> UpdatedComponentIds { get; } = updatedComponentIds ?? [];
}

public sealed class PluginConfigurationCloseRequestedEventArgs(string pluginId) : EventArgs
{
    public string PluginId { get; } = pluginId;
}

public sealed class PluginUiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
