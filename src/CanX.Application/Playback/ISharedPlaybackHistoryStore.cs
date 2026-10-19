// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;

namespace CanX.Application.Playback;

/// <summary>Exchanges history deltas between independent playback controllers.</summary>
public interface ISharedPlaybackHistoryStore : IPlaybackStateStore
{
    PlaybackPersistedState SynchronizeHistory(
        IReadOnlyList<PlaybackRecentItem> recentItems,
        IReadOnlyList<PlaybackBookmark> bookmarks);
}
