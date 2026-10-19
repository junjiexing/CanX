// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginPermissionSetTests
{
    [Fact]
    public void EmptyManifest_DeniesEveryCapabilityByDefault()
    {
        var permissions = PluginPermissionSet.FromManifest(Parse([]));

        Assert.False(permissions.Allows(PluginCapabilities.PlaybackRead));
        Assert.False(permissions.Allows(PluginCapabilities.ToolsInvoke, "playback.open"));
        Assert.False(permissions.AllowsHttp(new Uri("https://api.example.test/video")));
        Assert.Throws<PluginPermissionDeniedException>(() =>
            permissions.Demand(PluginCapabilities.Storage));
    }

    [Fact]
    public void ScopedPermissions_RequireExactToolAndSecretNames()
    {
        var permissions = PluginPermissionSet.FromManifest(Parse(
        [
            (PluginCapabilities.ToolsInvoke, ["playback.open"]),
            (PluginCapabilities.Secrets, ["api-token"]),
        ]));

        Assert.True(permissions.Allows(PluginCapabilities.ToolsInvoke, "playback.open"));
        Assert.False(permissions.Allows(PluginCapabilities.ToolsInvoke, "playback.stop"));
        Assert.True(permissions.Allows(PluginCapabilities.Secrets, "api-token"));
        Assert.False(permissions.Allows(PluginCapabilities.Secrets, "api-token-backup"));
    }

    [Theory]
    [InlineData("https://api.example.test/video", true)]
    [InlineData("https://cdn.example.test/video", false)]
    [InlineData("https://images.cdn.example.test/video", true)]
    [InlineData("https://example.test/video", false)]
    [InlineData("https://fakecdn.example.test.evil.test/video", false)]
    [InlineData("ftp://api.example.test/video", false)]
    public void HttpPermission_UsesOriginHostAndWildcardBoundary(string address, bool expected)
    {
        var permissions = PluginPermissionSet.FromManifest(Parse(
        [
            (PluginCapabilities.Http, ["api.example.test", "*.cdn.example.test"]),
        ]));

        Assert.Equal(expected, permissions.AllowsHttp(new Uri(address)));
    }

    [Fact]
    public void HttpPermission_GlobalWildcardAllowsAnyHttpHostWithoutCredentials()
    {
        var permissions = PluginPermissionSet.FromManifest(Parse(
        [
            (PluginCapabilities.Http, ["*"]),
        ]));

        Assert.True(permissions.AllowsHttp(new Uri("https://unrelated.example/value")));
        Assert.True(permissions.AllowsHttp(new Uri("http://192.168.1.10:7890/")));
        Assert.False(permissions.AllowsHttp(new Uri("ftp://unrelated.example/value")));
        Assert.False(permissions.AllowsHttp(new Uri("https://user@unrelated.example/value")));
    }

    [Theory]
    [InlineData("https://images.example.test/cover.webp", true)]
    [InlineData("https://cdn.example.test/cover.webp", false)]
    [InlineData("https://one.cdn.example.test/cover.webp", true)]
    [InlineData("ftp://images.example.test/cover.webp", false)]
    [InlineData("https://user@images.example.test/cover.webp", false)]
    public void RemoteImagePermission_UsesIndependentHostScopes(string address, bool expected)
    {
        var permissions = PluginPermissionSet.FromManifest(Parse(
        [
            (PluginCapabilities.UiRemoteImages, ["images.example.test", "*.cdn.example.test"]),
        ]));

        Assert.Equal(expected, permissions.AllowsRemoteImage(new Uri(address)));
        Assert.False(permissions.AllowsHttp(new Uri("https://images.example.test/cover.webp")));
    }

    [Fact]
    public void RemoteImagePermission_GlobalWildcardAllowsAnyHttpOriginOnly()
    {
        var permissions = PluginPermissionSet.FromManifest(Parse(
        [
            (PluginCapabilities.UiRemoteImages, ["*"]),
        ]));

        Assert.True(permissions.AllowsRemoteImage(new Uri("https://unrelated.example/cover.jpg")));
        Assert.True(permissions.AllowsRemoteImage(new Uri("http://203.0.113.5/cover.jpg")));
        Assert.False(permissions.AllowsRemoteImage(new Uri("file:///tmp/cover.jpg")));
    }

    [Fact]
    public void Demand_ReportsPluginCapabilityAndScope()
    {
        var permissions = PluginPermissionSet.FromManifest(Parse([]));

        var exception = Assert.Throws<PluginPermissionDeniedException>(() =>
            permissions.Demand(PluginCapabilities.ToolsInvoke, "downloads.add"));

        Assert.Equal("org.canx.permissions", exception.PluginId);
        Assert.Equal(PluginCapabilities.ToolsInvoke, exception.Capability);
        Assert.Equal("downloads.add", exception.Scope);
    }

    private static PluginManifest Parse((string Capability, string[] Scopes)[] permissions)
    {
        var permissionJson = string.Join(
            ",",
            permissions.Select(permission =>
            {
                var scopes = permission.Scopes.Length == 0
                    ? string.Empty
                    : $",\"scopes\":[{string.Join(',', permission.Scopes.Select(scope => $"\"{scope}\""))}]";
                return $"{{\"capability\":\"{permission.Capability}\"{scopes}}}";
            }));
        var json =
            $$"""
            {
              "manifestVersion": 1,
              "id": "org.canx.permissions",
              "name": "Permissions",
              "version": "1.0.0",
              "apiVersion": "2.0",
              "entry": "main.mjs",
              "permissions": [{{permissionJson}}]
            }
            """;
        return PluginManifestReader.Parse(Encoding.UTF8.GetBytes(json));
    }
}
