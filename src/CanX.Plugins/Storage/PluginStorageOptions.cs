// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Storage;

public sealed record PluginStorageOptions
{
    public int MaximumKeyBytes { get; init; } = 256;

    public int MaximumValueBytes { get; init; } = 64 * 1024;

    public int MaximumEntries { get; init; } = 1024;

    public int MaximumTotalBytes { get; init; } = 1024 * 1024;

    internal void Validate()
    {
        if (MaximumKeyBytes <= 0 || MaximumValueBytes <= 0 || MaximumEntries <= 0 ||
            MaximumTotalBytes < MaximumValueBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PluginStorageOptions),
                "Plugin storage limits must be positive and the total must fit one value.");
        }
    }
}
