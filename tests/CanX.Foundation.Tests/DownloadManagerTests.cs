// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;
using CanX.Core.Events;
using CanX.Downloads;
using CanX.Downloads.Engine;
using CanX.Downloads.Persistence;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task PollingStableEngine_DoesNotRepublishUnchangedSnapshots()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        var eventBus = new InMemoryEventBus();
        var listEvents = 0;
        var taskEvents = 0;
        var globalEvents = 0;
        using var listSubscription = eventBus.Subscribe<DownloadListChanged>(
            _ => Interlocked.Increment(ref listEvents));
        using var taskSubscription = eventBus.Subscribe<DownloadTaskChanged>(
            _ => Interlocked.Increment(ref taskEvents));
        using var globalSubscription = eventBus.Subscribe<DownloadGlobalStatusChanged>(
            _ => Interlocked.Increment(ref globalEvents));
        await using var manager = new DownloadManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")),
            eventBus,
            new DownloadManagerOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

        await manager.StartAsync();
        await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/stable.bin",
            DestinationDirectory = testDirectory.Path,
        });
        await WaitUntilAsync(() => Volatile.Read(ref globalEvents) > 0);
        await Task.Delay(50);
        var stableListEvents = Volatile.Read(ref listEvents);
        var stableTaskEvents = Volatile.Read(ref taskEvents);
        var stableGlobalEvents = Volatile.Read(ref globalEvents);

        await Task.Delay(100);

        Assert.Equal(stableListEvents, Volatile.Read(ref listEvents));
        Assert.Equal(stableTaskEvents, Volatile.Read(ref taskEvents));
        Assert.Equal(stableGlobalEvents, Volatile.Read(ref globalEvents));
    }

    [Fact]
    public async Task OpenMediaStream_ReadsCompletedPieceAndRequestsReadAheadPriority()
    {
        using var testDirectory = new TemporaryDirectory();
        var mediaPath = Path.Combine(testDirectory.Path, "video.bin");
        File.WriteAllBytes(mediaPath, Enumerable.Range(0, 100).Select(static value => (byte)value).ToArray());
        var engine = new FakeAria2Engine { ReportedFilePath = mediaPath };
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/video.bin",
            DestinationDirectory = testDirectory.Path,
        }));
        engine.SetDiagnostics(1);
        await WaitUntilAsync(() => manager.Tasks.Single().State == DownloadState.Active);

        using (var stream = await manager.OpenMediaStreamAsync(task.Id, fileIndex: 1))
        {
            var buffer = new byte[10];
            Assert.Equal(10, stream.Read(buffer, CancellationToken.None));
            Assert.Equal(Enumerable.Range(0, 10).Select(static value => (byte)value), buffer);
        }

        await WaitUntilAsync(() => engine.PriorityRequests.Count == 1);
        Assert.Equal((1UL, 0, 1), engine.PriorityRequests[0]);
        await WaitUntilAsync(() => engine.ClearedPriorityRequests.Count == 1);
        Assert.Equal(1UL, engine.ClearedPriorityRequests[0]);
    }

    [Fact]
    public async Task OpenMediaStream_ClearsPriorityOnlyAfterLastStreamCloses()
    {
        using var testDirectory = new TemporaryDirectory();
        var mediaPath = Path.Combine(testDirectory.Path, "video.bin");
        File.WriteAllBytes(mediaPath, new byte[100]);
        var engine = new FakeAria2Engine { ReportedFilePath = mediaPath };
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/video.bin",
            DestinationDirectory = testDirectory.Path,
        }));
        engine.SetDiagnostics(1);
        await WaitUntilAsync(() => manager.Tasks.Single().State == DownloadState.Active);

        var first = await manager.OpenMediaStreamAsync(task.Id, fileIndex: 1);
        var second = await manager.OpenMediaStreamAsync(task.Id, fileIndex: 1);
        first.Dispose();
        await Task.Delay(100);
        Assert.Empty(engine.ClearedPriorityRequests);

        second.Dispose();
        await WaitUntilAsync(() => engine.ClearedPriorityRequests.Count == 1);
        Assert.Equal(1UL, engine.ClearedPriorityRequests[0]);
    }

    [Fact]
    public async Task DelayedPause_RemainsPausedAndRepeatedRequestIsIdempotent()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine { DelayPauseRequests = true };
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/video.mp4",
            DestinationDirectory = testDirectory.Path,
        }));
        engine.SetDiagnostics(1);
        await WaitUntilAsync(() => manager.Tasks.Single().State == DownloadState.Active);

        await manager.PauseAsync(task.Id, force: true);
        await Task.Delay(50);

        var paused = Assert.Single(manager.Tasks);
        Assert.Equal(DownloadState.Paused, paused.State);
        Assert.Equal(0, paused.DownloadSpeed);
        Assert.Equal(1, engine.PauseCount);
        Assert.True(engine.LastPauseWasForced);

        await manager.PauseAsync(task.Id, force: true);
        Assert.Equal(1, engine.PauseCount);

        engine.CompletePause(1);
        await manager.ResumeAsync(task.Id);
        Assert.Equal(DownloadState.Waiting, Assert.Single(manager.Tasks).State);
        await WaitUntilAsync(() => engine.ResumeCount == 1);
    }

    [Fact]
    public async Task HttpTask_ControlAndRestart_PreservesStableIdentity()
    {
        using var testDirectory = new TemporaryDirectory();
        var sessionPath = Path.Combine(testDirectory.Path, "state", "downloads.json");
        var store = new JsonDownloadSessionStore(sessionPath);
        var firstEngine = new FakeAria2Engine();
        Guid taskId;
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(1);

        await using (var manager = CreateManager(firstEngine, store))
        {
            await manager.StartAsync();
            var added = await manager.AddAsync(new AddDownloadRequest
            {
                Source = "https://example.test/video.mp4",
                DestinationDirectory = testDirectory.Path,
                OutputName = "video.mp4",
            });
            taskId = Assert.Single(added).Id;

            await manager.PauseAsync(taskId);
            Assert.Equal(DownloadState.Paused, Assert.Single(manager.Tasks).State);

            await manager.ResumeAsync(taskId);
            Assert.Equal(DownloadState.Waiting, Assert.Single(manager.Tasks).State);

            await manager.ChangeOptionsAsync(taskId, new Dictionary<string, string>
            {
                ["max-download-limit"] = "1M",
                ["checksum"] = "sha-256=deadbeef",
            });
            await manager.ChangeGlobalOptionsAsync(new Dictionary<string, string>
            {
                ["max-overall-download-limit"] = "2M",
            });
            await manager.ChangeCompletionPolicyAsync(
                taskId,
                notifyOnCompletion: true,
                DownloadCompletionAction.OpenDirectory);
            await manager.ScheduleAsync(taskId, scheduledAt);

            Assert.Equal("1M", firstEngine.ChangedTaskOptions["max-download-limit"]);
            Assert.Equal("2M", firstEngine.ChangedGlobalOptions["max-overall-download-limit"]);
        }

        Assert.True(File.Exists(sessionPath));
        var secondEngine = new FakeAria2Engine();
        await using (var restored = CreateManager(secondEngine, store))
        {
            await restored.StartAsync();
            var task = Assert.Single(restored.Tasks);
            Assert.Equal(taskId, task.Id);
            Assert.Equal("video.mp4", task.DisplayName);
            Assert.Equal(1, secondEngine.AddUriCount);
            Assert.Equal("1M", task.Options["max-download-limit"]);
            Assert.True(task.NotifyOnCompletion);
            Assert.Equal(DownloadCompletionAction.OpenDirectory, task.CompletionAction);
            Assert.Equal(scheduledAt, task.ScheduledStartAt);
            Assert.Equal(DownloadState.Paused, task.State);
        }
    }

    [Fact]
    public async Task LocalTorrent_RestartTracksRestoredEngineTask()
    {
        using var testDirectory = new TemporaryDirectory();
        var torrentPath = Path.Combine(testDirectory.Path, "video.torrent");
        await File.WriteAllTextAsync(torrentPath, "fixture");
        var store = new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json"));
        Guid taskId;

        await using (var manager = CreateManager(new FakeAria2Engine(), store))
        {
            await manager.StartAsync();
            taskId = Assert.Single(await manager.AddAsync(new AddDownloadRequest
            {
                Kind = DownloadSourceKind.Torrent,
                Source = torrentPath,
                DestinationDirectory = testDirectory.Path,
            })).Id;
        }

        var restoredEngine = new FakeAria2Engine();
        await using var restored = CreateManager(restoredEngine, store);
        await restored.StartAsync();

        Assert.False(restoredEngine.LastAddOptions.ContainsKey("pause-metadata"));
        restoredEngine.SetDiagnostics(1);
        await WaitUntilAsync(() => restored.Tasks.Single().State == DownloadState.Active);

        var task = Assert.Single(restored.Tasks);
        Assert.Equal(taskId, task.Id);
        Assert.Equal(25, task.CompletedLength);
        Assert.Equal(25, task.DownloadSpeed);
    }

    [Fact]
    public async Task GlobalDhtOptions_ArePersistedForNextEngineStart()
    {
        using var testDirectory = new TemporaryDirectory();
        var store = new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json"));
        var firstEngine = new FakeAria2Engine();

        await using (var manager = CreateManager(firstEngine, store))
        {
            await manager.StartAsync();
            await manager.ChangeGlobalOptionsAsync(new Dictionary<string, string>
            {
                ["max-overall-download-limit"] = "2M",
                ["enable-dht"] = "false",
                ["dht-listen-port"] = "7000-7010",
            });

            Assert.Equal("2M", firstEngine.ChangedGlobalOptions["max-overall-download-limit"]);
            Assert.False(firstEngine.ChangedGlobalOptions.ContainsKey("enable-dht"));
            Assert.False(firstEngine.ChangedGlobalOptions.ContainsKey("dht-listen-port"));
            Assert.Equal("false", manager.GlobalStatus.Options["enable-dht"]);
            Assert.Equal("7000-7010", manager.GlobalStatus.Options["dht-listen-port"]);
        }

        var secondEngine = new FakeAria2Engine();
        await using (var restored = CreateManager(secondEngine, store))
        {
            await restored.StartAsync();

            Assert.Equal("false", secondEngine.InitialGlobalOptions["enable-dht"]);
            Assert.Equal("7000-7010", secondEngine.InitialGlobalOptions["dht-listen-port"]);
            Assert.Equal("2M", secondEngine.InitialGlobalOptions["max-overall-download-limit"]);
        }
    }

    [Fact]
    public async Task RemoveWithFiles_DeletesOnlyFilesInsideDestination()
    {
        using var testDirectory = new TemporaryDirectory();
        var outputPath = Path.Combine(testDirectory.Path, "payload.bin");
        await File.WriteAllTextAsync(outputPath, "payload");
        await File.WriteAllTextAsync($"{outputPath}.aria2", "control");
        var engine = new FakeAria2Engine { ReportedFilePath = outputPath };

        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/payload.bin",
            DestinationDirectory = testDirectory.Path,
        }));

        await manager.RemoveAsync(task.Id, deleteFiles: true);

        Assert.Empty(manager.Tasks);
        Assert.False(File.Exists(outputPath));
        Assert.False(File.Exists($"{outputPath}.aria2"));
        Assert.Equal(1, engine.RemoveCount);
    }

    [Fact]
    public async Task RemoveWithFiles_RejectsPathOutsideDestinationBeforeEngineMutation()
    {
        using var destination = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var outsidePath = Path.Combine(outside.Path, "keep.bin");
        await File.WriteAllTextAsync(outsidePath, "keep");
        var engine = new FakeAria2Engine { ReportedFilePath = outsidePath };

        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(destination.Path, "downloads.json")));
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/keep.bin",
            DestinationDirectory = destination.Path,
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await manager.RemoveAsync(task.Id, deleteFiles: true));

        Assert.True(File.Exists(outsidePath));
        Assert.Equal(0, engine.RemoveCount);
    }

    [Fact]
    public async Task RemoveWithFiles_RejectsSymbolicLinkParentWithoutDeletingOutsideFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var destination = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var outsidePath = Path.Combine(outside.Path, "keep.bin");
        await File.WriteAllTextAsync(outsidePath, "keep");
        var linkedDirectory = Path.Combine(destination.Path, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, outside.Path);
        var engine = new FakeAria2Engine { ReportedFilePath = Path.Combine(linkedDirectory, "keep.bin") };

        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(destination.Path, "downloads.json")));
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/keep.bin",
            DestinationDirectory = destination.Path,
        }));

        await Assert.ThrowsAsync<IOException>(
            async () => await manager.RemoveAsync(task.Id, deleteFiles: true));

        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public async Task UriTask_ForwardsAllMirrorsAsOneAria2Download()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();

        await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://mirror-a.test/video.mkv",
            MirrorUris = ["https://mirror-b.test/video.mkv", "https://mirror-c.test/video.mkv"],
            DestinationDirectory = testDirectory.Path,
        });

        Assert.Equal(3, engine.LastUris.Count);
        Assert.Equal("https://mirror-b.test/video.mkv", engine.LastUris[1]);
    }

    [Fact]
    public async Task AddBatch_RollsBackPreviouslyAddedTasksWhenOneSourceFails()
    {
        using var testDirectory = new TemporaryDirectory();
        var store = new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json"));
        var engine = new FakeAria2Engine { FailAddOnCall = 2 };
        await using var manager = CreateManager(engine, store);
        await manager.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.AddBatchAsync(
            [
                new AddDownloadRequest
                {
                    Source = "https://example.test/one.bin",
                    DestinationDirectory = testDirectory.Path,
                },
                new AddDownloadRequest
                {
                    Source = "https://example.test/two.bin",
                    DestinationDirectory = testDirectory.Path,
                },
            ]));

        Assert.Empty(manager.Tasks);
        Assert.Equal(1, engine.RemoveCount);
        Assert.Empty((await store.LoadAsync()).Groups);
    }

    [Fact]
    public async Task CallerCancellationReachesRunningDownloadCommandAndRollsBackAdd()
    {
        using var testDirectory = new TemporaryDirectory();
        var store = new BlockingSaveStore();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(engine, store);
        await manager.StartAsync();
        using var cancellation = new CancellationTokenSource();

        var adding = manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/cancelled.bin",
            DestinationDirectory = testDirectory.Path,
        }, cancellation.Token).AsTask();
        await store.BlockedSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding);
        await WaitUntilAsync(() => engine.RemoveCount == 1);
        Assert.Empty(manager.Tasks);
    }

    [Fact]
    public async Task FailedUriTask_ResumeRemovesStoppedResultAndKeepsIdentity()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var original = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/retry.bin",
            DestinationDirectory = testDirectory.Path,
        }));
        engine.Fail(1, 6);
        await WaitUntilAsync(() => manager.Tasks.Single().State == DownloadState.Error);
        Assert.Equal("发生网络错误。", manager.Tasks.Single().ErrorMessage);

        await manager.ResumeAsync(original.Id);

        var retried = Assert.Single(manager.Tasks);
        Assert.Equal(original.Id, retried.Id);
        Assert.NotEqual(original.EngineGid, retried.EngineGid);
        Assert.Equal(DownloadState.Waiting, retried.State);
        Assert.Equal(1, engine.RemoveResultCount);
        Assert.Equal(2, engine.AddUriCount);
    }

    [Fact]
    public async Task RemoveFailedTask_RemovesStoppedResult()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/failed.bin",
            DestinationDirectory = testDirectory.Path,
        }));
        engine.Fail(1, 6);
        await WaitUntilAsync(() => manager.Tasks.Single().State == DownloadState.Error);

        await manager.RemoveAsync(task.Id);

        Assert.Empty(manager.Tasks);
        Assert.Equal(1, engine.RemoveResultCount);
        Assert.Equal(0, engine.RemoveCount);
    }

    [Fact]
    public async Task EngineDiagnostics_AreMappedWithEtaAndRawState()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/diagnostics.bin",
            DestinationDirectory = testDirectory.Path,
        });
        engine.SetDiagnostics(1);

        await WaitUntilAsync(() => manager.Tasks.Single().CompletedLength == 25);

        var task = Assert.Single(manager.Tasks);
        Assert.Equal(0, task.EngineStatus);
        Assert.Equal(3, task.EtaSeconds);
        Assert.Equal("a0", task.Bitfield);
        Assert.Equal(["0000000000000002"], task.FollowedBy);
        Assert.Equal(25, Assert.Single(task.Files).CompletedLength);
        Assert.Equal(25, task.Files[0].ProgressPercent);
        Assert.Equal(1, task.Files[0].Uris[0].EngineStatus);
    }

    [Fact]
    public async Task ActiveTorrentSeeder_IsPublishedAsCompleted()
    {
        using var testDirectory = new TemporaryDirectory();
        var torrentPath = Path.Combine(testDirectory.Path, "video.torrent");
        await File.WriteAllTextAsync(torrentPath, "fixture");
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        await manager.AddAsync(new AddDownloadRequest
        {
            Kind = DownloadSourceKind.Torrent,
            Source = torrentPath,
            DestinationDirectory = testDirectory.Path,
        });

        engine.SetSeeding(1, "Video Pack", 1024 * 1024);
        await WaitUntilAsync(() => manager.Tasks.Single().State == DownloadState.Complete);

        var task = Assert.Single(manager.Tasks);
        Assert.Equal(0, task.EngineStatus);
        Assert.True(task.IsSeeder);
        Assert.Equal("Video Pack", task.DisplayName);
        Assert.Equal(task.TotalLength, task.CompletedLength);

        await manager.RemoveAsync(task.Id);
        Assert.Equal(1, engine.RemoveCount);
        Assert.Equal(0, engine.RemoveResultCount);
    }

    [Fact]
    public async Task MagnetMetadata_HandoffTracksPayloadDownload()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var original = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            DestinationDirectory = testDirectory.Path,
        }));

        var payloadGid = engine.FollowWithTorrentPayload(1, "Feature Film", 4L * 1024 * 1024 * 1024);
        await WaitUntilAsync(() => manager.Tasks.Single().EngineGid == payloadGid.ToString("x16"));

        var task = Assert.Single(manager.Tasks);
        Assert.Equal(original.Id, task.Id);
        Assert.Equal("Feature Film", task.DisplayName);
        Assert.Equal(4L * 1024 * 1024 * 1024, task.TotalLength);
        Assert.Equal(task.TotalLength, task.CompletedLength);
        Assert.Equal("feature-film.mkv", Path.GetFileName(Assert.Single(task.Files).Path));
        Assert.Equal(DownloadState.Complete, task.State);
    }

    [Fact]
    public async Task PendingMagnet_DisplayNameIncludesNormalizedBtih()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();

        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Feature",
            DestinationDirectory = testDirectory.Path,
        }));

        Assert.Equal("[METADATA] 0123456789abcdef0123456789abcdef01234567", task.DisplayName);
    }

    [Fact]
    public async Task MagnetDuplicate_UsesBtihInsteadOfTrackersOrDisplayName()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var existing = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=First&tr=https%3A%2F%2Ftracker-a.test",
            DestinationDirectory = testDirectory.Path,
        }));

        var exception = await Assert.ThrowsAsync<DuplicateDownloadException>(async () =>
            await manager.AddAsync(new AddDownloadRequest
            {
                Source = "magnet:?tr=https%3A%2F%2Ftracker-b.test&dn=Second&xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                DestinationDirectory = testDirectory.Path,
            }));

        Assert.Equal(existing.Id, exception.ExistingTaskId);
        Assert.Single(manager.Tasks);
        Assert.Equal(1, engine.AddUriCount);

        await manager.AddAsync(new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Second",
            DestinationDirectory = testDirectory.Path,
            AllowDuplicate = true,
        });
        Assert.Equal(2, manager.Tasks.Count);
    }

    [Fact]
    public async Task Base32MagnetBtih_MatchesEquivalentHexHash()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        await manager.AddAsync(new AddDownloadRequest
        {
            Source = $"magnet:?xt=urn:btih:{new string('0', 40)}",
            DestinationDirectory = testDirectory.Path,
        });

        await Assert.ThrowsAsync<DuplicateDownloadException>(async () =>
            await manager.AddAsync(new AddDownloadRequest
            {
                Source = $"magnet:?xt=urn:btih:{new string('A', 32)}",
                DestinationDirectory = testDirectory.Path,
            }));
    }

    [Fact]
    public async Task NormalizedUrls_AreDetectedAsDuplicates()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://EXAMPLE.test:443/media/../video.mp4#first",
            DestinationDirectory = testDirectory.Path,
        });

        await Assert.ThrowsAsync<DuplicateDownloadException>(async () =>
            await manager.AddAsync(new AddDownloadRequest
            {
                Source = "https://example.test/video.mp4#second",
                DestinationDirectory = testDirectory.Path,
            }));
    }

    [Fact]
    public async Task TorrentCopies_AreDetectedByInfoHashAndCanBeExplicitlyAdded()
    {
        using var testDirectory = new TemporaryDirectory();
        var firstTorrent = Path.Combine(testDirectory.Path, "first.torrent");
        var secondTorrent = Path.Combine(testDirectory.Path, "second.torrent");
        const string torrent = "d4:infod6:lengthi1e4:name5:videoee";
        await File.WriteAllTextAsync(firstTorrent, torrent);
        await File.WriteAllTextAsync(secondTorrent, torrent);
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        await manager.AddAsync(new AddDownloadRequest
        {
            Kind = DownloadSourceKind.Torrent,
            Source = firstTorrent,
            DestinationDirectory = testDirectory.Path,
        });

        await Assert.ThrowsAsync<DuplicateDownloadException>(async () =>
            await manager.AddAsync(new AddDownloadRequest
            {
                Kind = DownloadSourceKind.Torrent,
                Source = secondTorrent,
                DestinationDirectory = testDirectory.Path,
            }));

        await manager.AddAsync(new AddDownloadRequest
        {
            Kind = DownloadSourceKind.Torrent,
            Source = secondTorrent,
            DestinationDirectory = testDirectory.Path,
            AllowDuplicate = true,
        });
        Assert.Equal(2, manager.Tasks.Count);
    }

    [Fact]
    public async Task PausedMagnetRestore_PreservesPayloadSnapshotAndPausesNewPayload()
    {
        using var testDirectory = new TemporaryDirectory();
        var store = new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json"));
        var taskId = Guid.NewGuid();
        var request = new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            DestinationDirectory = testDirectory.Path,
        };
        await StorePausedMagnetAsync(store, taskId, request);
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(engine, store);

        await manager.StartAsync();

        var restoring = Assert.Single(manager.Tasks);
        Assert.Equal(taskId, restoring.Id);
        Assert.Equal("Feature Film", restoring.DisplayName);
        Assert.Equal(4_096, restoring.TotalLength);
        Assert.Equal(1_024, restoring.CompletedLength);
        Assert.Equal(1_024, Assert.Single(restoring.Files).CompletedLength);
        Assert.Equal(DownloadState.Paused, restoring.State);
        Assert.Equal("true", engine.LastAddOptions["pause-metadata"]);
        Assert.Equal(0, engine.PauseCount);

        var payloadGid = engine.FollowWithPausedTorrentPayload(1, "Feature Film", 4_096, 1_024);
        await WaitUntilAsync(() => manager.Tasks.Single().EngineGid == payloadGid.ToString("x16"));

        var restored = Assert.Single(manager.Tasks);
        Assert.Equal("Feature Film", restored.DisplayName);
        Assert.Equal(4_096, restored.TotalLength);
        Assert.Equal(1_024, restored.CompletedLength);
        Assert.Equal(DownloadState.Paused, restored.State);
        Assert.Equal(0, engine.ResumeCount);
    }

    [Fact]
    public async Task PausedMagnetRestore_ResumeWaitsForPayloadThenContinuesIt()
    {
        using var testDirectory = new TemporaryDirectory();
        var store = new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json"));
        var taskId = Guid.NewGuid();
        var request = new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            DestinationDirectory = testDirectory.Path,
        };
        await StorePausedMagnetAsync(store, taskId, request);
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(engine, store);
        await manager.StartAsync();

        await manager.ResumeAsync(taskId);

        Assert.Equal(DownloadState.Waiting, Assert.Single(manager.Tasks).State);
        Assert.Equal(0, engine.ResumeCount);
        var payloadGid = engine.FollowWithPausedTorrentPayload(1, "Feature Film", 4_096, 1_024);
        await WaitUntilAsync(() => manager.Tasks.Single().EngineGid == payloadGid.ToString("x16"));

        Assert.Equal(DownloadState.Waiting, Assert.Single(manager.Tasks).State);
        Assert.Equal(1, engine.ResumeCount);
    }

    [Fact]
    public async Task TorrentPreparation_DoesNotPublishOrCreateFilesBeforeCommit()
    {
        using var testDirectory = new TemporaryDirectory();
        var torrentPath = Path.Combine(testDirectory.Path, "video.torrent");
        var destination = Path.Combine(testDirectory.Path, "downloads");
        await File.WriteAllTextAsync(torrentPath, "fixture");
        var engine = new FakeAria2Engine { ProvideTorrentPreview = true };
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();

        var preparation = await manager.PrepareAsync(torrentPath);

        Assert.True(preparation.IsTorrent);
        Assert.Equal("Video Pack", preparation.DisplayName);
        Assert.Equal(2, preparation.Files.Count);
        Assert.Equal(0, engine.PauseCount);
        Assert.Empty(manager.Tasks);
        Assert.False(Directory.Exists(destination));

        var tasks = await manager.CommitPreparationAsync(new CommitDownloadPreparationRequest
        {
            PreparationId = preparation.Id,
            DestinationDirectory = destination,
            Files =
            [
                new DownloadFilePreference { Index = 1, Priority = DownloadFilePriority.High },
                new DownloadFilePreference { Index = 2, Priority = DownloadFilePriority.Low },
            ],
        });

        Assert.Single(tasks);
        Assert.True(Directory.Exists(destination));
        Assert.Equal("1,2", engine.ChangedTaskOptions["select-file"]);
        Assert.Equal("1", engine.ChangedTaskOptions["bt-prioritize-file"]);
        Assert.Equal("2", engine.ChangedTaskOptions["bt-deprioritize-file"]);
        Assert.Equal(1, engine.ResumeCount);
    }

    [Fact]
    public async Task TorrentPreparation_DetectsDuplicateInfoHashBeforePromotion()
    {
        using var testDirectory = new TemporaryDirectory();
        var firstTorrent = Path.Combine(testDirectory.Path, "first.torrent");
        var secondTorrent = Path.Combine(testDirectory.Path, "second.torrent");
        await File.WriteAllTextAsync(firstTorrent, "first fixture");
        await File.WriteAllTextAsync(secondTorrent, "second fixture");
        var engine = new FakeAria2Engine { ProvideTorrentPreview = true };
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var first = await manager.PrepareAsync(firstTorrent);
        await manager.CommitPreparationAsync(new CommitDownloadPreparationRequest
        {
            PreparationId = first.Id,
            DestinationDirectory = testDirectory.Path,
        });
        var second = await manager.PrepareAsync(secondTorrent);
        var secondCommit = new CommitDownloadPreparationRequest
        {
            PreparationId = second.Id,
            DestinationDirectory = testDirectory.Path,
        };

        await Assert.ThrowsAsync<DuplicateDownloadException>(async () =>
            await manager.CommitPreparationAsync(secondCommit));
        Assert.Single(manager.Tasks);

        await manager.CommitPreparationAsync(secondCommit with { AllowDuplicate = true });
        Assert.Equal(2, manager.Tasks.Count);
    }

    [Fact]
    public async Task MagnetPreparation_UsesPausedPayloadAndCanBeCancelled()
    {
        using var testDirectory = new TemporaryDirectory();
        var destination = Path.Combine(testDirectory.Path, "downloads");
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();

        var pending = manager.PrepareAsync(
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567").AsTask();
        await WaitUntilAsync(() => engine.AddUriCount == 1);
        engine.FollowWithPausedTorrentPayload(1, "Feature Film", 4L * 1024 * 1024 * 1024);
        var preparation = await pending;

        Assert.Equal("Feature Film", preparation.DisplayName);
        Assert.Equal(2, preparation.Files.Count);
        Assert.Empty(manager.Tasks);
        Assert.False(Directory.Exists(destination));
        Assert.Equal("mem", engine.LastAddOptions["follow-torrent"]);
        Assert.Equal("true", engine.LastAddOptions["pause-metadata"]);
        Assert.Equal("false", engine.LastAddOptions["bt-save-metadata"]);
        Assert.Equal("false", engine.LastAddOptions["bt-metadata-only"]);

        await manager.CancelPreparationAsync(preparation.Id);
        Assert.True(engine.RemoveCount > 0);
        Assert.Empty(manager.Tasks);
    }

    [Fact]
    public async Task PendingMagnetPreparation_CanBeCommittedAndStartsAllFilesAfterMetadata()
    {
        using var testDirectory = new TemporaryDirectory();
        var destination = Path.Combine(testDirectory.Path, "downloads");
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var preparationId = Guid.NewGuid();
        var pending = manager.PrepareAsync(
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            preparationId: preparationId).AsTask();
        await WaitUntilAsync(() => engine.AddUriCount == 1);

        var commitRequest = new CommitDownloadPreparationRequest
        {
            PreparationId = preparationId,
            DestinationDirectory = destination,
        };
        var committed = Assert.Single(await manager.CommitPreparationAsync(commitRequest));
        var duplicate = Assert.Single(await manager.CommitPreparationAsync(commitRequest));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Single(manager.Tasks);
        Assert.Equal(committed.Id, duplicate.Id);
        Assert.Equal(1, engine.AddUriCount);
        Assert.Equal(0, engine.RemoveCount);
        Assert.Equal("mem", engine.LastAddOptions["follow-torrent"]);
        Assert.Equal("true", engine.LastAddOptions["pause-metadata"]);
        Assert.Equal("false", engine.LastAddOptions["bt-metadata-only"]);
        var payloadGid = engine.FollowWithPausedTorrentPayload(1, "Feature Film", 4L * 1024 * 1024 * 1024);
        await WaitUntilAsync(() => manager.Tasks.Single().EngineGid == payloadGid.ToString("x16"));

        var task = Assert.Single(manager.Tasks);
        Assert.Equal(committed.Id, task.Id);
        Assert.Equal("Feature Film", task.DisplayName);
        Assert.Equal(DownloadState.Waiting, task.State);
        Assert.All(task.Files, static file => Assert.True(file.IsSelected));
        Assert.Equal(destination, task.DestinationDirectory);
        Assert.Equal(destination, engine.ChangedTaskOptions["dir"]);
        Assert.Equal(1, engine.ResumeCount);
    }

    [Fact]
    public async Task MagnetDownload_OverridesMetadataOnlyOptionsAndFollowsPayload()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();

        await manager.AddAsync(new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            DestinationDirectory = testDirectory.Path,
            Options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["follow-torrent"] = "false",
                ["bt-save-metadata"] = "true",
                ["bt-metadata-only"] = "true",
            },
        });

        Assert.Equal("mem", engine.LastAddOptions["follow-torrent"]);
        Assert.Equal("false", engine.LastAddOptions["bt-save-metadata"]);
        Assert.Equal("false", engine.LastAddOptions["bt-metadata-only"]);
        var payloadGid = engine.FollowWithPausedTorrentPayload(1, "Feature Film", 4096);
        await WaitUntilAsync(() => manager.Tasks.Single().EngineGid == payloadGid.ToString("x16"));

        Assert.Equal("Feature Film", Assert.Single(manager.Tasks).DisplayName);
        Assert.Equal(1, engine.ResumeCount);
    }

    [Fact]
    public async Task ScheduledTask_StartsWhenDueAndClearsSchedule()
    {
        using var testDirectory = new TemporaryDirectory();
        var engine = new FakeAria2Engine();
        await using var manager = CreateManager(
            engine,
            new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json")));
        await manager.StartAsync();
        var scheduledAt = DateTimeOffset.UtcNow.AddMilliseconds(150);

        var added = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "https://example.test/scheduled.bin",
            DestinationDirectory = testDirectory.Path,
            ScheduledStartAt = scheduledAt,
        }));

        Assert.Equal(DownloadState.Paused, added.State);
        Assert.NotNull(added.ScheduledStartAt);
        await WaitUntilAsync(() => manager.Tasks.Single().ScheduledStartAt is null);
        Assert.Equal(DownloadState.Waiting, manager.Tasks.Single().State);
        Assert.Equal(1, engine.ResumeCount);
    }

    [Fact]
    public async Task Dispose_WithActiveMagnetTask_PersistsTaskAndForcesEngineShutdown()
    {
        using var testDirectory = new TemporaryDirectory();
        var store = new JsonDownloadSessionStore(Path.Combine(testDirectory.Path, "downloads.json"));
        var engine = new FakeAria2Engine();
        var manager = CreateManager(engine, store);
        await manager.StartAsync();
        var task = Assert.Single(await manager.AddAsync(new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            DestinationDirectory = testDirectory.Path,
        }));

        await manager.DisposeAsync();

        Assert.True(engine.ShutdownForced);
        var persisted = await store.LoadAsync();
        Assert.Equal(task.Id, Assert.Single(Assert.Single(persisted.Groups).Tasks).Id);
    }

    private static DownloadManager CreateManager(IAria2Engine engine, IDownloadSessionStore store) =>
        new(
            engine,
            store,
            new InMemoryEventBus(),
            new DownloadManagerOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

    private static ValueTask StorePausedMagnetAsync(
        IDownloadSessionStore store,
        Guid taskId,
        AddDownloadRequest request) => store.SaveAsync(new DownloadSessionDocument
        {
            Groups =
            [
                new DownloadSessionGroup
                {
                    GroupId = Guid.NewGuid(),
                    Request = request,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Tasks =
                    [
                        new DownloadTaskSnapshot
                        {
                            Id = taskId,
                            EngineGid = "00000000000000aa",
                            Kind = DownloadSourceKind.Uri,
                            Source = request.Source,
                            DestinationDirectory = request.DestinationDirectory,
                            DisplayName = "Feature Film",
                            State = DownloadState.Paused,
                            EngineStatus = 2,
                            TotalLength = 4_096,
                            CompletedLength = 1_024,
                            DownloadSpeed = 512,
                            InfoHash = "0123456789abcdef0123456789abcdef01234567",
                            Torrent = new TorrentSnapshot { Name = "Feature Film", Mode = "Single" },
                            Files =
                            [
                                new DownloadFileSnapshot
                                {
                                    Index = 1,
                                    Path = Path.Combine(request.DestinationDirectory, "feature-film.mkv"),
                                    Length = 4_096,
                                    CompletedLength = 1_024,
                                    IsSelected = true,
                                },
                            ],
                        },
                    ],
                },
            ],
        });

    private sealed class FakeAria2Engine : IAria2Engine
    {
        private readonly Dictionary<ulong, Aria2DownloadDetails> _downloads = [];
        private ulong _nextGid = 1;

        public bool IsInitialized { get; private set; }

        public string? ReportedFilePath { get; init; }

        public bool ProvideTorrentPreview { get; init; }

        public bool DelayPauseRequests { get; init; }

        public int? FailAddOnCall { get; init; }

        public int AddUriCount { get; private set; }

        public int RemoveCount { get; private set; }

        public int RemoveResultCount { get; private set; }

        public int ResumeCount { get; private set; }

        public int PauseCount { get; private set; }

        public bool LastPauseWasForced { get; private set; }

        public bool? ShutdownForced { get; private set; }

        public IReadOnlyList<string> LastUris { get; private set; } = [];

        public Dictionary<string, string> LastAddOptions { get; private set; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ChangedTaskOptions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ChangedGlobalOptions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> InitialGlobalOptions { get; } = new(StringComparer.Ordinal);

        public List<(ulong Gid, int First, int Last)> PriorityRequests { get; } = [];

        public List<ulong> ClearedPriorityRequests { get; } = [];

        public void Initialize(IReadOnlyDictionary<string, string> globalOptions)
        {
            IsInitialized = true;
            foreach (var option in globalOptions)
            {
                InitialGlobalOptions[option.Key] = option.Value;
                ChangedGlobalOptions[option.Key] = option.Value;
            }
        }

        public ulong AddUri(string uri, IReadOnlyDictionary<string, string> options, int position)
        {
            AddUriCount++;
            return Add(uri, options);
        }

        public ulong AddUris(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string> options, int position)
        {
            AddUriCount++;
            if (AddUriCount == FailAddOnCall)
            {
                throw new InvalidOperationException("Synthetic add failure.");
            }
            LastUris = uris.ToArray();
            return Add(uris[0], options);
        }

        public ulong AddTorrent(string torrentFile, IReadOnlyDictionary<string, string> options, int position)
        {
            var gid = Add(torrentFile, options);
            if (ProvideTorrentPreview)
            {
                _downloads[gid] = CreateTorrentPayload(_downloads[gid], "Video Pack", 2 * 1024 * 1024, status: 2);
            }

            return gid;
        }

        public IReadOnlyList<ulong> AddMetalink(
            string metalinkFile,
            IReadOnlyDictionary<string, string> options,
            int position) => [Add(metalinkFile, options)];

        public bool RunOnce() => false;

        public Aria2DownloadDetails GetDownload(ulong gid) => _downloads[gid];

        public Aria2GlobalStatus GetGlobalStatus() => new(0, 0, _downloads.Count, 0, 0);

        public IReadOnlyDictionary<string, string> GetGlobalOptions() => ChangedGlobalOptions;

        public Aria2CapabilityDetails GetCapabilities() => new()
        {
            AbiVersion = 6,
            ExtendedApi = true,
            Features = ["runtime-piece-priority"],
        };

        public void PrioritizePieceRange(ulong gid, int firstPiece, int lastPiece)
        {
            PriorityRequests.Add((gid, firstPiece, lastPiece));
        }

        public void ClearPiecePriority(ulong gid)
        {
            ClearedPriorityRequests.Add(gid);
        }

        public IReadOnlyList<Aria2PeerDetails> GetPeers(ulong gid) => [];

        public IReadOnlyList<Aria2ServerDetails> GetServers(ulong gid) => [];

        public void Pause(ulong gid, bool force)
        {
            PauseCount++;
            LastPauseWasForced = force;
            if (!DelayPauseRequests)
            {
                SetStatus(gid, 2);
            }
        }

        public void Resume(ulong gid)
        {
            ResumeCount++;
            SetStatus(gid, 1);
        }

        public void CompletePause(ulong gid) => SetStatus(gid, 2);

        public void Remove(ulong gid, bool force)
        {
            RemoveCount++;
            SetStatus(gid, 5);
        }

        public void RemoveResult(ulong gid)
        {
            RemoveResultCount++;
            _downloads.Remove(gid);
        }

        public void ChangeOptions(ulong gid, IReadOnlyDictionary<string, string> options)
        {
            foreach (var option in options)
            {
                ChangedTaskOptions[option.Key] = option.Value;
            }

            _downloads[gid] = _downloads[gid] with
            {
                Options = new Dictionary<string, string>(ChangedTaskOptions, StringComparer.Ordinal),
            };
        }

        public void ChangeGlobalOptions(IReadOnlyDictionary<string, string> options)
        {
            foreach (var option in options)
            {
                ChangedGlobalOptions[option.Key] = option.Value;
            }
        }

        public int ChangePosition(ulong gid, int offset, int mode) => Math.Max(offset, 0);

        public void Shutdown(bool force)
        {
            ShutdownForced = force;
        }

        public void Dispose() => IsInitialized = false;

        private ulong Add(string source, IReadOnlyDictionary<string, string> options)
        {
            var gid = _nextGid++;
            LastAddOptions = new Dictionary<string, string>(options, StringComparer.Ordinal);
            var path = ReportedFilePath ?? Path.Combine(options["dir"], options.GetValueOrDefault("out", "download.bin"));
            _downloads.Add(gid, new Aria2DownloadDetails
            {
                Gid = gid.ToString("x16"),
                Status = 1,
                Dir = options["dir"],
                Options = new Dictionary<string, string>(options, StringComparer.Ordinal),
                Files =
                [
                    new Aria2FileDetails
                    {
                        Index = 1,
                        Path = path,
                        Selected = true,
                        Uris = [new Aria2UriDetails { Uri = source }],
                    },
                ],
            });
            return gid;
        }

        public ulong FollowWithPausedTorrentPayload(
            ulong metadataGid,
            string name,
            long totalLength,
            long completedLength = 0)
        {
            var metadata = _downloads[metadataGid];
            var payloadGid = _nextGid++;
            var payload = CreateTorrentPayload(metadata, name, totalLength, status: 2);
            _downloads[metadataGid] = metadata with
            {
                Status = 3,
                FollowedBy = [payloadGid.ToString("x16")],
            };
            _downloads[payloadGid] = payload with
            {
                Gid = payloadGid.ToString("x16"),
                Following = metadataGid.ToString("x16"),
                CompletedLength = completedLength,
                Files = payload.Files
                    .Select(file => file with
                    {
                        CompletedLength = file.Index == 1 ? Math.Min(completedLength, file.Length) : 0,
                    })
                    .ToList(),
            };
            return payloadGid;
        }

        private static Aria2DownloadDetails CreateTorrentPayload(
            Aria2DownloadDetails template,
            string name,
            long totalLength,
            int status)
        {
            return template with
            {
                Status = status,
                TotalLength = totalLength,
                CompletedLength = 0,
                InfoHash = "0123456789abcdef0123456789abcdef01234567",
                Torrent = new Aria2TorrentDetails { Name = name, Mode = 2 },
                Files =
            [
                new Aria2FileDetails
                {
                    Index = 1,
                    Path = Path.Combine(template.Dir, name, "feature-film.mkv"),
                    Length = totalLength - 1024,
                    Selected = true,
                },
                new Aria2FileDetails
                {
                    Index = 2,
                    Path = Path.Combine(template.Dir, name, "feature-film.srt"),
                    Length = 1024,
                    Selected = true,
                },
            ],
            };
        }

        private void SetStatus(ulong gid, int status) => _downloads[gid] = _downloads[gid] with { Status = status };

        public void Fail(ulong gid, int errorCode) =>
            _downloads[gid] = _downloads[gid] with { Status = 4, ErrorCode = errorCode };

        public void SetDiagnostics(ulong gid)
        {
            var current = _downloads[gid];
            _downloads[gid] = current with
            {
                Status = 0,
                TotalLength = 100,
                CompletedLength = 25,
                DownloadSpeed = 25,
                PieceLength = 50,
                NumPieces = 2,
                Bitfield = "a0",
                FollowedBy = ["0000000000000002"],
                Files =
                [
                    current.Files[0] with
                    {
                        Length = 100,
                        CompletedLength = 25,
                        Uris = [current.Files[0].Uris[0] with { Status = 1 }],
                    },
                ],
            };
        }

        public void SetSeeding(ulong gid, string name, long totalLength)
        {
            var current = _downloads[gid];
            _downloads[gid] = current with
            {
                Status = 0,
                TotalLength = totalLength,
                CompletedLength = totalLength,
                Seeder = true,
                InfoHash = "0123456789abcdef0123456789abcdef01234567",
                Torrent = new Aria2TorrentDetails { Name = name, Mode = 2 },
                Files =
                [
                    current.Files[0] with
                    {
                        Path = Path.Combine(Path.GetDirectoryName(current.Files[0].Path)!, "video.mkv"),
                        Length = totalLength,
                        CompletedLength = totalLength,
                    },
                ],
            };
        }

        public ulong FollowWithTorrentPayload(ulong metadataGid, string name, long totalLength)
        {
            var metadata = _downloads[metadataGid];
            var payloadGid = _nextGid++;
            _downloads[metadataGid] = metadata with
            {
                Status = 3,
                TotalLength = 256 * 1024,
                CompletedLength = 256 * 1024,
                FollowedBy = [payloadGid.ToString("x16")],
                Files =
                [
                    metadata.Files[0] with
                    {
                        Path = "[METADATA]",
                        Length = 256 * 1024,
                        CompletedLength = 256 * 1024,
                    },
                ],
            };
            _downloads[payloadGid] = new Aria2DownloadDetails
            {
                Gid = payloadGid.ToString("x16"),
                Status = 3,
                TotalLength = totalLength,
                CompletedLength = totalLength,
                Dir = metadata.Dir,
                InfoHash = "0123456789abcdef0123456789abcdef01234567",
                Following = metadataGid.ToString("x16"),
                Options = new Dictionary<string, string>(metadata.Options, StringComparer.Ordinal),
                Torrent = new Aria2TorrentDetails { Name = name, Mode = 1 },
                Files =
                [
                    new Aria2FileDetails
                    {
                        Index = 1,
                        Path = Path.Combine(metadata.Dir, "feature-film.mkv"),
                        Length = totalLength,
                        CompletedLength = totalLength,
                        Selected = true,
                    },
                ],
            };
            return payloadGid;
        }
    }

    private sealed class BlockingSaveStore : IDownloadSessionStore
    {
        private int _saveCount;

        public TaskCompletionSource BlockedSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DownloadSessionDocument> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DownloadSessionDocument());

        public async ValueTask SaveAsync(
            DownloadSessionDocument document,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) != 2)
            {
                return;
            }

            BlockedSaveStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
