// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using CanX.Application.Downloads;
using CanX.Core.Downloads;
using CanX.Core.Events;
using CanX.Downloads.Engine;
using CanX.Downloads.Persistence;
using CanX.Streaming;

namespace CanX.Downloads;

public sealed class DownloadManager : IDownloadManager
{
    private readonly IAria2Engine _engine;
    private readonly IDownloadSessionStore _store;
    private readonly IEventBus _eventBus;
    private readonly DownloadManagerOptions _options;
    private readonly Channel<IWorkItem> _commands = Channel.CreateUnbounded<IWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<RuntimeGroup> _groups = [];
    private readonly Dictionary<Guid, RuntimePreparation> _preparations = [];
    private readonly Dictionary<Guid, IReadOnlyList<DownloadTaskSnapshot>> _committedPreparations = [];
    private readonly HashSet<ulong> _previewCleanupGids = [];
    private readonly List<Guid> _taskOrder = [];
    private readonly Dictionary<string, string> _configuredGlobalOptions = new(StringComparer.Ordinal);
    private readonly Lock _mediaStreamSync = new();
    private readonly Dictionary<Guid, int> _openMediaStreams = [];
    private DownloadTaskSnapshot[] _publishedTasks = [];
    private DownloadGlobalSnapshot _globalStatus = new();
    private DownloadCapabilityManifest _capabilities = new();
    private Task? _worker;
    private Exception? _workerFailure;
    private int _started;
    private int _disposed;

    public DownloadManager(
        IAria2Engine engine,
        IDownloadSessionStore store,
        IEventBus eventBus,
        DownloadManagerOptions? options = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _options = options ?? new DownloadManagerOptions();
    }

    public bool IsStarted => Volatile.Read(ref _started) == 1;

    public IReadOnlyList<DownloadTaskSnapshot> Tasks => Volatile.Read(ref _publishedTasks);

    public DownloadGlobalSnapshot GlobalStatus => Volatile.Read(ref _globalStatus);

    public DownloadCapabilityManifest Capabilities => Volatile.Read(ref _capabilities);

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var globalOptions = new Dictionary<string, string>(_options.GlobalOptions, StringComparer.Ordinal);
            foreach (var option in document.GlobalOptions)
            {
                globalOptions[option.Key] = option.Value;
                _configuredGlobalOptions[option.Key] = option.Value;
            }

