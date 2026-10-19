// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Events;

namespace CanX.Core.Downloads;

public sealed record DownloadTaskChanged(DownloadTaskSnapshot Snapshot) : IAppEvent;

public sealed record DownloadListChanged(IReadOnlyList<DownloadTaskSnapshot> Snapshots) : IAppEvent;

public sealed record DownloadGlobalStatusChanged(DownloadGlobalSnapshot Snapshot) : IAppEvent;

public sealed record DownloadCompletionHandled(
    Guid TaskId,
    bool NotificationSent,
    bool ActionExecuted,
    string? ErrorMessage) : IAppEvent;

public interface IDownloadCompletionHandler
{
    ValueTask<(bool NotificationSent, bool ActionExecuted)> HandleAsync(
        DownloadTaskSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
