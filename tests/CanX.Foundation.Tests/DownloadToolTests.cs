// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Application.Downloads;
using CanX.Core.Downloads;
using CanX.Tooling;
using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DownloadToolTests
{
    [Fact]
    public async Task ListTool_RedactsSensitiveAria2Options()
    {
        var task = CreateTask();
        var registry = CreateRegistry(new StubDownloadManager([task]));

        var result = await registry.InvokeAsync("downloads.list", null, new ToolCallContext("agent"));

        Assert.True(result.IsSuccess);
        var item = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(result.Value)[0]);
        var options = Assert.IsType<JsonObject>(item["options"]);
        Assert.Equal("<redacted>", options["header"]?.GetValue<string>());
        Assert.Equal("4", options["split"]?.GetValue<string>());
    }

    [Fact]
    public async Task DestructiveTool_RequiresExplicitApproval()
    {
        var manager = new StubDownloadManager([CreateTask()]);
        var audit = new InMemoryToolAuditSink();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), audit);
        registry.ReplaceHandlers(DownloadToolCatalog.Create(manager));

        var arguments = new JsonObject { ["id"] = manager.Tasks[0].Id.ToString("D") };
        var denied = await registry.InvokeAsync("downloads.remove", arguments, new ToolCallContext("agent"));
        var allowed = await registry.InvokeAsync("downloads.remove", arguments, new ToolCallContext("agent", true));

        Assert.Equal("permission_denied", denied.ErrorCode);
        Assert.True(allowed.IsSuccess);
        Assert.Equal(1, manager.RemoveCount);
    }

    [Fact]
    public async Task CapabilityAndConnectionTools_AreAvailableToAgents()
    {
        var manager = new StubDownloadManager([CreateTask()]);
        var registry = CreateRegistry(manager);

        var capabilities = await registry.InvokeAsync(
            "downloads.capabilities",
            null,
            new ToolCallContext("agent"));
        var connections = await registry.InvokeAsync(
            "downloads.connections",
            new JsonObject { ["id"] = manager.Tasks[0].Id.ToString("D") },
            new ToolCallContext("agent"));

        Assert.True(capabilities.IsSuccess);
        Assert.True(connections.IsSuccess);
        Assert.NotNull(Assert.IsType<JsonObject>(connections.Value)["peers"]);
    }

    [Fact]
    public async Task GetTool_ReturnsCompleteRedactedDiagnostics()
    {
        var task = CreateTask() with
        {
            EngineStatus = 4,
            EtaSeconds = 42,
            PieceLength = 1024,
            PieceCount = 2,
            Bitfield = "80",
            ErrorCode = 24,
            ErrorMessage = DownloadErrorMessages.GetMessage(24),
            FollowedBy = ["0000000000000002"],
            Files =
            [
                new DownloadFileSnapshot
                {
                    Index = 1,
                    Path = "/tmp/file.bin",
                    Length = 2048,
                    CompletedLength = 1024,
                    IsSelected = true,
                    Uris =
                    [
                        new DownloadUriSnapshot
                        {
                            Uri = "https://user:password@example.test/file.bin?token=secret",
                            EngineStatus = 0,
                            IsUsed = true,
                        },
                    ],
                },
            ],
            Torrent = new TorrentSnapshot
            {
                Name = "fixture",
                Mode = "Single",
                AnnounceList = [["https://tracker.test/announce?key=secret"]],
            },
        };
        var registry = CreateRegistry(new StubDownloadManager([task]));

        var result = await registry.InvokeAsync(
            "downloads.get",
            new JsonObject { ["id"] = task.Id.ToString("D") },
            new ToolCallContext("agent", true));

        Assert.True(result.IsSuccess);
        var json = Assert.IsType<JsonObject>(result.Value);
        Assert.Equal(42, json["etaSeconds"]?.GetValue<long>());
        Assert.Equal("HTTP 授权失败。", json["errorMessage"]?.GetValue<string>());
        Assert.Equal("80", json["bitfield"]?.GetValue<string>());
        var file = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["files"])[0]);
        var uri = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(file["uris"])[0]);
        Assert.DoesNotContain("password", uri["uri"]?.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", uri["uri"]?.GetValue<string>(), StringComparison.Ordinal);
        var torrent = Assert.IsType<JsonObject>(json["torrent"]);
        var tracker = Assert.IsType<JsonArray>(Assert.IsType<JsonArray>(torrent["announceList"])[0]);
        Assert.DoesNotContain("secret", tracker[0]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionPolicy_IsAvailableWhenAddingAndUpdatingTasks()
    {
        var manager = new StubDownloadManager([CreateTask()]);
        var registry = CreateRegistry(manager);

        var added = await registry.InvokeAsync(
            "downloads.add",
            new JsonObject
            {
                ["source"] = "https://example.test/file.bin",
                ["destinationDirectory"] = "/tmp",
                ["notifyOnCompletion"] = true,
                ["completionAction"] = "open-directory",
            },
            new ToolCallContext("agent", true));
        var updated = await registry.InvokeAsync(
            "downloads.completion.set",
            new JsonObject
            {
                ["id"] = manager.Tasks[0].Id.ToString("D"),
                ["notify"] = true,
                ["action"] = "open-file",
            },
            new ToolCallContext("agent", true));
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(1);
        var scheduled = await registry.InvokeAsync(
            "downloads.schedule.set",
            new JsonObject
            {
                ["id"] = manager.Tasks[0].Id.ToString("D"),
                ["startAt"] = scheduledAt.ToString("O"),
            },
            new ToolCallContext("agent", true));

        Assert.True(added.IsSuccess);
        Assert.Equal(DownloadCompletionAction.OpenDirectory, manager.LastAddRequest?.CompletionAction);
        Assert.True(manager.LastAddRequest?.NotifyOnCompletion);
        Assert.True(updated.IsSuccess);
        Assert.Equal(DownloadCompletionAction.OpenFile, manager.CompletionAction);
        Assert.True(manager.NotifyOnCompletion);
        Assert.True(scheduled.IsSuccess);
        Assert.Equal(scheduledAt, manager.ScheduledStartAt);
    }

    [Fact]
    public async Task AddTool_ReportsDuplicateUnlessCallerExplicitlyAllowsIt()
    {
        var manager = new StubDownloadManager([CreateTask()]) { RejectDuplicates = true };
        var registry = CreateRegistry(manager);
        var arguments = new JsonObject
        {
            ["source"] = "https://example.test/file.bin",
            ["destinationDirectory"] = "/tmp",
        };

        var rejected = await registry.InvokeAsync(
            "downloads.add",
            arguments,
            new ToolCallContext("agent", true));
        arguments["allowDuplicate"] = true;
        var allowed = await registry.InvokeAsync(
            "downloads.add",
            arguments,
            new ToolCallContext("agent", true));

        Assert.False(rejected.IsSuccess);
        Assert.Equal("duplicate_download", rejected.ErrorCode);
        Assert.True(allowed.IsSuccess);
        Assert.True(manager.LastAddRequest?.AllowDuplicate);
    }

    [Fact]
    public async Task AddBatchTool_AcceptsSourcesWithoutSingleSourcePlaceholder()
    {
        var manager = new StubDownloadManager([CreateTask()]);
        var registry = CreateRegistry(manager);

        var result = await registry.InvokeAsync(
            "downloads.addBatch",
            new JsonObject
            {
                ["sources"] = new JsonArray(
                    "https://example.test/one.bin",
                    "https://example.test/two.bin"),
                ["destinationDirectory"] = "/tmp",
            },
            new ToolCallContext("agent", true));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(1, manager.AddBatchCount);
        Assert.Equal(2, manager.AddedRequests.Count);
        Assert.Equal("https://example.test/one.bin", manager.AddedRequests[0].Source);
        Assert.Equal("https://example.test/two.bin", manager.AddedRequests[1].Source);
    }

    [Theory]
    [InlineData("log")]
    [InlineData("save-session")]
    [InlineData("save-cookies")]
    [InlineData("all-proxy")]
    [InlineData("check-certificate")]
    [InlineData("on-download-complete")]
    [InlineData("dir")]
    public async Task OptionTools_RejectOptionsOutsideAutomationAllowList(string option)
    {
        var manager = new StubDownloadManager([CreateTask()]);
        var registry = CreateRegistry(manager);

        var global = await registry.InvokeAsync(
            "downloads.global.set",
            new JsonObject { ["options"] = new JsonObject { [option] = "value" } },
            new ToolCallContext("agent", true));
        var task = await registry.InvokeAsync(
            "downloads.options.set",
            new JsonObject
            {
                ["id"] = manager.Tasks[0].Id.ToString("D"),
                ["options"] = new JsonObject { [option] = "value" },
            },
            new ToolCallContext("agent", true));

        Assert.False(global.IsSuccess);
        Assert.False(task.IsSuccess);
    }

    [Fact]
    public async Task OptionTools_AcceptAllowListedRateLimits()
    {
        var manager = new StubDownloadManager([CreateTask()]);
        var registry = CreateRegistry(manager);

        var result = await registry.InvokeAsync(
            "downloads.global.set",
            new JsonObject { ["options"] = new JsonObject { ["max-overall-download-limit"] = "1M" } },
            new ToolCallContext("agent", true));

        Assert.True(result.IsSuccess, result.ErrorMessage);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData("/")]
    public async Task AddTool_RejectsDestinationOutsideDownloadRoot(string destination)
    {
        using var root = new TemporaryDirectory();
        var manager = new StubDownloadManager([CreateTask()]);
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(DownloadToolCatalog.Create(manager, () => root.Path));

        var result = await registry.InvokeAsync(
            "downloads.add",
            new JsonObject
            {
                ["source"] = "https://example.test/file.bin",
                ["destinationDirectory"] = destination,
            },
            new ToolCallContext("agent", true));

        Assert.False(result.IsSuccess);
        Assert.Null(manager.LastAddRequest);
    }

    [Fact]
    public async Task AddTool_AcceptsSubdirectoryOfDownloadRoot()
    {
        using var root = new TemporaryDirectory();
        var manager = new StubDownloadManager([CreateTask()]);
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(DownloadToolCatalog.Create(manager, () => root.Path));

        var result = await registry.InvokeAsync(
            "downloads.add",
            new JsonObject
            {
                ["source"] = "https://example.test/file.bin",
                ["destinationDirectory"] = "series",
            },
            new ToolCallContext("agent", true));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(System.IO.Path.Combine(root.Path, "series"), manager.LastAddRequest?.DestinationDirectory);
    }

    private static ToolRegistry CreateRegistry(IDownloadManager manager)
    {
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(DownloadToolCatalog.Create(manager));
        return registry;
    }

    private static DownloadTaskSnapshot CreateTask() => new()
    {
        Id = Guid.NewGuid(),
        EngineGid = "0000000000000001",
        Kind = DownloadSourceKind.Uri,
        Source = "https://example.test/file.bin",
        DestinationDirectory = "/tmp",
        DisplayName = "file.bin",
        Options = new Dictionary<string, string>
        {
            ["header"] = "Authorization: bearer secret",
            ["split"] = "4",
        },
    };

    private sealed class StubDownloadManager(IReadOnlyList<DownloadTaskSnapshot> tasks) : IDownloadManager
    {
        public bool IsStarted => true;

        public IReadOnlyList<DownloadTaskSnapshot> Tasks { get; } = tasks;

        public DownloadGlobalSnapshot GlobalStatus { get; } = new();

        public DownloadCapabilityManifest Capabilities { get; } = new();

        public int RemoveCount { get; private set; }

        public AddDownloadRequest? LastAddRequest { get; private set; }

        public List<AddDownloadRequest> AddedRequests { get; } = [];

        public int AddBatchCount { get; private set; }

        public bool NotifyOnCompletion { get; private set; }

        public DownloadCompletionAction CompletionAction { get; private set; }

        public DateTimeOffset? ScheduledStartAt { get; private set; }

        public bool RejectDuplicates { get; init; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddAsync(
            AddDownloadRequest request,
            CancellationToken cancellationToken = default)
        {
            LastAddRequest = request;
            AddedRequests.Add(request);
            if (RejectDuplicates && !request.AllowDuplicate)
            {
                throw new DuplicateDownloadException(Tasks[0]);
            }

            return ValueTask.FromResult(Tasks);
        }

        public async ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddBatchAsync(
            IReadOnlyList<AddDownloadRequest> requests,
            CancellationToken cancellationToken = default)
        {
            AddBatchCount++;
            var added = new List<DownloadTaskSnapshot>();
            foreach (var request in requests)
            {
                added.AddRange(await AddAsync(request, cancellationToken));
            }
            return added;
        }

        public ValueTask<DownloadPreparation> PrepareAsync(
            string source,
            CancellationToken cancellationToken = default,
            Guid? preparationId = null) =>
            ValueTask.FromResult(new DownloadPreparation
            {
                Id = preparationId ?? Guid.NewGuid(),
                Kind = DownloadSourceKind.Uri,
                Source = source,
                DisplayName = "file.bin",
            });

        public ValueTask<IReadOnlyList<DownloadTaskSnapshot>> CommitPreparationAsync(
            CommitDownloadPreparationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Tasks);

        public ValueTask CancelPreparationAsync(
            Guid preparationId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(Guid id, bool force = false, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ResumeAsync(Guid id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveAsync(
            Guid id,
            bool force = false,
            bool deleteFiles = false,
            CancellationToken cancellationToken = default)
        {
            RemoveCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ChangeOptionsAsync(
            Guid id,
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ChangeGlobalOptionsAsync(
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ChangeCompletionPolicyAsync(
            Guid id,
            bool notifyOnCompletion,
            DownloadCompletionAction completionAction,
            CancellationToken cancellationToken = default)
        {
            NotifyOnCompletion = notifyOnCompletion;
            CompletionAction = completionAction;
            return ValueTask.CompletedTask;
        }

        public ValueTask ScheduleAsync(
            Guid id,
            DateTimeOffset? startAt,
            CancellationToken cancellationToken = default)
        {
            ScheduledStartAt = startAt;
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> MoveAsync(
            Guid id,
            int offset,
            DownloadPositionMode mode,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public ValueTask<DownloadLiveDetails> GetLiveDetailsAsync(
            Guid id,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new DownloadLiveDetails());

        public ValueTask<IMediaByteStream> OpenMediaStreamAsync(
            Guid id,
            int fileIndex,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IMediaByteStream>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-download-tool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
