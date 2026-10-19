// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.ViewModels;
using CanX.App.Controls;
using CanX.Application.Downloads;
using CanX.Core.Downloads;
using CanX.Core.Events;
using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DownloadCenterViewModelTests
{
    [Fact]
    public void OptionText_RoundTripsValuesContainingEquals()
    {
        var options = DownloadOptionText.Parse("# comment\nsplit=8\nheader=Authorization: Bearer a=b\n");

        Assert.Equal("8", options["split"]);
        Assert.Equal("Authorization: Bearer a=b", options["header"]);
        Assert.Contains("header=Authorization: Bearer a=b", DownloadOptionText.Format(options), StringComparison.Ordinal);
    }

    [Fact]
    public void CommonOptionEditor_UsesCapabilityValuesForControlTypeAndLimits()
    {
        var booleanOption = CreateCommonOption("enable-dht", "true", "true, false");
        var numberOption = CreateCommonOption("bt-tracker-timeout", "60", "1-600");
        var decimalOption = CreateCommonOption("seed-ratio", "1.0", "0.0-*");
        var optionalDecimalOption = CreateCommonOption("seed-time", string.Empty, "0.0-*");
        var unitOption = CreateCommonOption("max-download-limit", "0", "0-*");
        var boundedUnitOption = CreateCommonOption("min-split-size", "20M", "1048576-1073741824");
        var choiceOption = CreateCommonOption("follow-torrent", "true", "true, mem, false");
        var structuredOption = CreateCommonOption("dht-listen-port", "6881-6999", "1024-65535");
        var trackerOption = CreateCommonOption("bt-tracker", string.Empty, "URI,...");

        Assert.True(booleanOption.IsBooleanEditor);
        Assert.Null(booleanOption.BooleanValue);
        booleanOption.BooleanValue = false;
        Assert.Equal("false", booleanOption.Value);

        Assert.True(numberOption.IsNumberEditor);
        numberOption.Value = "600";
        Assert.Equal("600", numberOption.ToAria2Value());
        numberOption.Value = "700";
        Assert.Throws<InvalidOperationException>(() => numberOption.ToAria2Value());

        Assert.True(decimalOption.IsNumberEditor);
        decimalOption.Value = "1.5";
        Assert.Equal("1.5", decimalOption.ToAria2Value());
        Assert.True(optionalDecimalOption.IsNumberEditor);

        Assert.True(unitOption.IsUnitNumberEditor);
        unitOption.Value = "4096";
        Assert.Equal("4194304", unitOption.ToAria2Value());
        unitOption.SetFromAria2Value("4194304");
        Assert.Equal("4096", unitOption.Value);
        unitOption.SetFromAria2Value("1");
        Assert.Equal("1", unitOption.ToAria2Value());
        boundedUnitOption.SetFromAria2Value("20M");
        Assert.Equal("20480", boundedUnitOption.Value);
        Assert.Equal("20971520", boundedUnitOption.ToAria2Value());
        boundedUnitOption.Value = "1023";
        Assert.Throws<InvalidOperationException>(() => boundedUnitOption.ToAria2Value());
        boundedUnitOption.Value = "1024";
        Assert.Equal("1048576", boundedUnitOption.ToAria2Value());

        Assert.True(choiceOption.IsChoiceEditor);
        Assert.Equal(["", "true", "mem", "false"], choiceOption.ChoiceItems.Select(static item => item.Value));
        choiceOption.SelectedChoice = choiceOption.ChoiceItems.Single(static item => item.Value == "mem");
        Assert.Equal("mem", choiceOption.Value);

        Assert.True(structuredOption.IsSingleLineTextEditor);

        Assert.True(trackerOption.IsMultilineTextEditor);
        trackerOption.Value = "https://a.test/announce, https://b.test/announce\nhttps://c.test/announce";
        Assert.Equal(
            "https://a.test/announce,https://b.test/announce,https://c.test/announce",
            trackerOption.ToAria2Value());
    }

    [Fact]
    public void OptionCoverage_ClassifiesEveryManifestEntry()
    {
        DownloadOptionDefinition[] options =
        [
            new() { Name = "split", Initial = true },
            new() { Name = "max-download-limit", Initial = true, ChangeActive = true, ChangeReserved = true },
            new() { Name = "max-overall-download-limit", ChangeGlobal = true },
            new() { Name = "version", Description = "read only" },
            new() { Name = "internal", Hidden = true },
        ];

        var report = DownloadOptionCoverage.Analyze(options);

        Assert.Equal(5, report.Total);
        Assert.Equal(5, report.Covered);
        Assert.Equal(1, report.Hidden);
        Assert.Equal(2, report.Initial);
        Assert.Equal(1, report.Active);
        Assert.Equal(1, report.Reserved);
        Assert.Equal(1, report.Global);
        Assert.Equal(1, report.ReadOnly);
        Assert.Empty(report.Uncovered);
    }

    [Theory]
    [InlineData("a0", 0, true)]
    [InlineData("a0", 1, false)]
    [InlineData("a0", 2, true)]
    [InlineData("a0", 7, false)]
    public void PieceMap_DecodesAria2BitfieldMostSignificantBitFirst(
        string bitfield,
        int piece,
        bool expected) => Assert.Equal(expected, DownloadPieceMap.IsPieceComplete(bitfield, piece));

    [Theory]
    [InlineData("a8", 5, 3)]
    [InlineData("ff", 5, 5)]
    [InlineData("0f", 8, 4)]
    [InlineData("zz", 8, 0)]
    public void PieceMap_CountsOnlyValidTorrentPieces(
        string bitfield,
        int pieceCount,
        int expected) => Assert.Equal(
            expected,
            DownloadPieceMap.CountCompletedPieces(bitfield, pieceCount));

    [Fact]
    public void DownloadRow_ReportsCompletedPiecesForTheMainList()
    {
        var row = new DownloadTaskRowViewModel(CreateSnapshot(DownloadState.Active, 25) with
        {
            TotalLength = 100,
            PieceLength = 20,
            PieceCount = 5,
            Bitfield = "a8",
            Files =
            [
                new DownloadFileSnapshot
                {
                    Index = 1,
                    Path = "/tmp/video.mkv",
                    Length = 100,
                    CompletedLength = 25,
                    IsSelected = true,
                },
            ],
        });

        Assert.True(row.HasPieceMap);
        Assert.Equal("已完成 3/5 个分片（60.0%）", row.PieceSummary);
        Assert.Equal("5 个分片 · 每片 20 B · 从左到右、从上到下", row.PieceDetails);
        Assert.Contains("25 B / 100 B", row.TooltipStatusSummary, StringComparison.Ordinal);
        Assert.Contains("1 个文件", row.TooltipIdentitySummary, StringComparison.Ordinal);

        row.Update(row.Snapshot with { PieceCount = 0, Bitfield = string.Empty });

        Assert.False(row.HasPieceMap);
        Assert.Equal("已下载 25 B", row.PieceSummary);
    }

    [Fact]
    public async Task DownloadEvents_UpdateExistingSelectedRow()
    {
        var eventBus = new InMemoryEventBus();
        var initial = CreateSnapshot(DownloadState.Waiting, 0);
        var manager = new StubDownloadManager([initial]);
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());

        await viewModel.InitializeAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        var selected = viewModel.SelectedTask;
        var updated = initial with
        {
            State = DownloadState.Active,
            TotalLength = 100,
            CompletedLength = 25,
            DownloadSpeed = 1024,
        };

        eventBus.Publish(new DownloadListChanged([updated]));

        Assert.Same(selected, viewModel.SelectedTask);
        Assert.Equal("下载中", viewModel.SelectedTask.State);
        Assert.Equal(25, viewModel.SelectedTask.Progress);
        Assert.Equal("1 KiB/s", viewModel.SelectedTask.DownloadSpeed);
    }

    [Fact]
    public async Task StartupListEvent_IsIgnoredUntilInitialTasksAreApplied()
    {
        var eventBus = new InMemoryEventBus();
        var restored = CreateSnapshot(DownloadState.Active, 25);
        var stale = CreateSnapshot(DownloadState.Complete, 100);
        var manager = new StubDownloadManager([restored])
        {
            StartAction = () => eventBus.Publish(new DownloadListChanged([stale])),
        };
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());

        await viewModel.InitializeAsync();

        Assert.Equal(restored.Id, Assert.Single(viewModel.Tasks).Id);

        var updated = restored with { State = DownloadState.Paused, CompletedLength = 50 };
        eventBus.Publish(new DownloadListChanged([updated]));

        var row = Assert.Single(viewModel.Tasks);
        Assert.Equal(restored.Id, row.Id);
        Assert.Equal("已暂停", row.State);
        Assert.Equal(50, row.Snapshot.CompletedLength);
    }

    [Fact]
    public async Task SynchronizeTaskList_ReconcilesUiWithManagerAfterResume()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager([]);
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        Assert.Empty(viewModel.Tasks);

        var restored = CreateSnapshot(DownloadState.Active, 25);
        manager.SetTasks([restored]);

        viewModel.SynchronizeTaskList();

        Assert.Equal(restored.Id, Assert.Single(viewModel.Tasks).Id);
    }

    [Fact]
    public async Task IncompleteMediaTask_IsPlayableOnlyWithRuntimePiecePriority()
    {
        var eventBus = new InMemoryEventBus();
        var task = CreateSnapshot(DownloadState.Active, 25) with
        {
            TotalLength = 100,
            PieceLength = 50,
            PieceCount = 2,
            Files =
            [
                new DownloadFileSnapshot
                {
                    Index = 1,
                    Path = "/tmp/video.mkv",
                    Length = 100,
                    IsSelected = true,
                },
                new DownloadFileSnapshot
                {
                    Index = 2,
                    Path = "/tmp/subtitle.srt",
                    Length = 10,
                    IsSelected = true,
                },
            ],
        };
        var manager = new StubDownloadManager(
            [task],
            new DownloadCapabilityManifest
            {
                ExtendedApi = true,
                Features = ["runtime-piece-priority"],
            });
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());

        await viewModel.InitializeAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);

        Assert.True(viewModel.CanPlaySelectedTask);
        Assert.Equal(1, viewModel.SelectedPlayableFile?.Index);
    }

    [Fact]
    public async Task PauseAndResumeMenu_TracksPublishedStateAndUsesForcePause()
    {
        var eventBus = new InMemoryEventBus();
        var active = CreateSnapshot(DownloadState.Active, 25);
        var manager = new StubDownloadManager([active]);
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);

        Assert.True(viewModel.PauseCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCommand.CanExecute(null));
        viewModel.PauseCommand.Execute(null);
        await WaitUntilAsync(() => manager.PauseRequests.Count == 1);
        Assert.True(manager.PauseRequests[0]);

        eventBus.Publish(new DownloadListChanged([active with { State = DownloadState.Paused }]));

        Assert.False(viewModel.PauseCommand.CanExecute(null));
        Assert.True(viewModel.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task TaskFilter_DefaultsToDownloadingAndCanShowCompletedTasks()
    {
        var eventBus = new InMemoryEventBus();
        var active = CreateSnapshot(DownloadState.Active, 25);
        var paused = CreateSnapshot(DownloadState.Paused, 50);
        var failed = CreateSnapshot(DownloadState.Error, 75);
        var completed = CreateSnapshot(DownloadState.Complete, 100);
        var manager = new StubDownloadManager([active, paused, failed, completed]);
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());

        await viewModel.InitializeAsync();

        Assert.True(viewModel.ShowDownloadingTasks);
        Assert.False(viewModel.ShowCompletedTasks);
        Assert.Equal([active.Id, paused.Id, failed.Id], viewModel.Tasks.Select(static task => task.Id));

        viewModel.ShowCompletedTasks = true;

        Assert.False(viewModel.ShowDownloadingTasks);
        Assert.True(viewModel.ShowCompletedTasks);
        Assert.Equal(completed.Id, Assert.Single(viewModel.Tasks).Id);

        viewModel.ShowDownloadingTasks = true;
        eventBus.Publish(new DownloadListChanged([
            active with { State = DownloadState.Complete },
            paused,
            failed,
            completed,
        ]));

        Assert.Equal([paused.Id, failed.Id], viewModel.Tasks.Select(static task => task.Id));
        viewModel.ShowCompletedTasks = true;
        Assert.Equal([active.Id, completed.Id], viewModel.Tasks.Select(static task => task.Id));
    }

    [Fact]
    public async Task CapabilityEditor_FiltersAndRoutesOptionByScope()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager(
            [CreateSnapshot(DownloadState.Active, 0)],
            new DownloadCapabilityManifest
            {
                ExtendedApi = true,
                Options =
                [
                    new DownloadOptionDefinition
                    {
                        Name = "max-download-limit",
                        Description = "Per-task limit",
                        Initial = true,
                        ChangeActive = true,
                        ChangeReserved = true,
                        ChangeGlobal = true,
                    },
                    new DownloadOptionDefinition { Name = "split", Initial = true },
                ],
            });
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);

        viewModel.OptionSearchText = "limit";
        viewModel.SelectedOptionDefinition = Assert.Single(viewModel.OptionDefinitions);
        viewModel.SelectedGlobalOptionDefinition = Assert.Single(viewModel.GlobalOptionDefinitions);
        Assert.Single(viewModel.GlobalOptionDefinitions);
        Assert.Single(viewModel.TaskOptionDefinitions);
        viewModel.SelectedOptionValue = "4M";
        viewModel.SelectedGlobalOptionValue = "4M";

        Assert.True(viewModel.StageNewTaskOptionCommand.CanExecute(null));
        Assert.True(viewModel.ApplySelectedTaskOptionCommand.CanExecute(null));
        Assert.True(viewModel.ApplySelectedGlobalOptionCommand.CanExecute(null));

        viewModel.StageNewTaskOptionCommand.Execute(null);
        viewModel.ApplySelectedTaskOptionCommand.Execute(null);
        viewModel.ApplySelectedGlobalOptionCommand.Execute(null);
        await Task.Yield();

        Assert.Equal("max-download-limit=4M", viewModel.NewTaskOptionsText);
        Assert.Equal("4M", manager.ChangedTaskOptions["max-download-limit"]);
        Assert.Equal("4M", manager.ChangedGlobalOptions["max-download-limit"]);
    }

    [Fact]
    public async Task CommonOptionEditor_RoutesOnlyValuesSupportedByEachScope()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager(
            [CreateSnapshot(DownloadState.Active, 0)],
            new DownloadCapabilityManifest
            {
                ExtendedApi = true,
                Options =
                [
                    new DownloadOptionDefinition
                    {
                        Name = "max-download-limit",
                        DefaultValue = "0",
                        PossibleValues = "0-*",
                        Initial = true,
                        ChangeActive = true,
                    },
                    new DownloadOptionDefinition
                    {
                        Name = "split",
                        DefaultValue = "5",
                        PossibleValues = "1-*",
                        Initial = true,
                    },
                    new DownloadOptionDefinition
                    {
                        Name = "max-overall-download-limit",
                        DefaultValue = "0",
                        PossibleValues = "0-*",
                        ChangeGlobal = true,
                    },
                ],
            });
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);
        Assert.Equal(3, viewModel.CommonOptions.Count);
        Assert.Equal(2, viewModel.TaskCommonOptions.Count);
        Assert.Single(viewModel.GlobalCommonOptions);
        viewModel.CommonOptions.Single(option => option.Name == "max-download-limit").Value = "4096";
        viewModel.CommonOptions.Single(option => option.Name == "split").Value = "8";
        viewModel.GlobalCommonOptions.Single(option => option.Name == "max-overall-download-limit").Value = "20480";

        viewModel.StageCommonNewTaskOptionsCommand.Execute(null);
        viewModel.ApplyCommonTaskOptionsCommand.Execute(null);
        viewModel.ApplyCommonGlobalOptionsCommand.Execute(null);
        await WaitUntilAsync(() => manager.ChangedTaskOptions.Count == 1 && manager.ChangedGlobalOptions.Count == 1);

        Assert.Contains("max-download-limit=4194304", viewModel.NewTaskOptionsText, StringComparison.Ordinal);
        Assert.Contains("split=8", viewModel.NewTaskOptionsText, StringComparison.Ordinal);
        Assert.DoesNotContain("max-overall-download-limit", viewModel.NewTaskOptionsText, StringComparison.Ordinal);
        Assert.Equal("4194304", manager.ChangedTaskOptions["max-download-limit"]);
        Assert.False(manager.ChangedTaskOptions.ContainsKey("split"));
        Assert.Equal("20971520", manager.ChangedGlobalOptions["max-overall-download-limit"]);
    }

    [Fact]
    public async Task CommonGlobalOptions_IncludeMagnetSettingsAndReportRestartRequirement()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager(
            [],
            new DownloadCapabilityManifest
            {
                ExtendedApi = true,
                Options =
                [
                    new DownloadOptionDefinition
                    {
                        Name = "enable-dht",
                        DefaultValue = "true",
                        PossibleValues = "true, false",
                    },
                    new DownloadOptionDefinition { Name = "dht-listen-port" },
                    new DownloadOptionDefinition { Name = "bt-tracker", ChangeGlobal = true },
                ],
            });
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();

        Assert.Equal(
            ["enable-dht", "dht-listen-port", "bt-tracker"],
            viewModel.GlobalCommonOptions.Select(static option => option.Name));
        viewModel.GlobalCommonOptions.Single(option => option.Name == "enable-dht").Value = "true";
        viewModel.GlobalCommonOptions.Single(option => option.Name == "bt-tracker").Value =
            "https://tracker.example.test/announce";

        viewModel.ApplyCommonGlobalOptionsCommand.Execute(null);
        await WaitUntilAsync(() => manager.ChangedGlobalOptions.Count == 2);

        Assert.Equal("true", manager.ChangedGlobalOptions["enable-dht"]);
        Assert.Equal(
            "https://tracker.example.test/announce",
            manager.ChangedGlobalOptions["bt-tracker"]);
        Assert.Contains("重启后生效", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalCommonOptions_RestorePersistedValuesWithoutChangingTaskEditors()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager(
            [],
            new DownloadCapabilityManifest
            {
                Options =
                [
                    new DownloadOptionDefinition
                    {
                        Name = "max-download-limit",
                        DefaultValue = "0",
                        PossibleValues = "0-*",
                        Initial = true,
                        ChangeActive = true,
                        ChangeGlobal = true,
                    },
                    new DownloadOptionDefinition
                    {
                        Name = "enable-dht",
                        DefaultValue = "true",
                        PossibleValues = "true, false",
                    },
                ],
            })
        {
            GlobalStatus = new DownloadGlobalSnapshot
            {
                Options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["max-download-limit"] = "4194304",
                    ["enable-dht"] = "false",
                },
            },
        };
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());

        await viewModel.InitializeAsync();

        Assert.Equal(
            "4096",
            viewModel.GlobalCommonOptions.Single(option => option.Name == "max-download-limit").Value);
        Assert.Equal(
            "false",
            viewModel.GlobalCommonOptions.Single(option => option.Name == "enable-dht").Value);
        Assert.Empty(viewModel.TaskCommonOptions.Single(option => option.Name == "max-download-limit").Value);
        Assert.Contains("max-download-limit=4194304", viewModel.GlobalOptionsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavePendingGlobalOptions_AppliesCommonEditorChangesOnlyOnce()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager(
            [],
            new DownloadCapabilityManifest
            {
                Options =
                [
                    new DownloadOptionDefinition
                    {
                        Name = "max-overall-download-limit",
                        DefaultValue = "0",
                        PossibleValues = "0-*",
                        ChangeGlobal = true,
                    },
                    new DownloadOptionDefinition
                    {
                        Name = "custom-global",
                        DefaultValue = "default",
                        ChangeGlobal = true,
                    },
                ],
            });
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        viewModel.GlobalCommonOptions.Single().Value = "20480";
        viewModel.SelectedGlobalOptionDefinition = viewModel.GlobalOptionDefinitions
            .Single(option => option.Name == "custom-global");
        viewModel.SelectedGlobalOptionValue = "changed";

        Assert.True(await viewModel.SavePendingGlobalOptionsAsync());
        Assert.Equal("20971520", manager.ChangedGlobalOptions["max-overall-download-limit"]);
        Assert.Equal("changed", manager.ChangedGlobalOptions["custom-global"]);
        Assert.False(await viewModel.SavePendingGlobalOptionsAsync());
    }

    [Fact]
    public async Task GlobalStatusRefresh_DoesNotOverwritePendingEdits()
    {
        var eventBus = new InMemoryEventBus();
        var definition = new DownloadOptionDefinition
        {
            Name = "max-overall-download-limit",
            DefaultValue = "0",
            PossibleValues = "0-*",
            ChangeGlobal = true,
        };
        var manager = new StubDownloadManager(
            [],
            new DownloadCapabilityManifest { Options = [definition] })
        {
            GlobalStatus = new DownloadGlobalSnapshot
            {
                Options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [definition.Name] = "1048576",
                },
            },
        };
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        var editor = viewModel.GlobalCommonOptions.Single();
        editor.Value = "2048";

        eventBus.Publish(new DownloadGlobalStatusChanged(new DownloadGlobalSnapshot
        {
            Options = new Dictionary<string, string>(manager.GlobalStatus.Options, StringComparer.Ordinal),
        }));
        await Task.Yield();
        Assert.Equal("2048", editor.Value);

        eventBus.Publish(new DownloadGlobalStatusChanged(new DownloadGlobalSnapshot
        {
            Options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [definition.Name] = "3145728",
            },
        }));
        await Task.Yield();
        Assert.Equal("2048", editor.Value);

        Assert.True(await viewModel.SavePendingGlobalOptionsAsync());
        eventBus.Publish(new DownloadGlobalStatusChanged(new DownloadGlobalSnapshot
        {
            Options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [definition.Name] = "4194304",
            },
        }));
        await WaitUntilAsync(() => editor.Value == "4096");
    }

    [Fact]
    public async Task ImportedSources_SelectMetadataKindAndRemoveDuplicates()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager([]);
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());

        viewModel.ImportSources([" /tmp/video.torrent "]);
        Assert.Equal(DownloadSourceKind.Torrent, viewModel.SelectedSourceKind);
        Assert.Equal("/tmp/video.torrent", viewModel.SourceText);

        viewModel.ImportSources([
            "https://example.test/a.mp4",
            "https://example.test/a.mp4",
            "https://example.test/b.mp4",
        ]);
        Assert.Equal(DownloadSourceKind.Uri, viewModel.SelectedSourceKind);
        Assert.Equal(2, viewModel.SourceText.Split(Environment.NewLine).Length);
    }

    [Fact]
    public async Task AddSelectedSourcesAsync_ReturnsSuccessAndClearsDialogFields()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager([]);
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        viewModel.SourceText = "https://example.test/video.mp4";
        viewModel.OutputName = "renamed.mp4";
        viewModel.NewTaskOptionsText = "split=4";

        var succeeded = await viewModel.AddSelectedSourcesAsync();

        Assert.True(succeeded);
        var request = Assert.Single(manager.AddRequests);
        Assert.Equal("renamed.mp4", request.OutputName);
        Assert.Equal("4", request.Options["split"]);
        Assert.Empty(viewModel.SourceText);
        Assert.Empty(viewModel.OutputName);
        Assert.Empty(viewModel.NewTaskOptionsText);
    }

    [Fact]
    public async Task AddDialog_PreparesTorrentThenCommitsFileChoices()
    {
        var manager = new StubDownloadManager([])
        {
            PreparationToReturn = new DownloadPreparation
            {
                Id = Guid.NewGuid(),
                Kind = DownloadSourceKind.Uri,
                Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                DisplayName = "Video Pack",
                TotalLength = 2048,
                IsTorrent = true,
                Files =
                [
                    new DownloadPreparationFile { Index = 1, Path = "video.mkv", Length = 1536 },
                    new DownloadPreparationFile { Index = 2, Path = "sample.mkv", Length = 512 },
                ],
            },
        };
        await using var viewModel = new AddDownloadTaskViewModel(manager, "/tmp/downloads")
        {
            SourceText = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
        };

        await viewModel.PrepareAsync();

        Assert.True(viewModel.IsDetailsStep);
        Assert.True(viewModel.ShowFileList);
        Assert.True(viewModel.AllFilesSelected);
        viewModel.Files[0].Priority = "高";
        viewModel.Files[1].IsSelected = false;
        Assert.True(await viewModel.AddAsync());

        var committed = Assert.IsType<CommitDownloadPreparationRequest>(manager.LastCommitRequest);
        Assert.Equal(DownloadFilePriority.High, committed.Files[0].Priority);
        Assert.False(committed.Files[1].IsSelected);
    }

    [Fact]
    public async Task AddDialog_CanCommitMagnetWhileMetadataIsPending()
    {
        var pendingPreparation = new TaskCompletionSource<DownloadPreparation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new StubDownloadManager([])
        {
            PendingPreparation = pendingPreparation,
        };
        await using var viewModel = new AddDownloadTaskViewModel(manager, "/tmp/downloads")
        {
            SourceText = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
        };

        var prepareTask = viewModel.PrepareAsync();
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CanContinue);
        Assert.True(viewModel.CanCommitPendingMetadata);

        Assert.True(await viewModel.CommitPendingMetadataAsync());
        Assert.False(await viewModel.CommitPendingMetadataAsync());
        await prepareTask;

        var committed = Assert.IsType<CommitDownloadPreparationRequest>(manager.LastCommitRequest);
        Assert.Equal(manager.LastPreparationId, committed.PreparationId);
        Assert.Empty(committed.Files);
        Assert.Equal(1, manager.CommitPreparationCount);
    }

    [Fact]
    public async Task AddDialog_ExplicitDuplicateConfirmationIsForwardedOnce()
    {
        var existing = CreateSnapshot(DownloadState.Active, 0);
        var manager = new StubDownloadManager([existing])
        {
            RejectDuplicateCommits = true,
        };
        await using var viewModel = new AddDownloadTaskViewModel(manager, "/tmp/downloads")
        {
            SourceText = "https://example.test/video.mp4",
        };
        await viewModel.PrepareAsync();

        await Assert.ThrowsAsync<DuplicateDownloadException>(async () => await viewModel.AddAsync());
        Assert.True(await viewModel.AddAsync(allowDuplicate: true));

        Assert.Equal(2, manager.CommitPreparationCount);
        Assert.True(manager.LastCommitRequest?.AllowDuplicate);
    }

    [Fact]
    public async Task RemoveSelectedAsync_SelectsWhetherLocalFilesAreRemoved()
    {
        var eventBus = new InMemoryEventBus();
        var manager = new StubDownloadManager([CreateSnapshot(DownloadState.Paused, 0)]);
        await using var viewModel = new DownloadCenterViewModel(manager, eventBus, Path.GetTempPath());
        await viewModel.InitializeAsync();
        viewModel.SelectedTask = Assert.Single(viewModel.Tasks);

        await viewModel.RemoveSelectedAsync(deleteFiles: false);
        await viewModel.RemoveSelectedAsync(deleteFiles: true);

        Assert.Equal([false, true], manager.DeleteFileRequests);
    }

    private static DownloadTaskSnapshot CreateSnapshot(DownloadState state, long completed) => new()
    {
        Id = Guid.NewGuid(),
        EngineGid = "0000000000000001",
        Kind = DownloadSourceKind.Uri,
        Source = "https://example.test/video.mp4",
        DestinationDirectory = Path.GetTempPath(),
        DisplayName = "video.mp4",
        State = state,
        CompletedLength = completed,
    };

    private static DownloadCommonOptionViewModel CreateCommonOption(
        string name,
        string defaultValue,
        string possibleValues) =>
        new(
            name,
            string.Empty,
            new DownloadOptionDefinition
            {
                Name = name,
                DefaultValue = defaultValue,
                PossibleValues = possibleValues,
            });

    private sealed class StubDownloadManager(
        IReadOnlyList<DownloadTaskSnapshot> tasks,
        DownloadCapabilityManifest? capabilities = null) : IDownloadManager
    {
        private IReadOnlyList<DownloadTaskSnapshot> _tasks = tasks;

        public bool IsStarted { get; private set; }

        public IReadOnlyList<DownloadTaskSnapshot> Tasks => _tasks;

        public DownloadGlobalSnapshot GlobalStatus { get; init; } = new();

        public DownloadCapabilityManifest Capabilities { get; } = capabilities ?? new();

        public Dictionary<string, string> ChangedTaskOptions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ChangedGlobalOptions { get; } = new(StringComparer.Ordinal);

        public List<AddDownloadRequest> AddRequests { get; } = [];

        public List<bool> DeleteFileRequests { get; } = [];

        public List<bool> PauseRequests { get; } = [];

        public DownloadPreparation? PreparationToReturn { get; init; }

        public TaskCompletionSource<DownloadPreparation>? PendingPreparation { get; init; }

        public Guid? LastPreparationId { get; private set; }

        public CommitDownloadPreparationRequest? LastCommitRequest { get; private set; }

        public int CommitPreparationCount { get; private set; }

        public bool RejectDuplicateCommits { get; init; }

        public Action? StartAction { get; init; }

        public void SetTasks(IReadOnlyList<DownloadTaskSnapshot> tasks) => _tasks = tasks;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            IsStarted = true;
            StartAction?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddAsync(
            AddDownloadRequest request,
            CancellationToken cancellationToken = default)
        {
            AddRequests.Add(request);
            return ValueTask.FromResult(Tasks);
        }

        public async ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddBatchAsync(
            IReadOnlyList<AddDownloadRequest> requests,
            CancellationToken cancellationToken = default)
        {
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
            Guid? preparationId = null)
        {
            LastPreparationId = preparationId;
            if (PendingPreparation is not null)
            {
                return new ValueTask<DownloadPreparation>(PendingPreparation.Task);
            }

            return ValueTask.FromResult(PreparationToReturn ?? new DownloadPreparation
            {
                Id = preparationId ?? Guid.NewGuid(),
                Kind = DownloadSourceKind.Uri,
                Source = source,
                DisplayName = "video.mp4",
            });
        }

        public ValueTask<IReadOnlyList<DownloadTaskSnapshot>> CommitPreparationAsync(
            CommitDownloadPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            CommitPreparationCount++;
            LastCommitRequest = request;
            if (RejectDuplicateCommits && !request.AllowDuplicate)
            {
                throw new DuplicateDownloadException(Tasks[0]);
            }

            PendingPreparation?.TrySetCanceled();
            return ValueTask.FromResult(Tasks);
        }

        public ValueTask CancelPreparationAsync(
            Guid preparationId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
        {
            PauseRequests.Add(force);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(Guid id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveAsync(
            Guid id,
            bool force = false,
            bool deleteFiles = false,
            CancellationToken cancellationToken = default)
        {
            DeleteFileRequests.Add(deleteFiles);
            return ValueTask.CompletedTask;
        }

        public ValueTask ChangeOptionsAsync(
            Guid id,
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken = default)
        {
            foreach (var option in options)
            {
                ChangedTaskOptions[option.Key] = option.Value;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ChangeGlobalOptionsAsync(
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken = default)
        {
            foreach (var option in options)
            {
                ChangedGlobalOptions[option.Key] = option.Value;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ChangeCompletionPolicyAsync(
            Guid id,
            bool notifyOnCompletion,
            DownloadCompletionAction completionAction,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ScheduleAsync(
            Guid id,
            DateTimeOffset? startAt,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
