// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Packaging;

public sealed record PluginPackageLimits(
    long MaximumArchiveBytes,
    long MaximumUncompressedBytes,
    long MaximumEntryBytes,
    int MaximumEntries)
{
    public static PluginPackageLimits Default { get; } = new(
        MaximumArchiveBytes: 32L * 1024 * 1024,
        MaximumUncompressedBytes: 128L * 1024 * 1024,
        MaximumEntryBytes: 64L * 1024 * 1024,
        MaximumEntries: 4096);

    internal void Validate()
    {
        if (MaximumArchiveBytes <= 0 ||
            MaximumUncompressedBytes <= 0 ||
            MaximumEntryBytes <= 0 ||
            MaximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PluginPackageLimits), "Package limits must be positive.");
        }

        if (MaximumEntryBytes > MaximumUncompressedBytes)
        {
            throw new ArgumentException(
                "Maximum entry size cannot exceed maximum uncompressed package size.",
                nameof(PluginPackageLimits));
        }
    }
}
