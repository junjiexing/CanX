// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using CanX.Core.Configuration;

namespace CanX.Infrastructure.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ApplicationSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
