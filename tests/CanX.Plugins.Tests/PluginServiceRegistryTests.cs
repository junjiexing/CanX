// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Plugins.Services;
using CanX.Core.Diagnostics;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginServiceRegistryTests
{
    [Fact]
    public async Task RegisterAndInvoke_ValidatesSchemasAndReturnsJson()
    {
        var registry = new PluginServiceRegistry();
        using var inputSchema = JsonDocument.Parse("""
            {"type":"object","required":["query"],"additionalProperties":false,"properties":{"query":{"type":"string","minLength":1}}}
            """);
        using var outputSchema = JsonDocument.Parse("""
            {"type":"object","required":["count"],"properties":{"count":{"type":"integer","minimum":0}}}
            """);
        using var registration = registry.Register(
            "org.canx.source",
            "search",
            "org.canx.search",
            "1.0",
            "Search",
            "Search test data.",
            PluginServiceRisk.Network,
            [PluginServiceAudience.Plugin, PluginServiceAudience.Ai],
            inputSchema.RootElement,
            outputSchema.RootElement,
            Permissions("org.canx.source", PluginCapabilities.ServicesProvide, "org.canx.search"),
            (input, context, _) =>
            {
                Assert.Equal("plugin:org.canx.client", context.Caller);
                Assert.Equal(["org.canx.client", "org.canx.source"], context.PluginChain);
                return ValueTask.FromResult("{\"count\":1}");
            });

        using var input = JsonDocument.Parse("{\"query\":\"test\"}");
        var result = await registry.InvokeAsync(
            "org.canx.source/search",
            input.RootElement,
            PluginServiceCallContext.ForPlugin("org.canx.client"));

        Assert.Equal(1, result.GetProperty("count").GetInt32());
        Assert.Equal("org.canx.search", Assert.Single(registry.Services).Contract);
    }

    [Fact]
    public async Task Invoke_RejectsInvalidInputAndOutput()
    {
        var registry = new PluginServiceRegistry();
        using var objectSchema = JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"value\"]}");
        using var registration = registry.Register(
            "org.canx.source",
            "bad",
            "org.canx.test",
            "1.0",
            "Bad service",
            null,
            PluginServiceRisk.ReadOnly,
            [PluginServiceAudience.Plugin],
            objectSchema.RootElement,
            objectSchema.RootElement,
            Permissions("org.canx.source", PluginCapabilities.ServicesProvide, "*"),
            (_, _, _) => ValueTask.FromResult("{}"));
        using var invalidInput = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<PluginServiceException>(async () =>
            await registry.InvokeAsync(
                "org.canx.source/bad",
                invalidInput.RootElement,
                PluginServiceCallContext.ForPlugin("org.canx.client")));

        using var validInput = JsonDocument.Parse("{\"value\":true}");
        var exception = await Assert.ThrowsAsync<PluginServiceException>(async () =>
            await registry.InvokeAsync(
                "org.canx.source/bad",
                validInput.RootElement,
                PluginServiceCallContext.ForPlugin("org.canx.client")));
        Assert.Equal("schema_validation_failed", exception.Code);
    }

    [Fact]
    public async Task Invoke_RejectsCallCycles()
    {
        var registry = new PluginServiceRegistry();
        using var schema = JsonDocument.Parse("true");
        using var registration = registry.Register(
            "org.canx.source",
            "loop",
            "org.canx.test",
            "1.0",
            "Loop",
            null,
            PluginServiceRisk.ReadOnly,
            [PluginServiceAudience.Plugin],
            schema.RootElement,
            schema.RootElement,
            Permissions("org.canx.source", PluginCapabilities.ServicesProvide, "org.canx.test"),
            (_, _, _) => ValueTask.FromResult("null"));

        using var input = JsonDocument.Parse("null");
        var exception = await Assert.ThrowsAsync<PluginServiceException>(async () =>
            await registry.InvokeAsync(
                "org.canx.source/loop",
                input.RootElement,
                PluginServiceCallContext.ForPlugin("org.canx.source")));
        Assert.Equal("service_call_cycle", exception.Code);
    }

    [Fact]
    public async Task Invoke_NormalizesServiceImplementationFailures()
    {
        var original = new InvalidOperationException("service internals must not escape");
        var log = new CapturingApplicationLog();
        var registry = new PluginServiceRegistry(log);
        using var schema = JsonDocument.Parse("true");
        using var registration = registry.Register(
            "org.canx.source",
            "failure",
            "org.canx.test",
            "1.0",
            "Failure",
            null,
            PluginServiceRisk.ReadOnly,
            [PluginServiceAudience.Plugin],
            schema.RootElement,
            schema.RootElement,
            Permissions("org.canx.source", PluginCapabilities.ServicesProvide, "org.canx.test"),
            (_, _, _) => ValueTask.FromException<string>(original));

        using var input = JsonDocument.Parse("null");
        var exception = await Assert.ThrowsAsync<PluginServiceException>(async () =>
            await registry.InvokeAsync(
                "org.canx.source/failure",
                input.RootElement,
                PluginServiceCallContext.ForPlugin("org.canx.client")));

        Assert.Equal("service_failure", exception.Code);
        Assert.Equal("Service invocation failed.", exception.Message);
        Assert.Contains(original, log.Exceptions);
    }

    [Fact]
    public async Task InvocationAndRegistrySnapshot_AreLoggedWithoutPayloads()
    {
        var log = new CapturingApplicationLog();
        var registry = new PluginServiceRegistry(log);
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        using var registration = registry.Register(
            "org.canx.source",
            "search",
            "org.canx.search",
            "1.0",
            "Search",
            null,
            PluginServiceRisk.Network,
            [PluginServiceAudience.Plugin],
            schema.RootElement,
            schema.RootElement,
            Permissions("org.canx.source", PluginCapabilities.ServicesProvide, "org.canx.search"),
            (_, _, _) => ValueTask.FromResult("{}"));
        using var input = JsonDocument.Parse("{\"privateQuery\":\"do-not-log\"}");

        await registry.InvokeAsync(
            "org.canx.source/search",
            input.RootElement,
            PluginServiceCallContext.ForPlugin("org.canx.client"));
        var failure = await Assert.ThrowsAsync<PluginServiceException>(async () =>
            await registry.InvokeAsync(
                "org.canx.missing/search",
                input.RootElement,
                PluginServiceCallContext.ForPlugin("org.canx.client")));
        registry.WriteSchemaSnapshot("startup");

        Assert.Equal("service_not_found", failure.Code);
        Assert.Contains(log.Messages, message =>
            message.Contains("plugin.service.call", StringComparison.Ordinal) &&
            message.Contains("plugin:org.canx.client", StringComparison.Ordinal) &&
            !message.Contains("do-not-log", StringComparison.Ordinal));
        Assert.Contains(log.Messages, message =>
            message.Contains("org.canx.missing/search", StringComparison.Ordinal) &&
            message.Contains("service_not_found", StringComparison.Ordinal));
        Assert.Contains(log.Messages, message =>
            message.Contains("plugin.service.registry", StringComparison.Ordinal) &&
            message.Contains("inputSchema", StringComparison.Ordinal));
    }

    private static PluginPermissionSet Permissions(string pluginId, string capability, string scope) =>
        PluginPermissionSet.FromManifest(new PluginManifest(
            1,
            pluginId,
            pluginId,
            null,
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            [new PluginPermission(capability, [scope])]));

    private sealed class CapturingApplicationLog : IApplicationLog
    {
        public bool IsEnabled => true;
        public string DirectoryPath => string.Empty;
        public List<string> Messages { get; } = [];
        public List<Exception> Exceptions { get; } = [];
        public event Action<bool>? EnabledChanged;
        public string GetFilePath(LogComponent component) => string.Empty;
        public void SetEnabled(bool enabled) => EnabledChanged?.Invoke(enabled);
        public void Write(LogComponent component, ApplicationLogLevel level, string message, Exception? exception = null)
        {
            Messages.Add(message);
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
        public void Dispose()
        {
        }
    }
}
