// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Playback;

public enum PlaybackCapabilityCoverageKind
{
    Unknown,
    DedicatedTool,
    AdvancedAccess,
    Blocked,
}

public sealed record PlaybackPropertyCapability
{
    public string Name { get; init; } = string.Empty;

    public bool CanRead { get; init; }

    public bool CanWrite { get; init; }

    public PlaybackCapabilityCoverageKind Coverage { get; init; }

    public List<string> Tools { get; init; } = [];

    public string Restriction { get; init; } = string.Empty;
}

public sealed record PlaybackCommandArgumentCapability
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public bool Optional { get; init; }

    public string DefaultValue { get; init; } = string.Empty;

    public List<string> Choices { get; init; } = [];
}

public sealed record PlaybackCommandCapability
{
    public string Name { get; init; } = string.Empty;

    public bool CanExecute { get; init; }

    public bool VariableArguments { get; init; }

    public PlaybackCapabilityCoverageKind Coverage { get; init; }

    public List<string> Tools { get; init; } = [];

    public string Restriction { get; init; } = string.Empty;

    public List<PlaybackCommandArgumentCapability> Arguments { get; init; } = [];
}

public sealed record PlaybackCapabilityManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string MpvVersion { get; init; } = string.Empty;

    public string ClientApiVersion { get; init; } = string.Empty;

    public string FfmpegVersion { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Configuration { get; init; } = string.Empty;

    public List<PlaybackPropertyCapability> Properties { get; init; } = [];

    public List<PlaybackCommandCapability> Commands { get; init; } = [];
}
