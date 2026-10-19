// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;

namespace CanX.Plugins.Security;

public sealed class PluginPermissionSet
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> grants;

    private PluginPermissionSet(
        string pluginId,
        IReadOnlyDictionary<string, IReadOnlySet<string>> grants)
    {
        PluginId = pluginId;
        this.grants = grants;
    }

    public string PluginId { get; }

    public static PluginPermissionSet FromManifest(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var grants = manifest.Permissions.ToDictionary(
            permission => permission.Capability,
            permission => (IReadOnlySet<string>)new HashSet<string>(permission.Scopes, StringComparer.Ordinal),
            StringComparer.Ordinal);
        return new PluginPermissionSet(manifest.Id, grants);
    }

    public bool Allows(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return grants.ContainsKey(capability);
    }

    public bool Allows(string capability, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return grants.TryGetValue(capability, out var scopes) && scopes.Contains(scope);
    }

    public bool AllowsHttp(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !grants.TryGetValue(PluginCapabilities.Http, out var scopes))
        {
            return false;
        }

        return AllowsHost(uri, scopes, allowGlobalWildcard: true);
    }

    public bool AllowsRemoteImage(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !grants.TryGetValue(PluginCapabilities.UiRemoteImages, out var scopes))
        {
            return false;
        }

        return AllowsHost(uri, scopes, allowGlobalWildcard: true);
    }

    private static bool AllowsHost(
        Uri uri,
        IReadOnlySet<string> scopes,
        bool allowGlobalWildcard)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        foreach (var scope in scopes)
        {
            if (allowGlobalWildcard && scope == "*")
            {
                return true;
            }
            if (scope.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = scope[1..];
                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (string.Equals(host, scope, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void Demand(string capability)
    {
        if (!Allows(capability))
        {
            throw new PluginPermissionDeniedException(PluginId, capability, null);
        }
    }

    public void Demand(string capability, string scope)
    {
        if (!Allows(capability, scope))
        {
            throw new PluginPermissionDeniedException(PluginId, capability, scope);
        }
    }

    public void DemandHttp(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!AllowsHttp(uri))
        {
            throw new PluginPermissionDeniedException(PluginId, PluginCapabilities.Http, uri.ToString());
        }
    }

    public void DemandRemoteImage(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!AllowsRemoteImage(uri))
        {
            throw new PluginPermissionDeniedException(
                PluginId,
                PluginCapabilities.UiRemoteImages,
                uri.ToString());
        }
    }

    public bool AllowsServiceContract(string capability, string contract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        return grants.TryGetValue(capability, out var scopes) &&
            (scopes.Contains("*") || scopes.Contains(contract));
    }

    public void DemandServiceContract(string capability, string contract)
    {
        if (!AllowsServiceContract(capability, contract))
        {
            throw new PluginPermissionDeniedException(PluginId, capability, contract);
        }
    }

    public bool AllowsServiceInvocation(string serviceId, string contract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        return grants.TryGetValue(PluginCapabilities.ServicesInvoke, out var scopes) &&
            (scopes.Contains("*") || scopes.Contains(serviceId) || scopes.Contains(contract));
    }

    public void DemandServiceInvocation(string serviceId, string contract)
    {
        if (!AllowsServiceInvocation(serviceId, contract))
        {
            throw new PluginPermissionDeniedException(
                PluginId,
                PluginCapabilities.ServicesInvoke,
                serviceId);
        }
    }

    internal IReadOnlySet<string> GetScopes(string capability) =>
        grants.TryGetValue(capability, out var scopes) ? scopes : EmptyScopes;

    private static IReadOnlySet<string> EmptyScopes { get; } = new HashSet<string>(StringComparer.Ordinal);
}
