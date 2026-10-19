// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Application.Discovery;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Plugins.Services;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class OnlineSubtitleCatalogTests
{
    [Fact]
    public async Task SearchAndResolve_UsesEnabledProviderAndReturnsPlayableFiles()
    {
        var registry = new PluginServiceRegistry();
        var catalog = new OnlineSubtitleCatalog(registry);
        Assert.False(catalog.HasProviders);

        using var searchInput = JsonDocument.Parse("""
            {"type":"object","required":["query","limit"],"properties":{"query":{"type":"string"},"limit":{"type":"integer"}}}
            """);
        using var searchOutput = JsonDocument.Parse("""
            {"type":"object","required":["items"],"properties":{"items":{"type":"array"}}}
            """);
        using var resolveInput = JsonDocument.Parse("""
            {"type":"object","required":["id","data"],"properties":{"id":{"type":"string"},"data":{"type":"object"}}}
            """);
        using var resolveOutput = JsonDocument.Parse("""
            {"type":"object","required":["sources"],"properties":{"sources":{"type":"array"}}}
            """);
        var permissions = PluginPermissionSet.FromManifest(new PluginManifest(
            1, "org.canx.subtitle.test", "Test subtitles", null, "1.0.0",
            new PluginApiVersion(2, 0), "main.mjs",
            [new PluginPermission(PluginCapabilities.ServicesProvide,
                ["org.canx.subtitle.search", "org.canx.media.resolve"])]));
        string? searchQuery = null;
        string? resolvedId = null;
        int? resolvedFileId = null;
        var otherSearchCalls = 0;
        var resolveFails = false;
        using var search = registry.Register(
            "org.canx.subtitle.test", "search", "org.canx.subtitle.search", "1.0",
            "Test provider", null, PluginServiceRisk.Network, [PluginServiceAudience.Ai],
            searchInput.RootElement, searchOutput.RootElement, permissions,
            (input, _, _) =>
            {
                using var request = JsonDocument.Parse(input);
                searchQuery = request.RootElement.GetProperty("query").GetString();
                if (searchQuery == "Unavailable")
                    return ValueTask.FromResult("""{"items":[],"error":"请配置 API Token"}""");
                return ValueTask.FromResult("""
                    {"items":[{"id":"42","title":"Example.zh.srt","language":"zh-CN","format":"srt","resolveData":{"fileId":42}}]}
                    """);
            });
        using var resolve = registry.Register(
            "org.canx.subtitle.test", "resolve", "org.canx.media.resolve", "1.0",
            "Test resolver", null, PluginServiceRisk.Network, [PluginServiceAudience.Ai],
            resolveInput.RootElement, resolveOutput.RootElement, permissions,
            (input, _, _) =>
            {
                using var request = JsonDocument.Parse(input);
                resolvedId = request.RootElement.GetProperty("id").GetString();
                resolvedFileId = request.RootElement.GetProperty("data").GetProperty("fileId").GetInt32();
                if (resolveFails)
                    return ValueTask.FromResult("""{"sources":[],"error":"Token 已过期"}""");
                return ValueTask.FromResult("""
                    {"sources":[
                      {"mediaType":"subtitle","label":"Example.zh.srt","uri":"https://file.example.org/42.srt"},
                      {"mediaType":"video","label":"wrong.mkv","uri":"https://file.example.org/wrong.mkv"},
                      {"mediaType":"subtitle","label":"local.srt","uri":"file:///tmp/local.srt"}
                    ]}
                    """);
            });
        var otherPermissions = PluginPermissionSet.FromManifest(new PluginManifest(
            1, "org.canx.subtitle.other", "Other subtitles", null, "1.0.0",
            new PluginApiVersion(2, 0), "main.mjs",
            [new PluginPermission(PluginCapabilities.ServicesProvide, ["org.canx.subtitle.search"])]));
        using var otherSearch = registry.Register(
            "org.canx.subtitle.other", "search", "org.canx.subtitle.search", "1.0",
            "Other provider", null, PluginServiceRisk.Network, [PluginServiceAudience.Ai],
            searchInput.RootElement, searchOutput.RootElement, otherPermissions,
            (_, _, _) =>
            {
                otherSearchCalls++;
                return ValueTask.FromResult("""{"items":[{"id":"99","title":"Other.srt"}]}""");
            });

        Assert.True(catalog.HasProviders);
        Assert.Equal(2, catalog.Providers.Count);
        var selected = Assert.Single(catalog.Providers, static provider => provider.Title == "Test provider");
        var page = await catalog.SearchAsync("Example Movie", selected.Id);
        var item = Assert.Single(page.Items);
        var sources = await catalog.ResolveAsync(item);

        Assert.Empty(page.Errors);
        Assert.Equal("Example Movie", searchQuery);
        Assert.Equal("Test provider", item.Provider);
        Assert.Equal(0, otherSearchCalls);
        Assert.Equal("42", resolvedId);
        Assert.Equal(42, resolvedFileId);
        Assert.Equal("Example.zh.srt", Assert.Single(sources).Name);

        var other = Assert.Single(catalog.Providers, static provider => provider.Title == "Other provider");
        var otherPage = await catalog.SearchAsync("Example Movie", other.Id);
        Assert.Equal("Other provider", Assert.Single(otherPage.Items).Provider);
        Assert.Equal(1, otherSearchCalls);

        var failedPage = await catalog.SearchAsync("Unavailable", selected.Id);
        Assert.Equal("Test provider：请配置 API Token", Assert.Single(failedPage.Errors));
        resolveFails = true;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ResolveAsync(item));
        Assert.Equal("Test provider：Token 已过期", exception.Message);
    }
}
