// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginPermissionDiffTests
{
    [Fact]
    public void Compare_ReportsCapabilitiesAndScopeChangesSeparately()
    {
        var current = Manifest(
            new PluginPermission(PluginCapabilities.Storage, []),
            new PluginPermission(PluginCapabilities.Http, ["api.example.test", "old.example.test"]));
        var proposed = Manifest(
            new PluginPermission(PluginCapabilities.Http, ["api.example.test", "new.example.test"]),
            new PluginPermission(PluginCapabilities.PlaybackRead, []));

        var diff = PluginPermissionDiff.Compare(current, proposed);

        Assert.True(diff.HasChanges);
        Assert.True(diff.HasPrivilegeIncrease);
        Assert.Collection(diff.Added, change => Assert.Equal(PluginCapabilities.PlaybackRead, change.Capability));
        Assert.Collection(diff.Removed, change => Assert.Equal(PluginCapabilities.Storage, change.Capability));
        Assert.Collection(
            diff.Changed,
            change =>
            {
                Assert.Equal(PluginCapabilities.Http, change.Capability);
                Assert.Equal(["new.example.test"], change.AddedScopes);
                Assert.Equal(["old.example.test"], change.RemovedScopes);
            });
    }

    [Fact]
    public void Compare_RemovingPermissions_IsNotAPrivilegeIncrease()
    {
        var current = Manifest(new PluginPermission(PluginCapabilities.PlaybackControl, []));
        var proposed = Manifest();

        var diff = PluginPermissionDiff.Compare(current, proposed);

        Assert.True(diff.HasChanges);
        Assert.False(diff.HasPrivilegeIncrease);
    }

    [Fact]
    public void Compare_DifferentPluginIds_IsRejected()
    {
        var current = Manifest();
        var proposed = current with { Id = "org.canx.other" };

        Assert.Throws<ArgumentException>(() => PluginPermissionDiff.Compare(current, proposed));
    }

    private static PluginManifest Manifest(params PluginPermission[] permissions) => new(
        ManifestVersion: 1,
        Id: "org.canx.permissions",
        Name: "Permissions",
        Description: null,
        Version: "1.0.0",
        ApiVersion: new PluginApiVersion(2, 0),
        Entry: "main.mjs",
        Permissions: permissions);
}
