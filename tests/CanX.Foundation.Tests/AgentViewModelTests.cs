// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Agent;
using CanX.App.ViewModels;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class AgentViewModelTests
{
    [Fact]
    public async Task LegacyConversationWithNullDisplayItemsStillLoads()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-agent-legacy-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "conversations.json");
            var store = new JsonAgentConversationStore(path);
            await store.SaveAsync([new AgentConversation
            {
                Id = "legacy",
                Title = "Legacy",
                Messages = [AgentMessage.User("hello"), AgentMessage.Assistant("answer")],
            }]);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
            document[0]!.AsObject()["DisplayItems"] = null;
            await File.WriteAllTextAsync(path, document.ToJsonString());

            var viewModel = CreateViewModel(store);
            await viewModel.InitializeAsync();
            Assert.Equal(["hello", "answer"], viewModel.Messages.Select(item => item.Content));
            await viewModel.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ToolCallVisibilityCanChangeWhileRunningAndSurvivesReopening()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-agent-timeline-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonAgentConversationStore(Path.Combine(directory, "conversations.json"));
            var tool = new BlockingTool();
            var showToolCalls = false;
            var first = CreateViewModel(store, new ToolSequenceProvider(), () => showToolCalls, tool);
            await first.InitializeAsync();
            var visible = new TaskCompletionSource<AgentChatMessageViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Messages.CollectionChanged += (_, args) =>
            {
                if (args.NewItems is null) return;
                foreach (var item in args.NewItems)
                {
                    if (item is AgentChatMessageViewModel { IsToolStatus: true } status)
                    {
                        visible.TrySetResult(status);
                    }
                }
            };
            first.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AgentViewModel.IsRunning) && !first.IsRunning)
                {
                    finished.TrySetResult();
                }
            };

            first.InputText = "hello";
            first.SendCommand.Execute(null);
            var toolStatus = await visible.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, first.Messages.IndexOf(toolStatus));
            Assert.Contains("正在调用", toolStatus.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(toolStatus, first.VisibleMessages);
            showToolCalls = true;
            first.RefreshMessageVisibility();
            Assert.Equal(["用户", "助手", "工具"], first.VisibleMessages.Select(item => item.Role));
            Assert.Same(toolStatus, first.VisibleMessages[2]);
            tool.Release();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["用户", "助手", "工具", "助手"], first.Messages.Select(item => item.Role));
            Assert.Equal("before", first.Messages[1].Content);
            Assert.Same(toolStatus, first.Messages[2]);
            Assert.Equal("test.wait 已完成", toolStatus.Content);
            Assert.Equal("after", first.Messages[3].Content);
            Assert.Equal("test.wait 已完成", first.VisibleMessages[2].Content);
            showToolCalls = false;
            first.RefreshMessageVisibility();
            Assert.Equal(["用户", "助手", "助手"], first.VisibleMessages.Select(item => item.Role));
            await first.DisposeAsync();

            var persisted = Assert.Single(await store.LoadAsync());
            Assert.Equal("beforeafter", persisted.Messages[1].Content);
            Assert.Equal(4, persisted.DisplayItems.Count);
            var reopened = CreateViewModel(store);
            await reopened.InitializeAsync();
            Assert.Equal(["用户", "助手", "工具", "助手"], reopened.Messages.Select(item => item.Role));
            Assert.Equal(["用户", "助手", "助手"], reopened.VisibleMessages.Select(item => item.Role));
            Assert.Equal("test.wait 已完成", reopened.Messages[2].Content);
            await reopened.DisposeAsync();

            var shown = CreateViewModel(store, showToolCalls: () => true);
            await shown.InitializeAsync();
            Assert.Equal(["用户", "助手", "工具", "助手"], shown.VisibleMessages.Select(item => item.Role));
            await shown.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ContextUsageAndVisibleHistorySurviveReopeningConversation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-agent-view-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonAgentConversationStore(Path.Combine(directory, "conversations.json"));
            var first = CreateViewModel(store);
            await first.InitializeAsync();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = false;
            first.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(AgentViewModel.IsRunning)) return;
                if (first.IsRunning) started = true;
                else if (started) finished.TrySetResult();
            };
            first.InputText = "hello";
            first.SendCommand.Execute(null);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(50, first.ContextTokensUsed);
            Assert.Contains(128_000.ToString("N0"), first.UsageText, StringComparison.Ordinal);
            await first.DisposeAsync();

            var reopened = CreateViewModel(store);
            await reopened.InitializeAsync();
            Assert.Equal(50, reopened.ContextTokensUsed);
            Assert.Equal(2, reopened.Messages.Count);
            Assert.Equal("hello", reopened.Messages[0].Content);
            Assert.Equal("answer", reopened.Messages[1].Content);
            await reopened.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static AgentViewModel CreateViewModel(
        IAgentConversationStore store,
        IAgentChatProvider? provider = null,
        Func<bool>? showToolCalls = null,
        params IToolHandler[] handlers)
    {
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(handlers);
        return new AgentViewModel(
            new AgentRunner(provider ?? new SingleAnswerProvider(), registry),
            store,
            () => new AgentProviderConfiguration
            {
                BaseUri = new Uri("https://gateway.test/v1"),
                Model = "test-model",
                ContextWindowTokens = 128_000,
            },
            () => new AgentRunOptions(),
            new AgentApprovalService(),
            () => 128_000,
            showToolCalls);
    }

    private sealed class SingleAnswerProvider : IAgentChatProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AgentProviderCompleted("answer", [], new AgentUsage(40, 10));
        }
    }

    private sealed class ToolSequenceProvider : IAgentChatProvider
    {
        private int _round;

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Interlocked.Increment(ref _round) == 1)
            {
                yield return new AgentProviderCompleted("before",
                    [new AgentToolCall { Id = "call-1", Name = "test.wait", ArgumentsJson = "{}" }],
                    new AgentUsage(4, 2));
            }
            else
            {
                yield return new AgentProviderTextDelta("after");
                yield return new AgentProviderCompleted("after", [], new AgentUsage(6, 2));
            }
        }
    }

    private sealed class BlockingTool : IToolHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolDescriptor Descriptor { get; } = new(
            "test.wait",
            "Wait until the test releases the tool",
            ToolRisk.ReadOnly,
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" });

        public void Release() => _release.TrySetResult();

        public async ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return ToolResult.Success(new JsonObject());
        }
    }
}
