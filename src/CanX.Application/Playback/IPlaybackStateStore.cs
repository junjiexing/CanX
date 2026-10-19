// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;

namespace CanX.Application.Playback;

public interface IPlaybackStateStore
{
    ValueTask<PlaybackPersistedState> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(PlaybackPersistedState state, CancellationToken cancellationToken = default);
}
