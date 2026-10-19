// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;

namespace CanX.Plugins.Security;

public sealed record PluginPermissionChange(
    string Capability,
    IReadOnlyList<string> AddedScopes,
    IReadOnlyList<string> RemovedScopes);

public sealed record PluginPermissionDiff(
    IReadOnlyList<PluginPermissionChange> Added,
    IReadOnlyList<PluginPermissionChange> Removed,
    IReadOnlyList<PluginPermissionChange> Changed)
{
    public bool HasPrivilegeIncrease => Added.Count > 0 || Changed.Any(change => change.AddedScopes.Count > 0);

    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;

    public static PluginPermissionDiff Compare(PluginManifest current, PluginManifest proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        if (!string.Equals(current.Id, proposed.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Permission diff requires manifests with the same plugin ID.", nameof(proposed));
        }

        var oldSet = PluginPermissionSet.FromManifest(current);
        var newSet = PluginPermissionSet.FromManifest(proposed);
        var oldCapabilities = current.Permissions
            .Select(permission => permission.Capability)
            .ToHashSet(StringComparer.Ordinal);
        var newCapabilities = proposed.Permissions
            .Select(permission => permission.Capability)
            .ToHashSet(StringComparer.Ordinal);

        var added = newCapabilities
            .Except(oldCapabilities, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(capability => new PluginPermissionChange(
                capability,
                Sorted(newSet.GetScopes(capability)),
                []))
            .ToArray();
        var removed = oldCapabilities
            .Except(newCapabilities, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(capability => new PluginPermissionChange(
                capability,
                [],
                Sorted(oldSet.GetScopes(capability))))
            .ToArray();
        var changed = oldCapabilities
            .Intersect(newCapabilities, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(capability => new PluginPermissionChange(
                capability,
                Sorted(newSet.GetScopes(capability).Except(oldSet.GetScopes(capability), StringComparer.Ordinal)),
                Sorted(oldSet.GetScopes(capability).Except(newSet.GetScopes(capability), StringComparer.Ordinal))))
            .Where(change => change.AddedScopes.Count > 0 || change.RemovedScopes.Count > 0)
            .ToArray();

        return new PluginPermissionDiff(added, removed, changed);
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> scopes) =>
        scopes.Order(StringComparer.Ordinal).ToArray();
}
