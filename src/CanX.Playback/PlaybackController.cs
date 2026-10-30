// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Application.Playback;
using CanX.Core.Downloads;
using CanX.Core.Events;
using CanX.Core.Playback;
using CanX.Playback.Engine;
using CanX.Streaming;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CanX.Playback;

public sealed partial class PlaybackController : IPlaybackController
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);
    private const int RecentItemLimit = 100;
    private const int PlaylistItemLimit = 10_000;
    private const int BookmarkLimit = 5_000;
    private const int BookmarkPerSourceLimit = 500;
    private const int FilterTextLimit = 32_768;
    private const int ShaderLimit = 256;
    private const int RuntimeValueLimit = 32_768;
    private const int StateSaveRefreshTicks = 25;
    private const int DiagnosticsRefreshTicks = 5;

    // FFmpeg source/subtitle/command filters open files named in their arguments.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_-])(?<name>movie|amovie|subtitles|ass|sendcmd|asendcmd|zmq|azmq)(?![A-Za-z0-9_-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileReadingFilterPattern();

    private static readonly HashSet<string> TargetColorspaceHintValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "yes", "no",
    };

    private static readonly HashSet<string> TargetColorspaceHintModeValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "target", "source", "source-dynamic",
    };

    private static readonly HashSet<string> TargetPrimariesValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "bt.470m", "bt.601-525", "bt.601-625", "bt.709", "bt.2020", "apple", "adobe",
        "prophoto", "cie1931", "dci-p3", "display-p3", "v-gamut", "s-gamut",
    };

    private static readonly HashSet<string> TargetTransferValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "bt.1886", "srgb", "linear", "gamma1.8", "gamma2.0", "gamma2.2", "gamma2.4",
        "gamma2.6", "gamma2.8", "prophoto", "pq", "hlg", "v-log", "s-log1", "s-log2", "scrgb",
    };

    private static readonly HashSet<string> ToneMappingValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "clip", "mobius", "reinhard", "hable", "bt.2390", "gamma", "linear", "spline",
        "bt.2446a", "st2094-40", "st2094-10",
    };

    private static readonly HashSet<string> GamutMappingValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "clip", "perceptual", "relative", "saturation", "absolute", "desaturate", "darken",
        "warn", "linear",
    };

    private static readonly HashSet<string> MediaFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".aac", ".ac3", ".aiff", ".ape", ".asf", ".avi", ".flac", ".flv",
        ".m2ts", ".m4a", ".m4v", ".mka", ".mkv", ".mov", ".mp2", ".mp3", ".mp4",
        ".mpeg", ".mpg", ".mts", ".oga", ".ogg", ".ogm", ".ogv", ".opus", ".ts",
        ".wav", ".webm", ".wma", ".wmv",
    };

    private readonly IPlaybackEngine _engine;
    private readonly IEventBus _eventBus;
    private readonly IPlaybackStateStore _stateStore;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly List<string> _playlistSources = [];
    private readonly List<PlaybackRecentItem> _recentItems = [];
    private readonly List<PlaybackBookmark> _bookmarks = [];
    private PlaybackSnapshot _snapshot = new();
    private PlaybackCapabilityManifest _capabilities = new();
    private Task? _refreshLoop;
    private int _trackRefreshCountdown;
    private int _playlistRefreshCountdown;
    private int _stateSaveCountdown = StateSaveRefreshTicks;
    private int _recentCaptureCountdown = StateSaveRefreshTicks;
    private int _diagnosticsRefreshCountdown;
    private int _selectedPlaylistIndex = -1;
    private HistoryIdentity? _recentCaptureSuppressedIdentity;
    private readonly Dictionary<string, HistoryIdentity> _activeHistoryIdentities = new(SourceComparer.Instance);
    private string? _resumeAppliedSource;
    private string? _pendingBookmarkSource;
    private double? _pendingBookmarkPosition;
    private bool _resumePlaybackEnabled = true;
    private bool _stateDirty;
    private IReadOnlyDictionary<string, string> _startupOptions =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private int _started;
    private int _disposed;

    public PlaybackController(
        IPlaybackEngine engine,
        IEventBus eventBus,
        IPlaybackStateStore? stateStore = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _stateStore = stateStore ?? NullPlaybackStateStore.Instance;
    }

    public bool IsStarted => Volatile.Read(ref _started) == 1;

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public PlaybackCapabilityManifest Capabilities => Volatile.Read(ref _capabilities);

    public void ConfigureStartupOptions(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (IsStarted)
        {
            throw new InvalidOperationException("mpv startup options cannot be changed after playback has started.");
        }

        _startupOptions = new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
        _resumePlaybackEnabled = !_startupOptions.TryGetValue("resume-playback", out var resume) ||
            !string.Equals(resume, "no", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var persistedState = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _playlistSources.Clear();
            _playlistSources.AddRange(persistedState.PlaylistSources.Take(PlaylistItemLimit));
            _selectedPlaylistIndex = _playlistSources.Count == 0
                ? -1
                : Math.Clamp(persistedState.SelectedPlaylistIndex, 0, _playlistSources.Count - 1);
            _recentItems.Clear();
            _recentItems.AddRange(persistedState.RecentItems
                .Where(static item => ShouldPersistSource(item.Source))
                .Take(RecentItemLimit));
            _bookmarks.Clear();
            _bookmarks.AddRange(persistedState.Bookmarks.Take(BookmarkLimit));
            _engine.Initialize(_startupOptions);
            Volatile.Write(ref _capabilities, PlaybackCapabilityPolicy.CreateManifest(_engine.GetCapabilities()));
            Publish(Snapshot with
            {
                Volume = ReadDouble("volume", 100),
                Brightness = ReadDouble("brightness", 0),
                Speed = ReadDouble("speed", 1),
                IsSubtitleVisible = ReadFlag("sub-visibility", fallback: true),
                SubtitleDelaySeconds = ReadDouble("sub-delay", 0),
                SubtitleFontSize = ReadDouble("sub-font-size", 38),
                SubtitleScale = ReadDouble("sub-scale", 1),
                SubtitlePosition = ReadDouble("sub-pos", 100),
                AudioDevice = ReadString("audio-device", "auto"),
                AudioDevices = _engine.GetAudioDevices().ToList(),
                VideoFilters = ReadString("vf", string.Empty),
                AudioFilters = ReadString("af", string.Empty),
                Shaders = _engine.GetShaders().ToList(),
                ColorSettings = ReadColorSettings(new PlaybackColorSettings()),
                Playlist = BuildPlaylistSnapshot(markCurrent: false),
                CurrentPlaylistIndex = -1,
                RecentItems = _recentItems.ToList(),
                Bookmarks = [],
            });
            _refreshLoop = Task.Run(() => RunRefreshLoopAsync(_shutdown.Token), CancellationToken.None);
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    public async ValueTask OpenAsync(string source, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSource(source);
        var playlist = await FindLocalVideoSiblingsAsync(normalized, cancellationToken).ConfigureAwait(false);
        var startIndex = playlist.Count == 1 ? 0 : playlist.FindIndex(path => PathsEqual(path, normalized));
        await RunSerializedAsync(
            () => OpenPlaylistCore(playlist, startIndex < 0 ? 0 : startIndex),
            cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask OpenMediaSourceAsync(
        string playbackSource,
        string historySource,
        string? mediaSourceProfileId = null,
        CancellationToken cancellationToken = default) =>
        OpenMediaSourcePlaylistAsync(
            [new PlaybackMediaSource(playbackSource, historySource)],
            mediaSourceProfileId: mediaSourceProfileId,
            cancellationToken: cancellationToken);

    public async ValueTask OpenMediaSourcePlaylistAsync(
        IReadOnlyList<PlaybackMediaSource> sources,
        int startIndex = 0,
        string? mediaSourceProfileId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var normalized = NormalizeSources(sources.Select(static source => source.PlaybackSource).ToArray());
        if (startIndex < 0 || startIndex >= normalized.Count)
            throw new ArgumentOutOfRangeException(nameof(startIndex));

        var identities = new Dictionary<string, HistoryIdentity>(SourceComparer.Instance);
        for (var index = 0; index < sources.Count; index++)
        {
            if (!Uri.TryCreate(normalized[index], UriKind.Absolute, out var playbackUri) ||
                playbackUri.Scheme != MediaStreamBroker.Scheme ||
                !Uri.TryCreate(sources[index].HistorySource, UriKind.Absolute, out var historyUri) ||
                historyUri.Scheme is not ("http" or "https" or "ftp" or "ftps" or "smb"))
                throw new ArgumentException("网络媒体历史需要有效的媒体流和原始媒体地址。");

            identities.Add(normalized[index], new HistoryIdentity(
                historyUri.AbsoluteUri, IsMediaSource: true, MediaSourceProfileId: mediaSourceProfileId));
        }

        await RunSerializedAsync(
            () => OpenPlaylistCore(normalized, startIndex, historyIdentities: identities),
            cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OpenPlaylistAsync(
        IReadOnlyList<string> sources,
        int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSources(sources);
        if (startIndex < 0 || startIndex >= normalized.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        await RunSerializedAsync(
            () => OpenPlaylistCore(normalized, startIndex),
            cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddToPlaylistAsync(
        IReadOnlyList<string> sources,
        bool playNow = false,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSources(sources);
        await RunSerializedAsync(() =>
        {
            if (_playlistSources.Count + normalized.Count > PlaylistItemLimit)
            {
                throw new InvalidOperationException($"播放列表最多包含 {PlaylistItemLimit} 项。");
            }

            if (playNow)
            {
                var combined = _playlistSources.Concat(normalized).ToList();
                OpenPlaylistCore(combined, _playlistSources.Count);
                return;
            }

            if (!string.IsNullOrWhiteSpace(Snapshot.Source) && EnginePlaylistMatches())
            {
                foreach (var source in normalized)
                {
                    LoadFile(source, "append");
                }
            }

            _playlistSources.AddRange(normalized);
            if (_selectedPlaylistIndex < 0 && _playlistSources.Count > 0)
            {
                _selectedPlaylistIndex = 0;
            }

            _stateDirty = true;
            Publish(Snapshot with
            {
                Playlist = BuildPlaylistSnapshot(markCurrent: !string.IsNullOrWhiteSpace(Snapshot.Source)),
                CurrentPlaylistIndex = string.IsNullOrWhiteSpace(Snapshot.Source) ? -1 : _selectedPlaylistIndex,
            });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OpenFolderAsync(
        string directory,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"媒体目录不存在：{fullPath}");
        }

        var files = await Task.Run(() => Directory
                .EnumerateFiles(
                    fullPath,
                    "*",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(static path => MediaFileExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Take(PlaylistItemLimit + 1)
                .ToList(),
            cancellationToken).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("所选目录中没有支持的媒体文件。");
        }

        if (files.Count > PlaylistItemLimit)
        {
            throw new InvalidOperationException($"所选目录包含超过 {PlaylistItemLimit} 个媒体文件，请缩小范围。");
        }

        await OpenPlaylistAsync(files, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PlayPlaylistItemAsync(int index, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            ValidatePlaylistIndex(index);
            if (index == _selectedPlaylistIndex &&
                (Snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused) &&
                EnginePlaylistMatches())
            {
                return;
            }

            if (EnginePlaylistMatches())
            {
                CaptureRecent(Snapshot, completed: false);
                _selectedPlaylistIndex = index;
                _resumeAppliedSource = null;
                _engine.Command("set", "playlist-pos", index.ToString(CultureInfo.InvariantCulture));
                if (ReadFlag("pause")) _engine.Command("set", "pause", "no");
                Publish(CreateLoadingSnapshot(_playlistSources[index], index));
                _stateDirty = true;
            }
            else
            {
                OpenPlaylistCore(
                    _playlistSources.ToList(),
                    index,
                    historyIdentities: new Dictionary<string, HistoryIdentity>(
                        _activeHistoryIdentities, SourceComparer.Instance));
            }
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask PlayNextAsync(CancellationToken cancellationToken = default) =>
        PlayAdjacentAsync(1, cancellationToken);

    public ValueTask PlayPreviousAsync(CancellationToken cancellationToken = default) =>
        PlayAdjacentAsync(-1, cancellationToken);

    public ValueTask SetAutoPlayNextAsync(bool autoPlayNext, CancellationToken cancellationToken = default) =>
        RunSerializedAsync(() =>
            _engine.Command("set", "keep-open", autoPlayNext ? "yes" : "always"), cancellationToken);

    public async ValueTask RemovePlaylistItemAsync(int index, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            ValidatePlaylistIndex(index);
            var engineMatches = EnginePlaylistMatches();
            var removingCurrent = index == _selectedPlaylistIndex && !string.IsNullOrWhiteSpace(Snapshot.Source);
            if (engineMatches)
            {
                if (_playlistSources.Count == 1)
                {
                    CaptureRecent(Snapshot, completed: false);
                    _engine.Command("stop");
                    _engine.Command("playlist-clear");
                }
                else
                {
                    _engine.Command("playlist-remove", index.ToString(CultureInfo.InvariantCulture));
                }
            }

            _playlistSources.RemoveAt(index);
            _selectedPlaylistIndex = AdjustSelectedIndexAfterRemoval(index);
            _stateDirty = true;
            if (_playlistSources.Count == 0)
            {
                Publish(Snapshot with
                {
                    State = PlaybackState.Idle,
                    Source = string.Empty,
                    Title = string.Empty,
                    PositionSeconds = 0,
                    DurationSeconds = 0,
                    Playlist = [],
                    CurrentPlaylistIndex = -1,
                    Tracks = [],
                    Chapters = [],
                    CurrentChapterIndex = -1,
                    Bookmarks = [],
                });
            }
            else
            {
                Publish(Snapshot with
                {
                    State = removingCurrent && engineMatches ? PlaybackState.Loading : Snapshot.State,
                    Playlist = BuildPlaylistSnapshot(markCurrent: !string.IsNullOrWhiteSpace(Snapshot.Source)),
                    CurrentPlaylistIndex = string.IsNullOrWhiteSpace(Snapshot.Source) ? -1 : _selectedPlaylistIndex,
                });
            }
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MovePlaylistItemAsync(
        int index,
        int newIndex,
        CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            ValidatePlaylistIndex(index);
            ValidatePlaylistIndex(newIndex);
            if (index == newIndex)
            {
                return;
            }

            if (EnginePlaylistMatches())
            {
                _engine.Command(
                    "playlist-move",
                    index.ToString(CultureInfo.InvariantCulture),
                    MpvPlaylistMoveTarget(index, newIndex).ToString(CultureInfo.InvariantCulture));
            }

            var source = _playlistSources[index];
            _playlistSources.RemoveAt(index);
            _playlistSources.Insert(newIndex, source);
            _selectedPlaylistIndex = AdjustSelectedIndexAfterMove(index, newIndex);
            _stateDirty = true;
            Publish(Snapshot with
            {
                Playlist = BuildPlaylistSnapshot(markCurrent: !string.IsNullOrWhiteSpace(Snapshot.Source)),
                CurrentPlaylistIndex = string.IsNullOrWhiteSpace(Snapshot.Source) ? -1 : _selectedPlaylistIndex,
            });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearPlaylistAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            var hasCurrent = !string.IsNullOrWhiteSpace(Snapshot.Source) &&
                _selectedPlaylistIndex >= 0 && _selectedPlaylistIndex < _playlistSources.Count;
            if (EnginePlaylistMatches())
            {
                _engine.Command("playlist-clear");
            }

            if (hasCurrent)
            {
                var currentSource = Snapshot.Source;
                _playlistSources.Clear();
                _playlistSources.Add(currentSource);
                _selectedPlaylistIndex = 0;
            }
            else
            {
                _playlistSources.Clear();
                _selectedPlaylistIndex = -1;
            }

            _stateDirty = true;
            Publish(Snapshot with
            {
                Playlist = BuildPlaylistSnapshot(markCurrent: hasCurrent),
                CurrentPlaylistIndex = hasCurrent ? 0 : -1,
            });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OpenRecentAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var recent = Snapshot.RecentItems.FirstOrDefault(item =>
                !item.IsMediaSource && SourcesEqual(item.Source, source.Trim()))
            ?? throw new KeyNotFoundException("最近播放记录不存在。");
        await OpenAsync(recent.Source, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask RemoveRecentAsync(string source, CancellationToken cancellationToken = default) =>
        RemoveRecentCoreAsync(source, null, null, cancellationToken);

    public ValueTask RemoveRecentAsync(
        string source,
        bool isMediaSource,
        string? mediaSourceProfileId,
        CancellationToken cancellationToken = default) =>
        RemoveRecentCoreAsync(source, isMediaSource, mediaSourceProfileId, cancellationToken);

    private async ValueTask RemoveRecentCoreAsync(
        string source,
        bool? isMediaSource,
        string? mediaSourceProfileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        await RunSerializedAsync(() =>
        {
            var normalizedSource = source.Trim();
            var removed = _recentItems.RemoveAll(item =>
                SourcesEqual(item.Source, normalizedSource) &&
                (isMediaSource is null ||
                 item.IsMediaSource == isMediaSource &&
                 string.Equals(item.MediaSourceProfileId, mediaSourceProfileId, StringComparison.Ordinal)));
            if (removed == 0)
            {
                throw new KeyNotFoundException("最近播放记录不存在。");
            }

            var currentIdentity = GetHistoryIdentity(Snapshot.Source);
            if (Snapshot.State is (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended) &&
                SourcesEqual(currentIdentity.Source, normalizedSource) &&
                (isMediaSource is null ||
                 currentIdentity.IsMediaSource == isMediaSource &&
                 string.Equals(currentIdentity.MediaSourceProfileId, mediaSourceProfileId, StringComparison.Ordinal)))
            {
                _recentCaptureSuppressedIdentity = currentIdentity;
            }

            _stateDirty = true;
            Publish(Snapshot with { RecentItems = _recentItems.ToList() });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearRecentAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            _recentItems.Clear();
            _recentCaptureSuppressedIdentity = Snapshot.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended
                ? GetHistoryIdentity(Snapshot.Source)
                : null;
            _stateDirty = true;
            Publish(Snapshot with { RecentItems = [] });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SeekToChapterAsync(int index, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            var chapter = Snapshot.Chapters.FirstOrDefault(item => item.Index == index)
                ?? throw new ArgumentOutOfRangeException(nameof(index), "章节索引超出范围。");
            _engine.Command("set", "chapter", index.ToString(CultureInfo.InvariantCulture));
            Publish(Snapshot with
            {
                PositionSeconds = chapter.TimeSeconds,
                Chapters = Snapshot.Chapters
                    .Select(item => item with { IsCurrent = item.Index == index })
                    .ToList(),
                CurrentChapterIndex = index,
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddBookmarkAsync(
        string? title = null,
        double? positionSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = NormalizeBookmarkTitle(title);
        if (positionSeconds is { } requestedPosition &&
            (!double.IsFinite(requestedPosition) || requestedPosition < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(positionSeconds), "书签位置不能小于零。");
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            if (!Snapshot.IsSeekable)
            {
                throw new InvalidOperationException("当前媒体不可定位，无法添加书签。");
            }

            if (_bookmarks.Count >= BookmarkLimit)
            {
                throw new InvalidOperationException($"书签总数不能超过 {BookmarkLimit} 个。");
            }

            var sourceBookmarkCount = _bookmarks.Count(item => SourcesEqual(item.Source, Snapshot.Source));
            if (sourceBookmarkCount >= BookmarkPerSourceLimit)
            {
                throw new InvalidOperationException($"每个媒体最多可添加 {BookmarkPerSourceLimit} 个书签。");
            }

            var position = positionSeconds ?? Snapshot.PositionSeconds;
            if (Snapshot.DurationSeconds > 0 && position > Snapshot.DurationSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(positionSeconds), "书签位置不能超过媒体时长。");
            }

            _bookmarks.Add(new PlaybackBookmark
            {
                Id = Guid.NewGuid().ToString("N"),
                Source = Snapshot.Source,
                Title = normalizedTitle ?? $"书签 {FormatPlaybackTime(position)}",
                PositionSeconds = position,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            _stateDirty = true;
            Publish(Snapshot with { Bookmarks = BuildBookmarksForSource(Snapshot.Source) });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OpenBookmarkAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await RunSerializedAsync(() =>
        {
            var bookmark = FindBookmark(id);
            if (SourcesEqual(bookmark.Source, Snapshot.Source) &&
                Snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused)
            {
                if (!Snapshot.IsSeekable)
                {
                    throw new InvalidOperationException("当前媒体不可定位，无法跳转到书签。");
                }

                _engine.Command(
                    "seek",
                    bookmark.PositionSeconds.ToString("R", CultureInfo.InvariantCulture),
                    "absolute+exact");
                Publish(Snapshot with { PositionSeconds = bookmark.PositionSeconds });
                return;
            }

            var source = NormalizeSource(bookmark.Source);
            OpenCore(source);
            _pendingBookmarkSource = source;
            _pendingBookmarkPosition = bookmark.PositionSeconds;
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RenameBookmarkAsync(
        string id,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var normalizedTitle = NormalizeBookmarkTitle(title)
            ?? throw new ArgumentException("书签名称不能为空。", nameof(title));
        await RunSerializedAsync(() =>
        {
            var bookmark = FindBookmark(id);
            var index = _bookmarks.IndexOf(bookmark);
            _bookmarks[index] = bookmark with { Title = normalizedTitle };
            _stateDirty = true;
            Publish(Snapshot with { Bookmarks = BuildBookmarksForSource(Snapshot.Source) });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveBookmarkAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await RunSerializedAsync(() =>
        {
            var bookmark = FindBookmark(id);
            _bookmarks.Remove(bookmark);
            _stateDirty = true;
            Publish(Snapshot with { Bookmarks = BuildBookmarksForSource(Snapshot.Source) });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearBookmarksAsync(
        bool all = false,
        CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            if (all)
            {
                _bookmarks.Clear();
            }
            else
            {
                EnsureMediaLoaded();
                _bookmarks.RemoveAll(item => SourcesEqual(item.Source, Snapshot.Source));
            }

            _stateDirty = true;
            Publish(Snapshot with { Bookmarks = BuildBookmarksForSource(Snapshot.Source) });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() => SetPausedCore(paused), cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask TogglePauseAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            SetPausedCore(Snapshot.State is PlaybackState.Playing or PlaybackState.Loading);
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            CaptureRecent(Snapshot, completed: false);
            _engine.Command("stop");
            Publish(Snapshot with
            {
                State = PlaybackState.Idle,
                PositionSeconds = 0,
                ErrorMessage = string.Empty,
                RecentItems = _recentItems.ToList(),
            });
        }, cancellationToken).ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SeekAsync(
        double seconds,
        bool relative = false,
        bool exact = true,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || (!relative && seconds < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command(
                "seek",
                seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                $"{(relative ? "relative" : "absolute")}+{(exact ? "exact" : "keyframes")}");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StepFrameAsync(
        bool backward = false,
        CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command(backward ? "frame-back-step" : "frame-step");
            Publish(Snapshot with { State = PlaybackState.Paused, ErrorMessage = string.Empty });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAbLoopAsync(
        double? startSeconds = null,
        double? endSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (startSeconds is null && endSeconds is null)
        {
            throw new ArgumentException("At least one A-B loop point is required.");
        }

        ValidateMediaPosition(startSeconds, nameof(startSeconds));
        ValidateMediaPosition(endSeconds, nameof(endSeconds));
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            var start = startSeconds ?? Snapshot.AbLoopASeconds;
            var end = endSeconds ?? Snapshot.AbLoopBSeconds;
            if (start is { } a && end is { } b && a >= b)
            {
                throw new ArgumentException("A-B 循环的 A 点必须早于 B 点。");
            }

            if (Snapshot.DurationSeconds > 0 &&
                (startSeconds > Snapshot.DurationSeconds || endSeconds > Snapshot.DurationSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    startSeconds > Snapshot.DurationSeconds ? nameof(startSeconds) : nameof(endSeconds),
                    "A-B 循环点不能超过媒体时长。");
            }

            if (startSeconds is { } requestedStart)
            {
                _engine.Command("set", "ab-loop-a", requestedStart.ToString("R", CultureInfo.InvariantCulture));
            }

            if (endSeconds is { } requestedEnd)
            {
                _engine.Command("set", "ab-loop-b", requestedEnd.ToString("R", CultureInfo.InvariantCulture));
            }

            Publish(Snapshot with
            {
                AbLoopASeconds = start,
                AbLoopBSeconds = end,
                ErrorMessage = string.Empty,
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAbLoopAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("set", "ab-loop-a", "no");
            _engine.Command("set", "ab-loop-b", "no");
            Publish(Snapshot with
            {
                AbLoopASeconds = null,
                AbLoopBSeconds = null,
                ErrorMessage = string.Empty,
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<string> TakeScreenshotAsync(
        string? outputPath = null,
        PlaybackScreenshotMode mode = PlaybackScreenshotMode.Subtitles,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            var path = PrepareScreenshotPath(outputPath);
            _engine.Command("screenshot-to-file", path, mode switch
            {
                PlaybackScreenshotMode.Video => "video",
                PlaybackScreenshotMode.Subtitles => "subtitles",
                PlaybackScreenshotMode.Window => "window",
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            });
            return path;
        }, cancellationToken);
    }

    public async ValueTask SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 130)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 130.");
        }

        await RunSerializedAsync(() =>
        {
            _engine.Command("set", "volume", volume.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Publish(Snapshot with { Volume = volume });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetBrightnessAsync(double brightness, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(brightness) || brightness is < -100 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness), "Brightness must be between -100 and 100.");
        }

        await RunSerializedAsync(() =>
        {
            _engine.Command("set", "brightness", brightness.ToString("R", CultureInfo.InvariantCulture));
            Publish(Snapshot with { Brightness = brightness });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            _engine.Command("set", "mute", muted ? "yes" : "no");
            Publish(Snapshot with { IsMuted = muted });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetSpeedAsync(double speed, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(speed) || speed is < 0.1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be between 0.1 and 4.");
        }

        await RunSerializedAsync(() =>
        {
            _engine.Command("set", "speed", speed.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Publish(Snapshot with { Speed = speed });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SelectAudioDeviceAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 1024 || normalized.Contains('\0'))
        {
            throw new ArgumentException("音频设备名称无效。", nameof(name));
        }

        await RunSerializedAsync(() =>
        {
            var devices = _engine.GetAudioDevices();
            if (!string.Equals(normalized, "auto", StringComparison.Ordinal) &&
                !devices.Any(device => string.Equals(device.Name, normalized, StringComparison.Ordinal)))
            {
                throw new ArgumentException("指定的音频设备不可用。", nameof(name));
            }

            _engine.Command("set", "audio-device", normalized);
            Publish(Snapshot with
            {
                AudioDevice = normalized,
                AudioDevices = devices
                    .Select(device => device with
                    {
                        IsCurrent = string.Equals(device.Name, normalized, StringComparison.Ordinal),
                    })
                    .ToList(),
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SetVideoFiltersAsync(string filters, CancellationToken cancellationToken = default) =>
        SetFiltersAsync("vf", filters, cancellationToken);

    public ValueTask SetAudioFiltersAsync(string filters, CancellationToken cancellationToken = default) =>
        SetFiltersAsync("af", filters, cancellationToken);

    public async ValueTask AddShaderAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeShaderPath(path);
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            if (Snapshot.Shaders.Any(shader => PathsEqual(shader, normalized)))
            {
                throw new InvalidOperationException("该 Shader 已经加载。");
            }

            if (Snapshot.Shaders.Count >= ShaderLimit)
            {
                throw new InvalidOperationException($"最多可加载 {ShaderLimit} 个 Shader。");
            }

            _engine.Command("change-list", "glsl-shaders", "append", normalized);
            Publish(Snapshot with { Shaders = ReadShadersOrFallback([.. Snapshot.Shaders, normalized]) });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveShaderAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path.Trim());
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            var existing = Snapshot.Shaders.FirstOrDefault(shader => PathsEqual(shader, normalized))
                ?? throw new KeyNotFoundException("指定的 Shader 尚未加载。");
            _engine.Command("change-list", "glsl-shaders", "remove", existing);
            Publish(Snapshot with
            {
                Shaders = ReadShadersOrFallback(
                    Snapshot.Shaders.Where(shader => !PathsEqual(shader, existing)).ToList()),
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearShadersAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("change-list", "glsl-shaders", "clr", string.Empty);
            Publish(Snapshot with { Shaders = [] });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetColorSettingsAsync(
        PlaybackColorSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = NormalizeColorSettings(settings);
        await RunSerializedAsync(() =>
        {
            SetRuntimePropertyCore("target-colorspace-hint", normalized.TargetColorspaceHint);
            SetRuntimePropertyCore("target-colorspace-hint-mode", normalized.TargetColorspaceHintMode);
            SetRuntimePropertyCore("target-prim", normalized.TargetPrimaries);
            SetRuntimePropertyCore("target-trc", normalized.TargetTransfer);
            SetRuntimePropertyCore("target-peak", normalized.TargetPeak);
            SetRuntimePropertyCore("tone-mapping", normalized.ToneMapping);
            SetRuntimePropertyCore("gamut-mapping-mode", normalized.GamutMappingMode);
            SetRuntimePropertyCore("hdr-compute-peak", normalized.HdrComputePeak);
            Publish(Snapshot with { ColorSettings = ReadColorSettings(normalized) });
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<string> GetRuntimePropertyAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRuntimePropertyName(name);
        EnsureRuntimePropertyReadable(normalized);
        return RunSerializedAsync(() =>
        {
            if (!_engine.TryGetString(normalized, out var value))
            {
                throw new KeyNotFoundException($"mpv 属性不可用：{normalized}");
            }

            return value;
        }, cancellationToken);
    }

    public async ValueTask SetRuntimePropertyAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeRuntimePropertyName(name);
        EnsureRuntimePropertyWritable(normalizedName);
        var normalizedValue = NormalizeRuntimeValue(value);
        await RunSerializedAsync(() => SetRuntimePropertyCore(normalizedName, normalizedValue), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask ExecuteCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var normalizedCommand = NormalizeRuntimeCommand(command);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > 64)
        {
            throw new ArgumentException("mpv 命令最多包含 64 个参数。", nameof(arguments));
        }

        var normalizedArguments = arguments.Select(NormalizeRuntimeValue).ToArray();
        if (normalizedArguments.Sum(static argument => argument.Length) > RuntimeValueLimit)
        {
            throw new ArgumentException($"mpv 命令参数总长度不能超过 {RuntimeValueLimit} 个字符。", nameof(arguments));
        }

        if (normalizedArguments.Any(static argument => argument.Contains("${", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("高级 mpv 命令参数不能展开 property；请通过受限的属性读取入口获取值。");
        }

        await RunSerializedAsync(
            () => _engine.Command([normalizedCommand, .. normalizedArguments]),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SetFiltersAsync(
        string property,
        string filters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Length > FilterTextLimit || filters.Contains('\0'))
        {
            throw new ArgumentException($"滤镜表达式不能超过 {FilterTextLimit} 个字符且不能包含 NUL。", nameof(filters));
        }

        var normalized = filters.Trim();
        if (FileReadingFilterPattern().Match(normalized) is { Success: true } match)
        {
            throw new ArgumentException(
                $"滤镜 {match.Groups["name"].Value} 会读取任意文件，不能用于播放滤镜。",
                nameof(filters));
        }
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command(property, string.IsNullOrEmpty(normalized) ? "clr" : "set", normalized);
            Publish(property == "vf"
                ? Snapshot with { VideoFilters = normalized }
                : Snapshot with { AudioFilters = normalized });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SelectTrackAsync(
        PlaybackTrackType type,
        long? trackId,
        CancellationToken cancellationToken = default,
        bool remember = true)
    {
        var property = type switch
        {
            PlaybackTrackType.Video => "vid",
            PlaybackTrackType.Audio => "aid",
            PlaybackTrackType.Subtitle => "sid",
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Only video, audio, and subtitle tracks can be selected."),
        };
        if (trackId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId), "Track identifiers must be positive.");
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            var tracks = _engine.GetTracks();
            if (trackId is { } id && !tracks.Any(track => track.Id == id && track.Type == type))
            {
                throw new ArgumentException($"Track {id} is not an available {type} track.", nameof(trackId));
            }

            _engine.Command("set", property, trackId?.ToString(CultureInfo.InvariantCulture) ?? "no");
            var updatedTracks = _engine.GetTracks().ToList();
            Publish(Snapshot with { Tracks = updatedTracks });
            if (remember) RememberSelectedTrack(type, trackId, updatedTracks);
        }, cancellationToken).ConfigureAwait(false);
        if (remember) await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RestoreVideoOutputAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId), "Track identifiers must be positive.");
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("set", "vid", trackId.ToString(CultureInfo.InvariantCulture));
            Publish(Snapshot with { Tracks = _engine.GetTracks().ToList() });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddSubtitleAsync(
        string source,
        bool select = true,
        CancellationToken cancellationToken = default,
        PlaybackExternalSubtitleSource? persistentSource = null,
        bool remember = true)
    {
        var normalized = NormalizeSubtitleSource(source);
        persistentSource ??= ShouldPersistSource(normalized)
            ? new PlaybackExternalSubtitleSource { Source = normalized } : null;
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("sub-add", normalized, select ? "select" : "auto");
            if (persistentSource is { Source: { Length: > 0 } } stable &&
                ShouldPersistSource(stable.Source))
                _externalSubtitleSources[normalized] = stable;
            var updatedTracks = _engine.GetTracks().ToList();
            Publish(Snapshot with { Tracks = updatedTracks });
            if (remember) RememberAddedSubtitle(normalized, select, updatedTracks);
        }, cancellationToken).ConfigureAwait(false);
        if (remember) await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetSubtitleVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("set", "sub-visibility", visible ? "yes" : "no");
            Publish(Snapshot with { IsSubtitleVisible = visible });
            RememberCurrentPreferences(preferences => preferences with { SubtitleVisible = visible });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds is < -3600 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "Subtitle delay must be between -3600 and 3600 seconds.");
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("set", "sub-delay", seconds.ToString("R", CultureInfo.InvariantCulture));
            Publish(Snapshot with { SubtitleDelaySeconds = seconds });
            RememberCurrentPreferences(preferences => preferences with { SubtitleDelaySeconds = seconds });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetSubtitleFontSizeAsync(double size, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(size) || size is < 1 or > 9000)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Subtitle font size must be between 1 and 9000.");
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("set", "sub-font-size", size.ToString("R", CultureInfo.InvariantCulture));
            Publish(Snapshot with { SubtitleFontSize = size });
            RememberCurrentPreferences(preferences => preferences with { SubtitleFontSize = size });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetSubtitleScaleAsync(double scale, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(scale) || scale is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Subtitle scale must be between 0 and 100.");
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            // sub-font-size only changes the default text style. Scaling also
            // handles ASS script/inline font sizes and bitmap subtitles, without
            // replacing the script's font, colors or other style fields.
            if (ReadString("sub-ass-override", "scale") is "no" or "yes")
                _engine.Command("set", "sub-ass-override", "scale");
            // Newer mpv versions can scale positioned ASS text as well. Older
            // versions lack this option, so only set it when mpv reports it.
            if (_engine.TryGetFlag("sub-scale-signs", out _))
                _engine.Command("set", "sub-scale-signs", "yes");
            _engine.Command("set", "sub-scale", scale.ToString("R", CultureInfo.InvariantCulture));
            Publish(Snapshot with { SubtitleScale = scale });
            RememberCurrentPreferences(preferences => preferences with { SubtitleScale = scale });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetSubtitlePositionAsync(double position, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(position) || position is < 0 or > 150)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Subtitle position must be between 0 and 150.");
        }

        await RunSerializedAsync(() =>
        {
            EnsureMediaLoaded();
            _engine.Command("set", "sub-pos", position.ToString("R", CultureInfo.InvariantCulture));
            Publish(Snapshot with { SubtitlePosition = position });
            RememberCurrentPreferences(preferences => preferences with { SubtitlePosition = position });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendKeyDownAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateInputKey(key);
        await RunSerializedAsync(() => _engine.Command("keydown", key), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendKeyUpAsync(string? key = null, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(
            () =>
            {
                if (string.IsNullOrEmpty(key))
                {
                    _engine.Command("keyup");
                }
                else
                {
                    _engine.Command("keyup", key);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendKeyPressAsync(
        string key,
        double scale = 1,
        CancellationToken cancellationToken = default)
    {
        ValidateInputKey(key);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        await RunSerializedAsync(
            () => _engine.Command("keypress", key, scale.ToString("R", CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetMousePositionAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(
            () => _engine.Command(
                "mouse",
                x.ToString(CultureInfo.InvariantCulture),
                y.ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteWatchLaterAsync(CancellationToken cancellationToken = default)
    {
        await RunSerializedAsync(() =>
        {
            CaptureRecent(Snapshot, completed: false);
            _engine.Command("write-watch-later-config");
        }, cancellationToken)
            .ConfigureAwait(false);
        await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        if (_refreshLoop is not null)
        {
            try
            {
                await _refreshLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CaptureRecent(Snapshot, completed: Snapshot.State == PlaybackState.Ended);
            _engine.Dispose();
            Volatile.Write(ref _started, 0);
        }
        finally
        {
            _operationGate.Release();
        }

        await PersistStateIfDirtyAsync(CancellationToken.None).ConfigureAwait(false);
        _persistenceGate.Dispose();
        _shutdown.Dispose();
    }

    private async ValueTask RunSerializedAsync(Action operation, CancellationToken cancellationToken)
    {
        EnsureStarted();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                SynchronizeSharedHistory();
                try { operation(); }
                finally { SynchronizeSharedHistory(); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<T> RunSerializedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        EnsureStarted();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                SynchronizeSharedHistory();
                try { return operation(); }
                finally { SynchronizeSharedHistory(); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var saveState = false;
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Refresh();
                if (_stateDirty && --_stateSaveCountdown <= 0)
                {
                    _stateSaveCountdown = StateSaveRefreshTicks;
                    saveState = true;
                }
            }
            catch (Exception exception)
            {
                Publish(Snapshot with
                {
                    State = PlaybackState.Error,
                    ErrorMessage = exception.Message,
                });
            }
            finally
            {
                _operationGate.Release();
            }

            if (saveState)
            {
                try
                {
                    await PersistStateIfDirtyAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The state remains dirty and will be retried without stopping playback.
                }
            }
        }
    }

    private void OpenCore(string normalized)
    {
        OpenPlaylistCore([normalized], 0);
    }

    private static Task<List<string>> FindLocalVideoSiblingsAsync(string source, CancellationToken cancellationToken)
    {
        if (HasMpvProtocol(source) || !ExternalSubtitleMatcher.IsVideoFile(source))
            return Task.FromResult(new List<string> { source });

        return Task.Run(() =>
        {
            try
            {
                var directory = Path.GetDirectoryName(source);
                if (directory is null) return new List<string> { source };
                var siblings = Directory.EnumerateFiles(directory)
                    .Where(ExternalSubtitleMatcher.IsVideoFile)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static path => path, StringComparer.Ordinal)
                    .Take(PlaylistItemLimit + 1)
                    .ToList();
                if (siblings.Count > PlaylistItemLimit)
                    throw new InvalidOperationException($"目录包含超过 {PlaylistItemLimit} 个视频，请缩小范围。");
                if (siblings.Any(path => PathsEqual(path, source)))
                    return siblings;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Opening the selected video must still work if sibling discovery fails.
            }

            return new List<string> { source };
        }, cancellationToken);
    }

    private void LoadFile(string source, string mode)
    {
        if (ShouldDisableCacheForBrokerSource(source))
        {
            _engine.Command("loadfile", source, mode, "-1", "cache=no");
            return;
        }

        if (ShouldEnableCacheForBrokerSource(source))
        {
            _engine.Command("loadfile", source, mode, "-1", "cache=yes");
            return;
        }

        _engine.Command("loadfile", source, mode);
    }

    private bool ShouldEnableCacheForBrokerSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, MediaStreamBroker.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Query) &&
        (!_startupOptions.TryGetValue("cache", out var cache) ||
         string.Equals(cache, "auto", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldDisableCacheForBrokerSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, MediaStreamBroker.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Query, "?cache=no", StringComparison.Ordinal);

    private void OpenPlaylistCore(
        IReadOnlyList<string> normalized,
        int startIndex,
        HistoryIdentity? historyIdentity = null,
        IReadOnlyDictionary<string, HistoryIdentity>? historyIdentities = null)
    {
        CaptureRecent(Snapshot, completed: Snapshot.State == PlaybackState.Ended);
        _externalSubtitleSources.Clear();
        _activeHistoryIdentities.Clear();
        if (historyIdentities is not null)
        {
            foreach (var (source, identity) in historyIdentities)
                _activeHistoryIdentities[source] = identity;
        }
        else if (historyIdentity is not null)
        {
            _activeHistoryIdentities[normalized[startIndex]] = historyIdentity;
        }
        _playlistSources.Clear();
        _playlistSources.AddRange(normalized);
        _selectedPlaylistIndex = startIndex;
        _resumeAppliedSource = null;
        _pendingBookmarkSource = null;
        _pendingBookmarkPosition = null;
        _stateDirty = true;
        Publish(CreateLoadingSnapshot(normalized[startIndex], startIndex));
        try
        {
            LoadFile(normalized[startIndex], "replace");
            for (var index = 0; index < normalized.Count; index++)
            {
                if (index == startIndex) continue;
                LoadFile(normalized[index], "append");
            }

            if (startIndex > 0)
            {
                _engine.Command("playlist-move", "0",
                    MpvPlaylistMoveTarget(0, startIndex).ToString(CultureInfo.InvariantCulture));
            }

            // mpv retains pause across stop/loadfile; opening media should start playback.
            if (ReadFlag("pause")) _engine.Command("set", "pause", "no");
        }
        catch (Exception exception)
        {
            Publish(Snapshot with
            {
                State = PlaybackState.Error,
                ErrorMessage = exception.Message,
            });
            throw;
        }
    }

    // mpv moves an entry before the original target entry, so forward moves
    // require the index after the desired final position (or count for the end).
    private static int MpvPlaylistMoveTarget(int fromIndex, int destinationIndex) =>
        fromIndex < destinationIndex ? destinationIndex + 1 : destinationIndex;

    private PlaybackSnapshot CreateLoadingSnapshot(string source, int playlistIndex) => Snapshot with
    {
        State = PlaybackState.Loading,
        Source = source,
        Title = GetDisplayName(source),
        PositionSeconds = 0,
        DurationSeconds = 0,
        VideoWidth = 0,
        VideoHeight = 0,
        ErrorMessage = string.Empty,
        Tracks = [],
        Chapters = [],
        CurrentChapterIndex = -1,
        Bookmarks = BuildBookmarksForSource(source),
        AbLoopASeconds = null,
        AbLoopBSeconds = null,
        Diagnostics = new PlaybackDiagnostics(),
        Playlist = BuildPlaylistSnapshot(markCurrent: true),
        CurrentPlaylistIndex = playlistIndex,
        RecentItems = _recentItems.ToList(),
    };

    private void SetPausedCore(bool paused)
    {
        EnsureMediaLoaded();
        if (Snapshot.State is PlaybackState.Idle or PlaybackState.Ended or PlaybackState.Error)
        {
            if (!paused)
            {
                if (_playlistSources.Count > 0 && _selectedPlaylistIndex >= 0)
                {
                    OpenPlaylistCore(
                        _playlistSources.ToList(),
                        _selectedPlaylistIndex,
                        historyIdentities: new Dictionary<string, HistoryIdentity>(
                            _activeHistoryIdentities, SourceComparer.Instance));
                }
                else
                {
                    OpenPlaylistCore([Snapshot.Source], 0, GetActiveHistoryIdentity(Snapshot.Source));
                }
            }

            return;
        }

        _engine.Command("set", "pause", paused ? "yes" : "no");
        var updated = Snapshot with
        {
            State = paused ? PlaybackState.Paused : PlaybackState.Playing,
            ErrorMessage = string.Empty,
        };
        if (paused)
        {
            CaptureRecent(updated, completed: false);
            updated = updated with { RecentItems = _recentItems.ToList() };
        }

        Publish(updated);
    }

    private static void ValidateInputKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("An mpv input key cannot be empty.", nameof(key));
        }
    }

    private void Refresh()
    {
        SynchronizeSharedHistory();
        var current = Snapshot;
        var state = current.State;
        var errorMessage = current.ErrorMessage;
        var refreshTracks = false;
        var refreshPlaylist = false;
        var refreshDiagnostics = false;
        var fileLoaded = false;
        while (true)
        {
            var engineEvent = _engine.PollEvent();
            if (engineEvent.Kind == PlaybackEngineEventKind.None)
            {
                break;
            }

            switch (engineEvent.Kind)
            {
                case PlaybackEngineEventKind.StartFile:
                    _recentCaptureSuppressedIdentity = null;
                    state = PlaybackState.Loading;
                    errorMessage = string.Empty;
                    refreshPlaylist = true;
                    break;
                case PlaybackEngineEventKind.FileLoaded:
                    state = ReadFlag("pause") ? PlaybackState.Paused : PlaybackState.Playing;
                    refreshTracks = true;
                    refreshPlaylist = true;
                    fileLoaded = true;
                    break;
                case PlaybackEngineEventKind.EndFile when engineEvent.IsError:
                    CaptureRecent(current, completed: false);
                    state = PlaybackState.Error;
                    errorMessage = engineEvent.ErrorMessage;
                    break;
                case PlaybackEngineEventKind.EndFile:
                    CaptureRecent(current, completed: true);
                    state = PlaybackState.Ended;
                    break;
                case PlaybackEngineEventKind.Stopped:
                    CaptureRecent(current, completed: false);
                    state = PlaybackState.Idle;
                    break;
                case PlaybackEngineEventKind.Shutdown:
                    state = PlaybackState.Idle;
                    break;
            }
        }

        var idle = ReadFlag("idle-active", fallback: true);
        if (!idle && state is not (PlaybackState.Loading or PlaybackState.Ended or PlaybackState.Error))
        {
            state = ReadFlag("pause") ? PlaybackState.Paused : PlaybackState.Playing;
        }
        else if (idle && state is PlaybackState.Playing or PlaybackState.Paused)
        {
            state = PlaybackState.Idle;
        }

        if (!idle && (state is PlaybackState.Playing or PlaybackState.Paused) &&
            ReadFlag("eof-reached"))
        {
            CaptureRecent(current, completed: true);
            state = PlaybackState.Ended;
        }

        if (state is PlaybackState.Playing or PlaybackState.Paused)
        {
            if (refreshTracks)
            {
                _trackRefreshCountdown = 5;
            }
            else if (--_trackRefreshCountdown <= 0)
            {
                refreshTracks = true;
                _trackRefreshCountdown = 5;
            }

            if (refreshPlaylist)
            {
                _playlistRefreshCountdown = 5;
            }
            else if (--_playlistRefreshCountdown <= 0)
            {
                refreshPlaylist = true;
                _playlistRefreshCountdown = 5;
            }

            if (fileLoaded || --_diagnosticsRefreshCountdown <= 0)
            {
                refreshDiagnostics = true;
                _diagnosticsRefreshCountdown = DiagnosticsRefreshTicks;
            }
        }
        else
        {
            _trackRefreshCountdown = 0;
            _playlistRefreshCountdown = 0;
            _diagnosticsRefreshCountdown = 0;
        }

        var source = current.Source;
        if ((state is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused) &&
            _engine.TryGetString("path", out var engineSource) &&
            !string.IsNullOrWhiteSpace(engineSource))
        {
            source = NormalizeEngineSource(engineSource);
        }

        if (!string.IsNullOrWhiteSpace(current.Source) && !SourcesEqual(current.Source, source))
        {
            CaptureRecent(current, completed: false);
            _resumeAppliedSource = null;
            _externalSubtitleSources.Clear();
        }

        var playlist = current.Playlist;
        if (refreshPlaylist)
        {
            var enginePlaylist = _engine.GetPlaylist();
            if (enginePlaylist.Count > 0)
            {
                var newSources = enginePlaylist.Select(static item => item.Source).ToList();
                var previousIndex = _selectedPlaylistIndex;
                _playlistSources.Clear();
                _playlistSources.AddRange(newSources);
                var currentItem = enginePlaylist.FirstOrDefault(static item => item.IsCurrent);
                _selectedPlaylistIndex = currentItem?.Index ??
                    (int)Math.Clamp(ReadInt64("playlist-pos", _selectedPlaylistIndex), 0, enginePlaylist.Count - 1);
                playlist = enginePlaylist
                    .Select(item => item with { IsCurrent = item.Index == _selectedPlaylistIndex })
                    .ToList();
                _stateDirty |= previousIndex != _selectedPlaylistIndex ||
                    !current.Playlist.Select(static item => item.Source).SequenceEqual(newSources, SourceComparer.Instance);
            }
        }

        var duration = Math.Max(0, ReadDouble("duration", current.DurationSeconds));
        var position = Math.Clamp(ReadDouble("time-pos", current.PositionSeconds), 0, Math.Max(duration, 0));
        var title = ReadString("media-title", current.Title);
        if (fileLoaded &&
            _pendingBookmarkPosition is { } bookmarkPosition &&
            SourcesEqual(_pendingBookmarkSource, source))
        {
            var target = Math.Clamp(bookmarkPosition, 0, duration > 0 ? duration : double.MaxValue);
            _engine.Command("seek", target.ToString("R", CultureInfo.InvariantCulture), "absolute+exact");
            position = target;
            _resumeAppliedSource = source;
            _pendingBookmarkSource = null;
            _pendingBookmarkPosition = null;
        }
        else if (fileLoaded && TryGetResumePosition(source, duration, out var resumePosition) &&
            !SourcesEqual(_resumeAppliedSource, source))
        {
            if (Math.Abs(position - resumePosition) > 1)
            {
                _engine.Command("seek", resumePosition.ToString("R", CultureInfo.InvariantCulture), "absolute+exact");
            }

            position = resumePosition;
            _resumeAppliedSource = source;
        }

        if (fileLoaded) RestoreMediaPreferences(source);
        var tracks = refreshTracks ? _engine.GetTracks().ToList() : current.Tracks;
        if (tracks.SequenceEqual(current.Tracks))
        {
            tracks = current.Tracks;
        }

        var chapters = refreshTracks ? _engine.GetChapters().ToList() : current.Chapters;
        if (chapters.SequenceEqual(current.Chapters))
        {
            chapters = current.Chapters;
        }

        var audioDevice = ReadString("audio-device", current.AudioDevice);
        var audioDevices = refreshTracks || current.AudioDevices.Count == 0
            ? _engine.GetAudioDevices()
                .Select(device => device with
                {
                    IsCurrent = string.Equals(device.Name, audioDevice, StringComparison.Ordinal),
                })
                .ToList()
            : current.AudioDevices;
        if (audioDevices.SequenceEqual(current.AudioDevices))
        {
            audioDevices = current.AudioDevices;
        }

        var shaders = refreshTracks ? _engine.GetShaders().ToList() : current.Shaders;
        if (shaders.SequenceEqual(current.Shaders, PathComparer.Instance))
        {
            shaders = current.Shaders;
        }

        var bookmarks = SourcesEqual(current.Source, source)
            ? current.Bookmarks
            : BuildBookmarksForSource(source);
        var currentChapterIndex = chapters.FirstOrDefault(static chapter => chapter.IsCurrent)?.Index ?? -1;
        var diagnostics = refreshDiagnostics ? ReadDiagnostics() : current.Diagnostics;
        var colorSettings = refreshDiagnostics ? ReadColorSettings(current.ColorSettings) : current.ColorSettings;
        if (state is not (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Loading))
        {
            diagnostics = new PlaybackDiagnostics();
        }

        var updated = current with
        {
            State = state,
            Source = source,
            Title = title,
            PositionSeconds = position,
            DurationSeconds = duration,
            Volume = Math.Clamp(ReadDouble("volume", current.Volume), 0, 130),
            Brightness = Math.Clamp(ReadDouble("brightness", current.Brightness), -100, 100),
            IsMuted = ReadFlag("mute", current.IsMuted),
            Speed = Math.Clamp(ReadDouble("speed", current.Speed), 0.1, 4),
            IsSubtitleVisible = ReadFlag("sub-visibility", current.IsSubtitleVisible),
            SubtitleDelaySeconds = Math.Clamp(ReadDouble("sub-delay", current.SubtitleDelaySeconds), -3600, 3600),
            SubtitleFontSize = Math.Clamp(ReadDouble("sub-font-size", current.SubtitleFontSize), 1, 9000),
            SubtitleScale = Math.Clamp(ReadDouble("sub-scale", current.SubtitleScale), 0, 100),
            SubtitlePosition = Math.Clamp(ReadDouble("sub-pos", current.SubtitlePosition), 0, 150),
            AbLoopASeconds = ReadOptionalDouble("ab-loop-a"),
            AbLoopBSeconds = ReadOptionalDouble("ab-loop-b"),
            AudioDevice = audioDevice,
            AudioDevices = audioDevices,
            VideoFilters = ReadRawString("vf", current.VideoFilters),
            AudioFilters = ReadRawString("af", current.AudioFilters),
            Shaders = shaders,
            ColorSettings = colorSettings,
            Diagnostics = diagnostics,
            IsSeekable = ReadFlag("seekable", current.IsSeekable),
            VideoWidth = (int)Math.Clamp(
                ReadInt64("video-params/dw", ReadInt64("video-params/w", current.VideoWidth)),
                0,
                int.MaxValue),
            VideoHeight = (int)Math.Clamp(
                ReadInt64("video-params/dh", ReadInt64("video-params/h", current.VideoHeight)),
                0,
                int.MaxValue),
            ErrorMessage = errorMessage,
            Tracks = tracks,
            Chapters = chapters,
            CurrentChapterIndex = currentChapterIndex,
            Bookmarks = bookmarks,
            Playlist = playlist,
            CurrentPlaylistIndex = string.IsNullOrWhiteSpace(source) ? -1 : _selectedPlaylistIndex,
            RecentItems = _recentItems.ToList(),
        };

        if (fileLoaded)
        {
            CaptureRecent(updated, completed: false);
            _recentCaptureCountdown = StateSaveRefreshTicks;
        }
        else if (state == PlaybackState.Playing)
        {
            if (--_recentCaptureCountdown <= 0)
            {
                CaptureRecent(updated, completed: false);
                _recentCaptureCountdown = StateSaveRefreshTicks;
            }
        }
        else
        {
            _recentCaptureCountdown = StateSaveRefreshTicks;
        }

        updated = updated with { RecentItems = _recentItems.ToList() };
        Publish(updated);
    }

    private async ValueTask PlayAdjacentAsync(int offset, CancellationToken cancellationToken)
    {
        var target = Snapshot.CurrentPlaylistIndex + offset;
        if (target < 0 || target >= Snapshot.Playlist.Count)
        {
            throw new InvalidOperationException(offset > 0 ? "已经是播放列表最后一项。" : "已经是播放列表第一项。");
        }

        await PlayPlaylistItemAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private bool EnginePlaylistMatches()
    {
        if (string.IsNullOrWhiteSpace(Snapshot.Source))
        {
            return false;
        }

        var enginePlaylist = _engine.GetPlaylist();
        return enginePlaylist.Count == _playlistSources.Count &&
            enginePlaylist.Select(static item => item.Source).SequenceEqual(_playlistSources, SourceComparer.Instance);
    }

    private void ValidatePlaylistIndex(int index)
    {
        if (index < 0 || index >= _playlistSources.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "播放列表索引超出范围。");
        }
    }

    private int AdjustSelectedIndexAfterRemoval(int removedIndex)
    {
        if (_playlistSources.Count == 0)
        {
            return -1;
        }

        if (removedIndex < _selectedPlaylistIndex)
        {
            return _selectedPlaylistIndex - 1;
        }

        if (removedIndex == _selectedPlaylistIndex)
        {
            return Math.Min(removedIndex, _playlistSources.Count - 1);
        }

        return Math.Clamp(_selectedPlaylistIndex, 0, _playlistSources.Count - 1);
    }

    private int AdjustSelectedIndexAfterMove(int oldIndex, int newIndex)
    {
        if (_selectedPlaylistIndex == oldIndex)
        {
            return newIndex;
        }

        if (oldIndex < _selectedPlaylistIndex && newIndex >= _selectedPlaylistIndex)
        {
            return _selectedPlaylistIndex - 1;
        }

        if (oldIndex > _selectedPlaylistIndex && newIndex <= _selectedPlaylistIndex)
        {
            return _selectedPlaylistIndex + 1;
        }

        return _selectedPlaylistIndex;
    }

    private List<PlaybackPlaylistItem> BuildPlaylistSnapshot(bool markCurrent) =>
        _playlistSources.Select((source, index) => new PlaybackPlaylistItem
        {
            Index = index,
            Source = source,
            Title = GetDisplayName(source),
            IsCurrent = markCurrent && index == _selectedPlaylistIndex,
        }).ToList();

    private List<PlaybackBookmark> BuildBookmarksForSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        return _bookmarks
            .Where(item => SourcesEqual(item.Source, source))
            .OrderBy(static item => item.PositionSeconds)
            .ThenBy(static item => item.CreatedAt)
            .ToList();
    }

    private PlaybackBookmark FindBookmark(string id) =>
        _bookmarks.FirstOrDefault(item => string.Equals(item.Id, id.Trim(), StringComparison.Ordinal))
        ?? throw new KeyNotFoundException("书签不存在。");

    private static string? NormalizeBookmarkTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        title = title.Trim();
        return title.Length <= 200
            ? title
            : throw new ArgumentException("书签名称不能超过 200 个字符。", nameof(title));
    }

    private static string FormatPlaybackTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private bool CaptureRecent(PlaybackSnapshot snapshot, bool completed)
    {
        var identity = GetHistoryIdentity(snapshot.Source);
        if (string.IsNullOrWhiteSpace(snapshot.Source) ||
            !ShouldPersistSource(identity.Source) ||
            (_recentCaptureSuppressedIdentity is { } suppressed &&
             HistoryIdentitiesEqual(identity, suppressed)) ||
            snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended))
        {
            return false;
        }

        var duration = Math.Max(0, snapshot.DurationSeconds);
        var position = Math.Clamp(snapshot.PositionSeconds, 0, duration > 0 ? duration : double.MaxValue);
        completed |= duration > 0 &&
            (position / duration >= 0.95 || duration >= 30 && position >= duration - 10);
        var preferences = _recentItems.FirstOrDefault(item => RecentMatches(item, identity))?.Preferences;
        _recentItems.RemoveAll(item => RecentMatches(item, identity));
        _recentItems.Insert(0, new PlaybackRecentItem
        {
            Source = identity.Source,
            IsMediaSource = identity.IsMediaSource,
            MediaSourceProfileId = identity.MediaSourceProfileId,
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? GetDisplayName(identity.Source) : snapshot.Title,
            PositionSeconds = position,
            DurationSeconds = duration,
            LastPlayedAt = DateTimeOffset.UtcNow,
            IsCompleted = completed,
            Preferences = preferences,
        });
        if (_recentItems.Count > RecentItemLimit)
        {
            _recentItems.RemoveRange(RecentItemLimit, _recentItems.Count - RecentItemLimit);
        }

        _stateDirty = true;
        return true;
    }

    private bool TryGetResumePosition(string source, double duration, out double position)
    {
        position = 0;
        if (!_resumePlaybackEnabled || string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var identity = GetHistoryIdentity(source);
        var recent = _recentItems.FirstOrDefault(item => RecentMatches(item, identity));
        if (recent is null || recent.IsCompleted || recent.PositionSeconds < 5)
        {
            return false;
        }

        var knownDuration = duration > 0 ? duration : recent.DurationSeconds;
        if (knownDuration > 0 &&
            (recent.PositionSeconds / knownDuration >= 0.95 ||
             knownDuration >= 30 && recent.PositionSeconds >= knownDuration - 10))
        {
            return false;
        }

        position = Math.Max(0, recent.PositionSeconds);
        return true;
    }

    private HistoryIdentity? GetActiveHistoryIdentity(string playbackSource) =>
        _activeHistoryIdentities.GetValueOrDefault(playbackSource);

    private HistoryIdentity GetHistoryIdentity(string playbackSource) =>
        GetActiveHistoryIdentity(playbackSource) ?? new HistoryIdentity(playbackSource);

    private static bool RecentMatches(PlaybackRecentItem item, HistoryIdentity identity) =>
        SourcesEqual(item.Source, identity.Source) &&
        item.IsMediaSource == identity.IsMediaSource &&
        string.Equals(item.MediaSourceProfileId, identity.MediaSourceProfileId, StringComparison.Ordinal);

    private static bool HistoryIdentitiesEqual(HistoryIdentity left, HistoryIdentity right) =>
        SourcesEqual(left.Source, right.Source) &&
        left.IsMediaSource == right.IsMediaSource &&
        string.Equals(left.MediaSourceProfileId, right.MediaSourceProfileId, StringComparison.Ordinal);

    private sealed record HistoryIdentity(
        string Source,
        bool IsMediaSource = false,
        string? MediaSourceProfileId = null);

    private async ValueTask PersistStateIfDirtyAsync(CancellationToken cancellationToken)
    {
        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PlaybackPersistedState state;
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SynchronizeSharedHistory();
                if (!_stateDirty)
                {
                    return;
                }

                var persistedPlaylist = new List<string>(_playlistSources.Count);
                var persistedSelectedIndex = -1;
                for (var index = 0; index < _playlistSources.Count; index++)
                {
                    var source = _playlistSources[index];
                    if (!ShouldPersistSource(source))
                    {
                        continue;
                    }

                    if (index == _selectedPlaylistIndex)
                    {
                        persistedSelectedIndex = persistedPlaylist.Count;
                    }

                    persistedPlaylist.Add(source);
                }

                state = new PlaybackPersistedState
                {
                    PlaylistSources = persistedPlaylist,
                    SelectedPlaylistIndex = persistedSelectedIndex >= 0
                        ? persistedSelectedIndex
                        : (persistedPlaylist.Count == 0 ? -1 : 0),
                    RecentItems = _recentItems
                        .Where(static item => ShouldPersistSource(item.Source))
                        .ToList(),
                    Bookmarks = _bookmarks
                        .Where(static item => ShouldPersistSource(item.Source))
                        .ToList(),
                };
                _stateDirty = false;
            }
            finally
            {
                _operationGate.Release();
            }

            try
            {
                await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _stateDirty = true;
                }
                finally
                {
                    _operationGate.Release();
                }

                throw;
            }
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private static IReadOnlyList<string> NormalizeSources(IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("播放列表至少需要一个媒体来源。", nameof(sources));
        }

        if (sources.Count > PlaylistItemLimit)
        {
            throw new ArgumentException($"播放列表最多包含 {PlaylistItemLimit} 项。", nameof(sources));
        }

        return sources.Select(NormalizeSource).ToList();
    }

    private static string NormalizeEngineSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) return Path.GetFullPath(uri.LocalPath);
            return uri.Scheme is "http" or "https" or MediaStreamBroker.Scheme ? uri.AbsoluteUri : source;
        }

        return !HasMpvProtocol(source) && Path.IsPathRooted(source) ? Path.GetFullPath(source) : source;
    }

    private static bool SourcesEqual(string? first, string? second) =>
        SourceComparer.Instance.Equals(first ?? string.Empty, second ?? string.Empty);

    private static bool PathsEqual(string first, string second) =>
        PathComparer.Instance.Equals(Path.GetFullPath(first), Path.GetFullPath(second));

    private static void ValidateMediaPosition(double? seconds, string parameterName)
    {
        if (seconds is { } value && (!double.IsFinite(value) || value < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "播放位置不能小于零。");
        }
    }

    private static string PrepareScreenshotPath(string? outputPath)
    {
        string path;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
            {
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            }

            var directory = Path.Combine(pictures, "CanX");
            Directory.CreateDirectory(directory);
            var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            path = Path.Combine(directory, $"CanX-{timestamp}.png");
            for (var suffix = 1; File.Exists(path); suffix++)
            {
                path = Path.Combine(directory, $"CanX-{timestamp}-{suffix}.png");
            }
        }
        else
        {
            path = Path.GetFullPath(outputPath.Trim());
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"截图目录不存在：{directory}");
            }
        }

        var extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("截图文件扩展名必须是 .png、.jpg、.jpeg 或 .webp。", nameof(outputPath));
        }

        if (File.Exists(path))
        {
            throw new IOException($"截图文件已存在，不会覆盖：{path}");
        }

        return path;
    }

    private static string NormalizeShaderPath(string source)
    {
        var path = NormalizeLocalFile(source, "Shader 文件不存在。");
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".glsl", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".hook", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Shader 文件扩展名必须是 .glsl 或 .hook。", nameof(source));
        }

        return path;
    }

    private List<string> ReadShadersOrFallback(List<string> fallback)
    {
        var shaders = _engine.GetShaders();
        return shaders.Count == 0 && fallback.Count > 0 ? fallback : shaders.ToList();
    }

    private PlaybackDiagnostics ReadDiagnostics() => new()
    {
        VideoCodec = ReadRawString("video-codec", string.Empty),
        VideoFormat = ReadRawString("video-format", string.Empty),
        DecoderPixelFormat = ReadRawString("video-params/pixelformat", string.Empty),
        HardwarePixelFormat = ReadRawString("video-params/hw-pixelformat", string.Empty),
        HardwareDecoder = ReadRawString("hwdec-current", string.Empty),
        HardwareInterop = ReadRawString("hwdec-interop", string.Empty),
        VideoOutput = ReadRawString("current-vo", string.Empty),
        GpuApi = ReadRenderApi(),
        GpuContext = ReadString("current-gpu-context", _engine.RenderContext),
        InputColorMatrix = ReadRawString("video-params/colormatrix", string.Empty),
        InputPrimaries = ReadRawString("video-params/primaries", string.Empty),
        InputTransfer = ReadRawString("video-params/gamma", string.Empty),
        InputLevels = ReadRawString("video-params/colorlevels", string.Empty),
        InputSignalPeak = ReadOptionalDouble("video-params/sig-peak"),
        TargetColorMatrix = ReadRawString("video-target-params/colormatrix", string.Empty),
        TargetPrimaries = ReadRawString("video-target-params/primaries", string.Empty),
        TargetTransfer = ReadRawString("video-target-params/gamma", string.Empty),
        TargetLevels = ReadRawString("video-target-params/colorlevels", string.Empty),
        TargetSignalPeak = ReadOptionalDouble("video-target-params/sig-peak"),
        ContainerFps = ReadOptionalDouble("container-fps"),
        EstimatedVideoFps = ReadOptionalDouble("estimated-vf-fps"),
        DisplayFps = ReadOptionalDouble("display-fps"),
        AvSyncSeconds = ReadOptionalDouble("avsync"),
        TotalAvSyncChangeSeconds = ReadOptionalDouble("total-avsync-change"),
        DecoderDroppedFrames = ReadInt64("decoder-frame-drop-count", 0),
        OutputDroppedFrames = ReadInt64("frame-drop-count", 0),
        MistimedFrames = ReadInt64("mistimed-frame-count", 0),
        DelayedFrames = ReadInt64("vo-delayed-frame-count", 0),
        VideoBitrateBitsPerSecond = ReadOptionalDouble("video-bitrate"),
        AudioBitrateBitsPerSecond = ReadOptionalDouble("audio-bitrate"),
        CacheSpeedBytesPerSecond = ReadOptionalDouble("cache-speed"),
        CacheDurationSeconds = ReadOptionalDouble("demuxer-cache-duration"),
    };

    private PlaybackColorSettings ReadColorSettings(PlaybackColorSettings fallback) => new()
    {
        TargetColorspaceHint = ReadString("target-colorspace-hint", fallback.TargetColorspaceHint),
        TargetColorspaceHintMode = ReadString("target-colorspace-hint-mode", fallback.TargetColorspaceHintMode),
        TargetPrimaries = ReadString("target-prim", fallback.TargetPrimaries),
        TargetTransfer = ReadString("target-trc", fallback.TargetTransfer),
        TargetPeak = ReadString("target-peak", fallback.TargetPeak),
        ToneMapping = ReadString("tone-mapping", fallback.ToneMapping),
        GamutMappingMode = ReadString("gamut-mapping-mode", fallback.GamutMappingMode),
        HdrComputePeak = ReadString("hdr-compute-peak", fallback.HdrComputePeak),
    };

    private string ReadRenderApi()
    {
        var value = ReadString("gpu-api", _engine.RenderApi);
        return string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) ? _engine.RenderApi : value;
    }

    private static PlaybackColorSettings NormalizeColorSettings(PlaybackColorSettings settings) => new()
    {
        TargetColorspaceHint = RequireOneOf(
            settings.TargetColorspaceHint,
            TargetColorspaceHintValues,
            nameof(settings.TargetColorspaceHint)),
        TargetColorspaceHintMode = RequireOneOf(
            settings.TargetColorspaceHintMode,
            TargetColorspaceHintModeValues,
            nameof(settings.TargetColorspaceHintMode)),
        TargetPrimaries = RequireOneOf(
            settings.TargetPrimaries,
            TargetPrimariesValues,
            nameof(settings.TargetPrimaries)),
        TargetTransfer = RequireOneOf(
            settings.TargetTransfer,
            TargetTransferValues,
            nameof(settings.TargetTransfer)),
        TargetPeak = NormalizeTargetPeak(settings.TargetPeak),
        ToneMapping = RequireOneOf(settings.ToneMapping, ToneMappingValues, nameof(settings.ToneMapping)),
        GamutMappingMode = RequireOneOf(
            settings.GamutMappingMode,
            GamutMappingValues,
            nameof(settings.GamutMappingMode)),
        HdrComputePeak = RequireOneOf(settings.HdrComputePeak, TargetColorspaceHintValues, nameof(settings.HdrComputePeak)),
    };

    private static string NormalizeTargetPeak(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nits) ||
            !double.IsFinite(nits) || nits is < 10 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "目标峰值必须是 auto 或 10 到 10000 nits。");
        }

        return nits.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string RequireOneOf(string value, HashSet<string> allowed, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized)
            ? normalized
            : throw new ArgumentException($"不支持的 mpv 选项值：{value}", parameterName);
    }

    private static string NormalizeRuntimePropertyName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 128 ||
            normalized.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '/'))
        {
            throw new ArgumentException("mpv 属性名无效。", nameof(name));
        }

        return normalized;
    }

    private static string NormalizeRuntimeCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var normalized = command.Trim();
        if (normalized.Length > 128 ||
            normalized.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("mpv 命令名无效。", nameof(command));
        }

        if (PlaybackCapabilityPolicy.GetCommandRestriction(normalized) is { } restriction)
        {
            throw new InvalidOperationException($"为保护宿主、文件和凭证，不能通过高级入口执行 mpv 命令：{normalized}。{restriction}");
        }

        return normalized;
    }

    private static string NormalizeRuntimeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > RuntimeValueLimit || value.Contains('\0'))
        {
            throw new ArgumentException($"mpv 参数不能超过 {RuntimeValueLimit} 个字符且不能包含 NUL。", nameof(value));
        }

        return value;
    }

    private static void EnsureRuntimePropertyReadable(string name)
    {
        if (PlaybackCapabilityPolicy.GetPropertyReadRestriction(name) is { } restriction)
        {
            throw new InvalidOperationException($"为避免泄露媒体凭证，不能通过高级入口读取 mpv 属性：{name}。{restriction}");
        }
    }

    private static void EnsureRuntimePropertyWritable(string name)
    {
        if (PlaybackCapabilityPolicy.GetPropertyWriteRestriction(name) is { } restriction)
        {
            throw new InvalidOperationException($"该 mpv 属性不能通过高级入口写入：{name}。{restriction}");
        }
    }

    private void SetRuntimePropertyCore(string name, string value) => _engine.Command("set", name, value);

    private double ReadDouble(string name, double fallback) =>
        _engine.TryGetDouble(name, out var value) && double.IsFinite(value) ? value : fallback;

    private double? ReadOptionalDouble(string name) =>
        _engine.TryGetDouble(name, out var value) && double.IsFinite(value) ? value : null;

    private long ReadInt64(string name, long fallback) =>
        _engine.TryGetInt64(name, out var value) ? value : fallback;

    private bool ReadFlag(string name, bool fallback = false) =>
        _engine.TryGetFlag(name, out var value) ? value : fallback;

    private string ReadString(string name, string fallback) =>
        _engine.TryGetString(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private string ReadRawString(string name, string fallback) =>
        _engine.TryGetString(name, out var value) ? value : fallback;

    private void Publish(PlaybackSnapshot snapshot)
    {
        if (_stateStore is ISharedPlaybackHistoryStore)
        {
            SynchronizeSharedHistory();
            snapshot = snapshot with
            {
                RecentItems = _recentItems.ToList(),
                Bookmarks = BuildBookmarksForSource(snapshot.Source),
            };
        }
        if (SnapshotsEqual(Snapshot, snapshot))
        {
            return;
        }

        Volatile.Write(ref _snapshot, snapshot);
        _eventBus.Publish(new PlaybackChanged(snapshot));
    }

    private void SynchronizeSharedHistory()
    {
        if (_stateStore is not ISharedPlaybackHistoryStore shared) return;
        var privateBookmarks = _bookmarks.Where(static item => !ShouldPersistSource(item.Source)).ToArray();
        var history = shared.SynchronizeHistory(
            _recentItems.Where(static item => ShouldPersistSource(item.Source)).ToArray(),
            _bookmarks.Where(static item => ShouldPersistSource(item.Source)).ToArray());
        _recentItems.Clear();
        _recentItems.AddRange(history.RecentItems);
        _bookmarks.Clear();
        _bookmarks.AddRange(history.Bookmarks);
        _bookmarks.AddRange(privateBookmarks);
    }

    private static bool SnapshotsEqual(PlaybackSnapshot current, PlaybackSnapshot updated) =>
        current.State == updated.State &&
        current.Source == updated.Source &&
        current.Title == updated.Title &&
        current.PositionSeconds.Equals(updated.PositionSeconds) &&
        current.DurationSeconds.Equals(updated.DurationSeconds) &&
        current.Volume.Equals(updated.Volume) &&
        current.Brightness.Equals(updated.Brightness) &&
        current.IsMuted == updated.IsMuted &&
        current.IsSubtitleVisible == updated.IsSubtitleVisible &&
        current.SubtitleDelaySeconds.Equals(updated.SubtitleDelaySeconds) &&
        current.SubtitleFontSize.Equals(updated.SubtitleFontSize) &&
        current.SubtitleScale.Equals(updated.SubtitleScale) &&
        current.SubtitlePosition.Equals(updated.SubtitlePosition) &&
        current.Speed.Equals(updated.Speed) &&
        current.IsSeekable == updated.IsSeekable &&
        current.VideoWidth == updated.VideoWidth &&
        current.VideoHeight == updated.VideoHeight &&
        current.ErrorMessage == updated.ErrorMessage &&
        current.Tracks.SequenceEqual(updated.Tracks) &&
        current.Playlist.SequenceEqual(updated.Playlist) &&
        current.CurrentPlaylistIndex == updated.CurrentPlaylistIndex &&
        current.RecentItems.SequenceEqual(updated.RecentItems) &&
        current.Chapters.SequenceEqual(updated.Chapters) &&
        current.CurrentChapterIndex == updated.CurrentChapterIndex &&
        current.Bookmarks.SequenceEqual(updated.Bookmarks) &&
        current.AbLoopASeconds.Equals(updated.AbLoopASeconds) &&
        current.AbLoopBSeconds.Equals(updated.AbLoopBSeconds) &&
        current.AudioDevices.SequenceEqual(updated.AudioDevices) &&
        current.AudioDevice == updated.AudioDevice &&
        current.VideoFilters == updated.VideoFilters &&
        current.AudioFilters == updated.AudioFilters &&
        current.Shaders.SequenceEqual(updated.Shaders, PathComparer.Instance) &&
        current.ColorSettings == updated.ColorSettings &&
        current.Diagnostics == updated.Diagnostics;

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!IsStarted)
        {
            throw new InvalidOperationException("播放器尚未启动。");
        }
    }

    private void EnsureMediaLoaded()
    {
        if (string.IsNullOrWhiteSpace(Snapshot.Source))
        {
            throw new InvalidOperationException("尚未打开媒体。");
        }
    }

    private static string NormalizeSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = source.Trim();
        if ((Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile) || HasMpvProtocol(source))
        {
            // mpv protocols such as av:// and edl:// are not necessarily valid
            // .NET URIs. Preserve their payload and let mpv resolve the protocol.
            return NormalizeEngineSource(source);
        }

        var path = Path.GetFullPath(uri?.LocalPath ?? source);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("媒体文件不存在。", path);
    }

    private static bool HasMpvProtocol(string source)
    {
        var separator = source.IndexOf("://", StringComparison.Ordinal);
        return separator > 1 &&
            Uri.CheckSchemeName(source[..separator]) &&
            !source.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPersistSource(string source) =>
        !DownloadSecretClassifier.IsSensitiveUri(source) &&
        (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
         !string.Equals(uri.Scheme, MediaStreamBroker.Scheme, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeLocalFile(string source, string notFoundMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = source.Trim();
        var path = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : source;
        path = Path.GetFullPath(path);
        return File.Exists(path) ? path : throw new FileNotFoundException(notFoundMessage, path);
    }

    private static string NormalizeSubtitleSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = source.Trim();
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return string.Equals(uri.Scheme, MediaStreamBroker.Scheme, StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri
                : throw new ArgumentException(
                    "字幕来源必须是存在的本地文件或宿主管理的媒体流。",
                    nameof(source));
        }

        return NormalizeLocalFile(source, "字幕文件不存在。");
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

    private sealed class SourceComparer : IEqualityComparer<string>
    {
        public static SourceComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) => string.Equals(
            x,
            y,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        public int GetHashCode(string value) =>
            (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).GetHashCode(value);
    }

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public static PathComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) => string.Equals(
            x,
            y,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        public int GetHashCode(string value) =>
            (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).GetHashCode(value);
    }

    private sealed class NullPlaybackStateStore : IPlaybackStateStore
    {
        public static NullPlaybackStateStore Instance { get; } = new();

        public ValueTask<PlaybackPersistedState> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PlaybackPersistedState());

        public ValueTask SaveAsync(
            PlaybackPersistedState state,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
