// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;
using CanX.Plugins.Runtime;
using CanX.Plugins.Packaging;

namespace CanX.Plugins.Lifecycle;

public sealed record PluginStatus(
    PluginManifest Manifest,
    string DirectoryPath,
    string PackageSha256,
    DateTimeOffset InstalledAt,
    bool IsEnabled,
    bool RequiresPermissionApproval,
    PluginHostState RuntimeState,
    string? LastError,
    PluginSignatureInfo? Signature = null,
    string? RollbackVersion = null);
