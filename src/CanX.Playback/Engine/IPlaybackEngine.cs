// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;

namespace CanX.Playback.Engine;

public interface IPlaybackEngine : IDisposable
{
    string RenderApi { get; }

    string RenderContext { get; }

    void Initialize(IReadOnlyDictionary<string, string> options);

    void Command(params string[] arguments);

    bool TryGetString(string name, out string value);

    bool TryGetDouble(string name, out double value);

    bool TryGetInt64(string name, out long value);

    bool TryGetFlag(string name, out bool value);

    PlaybackEngineEvent PollEvent();

    IReadOnlyList<PlaybackTrack> GetTracks();

    IReadOnlyList<PlaybackPlaylistItem> GetPlaylist();

    IReadOnlyList<PlaybackChapter> GetChapters();

    IReadOnlyList<PlaybackAudioDevice> GetAudioDevices();

    IReadOnlyList<string> GetShaders();

    MpvRuntimeCapabilityManifest GetCapabilities();
}

public sealed record MpvRuntimeCapabilityManifest
{
    public string MpvVersion { get; init; } = string.Empty;

    public string ClientApiVersion { get; init; } = string.Empty;

    public string FfmpegVersion { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Configuration { get; init; } = string.Empty;

    public List<string> Properties { get; init; } = [];

    public List<MpvRuntimeCommandCapability> Commands { get; init; } = [];
}

public sealed record MpvRuntimeCommandCapability
{
    public string Name { get; init; } = string.Empty;

    public bool VariableArguments { get; init; }

    public List<MpvRuntimeCommandArgumentCapability> Arguments { get; init; } = [];
}

public sealed record MpvRuntimeCommandArgumentCapability
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public bool Optional { get; init; }

    public string DefaultValue { get; init; } = string.Empty;

    public List<string> Choices { get; init; } = [];
}

public enum PlaybackEngineEventKind
{
    None,
    StartFile,
    FileLoaded,
    EndFile,
    Stopped,
    Shutdown,
}

public readonly record struct PlaybackEngineEvent(
    PlaybackEngineEventKind Kind,
    bool IsError = false,
    string ErrorMessage = "");
