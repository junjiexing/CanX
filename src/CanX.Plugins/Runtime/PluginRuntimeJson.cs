// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace CanX.Plugins.Runtime;

internal sealed record PluginBridgeError(string Code, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginBridgeError))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class PluginRuntimeJsonContext : JsonSerializerContext;