            _engine.Initialize(globalOptions);
            Volatile.Write(ref _capabilities, MapCapabilities(_engine.GetCapabilities()));
            Restore(document);
            RefreshAll(publishEvents: false, refreshGlobalOptions: true);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            PublishList();
            _worker = Task.Run(() => RunLoopAsync(_shutdown.Token), CancellationToken.None);
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            _engine.Dispose();
            throw;
        }
    }

    public ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddAsync(
        AddDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = DownloadRequestValidator.ValidateAndNormalize(request);
        return EnqueueAsync(cancellation => AddCoreAsync(normalized, cancellation, knownInfoHash: null), cancellationToken);
    }

    public ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddBatchAsync(
        IReadOnlyList<AddDownloadRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(requests), "A download batch must contain between 1 and 256 requests.");
        }

        var normalized = requests.Select(DownloadRequestValidator.ValidateAndNormalize).ToArray();
        return EnqueueAsync(cancellation => AddBatchCoreAsync(normalized, cancellation), cancellationToken);
    }

    public async ValueTask<DownloadPreparation> PrepareAsync(
        string source,
        CancellationToken cancellationToken = default,
        Guid? preparationId = null)
    {
        var preparation = await EnqueueAsync(
            cancellation => ValueTask.FromResult(CreatePreparation(
                preparationId ?? Guid.NewGuid(),
                source,
                cancellation)),
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await preparation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await CancelPreparationAsync(preparation.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original preparation error or cancellation.
            }

            throw;
        }
    }

    public ValueTask<IReadOnlyList<DownloadTaskSnapshot>> CommitPreparationAsync(
        CommitDownloadPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnqueueAsync(
            cancellation => CommitPreparationCoreAsync(request, cancellation),
            cancellationToken);
    }

    public ValueTask CancelPreparationAsync(
        Guid preparationId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (_preparations.Remove(preparationId, out var preparation))
                {
                    CleanupPreparation(preparation, keepPayload: false);
                    preparation.Completion.TrySetCanceled();
                }

                return ValueTask.FromResult(true);
            },
            cancellationToken).AsVoid();

    public ValueTask PauseAsync(Guid id, bool force = false, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async cancellation =>
            {
                var task = FindTask(id);
                if (task.AwaitingMetadataPayload)
                {
                    task.PausePayloadOnFollow = true;
                    task.ResumePayloadOnFollow = false;
                }
                else if (task.Snapshot.State != DownloadState.Paused && !task.PauseRequested)
                {
                    _engine.Pause(RequireGid(task), force);
                    task.PauseRequested = true;
                    task.ResumeRequested = false;
                    task.ResumeWhenPaused = false;
                    _engine.RunOnce();
                }

                task.Snapshot = task.Snapshot with
                {
                    State = DownloadState.Paused,
                    EngineStatus = 2,
                    DownloadSpeed = 0,
                    UploadSpeed = 0,
                    EtaSeconds = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await PersistAndPublishAsync(task, cancellation).ConfigureAwait(false);
                return true;
            },
            cancellationToken).AsVoid();

    public ValueTask ResumeAsync(Guid id, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async cancellation =>
            {
                var (group, task) = FindTaskAndGroup(id);
                if (task.AwaitingMetadataPayload)
                {
                    task.PausePayloadOnFollow = false;
                    task.ResumePayloadOnFollow = true;
                }
                else if (task.PauseRequested)
                {
                    task.ResumeWhenPaused = true;
                }
                else if (task.Snapshot.State == DownloadState.Error)
                {
                    if (group.Tasks.Count != 1)
                    {
                        throw new InvalidOperationException(
                            "A failed multi-result Metalink must be removed and added again as a group.");
                    }

                    if (task.EngineGid is { } failedGid)
                    {
                        _engine.RemoveResult(failedGid);
                        task.EngineGid = null;
                    }

                    var retryRequest = group.Request with { StartPaused = false };
                    var awaitingMetadataPayload = RequiresMetadataHandoff(retryRequest);
                    var retriedGids = AddToEngine(awaitingMetadataPayload
                        ? CreateMetadataHandoffRequest(retryRequest)
                        : retryRequest);
                    if (retriedGids.Count != 1)
                    {
                        throw new InvalidOperationException("Retry unexpectedly created more than one aria2 task.");
                    }

                    task.EngineGid = retriedGids[0];
                    task.AwaitingMetadataPayload = awaitingMetadataPayload;
                    task.ResumePayloadOnFollow = awaitingMetadataPayload;
                    task.Snapshot = task.Snapshot with
                    {
                        EngineGid = retriedGids[0].ToString("x16"),
                        ErrorCode = 0,
                        ErrorMessage = string.Empty,
                    };
                }
                else if (task.ResumeRequested || task.Snapshot.State is DownloadState.Active or DownloadState.Waiting)
                {
                    // Resume is idempotent while aria2 is still applying an earlier request.
                }
                else if (task.EngineGid is { } gid)
                {
                    _engine.Resume(gid);
                    task.ResumeRequested = true;
                    _engine.RunOnce();
                }
                else
                {
                    var restoreRequest = group.Request with { StartPaused = false };
                    var awaitingMetadataPayload = RequiresMetadataHandoff(restoreRequest);
                    var restoredGids = AddToEngine(awaitingMetadataPayload
                        ? CreateMetadataHandoffRequest(restoreRequest)
                        : restoreRequest);
                    if (restoredGids.Count != 1)
                    {
                        throw new InvalidOperationException("Cannot individually recreate a multi-result Metalink task.");
                    }

                    task.EngineGid = restoredGids[0];
                    task.AwaitingMetadataPayload = awaitingMetadataPayload;
                    task.ResumePayloadOnFollow = awaitingMetadataPayload;
                    task.Snapshot = task.Snapshot with { EngineGid = restoredGids[0].ToString("x16") };
                }

                task.Snapshot = task.Snapshot with
                {
                    State = DownloadState.Waiting,
                    EngineStatus = 1,
                    DownloadSpeed = 0,
                    UploadSpeed = 0,
                    EtaSeconds = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await PersistAndPublishAsync(task, cancellation).ConfigureAwait(false);
                return true;
            },
            cancellationToken).AsVoid();

    public ValueTask RemoveAsync(
        Guid id,
        bool force = false,
        bool deleteFiles = false,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async cancellation =>
            {
                var (group, task) = FindTaskAndGroup(id);
                var filesToDelete = deleteFiles ? ResolveTaskFilesForDeletion(task.Snapshot) : [];
                if (task.EngineGid is { } gid)
                {
                    if (task.Snapshot.EngineStatus is 3 or 4 or 5)
                    {
                        _engine.RemoveResult(gid);
                    }
                    else
                    {
                        _engine.Remove(gid, force);
                    }

                    task.EngineGid = null;
                }

                task.Snapshot = task.Snapshot with
                {
                    State = DownloadState.Removed,
                    EngineStatus = 5,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                _taskOrder.Remove(id);
                foreach (var path in filesToDelete)
                {
                    SafeDownloadFileDeletion.Delete(task.Snapshot.DestinationDirectory, path);
                }

                if (group.Tasks.All(static item => item.Snapshot.State == DownloadState.Removed))
                {
                    _groups.Remove(group);
                }

                await PersistAsync(cancellation).ConfigureAwait(false);
                PublishTask(task.Snapshot);
                PublishList();
                return true;
            },
            cancellationToken).AsVoid();

    public ValueTask ChangeOptionsAsync(
        Guid id,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default)
    {
        DownloadRequestValidator.ValidateOptions(options);
        return EnqueueAsync(
            async cancellation =>
            {
                var (group, task) = FindTaskAndGroup(id);
                _engine.ChangeOptions(RequireGid(task), options);
                var merged = new Dictionary<string, string>(group.Request.Options, StringComparer.Ordinal);
                foreach (var option in options)
                {
                    merged[option.Key] = option.Value;
                }

                group.Request = group.Request with { Options = merged };
                task.Snapshot = task.Snapshot with
                {
                    Options = new Dictionary<string, string>(merged, StringComparer.Ordinal),
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await PersistAndPublishAsync(task, cancellation).ConfigureAwait(false);
                return true;
            },
            cancellationToken).AsVoid();
    }

    public ValueTask ChangeGlobalOptionsAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default)
    {
        DownloadRequestValidator.ValidateOptions(options);
        return EnqueueAsync(
            async cancellation =>
            {
                var runtimeOptions = options
                    .Where(static option => !DownloadOptionPolicy.RequiresEngineRestart(option.Key))
                    .ToDictionary(static option => option.Key, static option => option.Value, StringComparer.Ordinal);
                if (runtimeOptions.Count > 0)
                {
                    _engine.ChangeGlobalOptions(runtimeOptions);
                }

                foreach (var option in options)
                {
                    _configuredGlobalOptions[option.Key] = option.Value;
                }

                RefreshGlobalStatus(refreshOptions: true);
                await PersistAsync(cancellation).ConfigureAwait(false);
                return true;
            },
            cancellationToken).AsVoid();
    }

    public ValueTask ChangeCompletionPolicyAsync(
        Guid id,
        bool notifyOnCompletion,
        DownloadCompletionAction completionAction,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async cancellation =>
            {
                var (group, task) = FindTaskAndGroup(id);
                group.Request = group.Request with
                {
                    NotifyOnCompletion = notifyOnCompletion,
                    CompletionAction = completionAction,
                };
                task.Snapshot = task.Snapshot with
                {
                    NotifyOnCompletion = notifyOnCompletion,
                    CompletionAction = completionAction,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await PersistAndPublishAsync(task, cancellation).ConfigureAwait(false);
                return true;
            },
            cancellationToken).AsVoid();

    public ValueTask ScheduleAsync(
        Guid id,
        DateTimeOffset? startAt,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async cancellation =>
            {
                var (group, _) = FindTaskAndGroup(id);
                if (group.Tasks.All(static task => task.Snapshot.State is
                    DownloadState.Complete or DownloadState.Error or DownloadState.Removed))
                {
                    throw new InvalidOperationException("A finished download cannot be scheduled.");
                }

                var normalized = startAt?.ToUniversalTime();
                if (normalized <= DateTimeOffset.UtcNow)
                {
                    normalized = null;
                }

                var previousSchedule = group.Request.ScheduledStartAt;
                group.Request = group.Request with { ScheduledStartAt = normalized, StartPaused = false };
                foreach (var task in group.Tasks.Where(static task => task.Snapshot.State != DownloadState.Removed))
                {
                    if (normalized is not null && task.Snapshot.State is DownloadState.Active or DownloadState.Waiting)
                    {
                        if (task.AwaitingMetadataPayload)
                        {
                            task.PausePayloadOnFollow = true;
                            task.ResumePayloadOnFollow = false;
                        }
                        else
                        {
                            _engine.Pause(RequireGid(task), force: false);
                            task.PauseRequested = true;
                            _engine.RunOnce();
                        }

                        task.Snapshot = task.Snapshot with { State = DownloadState.Paused, EngineStatus = 2 };
                    }
                    else if (normalized is null && previousSchedule is not null && task.Snapshot.State == DownloadState.Paused)
                    {
                        if (task.AwaitingMetadataPayload)
                        {
                            task.PausePayloadOnFollow = false;
                            task.ResumePayloadOnFollow = true;
                        }
                        else if (task.PauseRequested)
                        {
                            task.ResumeWhenPaused = true;
                        }
                        else
                        {
                            _engine.Resume(RequireGid(task));
                            task.ResumeRequested = true;
                            _engine.RunOnce();
                        }

                        task.Snapshot = task.Snapshot with { State = DownloadState.Waiting, EngineStatus = 1 };
                    }

                    task.Snapshot = task.Snapshot with
                    {
                        ScheduledStartAt = normalized,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    PublishTask(task.Snapshot);
                }

                await PersistAsync(cancellation).ConfigureAwait(false);
                PublishList();
                return true;
            },
            cancellationToken).AsVoid();

    public ValueTask<int> MoveAsync(
        Guid id,
        int offset,
        DownloadPositionMode mode,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            async cancellation =>
            {
                var task = FindTask(id);
                var position = _engine.ChangePosition(RequireGid(task), offset, (int)mode);
                _taskOrder.Remove(id);
                position = Math.Clamp(position, 0, _taskOrder.Count);
                _taskOrder.Insert(position, id);
                ApplyQueuePositions();
                await PersistAsync(cancellation).ConfigureAwait(false);
                PublishList();
                return position;
            },
            cancellationToken);

    public ValueTask<DownloadLiveDetails> GetLiveDetailsAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var task = FindTask(id);
                var gid = RequireGid(task);
                var peers = _capabilities.ExtendedApi
                    ? _engine.GetPeers(gid).Select(MapPeer).ToList()
                    : [];
                var servers = _capabilities.ExtendedApi
                    ? _engine.GetServers(gid).Select(MapServer).ToList()
                    : [];
                return ValueTask.FromResult(new DownloadLiveDetails { Peers = peers, Servers = servers });
            },
            cancellationToken);

    public ValueTask<IMediaByteStream> OpenMediaStreamAsync(
        Guid id,
        int fileIndex,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var task = FindTask(id);
                var snapshot = task.Snapshot;
                var file = snapshot.Files.SingleOrDefault(candidate => candidate.Index == fileIndex)
                    ?? throw new ArgumentOutOfRangeException(
                        nameof(fileIndex),
                        fileIndex,
                        "The download does not contain that file index.");
                if (!file.IsSelected)
                {
                    throw new InvalidOperationException("The selected file is not part of this download.");
                }

                if (snapshot.State is DownloadState.Error or DownloadState.Removed)
                {
                    throw new InvalidOperationException("A failed or removed download cannot be streamed.");
                }

                if (snapshot.State != DownloadState.Complete &&
                    (!_capabilities.ExtendedApi ||
                     !_capabilities.Features.Contains("runtime-piece-priority", StringComparer.Ordinal)))
                {
                    throw new NotSupportedException(
                        "This libaria2 build does not support runtime piece priority.");
                }

                long torrentOffset = 0;
                foreach (var candidate in snapshot.Files.OrderBy(static candidate => candidate.Index))
                {
                    if (candidate.Index == fileIndex)
                    {
                        break;
                    }

                    torrentOffset = checked(torrentOffset + candidate.Length);
                }

                TrackMediaStreamOpened(id);
                try
                {
                    IMediaByteStream stream = new Aria2MediaByteStream(
                        id,
                        fileIndex,
                        Path.IsPathRooted(file.Path)
                            ? file.Path
                            : Path.GetFullPath(file.Path, snapshot.DestinationDirectory),
                        file.Length,
                        torrentOffset,
                        snapshot.PieceLength > 0 ? snapshot.PieceLength : Math.Max(1, file.Length),
                        GetPublishedTask,
                        PrioritizeMediaRangeAsync,
                        ReleaseMediaStream);
                    return ValueTask.FromResult(stream);
                }
                catch
                {
                    ReleaseMediaStream(id);
                    throw;
                }
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (!IsStarted)
        {
            Interlocked.Exchange(ref _disposed, 1);
            _engine.Dispose();
            _shutdown.Dispose();
            return;
        }

        try
        {
            await EnqueueCoreAsync(
                async cancellation =>
                {
                    // Persist one final authoritative snapshot without publishing UI
                    // events while consumers are already being torn down.
                    RefreshAll(publishEvents: false, refreshGlobalOptions: false);
                    await PersistAsync(cancellation).ConfigureAwait(false);
                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _disposed, 1);
            foreach (var preparation in _preparations.Values)
            {
                preparation.Completion.TrySetCanceled();
            }
            _commands.Writer.TryComplete();
            _shutdown.Cancel();
            if (_worker is not null)
            {
                try
                {
                    await _worker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _shutdown.Dispose();
            Volatile.Write(ref _started, 0);
        }
    }

    private async ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddCoreAsync(
        AddDownloadRequest normalized,
        CancellationToken cancellationToken,
        string? knownInfoHash,
        bool persistAndPublish = true)
    {
        EnsureNotDuplicate(normalized, knownInfoHash);
        Directory.CreateDirectory(normalized.DestinationDirectory);
        normalized = normalized with { AllowDuplicate = false };
        var shouldWaitForSchedule = normalized.ScheduledStartAt > DateTimeOffset.UtcNow;
        var persistentRequest = shouldWaitForSchedule
            ? normalized
            : normalized with { ScheduledStartAt = null };
        var engineRequest = shouldWaitForSchedule
            ? persistentRequest with { StartPaused = true }
            : persistentRequest;
        var awaitingMetadataPayload = RequiresMetadataHandoff(engineRequest);
        var ariaRequest = awaitingMetadataPayload
            ? CreateMetadataHandoffRequest(engineRequest)
            : engineRequest;
        var gids = AddToEngine(ariaRequest);
        if (gids.Count == 0)
        {
            throw new InvalidOperationException("aria2 did not create a download task.");
        }

        RuntimeGroup? group = null;
        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            group = new RuntimeGroup(Guid.NewGuid(), persistentRequest, createdAt);
            foreach (var gid in gids)
            {
                var id = Guid.NewGuid();
                var snapshot = CreateInitialSnapshot(id, gid, engineRequest, createdAt, _taskOrder.Count) with
                {
                    ScheduledStartAt = shouldWaitForSchedule ? normalized.ScheduledStartAt : null,
                };
                group.Tasks.Add(new RuntimeTask(id, gid, snapshot)
                {
                    AwaitingMetadataPayload = awaitingMetadataPayload,
                    PausePayloadOnFollow = awaitingMetadataPayload && engineRequest.StartPaused,
                    ResumePayloadOnFollow = awaitingMetadataPayload && !engineRequest.StartPaused,
                });
                _taskOrder.Add(id);
            }

            _groups.Add(group);
            RefreshGroup(group, publishEvents: false);
            if (engineRequest.StartPaused)
            {
                foreach (var task in group.Tasks)
                {
                    task.Snapshot = task.Snapshot with
                    {
                        State = DownloadState.Paused,
                        EngineStatus = 2,
                        DownloadSpeed = 0,
                        UploadSpeed = 0,
                        EtaSeconds = null,
                    };
                }
            }

            if (persistAndPublish)
            {
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            RollbackAddedGroup(group, gids);
            throw;
        }

        if (persistAndPublish)
        {
            PublishAddedGroup(group);
        }
        return group.Tasks.Select(static task => task.Snapshot).ToArray();
    }

    private async ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddBatchCoreAsync(
        IReadOnlyList<AddDownloadRequest> requests,
        CancellationToken cancellationToken)
    {
        var addedGroups = new List<RuntimeGroup>(requests.Count);
        var added = new List<DownloadTaskSnapshot>();
        try
        {
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupCount = _groups.Count;
                added.AddRange(await AddCoreAsync(
                    request,
                    cancellationToken,
                    knownInfoHash: null,
                    persistAndPublish: false).ConfigureAwait(false));
                addedGroups.AddRange(_groups.Skip(groupCount));
            }

            await PersistAsync(cancellationToken).ConfigureAwait(false);
            foreach (var group in addedGroups)
            {
                foreach (var task in group.Tasks)
                {
                    PublishTask(task.Snapshot);
                }
            }
            PublishList();
            return added;
        }
        catch
        {
            foreach (var group in addedGroups.AsEnumerable().Reverse())
            {
                RollbackAddedGroup(group, group.Tasks.Where(static task => task.EngineGid.HasValue)
                    .Select(static task => task.EngineGid!.Value).ToArray());
            }
            try
            {
                await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The original failure is more actionable. Stores use atomic replacement, so an interrupted
                // write retains either the old document or a document that this best-effort save corrects.
            }
            throw;
        }
    }

    private void PublishAddedGroup(RuntimeGroup group)
    {
        foreach (var task in group.Tasks)
        {
            PublishTask(task.Snapshot);
        }
        PublishList();
    }

    private void RollbackAddedGroup(RuntimeGroup? group, IReadOnlyList<ulong> gids)
    {
        if (group is not null)
        {
            _groups.Remove(group);
            foreach (var task in group.Tasks)
            {
                _taskOrder.Remove(task.Id);
            }
        }

        foreach (var gid in gids)
        {
            try
            {
                _engine.Remove(gid, force: true);
            }
            catch
            {
                try
                {
                    _engine.RemoveResult(gid);
                }
                catch
                {
                    // Preserve the original add failure. The engine session is still isolated from persisted state.
                }
            }
        }
    }

    private RuntimePreparation CreatePreparation(
        Guid id,
        string source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty || _preparations.ContainsKey(id) || _committedPreparations.ContainsKey(id))
        {
            throw new ArgumentException("下载预览标识无效或已经存在。", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(source) || source.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("请输入一个下载地址或选择一个元数据文件。", nameof(source));
        }

        source = source.Trim();
        var previewDirectory = Path.Combine(Path.GetTempPath(), "CanX", "download-preview", id.ToString("N"));
        var kind = DetectSourceKind(source);
        if (kind != DownloadSourceKind.Uri)
        {
            source = Path.GetFullPath(source);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("下载元数据文件不存在。", source);
            }
        }

        var preparation = new RuntimePreparation(id, kind, source, previewDirectory);
        _preparations.Add(id, preparation);
        try
        {
            if (kind == DownloadSourceKind.Torrent)
            {
                var options = CreatePreviewOptions(previewDirectory);
                options["pause"] = "true";
                var gid = _engine.AddTorrent(source, options, -1);
                preparation.PayloadGid = gid;
                preparation.EngineGids.Add(gid);
                // The one-shot pause option can place the reserved group in
                // the paused state immediately. aria2 reports -1 when asked
                // to pause an already paused group, so only issue the explicit
                // request when the option has not taken effect yet.
                if (_engine.GetDownload(gid).Status != 2)
                {
                    _engine.Pause(gid, force: false);
                }

                _engine.RunOnce();
                CompletePreparation(preparation, _engine.GetDownload(gid));
            }
            else if (IsRemoteTorrentSource(source))
            {
                var gid = _engine.AddUri(source, CreatePreviewOptions(previewDirectory), -1);
                preparation.MetadataGid = gid;
                preparation.EngineGids.Add(gid);
            }
            else
            {
                preparation.Completion.TrySetResult(new DownloadPreparation
                {
                    Id = id,
                    Kind = kind,
                    Source = source,
                    DisplayName = GetSourceDisplayName(source),
                });
            }

            return preparation;
        }
        catch
        {
            _preparations.Remove(id);
            CleanupPreparation(preparation, keepPayload: false);
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<DownloadTaskSnapshot>> CommitPreparationCoreAsync(
        CommitDownloadPreparationRequest request,
        CancellationToken cancellationToken)
    {
        if (_committedPreparations.TryGetValue(request.PreparationId, out var committedTasks))
        {
            return committedTasks;
        }

        if (!_preparations.TryGetValue(request.PreparationId, out var preparation))
        {
            throw new InvalidOperationException("下载信息尚未准备完成或已经失效。");
        }

        if (!preparation.Completion.Task.IsCompleted)
        {
            return await CommitPendingMetadataPreparationCoreAsync(
                preparation,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        if (!preparation.Completion.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("下载信息读取失败或已经取消。");
        }

        var prepared = preparation.Completion.Task.Result;
        DownloadRequestValidator.ValidateOptions(request.Options);
        var options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal);
        foreach (var option in BuildFilePreferenceOptions(prepared, request.Files))
        {
            options[option.Key] = option.Value;
        }
        var addRequest = DownloadRequestValidator.ValidateAndNormalize(new AddDownloadRequest
        {
            Kind = prepared.Kind,
            Source = prepared.Source,
            DestinationDirectory = request.DestinationDirectory,
            OutputName = prepared.IsTorrent ? null : request.OutputName,
            StartPaused = request.StartPaused,
            AllowDuplicate = request.AllowDuplicate,
            Options = options,
        });

        if (preparation.PayloadGid is not { } payloadGid)
        {
            var tasks = await AddCoreAsync(addRequest, cancellationToken, prepared.InfoHash).ConfigureAwait(false);
            _preparations.Remove(preparation.Id);
            _committedPreparations.Add(preparation.Id, tasks);
            return tasks;
        }

        EnsureNotDuplicate(addRequest, prepared.InfoHash);
        addRequest = addRequest with { AllowDuplicate = false };
        Directory.CreateDirectory(addRequest.DestinationDirectory);
        _engine.ChangeOptions(payloadGid, BuildEngineOptions(addRequest));
        var preparedDetails = _engine.GetDownload(payloadGid);
        if (!addRequest.StartPaused && preparedDetails.Status == 2)
        {
            _engine.Resume(payloadGid);
        }

        var createdAt = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid();
        var details = _engine.GetDownload(payloadGid);
        var snapshot = MapSnapshot(
            CreateInitialSnapshot(taskId, payloadGid, addRequest, createdAt, _taskOrder.Count),
            addRequest,
            details) with
        {
            State = addRequest.StartPaused ? DownloadState.Paused : DownloadState.Waiting,
            EngineStatus = addRequest.StartPaused ? 2 : 1,
        };
        var group = new RuntimeGroup(Guid.NewGuid(), addRequest, createdAt);
        group.Tasks.Add(new RuntimeTask(taskId, payloadGid, snapshot));
        _groups.Add(group);
        _taskOrder.Add(taskId);
        _preparations.Remove(preparation.Id);
        CleanupPreparation(preparation, keepPayload: true);

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        PublishTask(snapshot);
        PublishList();
        IReadOnlyList<DownloadTaskSnapshot> result = [snapshot];
        _committedPreparations.Add(preparation.Id, result);
        return result;
    }

    private async ValueTask<IReadOnlyList<DownloadTaskSnapshot>> CommitPendingMetadataPreparationCoreAsync(
        RuntimePreparation preparation,
        CommitDownloadPreparationRequest request,
        CancellationToken cancellationToken)
    {
        if (preparation.MetadataGid is null || preparation.PayloadGid is not null)
        {
            throw new InvalidOperationException("只有正在获取元数据的 Magnet 或远程 Torrent 可以直接添加。");
        }

        if (request.Files.Count > 0)
        {
            throw new InvalidOperationException("元数据尚未完成时不能指定文件选择，将默认下载全部文件。");
        }

        DownloadRequestValidator.ValidateOptions(request.Options);
        var addRequest = DownloadRequestValidator.ValidateAndNormalize(new AddDownloadRequest
        {
            Kind = preparation.Kind,
            Source = preparation.Source,
            DestinationDirectory = request.DestinationDirectory,
            OutputName = request.OutputName,
            StartPaused = request.StartPaused,
            AllowDuplicate = request.AllowDuplicate,
            Options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal),
        });

        EnsureNotDuplicate(addRequest, knownInfoHash: null);

        var metadataGid = preparation.MetadataGid.Value;
        var createdAt = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid();
        var details = _engine.GetDownload(metadataGid);
        var snapshot = MapSnapshot(
            CreateInitialSnapshot(taskId, metadataGid, addRequest, createdAt, _taskOrder.Count),
            addRequest,
            details);
        snapshot = WithPendingState(
            snapshot,
            addRequest.StartPaused ? DownloadState.Paused : DownloadState.Waiting,
            addRequest.StartPaused ? 2 : 1);

        var task = new RuntimeTask(taskId, metadataGid, snapshot)
        {
            AwaitingMetadataPayload = true,
            ApplyOptionsOnMetadataFollow = true,
            PausePayloadOnFollow = addRequest.StartPaused,
            ResumePayloadOnFollow = !addRequest.StartPaused,
        };
        var group = new RuntimeGroup(Guid.NewGuid(), addRequest with { AllowDuplicate = false }, createdAt);
        group.Tasks.Add(task);

        _preparations.Remove(preparation.Id);
        preparation.Completion.TrySetCanceled();
        _groups.Add(group);
        _taskOrder.Add(taskId);

        // Keep the already running metadata transfer instead of removing and
        // immediately adding the same Magnet again. aria2 processes removal
        // asynchronously, so the old sequence could make the durable task
        // collide with its preview and fail as a duplicate [METADATA] task.
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        PublishTask(snapshot);
        PublishList();
        IReadOnlyList<DownloadTaskSnapshot> result = [snapshot];
        _committedPreparations.Add(preparation.Id, result);
        return result;
    }

    private static Dictionary<string, string> BuildFilePreferenceOptions(
        DownloadPreparation preparation,
        IReadOnlyList<DownloadFilePreference> preferences)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!preparation.IsTorrent || preparation.Files.Count == 0)
        {
            return options;
        }

        var expectedIndexes = preparation.Files.Select(static file => file.Index).ToHashSet();
        var effective = preferences.Count == 0
            ? preparation.Files.Select(static file => new DownloadFilePreference { Index = file.Index }).ToArray()
            : preferences.ToArray();
        if (effective.Select(static item => item.Index).Distinct().Count() != effective.Length ||
            effective.Any(item => !expectedIndexes.Contains(item.Index)))
        {
            throw new ArgumentException("文件选择包含无效或重复的索引。", nameof(preferences));
        }

        var selected = effective.Where(static item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("请至少选择一个要下载的文件。");
        }

        options["select-file"] = string.Join(',', selected.Select(static item => item.Index));
        var high = selected.Where(static item => item.Priority == DownloadFilePriority.High).ToArray();
        var low = selected.Where(static item => item.Priority == DownloadFilePriority.Low).ToArray();
        if (high.Length > 0)
        {
            options["bt-prioritize-file"] = string.Join(',', high.Select(static item => item.Index));
        }
        if (low.Length > 0)
        {
            options["bt-deprioritize-file"] = string.Join(',', low.Select(static item => item.Index));
        }

        return options;
    }

    private static DownloadSourceKind DetectSourceKind(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            !uri.IsFile)
        {
            return DownloadSourceKind.Uri;
        }

        return Path.GetExtension(source).ToLowerInvariant() switch
        {
            ".torrent" => DownloadSourceKind.Torrent,
            ".meta4" or ".metalink" => DownloadSourceKind.Metalink,
            _ => throw new ArgumentException("仅支持下载地址、Magnet、.torrent 和 .meta4/.metalink 文件。", nameof(source)),
        };
    }

    private static bool IsRemoteTorrentSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return false;
        }

        return uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(uri.AbsolutePath).Equals(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> CreatePreviewOptions(string previewDirectory) =>
        new(StringComparer.Ordinal)
        {
            ["dir"] = previewDirectory,
            ["follow-torrent"] = "mem",
            ["pause-metadata"] = "true",
            ["bt-save-metadata"] = "false",
            ["bt-metadata-only"] = "false",
            ["file-allocation"] = "none",
        };

    private static string GetSourceDisplayName(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? uri.Host : name;
        }

        return Path.GetFileName(source);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var refreshTimer = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_commands.Reader.TryRead(out var command))
                {
                    await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                }

                // Housekeeping failures (a task aria2 rejects, a snapshot that cannot be mapped) are
                // recoverable: skip this round instead of shutting down every download. Only failures of
                // the engine event loop itself stop the worker.
                bool scheduledStarted;
                try
                {
                    scheduledStarted = StartDueScheduledTasks();
                }
                catch (Exception exception) when (IsRecoverableWorkerFailure(exception))
                {
                    ReportWorkerFault(exception);
                    scheduledStarted = true;
                }
                if (scheduledStarted)
                {
                    await PersistAsync(cancellationToken).ConfigureAwait(false);
                    PublishList();
                }

                var hasWork = _engine.RunOnce();
                if (refreshTimer.Elapsed >= _options.PollInterval)
                {
                    RunHousekeepingStep(RefreshPreparations);
                    RunHousekeepingStep(CleanupRemovedPreviews);
                    RunHousekeepingStep(() => RefreshAll(publishEvents: true, refreshGlobalOptions: false));
                    refreshTimer.Restart();
                }

                if (!hasWork)
                {
                    await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _workerFailure = exception;
            foreach (var preparation in _preparations.Values)
            {
                preparation.Completion.TrySetException(exception);
            }
            _commands.Writer.TryComplete(exception);
            while (_commands.Reader.TryRead(out var command))
            {
                command.Fail(exception);
            }
        }
        finally
        {
            try
            {
                // Application shutdown must not wait for slow protocol cleanup. In
                // particular, aria2's graceful BitTorrent shutdown may contact
                // trackers while a Magnet task is still fetching its metadata.
                // The session has already been persisted by DisposeAsync, so use
                // aria2's immediate shutdown path here.
                _engine.Shutdown(force: true);
            }
            finally
            {
                _engine.Dispose();
            }
        }
    }

    private void RunHousekeepingStep(Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception) when (IsRecoverableWorkerFailure(exception))
        {
            ReportWorkerFault(exception);
        }
    }

    private static bool IsRecoverableWorkerFailure(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException or ObjectDisposedException);

    private void ReportWorkerFault(Exception exception) => _options.WorkerFaultHandler?.Invoke(exception);

    private IReadOnlyList<ulong> AddToEngine(AddDownloadRequest request)
    {
        var options = BuildEngineOptions(request);
        var gids = request.Kind switch
        {
            DownloadSourceKind.Uri =>
                [_engine.AddUris([request.Source, .. request.MirrorUris], options, request.Position)],
            DownloadSourceKind.Torrent => [_engine.AddTorrent(request.Source, options, request.Position)],
            DownloadSourceKind.Metalink => _engine.AddMetalink(request.Source, options, request.Position),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown download source kind."),
        };
        if (request.StartPaused)
        {
            foreach (var gid in gids)
            {
                _engine.Pause(gid, force: false);
            }

            _engine.RunOnce();
        }

        return gids;
    }

    private void RefreshPreparations()
    {
        foreach (var preparation in _preparations.Values
                     .Where(static item => !item.Completion.Task.IsCompleted && item.MetadataGid is not null)
                     .ToArray())
        {
            try
            {
                var metadata = _engine.GetDownload(preparation.MetadataGid!.Value);
                if (metadata.Status == 4)
                {
                    throw new InvalidOperationException(
                        $"无法获取 Torrent 元数据，aria2 错误码：{metadata.ErrorCode}。");
                }

                if (metadata.FollowedBy.Count != 1 ||
                    !ulong.TryParse(
                        metadata.FollowedBy[0],
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var payloadGid))
                {
                    continue;
                }

                var payload = _engine.GetDownload(payloadGid);
                preparation.PayloadGid = payloadGid;
                preparation.EngineGids.Add(payloadGid);
                CompletePreparation(preparation, payload);
            }
            catch (Aria2Exception exception) when (exception.ErrorCode == -1003)
            {
                // The metadata result may reference its paused child one event-loop
                // iteration before that child becomes visible through libaria2.
            }
            catch (Exception exception)
            {
                preparation.Completion.TrySetException(exception);
            }
        }
    }

    private static void CompletePreparation(RuntimePreparation preparation, Aria2DownloadDetails details)
    {
        var files = details.Files.Select(file => new DownloadPreparationFile
        {
            Index = file.Index,
            Path = MakePreviewPathRelative(details.Dir, file.Path),
            Length = file.Length,
        }).ToList();
        var totalLength = details.TotalLength > 0
            ? details.TotalLength
            : files.Sum(static file => file.Length);
        preparation.Completion.TrySetResult(new DownloadPreparation
        {
            Id = preparation.Id,
            Kind = preparation.Kind,
            Source = preparation.Source,
            DisplayName = !string.IsNullOrWhiteSpace(details.Torrent.Name)
                ? details.Torrent.Name
                : GetSourceDisplayName(preparation.Source),
            TotalLength = totalLength,
            IsTorrent = !string.IsNullOrWhiteSpace(details.InfoHash),
            InfoHash = details.InfoHash,
            Files = files,
        });
    }

    private static string MakePreviewPathRelative(string directory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(path))
        {
            return path;
        }

        var relative = Path.GetRelativePath(directory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(path) : relative;
    }

    private void CleanupPreparation(RuntimePreparation preparation, bool keepPayload)
    {
        foreach (var gid in preparation.EngineGids.Where(gid => !keepPayload || gid != preparation.PayloadGid))
        {
            try
            {
                var details = _engine.GetDownload(gid);
                if (details.Status is 3 or 4 or 5)
                {
                    _engine.RemoveResult(gid);
                }
                else
                {
                    _engine.Remove(gid, force: true);
                    _previewCleanupGids.Add(gid);
                }
            }
            catch (Aria2Exception exception) when (exception.ErrorCode == -1003)
            {
            }
        }
    }

    private void CleanupRemovedPreviews()
    {
        foreach (var gid in _previewCleanupGids.ToArray())
        {
            try
            {
                var details = _engine.GetDownload(gid);
                if (details.Status is not (3 or 4 or 5))
                {
                    continue;
                }

                _engine.RemoveResult(gid);
                _previewCleanupGids.Remove(gid);
            }
            catch (Aria2Exception exception) when (exception.ErrorCode == -1003)
            {
                _previewCleanupGids.Remove(gid);
            }
        }
    }

    private static Dictionary<string, string> BuildEngineOptions(AddDownloadRequest request)
    {
        var options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal)
        {
            ["dir"] = request.DestinationDirectory,
            ["continue"] = "true",
        };
        if (request.OutputName is not null)
        {
            options["out"] = request.OutputName;
        }

        return options;
    }

    private static bool RequiresMetadataHandoff(AddDownloadRequest request) =>
        request.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
        IsRemoteTorrentSource(request.Source);

    private static AddDownloadRequest CreateMetadataHandoffRequest(AddDownloadRequest request)
    {
        var options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal)
        {
            // CanX tracks the payload child itself. Override incompatible
            // global/per-task aria2 values so a Magnet cannot stop after the
            // temporary [METADATA] transfer.
            ["follow-torrent"] = "mem",
            ["pause-metadata"] = "true",
            ["bt-save-metadata"] = "false",
            ["bt-metadata-only"] = "false",
        };
        return request with { StartPaused = false, Options = options };
    }

    private static bool HasPayloadSnapshot(DownloadTaskSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.InfoHash) ||
        snapshot.Torrent is not null ||
        snapshot.Files.Any(static file =>
            !string.IsNullOrWhiteSpace(file.Path) &&
            !string.Equals(Path.GetFileName(file.Path), "[METADATA]", StringComparison.OrdinalIgnoreCase));

    private void Restore(DownloadSessionDocument document)
    {
        foreach (var storedGroup in document.Groups)
        {
            // Finished groups never return to the engine, so their original source (for example a temporary
            // .torrent file) is not revalidated. Each active group is restored independently: one unusable
            // group is surfaced as a failed task instead of preventing the whole download center from starting.
            if (storedGroup.Tasks.All(static task => task.State is DownloadState.Complete or DownloadState.Error or DownloadState.Removed))
            {
                RestoreInactive(storedGroup, storedGroup.Request, errorMessage: null);
                continue;
            }

            var engineRequestAdded = false;
            try
            {
                RestoreActive(storedGroup, ref engineRequestAdded);
            }
            catch (Exception exception) when (!engineRequestAdded && exception is not OutOfMemoryException)
            {
                _groups.RemoveAll(group => group.Id == storedGroup.GroupId);
                RestoreInactive(storedGroup, storedGroup.Request, $"无法恢复下载任务：{exception.Message}");
            }
        }

        _taskOrder.Sort((left, right) => FindTask(left).Snapshot.QueuePosition.CompareTo(FindTask(right).Snapshot.QueuePosition));
        ApplyQueuePositions();
    }

    private void RestoreInactive(DownloadSessionGroup storedGroup, AddDownloadRequest request, string? errorMessage)
    {
        var runtime = new RuntimeGroup(storedGroup.GroupId, request, storedGroup.CreatedAt);
        _groups.Add(runtime);
        foreach (var stored in storedGroup.Tasks)
        {
            var snapshot = errorMessage is not null && stored.State is not (DownloadState.Complete or DownloadState.Removed)
                ? stored with
                {
                    State = DownloadState.Error,
                    ErrorMessage = errorMessage,
                    DownloadSpeed = 0,
                    UploadSpeed = 0,
                    EtaSeconds = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
                : stored;
            runtime.Tasks.Add(new RuntimeTask(snapshot.Id, null, snapshot));
            if (snapshot.State != DownloadState.Removed)
            {
                _taskOrder.Add(snapshot.Id);
            }
        }
    }

    private void RestoreActive(DownloadSessionGroup storedGroup, ref bool engineRequestAdded)
    {
        var request = DownloadRequestValidator.ValidateAndNormalize(storedGroup.Request);
        var runtime = new RuntimeGroup(storedGroup.GroupId, request, storedGroup.CreatedAt);
        _groups.Add(runtime);

        var shouldPause = request.ScheduledStartAt > DateTimeOffset.UtcNow || storedGroup.Tasks
            .Where(static task => task.State != DownloadState.Removed)
            .All(static task => task.State == DownloadState.Paused);
        var awaitingMetadataPayload = RequiresMetadataHandoff(request);
        var restoreRequest = request with { StartPaused = shouldPause };
        var engineRequest = awaitingMetadataPayload
            ? CreateMetadataHandoffRequest(restoreRequest)
            : restoreRequest;
        var gids = AddToEngine(engineRequest);
        engineRequestAdded = true;
        for (var index = 0; index < gids.Count; index++)
        {
            var existing = index < storedGroup.Tasks.Count ? storedGroup.Tasks[index] : null;
            var id = existing?.Id ?? Guid.NewGuid();
            var snapshot = existing is null
                ? CreateInitialSnapshot(id, gids[index], request, storedGroup.CreatedAt, _taskOrder.Count)
                : existing with { EngineGid = gids[index].ToString("x16"), UpdatedAt = DateTimeOffset.UtcNow };
            if (awaitingMetadataPayload)
            {
                snapshot = snapshot with
                {
                    State = shouldPause ? DownloadState.Paused : DownloadState.Waiting,
                    EngineStatus = shouldPause ? 2 : 1,
                    DownloadSpeed = 0,
                    UploadSpeed = 0,
                    EtaSeconds = null,
                };
            }

            var task = new RuntimeTask(id, gids[index], snapshot)
            {
                AwaitingMetadataPayload = awaitingMetadataPayload,
                PausePayloadOnFollow = awaitingMetadataPayload && shouldPause,
                ResumePayloadOnFollow = awaitingMetadataPayload && !shouldPause,
                PreserveSnapshotUntilMetadataFollow = awaitingMetadataPayload &&
                    existing is not null && HasPayloadSnapshot(existing),
            };
            runtime.Tasks.Add(task);

            if (snapshot.State == DownloadState.Removed)
            {
                _engine.Remove(gids[index], force: true);
                task.EngineGid = null;
            }
            else
            {
                _taskOrder.Add(id);
            }
        }
    }

    private void RefreshAll(bool publishEvents, bool refreshGlobalOptions)
    {
        var tasksChanged = false;
        foreach (var group in _groups)
        {
            tasksChanged |= RefreshGroup(group, publishEvents);
        }

        RefreshGlobalStatus(refreshGlobalOptions, publishEvents);
        if (tasksChanged && publishEvents)
        {
            PublishList();
        }
    }

    private bool RefreshGroup(RuntimeGroup group, bool publishEvents)
    {
        var groupChanged = false;
        foreach (var task in group.Tasks)
        {
            if (task.EngineGid is not { } gid)
            {
                continue;
            }

            try
            {
                var details = _engine.GetDownload(gid);
                details = FollowSingleMetadataChild(group.Request, task, gid, details);
                if (task.PreserveSnapshotUntilMetadataFollow && task.EngineGid == gid)
                {
                    continue;
                }

                details = CompletePendingControlTransitions(task, details);
                var updated = MapSnapshot(task.Snapshot, group.Request, details);
                if ((task.AwaitingMetadataPayload && task.PausePayloadOnFollow) ||
                    (task.PauseRequested && !task.ResumeWhenPaused))
                {
                    updated = WithPendingState(updated, DownloadState.Paused, 2);
                }
                else if (task.ResumeWhenPaused || task.ResumeRequested)
                {
                    updated = WithPendingState(updated, DownloadState.Waiting, 1);
                }

                var changed = HasMeaningfulChange(task.Snapshot, updated);
                if (changed)
                {
                    task.Snapshot = updated;
                    groupChanged = true;
                    if (publishEvents)
                    {
                        PublishTask(updated);
                    }
                }
            }
            catch (Aria2Exception exception) when (exception.ErrorCode == -1003)
            {
                task.EngineGid = null;
            }
        }

        return groupChanged;
    }

    private Aria2DownloadDetails FollowSingleMetadataChild(
        AddDownloadRequest request,
        RuntimeTask task,
        ulong currentGid,
        Aria2DownloadDetails details)
    {
        if (details.FollowedBy.Count != 1 ||
            (!request.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
             !IsRemoteTorrentSource(request.Source)))
        {
            return details;
        }

        foreach (var followedBy in details.FollowedBy)
        {
            if (!ulong.TryParse(followedBy, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var followedGid) ||
                followedGid == 0 ||
                followedGid == currentGid)
            {
                continue;
            }

            try
            {
                var followedDetails = _engine.GetDownload(followedGid);
                if (task.ApplyOptionsOnMetadataFollow)
                {
                    _engine.ChangeOptions(followedGid, BuildEngineOptions(request));
                    followedDetails = _engine.GetDownload(followedGid);
                    task.ApplyOptionsOnMetadataFollow = false;
                }

                task.EngineGid = followedGid;
                task.AwaitingMetadataPayload = false;
                task.PreserveSnapshotUntilMetadataFollow = false;
                if (task.PausePayloadOnFollow && followedDetails.Status is not (2 or 3 or 4 or 5))
                {
                    _engine.Pause(followedGid, force: true);
                    task.PauseRequested = true;
                    _engine.RunOnce();
                    followedDetails = _engine.GetDownload(followedGid);
                }
                else if (task.ResumePayloadOnFollow && followedDetails.Status == 2)
                {
                    _engine.Resume(followedGid);
                    task.ResumeRequested = true;
                    _engine.RunOnce();
                    followedDetails = _engine.GetDownload(followedGid);
                }

                task.PausePayloadOnFollow = false;
                task.ResumePayloadOnFollow = false;
                return followedDetails;
            }
            catch (Aria2Exception exception) when (exception.ErrorCode == -1003)
            {
                // aria2 may publish followedBy one iteration before the child
                // handle becomes queryable. Keep the metadata handle for this
                // refresh and retry on the next one.
            }
        }

        return details;
    }

    private Aria2DownloadDetails CompletePendingControlTransitions(
        RuntimeTask task,
        Aria2DownloadDetails details)
    {
        if (task.PauseRequested)
        {
            if (details.Status is 3 or 4 or 5)
            {
                task.PauseRequested = false;
                task.ResumeWhenPaused = false;
            }
            else if (details.Status == 2)
            {
                task.PauseRequested = false;
                if (task.ResumeWhenPaused)
                {
                    task.ResumeWhenPaused = false;
                    _engine.Resume(RequireGid(task));
                    task.ResumeRequested = true;
                    _engine.RunOnce();
                    details = _engine.GetDownload(RequireGid(task));
                }
            }
        }

        if (task.ResumeRequested && details.Status != 2)
        {
            task.ResumeRequested = false;
        }

        return details;
    }

    private static DownloadTaskSnapshot WithPendingState(
        DownloadTaskSnapshot snapshot,
        DownloadState state,
        int engineStatus) => snapshot with
        {
            State = state,
            EngineStatus = engineStatus,
            DownloadSpeed = 0,
            UploadSpeed = 0,
            EtaSeconds = null,
        };

    private bool RefreshGlobalStatus(bool refreshOptions = false, bool publishEvent = true)
    {
        var native = _engine.GetGlobalStatus();
        var options = _globalStatus.Options;
        if (refreshOptions)
        {
            var refreshedOptions = new Dictionary<string, string>(_engine.GetGlobalOptions(), StringComparer.Ordinal);
            foreach (var option in _configuredGlobalOptions.Where(static option =>
                         DownloadOptionPolicy.RequiresEngineRestart(option.Key)))
            {
                refreshedOptions[option.Key] = option.Value;
            }

            options = refreshedOptions;
        }

        var snapshot = new DownloadGlobalSnapshot
        {
            DownloadSpeed = native.DownloadSpeed,
            UploadSpeed = native.UploadSpeed,
            ActiveCount = native.ActiveCount,
            WaitingCount = native.WaitingCount,
            StoppedCount = native.StoppedCount,
            Options = options,
        };
        if (!HasMeaningfulChange(_globalStatus, snapshot))
        {
            return false;
        }

        Volatile.Write(ref _globalStatus, snapshot);
        if (publishEvent)
        {
            _eventBus.Publish(new DownloadGlobalStatusChanged(snapshot));
        }

        return true;
    }

    private async ValueTask PersistAndPublishAsync(RuntimeTask task, CancellationToken cancellationToken)
    {
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        PublishTask(task.Snapshot);
        PublishList();
    }

    private ValueTask PersistAsync(CancellationToken cancellationToken)
    {
        var document = new DownloadSessionDocument
        {
            GlobalOptions = new Dictionary<string, string>(_configuredGlobalOptions, StringComparer.Ordinal),
            Groups = _groups.Select(static group => new DownloadSessionGroup
            {
                GroupId = group.Id,
                Request = group.Request,
                CreatedAt = group.CreatedAt,
                Tasks = group.Tasks.Select(static task => task.Snapshot).ToList(),
            }).ToList(),
        };
        return _store.SaveAsync(document, cancellationToken);
    }

    private void PublishTask(DownloadTaskSnapshot snapshot) => _eventBus.Publish(new DownloadTaskChanged(snapshot));

    private void PublishList()
    {
        var byId = _groups.SelectMany(static group => group.Tasks).ToDictionary(static task => task.Id);
        var snapshots = _taskOrder
            .Where(byId.ContainsKey)
            .Select(id => byId[id].Snapshot)
            .Where(static snapshot => snapshot.State != DownloadState.Removed)
            .ToArray();
        Volatile.Write(ref _publishedTasks, snapshots);
        _eventBus.Publish(new DownloadListChanged(new ReadOnlyCollection<DownloadTaskSnapshot>(snapshots)));
    }

    private void ApplyQueuePositions()
    {
        for (var index = 0; index < _taskOrder.Count; index++)
        {
            var task = FindTask(_taskOrder[index]);
            task.Snapshot = task.Snapshot with { QueuePosition = index };
        }
    }

    private RuntimeTask FindTask(Guid id) => FindTaskAndGroup(id).Task;

    private DownloadTaskSnapshot? GetPublishedTask(Guid id) =>
        Volatile.Read(ref _publishedTasks).FirstOrDefault(task => task.Id == id);

    private ValueTask<bool> PrioritizeMediaRangeAsync(
        Guid id,
        int firstPiece,
        int lastPiece,
        CancellationToken cancellationToken) =>
        EnqueueAsync(
            cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var task = FindTask(id);
                if (task.Snapshot.State == DownloadState.Complete)
                {
                    return ValueTask.FromResult(true);
                }

                if (task.Snapshot.State is DownloadState.Error or DownloadState.Removed)
                {
                    throw new IOException("The download stopped before the requested media range was available.");
                }

                if (task.EngineGid is not { } gid || task.Snapshot.State != DownloadState.Active)
                {
                    return ValueTask.FromResult(false);
                }

                _engine.PrioritizePieceRange(gid, firstPiece, lastPiece);
                return ValueTask.FromResult(true);
            },
            cancellationToken);

    private void TrackMediaStreamOpened(Guid id)
    {
        lock (_mediaStreamSync)
        {
            _openMediaStreams.TryGetValue(id, out var count);
            _openMediaStreams[id] = checked(count + 1);
        }
    }

    private void ReleaseMediaStream(Guid id)
    {
        var clearPriority = false;
        lock (_mediaStreamSync)
        {
            if (!_openMediaStreams.TryGetValue(id, out var count))
            {
                return;
            }

            if (count > 1)
            {
                _openMediaStreams[id] = count - 1;
            }
            else
            {
                _openMediaStreams.Remove(id);
                clearPriority = true;
            }
        }

        if (clearPriority && IsStarted && Volatile.Read(ref _disposed) == 0)
        {
            _ = ClearMediaPriorityAfterReleaseAsync(id);
        }
    }

    private async Task ClearMediaPriorityAfterReleaseAsync(Guid id)
    {
        try
        {
            await EnqueueAsync(
                cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    lock (_mediaStreamSync)
                    {
                        if (_openMediaStreams.ContainsKey(id))
                        {
                            return ValueTask.FromResult(false);
                        }
                    }

                    var task = FindTask(id);
                    if (task.EngineGid is not { } gid ||
                        task.Snapshot.State is DownloadState.Complete or DownloadState.Error or DownloadState.Removed)
                    {
                        return ValueTask.FromResult(false);
                    }

                    _engine.ClearPiecePriority(gid);
                    return ValueTask.FromResult(true);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Priority cleanup is best-effort; playback teardown must not fail if the task
            // concurrently completes, pauses, is removed, or the engine starts shutting down.
        }
    }

    private (RuntimeGroup Group, RuntimeTask Task) FindTaskAndGroup(Guid id)
    {
        foreach (var group in _groups)
        {
            var task = group.Tasks.Find(item => item.Id == id);
            if (task is not null)
            {
                return (group, task);
            }
        }

        throw new KeyNotFoundException($"Download task '{id}' was not found.");
    }

    private static ulong RequireGid(RuntimeTask task) =>
        task.EngineGid ?? throw new InvalidOperationException("The download does not have an active aria2 task.");

    private static DownloadTaskSnapshot CreateInitialSnapshot(
        Guid id,
        ulong gid,
        AddDownloadRequest request,
        DateTimeOffset createdAt,
        int queuePosition) =>
        new()
        {
            Id = id,
            EngineGid = gid.ToString("x16"),
            Kind = request.Kind,
            Source = request.Source,
            MirrorUris = request.MirrorUris,
            DestinationDirectory = request.DestinationDirectory,
            OutputName = request.OutputName,
            DisplayName = GetFallbackName(request),
            State = request.StartPaused ? DownloadState.Paused : DownloadState.Waiting,
            EngineStatus = request.StartPaused ? 2 : 1,
            QueuePosition = queuePosition,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal),
            NotifyOnCompletion = request.NotifyOnCompletion,
            CompletionAction = request.CompletionAction,
            ScheduledStartAt = request.ScheduledStartAt,
        };

    private static DownloadTaskSnapshot MapSnapshot(
        DownloadTaskSnapshot current,
        AddDownloadRequest request,
        Aria2DownloadDetails details)
    {
        var files = details.Files.Select(static file => new DownloadFileSnapshot
        {
            Index = file.Index,
            Path = file.Path,
            Length = file.Length,
            CompletedLength = file.CompletedLength,
            IsSelected = file.Selected,
            Uris = file.Uris.Select(static uri => new DownloadUriSnapshot
            {
                Uri = uri.Uri,
                EngineStatus = uri.Status,
                IsUsed = uri.Status == 0,
            }).ToList(),
        }).ToList();
        var torrent = string.IsNullOrEmpty(details.InfoHash) && string.IsNullOrEmpty(details.Torrent.Name)
            ? null
            : new TorrentSnapshot
            {
                Name = details.Torrent.Name,
                Comment = details.Torrent.Comment,
                CreationDate = details.Torrent.CreationDate > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(details.Torrent.CreationDate)
                    : null,
                Mode = details.Torrent.Mode switch { 1 => "Single", 2 => "Multi", _ => "None" },
                AnnounceList = details.Torrent.AnnounceList,
            };
        var mergedOptions = new Dictionary<string, string>(details.Options, StringComparer.Ordinal);
        foreach (var option in request.Options)
        {
            mergedOptions.TryAdd(option.Key, option.Value);
        }

        return current with
        {
            EngineGid = details.Gid,
            DisplayName = GetDisplayName(request, details),
            State = MapState(details),
            EngineStatus = details.Status,
            TotalLength = details.TotalLength,
            CompletedLength = details.CompletedLength,
            UploadedLength = details.UploadedLength,
            DownloadSpeed = details.DownloadSpeed,
            UploadSpeed = details.UploadSpeed,
            EtaSeconds = CalculateEtaSeconds(details),
            PieceLength = details.PieceLength,
            PieceCount = details.NumPieces,
            Connections = details.Connections,
            IsSeeder = details.Seeder,
            SeederCount = details.NumSeeders,
            ErrorCode = details.ErrorCode,
            ErrorMessage = DownloadErrorMessages.GetMessage(details.ErrorCode),
            InfoHash = details.InfoHash,
            Bitfield = details.Bitfield,
            FollowedBy = details.FollowedBy,
            Following = details.Following,
            BelongsTo = details.BelongsTo,
            UpdatedAt = DateTimeOffset.UtcNow,
            Options = mergedOptions,
            Files = files,
            Torrent = torrent,
            NotifyOnCompletion = request.NotifyOnCompletion,
            CompletionAction = request.CompletionAction,
            ScheduledStartAt = request.ScheduledStartAt,
        };
    }

    private bool StartDueScheduledTasks()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var group in _groups.Where(group => group.Request.ScheduledStartAt <= now))
        {
            group.Request = group.Request with { ScheduledStartAt = null };
            foreach (var task in group.Tasks.Where(static task => task.Snapshot.State == DownloadState.Paused))
            {
                if (task.AwaitingMetadataPayload)
                {
                    task.PausePayloadOnFollow = false;
                    task.ResumePayloadOnFollow = true;
                }
                else if (task.PauseRequested)
                {
                    task.ResumeWhenPaused = true;
                }
                else
                {
                    _engine.Resume(RequireGid(task));
                    task.ResumeRequested = true;
                    _engine.RunOnce();
                }

                task.Snapshot = task.Snapshot with
                {
                    State = DownloadState.Waiting,
                    EngineStatus = 1,
                    ScheduledStartAt = null,
                    UpdatedAt = now,
                };
                PublishTask(task.Snapshot);
            }

            changed = true;
        }

        return changed;
    }

    private static long? CalculateEtaSeconds(Aria2DownloadDetails details)
    {
        var remaining = Math.Max(0, details.TotalLength - details.CompletedLength);
        if (remaining == 0)
        {
            return details.TotalLength > 0 ? 0 : null;
        }

        return details.DownloadSpeed > 0
            ? (long)Math.Ceiling((double)remaining / details.DownloadSpeed)
            : null;
    }

    private static DownloadCapabilityManifest MapCapabilities(Aria2CapabilityDetails details) => new()
    {
        AbiVersion = details.AbiVersion,
        ExtendedApi = details.ExtendedApi,
        Features = details.Features,
        Options = details.Options.Select(static option => new DownloadOptionDefinition
        {
            Name = option.Name,
            Description = option.Description,
            DefaultValue = option.DefaultValue,
            PossibleValues = option.PossibleValues,
            ShortName = option.ShortName,
            ArgumentType = option.ArgumentType,
            Hidden = option.Hidden,
            Initial = option.Initial,
            ChangeActive = option.ChangeActive,
            ChangeReserved = option.ChangeReserved,
            ChangeGlobal = option.ChangeGlobal,
            Cumulative = option.Cumulative,
        }).ToList(),
    };

    private static DownloadPeerSnapshot MapPeer(Aria2PeerDetails peer) => new()
    {
        PeerId = peer.PeerId,
        Ip = peer.Ip,
        Port = peer.Port,
        Bitfield = peer.Bitfield,
        DownloadSpeed = peer.DownloadSpeed,
        UploadSpeed = peer.UploadSpeed,
        AmChoking = peer.AmChoking,
        PeerChoking = peer.PeerChoking,
        Seeder = peer.Seeder,
    };

    private static DownloadServerSnapshot MapServer(Aria2ServerDetails server) => new()
    {
        FileIndex = server.FileIndex,
        Uri = server.Uri,
        CurrentUri = server.CurrentUri,
        DownloadSpeed = server.DownloadSpeed,
    };

    private static DownloadState MapState(Aria2DownloadDetails details)
    {
        if (details.Status == 0 && details.Seeder)
        {
            return DownloadState.Complete;
        }

        return details.Status switch
        {
            0 => DownloadState.Active,
            1 => DownloadState.Waiting,
            2 => DownloadState.Paused,
            3 => DownloadState.Complete,
            4 => DownloadState.Error,
            5 => DownloadState.Removed,
            _ => throw new InvalidDataException($"Unknown aria2 download status {details.Status}."),
        };
    }

    private static string GetDisplayName(AddDownloadRequest request, Aria2DownloadDetails details)
    {
        if (!string.IsNullOrWhiteSpace(details.Torrent.Name))
        {
            return details.Torrent.Name;
        }

        if (DownloadIdentity.TryGetMagnetInfoHash(request.Source, out _) &&
            string.IsNullOrWhiteSpace(details.InfoHash))
        {
            return GetFallbackName(request);
        }

        var firstPath = details.Files.FirstOrDefault()?.Path;
        return string.IsNullOrWhiteSpace(firstPath) ? GetFallbackName(request) : Path.GetFileName(firstPath);
    }

    private static string GetFallbackName(AddDownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.OutputName))
        {
            return request.OutputName;
        }

        if (DownloadIdentity.TryGetMagnetInfoHash(request.Source, out var infoHash))
        {
            return $"[METADATA] {infoHash}";
        }

        if (request.Kind != DownloadSourceKind.Uri)
        {
            return Path.GetFileName(request.Source);
        }

        if (Uri.TryCreate(request.Source, UriKind.Absolute, out var uri))
        {
            var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return request.Source;
    }

    private void EnsureNotDuplicate(AddDownloadRequest request, string? knownInfoHash)
    {
        if (request.AllowDuplicate)
        {
            return;
        }

        var infoHash = DownloadIdentity.GetInfoHash(request, knownInfoHash);
        var canonicalSource = DownloadIdentity.GetCanonicalSource(request);
        foreach (var existing in _groups
                     .SelectMany(static group => group.Tasks)
                     .Select(static task => task.Snapshot)
                     .Where(static task => task.State != DownloadState.Removed))
        {
            var existingInfoHash = DownloadIdentity.GetInfoHash(existing);
            if (infoHash is not null && existingInfoHash is not null &&
                string.Equals(infoHash, existingInfoHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new DuplicateDownloadException(existing);
            }

            var existingSource = DownloadIdentity.GetCanonicalSource(existing);
            if (canonicalSource is not null && existingSource is not null &&
                CanonicalSourcesEqual(request.Kind, canonicalSource, existingSource))
            {
                throw new DuplicateDownloadException(existing);
            }
        }
    }

    private static bool CanonicalSourcesEqual(DownloadSourceKind kind, string left, string right)
    {
        var comparison = kind == DownloadSourceKind.Uri || OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(left, right, comparison);
    }

    private static bool HasMeaningfulChange(DownloadGlobalSnapshot current, DownloadGlobalSnapshot updated) =>
        current.DownloadSpeed != updated.DownloadSpeed ||
        current.UploadSpeed != updated.UploadSpeed ||
        current.ActiveCount != updated.ActiveCount ||
        current.WaitingCount != updated.WaitingCount ||
        current.StoppedCount != updated.StoppedCount ||
        !DictionariesEqual(current.Options, updated.Options);

    private static bool HasMeaningfulChange(DownloadTaskSnapshot current, DownloadTaskSnapshot updated) =>
        current.Id != updated.Id ||
        current.EngineGid != updated.EngineGid ||
        current.Kind != updated.Kind ||
        current.Source != updated.Source ||
        !current.MirrorUris.SequenceEqual(updated.MirrorUris, StringComparer.Ordinal) ||
        current.DestinationDirectory != updated.DestinationDirectory ||
        current.OutputName != updated.OutputName ||
        current.DisplayName != updated.DisplayName ||
        current.State != updated.State ||
        current.EngineStatus != updated.EngineStatus ||
        current.TotalLength != updated.TotalLength ||
        current.CompletedLength != updated.CompletedLength ||
        current.UploadedLength != updated.UploadedLength ||
        current.DownloadSpeed != updated.DownloadSpeed ||
        current.UploadSpeed != updated.UploadSpeed ||
        current.EtaSeconds != updated.EtaSeconds ||
        current.PieceLength != updated.PieceLength ||
        current.PieceCount != updated.PieceCount ||
        current.Connections != updated.Connections ||
        current.IsSeeder != updated.IsSeeder ||
        current.SeederCount != updated.SeederCount ||
        current.ErrorCode != updated.ErrorCode ||
        current.ErrorMessage != updated.ErrorMessage ||
        current.InfoHash != updated.InfoHash ||
        current.Bitfield != updated.Bitfield ||
        !current.FollowedBy.SequenceEqual(updated.FollowedBy, StringComparer.Ordinal) ||
        current.Following != updated.Following ||
        current.BelongsTo != updated.BelongsTo ||
        current.QueuePosition != updated.QueuePosition ||
        current.CreatedAt != updated.CreatedAt ||
        !DictionariesEqual(current.Options, updated.Options) ||
        !FilesEqual(current.Files, updated.Files) ||
        !TorrentsEqual(current.Torrent, updated.Torrent) ||
        current.NotifyOnCompletion != updated.NotifyOnCompletion ||
        current.CompletionAction != updated.CompletionAction ||
        current.ScheduledStartAt != updated.ScheduledStartAt;

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> updated) =>
        current.Count == updated.Count && current.All(option =>
            updated.TryGetValue(option.Key, out var value) &&
            string.Equals(option.Value, value, StringComparison.Ordinal));

    private static bool FilesEqual(
        IReadOnlyList<DownloadFileSnapshot> current,
        IReadOnlyList<DownloadFileSnapshot> updated)
    {
        if (current.Count != updated.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = updated[index];
            if (left.Index != right.Index ||
                left.Path != right.Path ||
                left.Length != right.Length ||
                left.CompletedLength != right.CompletedLength ||
                left.IsSelected != right.IsSelected ||
                !left.Uris.SequenceEqual(right.Uris))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TorrentsEqual(TorrentSnapshot? current, TorrentSnapshot? updated)
    {
        if (ReferenceEquals(current, updated))
        {
            return true;
        }

        if (current is null || updated is null ||
            current.Name != updated.Name ||
            current.Comment != updated.Comment ||
            current.CreationDate != updated.CreationDate ||
            current.Mode != updated.Mode ||
            current.AnnounceList.Count != updated.AnnounceList.Count)
        {
            return false;
        }

        for (var index = 0; index < current.AnnounceList.Count; index++)
        {
            if (!current.AnnounceList[index].SequenceEqual(updated.AnnounceList[index], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ResolveTaskFilesForDeletion(DownloadTaskSnapshot snapshot)
    {
        var root = Path.GetFullPath(snapshot.DestinationDirectory);
        var candidates = snapshot.Files.Select(static file => file.Path).ToList();
        if (candidates.Count == 0 && snapshot.OutputName is not null)
        {
            candidates.Add(Path.Combine(root, snapshot.OutputName));
        }

        var paths = new List<string>(candidates.Count * 2);
        foreach (var candidate in candidates)
        {
            var path = Path.IsPathRooted(candidate) ? Path.GetFullPath(candidate) : Path.GetFullPath(candidate, root);
            var relative = Path.GetRelativePath(root, path);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to delete a file outside '{root}'.");
            }

            paths.Add(path);
            paths.Add($"{path}.aria2");
        }

        return paths;
    }

    private ValueTask<TResult> EnqueueAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!IsStarted)
        {
            throw new InvalidOperationException("The download manager has not been started.");
        }

        if (_workerFailure is not null)
        {
            return ValueTask.FromException<TResult>(
                new InvalidOperationException("The download worker has stopped.", _workerFailure));
        }

        return EnqueueCoreAsync(action, cancellationToken);
    }

    private ValueTask<TResult> EnqueueCoreAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        var item = new WorkItem<TResult>(action, cancellationToken);
        if (!_commands.Writer.TryWrite(item))
        {
            return ValueTask.FromException<TResult>(new InvalidOperationException("The download command queue is closed."));
        }

        return new ValueTask<TResult>(item.Completion.Task.WaitAsync(cancellationToken));
    }

    private interface IWorkItem
    {
        ValueTask ExecuteAsync(CancellationToken cancellationToken);

        void Fail(Exception exception);
    }

    private sealed class WorkItem<TResult>(
        Func<CancellationToken, ValueTask<TResult>> action,
        CancellationToken callerCancellationToken) : IWorkItem
    {
        public TaskCompletionSource<TResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken)
        {
            if (callerCancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(callerCancellationToken);
                return;
            }

            try
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    callerCancellationToken);
                Completion.TrySetResult(await action(linkedCancellation.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException exception)
            {
                Completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class RuntimeGroup(Guid id, AddDownloadRequest request, DateTimeOffset createdAt)
    {
        public Guid Id { get; } = id;

        public AddDownloadRequest Request { get; set; } = request;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public List<RuntimeTask> Tasks { get; } = [];
    }

    private sealed class RuntimeTask(Guid id, ulong? engineGid, DownloadTaskSnapshot snapshot)
    {
        public Guid Id { get; } = id;

        public ulong? EngineGid { get; set; } = engineGid;

        public DownloadTaskSnapshot Snapshot { get; set; } = snapshot;

        public bool PauseRequested { get; set; }

        public bool ResumeWhenPaused { get; set; }

        public bool ResumeRequested { get; set; }

        public bool AwaitingMetadataPayload { get; set; }

        public bool ApplyOptionsOnMetadataFollow { get; set; }

        public bool PausePayloadOnFollow { get; set; }

        public bool ResumePayloadOnFollow { get; set; }

        public bool PreserveSnapshotUntilMetadataFollow { get; set; }
    }

    private sealed class RuntimePreparation(
        Guid id,
        DownloadSourceKind kind,
        string source,
        string previewDirectory)
    {
        public Guid Id { get; } = id;

        public DownloadSourceKind Kind { get; } = kind;

        public string Source { get; } = source;

        public string PreviewDirectory { get; } = previewDirectory;

        public ulong? MetadataGid { get; set; }

        public ulong? PayloadGid { get; set; }

        public HashSet<ulong> EngineGids { get; } = [];

        public TaskCompletionSource<DownloadPreparation> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class ValueTaskExtensions
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> valueTask) =>
        _ = await valueTask.ConfigureAwait(false);
}
