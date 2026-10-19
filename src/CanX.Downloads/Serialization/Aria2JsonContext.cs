// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using CanX.Downloads.Engine;

namespace CanX.Downloads.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Aria2DownloadDetails))]
[JsonSerializable(typeof(Aria2CapabilityDetails))]
[JsonSerializable(typeof(List<Aria2PeerDetails>))]
[JsonSerializable(typeof(List<Aria2ServerDetails>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class Aria2JsonContext : JsonSerializerContext;
