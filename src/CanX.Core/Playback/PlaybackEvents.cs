// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Events;

namespace CanX.Core.Playback;

public sealed record PlaybackChanged(PlaybackSnapshot Snapshot) : IAppEvent;
