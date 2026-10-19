// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CanX.Core.Diagnostics;
using CanX.Core.Playback;
using CanX.Playback.Interop;
using CanX.Streaming;

namespace CanX.Playback.Engine;

public sealed class NativeMpvEngine : IPlaybackEngine, IMpvRenderSource
{
    private readonly Lock _sync = new();
    private readonly IApplicationLog? _applicationLog;
    private readonly MediaStreamBroker _streamBroker;
    private readonly bool _ownsStreamBroker;
    private readonly double? _displayRefreshRate;
    private readonly bool _enumerateAudioDevices;
    private readonly bool _paceRenderApiSwaps;
    private readonly bool _reportRenderApiSwaps;
    private readonly MpvStreamCallbackCounters _streamCallbackCounters = new();
    private nint _handle;
    private MpvStreamCallbackBridge? _streamCallbackBridge;
    private double? _effectiveDisplayRefreshRate;
    private bool _disposed;

    public NativeMpvEngine(
        IApplicationLog? applicationLog = null,
        MediaStreamBroker? streamBroker = null,
        double? displayRefreshRate = null,
        bool enumerateAudioDevices = true,
        bool paceRenderApiSwaps = true,
        bool reportRenderApiSwaps = true)
    {
        _applicationLog = applicationLog;
        _streamBroker = streamBroker ?? new MediaStreamBroker();
        _ownsStreamBroker = streamBroker is null;
        _displayRefreshRate = displayRefreshRate is >= 20 and <= 1000
            ? displayRefreshRate
            : null;
        _enumerateAudioDevices = enumerateAudioDevices;
        _paceRenderApiSwaps = paceRenderApiSwaps;
        _reportRenderApiSwaps = reportRenderApiSwaps;
        if (_applicationLog is not null)
        {
            _applicationLog.EnabledChanged += OnLoggingEnabledChanged;
        }
    }

    public bool IsInitialized
    {
        get
        {
            lock (_sync)
            {
                return _handle != 0;
            }
        }
    }

    public string RenderApi => "opengl";

    public string RenderContext => "host-provided";

    public MpvStreamCallbackMetrics StreamCallbackMetrics => _streamCallbackCounters.Snapshot;

    public void Initialize(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle != 0)
            {
                return;
            }

            ConfigureAlsaPluginDirectory();
            var handle = MpvNative.Create();
            if (handle == 0)
            {
                throw new InvalidOperationException("mpv_create failed.");
            }

            MpvStreamCallbackBridge? streamCallbackBridge = null;
            try
            {
                streamCallbackBridge = new MpvStreamCallbackBridge(_streamBroker, _streamCallbackCounters);
                streamCallbackBridge.Register(handle);

                foreach (var option in options)
                {
                    SetOption(handle, option.Key, option.Value);
                }

                var explicitDisplayRefreshRate = options
                    .Where(static option => string.Equals(
                        option.Key,
                        "display-fps-override",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(static option => double.TryParse(
                        option.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var rate) && rate is >= 20 and <= 1000
                            ? rate
                            : (double?)null)
                    .FirstOrDefault();
                _effectiveDisplayRefreshRate = explicitDisplayRefreshRate ?? _displayRefreshRate;
                if (explicitDisplayRefreshRate is null && _displayRefreshRate is { } refreshRate)
                {
                    SetOption(
                        handle,
                        "display-fps-override",
                        refreshRate.ToString("0.###", CultureInfo.InvariantCulture));
                }

                // These options define the libmpv/Avalonia host contract and must win over user configuration.
                SetOption(handle, "config", "no");
                SetOption(handle, "terminal", "no");
                SetOption(handle, "input-default-bindings", "yes");
                SetOption(handle, "input-builtin-bindings", "yes");
                SetOption(handle, "input-vo-keyboard", "no");
                SetOption(handle, "osd-bar", "no");
                SetOption(handle, "idle", "yes");
                SetOption(handle, "keep-open", options.TryGetValue("keep-open", out var keepOpen) ? keepOpen : "yes");
                SetOption(handle, "vo", "libmpv");

                EnsureSuccess(MpvNative.Initialize(handle), "mpv_initialize");
                _ = MpvNative.RequestLogMessages(handle, _applicationLog?.IsEnabled == true ? "v" : "no");
                _streamCallbackBridge = streamCallbackBridge;
                _handle = handle;
            }
            catch
            {
                MpvNative.TerminateDestroy(handle);
                streamCallbackBridge?.Dispose();
                throw;
            }
        }
    }

