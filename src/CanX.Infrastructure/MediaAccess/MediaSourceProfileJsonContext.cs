// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace CanX.Infrastructure.MediaAccess;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MediaSourceProfileDocument))]
internal sealed partial class MediaSourceProfileJsonContext : JsonSerializerContext;
