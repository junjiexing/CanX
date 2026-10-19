// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Security;

public sealed class PluginPermissionDeniedException : Exception
{
    public PluginPermissionDeniedException(string pluginId, string capability, string? scope)
        : base(scope is null
            ? $"Plugin '{pluginId}' is not allowed to use '{capability}'."
            : $"Plugin '{pluginId}' is not allowed to use '{capability}' with scope '{scope}'.")
    {
        PluginId = pluginId;
        Capability = capability;
        Scope = scope;
    }

    public string PluginId { get; }

    public string Capability { get; }

    public string? Scope { get; }
}
