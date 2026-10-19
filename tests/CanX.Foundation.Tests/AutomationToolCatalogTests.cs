// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Application.Automation;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class AutomationToolCatalogTests
{
    [Fact]
    public async Task Catalog_CoversPlannedNamespacesAndRequiresApprovalForWrites()
    {
        var service = new StubAutomationService();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(AutomationToolCatalog.Create(service, service, service, service, service, registry));

        var coverage = await registry.InvokeAsync("diagnostics.tools", null, new ToolCallContext("agent"));
        var denied = await registry.InvokeAsync(
            "ui.navigate",
            new JsonObject { ["page"] = "downloads" },
            new ToolCallContext("agent"));
        var allowed = await registry.InvokeAsync(
            "ui.navigate",
            new JsonObject { ["page"] = "downloads" },
            new ToolCallContext("agent", true));

        Assert.True(coverage.IsSuccess);
        var namespaces = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(coverage.Value)["namespaces"]);
        Assert.Equal(5, namespaces["media"]?.GetValue<int>());
        Assert.Equal(6, namespaces["plugins"]?.GetValue<int>());
        Assert.Equal(1, coverage.Value?["schemaVersion"]?.GetValue<int>());
        Assert.True(coverage.Value?["confirmationRequired"]?.GetValue<int>() > 0);
        var tools = Assert.IsType<JsonArray>(coverage.Value?["tools"]);
        Assert.All(tools.OfType<JsonObject>(), tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]?.GetValue<string>()));
            Assert.NotNull(tool["requiresConfirmation"]);
        });
        Assert.Equal("permission_denied", denied.ErrorCode);
        Assert.True(allowed.IsSuccess);
        Assert.Equal("downloads", service.LastPage);
    }

    [Fact]
    public async Task DiagnosticsLog_RejectsUnknownComponentBeforeServiceInvocation()
    {
        var service = new StubAutomationService();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(AutomationToolCatalog.Create(service, service, service, service, service, registry));

        var result = await registry.InvokeAsync(
            "diagnostics.logs",
            new JsonObject { ["component"] = "secret-file" },
            new ToolCallContext("agent"));

        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(0, service.LogReads);
    }

    [Fact]
    public async Task SourceAndReloadMutations_RequireApprovalAndRejectCredentialArguments()
    {
        var service = new StubAutomationService();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(AutomationToolCatalog.Create(service, service, service, service, service, registry));
        var addArguments = new JsonObject
        {
            ["name"] = "Media",
            ["protocol"] = "local",
            ["endpoint"] = "/media/videos",
        };

        var denied = await registry.InvokeAsync("media.sources.add", addArguments, new ToolCallContext("agent"));
        var credentialsRejected = await registry.InvokeAsync(
            "media.sources.add",
            new JsonObject
            {
                ["name"] = "Private",
                ["protocol"] = "smb",
                ["endpoint"] = "smb://server/share",
                ["password"] = "must-not-be-accepted",
            },
            new ToolCallContext("agent", HasUserApproval: true));
        var added = await registry.InvokeAsync(
            "media.sources.add",
            addArguments,
            new ToolCallContext("agent", HasUserApproval: true));
        var reloaded = await registry.InvokeAsync(
            "plugins.reload",
            new JsonObject { ["id"] = "org.canx.test" },
            new ToolCallContext("agent", HasUserApproval: true));

        Assert.Equal("permission_denied", denied.ErrorCode);
        Assert.Equal("invalid_arguments", credentialsRejected.ErrorCode);
        Assert.True(added.IsSuccess);
        Assert.True(reloaded.IsSuccess);
        Assert.Equal(1, service.SourceAdds);
        Assert.Equal(1, service.PluginReloads);
    }

    private sealed class StubAutomationService :
        IMediaAutomationService,
        IPluginAutomationService,
        ISettingsAutomationService,
        IDiagnosticsAutomationService,
        IUiAutomationService
    {
        public int LogReads { get; private set; }
        public string? LastPage { get; private set; }
        public int SourceAdds { get; private set; }
        public int PluginReloads { get; private set; }

        public ValueTask<JsonArray> ListSourcesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JsonArray());

        public ValueTask<JsonObject> AddSourceAsync(
            string name,
            string protocol,
            string endpoint,
            CancellationToken cancellationToken)
        {
            SourceAdds++;
            return ValueTask.FromResult(new JsonObject
            {
                ["id"] = "source-id",
                ["name"] = name,
                ["protocol"] = protocol,
            });
        }

        public ValueTask<bool> RemoveSourceAsync(string sourceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<JsonObject> BrowseAsync(string sourceId, string? location, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JsonObject());

        public ValueTask PlayAsync(string sourceId, string location, string? subtitleLocation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public JsonArray List() => [];

        public ValueTask EnableAsync(string pluginId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisableAsync(string pluginId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ReloadAsync(string pluginId, CancellationToken cancellationToken)
        {
            PluginReloads++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(string pluginId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask UninstallAsync(string pluginId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public JsonObject Get(string section) => new();

        public ValueTask<JsonObject> SetAsync(string section, JsonObject values, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JsonObject());

        public JsonObject GetSnapshot() => new();

        public ValueTask<JsonArray> ReadLogAsync(string component, int maximumLines, CancellationToken cancellationToken)
        {
            LogReads++;
            return ValueTask.FromResult(new JsonArray());
        }

        public JsonObject GetContext() => new();

        public ValueTask NavigateAsync(string page, CancellationToken cancellationToken)
        {
            LastPage = page;
            return ValueTask.CompletedTask;
        }

        public ValueTask NotifyAsync(string message, string severity, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
