// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace CanX.Plugins.Services;

public enum PluginServiceAudience
{
    Plugin,
    Ai,
}

public enum PluginServiceRisk
{
    ReadOnly,
    Network,
    LocalWrite,
    Destructive,
}

public sealed record PluginServiceDescriptor(
    string Id,
    string PluginId,
    string LocalId,
    string Contract,
    string ContractVersion,
    string Title,
    string? Description,
    PluginServiceRisk Risk,
    IReadOnlyList<PluginServiceAudience> Audiences,
    JsonElement InputSchema,
    JsonElement OutputSchema);

public sealed record PluginServiceCallContext(
    string Caller,
    IReadOnlyList<string> PluginChain)
{
    public static PluginServiceCallContext ForPlugin(string pluginId) =>
        new($"plugin:{pluginId}", [pluginId]);

    public static PluginServiceCallContext ForAi(string caller = "ai") => new(caller, []);
}

public sealed class PluginServiceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
