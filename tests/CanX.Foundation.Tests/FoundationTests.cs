// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Core.Configuration;
using CanX.Core.Events;
using CanX.Infrastructure.Configuration;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void EventBus_SubscribeAndDispose_ControlsDelivery()
    {
        var bus = new InMemoryEventBus();
        var received = new List<int>();
        using var subscription = bus.Subscribe<TestEvent>(message => received.Add(message.Value));

        bus.Publish(new TestEvent(7));
        subscription.Dispose();
        bus.Publish(new TestEvent(8));

        Assert.Equal([7], received);
    }

    [Fact]
    public async Task ToolRegistry_EnforcesApprovalAndAuditsCalls()
    {
        var audit = new InMemoryToolAuditSink();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), audit);
        registry.ReplaceHandlers([new EchoTool()]);

        var denied = await registry.InvokeAsync("test.echo", [], new ToolCallContext("plugin"));
        var allowed = await registry.InvokeAsync(
            "test.echo",
            new JsonObject { ["value"] = "hello" },
            ToolCallContext.LocalUser);

        Assert.False(denied.IsSuccess);
        Assert.Equal("permission_denied", denied.ErrorCode);
        Assert.True(allowed.IsSuccess);
        Assert.Equal("hello", allowed.Value?["value"]?.GetValue<string>());
        Assert.Collection(
            audit.Entries,
            entry => Assert.False(entry.Allowed),
            entry => Assert.True(entry.Succeeded));
    }

    [Fact]
    public async Task ToolRegistry_ValidatesSchemaLimitsOutputAndReusesIdempotentResult()
    {
        var audit = new InMemoryToolAuditSink();
        var tool = new CountingTool();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), audit);
        registry.ReplaceHandlers([tool]);
        var context = new ToolCallContext(
            "agent:test",
            HasUserApproval: true,
            CallerKind: ToolCallerKind.Agent,
            CorrelationId: "correlation",
            IdempotencyKey: "same-operation");

        var invalid = await registry.InvokeAsync("test.count", [], context);
        var first = await registry.InvokeAsync(
            "test.count",
            new JsonObject { ["value"] = "hello" },
            context);
        var second = await registry.InvokeAsync(
            "test.count",
            new JsonObject { ["value"] = "ignored-by-idempotency" },
            context);
        var tooLarge = await registry.InvokeAsync(
            "test.count",
            new JsonObject { ["value"] = "12345" },
            context with { IdempotencyKey = "large", MaximumOutputBytes = 4 });

        Assert.Equal("invalid_arguments", invalid.ErrorCode);
        Assert.True(first.IsSuccess);
        Assert.Equal("hello", second.Value?["value"]?.GetValue<string>());
        Assert.Equal(2, tool.InvocationCount);
        Assert.Equal("output_too_large", tooLarge.ErrorCode);
        Assert.All(audit.Entries, entry => Assert.Equal("correlation", entry.CorrelationId));
    }

    [Fact]
    public async Task ToolRegistry_DoesNotExecuteHighRiskToolWhenStartAuditFails()
    {
        var tool = new CountingTool(ToolRisk.Destructive);
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new ThrowingAuditSink());
        registry.ReplaceHandlers([tool]);

        var result = await registry.InvokeAsync(
            "test.count",
            new JsonObject { ["value"] = "hello" },
            ToolCallContext.LocalUser);

        Assert.Equal("audit_unavailable", result.ErrorCode);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task ToolRegistry_PostExecutionAuditFailureDoesNotChangeSuccessfulResult()
    {
        var tool = new CountingTool(ToolRisk.Network);
        var audit = new FailAfterFirstAuditSink();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), audit);
        registry.ReplaceHandlers([tool]);

        var result = await registry.InvokeAsync(
            "test.count",
            new JsonObject { ["value"] = "hello" },
            ToolCallContext.LocalUser);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, tool.InvocationCount);
        Assert.Equal(ToolAuditPhase.Started, Assert.Single(audit.Entries).Phase);
    }

    [Fact]
    public async Task FileToolAuditSink_DoesNotPersistArgumentsOrIdempotencyKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-tool-audit-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "tool-audit.jsonl");
        try
        {
            using (var sink = new FileToolAuditSink(path))
            {
                await sink.WriteAsync(
                    new ToolAuditEntry(
                        DateTimeOffset.UnixEpoch,
                        "test.secret",
                        "agent:test",
                        ToolRisk.Network,
                        Allowed: true,
                        Succeeded: true,
                        ErrorCode: null,
                        CallerKind: ToolCallerKind.Agent,
                        CorrelationId: "correlation",
                        IdempotencyKey: "must-not-be-written"),
                    CancellationToken.None);
            }

            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("test.secret", text, StringComparison.Ordinal);
            Assert.Contains("\"idempotent\":true", text, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-be-written", text, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directory));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsStore_RoundTripsWithAtomicFileReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new JsonApplicationSettingsStore(path);
            var original = new ApplicationSettings
            {
                ColorTheme = "Dark",
                DownloadDirectory = "/tmp/canx-downloads",
                EnableLogging = true,
                Mpv = new MpvSettings
                {
                    HardwareDecoding = "vaapi",
                    SubtitleLanguages = "zh,en",
                    SeekPreviewEnabled = false,
                    SeekPreviewDelayMilliseconds = 0,
                    AdvancedOptions = "tone-mapping=auto",
                },
            };

            await store.SaveAsync(original);
            var restored = await store.LoadAsync();

            Assert.Equal(original, restored);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed record TestEvent(int Value) : IAppEvent;

    private sealed class EchoTool : IToolHandler
    {
        public ToolDescriptor Descriptor { get; } = new(
            "test.echo",
            "Returns the supplied value.",
            ToolRisk.LocalWrite,
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" });

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolResult.Success(arguments.DeepClone()));
    }

    private sealed class CountingTool(ToolRisk risk = ToolRisk.LocalWrite) : IToolHandler
    {
        public int InvocationCount { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "test.count",
            "Counts valid invocations.",
            risk,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("value"),
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["value"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                },
            },
            new JsonObject { ["type"] = "object" });

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult(ToolResult.Success(arguments.DeepClone()));
        }
    }

    private sealed class ThrowingAuditSink : IToolAuditSink
    {
        public ValueTask WriteAsync(ToolAuditEntry entry, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("Audit storage is unavailable."));
    }

    private sealed class FailAfterFirstAuditSink : IToolAuditSink
    {
        public List<ToolAuditEntry> Entries { get; } = [];

        public ValueTask WriteAsync(ToolAuditEntry entry, CancellationToken cancellationToken)
        {
            if (Entries.Count > 0)
            {
                return ValueTask.FromException(new IOException("Audit storage is unavailable."));
            }

            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }
}
