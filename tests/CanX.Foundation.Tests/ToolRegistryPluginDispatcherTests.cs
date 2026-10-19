// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Application.Plugins;
using CanX.Plugins.Runtime;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class ToolRegistryPluginDispatcherTests
{
    [Fact]
    public async Task InvokeAsync_ForwardsPluginIdentityAndJsonArguments()
    {
        var audit = new InMemoryToolAuditSink();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), audit);
        var tool = new EchoTool("test.echo", ToolRisk.ReadOnly);
        registry.ReplaceHandlers([tool]);
        var dispatcher = new ToolRegistryPluginDispatcher(registry);

        var result = await dispatcher.InvokeAsync(
            new PluginToolRequest("org.canx.test", "test.echo", "{\"message\":\"hello\"}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("hello", result.Json, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("plugin:org.canx.test", entry.Caller);
        Assert.True(entry.Allowed);
        Assert.NotNull(tool.LastContext);
        Assert.False(tool.LastContext.HasUserApproval);
        Assert.Equal(ToolCallerKind.Plugin, tool.LastContext.CallerKind);
        Assert.Contains("test.echo", tool.LastContext.Grants!);
    }

    [Fact]
    public void PluginAllowList_CoversEveryWritableSdkAction()
    {
        var sdkWriteTools = CanX.Plugins.Sdk.PluginSdkToolPolicy.SupportedActions
            .Where(static entry => entry.Key is CanX.Plugins.Manifest.PluginCapabilities.PlaybackControl
                or CanX.Plugins.Manifest.PluginCapabilities.DownloadsManage)
            .SelectMany(static entry => entry.Value.Select(action =>
                CanX.Plugins.Sdk.PluginSdkToolPolicy.Resolve(
                    entry.Key == CanX.Plugins.Manifest.PluginCapabilities.PlaybackControl ? "playback" : "downloads",
                    action).ToolName));

        Assert.All(sdkWriteTools, tool => Assert.Contains(tool, ToolRegistryPluginDispatcher.PluginInvocableWriteTools));
    }

    [Theory]
    [InlineData("settings.set", ToolRisk.LocalWrite)]
    [InlineData("plugins.enable", ToolRisk.Destructive)]
    [InlineData("downloads.global.set", ToolRisk.LocalWrite)]
    [InlineData("downloads.options.set", ToolRisk.LocalWrite)]
    [InlineData("downloads.completion.set", ToolRisk.LocalWrite)]
    [InlineData("playback.property.set", ToolRisk.Destructive)]
    [InlineData("media.sources.add", ToolRisk.Network)]
    public async Task InvokeAsync_RejectsWriteToolsOutsidePluginAllowList(string toolName, ToolRisk risk)
    {
        var tool = new EchoTool(toolName, risk);
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers([tool]);
        var dispatcher = new ToolRegistryPluginDispatcher(registry);

        var result = await dispatcher.InvokeAsync(
            new PluginToolRequest("org.canx.test", toolName, "{}"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("tool_not_plugin_invocable", result.Json, StringComparison.Ordinal);
        Assert.Null(tool.LastContext);
    }

    [Fact]
    public async Task InvokeAsync_AllowsReversiblePlaybackControl()
    {
        var tool = new EchoTool("playback.pause", ToolRisk.LocalWrite);
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers([tool]);
        var dispatcher = new ToolRegistryPluginDispatcher(registry);

        var result = await dispatcher.InvokeAsync(
            new PluginToolRequest("org.canx.test", "playback.pause", "{}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(tool.LastContext);
    }

    [Fact]
    public async Task InvokeAsync_UnknownToolReturnsStructuredFailure()
    {
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        var dispatcher = new ToolRegistryPluginDispatcher(registry);

        var result = await dispatcher.InvokeAsync(
            new PluginToolRequest("org.canx.test", "missing", "{}"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("tool_not_found", result.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("downloads.add")]
    [InlineData("downloads.addBatch")]
    [InlineData("search.download")]
    public async Task InvokeAsync_DownloadCreationRequiresStandardConfirmationPage(string toolName)
    {
        var tool = new NamedTool(toolName);
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers([tool]);
        var dispatcher = new ToolRegistryPluginDispatcher(registry);

        var result = await dispatcher.InvokeAsync(
            new PluginToolRequest("org.canx.test", toolName, "{}"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("user_confirmation_required", result.Json, StringComparison.Ordinal);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("not-json")]
    public async Task InvokeAsync_NonObjectArgumentsReturnStructuredFailure(string input)
    {
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        var dispatcher = new ToolRegistryPluginDispatcher(registry);

        var result = await dispatcher.InvokeAsync(
            new PluginToolRequest("org.canx.test", "test.echo", input),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("invalid_arguments", result.Json, StringComparison.Ordinal);
    }

    private sealed class EchoTool(string name, ToolRisk risk) : IToolHandler
    {
        public ToolCallContext? LastContext { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            name,
            "Echoes its arguments.",
            risk,
            new JsonObject(),
            new JsonObject());

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken)
        {
            LastContext = context;
            return ValueTask.FromResult(ToolResult.Success(arguments.DeepClone()));
        }
    }

    private sealed class NamedTool(string name) : IToolHandler
    {
        public int InvocationCount { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            name,
            "Test Tool.",
            ToolRisk.Network,
            new JsonObject(),
            new JsonObject());

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult(ToolResult.Success());
        }
    }
}
