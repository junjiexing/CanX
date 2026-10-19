// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using CanX.App.ViewModels;
using CanX.Application.Automation;
using CanX.Application.Discovery;
using CanX.Application.Downloads;
using CanX.Core.Configuration;
using CanX.Core.Diagnostics;
using CanX.Core.Downloads;
using CanX.Infrastructure.Configuration;
using CanX.MediaAccess;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Runtime;
using CanX.Streaming;

namespace CanX.App.Automation;

internal sealed class DesktopAutomationService :
    IMediaAutomationService,
    IPluginAutomationService,
    ISettingsAutomationService,
    IDiagnosticsAutomationService,
    IUiAutomationService
{
    private const int MaximumBrowseItems = 500;
    private const int MaximumCachedHandles = 4096;
    private static readonly TimeSpan HandleLifetime = TimeSpan.FromMinutes(30);
    private readonly IMediaSourceProfileStore _mediaProfiles;
    private readonly PluginLifecycleManager _plugins;
    private readonly IApplicationSettingsStore _settings;
    private readonly IApplicationLog _log;
    private readonly Func<ConfiguredMediaPlaybackRequest, CancellationToken, Task> _play;
    private readonly Func<CancellationToken, Task> _reloadSettings;
    private readonly Func<CancellationToken, Task> _refreshPlugins;
    private readonly Func<CancellationToken, Task> _refreshMediaSources;
    private readonly Func<string> _uiContext;
    private readonly Func<string, CancellationToken, Task> _navigate;
    private readonly Func<string, string, CancellationToken, Task> _notify;
    private readonly Func<MediaSourceConnectionProfile, IMediaSourceClient> _clientFactory;
    private readonly Lock _handleGate = new();
    private readonly Dictionary<string, MediaHandle> _handles = new(StringComparer.Ordinal);

    public DesktopAutomationService(
        IMediaSourceProfileStore mediaProfiles,
        PluginLifecycleManager plugins,
        IApplicationSettingsStore settings,
        IApplicationLog log,
        Func<ConfiguredMediaPlaybackRequest, CancellationToken, Task> play,
        Func<CancellationToken, Task> reloadSettings,
        Func<CancellationToken, Task> refreshPlugins,
        Func<CancellationToken, Task> refreshMediaSources,
        Func<string> uiContext,
        Func<string, CancellationToken, Task> navigate,
        Func<string, string, CancellationToken, Task> notify,
        Func<MediaSourceConnectionProfile, IMediaSourceClient>? clientFactory = null)
    {
        _mediaProfiles = mediaProfiles ?? throw new ArgumentNullException(nameof(mediaProfiles));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _play = play ?? throw new ArgumentNullException(nameof(play));
        _reloadSettings = reloadSettings ?? throw new ArgumentNullException(nameof(reloadSettings));
        _refreshPlugins = refreshPlugins ?? throw new ArgumentNullException(nameof(refreshPlugins));
        _refreshMediaSources = refreshMediaSources ?? throw new ArgumentNullException(nameof(refreshMediaSources));
        _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _clientFactory = clientFactory ?? CreateClient;
    }

    public async ValueTask<JsonArray> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var profiles = await _mediaProfiles.ListProfilesAsync(cancellationToken).ConfigureAwait(false);
        return new JsonArray(profiles.Select(profile => (JsonNode?)new JsonObject
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
            ["protocol"] = ProtocolName(profile.Protocol),
        }).ToArray());
    }

    public async ValueTask<JsonObject> AddSourceAsync(
        string name,
        string protocol,
        string endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (name.Trim().Length > 100) throw new ArgumentException("Media source name is too long.", nameof(name));
        var sourceProtocol = ParseProtocol(protocol);
        Uri sourceEndpoint;
        if (sourceProtocol == MediaSourceProtocol.LocalFolder)
        {
            var path = Path.GetFullPath(endpoint.Trim());
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected media directory does not exist.");
            EnsureNoReparsePoint(path);
            sourceEndpoint = new Uri(Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar);
        }
        else
        {
            sourceEndpoint = ParseNetworkEndpoint(sourceProtocol, endpoint);
            await using var candidate = _clientFactory(new MediaSourceConnectionProfile
            {
                Id = "connection-test",
                Name = "connection-test",
                Protocol = sourceProtocol,
                Endpoint = sourceEndpoint,
            });
            _ = await candidate.BrowseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var existing = await _mediaProfiles.ListProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(profile => profile.Protocol == sourceProtocol && Uri.Compare(
                profile.Endpoint,
                sourceEndpoint,
                UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0))
        {
            throw new InvalidOperationException("The same media source is already configured.");
        }
        var created = new MediaSourceConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Protocol = sourceProtocol,
            Endpoint = sourceEndpoint,
        };
        await _mediaProfiles.SaveProfileAsync(created, cancellationToken).ConfigureAwait(false);
        await _refreshMediaSources(cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["id"] = created.Id,
            ["name"] = created.Name,
            ["protocol"] = ProtocolName(created.Protocol),
        };
    }

    public async ValueTask<bool> RemoveSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        var removed = await _mediaProfiles.DeleteProfileAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (removed) await _refreshMediaSources(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async ValueTask<JsonObject> BrowseAsync(
        string sourceId,
        string? location,
        CancellationToken cancellationToken)
    {
        var profile = await RequireProfileAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return profile.Protocol == MediaSourceProtocol.LocalFolder
            ? BrowseLocal(profile, location, cancellationToken)
            : await BrowseRemoteAsync(profile, location, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PlayAsync(
        string sourceId,
        string location,
        string? subtitleLocation,
        CancellationToken cancellationToken)
    {
        var profile = await RequireProfileAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var media = RequireHandle(sourceId, location, directory: false);
        if (!MediaFileClassifier.IsPlayable(media.Name))
        {
            throw new InvalidOperationException("The selected media handle is not a supported playable file.");
        }
        var subtitle = string.IsNullOrWhiteSpace(subtitleLocation)
            ? null
            : RequireHandle(sourceId, subtitleLocation, directory: false);
        if (subtitle is not null && !MediaFileClassifier.IsSubtitle(subtitle.Name))
        {
            throw new InvalidOperationException("The selected subtitle handle is not a supported subtitle file.");
        }

        ConfiguredMediaPlaybackRequest request;
        if (profile.Protocol == MediaSourceProtocol.LocalFolder)
        {
            EnsureRegularLocalFile(media.LocalPath!);
            if (subtitle is not null) EnsureRegularLocalFile(subtitle.LocalPath!);
            request = ConfiguredMediaPlaybackRequest.Local(
                media.Name,
                media.LocalPath!,
                subtitle?.LocalPath);
        }
        else
        {
            request = await CreateRemotePlaybackRequestAsync(
                profile,
                media,
                subtitle,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _play(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    public JsonArray List() => new(_plugins.Plugins.Select(status => (JsonNode?)new JsonObject
    {
        ["id"] = status.Manifest.Id,
        ["name"] = status.Manifest.Name,
        ["version"] = status.Manifest.Version,
        ["enabled"] = status.IsEnabled,
        ["requiresPermissionApproval"] = status.RequiresPermissionApproval,
        ["runtimeState"] = status.RuntimeState.ToString(),
        ["rollbackVersion"] = status.RollbackVersion,
        ["signature"] = status.Signature is null ? null : new JsonObject
        {
            ["keyId"] = status.Signature.KeyId,
            ["algorithm"] = status.Signature.Algorithm,
            ["trusted"] = status.Signature.IsTrusted,
        },
        ["permissions"] = new JsonArray(status.Manifest.Permissions.Select(permission => (JsonNode?)new JsonObject
        {
            ["capability"] = permission.Capability,
            ["scopes"] = new JsonArray(permission.Scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray()),
        }).ToArray()),
    }).ToArray());

    public async ValueTask EnableAsync(string pluginId, CancellationToken cancellationToken)
    {
        await _plugins.ApproveAndEnableAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _refreshPlugins(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisableAsync(string pluginId, CancellationToken cancellationToken)
    {
        await _plugins.DisableAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _refreshPlugins(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReloadAsync(string pluginId, CancellationToken cancellationToken)
    {
        await _plugins.ReloadAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _refreshPlugins(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RollbackAsync(string pluginId, CancellationToken cancellationToken)
    {
        await _plugins.RollbackAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _refreshPlugins(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UninstallAsync(string pluginId, CancellationToken cancellationToken)
    {
        await _plugins.UninstallAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _refreshPlugins(cancellationToken).ConfigureAwait(false);
    }

    public JsonObject Get(string section)
    {
        var settings = _settings.LoadAsync().AsTask().GetAwaiter().GetResult();
        return Section(settings, section);
    }

    public async ValueTask<JsonObject> SetAsync(
        string section,
        JsonObject values,
        CancellationToken cancellationToken)
    {
        var current = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var updated = ApplySection(current, section, values);
        await _settings.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await _reloadSettings(cancellationToken).ConfigureAwait(false);
        return Section(updated, section);
    }

    public JsonObject GetSnapshot() => new()
    {
        ["version"] = typeof(DesktopAutomationService).Assembly.GetName().Version?.ToString() ?? "unknown",
        ["os"] = RuntimeInformation.OSDescription,
        ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["loggingEnabled"] = _log.IsEnabled,
        ["pluginCount"] = _plugins.Plugins.Count,
    };

    public ValueTask<JsonArray> ReadLogAsync(
        string component,
        int maximumLines,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = component switch
        {
            "app" => _log.GetFilePath(LogComponent.App),
            "aria2" => _log.GetFilePath(LogComponent.Aria2),
            "mpv" => _log.GetFilePath(LogComponent.Mpv),
            "quickjs" => _log.GetFilePath(LogComponent.QuickJs),
            "tool-audit" => Path.Combine(_log.DirectoryPath, "tool-audit.jsonl"),
            "plugin-audit" => Path.Combine(_log.DirectoryPath, "plugin-audit.jsonl"),
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };
        var result = File.Exists(path)
            ? File.ReadLines(path)
                .TakeLast(Math.Clamp(maximumLines, 1, 500))
                .Select(static line => line.Length <= 8192 ? line : $"{line[..8192]}…")
                .Select(line => (JsonNode?)JsonValue.Create(line))
                .ToArray()
            : [];
        return ValueTask.FromResult(new JsonArray(result));
    }

    public JsonObject GetContext() => new() { ["page"] = _uiContext() };

    public async ValueTask NavigateAsync(string page, CancellationToken cancellationToken) =>
        await _navigate(page, cancellationToken).ConfigureAwait(false);

    public async ValueTask NotifyAsync(string message, string severity, CancellationToken cancellationToken) =>
        await _notify(message, severity, cancellationToken).ConfigureAwait(false);

    private JsonObject BrowseLocal(
        MediaSourceConnectionProfile profile,
        string? location,
        CancellationToken cancellationToken)
    {
        var directory = location is null
            ? Path.GetFullPath(profile.Endpoint.LocalPath)
            : RequireHandle(profile.Id, location, directory: true).LocalPath!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The selected media directory no longer exists.");
        EnsureNoReparsePoint(directory);
        var entries = new DirectoryInfo(directory)
            .EnumerateFileSystemInfos()
            .Where(static entry => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(static entry => entry is DirectoryInfo)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumBrowseItems + 1)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var truncated = entries.Length > MaximumBrowseItems;
        return BrowseResult(profile.Id, entries.Take(MaximumBrowseItems).Select(entry =>
        {
            var isDirectory = entry is DirectoryInfo;
            var length = entry is FileInfo file ? file.Length : (long?)null;
            var handle = AddHandle(new MediaHandle(
                profile.Id,
                entry.Name,
                isDirectory,
                entry.FullName,
                null,
                DateTimeOffset.UtcNow.Add(HandleLifetime)));
            return BrowseItem(handle, entry.Name, isDirectory, length, entry.LastWriteTimeUtc);
        }), truncated);
    }

    private async ValueTask<JsonObject> BrowseRemoteAsync(
        MediaSourceConnectionProfile profile,
        string? location,
        CancellationToken cancellationToken)
    {
        Uri? directory = null;
        if (location is not null)
        {
            var handle = RequireHandle(profile.Id, location, directory: true);
            directory = handle.Resource!.Uri;
        }
        await using var client = _clientFactory(profile);
        var resources = await client.BrowseAsync(directory, cancellationToken).ConfigureAwait(false);
        var entries = resources
            .OrderByDescending(static resource => resource.IsDirectory)
            .ThenBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumBrowseItems + 1)
            .ToArray();
        var truncated = entries.Length > MaximumBrowseItems;
        return BrowseResult(profile.Id, entries.Take(MaximumBrowseItems).Select(resource =>
        {
            var handle = AddHandle(new MediaHandle(
                profile.Id,
                resource.Name,
                resource.IsDirectory,
                null,
                resource,
                DateTimeOffset.UtcNow.Add(HandleLifetime)));
            return BrowseItem(handle, resource.Name, resource.IsDirectory, resource.Length, resource.LastModified);
        }), truncated);
    }

    private async ValueTask<ConfiguredMediaPlaybackRequest> CreateRemotePlaybackRequestAsync(
        MediaSourceConnectionProfile profile,
        MediaHandle media,
        MediaHandle? subtitle,
        CancellationToken cancellationToken)
    {
        var client = _clientFactory(profile);
        try
        {
            var mediaResource = await client.GetAsync(media.Resource!.Uri, cancellationToken).ConfigureAwait(false);
            var subtitleResource = subtitle is null
                ? null
                : await client.GetAsync(subtitle.Resource!.Uri, cancellationToken).ConfigureAwait(false);
            var session = new MediaSourceClientSession(client);
            var mediaLease = session.Acquire(mediaResource);
            var subtitleLease = subtitleResource is null ? null : session.Acquire(subtitleResource);
            session.Release();
            return ConfiguredMediaPlaybackRequest.Remote(media.Name, mediaLease, subtitleLease, subtitle?.Name);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<MediaSourceConnectionProfile> RequireProfileAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (sourceId.Length is 0 or > 128) throw new ArgumentException("Invalid media source ID.", nameof(sourceId));
        return await _mediaProfiles.LoadProfileAsync(sourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Media source '{sourceId}' was not found.");
    }

    private string AddHandle(MediaHandle value)
    {
        lock (_handleGate)
        {
            RemoveExpiredHandles();
            if (_handles.Count >= MaximumCachedHandles)
            {
                foreach (var key in _handles.OrderBy(static item => item.Value.ExpiresAt).Take(512).Select(static item => item.Key).ToArray())
                {
                    _handles.Remove(key);
                }
            }
            var id = Guid.NewGuid().ToString("N");
            _handles.Add(id, value);
            return id;
        }
    }

    private MediaHandle RequireHandle(string sourceId, string handleId, bool directory)
    {
        if (handleId.Length != 32) throw new ArgumentException("Invalid or expired media location handle.", nameof(handleId));
        lock (_handleGate)
        {
            RemoveExpiredHandles();
            if (!_handles.TryGetValue(handleId, out var handle) ||
                !string.Equals(handle.SourceId, sourceId, StringComparison.Ordinal) ||
                handle.IsDirectory != directory)
            {
                throw new ArgumentException("Invalid or expired media location handle.", nameof(handleId));
            }
            return handle;
        }
    }

    private void RemoveExpiredHandles()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _handles.Where(item => item.Value.ExpiresAt <= now).Select(static item => item.Key).ToArray())
        {
            _handles.Remove(id);
        }
    }

    private static JsonObject BrowseResult(string sourceId, IEnumerable<JsonNode?> entries, bool truncated) => new()
    {
        ["sourceId"] = sourceId,
        ["items"] = new JsonArray(entries.ToArray()),
        ["truncated"] = truncated,
    };

    private static JsonObject BrowseItem(
        string handle,
        string name,
        bool isDirectory,
        long? length,
        DateTimeOffset? modified) => new()
    {
        ["location"] = handle,
        ["name"] = name,
        ["kind"] = isDirectory
            ? "directory"
            : MediaFileClassifier.IsPlayable(name)
                ? "media"
                : MediaFileClassifier.IsSubtitle(name) ? "subtitle" : "file",
        ["length"] = length,
        ["modifiedAt"] = modified?.ToString("O"),
    };

    private static void EnsureRegularLocalFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new FileNotFoundException("The selected media file no longer exists or is a symbolic link.");
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Symbolic-link media directories are not available to Agent automation.");
        }
    }

    private static IMediaSourceClient CreateClient(MediaSourceConnectionProfile profile)
    {
        var credential = string.IsNullOrWhiteSpace(profile.UserName)
            ? null
            : new NetworkCredential(profile.UserName, profile.Password);
        return profile.Protocol switch
        {
            MediaSourceProtocol.WebDav => WebDavMediaSourceClient.Create(profile.Endpoint, credential),
            MediaSourceProtocol.Ftp => FtpMediaSourceClient.Create(profile.Endpoint, credential),
            MediaSourceProtocol.Smb => SmbMediaSourceClient.Create(profile.Endpoint, credential),
            _ => throw new NotSupportedException($"Unsupported automated media protocol: {profile.Protocol}."),
        };
    }

    private static string ProtocolName(MediaSourceProtocol protocol) => protocol switch
    {
        MediaSourceProtocol.LocalFolder => "local",
        MediaSourceProtocol.WebDav => "webdav",
        MediaSourceProtocol.Ftp => "ftp",
        MediaSourceProtocol.Smb => "smb",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    private static MediaSourceProtocol ParseProtocol(string value) => value.ToLowerInvariant() switch
    {
        "local" => MediaSourceProtocol.LocalFolder,
        "webdav" => MediaSourceProtocol.WebDav,
        "ftp" => MediaSourceProtocol.Ftp,
        "smb" => MediaSourceProtocol.Smb,
        _ => throw new ArgumentException("Supported media source protocols are local, webdav, ftp, and smb.", nameof(value)),
    };

    private static Uri ParseNetworkEndpoint(MediaSourceProtocol protocol, string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("Media source endpoint must be an absolute URI without credentials, query, or fragment.", nameof(value));
        }
        var schemeAllowed = protocol switch
        {
            MediaSourceProtocol.WebDav => endpoint.Scheme is "http" or "https",
            MediaSourceProtocol.Ftp => endpoint.Scheme is "ftp" or "ftps",
            MediaSourceProtocol.Smb => endpoint.Scheme == "smb",
            _ => false,
        };
        return schemeAllowed
            ? endpoint
            : throw new ArgumentException("Media source endpoint scheme does not match its protocol.", nameof(value));
    }

    private static JsonObject Section(ApplicationSettings settings, string section) => section switch
    {
        "general" => new JsonObject
        {
            ["colorTheme"] = settings.ColorTheme,
            ["downloadDirectory"] = settings.DownloadDirectory,
            ["enableLogging"] = settings.EnableLogging,
        },
        "agent" => new JsonObject
        {
            ["baseUrl"] = settings.Agent.BaseUrl,
            ["model"] = settings.Agent.Model,
            ["temperature"] = settings.Agent.Temperature,
            ["contextWindowTokens"] = settings.Agent.ContextWindowTokens,
            ["maximumDurationSeconds"] = settings.Agent.MaximumDurationSeconds,
            ["toolTimeoutSeconds"] = settings.Agent.ToolTimeoutSeconds,
            ["showToolCalls"] = settings.Agent.ShowToolCalls,
            ["autoApproveToolCalls"] = settings.Agent.AutoApproveToolCalls,
            ["apiKeyStorage"] = "system-credential-store",
        },
        "playback" => MpvSection(settings.Mpv),
        _ => throw new ArgumentException("Supported settings sections are general, agent, and playback.", nameof(section)),
    };

    // These settings decide the Agent approval policy, where its API key is sent, which code libmpv loads,
    // and where downloads are written. A single LocalWrite approval is not a sufficient guard, so automation can read
    // them but only the settings UI can change them.
    private static readonly string[] UserOnlySettings = ["downloadDirectory", "baseUrl", "contextWindowTokens", "advancedOptions", "autoApproveToolCalls"];

    private static ApplicationSettings ApplySection(ApplicationSettings current, string section, JsonObject values)
    {
        foreach (var property in values)
        {
            if (UserOnlySettings.Contains(property.Key, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Setting '{property.Key}' can only be changed by the user in CanX settings.");
            }
        }
        EnsureKnownProperties(values, section switch
        {
            "general" => ["colorTheme", "enableLogging"],
            "agent" => ["model", "temperature", "maximumDurationSeconds", "toolTimeoutSeconds"],
            "playback" => MpvPropertyNames,
            _ => throw new ArgumentException("Supported settings sections are general, agent, and playback.", nameof(section)),
        });
        return section switch
        {
            "general" => current with
            {
                ColorTheme = OptionalString(values, "colorTheme", current.ColorTheme, 16),
                EnableLogging = OptionalBoolean(values, "enableLogging", current.EnableLogging),
            },
            "agent" => current with { Agent = ApplyAgent(current.Agent, values) },
            "playback" => current with { Mpv = ApplyMpv(current.Mpv, values) },
            _ => current,
        };
    }

    private static AgentSettings ApplyAgent(AgentSettings value, JsonObject values)
    {
        var model = OptionalString(values, "model", value.Model, 200);
        var result = value with
        {
            Model = model,
            ContextWindowTokens = model == value.Model ? value.ContextWindowTokens : 0,
            Temperature = OptionalDouble(values, "temperature", value.Temperature, 0, 2),
            MaximumDurationSeconds = OptionalInt(values, "maximumDurationSeconds", value.MaximumDurationSeconds, 1, 3600),
            ToolTimeoutSeconds = OptionalInt(values, "toolTimeoutSeconds", value.ToolTimeoutSeconds, 1, 3600),
        };
        if (result.ToolTimeoutSeconds > result.MaximumDurationSeconds)
        {
            throw new ArgumentException("toolTimeoutSeconds cannot exceed maximumDurationSeconds.");
        }
        if (string.IsNullOrWhiteSpace(result.BaseUrl) != string.IsNullOrWhiteSpace(result.Model))
        {
            throw new ArgumentException("baseUrl and model must both be set or both be empty.");
        }
        if (result.BaseUrl.Length > 0)
        {
            new CanX.Agent.AgentProviderConfiguration
            {
                BaseUri = new Uri(result.BaseUrl, UriKind.Absolute),
                Model = result.Model,
                Temperature = result.Temperature,
                ContextWindowTokens = result.ContextWindowTokens,
            }.Validate();
        }
        return result;
    }

    private static JsonObject MpvSection(MpvSettings value) => new()
    {
        ["hardwareDecoding"] = value.HardwareDecoding,
        ["videoSync"] = value.VideoSync,
        ["deinterlace"] = value.Deinterlace,
        ["savePositionOnQuit"] = value.SavePositionOnQuit,
        ["resumePlayback"] = value.ResumePlayback,
        ["interpolation"] = value.Interpolation,
        ["scale"] = value.Scale,
        ["chromaScale"] = value.ChromaScale,
        ["downscale"] = value.Downscale,
        ["deband"] = value.Deband,
        ["sigmoidUpscaling"] = value.SigmoidUpscaling,
        ["targetColorspaceHint"] = value.TargetColorspaceHint,
        ["targetColorspaceHintMode"] = value.TargetColorspaceHintMode,
        ["targetPrimaries"] = value.TargetPrimaries,
        ["targetTransfer"] = value.TargetTransfer,
        ["targetPeak"] = value.TargetPeak,
        ["toneMapping"] = value.ToneMapping,
        ["gamutMappingMode"] = value.GamutMappingMode,
        ["hdrComputePeak"] = value.HdrComputePeak,
        ["audioDevice"] = value.AudioDevice,
        ["audioChannels"] = value.AudioChannels,
        ["audioExclusive"] = value.AudioExclusive,
        ["audioPitchCorrection"] = value.AudioPitchCorrection,
        ["audioLanguages"] = value.AudioLanguages,
        ["subtitleLanguages"] = value.SubtitleLanguages,
        ["subtitleAuto"] = value.SubtitleAuto,
        ["subtitleFont"] = value.SubtitleFont,
        ["subtitleFontSize"] = value.SubtitleFontSize,
        ["subtitleScaleWithWindow"] = value.SubtitleScaleWithWindow,
        ["cache"] = value.Cache,
        ["cacheSeconds"] = value.CacheSeconds,
        ["demuxerMaxBytesMiB"] = value.DemuxerMaxBytesMiB,
        ["demuxerMaxBackBytesMiB"] = value.DemuxerMaxBackBytesMiB,
        ["cachePause"] = value.CachePause,
        ["cachePauseInitial"] = value.CachePauseInitial,
        ["cachePauseWaitSeconds"] = value.CachePauseWaitSeconds,
        ["networkTimeoutSeconds"] = value.NetworkTimeoutSeconds,
        ["advancedOptions"] = value.AdvancedOptions,
    };

    private static MpvSettings ApplyMpv(MpvSettings value, JsonObject values) => value with
    {
        HardwareDecoding = OptionalString(values, "hardwareDecoding", value.HardwareDecoding, 64),
        VideoSync = OptionalString(values, "videoSync", value.VideoSync, 64),
        Deinterlace = OptionalString(values, "deinterlace", value.Deinterlace, 16),
        SavePositionOnQuit = OptionalBoolean(values, "savePositionOnQuit", value.SavePositionOnQuit),
        ResumePlayback = OptionalBoolean(values, "resumePlayback", value.ResumePlayback),
        Interpolation = OptionalBoolean(values, "interpolation", value.Interpolation),
        Scale = OptionalString(values, "scale", value.Scale, 64),
        ChromaScale = OptionalString(values, "chromaScale", value.ChromaScale, 64),
        Downscale = OptionalString(values, "downscale", value.Downscale, 64),
        Deband = OptionalBoolean(values, "deband", value.Deband),
        SigmoidUpscaling = OptionalBoolean(values, "sigmoidUpscaling", value.SigmoidUpscaling),
        TargetColorspaceHint = OptionalString(values, "targetColorspaceHint", value.TargetColorspaceHint, 32),
        TargetColorspaceHintMode = OptionalString(values, "targetColorspaceHintMode", value.TargetColorspaceHintMode, 32),
        TargetPrimaries = OptionalString(values, "targetPrimaries", value.TargetPrimaries, 32),
        TargetTransfer = OptionalString(values, "targetTransfer", value.TargetTransfer, 32),
        TargetPeak = OptionalString(values, "targetPeak", value.TargetPeak, 32),
        ToneMapping = OptionalString(values, "toneMapping", value.ToneMapping, 32),
        GamutMappingMode = OptionalString(values, "gamutMappingMode", value.GamutMappingMode, 32),
        HdrComputePeak = OptionalString(values, "hdrComputePeak", value.HdrComputePeak, 32),
        AudioDevice = OptionalString(values, "audioDevice", value.AudioDevice, 512),
        AudioChannels = OptionalString(values, "audioChannels", value.AudioChannels, 64),
        AudioExclusive = OptionalBoolean(values, "audioExclusive", value.AudioExclusive),
        AudioPitchCorrection = OptionalBoolean(values, "audioPitchCorrection", value.AudioPitchCorrection),
        AudioLanguages = OptionalString(values, "audioLanguages", value.AudioLanguages, 512),
        SubtitleLanguages = OptionalString(values, "subtitleLanguages", value.SubtitleLanguages, 512),
        SubtitleAuto = OptionalString(values, "subtitleAuto", value.SubtitleAuto, 32),
        SubtitleFont = OptionalString(values, "subtitleFont", value.SubtitleFont, 256),
        SubtitleFontSize = OptionalString(values, "subtitleFontSize", value.SubtitleFontSize, 32),
        SubtitleScaleWithWindow = OptionalBoolean(values, "subtitleScaleWithWindow", value.SubtitleScaleWithWindow),
        Cache = OptionalString(values, "cache", value.Cache, 16),
        CacheSeconds = OptionalString(values, "cacheSeconds", value.CacheSeconds, 32),
        DemuxerMaxBytesMiB = OptionalString(values, "demuxerMaxBytesMiB", value.DemuxerMaxBytesMiB, 32),
        DemuxerMaxBackBytesMiB = OptionalString(values, "demuxerMaxBackBytesMiB", value.DemuxerMaxBackBytesMiB, 32),
        CachePause = OptionalBoolean(values, "cachePause", value.CachePause),
        CachePauseInitial = OptionalBoolean(values, "cachePauseInitial", value.CachePauseInitial),
        CachePauseWaitSeconds = OptionalString(values, "cachePauseWaitSeconds", value.CachePauseWaitSeconds, 32),
        NetworkTimeoutSeconds = OptionalString(values, "networkTimeoutSeconds", value.NetworkTimeoutSeconds, 32),
    };

    private static readonly string[] MpvPropertyNames =
    [
        "hardwareDecoding", "videoSync", "deinterlace", "savePositionOnQuit", "resumePlayback", "interpolation",
        "scale", "chromaScale", "downscale", "deband", "sigmoidUpscaling", "targetColorspaceHint",
        "targetColorspaceHintMode", "targetPrimaries", "targetTransfer", "targetPeak", "toneMapping",
        "gamutMappingMode", "hdrComputePeak", "audioDevice", "audioChannels", "audioExclusive",
        "audioPitchCorrection", "audioLanguages", "subtitleLanguages", "subtitleAuto", "subtitleFont",
        "subtitleFontSize", "subtitleScaleWithWindow", "cache", "cacheSeconds", "demuxerMaxBytesMiB",
        "demuxerMaxBackBytesMiB", "cachePause", "cachePauseInitial", "cachePauseWaitSeconds",
        "networkTimeoutSeconds",
    ];

    private static void EnsureKnownProperties(JsonObject values, IReadOnlyCollection<string> names)
    {
        foreach (var property in values)
        {
            if (!names.Contains(property.Key, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unsupported setting '{property.Key}'.");
            }
        }
    }

    private static string OptionalString(JsonObject values, string name, string fallback, int maximumLength)
    {
        if (!values.TryGetPropertyValue(name, out var node)) return fallback;
        var result = node?.GetValue<string>() ?? throw new ArgumentException($"'{name}' must be a string.");
        if (result.Length > maximumLength) throw new ArgumentException($"'{name}' is too long.");
        return result;
    }

    private static bool OptionalBoolean(JsonObject values, string name, bool fallback) =>
        values.TryGetPropertyValue(name, out var node)
            ? node?.GetValue<bool>() ?? throw new ArgumentException($"'{name}' must be a boolean.")
            : fallback;

    private static int OptionalInt(JsonObject values, string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetPropertyValue(name, out var node)) return fallback;
        var result = node?.GetValue<int>() ?? throw new ArgumentException($"'{name}' must be an integer.");
        return result is >= 0 && result >= minimum && result <= maximum
            ? result
            : throw new ArgumentOutOfRangeException(name);
    }

    private static double OptionalDouble(JsonObject values, string name, double fallback, double minimum, double maximum)
    {
        if (!values.TryGetPropertyValue(name, out var node)) return fallback;
        var result = node?.GetValue<double>() ?? throw new ArgumentException($"'{name}' must be a number.");
        return double.IsFinite(result) && result >= minimum && result <= maximum
            ? result
            : throw new ArgumentOutOfRangeException(name);
    }

    private sealed record MediaHandle(
        string SourceId,
        string Name,
        bool IsDirectory,
        string? LocalPath,
        MediaResource? Resource,
        DateTimeOffset ExpiresAt);
}

internal sealed class ConfiguredMediaPlaybackRequest : IDisposable
{
    private ConfiguredMediaPlaybackRequest(
        string name,
        string? localPath,
        string? localSubtitlePath,
        MediaSourceReadLease? mediaLease,
        MediaSourceReadLease? subtitleLease,
        string? subtitleName)
    {
        Name = name;
        LocalPath = localPath;
        LocalSubtitlePath = localSubtitlePath;
        MediaLease = mediaLease;
        SubtitleLease = subtitleLease;
        SubtitleName = subtitleName;
    }

    public string Name { get; }
    public string? LocalPath { get; }
    public string? LocalSubtitlePath { get; }
    public MediaSourceReadLease? MediaLease { get; }
    public MediaSourceReadLease? SubtitleLease { get; }
    public string? SubtitleName { get; }
    public bool IsLocal => LocalPath is not null;

    public static ConfiguredMediaPlaybackRequest Local(string name, string path, string? subtitlePath) =>
        new(name, path, subtitlePath, null, null, null);

    public static ConfiguredMediaPlaybackRequest Remote(
        string name,
        MediaSourceReadLease mediaLease,
        MediaSourceReadLease? subtitleLease,
        string? subtitleName) =>
        new(name, null, null, mediaLease, subtitleLease, subtitleName);

    public void Dispose()
    {
        MediaLease?.Dispose();
        SubtitleLease?.Dispose();
    }
}

internal sealed class DesktopSearchActionService(
    IDownloadManager downloads,
    Func<string> downloadDirectory,
    Func<Uri, Uri?, CancellationToken, Task> play) : ISearchActionService
{
    private readonly IDownloadManager _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
    private readonly Func<string> _downloadDirectory = downloadDirectory ?? throw new ArgumentNullException(nameof(downloadDirectory));
    private readonly Func<Uri, Uri?, CancellationToken, Task> _play = play ?? throw new ArgumentNullException(nameof(play));

    public async ValueTask PlayResolvedAsync(Uri media, Uri? subtitle, CancellationToken cancellationToken) =>
        await _play(media, subtitle, cancellationToken).ConfigureAwait(false);

    public async ValueTask DownloadResolvedAsync(string source, CancellationToken cancellationToken)
    {
        await _downloads.AddAsync(new AddDownloadRequest
        {
            Kind = DownloadSourceKind.Uri,
            Source = source,
            DestinationDirectory = Path.GetFullPath(_downloadDirectory()),
        }, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class DenyingPluginToolDispatcher : IPluginToolDispatcher
{
    public ValueTask<PluginToolDispatchResult> InvokeAsync(
        PluginToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PluginToolDispatchResult.Failure(
            "{\"code\":\"workspace_tool_denied\",\"message\":\"Tools are disabled during workspace tests.\"}"));
    }
}

internal sealed class BlockedPluginNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Network access is disabled during workspace tests."),
            RequestMessage = request,
        });
    }
}
