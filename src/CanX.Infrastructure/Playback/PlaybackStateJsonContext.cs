// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using CanX.Core.Playback;

namespace CanX.Infrastructure.Playback;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PlaybackPersistedState))]
internal sealed partial class PlaybackStateJsonContext : JsonSerializerContext;
