// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Manifest;

public sealed record PluginManifest(
    int ManifestVersion,
    string Id,
    string Name,
    string? Description,
    string Version,
    PluginApiVersion ApiVersion,
    string Entry,
    IReadOnlyList<PluginPermission> Permissions,
    PluginUiManifest? Ui = null,
    PluginConfigurationManifest? Configuration = null);

public sealed record PluginUiManifest(string Title, PluginUiIcon Icon);

public sealed record PluginConfigurationManifest(string Title);

public enum PluginUiIconKind
{
    BuiltIn,
    Asset,
}

public sealed record PluginUiIcon(PluginUiIconKind Kind, string Value);

public readonly record struct PluginApiVersion(int Major, int Minor)
{
    public override string ToString() => $"{Major}.{Minor}";
}

public sealed record PluginPermission(string Capability, IReadOnlyList<string> Scopes);
