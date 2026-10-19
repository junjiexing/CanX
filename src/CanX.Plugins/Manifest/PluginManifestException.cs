// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Manifest;

public sealed class PluginManifestException : Exception
{
    public PluginManifestException(IReadOnlyList<string> errors)
        : base($"Invalid plugin manifest: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
