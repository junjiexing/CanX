// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;
using CanX.Streaming;

namespace CanX.Application.Downloads;

public interface IDownloadManager : IAsyncDisposable
{
    bool IsStarted { get; }

    IReadOnlyList<DownloadTaskSnapshot> Tasks { get; }

    DownloadGlobalSnapshot GlobalStatus { get; }

    DownloadCapabilityManifest Capabilities { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddAsync(
        AddDownloadRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DownloadTaskSnapshot>> AddBatchAsync(
        IReadOnlyList<AddDownloadRequest> requests,
        CancellationToken cancellationToken = default);

    ValueTask<DownloadPreparation> PrepareAsync(
        string source,
        CancellationToken cancellationToken = default,
        Guid? preparationId = null);

    ValueTask<IReadOnlyList<DownloadTaskSnapshot>> CommitPreparationAsync(
        CommitDownloadPreparationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CancelPreparationAsync(
        Guid preparationId,
        CancellationToken cancellationToken = default);

    ValueTask PauseAsync(Guid id, bool force = false, CancellationToken cancellationToken = default);

    ValueTask ResumeAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(
        Guid id,
        bool force = false,
        bool deleteFiles = false,
        CancellationToken cancellationToken = default);

    ValueTask ChangeOptionsAsync(
        Guid id,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default);

    ValueTask ChangeGlobalOptionsAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default);

    ValueTask ChangeCompletionPolicyAsync(
        Guid id,
        bool notifyOnCompletion,
        DownloadCompletionAction completionAction,
        CancellationToken cancellationToken = default);

    ValueTask ScheduleAsync(
        Guid id,
        DateTimeOffset? startAt,
        CancellationToken cancellationToken = default);

    ValueTask<int> MoveAsync(
        Guid id,
        int offset,
        DownloadPositionMode mode,
        CancellationToken cancellationToken = default);

    ValueTask<DownloadLiveDetails> GetLiveDetailsAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<IMediaByteStream> OpenMediaStreamAsync(
        Guid id,
        int fileIndex,
        CancellationToken cancellationToken = default);
}
