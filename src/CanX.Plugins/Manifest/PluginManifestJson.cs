// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace CanX.Plugins.Manifest;

internal sealed class PluginManifestDocument
{
    public int ManifestVersion { get; init; }

    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Version { get; init; }

    public string? ApiVersion { get; init; }

    public string? Entry { get; init; }

    public PluginPermissionDocument[]? Permissions { get; init; }

    public PluginUiManifestDocument? Ui { get; init; }

    public PluginConfigurationManifestDocument? Configuration { get; init; }
}

internal sealed class PluginConfigurationManifestDocument
{
    public string? Title { get; init; }
}

internal sealed class PluginUiManifestDocument
{
    public string? Title { get; init; }

    public PluginUiIconDocument? Icon { get; init; }
}

internal sealed class PluginUiIconDocument
{
    public string? Builtin { get; init; }

    public string? Asset { get; init; }
}

internal sealed class PluginPermissionDocument
{
    public string? Capability { get; init; }

    public string[]? Scopes { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginManifestDocument))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;
