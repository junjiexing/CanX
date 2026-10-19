// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;

namespace CanX.Plugins.Packaging;

public sealed record PluginInstallation(
    PluginManifest Manifest,
    string DirectoryPath,
    string PackageSha256,
    DateTimeOffset InstalledAt,
    PluginSignatureInfo? Signature = null);
