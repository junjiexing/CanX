// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using CanX.Downloads.Persistence;

namespace CanX.Downloads.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DownloadSessionDocument))]
internal sealed partial class DownloadSessionJsonContext : JsonSerializerContext;
