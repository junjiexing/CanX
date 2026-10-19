// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using CanX.Plugins.Manifest;

namespace CanX.Plugins.Security;

public static class PluginPermissionFingerprint
{
    public static string Compute(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = new StringBuilder();
        foreach (var permission in manifest.Permissions.OrderBy(
                     permission => permission.Capability,
                     StringComparer.Ordinal))
        {
            canonical.Append(permission.Capability).Append('\n');
            foreach (var scope in permission.Scopes.Order(StringComparer.Ordinal))
            {
                canonical.Append('\t').Append(scope).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}