    public void Command(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0 || string.IsNullOrEmpty(arguments[0]) || arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("An mpv command requires non-empty arguments.", nameof(arguments));
        }

        lock (_sync)
        {
            if (arguments.Length >= 2 && arguments[0] == "loadfile")
            {
                arguments = (string[])arguments.Clone();
                arguments[1] = _streamBroker.GetPlaybackSource(arguments[1]);
            }
            EnsureSuccess(MpvNative.RunCommand(RequireHandle(), arguments), $"mpv command '{arguments[0]}'");
        }
    }

    public unsafe bool TryGetDouble(string name, out double value)
    {
        lock (_sync)
        {
            var nativeValue = 0d;
            var succeeded = MpvNative.GetProperty(RequireHandle(), name, MpvFormat.Double, &nativeValue) >= 0;
            value = nativeValue;
            return succeeded;
        }
    }

    public unsafe bool TryGetInt64(string name, out long value)
    {
        lock (_sync)
        {
            var nativeValue = 0L;
            var succeeded = MpvNative.GetProperty(RequireHandle(), name, MpvFormat.Int64, &nativeValue) >= 0;
            value = nativeValue;
            return succeeded;
        }
    }

    public unsafe bool TryGetFlag(string name, out bool value)
    {
        lock (_sync)
        {
            var flag = 0;
            var succeeded = MpvNative.GetProperty(RequireHandle(), name, MpvFormat.Flag, &flag) >= 0;
            value = flag != 0;
            return succeeded;
        }
    }

    public bool TryGetString(string name, out string value)
    {
        lock (_sync)
        {
            var pointer = MpvNative.GetPropertyString(RequireHandle(), name);
            if (pointer == 0)
            {
                value = string.Empty;
                return false;
            }

            try
            {
                value = _streamBroker.GetPublicSource(Marshal.PtrToStringUTF8(pointer) ?? string.Empty);
                return true;
            }
            finally
            {
                MpvNative.Free(pointer);
            }
        }
    }

    public unsafe PlaybackEngineEvent PollEvent()
    {
        lock (_sync)
        {
            for (var index = 0; index < 256; index++)
            {
                var eventPointer = MpvNative.WaitEvent(RequireHandle(), 0);
                if (eventPointer == 0)
                {
                    return default;
                }

                var nativeEvent = *(MpvEvent*)eventPointer;
                if (nativeEvent.EventId == MpvEventId.LogMessage)
                {
                    WriteLogMessage(nativeEvent);
                    continue;
                }

                return nativeEvent.EventId switch
                {
                    MpvEventId.StartFile => new PlaybackEngineEvent(PlaybackEngineEventKind.StartFile),
                    MpvEventId.FileLoaded => new PlaybackEngineEvent(PlaybackEngineEventKind.FileLoaded),
                    MpvEventId.EndFile => MapEndFile(nativeEvent),
                    MpvEventId.Shutdown => new PlaybackEngineEvent(PlaybackEngineEventKind.Shutdown),
                    _ => default,
                };
            }

            return default;
        }
    }

    public IReadOnlyList<PlaybackTrack> GetTracks()
    {
        if (!TryGetInt64("track-list/count", out var count) || count is < 1 or > 1024)
        {
            return [];
        }

        var tracks = new List<PlaybackTrack>((int)count);
        for (var index = 0; index < count; index++)
        {
            if (!TryGetInt64($"track-list/{index}/id", out var id) ||
                !TryGetString($"track-list/{index}/type", out var type))
            {
                continue;
            }

            _ = TryGetString($"track-list/{index}/title", out var title);
            _ = TryGetString($"track-list/{index}/lang", out var language);
            _ = TryGetString($"track-list/{index}/codec", out var codec);
            _ = TryGetFlag($"track-list/{index}/selected", out var selected);
            _ = TryGetFlag($"track-list/{index}/albumart", out var albumArt);
            _ = TryGetFlag($"track-list/{index}/default", out var isDefault);
            _ = TryGetFlag($"track-list/{index}/forced", out var forced);
            _ = TryGetFlag($"track-list/{index}/external", out var external);
            _ = TryGetString($"track-list/{index}/external-filename", out var externalFileName);
            tracks.Add(new PlaybackTrack
            {
                Id = id,
                Type = type switch
                {
                    "video" => PlaybackTrackType.Video,
                    "audio" => PlaybackTrackType.Audio,
                    "sub" => PlaybackTrackType.Subtitle,
                    _ => PlaybackTrackType.Unknown,
                },
                Title = title,
                Language = language,
                Codec = codec,
                IsSelected = selected,
                IsAlbumArt = albumArt,
                IsDefault = isDefault,
                IsForced = forced,
                IsExternal = external,
                ExternalFileName = externalFileName,
            });
        }

        return tracks;
    }

    public IReadOnlyList<PlaybackPlaylistItem> GetPlaylist()
    {
        if (!TryGetInt64("playlist/count", out var count) || count is < 1 or > 10_000)
        {
            return [];
        }

        var playlist = new List<PlaybackPlaylistItem>((int)count);
        for (var index = 0; index < count; index++)
        {
            if (!TryGetString($"playlist/{index}/filename", out var source) ||
                string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            _ = TryGetString($"playlist/{index}/title", out var title);
            _ = TryGetFlag($"playlist/{index}/current", out var current);
            playlist.Add(new PlaybackPlaylistItem
            {
                Index = (int)index,
                Source = source,
                Title = string.IsNullOrWhiteSpace(title) ? GetDisplayName(source) : title,
                IsCurrent = current,
            });
        }

        return playlist;
    }

    public IReadOnlyList<PlaybackChapter> GetChapters()
    {
        if (!TryGetInt64("chapter-list/count", out var count) || count is < 1 or > 10_000)
        {
            return [];
        }

        var currentChapter = TryGetInt64("chapter", out var selectedChapter) ? selectedChapter : -1;
        var chapters = new List<PlaybackChapter>((int)count);
        for (var index = 0; index < count; index++)
        {
            if (!TryGetDouble($"chapter-list/{index}/time", out var time) || !double.IsFinite(time))
            {
                continue;
            }

            _ = TryGetString($"chapter-list/{index}/title", out var title);
            chapters.Add(new PlaybackChapter
            {
                Index = (int)index,
                Title = string.IsNullOrWhiteSpace(title) ? $"章节 {index + 1}" : title,
                TimeSeconds = Math.Max(0, time),
                IsCurrent = index == currentChapter,
            });
        }

        return chapters;
    }

    public unsafe IReadOnlyList<PlaybackAudioDevice> GetAudioDevices()
    {
        // mpv implements audio-device-list by probing every available AO. On
        // Android, probing AudioTrack repeatedly creates and destroys real
        // AudioTrack instances and can starve the active playback clock. The
        // platform UI therefore exposes Android's system-routed output as the
        // single automatic device instead of running that desktop-oriented
        // probe.
        if (!_enumerateAudioDevices)
        {
            return
            [
                new PlaybackAudioDevice
                {
                    Name = "auto",
                    Description = "Android system output",
                    IsCurrent = true,
                },
            ];
        }

        lock (_sync)
        {
            var node = default(MpvNode);
            if (MpvNative.GetProperty(RequireHandle(), "audio-device-list", MpvFormat.Node, &node) < 0)
            {
                return [];
            }

            try
            {
                if (node.Format != MpvFormat.NodeArray || node.Value.List == 0)
                {
                    return [];
                }

                var selected = ReadStringProperty("audio-device", "auto");
                var devices = new List<PlaybackAudioDevice>();
                var list = *(MpvNodeList*)node.Value.List;
                if (list.Count is < 0 or > 4096 || list.Values == 0)
                {
                    return [];
                }

                var values = (MpvNode*)list.Values;
                for (var index = 0; index < list.Count; index++)
                {
                    if (!TryReadMapString(values[index], "name", out var name) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    _ = TryReadMapString(values[index], "description", out var description);
                    devices.Add(new PlaybackAudioDevice
                    {
                        Name = name,
                        Description = string.IsNullOrWhiteSpace(description) ? name : description,
                        IsCurrent = string.Equals(name, selected, StringComparison.Ordinal),
                    });
                }

                return devices;
            }
            finally
            {
                MpvNative.FreeNodeContents(&node);
            }
        }
    }

    public unsafe IReadOnlyList<string> GetShaders()
    {
        lock (_sync)
        {
            var node = default(MpvNode);
            if (MpvNative.GetProperty(RequireHandle(), "glsl-shaders", MpvFormat.Node, &node) < 0)
            {
                return [];
            }

            try
            {
                if (node.Format != MpvFormat.NodeArray || node.Value.List == 0)
                {
                    return [];
                }

                var list = *(MpvNodeList*)node.Value.List;
                if (list.Count is < 0 or > 4096 || list.Values == 0)
                {
                    return [];
                }

                var values = (MpvNode*)list.Values;
                var shaders = new List<string>(list.Count);
                for (var index = 0; index < list.Count; index++)
                {
                    if (values[index].Format == MpvFormat.String && values[index].Value.String != 0)
                    {
                        shaders.Add(Marshal.PtrToStringUTF8(values[index].Value.String) ?? string.Empty);
                    }
                }

                return shaders;
            }
            finally
            {
                MpvNative.FreeNodeContents(&node);
            }
        }
    }

    public unsafe MpvRuntimeCapabilityManifest GetCapabilities()
    {
        lock (_sync)
        {
            var properties = ReadStringListProperty("property-list", 16_384);
            var commands = new List<MpvRuntimeCommandCapability>();
            var node = default(MpvNode);
            if (MpvNative.GetProperty(RequireHandle(), "command-list", MpvFormat.Node, &node) >= 0)
            {
                try
                {
                    if (TryReadArray(node, 4096, out var commandValues))
                    {
                        foreach (var commandNode in commandValues)
                        {
                            if (!TryReadMapString(commandNode, "name", out var name))
                            {
                                name = string.Empty;
                            }

                            _ = TryReadMapFlag(commandNode, "vararg", out var variableArguments);
                            var arguments = new List<MpvRuntimeCommandArgumentCapability>();
                            if (TryReadMapNode(commandNode, "args", out var argumentsNode) &&
                                TryReadArray(argumentsNode, 256, out var argumentValues))
                            {
                                foreach (var argumentNode in argumentValues)
                                {
                                    _ = TryReadMapString(argumentNode, "name", out var argumentName);
                                    _ = TryReadMapString(argumentNode, "type", out var type);
                                    _ = TryReadMapFlag(argumentNode, "optional", out var optional);
                                    var defaultValue = TryReadMapNode(argumentNode, "default-value", out var defaultNode)
                                        ? FormatNodeValue(defaultNode)
                                        : string.Empty;
                                    var choices = TryReadMapNode(argumentNode, "choices", out var choicesNode)
                                        ? ReadStringArray(choicesNode, 4096)
                                        : [];
                                    arguments.Add(new MpvRuntimeCommandArgumentCapability
                                    {
                                        Name = argumentName,
                                        Type = type,
                                        Optional = optional,
                                        DefaultValue = defaultValue,
                                        Choices = choices,
                                    });
                                }
                            }

                            commands.Add(new MpvRuntimeCommandCapability
                            {
                                Name = name,
                                VariableArguments = variableArguments,
                                Arguments = arguments,
                            });
                        }
                    }
                }
                finally
                {
                    MpvNative.FreeNodeContents(&node);
                }
            }

            return new MpvRuntimeCapabilityManifest
            {
                MpvVersion = ReadStringProperty("mpv-version", string.Empty),
                ClientApiVersion = ApiVersion,
                FfmpegVersion = ReadStringProperty("ffmpeg-version", string.Empty),
                Platform = ReadStringProperty(
                    "platform",
                    $"{RuntimeInformation.OSDescription}/{RuntimeInformation.ProcessArchitecture}"),
                Configuration = ReadStringProperty("mpv-configuration", string.Empty),
                Properties = properties,
                Commands = commands,
            };
        }
    }

    public unsafe IMpvRenderContext CreateOpenGlRenderContext(nint getProcAddress, nint callbackContext)
    {
        if (getProcAddress == 0)
        {
            throw new ArgumentException("A GL procedure resolver is required.", nameof(getProcAddress));
        }

        lock (_sync)
        {
            var initParameters = new MpvOpenGlInitParams
            {
                GetProcAddress = getProcAddress,
                Context = callbackContext,
            };
            var apiType = stackalloc byte[]
            {
                (byte)'o', (byte)'p', (byte)'e', (byte)'n', (byte)'g', (byte)'l', 0,
            };
            var advancedControl = 1;
            var parameters = stackalloc MpvRenderParam[4];
            parameters[0] = new MpvRenderParam(MpvRenderParamType.ApiType, (nint)apiType);
            parameters[1] = new MpvRenderParam(MpvRenderParamType.OpenGlInitParams, (nint)(&initParameters));
            parameters[2] = new MpvRenderParam(MpvRenderParamType.AdvancedControl, (nint)(&advancedControl));
            parameters[3] = new MpvRenderParam(MpvRenderParamType.Invalid, 0);
            nint renderContext = 0;
            EnsureSuccess(
                MpvNative.RenderContextCreate(&renderContext, RequireHandle(), parameters),
                "mpv_render_context_create");
            return new NativeMpvRenderContext(
                renderContext,
                _paceRenderApiSwaps ? _effectiveDisplayRefreshRate : null,
                _reportRenderApiSwaps);
        }
    }

    public void Dispose()
    {
        if (_applicationLog is not null)
        {
            _applicationLog.EnabledChanged -= OnLoggingEnabledChanged;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_handle != 0)
            {
                MpvNative.TerminateDestroy(_handle);
                _handle = 0;
            }

            _streamCallbackBridge?.Dispose();
            _streamCallbackBridge = null;
            if (_ownsStreamBroker)
            {
                _streamBroker.Dispose();
            }
        }
    }

    public static string ApiVersion
    {
        get
        {
            var version = MpvNative.ClientApiVersion();
            return $"{version >> 16}.{version & 0xffff}";
        }
    }

    private nint RequireHandle() => _handle != 0
        ? _handle
        : throw new InvalidOperationException("libmpv is not initialized.");

    private string ReadStringProperty(string name, string fallback)
    {
        var pointer = MpvNative.GetPropertyString(RequireHandle(), name);
        if (pointer == 0)
        {
            return fallback;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer) ?? fallback;
        }
        finally
        {
            MpvNative.Free(pointer);
        }
    }

    private static unsafe bool TryReadMapString(MpvNode node, string key, out string value)
    {
        value = string.Empty;
        if (node.Format != MpvFormat.NodeMap || node.Value.List == 0)
        {
            return false;
        }

        var map = *(MpvNodeList*)node.Value.List;
        if (map.Count is < 0 or > 4096 || map.Values == 0 || map.Keys == 0)
        {
            return false;
        }

        var values = (MpvNode*)map.Values;
        var keys = (nint*)map.Keys;
        for (var index = 0; index < map.Count; index++)
        {
            if (!string.Equals(Marshal.PtrToStringUTF8(keys[index]), key, StringComparison.Ordinal) ||
                values[index].Format != MpvFormat.String || values[index].Value.String == 0)
            {
                continue;
            }

            value = Marshal.PtrToStringUTF8(values[index].Value.String) ?? string.Empty;
            return true;
        }

        return false;
    }

    private unsafe List<string> ReadStringListProperty(string name, int maximumCount)
    {
        var node = default(MpvNode);
        if (MpvNative.GetProperty(RequireHandle(), name, MpvFormat.Node, &node) < 0)
        {
            return [];
        }

        try
        {
            return ReadStringArray(node, maximumCount);
        }
        finally
        {
            MpvNative.FreeNodeContents(&node);
        }
    }

    private static unsafe List<string> ReadStringArray(MpvNode node, int maximumCount)
    {
        if (!TryReadArray(node, maximumCount, out var values))
        {
            return [];
        }

        var result = new List<string>(values.Length);
        foreach (var value in values)
        {
            if (value.Format == MpvFormat.String && value.Value.String != 0)
            {
                result.Add(Marshal.PtrToStringUTF8(value.Value.String) ?? string.Empty);
            }
        }

        return result;
    }

    private static unsafe bool TryReadArray(MpvNode node, int maximumCount, out ReadOnlySpan<MpvNode> values)
    {
        values = default;
        if (node.Format != MpvFormat.NodeArray || node.Value.List == 0)
        {
            return false;
        }

        var list = *(MpvNodeList*)node.Value.List;
        if (list.Count is < 0 || list.Count > maximumCount || list.Values == 0)
        {
            return false;
        }

        values = new ReadOnlySpan<MpvNode>((void*)list.Values, list.Count);
        return true;
    }

    private static unsafe bool TryReadMapNode(MpvNode node, string key, out MpvNode value)
    {
        value = default;
        if (node.Format != MpvFormat.NodeMap || node.Value.List == 0)
        {
            return false;
        }

        var map = *(MpvNodeList*)node.Value.List;
        if (map.Count is < 0 or > 4096 || map.Values == 0 || map.Keys == 0)
        {
            return false;
        }

        var values = (MpvNode*)map.Values;
        var keys = (nint*)map.Keys;
        for (var index = 0; index < map.Count; index++)
        {
            if (string.Equals(Marshal.PtrToStringUTF8(keys[index]), key, StringComparison.Ordinal))
            {
                value = values[index];
                return true;
            }
        }

        return false;
    }

    private static bool TryReadMapFlag(MpvNode node, string key, out bool value)
    {
        value = false;
        if (!TryReadMapNode(node, key, out var flagNode) || flagNode.Format != MpvFormat.Flag)
        {
            return false;
        }

        value = flagNode.Value.Flag != 0;
        return true;
    }

    private static string FormatNodeValue(MpvNode node) => node.Format switch
    {
        MpvFormat.String when node.Value.String != 0 => Marshal.PtrToStringUTF8(node.Value.String) ?? string.Empty,
        MpvFormat.Flag => node.Value.Flag == 0 ? "no" : "yes",
        MpvFormat.Int64 => node.Value.Int64.ToString(CultureInfo.InvariantCulture),
        MpvFormat.Double => node.Value.Double.ToString("R", CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private void OnLoggingEnabledChanged(bool enabled)
    {
        lock (_sync)
        {
            if (_handle != 0 && !_disposed)
            {
                _ = MpvNative.RequestLogMessages(_handle, enabled ? "v" : "no");
            }
        }
    }

    private unsafe void WriteLogMessage(MpvEvent nativeEvent)
    {
        if (_applicationLog is null || nativeEvent.Data == 0)
        {
            return;
        }

        var logMessage = *(MpvEventLogMessage*)nativeEvent.Data;
        var prefix = Marshal.PtrToStringUTF8(logMessage.Prefix) ?? "mpv";
        var text = Marshal.PtrToStringUTF8(logMessage.Text) ?? string.Empty;
        _applicationLog.Write(
            LogComponent.Mpv,
            MapLogLevel(logMessage.LogLevel),
            $"[{prefix}] {text.TrimEnd()}");
    }

    private static ApplicationLogLevel MapLogLevel(int level) => level switch
    {
        >= 70 => ApplicationLogLevel.Trace,
        >= 50 => ApplicationLogLevel.Debug,
        >= 40 => ApplicationLogLevel.Information,
        >= 30 => ApplicationLogLevel.Warning,
        >= 20 => ApplicationLogLevel.Error,
        _ => ApplicationLogLevel.Critical,
    };

    private static void ConfigureAlsaPluginDirectory()
    {
        if (!OperatingSystem.IsLinux() ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALSA_PLUGIN_DIR")))
        {
            return;
        }

        string[] candidates =
        [
            "/usr/lib/x86_64-linux-gnu/alsa-lib",
            "/usr/lib/aarch64-linux-gnu/alsa-lib",
            "/usr/lib64/alsa-lib",
            "/usr/lib/alsa-lib",
            "/usr/local/lib/alsa-lib",
        ];
        var pluginDirectory = candidates.FirstOrDefault(Directory.Exists);
        if (pluginDirectory is not null &&
            MpvNative.SetEnvironmentVariable("ALSA_PLUGIN_DIR", pluginDirectory, overwrite: 0) != 0)
        {
            throw new InvalidOperationException("Could not configure the ALSA plug-in directory.");
        }
    }

    private static string GetDisplayName(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? uri.Host : name;
        }

        return Path.GetFileName(source);
    }

    private static void SetOption(nint handle, string name, string value) =>
        EnsureSuccess(MpvNative.SetOptionString(handle, name, value), $"mpv option '{name}'");

    private static unsafe PlaybackEngineEvent MapEndFile(MpvEvent nativeEvent)
    {
        if (nativeEvent.Data == 0)
        {
            return new PlaybackEngineEvent(PlaybackEngineEventKind.EndFile);
        }

        var endFile = *(MpvEventEndFile*)nativeEvent.Data;
        var isError = endFile.Reason == MpvEndFileReason.Error || endFile.Error < 0;
        if (!isError && endFile.Reason != MpvEndFileReason.Eof)
        {
            return new PlaybackEngineEvent(PlaybackEngineEventKind.Stopped);
        }

        return new PlaybackEngineEvent(
            PlaybackEngineEventKind.EndFile,
            isError,
            isError ? MpvNative.GetError(endFile.Error) : string.Empty);
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{operation} failed: {MpvNative.GetError(result)} ({result}).");
        }
    }

    private sealed class NativeMpvRenderContext : IMpvRenderContext
    {
        private readonly long _swapIntervalTicks;
        private readonly bool _reportSwaps;
        private nint _handle;
        private long _nextSwapTimestamp;

        public NativeMpvRenderContext(nint handle, double? displayRefreshRate, bool reportSwaps)
        {
            _handle = handle;
            _reportSwaps = reportSwaps;
            _swapIntervalTicks = displayRefreshRate is { } rate
                ? Math.Max(1, (long)Math.Round(Stopwatch.Frequency / rate))
                : 0;
        }

        public void SetUpdateCallback(nint callback, nint callbackContext) =>
            MpvNative.RenderContextSetUpdateCallback(RequireHandle(), callback, callbackContext);

        public bool Update() =>
            (MpvNative.RenderContextUpdate(RequireHandle()) & (ulong)MpvRenderUpdateFlags.Frame) != 0;

        public unsafe MpvRenderFrameInfo GetNextFrameInfo()
        {
            var native = new MpvRenderFrameInfoNative();
            var parameter = new MpvRenderParam(MpvRenderParamType.NextFrameInfo, (nint)(&native));
            EnsureSuccess(
                MpvNative.RenderContextGetInfo(RequireHandle(), parameter),
                "mpv_render_context_get_info");
            return new MpvRenderFrameInfo(
                native.Flags.HasFlag(MpvRenderFrameInfoFlags.Present),
                native.Flags.HasFlag(MpvRenderFrameInfoFlags.Redraw),
                native.Flags.HasFlag(MpvRenderFrameInfoFlags.Repeat),
                native.Flags.HasFlag(MpvRenderFrameInfoFlags.BlockVSync),
                native.TargetTime);
        }

        public unsafe void Render(int framebuffer, int width, int height, bool flipY)
        {
            var target = new MpvOpenGlFbo
            {
                Framebuffer = framebuffer,
                Width = width,
                Height = height,
            };
            var flip = flipY ? 1 : 0;
            var parameters = stackalloc MpvRenderParam[3];
            parameters[0] = new MpvRenderParam(MpvRenderParamType.OpenGlFbo, (nint)(&target));
            parameters[1] = new MpvRenderParam(MpvRenderParamType.FlipY, (nint)(&flip));
            parameters[2] = new MpvRenderParam(MpvRenderParamType.Invalid, 0);
            EnsureSuccess(MpvNative.RenderContextRender(RequireHandle(), parameters), "mpv_render_context_render");
        }

        public unsafe void SkipRender()
        {
            var skip = 1;
            var parameters = stackalloc MpvRenderParam[2];
            parameters[0] = new MpvRenderParam(MpvRenderParamType.SkipRendering, (nint)(&skip));
            parameters[1] = new MpvRenderParam(MpvRenderParamType.Invalid, 0);
            EnsureSuccess(MpvNative.RenderContextRender(RequireHandle(), parameters), "mpv_render_context_render(skip)");
        }

        public void ReportSwap()
        {
            if (!_reportSwaps)
            {
                return;
            }

            PacePresentation();
            MpvNative.RenderContextReportSwap(RequireHandle());
        }

        public void Dispose()
        {
            var handleToFree = Interlocked.Exchange(ref _handle, 0);
            if (handleToFree != 0)
            {
                MpvNative.RenderContextSetUpdateCallback(handleToFree, 0, 0);
                MpvNative.RenderContextFree(handleToFree);
            }
        }

        private nint RequireHandle() => _handle != 0
            ? _handle
            : throw new ObjectDisposedException(nameof(NativeMpvRenderContext));

        private void PacePresentation()
        {
            if (_swapIntervalTicks <= 0)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var target = _nextSwapTimestamp;
            if (target == 0 || now - target > _swapIntervalTicks * 2)
            {
                _nextSwapTimestamp = now + _swapIntervalTicks;
                return;
            }

            while (now < target)
            {
                var remainingMilliseconds = (target - now) * 1000d / Stopwatch.Frequency;
                if (remainingMilliseconds > 1.5)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(remainingMilliseconds - 0.75));
                }
                else
                {
                    Thread.SpinWait(64);
                }

                now = Stopwatch.GetTimestamp();
            }

            _nextSwapTimestamp = Math.Max(target + _swapIntervalTicks, now);
        }
    }
}
