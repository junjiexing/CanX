// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Application.Playback;
using CanX.Core.Playback;

namespace CanX.Infrastructure.Playback;

/// <summary>
/// Shares history while keeping each player's playlist independent. Each session
/// submits changes relative to its last synchronized history, so a stale player
/// cannot erase another player's progress or resurrect deleted history.
/// </summary>
public sealed class SharedPlaybackStateStore(IPlaybackStateStore store)
{
    private readonly IPlaybackStateStore _store = store;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private PlaybackPersistedState? _state;
    private Session? _active;
    private long _version;

    public Session CreateSession(bool restorePlaylist = false)
    {
        var session = new Session(this, restorePlaylist);
        lock (_gate) _active ??= session;
        return session;
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) if (_state is not null) return;
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) _state = state;
        }
        finally { _ioGate.Release(); }
    }

    public sealed class Session : ISharedPlaybackHistoryStore
    {
        private readonly SharedPlaybackStateStore _owner;
        private readonly bool _restorePlaylist;
        private IReadOnlyList<PlaybackRecentItem> _recent = [];
        private IReadOnlyList<PlaybackBookmark> _bookmarks = [];
        private long _version = -1;

        internal Session(SharedPlaybackStateStore owner, bool restorePlaylist)
        {
            _owner = owner;
            _restorePlaylist = restorePlaylist;
        }

        public void Activate()
        {
            lock (_owner._gate) _owner._active = this;
        }

        public async ValueTask<PlaybackPersistedState> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _owner.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            lock (_owner._gate)
            {
                var state = _owner._state!;
                Remember(state);
                return state with
                {
                    PlaylistSources = _restorePlaylist ? state.PlaylistSources.ToList() : [],
                    SelectedPlaylistIndex = _restorePlaylist ? state.SelectedPlaylistIndex : -1,
                    RecentItems = state.RecentItems.ToList(),
                    Bookmarks = state.Bookmarks.ToList(),
                };
            }
        }

        public PlaybackPersistedState SynchronizeHistory(
            IReadOnlyList<PlaybackRecentItem> recentItems,
            IReadOnlyList<PlaybackBookmark> bookmarks)
        {
            lock (_owner._gate)
            {
                var state = _owner._state ?? throw new InvalidOperationException("Playback state has not been loaded.");
                if (_version == _owner._version && _recent.SequenceEqual(recentItems) && _bookmarks.SequenceEqual(bookmarks))
                    return state;

                var recent = Merge(state.RecentItems, _recent, recentItems, RecentKey)
                    .OrderByDescending(static item => item.LastPlayedAt).Take(100).ToList();
                var mergedBookmarks = Merge(state.Bookmarks, _bookmarks, bookmarks, static item => item.Id)
                    .OrderBy(static item => item.CreatedAt).Take(5000).ToList();
                if (!state.RecentItems.SequenceEqual(recent) || !state.Bookmarks.SequenceEqual(mergedBookmarks))
                {
                    state = state with { RecentItems = recent, Bookmarks = mergedBookmarks };
                    _owner._state = state;
                    _owner._version++;
                }
                Remember(state);
                return state;
            }
        }

        public async ValueTask SaveAsync(PlaybackPersistedState state, CancellationToken cancellationToken = default)
        {
            await _owner._ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PlaybackPersistedState merged;
                lock (_owner._gate)
                {
                    merged = _owner._state!;
                    if (ReferenceEquals(_owner._active, this))
                    {
                        merged = merged with
                        {
                            PlaylistSources = state.PlaylistSources.ToList(),
                            SelectedPlaylistIndex = state.SelectedPlaylistIndex,
                        };
                        _owner._state = merged;
                    }
                }
                await _owner._store.SaveAsync(merged, cancellationToken).ConfigureAwait(false);
            }
            finally { _owner._ioGate.Release(); }
        }

        private void Remember(PlaybackPersistedState state)
        {
            _recent = state.RecentItems;
            _bookmarks = state.Bookmarks;
            _version = _owner._version;
        }

        private static string RecentKey(PlaybackRecentItem item) =>
            $"{item.IsMediaSource}\n{item.MediaSourceProfileId}\n" +
            (OperatingSystem.IsWindows() ? item.Source.ToUpperInvariant() : item.Source);

        private static IEnumerable<T> Merge<T>(IReadOnlyList<T> shared, IReadOnlyList<T> baseline,
            IReadOnlyList<T> incoming, Func<T, string> key) where T : notnull
        {
            var result = shared.ToDictionary(key, StringComparer.Ordinal);
            var previous = baseline.ToDictionary(key, StringComparer.Ordinal);
            var current = incoming.ToDictionary(key, StringComparer.Ordinal);
            foreach (var removed in previous.Keys.Except(current.Keys)) result.Remove(removed);
            foreach (var (identity, value) in current)
            {
                if (!previous.TryGetValue(identity, out var old) || !EqualityComparer<T>.Default.Equals(old, value))
                    result[identity] = value;
            }
            return result.Values;
        }
    }
}
