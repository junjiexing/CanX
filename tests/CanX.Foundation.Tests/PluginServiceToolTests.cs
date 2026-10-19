// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using CanX.Application.Plugins;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Plugins.Services;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PluginServiceToolTests
{
    [Fact]
    public async Task AiTools_DiscoverDescribeAndInvokeExposedService()
    {
        var services = new PluginServiceRegistry();
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var registration = services.Register(
            "org.canx.source",
            "search",
            "org.canx.search",
            "1.0",
            "Search",
            "Search data",
            PluginServiceRisk.Network,
            [PluginServiceAudience.Ai],
            schema.RootElement,
            schema.RootElement,
            Permissions(),
            (input, context, _) =>
            {
                Assert.Equal("assistant", context.Caller);
                return ValueTask.FromResult(input);
            });
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(PluginServiceToolCatalog.Create(services));

        var listed = await registry.InvokeAsync(
            "services.list",
            new JsonObject { ["contract"] = "org.canx.search" },
            new ToolCallContext("assistant"));
        Assert.True(listed.IsSuccess);
        Assert.Contains("org.canx.source/search", listed.Value!.ToJsonString(), StringComparison.Ordinal);

        var described = await registry.InvokeAsync(
            "services.describe",
            new JsonObject { ["serviceId"] = "org.canx.source/search" },
            new ToolCallContext("assistant"));
        Assert.True(described.IsSuccess);
        Assert.Contains("inputSchema", described.Value!.ToJsonString(), StringComparison.Ordinal);

        var denied = await registry.InvokeAsync(
            "services.invoke",
            new JsonObject { ["serviceId"] = "org.canx.source/search", ["input"] = new JsonObject() },
            new ToolCallContext("assistant"));
        Assert.False(denied.IsSuccess);
        Assert.Equal("permission_denied", denied.ErrorCode);

        var invoked = await registry.InvokeAsync(
            "services.invoke",
            new JsonObject { ["serviceId"] = "org.canx.source/search", ["input"] = new JsonObject() },
            new ToolCallContext("assistant", HasUserApproval: true));
        Assert.True(invoked.IsSuccess);
        Assert.Equal("{}", invoked.Value!.ToJsonString());
    }

    [Theory]
    [InlineData("org.canx.media.search")]
    [InlineData("org.canx.media.resolve")]
    [InlineData("org.canx.subtitle.search")]
    [InlineData("org.canx.bittorrent.search")]
    public async Task AiTools_CannotBypassCachedSearchWorkflows(string contract)
    {
        var services = new PluginServiceRegistry();
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var registration = services.Register(
            "org.canx.source",
            "protected",
            contract,
            "1.0",
            "Protected search",
            null,
            PluginServiceRisk.Network,
            [PluginServiceAudience.Ai],
            schema.RootElement,
            schema.RootElement,
            Permissions(contract),
            (input, _, _) => ValueTask.FromResult(input));
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(PluginServiceToolCatalog.Create(services));

        var result = await registry.InvokeAsync(
            "services.invoke",
            new JsonObject { ["serviceId"] = "org.canx.source/protected", ["input"] = new JsonObject() },
            new ToolCallContext("assistant", HasUserApproval: true));

        Assert.False(result.IsSuccess);
        Assert.Equal("use_search_tools", result.ErrorCode);
    }

    private static PluginPermissionSet Permissions(string contract = "org.canx.search") => PluginPermissionSet.FromManifest(new PluginManifest(
        1,
        "org.canx.source",
        "Source",
        null,
        "1.0.0",
        new PluginApiVersion(2, 0),
        "main.mjs",
        [new PluginPermission(PluginCapabilities.ServicesProvide, [contract])]));
}
