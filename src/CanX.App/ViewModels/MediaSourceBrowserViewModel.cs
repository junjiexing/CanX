// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using CanX.Core.Diagnostics;
using CanX.Core.Playback;
using CanX.MediaAccess;
using CanX.Streaming;

namespace CanX.App.ViewModels;

public sealed class MediaSourceBrowserViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IApplicationLog _applicationLog;
    private readonly IMediaSourceProfileStore? _profileStore;
    private readonly IMediaSourceFavoriteStore? _favoriteStore;
    private readonly Func<Uri, NetworkCredential?, IMediaSourceClient> _webDavFactory;
    private readonly Func<Uri, NetworkCredential?, IMediaSourceClient> _ftpFactory;
    private readonly Func<Uri, NetworkCredential?, IMediaSourceClient> _smbFactory;
    private readonly Stack<Uri> _backHistory = new();
    private readonly Stack<Uri> _forwardHistory = new();
    private readonly List<MediaResourceItemViewModel> _allEntries = [];
    private PlaybackHistoryProgressLookup _playbackProgress = new([]);
    private readonly Dictionary<string, MediaSourceConnectionProfile> _profiles = new(StringComparer.Ordinal);
    private MediaSourceClientSession? _clientSession;
    private Uri? _currentDirectory;
    private MediaResourceItemViewModel? _selectedEntry;
    private RemoteSubtitleChoiceViewModel? _selectedSubtitle;
    private MediaSourceFavoriteViewModel? _selectedFavorite;
    private PlaybackSourceItemViewModel? _selectedSource;
    private string? _activeProfileId;
    private string _endpointText = "https://";
    private string _userName = string.Empty;
    private string _password = string.Empty;
    private string _entryFilterText = string.Empty;
    private string _statusMessage = "输入 WebDAV 地址后连接；可选择将凭证保存到系统安全存储。";
    private MediaSourceProtocol _protocol = MediaSourceProtocol.WebDav;
    private string? _credentialPersistenceWarning;
    private bool _isBusy;
    private bool _isConnected;
    private bool _rememberCredentials;
    private bool _sortDescending;
    private bool _isProtocolPageLoaded;
    private MediaEntryTypeFilter _entryTypeFilter = MediaEntryTypeFilter.All;
    private MediaEntrySortOrder _entrySortOrder = MediaEntrySortOrder.Name;
    private bool _disposed;

    public MediaSourceBrowserViewModel(
        IApplicationLog? applicationLog = null,
        IMediaSourceProfileStore? profileStore = null,
        IMediaSourceFavoriteStore? favoriteStore = null,
        Func<Uri, NetworkCredential?, IMediaSourceClient>? webDavFactory = null,
        Func<Uri, NetworkCredential?, IMediaSourceClient>? ftpFactory = null,
        Func<Uri, NetworkCredential?, IMediaSourceClient>? smbFactory = null)
    {
        _applicationLog = applicationLog ?? NullApplicationLog.Instance;
        _profileStore = profileStore;
        _favoriteStore = favoriteStore ?? profileStore as IMediaSourceFavoriteStore;
        _webDavFactory = webDavFactory ?? (static (root, credential) =>
            WebDavMediaSourceClient.Create(root, credential));
        _ftpFactory = ftpFactory ?? (static (root, credential) =>
            FtpMediaSourceClient.Create(root, credential));
        _smbFactory = smbFactory ?? (static (root, credential) =>
            SmbMediaSourceClient.Create(root, credential));
        SubtitleChoices.Add(RemoteSubtitleChoiceViewModel.Auto);
        SubtitleChoices.Add(RemoteSubtitleChoiceViewModel.None);
        _selectedSubtitle = RemoteSubtitleChoiceViewModel.Auto;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MediaResourceItemViewModel> Entries { get; } = [];

    public ObservableCollection<RemoteSubtitleChoiceViewModel> SubtitleChoices { get; } = [];

    public ObservableCollection<MediaSourceFavoriteViewModel> Favorites { get; } = [];

    public ObservableCollection<PlaybackSourceItemViewModel> Sources { get; } = [];

    public void SetPlaybackHistory(IReadOnlyList<PlaybackRecentItemViewModel> items)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _playbackProgress = new PlaybackHistoryProgressLookup(items);
        foreach (var entry in _allEntries)
        {
            entry.SetPlaybackProgress(entry.IsVideo
                ? _playbackProgress.Find(entry.Resource, _activeProfileId)
                : null);
        }

        if (_entrySortOrder == MediaEntrySortOrder.Progress && _allEntries.Count > 0)
        {
            ApplyEntryView();
        }
    }

    public PlaybackSourceItemViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetField(ref _selectedSource, value))
            {
                OnPropertyChanged(nameof(CanOpenSelectedSource));
                OnPropertyChanged(nameof(CanRemoveSelectedSource));
            }
        }
    }

    public bool HasSources => Sources.Count > 0;

    public string? ActiveProfileId => _activeProfileId;

    public bool CanOpenSelectedSource => !IsBusy && SelectedSource is not null;

    public bool CanRemoveSelectedSource => !IsBusy && SelectedSource is not null;

    public IReadOnlyList<MediaEntryTypeFilterOption> EntryTypeFilterOptions { get; } =
    [
        new(MediaEntryTypeFilter.All, "全部类型"),
        new(MediaEntryTypeFilter.Directory, "文件夹"),
        new(MediaEntryTypeFilter.Media, "媒体"),
        new(MediaEntryTypeFilter.Image, "图片"),
        new(MediaEntryTypeFilter.Subtitle, "字幕"),
        new(MediaEntryTypeFilter.Other, "其他文件"),
    ];

    public IReadOnlyList<MediaEntrySortOption> EntrySortOptions { get; } =
    [
        new(MediaEntrySortOrder.Name, "名称"),
        new(MediaEntrySortOrder.Type, "类型"),
        new(MediaEntrySortOrder.Progress, "进度"),
        new(MediaEntrySortOrder.Size, "大小"),
        new(MediaEntrySortOrder.Modified, "修改时间"),
    ];

    public MediaSourceProtocol Protocol => _protocol;

    public string PageTitle => Protocol switch
    {
        MediaSourceProtocol.LocalFolder => "本地媒体文件夹",
        MediaSourceProtocol.WebDav => "WebDAV 媒体源",
        MediaSourceProtocol.Ftp => "FTP / FTPS 媒体源",
        MediaSourceProtocol.Smb => "SMB 媒体源",
        _ => "网络媒体源",
    };

    public string EndpointLabel => Protocol switch
    {
        MediaSourceProtocol.WebDav => "服务器地址",
        MediaSourceProtocol.Ftp => "FTP 服务器地址",
        MediaSourceProtocol.Smb => "SMB 服务器 / 共享地址",
        _ => "服务器地址",
    };

    public string EndpointPlaceholder => Protocol switch
    {
        MediaSourceProtocol.WebDav => "https://server.example/dav/",
        MediaSourceProtocol.Ftp => "ftp://server.example/media/",
        MediaSourceProtocol.Smb => "smb://server.example/（共享名可选）",
        _ => string.Empty,
    };

    public string ProtocolDescription => Protocol switch
    {
        MediaSourceProtocol.LocalFolder => "浏览所选文件夹中的全部文件，可播放音视频并查看图片。",
        MediaSourceProtocol.WebDav => "支持 Range 时可随机跳转；不支持时自动顺序播放，媒体与字幕不会保存为本地文件。",
        MediaSourceProtocol.Ftp => "支持 FTP 与显式 TLS 的 FTPS；支持 REST 时可随机跳转，否则自动顺序播放。",
        MediaSourceProtocol.Smb => "支持 SMB 2/3 共享与随机读取；省略共享名称时会列出服务器上的共享。",
        _ => string.Empty,
    };

    public string EndpointText
    {
        get => _endpointText;
        set
        {
            if (SetField(ref _endpointText, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanTestConnection));
            }
        }
    }

    public string UserName
    {
        get => _userName;
        set => SetField(ref _userName, value);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value);
    }

    public bool RememberCredentials
    {
        get => _rememberCredentials;
        set => SetField(ref _rememberCredentials, value);
    }

    public string EntryFilterText
    {
        get => _entryFilterText;
        set
        {
            if (SetField(ref _entryFilterText, value ?? string.Empty))
            {
                ApplyEntryView();
            }
        }
    }

    public MediaEntryTypeFilterOption? SelectedEntryTypeFilter
    {
        get => EntryTypeFilterOptions.First(option => option.Value == _entryTypeFilter);
        set
        {
            if (value is not null && _entryTypeFilter != value.Value)
            {
                _entryTypeFilter = value.Value;
                OnPropertyChanged();
                ApplyEntryView();
            }
        }
    }

    public MediaEntrySortOption? SelectedEntrySort
    {
        get => EntrySortOptions.First(option => option.Value == _entrySortOrder);
        set
        {
            if (value is not null && _entrySortOrder != value.Value)
            {
                _entrySortOrder = value.Value;
                OnPropertyChanged();
                RaiseSortHeaderChanges();
                ApplyEntryView();
            }
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (SetField(ref _sortDescending, value))
            {
                RaiseSortHeaderChanges();
                ApplyEntryView();
            }
        }
    }

    public string NameSortIndicator => SortIndicator(MediaEntrySortOrder.Name);

    public string TypeSortIndicator => SortIndicator(MediaEntrySortOrder.Type);

    public string ProgressSortIndicator => SortIndicator(MediaEntrySortOrder.Progress);

    public string SizeSortIndicator => SortIndicator(MediaEntrySortOrder.Size);

    public string ModifiedSortIndicator => SortIndicator(MediaEntrySortOrder.Modified);

    public void SortEntriesBy(MediaEntrySortOrder order)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        if (_entrySortOrder == order)
        {
            SortDescending = !SortDescending;
            return;
        }

        _entrySortOrder = order;
        _sortDescending = false;
        OnPropertyChanged(nameof(SelectedEntrySort));
        OnPropertyChanged(nameof(SortDescending));
        RaiseSortHeaderChanges();
        ApplyEntryView();
    }

    private string SortIndicator(MediaEntrySortOrder order) =>
        _entrySortOrder != order ? string.Empty : _sortDescending ? "▼" : "▲";

    private void RaiseSortHeaderChanges()
    {
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(TypeSortIndicator));
        OnPropertyChanged(nameof(ProgressSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        OnPropertyChanged(nameof(ModifiedSortIndicator));
    }

    public string EntryViewSummary => _allEntries.Count == Entries.Count
        ? $"{Entries.Count} 项"
        : $"显示 {Entries.Count} / {_allEntries.Count} 项";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RaiseActionStates();
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetField(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(SourceLabel));
                RaiseActionStates();
            }
        }
    }

    public string SourceLabel => IsConnected &&
        _activeProfileId is not null &&
        _profiles.TryGetValue(_activeProfileId, out var profile)
            ? $"{profile.Name} · {GetProtocolLabel(profile.Protocol)}"
            : GetProtocolLabel(Protocol);

    public string CurrentLocation => _currentDirectory is null
        ? "尚未连接"
        : Uri.UnescapeDataString(_currentDirectory.AbsolutePath);

    public MediaResourceItemViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
            {
                SelectedSubtitle = RemoteSubtitleChoiceViewModel.Auto;
                OnPropertyChanged(nameof(CanOpenSelected));
                OnPropertyChanged(nameof(SelectedActionText));
                OnPropertyChanged(nameof(IsPlayableSelected));
            }
        }
    }

    public RemoteSubtitleChoiceViewModel? SelectedSubtitle
    {
        get => _selectedSubtitle;
        set => SetField(ref _selectedSubtitle, value ?? RemoteSubtitleChoiceViewModel.Auto);
    }

    public MediaSourceFavoriteViewModel? SelectedFavorite
    {
        get => _selectedFavorite;
        set
        {
            if (SetField(ref _selectedFavorite, value))
            {
                OnPropertyChanged(nameof(CanOpenFavorite));
            }
        }
    }

    public bool CanConnect => !IsBusy && !string.IsNullOrWhiteSpace(EndpointText);

    public bool CanTestConnection => CanConnect;

    public bool CanRefresh => IsConnected && !IsBusy;

    public bool CanNavigateBack => IsConnected && !IsBusy && _backHistory.Count > 0;

    public bool CanNavigateForward => IsConnected && !IsBusy && _forwardHistory.Count > 0;

    public bool CanOpenFavorite => IsConnected && !IsBusy && SelectedFavorite is not null;

    public bool CanToggleFavorite => IsConnected && !IsBusy && _currentDirectory is not null &&
        _favoriteStore is not null;

    public bool IsCurrentLocationFavorite => _currentDirectory is not null && Favorites.Any(favorite =>
        UriEquals(favorite.Favorite.Location, _currentDirectory));

    public string FavoriteActionText => IsCurrentLocationFavorite ? "取消收藏" : "收藏当前目录";

    public bool CanNavigateUp => IsConnected && !IsBusy && _clientSession is not null &&
        _currentDirectory is not null && !UriEquals(_currentDirectory, _clientSession.Client.Root);

    public bool CanOpenSelected => !IsBusy && SelectedEntry is { IsDirectory: true } or { IsPlayable: true } or { IsImage: true };

    public bool IsPlayableSelected => SelectedEntry?.IsPlayable == true;

    public string SelectedActionText => SelectedEntry?.IsDirectory == true
        ? "进入"
        : SelectedEntry?.IsImage == true ? "查看" : "播放";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_profileStore is null)
        {
            return;
        }

        var profiles = await _profileStore.ListProfilesAsync(cancellationToken).ConfigureAwait(true);
        ReplaceSources(profiles);
    }

    public async Task<PlaybackSourceItemViewModel> AddLocalFolderAsync(
        string directory,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.GetFullPath(directory.Trim());
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"媒体文件夹不存在：{path}");
        }

        var profile = new MediaSourceConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NormalizeSourceName(name, new DirectoryInfo(path).Name, "本地媒体"),
            Protocol = MediaSourceProtocol.LocalFolder,
            Endpoint = new Uri(Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar),
        };
        await SaveAndAddSourceAsync(profile, cancellationToken).ConfigureAwait(true);
        return SelectedSource!;
    }

    public async Task<PlaybackSourceItemViewModel> AddNetworkSourceAsync(
        MediaSourceProtocol protocol,
        string endpointText,
        string? name,
        string? userName,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (protocol == MediaSourceProtocol.LocalFolder)
        {
            throw new ArgumentException("本地文件夹必须通过本地源入口添加。", nameof(protocol));
        }

        var endpoint = ParseEndpoint(protocol, endpointText);
        var normalizedUserName = userName?.Trim() ?? string.Empty;
        var normalizedPassword = password ?? string.Empty;
        if (normalizedUserName.Length == 0 && normalizedPassword.Length > 0)
        {
            throw new ArgumentException("输入密码时必须同时填写用户名。", nameof(userName));
        }

        var credential = normalizedUserName.Length == 0
            ? null
            : new NetworkCredential(normalizedUserName, normalizedPassword);
        await using (var candidate = CreateClient(protocol, endpoint, credential))
        {
            _ = await candidate.BrowseAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        }

        var profile = new MediaSourceConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NormalizeSourceName(name, endpoint.Host, GetProtocolLabel(protocol)),
            Protocol = protocol,
            Endpoint = endpoint,
            UserName = normalizedUserName,
            Password = normalizedPassword,
        };
        await SaveAndAddSourceAsync(profile, cancellationToken).ConfigureAwait(true);
        return SelectedSource!;
    }

    public async Task OpenSelectedSourceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SelectedSource is null || IsBusy)
        {
            return;
        }

        if (!_profiles.TryGetValue(SelectedSource.Id, out var profile))
        {
            throw new InvalidOperationException("播放源已不存在，请刷新后重试。");
        }

        IMediaSourceClient? candidate = null;
        IsBusy = true;
        StatusMessage = $"正在连接 {profile.Name}…";
        try
        {
            if (_profileStore is not null)
            {
                profile = await _profileStore.LoadProfileAsync(profile.Id, cancellationToken).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("播放源已不存在，请刷新后重试。");
                _profiles[profile.Id] = profile;
            }

            var credential = profile.Protocol == MediaSourceProtocol.LocalFolder ||
                string.IsNullOrWhiteSpace(profile.UserName)
                ? null
                : new NetworkCredential(profile.UserName, profile.Password);
            candidate = CreateClient(profile.Protocol, profile.Endpoint, credential);
            var resources = await candidate.BrowseAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            var previous = _clientSession;
            _clientSession = new MediaSourceClientSession(candidate);
            candidate = null;
            _activeProfileId = profile.Id;
            _currentDirectory = _clientSession.Client.Root;
            ClearNavigationHistory();
            SetProtocol(profile.Protocol);
            EndpointText = profile.Endpoint.AbsoluteUri;
            UserName = profile.UserName;
            Password = string.Empty;
            IsConnected = true;
            ReplaceEntries(resources);
            previous?.Release();
            var favoritesLoaded = await TryLoadFavoritesAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = favoritesLoaded
                ? $"已连接，共 {_allEntries.Count} 项"
                : $"已连接，共 {_allEntries.Count} 项；收藏加载失败";
        }
        catch (Exception exception)
        {
            StatusMessage = $"{profile.Name} 连接失败：{exception.Message}";
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{profile.Protocol} media source connection failed ({exception.GetType().Name}).");
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(true);
            }

            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenRecentFolderAsync(
        PlaybackRecentFolderViewModel folder,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(folder);
        if (IsBusy)
        {
            throw new InvalidOperationException("媒体源正在执行其他操作，请稍后重试。");
        }

        var directory = folder.Directory;
        var protocol = directory.Scheme switch
        {
            "file" => MediaSourceProtocol.LocalFolder,
            "http" or "https" => MediaSourceProtocol.WebDav,
            "ftp" or "ftps" => MediaSourceProtocol.Ftp,
            "smb" => MediaSourceProtocol.Smb,
            _ => throw new NotSupportedException("这条播放历史的文件夹类型不受支持。"),
        };
        if (_clientSession is { } activeSession &&
            activeSession.Client.Protocol == protocol &&
            (folder.MediaSourceProfileId is null ||
             string.Equals(folder.MediaSourceProfileId, _activeProfileId, StringComparison.Ordinal)) &&
            activeSession.Client.Root.IsBaseOf(directory))
        {
            await NavigateToAsync(directory, cancellationToken).ConfigureAwait(true);
            return;
        }

        IMediaSourceClient? candidate = null;
        IsBusy = true;
        StatusMessage = $"正在打开 {folder.Name}…";
        try
        {
            MediaSourceConnectionProfile? profile = null;
            if (folder.MediaSourceProfileId is { } profileId)
            {
                profile = _profileStore is null
                    ? null
                    : await _profileStore.LoadProfileAsync(profileId, cancellationToken).ConfigureAwait(true);
                if (profile is null)
                {
                    throw new InvalidOperationException("对应的播放源已被删除，请重新添加后打开文件夹。");
                }

                if (profile.Protocol != protocol)
                {
                    throw new InvalidOperationException("播放历史中的文件夹不属于对应的播放源。");
                }
            }

            var endpoint = profile?.Endpoint ?? directory;
            var credential = profile is null || string.IsNullOrWhiteSpace(profile.UserName)
                ? null
                : new NetworkCredential(profile.UserName, profile.Password);
            candidate = CreateClient(protocol, endpoint, credential);
            if (!candidate.Root.IsBaseOf(directory))
            {
                throw new InvalidOperationException("播放历史中的文件夹不属于对应的播放源。");
            }
            var resources = await candidate.BrowseAsync(directory, cancellationToken).ConfigureAwait(true);
            var previous = _clientSession;
            _clientSession = new MediaSourceClientSession(candidate);
            candidate = null;
            if (profile is not null)
            {
                _profiles[profile.Id] = profile;
            }
            _activeProfileId = profile?.Id;
            _currentDirectory = directory;
            ClearNavigationHistory();
            SetProtocol(protocol);
            EndpointText = endpoint.AbsoluteUri;
            UserName = profile?.UserName ?? string.Empty;
            Password = string.Empty;
            IsConnected = true;
            ReplaceEntries(resources);
            previous?.Release();
            var favoritesLoaded = await TryLoadFavoritesAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = favoritesLoaded
                ? $"已打开 {folder.Name}，共 {_allEntries.Count} 项"
                : $"已打开 {folder.Name}，共 {_allEntries.Count} 项；收藏加载失败";
        }
        catch (Exception exception)
        {
            StatusMessage = $"打开文件夹失败：{exception.Message}";
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(true);
            }

            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RemoveSelectedSourceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SelectedSource is null || IsBusy)
        {
            return;
        }

        var source = SelectedSource;
        if (_profileStore is not null)
        {
            await _profileStore.DeleteProfileAsync(source.Id, cancellationToken).ConfigureAwait(true);
        }

        _profiles.Remove(source.Id);
        Sources.Remove(source);
        SelectedSource = null;
        if (string.Equals(_activeProfileId, source.Id, StringComparison.Ordinal))
        {
            _activeProfileId = null;
            _clientSession?.Release();
            _clientSession = null;
            _currentDirectory = null;
            IsConnected = false;
            ReplaceEntries([]);
        }

        OnPropertyChanged(nameof(HasSources));
    }

    public async Task SelectProtocolAsync(
        MediaSourceProtocol protocol,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy || (protocol == _protocol && _isProtocolPageLoaded))
        {
            return;
        }

        IsBusy = true;
        var previous = _clientSession;
        _clientSession = null;
        _activeProfileId = null;
        _currentDirectory = null;
        ClearNavigationHistory();
        IsConnected = false;
        SetProtocol(protocol);
        _isProtocolPageLoaded = false;
        UserName = string.Empty;
        Password = string.Empty;
        RememberCredentials = false;
        EndpointText = protocol switch
        {
            MediaSourceProtocol.WebDav => "https://",
            MediaSourceProtocol.Ftp => "ftp://",
            MediaSourceProtocol.Smb => "smb://",
            _ => string.Empty,
        };
        ReplaceEntries([]);
        ReplaceFavorites([]);
        previous?.Release();
        try
        {
            MediaSourceConnectionProfile? profile = null;
            if (_profileStore is not null)
            {
                var profiles = await Task.Run(
                    () => _profileStore.ListProfilesAsync(cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(true);
                var savedProfile = profiles.FirstOrDefault(candidate => candidate.Protocol == protocol);
                if (savedProfile is not null)
                {
                    profile = await Task.Run(
                        () => _profileStore.LoadProfileAsync(savedProfile.Id, cancellationToken).AsTask(),
                        cancellationToken).ConfigureAwait(true);
                }
            }
            if (profile is null)
            {
                StatusMessage = $"输入 {GetProtocolLabel(protocol)} 地址后连接；可选择将凭证保存到系统安全存储。";
            }
            else
            {
                EndpointText = profile.Endpoint.AbsoluteUri;
                UserName = profile.UserName;
                Password = profile.Password;
                RememberCredentials = true;
                StatusMessage = "已从系统安全存储加载连接信息。";
            }

            _isProtocolPageLoaded = true;
        }
        catch (Exception exception)
        {
            StatusMessage = "无法从系统安全存储加载连接信息。";
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{Protocol} media source credential load failed ({exception.GetType().Name}).");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy)
        {
            return;
        }

        var (endpoint, credential) = GetConnectionInput();
        IMediaSourceClient? candidate = null;
        IsBusy = true;
        _credentialPersistenceWarning = null;
        StatusMessage = $"正在连接 {GetProtocolLabel(Protocol)}…";
        try
        {
            candidate = CreateClient(endpoint, credential);
            var resources = await candidate.BrowseAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            var previous = _clientSession;
            _clientSession = new MediaSourceClientSession(candidate);
            candidate = null;
            _activeProfileId = null;
            OnPropertyChanged(nameof(SourceLabel));
            _currentDirectory = _clientSession.Client.Root;
            ClearNavigationHistory();
            IsConnected = true;
            ReplaceEntries(resources);
            previous?.Release();
            await UpdateSavedProfileAsync(endpoint, credential, cancellationToken).ConfigureAwait(true);
            var favoritesLoaded = await TryLoadFavoritesAsync(cancellationToken).ConfigureAwait(true);
            Password = string.Empty;
            StatusMessage = (_credentialPersistenceWarning, favoritesLoaded) switch
            {
                (null, true) => $"已连接，共 {_allEntries.Count} 项",
                (not null, true) => $"已连接，共 {_allEntries.Count} 项；凭证未持久化",
                (null, false) => $"已连接，共 {_allEntries.Count} 项；收藏加载失败",
                _ => $"已连接，共 {_allEntries.Count} 项；凭证未持久化且收藏加载失败",
            };
        }
        catch (Exception exception)
        {
            StatusMessage = $"{GetProtocolLabel(Protocol)} 连接失败：{exception.Message}";
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{Protocol} media source connection failed ({exception.GetType().Name}).");
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(true);
            }

            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy)
        {
            return;
        }

        var (endpoint, credential) = GetConnectionInput();
        IMediaSourceClient? candidate = null;
        IsBusy = true;
        StatusMessage = $"正在测试 {GetProtocolLabel(Protocol)} 连接…";
        try
        {
            candidate = CreateClient(endpoint, credential);
            var resources = await candidate.BrowseAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            StatusMessage = $"连接测试成功，根目录可读取，共 {resources.Count} 项";
        }
        catch (Exception exception)
        {
            StatusMessage = $"{GetProtocolLabel(Protocol)} 连接测试失败：{exception.Message}";
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{Protocol} media source connection test failed ({exception.GetType().Name}).");
            throw;
        }
        finally
        {
            try
            {
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(true);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        BrowseDirectoryAsync(_currentDirectory, cancellationToken);

    public async Task NavigateBackAsync(CancellationToken cancellationToken = default)
    {
        if (!CanNavigateBack || _currentDirectory is null)
        {
            return;
        }

        var previous = _currentDirectory;
        var target = _backHistory.Peek();
        await BrowseDirectoryAsync(target, cancellationToken).ConfigureAwait(true);
        _backHistory.Pop();
        _forwardHistory.Push(previous);
        RaiseActionStates();
    }

    public async Task NavigateForwardAsync(CancellationToken cancellationToken = default)
    {
        if (!CanNavigateForward || _currentDirectory is null)
        {
            return;
        }

        var previous = _currentDirectory;
        var target = _forwardHistory.Peek();
        await BrowseDirectoryAsync(target, cancellationToken).ConfigureAwait(true);
        _forwardHistory.Pop();
        _backHistory.Push(previous);
        RaiseActionStates();
    }

    public Task OpenSelectedFavoriteAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOpenFavorite || SelectedFavorite is null)
        {
            return Task.CompletedTask;
        }

        return NavigateToAsync(SelectedFavorite.Favorite.Location, cancellationToken);
    }

    public async Task ToggleCurrentFavoriteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanToggleFavorite || _currentDirectory is null || _clientSession is null || _favoriteStore is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var existing = Favorites.FirstOrDefault(favorite =>
                UriEquals(favorite.Favorite.Location, _currentDirectory));
            if (existing is not null)
            {
                await Task.Run(
                    () => _favoriteStore.DeleteAsync(existing.Favorite.Id, cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(true);
                Favorites.Remove(existing);
                SelectedFavorite = null;
                StatusMessage = "已取消收藏当前目录";
            }
            else
            {
                var client = _clientSession.Client;
                var favorite = await Task.Run(
                    () => _favoriteStore.AddAsync(
                        client.Protocol,
                        client.Root,
                        _currentDirectory,
                        GetFavoriteName(client.Root, _currentDirectory),
                        cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(true);
                var item = new MediaSourceFavoriteViewModel(favorite);
                Favorites.Add(item);
                SortFavorites();
                SelectedFavorite = Favorites.First(entry => entry.Favorite.Id == favorite.Id);
                StatusMessage = "已收藏当前目录";
            }

            RaiseFavoriteStates();
        }
        catch (Exception exception)
        {
            StatusMessage = $"收藏操作失败：{exception.Message}";
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{Protocol} media source favorite update failed ({exception.GetType().Name}).");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task NavigateUpAsync(CancellationToken cancellationToken = default)
    {
        if (!CanNavigateUp || _currentDirectory is null)
        {
            return Task.CompletedTask;
        }

        return NavigateToAsync(new Uri(_currentDirectory, "../"), cancellationToken);
    }

    public Task OpenSelectedDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedEntry is not { IsDirectory: true } directory)
        {
            return Task.CompletedTask;
        }

        return NavigateToAsync(directory.Resource.Uri, cancellationToken);
    }

    public ValueTask<IMediaByteStream> OpenReadAsync(
        MediaResource resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (_clientSession?.Client ?? throw new InvalidOperationException("尚未连接网络媒体源。"))
            .OpenReadAsync(resource, cancellationToken);
    }

    public MediaSourceReadLease AcquireRead(MediaResource resource)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        return (_clientSession ?? throw new InvalidOperationException("尚未连接网络媒体源。"))
            .Acquire(resource);
    }

    public IReadOnlyList<MediaResource> GetVideoSiblings(MediaResource selected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selected);
        if (!ExternalSubtitleMatcher.IsVideoFile(selected.Name)) return [selected];
        var videos = _allEntries.Where(static entry => entry.IsVideo).ToList();
        if (!videos.Any(entry => entry.Resource.Uri == selected.Uri)) return [selected];
        videos.Sort(CompareEntries);
        return videos.Select(static entry => entry.Resource).ToArray();
    }

    public async Task<SubtitleSourceBrowser> OpenSubtitleSourceAsync(
        string profileId,
        Uri? mediaUri = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.Equals(profileId, _activeProfileId, StringComparison.Ordinal) &&
            _clientSession is { } activeSession)
        {
            var activeBrowser = new SubtitleSourceBrowser(activeSession);
            try
            {
                await activeBrowser.BrowseAsync(mediaUri is null ? null : new Uri(mediaUri, "."),
                    cancellationToken).ConfigureAwait(true);
                return activeBrowser;
            }
            catch
            {
                await activeBrowser.DisposeAsync().ConfigureAwait(true);
                throw;
            }
        }

        if (!_profiles.TryGetValue(profileId, out var profile))
        {
            throw new InvalidOperationException("字幕播放源已不存在，请刷新后重试。");
        }

        if (_profileStore is not null)
        {
            profile = await _profileStore.LoadProfileAsync(profileId, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("字幕播放源已被删除，请重新添加。");
        }

        var credential = profile.Protocol == MediaSourceProtocol.LocalFolder ||
            string.IsNullOrWhiteSpace(profile.UserName)
            ? null : new NetworkCredential(profile.UserName, profile.Password);
        var client = CreateClient(profile.Protocol, profile.Endpoint, credential);
        var browser = new SubtitleSourceBrowser(client);
        try
        {
            await browser.BrowseAsync(mediaUri is null ? null : new Uri(mediaUri, "."), cancellationToken)
                .ConfigureAwait(true);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    public MediaImageSequence AcquireImageSequence(MediaResource selected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selected);
        var images = Entries.Where(static entry => entry.IsImage).Select(static entry => entry.Resource).ToArray();
        var index = Array.FindIndex(images, resource => resource.Uri == selected.Uri);
        if (index < 0)
        {
            throw new ArgumentException("所选图片不在当前文件列表中。", nameof(selected));
        }
        return new MediaImageSequence(
            _clientSession ?? throw new InvalidOperationException("尚未打开媒体源。"), images, index);
    }

    public async Task<(MediaResource Resource, MediaSourceReadLease Lease)> AcquireHistoryReadAsync(
        Uri mediaUri,
        string? mediaSourceProfileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mediaUri);
        if (!mediaUri.IsAbsoluteUri)
        {
            throw new ArgumentException("播放历史中的媒体地址无效。", nameof(mediaUri));
        }

        if (_clientSession is { } activeSession &&
            (mediaSourceProfileId is null ||
             string.Equals(mediaSourceProfileId, _activeProfileId, StringComparison.Ordinal)))
        {
            using var retention = activeSession.Retain();
            var activeResource = await activeSession.Client.GetAsync(mediaUri, cancellationToken)
                .ConfigureAwait(true);
            if (activeResource.IsDirectory)
            {
                throw new InvalidOperationException("播放历史指向文件夹，无法播放。");
            }

            return (activeResource, activeSession.Acquire(activeResource));
        }

        if (mediaSourceProfileId is null || _profileStore is null)
        {
            throw new InvalidOperationException("请先连接这条播放历史对应的媒体源。");
        }

        var profile = await _profileStore.LoadProfileAsync(mediaSourceProfileId, cancellationToken)
            .ConfigureAwait(true) ?? throw new InvalidOperationException("对应的媒体源已被删除，请重新添加后播放。");
        if (profile.Protocol == MediaSourceProtocol.LocalFolder)
        {
            throw new InvalidOperationException("播放历史中的网络媒体源无效。");
        }

        var credential = string.IsNullOrWhiteSpace(profile.UserName)
            ? null
            : new NetworkCredential(profile.UserName, profile.Password);
        var client = CreateClient(profile.Protocol, profile.Endpoint, credential);
        try
        {
            var resource = await client.GetAsync(mediaUri, cancellationToken).ConfigureAwait(true);
            if (resource.IsDirectory)
            {
                throw new InvalidOperationException("播放历史指向文件夹，无法播放。");
            }

            var session = new MediaSourceClientSession(client);
            var lease = session.Acquire(resource);
            session.Release();
            return (resource, lease);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Password = string.Empty;
        _activeProfileId = null;
        _clientSession?.Release();
        _clientSession = null;
        await ValueTask.CompletedTask;
    }

    public string? ConsumeCredentialPersistenceWarning()
    {
        var warning = _credentialPersistenceWarning;
        _credentialPersistenceWarning = null;
        return warning;
    }

    private async Task UpdateSavedProfileAsync(
        Uri endpoint,
        NetworkCredential? credential,
        CancellationToken cancellationToken)
    {
        if (_profileStore is null)
        {
            if (RememberCredentials)
            {
                SetCredentialPersistenceWarning("系统安全凭证存储尚未配置，本次连接仍然有效，但凭证不会持久化。");
                RememberCredentials = false;
            }

            return;
        }

        try
        {
            if (RememberCredentials)
            {
                var profile = new MediaSourceConnectionProfile
                {
                    Id = Sources.FirstOrDefault(source => source.Protocol == Protocol)?.Id ??
                        Guid.NewGuid().ToString("N"),
                    Name = Sources.FirstOrDefault(source => source.Protocol == Protocol)?.Name ??
                        NormalizeSourceName(null, endpoint.Host, GetProtocolLabel(Protocol)),
                    Protocol = Protocol,
                    Endpoint = endpoint,
                    UserName = credential?.UserName ?? string.Empty,
                    Password = credential?.Password ?? string.Empty,
                };
                await Task.Run(
                    () => _profileStore.SaveProfileAsync(profile, cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(true);
                UpsertSource(profile);
                _activeProfileId = profile.Id;
                OnPropertyChanged(nameof(SourceLabel));
            }
            else
            {
                var existing = Sources.FirstOrDefault(source => source.Protocol == Protocol);
                if (existing is not null)
                {
                    await Task.Run(
                        () => _profileStore.DeleteProfileAsync(existing.Id, cancellationToken).AsTask(),
                        cancellationToken).ConfigureAwait(true);
                    _profiles.Remove(existing.Id);
                    Sources.Remove(existing);
                    if (ReferenceEquals(SelectedSource, existing))
                    {
                        SelectedSource = null;
                    }

                    OnPropertyChanged(nameof(HasSources));
                }
            }
        }
        catch (ArgumentException exception)
        {
            SetCredentialPersistenceWarning("当前连接地址包含不能安全写入普通配置的信息；本次连接仍然有效，连接和凭证不会持久化。");
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Warning,
                $"{Protocol} media source profile was rejected ({exception.GetType().Name}).");
            RememberCredentials = false;
        }
        catch (Exception exception)
        {
            SetCredentialPersistenceWarning(RememberCredentials
                ? "系统安全凭证存储不可用。本次连接仍然有效，凭证只保留在当前运行中。"
                : "无法删除系统安全存储中此前保存的凭证，请稍后重试。");
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{Protocol} media source credential update failed ({exception.GetType().Name}).");
            RememberCredentials = false;
        }
    }

    private void SetCredentialPersistenceWarning(string warning) =>
        _credentialPersistenceWarning = warning;

    private async Task NavigateToAsync(Uri directory, CancellationToken cancellationToken)
    {
        var previous = _currentDirectory;
        await BrowseDirectoryAsync(directory, cancellationToken).ConfigureAwait(true);
        if (previous is not null && !UriEquals(previous, directory))
        {
            _backHistory.Push(previous);
            _forwardHistory.Clear();
            RaiseActionStates();
        }
    }

    private async Task BrowseDirectoryAsync(Uri? directory, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy)
        {
            return;
        }

        var client = _clientSession?.Client ?? throw new InvalidOperationException("尚未打开媒体源。");
        IsBusy = true;
        StatusMessage = "正在读取目录…";
        try
        {
            var resources = await client.BrowseAsync(directory, cancellationToken).ConfigureAwait(true);
            _currentDirectory = directory ?? client.Root;
            ReplaceEntries(resources);
            SelectCurrentFavorite();
            StatusMessage = $"共 {_allEntries.Count} 项";
        }
        catch (Exception exception)
        {
            StatusMessage = $"目录读取失败：{exception.Message}";
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{Protocol} media source directory browse failed ({exception.GetType().Name}).");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReplaceEntries(IReadOnlyList<MediaResource> resources)
    {
        _allEntries.Clear();
        SubtitleChoices.Clear();
        SubtitleChoices.Add(RemoteSubtitleChoiceViewModel.Auto);
        SubtitleChoices.Add(RemoteSubtitleChoiceViewModel.None);
        foreach (var resource in resources)
        {
            var item = new MediaResourceItemViewModel(resource);
            item.SetPlaybackProgress(item.IsVideo
                ? _playbackProgress.Find(resource, _activeProfileId)
                : null);
            _allEntries.Add(item);
        }

        var pairedSubtitles = _allEntries
            .Where(static item => item.IsSubtitle &&
                Path.GetExtension(item.Name).Equals(".idx", StringComparison.OrdinalIgnoreCase))
            .Select(static item => Path.ChangeExtension(item.Name, ".sub"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableSubtitles = _allEntries
            .Where(static item => item.IsSubtitle &&
                Path.GetExtension(item.Name).Equals(".sub", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allEntries.Where(static item => item.IsSubtitle))
        {
            var extension = Path.GetExtension(item.Name);
            if ((extension.Equals(".sub", StringComparison.OrdinalIgnoreCase) &&
                 pairedSubtitles.Contains(item.Name)) ||
                (extension.Equals(".idx", StringComparison.OrdinalIgnoreCase) &&
                 !availableSubtitles.Contains(Path.ChangeExtension(item.Name, ".sub"))))
            {
                continue;
            }

            SubtitleChoices.Add(new RemoteSubtitleChoiceViewModel(item.Resource, item.Name));
        }

        SelectedEntry = null;
        SelectedSubtitle = RemoteSubtitleChoiceViewModel.Auto;
        ApplyEntryView();
        OnPropertyChanged(nameof(CurrentLocation));
        RaiseActionStates();
    }

    private void ApplyEntryView()
    {
        var selectedUri = SelectedEntry?.Resource.Uri;
        var filterText = EntryFilterText.Trim();
        var filtered = _allEntries
            .Where(entry => string.IsNullOrEmpty(filterText) ||
                entry.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .Where(MatchesEntryTypeFilter)
            .ToList();
        filtered.Sort(CompareEntries);

        Entries.Clear();
        foreach (var entry in filtered)
        {
            Entries.Add(entry);
        }

        SelectedEntry = selectedUri is null
            ? null
            : Entries.FirstOrDefault(entry => entry.Resource.Uri == selectedUri);
        OnPropertyChanged(nameof(EntryViewSummary));
    }

    private bool MatchesEntryTypeFilter(MediaResourceItemViewModel entry) => _entryTypeFilter switch
    {
        MediaEntryTypeFilter.All => true,
        MediaEntryTypeFilter.Directory => entry.IsDirectory,
        MediaEntryTypeFilter.Media => entry.IsPlayable || entry.IsImage,
        MediaEntryTypeFilter.Image => entry.IsImage,
        MediaEntryTypeFilter.Subtitle => entry.IsSubtitle,
        MediaEntryTypeFilter.Other => !entry.IsDirectory && !entry.IsPlayable && !entry.IsImage && !entry.IsSubtitle,
        _ => false,
    };

    private int CompareEntries(MediaResourceItemViewModel left, MediaResourceItemViewModel right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        var result = _entrySortOrder switch
        {
            MediaEntrySortOrder.Name => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
            MediaEntrySortOrder.Type => StringComparer.OrdinalIgnoreCase.Compare(left.Type, right.Type),
            MediaEntrySortOrder.Progress => CompareNullable<double>(
                left.HasPlaybackHistory ? left.PlaybackProgress : null,
                right.HasPlaybackHistory ? right.PlaybackProgress : null),
            MediaEntrySortOrder.Size => CompareNullable(left.Resource.Length, right.Resource.Length),
            MediaEntrySortOrder.Modified => CompareNullable(
                left.Resource.LastModified,
                right.Resource.LastModified),
            _ => 0,
        };
        if (result != 0)
        {
            return (_entrySortOrder is MediaEntrySortOrder.Name or MediaEntrySortOrder.Type) && SortDescending
                ? -result : result;
        }

        var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return SortDescending ? -byName : byName;
    }

    private int CompareNullable<T>(T? left, T? right)
        where T : struct, IComparable<T>
    {
        if (left is null || right is null)
        {
            return left is null
                ? right is null ? 0 : 1
                : -1;
        }

        var result = left.Value.CompareTo(right.Value);
        return SortDescending ? -result : result;
    }

    private void RaiseActionStates()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        OnPropertyChanged(nameof(CanOpenFavorite));
        OnPropertyChanged(nameof(CanToggleFavorite));
        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(CanOpenSelected));
        OnPropertyChanged(nameof(CanOpenSelectedSource));
        OnPropertyChanged(nameof(CanRemoveSelectedSource));
    }

    private void RaiseFavoriteStates()
    {
        OnPropertyChanged(nameof(IsCurrentLocationFavorite));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(CanOpenFavorite));
        OnPropertyChanged(nameof(CanToggleFavorite));
    }

    private void ClearNavigationHistory()
    {
        _backHistory.Clear();
        _forwardHistory.Clear();
        RaiseActionStates();
    }

    private async Task<bool> TryLoadFavoritesAsync(CancellationToken cancellationToken)
    {
        if (_favoriteStore is null || _clientSession is null)
        {
            ReplaceFavorites([]);
            return true;
        }

        try
        {
            var client = _clientSession.Client;
            var favorites = await Task.Run(
                () => _favoriteStore.ListAsync(client.Protocol, client.Root, cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(true);
            ReplaceFavorites(favorites);
            return true;
        }
        catch (Exception exception)
        {
            ReplaceFavorites([]);
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                $"{Protocol} media source favorites load failed ({exception.GetType().Name}).");
            return false;
        }
    }

    private void ReplaceFavorites(IReadOnlyList<MediaSourceFavorite> favorites)
    {
        Favorites.Clear();
        foreach (var favorite in favorites)
        {
            Favorites.Add(new MediaSourceFavoriteViewModel(favorite));
        }

        SortFavorites();
        SelectCurrentFavorite();
        RaiseFavoriteStates();
    }

    private void SortFavorites()
    {
        var sorted = Favorites
            .OrderBy(static favorite => favorite.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static favorite => favorite.Location, StringComparer.Ordinal)
            .ToList();
        Favorites.Clear();
        foreach (var favorite in sorted)
        {
            Favorites.Add(favorite);
        }
    }

    private void SelectCurrentFavorite()
    {
        SelectedFavorite = _currentDirectory is null
            ? null
            : Favorites.FirstOrDefault(favorite => UriEquals(favorite.Favorite.Location, _currentDirectory));
        RaiseFavoriteStates();
    }

    private static string GetFavoriteName(Uri root, Uri location)
    {
        if (UriEquals(root, location))
        {
            return $"{root.Host} /";
        }

        var path = Uri.UnescapeDataString(location.AbsolutePath).TrimEnd('/');
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? location.Host : name;
    }

    private static bool UriEquals(Uri left, Uri right) =>
        Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.Ordinal) == 0;

    private (Uri Endpoint, NetworkCredential? Credential) GetConnectionInput()
    {
        if (!Uri.TryCreate(EndpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException($"请输入有效的 {GetProtocolLabel(Protocol)} 地址。");
        }

        if (string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrEmpty(Password))
        {
            throw new ArgumentException("输入密码时必须同时填写用户名。");
        }

        var credential = string.IsNullOrWhiteSpace(UserName)
            ? null
            : new NetworkCredential(UserName, Password);
        return (endpoint, credential);
    }

    private IMediaSourceClient CreateClient(Uri endpoint, NetworkCredential? credential) =>
        CreateClient(Protocol, endpoint, credential);

    private IMediaSourceClient CreateClient(
        MediaSourceProtocol protocol,
        Uri endpoint,
        NetworkCredential? credential) => protocol switch
        {
            MediaSourceProtocol.LocalFolder => new LocalFolderMediaSourceClient(endpoint),
            MediaSourceProtocol.WebDav => _webDavFactory(endpoint, credential),
            MediaSourceProtocol.Ftp => _ftpFactory(endpoint, credential),
            MediaSourceProtocol.Smb => _smbFactory(endpoint, credential),
            _ => throw new NotSupportedException($"Unsupported media source protocol: {protocol}."),
        };

    private static string GetProtocolLabel(MediaSourceProtocol protocol) => protocol switch
    {
        MediaSourceProtocol.LocalFolder => "本地文件夹",
        MediaSourceProtocol.WebDav => "WebDAV",
        MediaSourceProtocol.Ftp => "FTP / FTPS",
        MediaSourceProtocol.Smb => "SMB",
        _ => protocol.ToString(),
    };

    private static Uri ParseEndpoint(MediaSourceProtocol protocol, string endpointText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointText);
        if (!Uri.TryCreate(endpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException($"请输入有效的 {GetProtocolLabel(protocol)} 地址。", nameof(endpointText));
        }

        var validScheme = protocol switch
        {
            MediaSourceProtocol.WebDav => endpoint.Scheme is "http" or "https",
            MediaSourceProtocol.Ftp => endpoint.Scheme is "ftp" or "ftps",
            MediaSourceProtocol.Smb => endpoint.Scheme == "smb",
            _ => false,
        };
        if (!validScheme)
        {
            throw new ArgumentException($"地址协议与 {GetProtocolLabel(protocol)} 类型不匹配。", nameof(endpointText));
        }

        return endpoint;
    }

    private async Task SaveAndAddSourceAsync(
        MediaSourceConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (_profiles.Values.Any(existing =>
                existing.Protocol == profile.Protocol &&
                Uri.Compare(
                    existing.Endpoint,
                    profile.Endpoint,
                    UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0))
        {
            throw new InvalidOperationException("该播放源已经添加。");
        }

        if (_profileStore is not null)
        {
            await _profileStore.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(true);
        }

        UpsertSource(profile);
        SelectedSource = Sources.First(source => source.Id == profile.Id);
    }

    private void ReplaceSources(IReadOnlyList<MediaSourceConnectionProfile> profiles)
    {
        _profiles.Clear();
        Sources.Clear();
        foreach (var profile in profiles)
        {
            _profiles.Add(profile.Id, profile);
            Sources.Add(new PlaybackSourceItemViewModel(profile));
        }

        SelectedSource = null;
        OnPropertyChanged(nameof(HasSources));
    }

    private void UpsertSource(MediaSourceConnectionProfile profile)
    {
        _profiles[profile.Id] = profile;
        var existing = Sources.FirstOrDefault(source => source.Id == profile.Id);
        if (existing is not null)
        {
            Sources.Remove(existing);
        }

        var item = new PlaybackSourceItemViewModel(profile);
        var insertIndex = 0;
        while (insertIndex < Sources.Count &&
               StringComparer.OrdinalIgnoreCase.Compare(Sources[insertIndex].Name, item.Name) <= 0)
        {
            insertIndex++;
        }

        Sources.Insert(insertIndex, item);
        OnPropertyChanged(nameof(HasSources));
    }

    private void SetProtocol(MediaSourceProtocol protocol)
    {
        _protocol = protocol;
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(EndpointLabel));
        OnPropertyChanged(nameof(EndpointPlaceholder));
        OnPropertyChanged(nameof(ProtocolDescription));
        OnPropertyChanged(nameof(SourceLabel));
    }

    private static string NormalizeSourceName(string? name, string fallback, string secondaryFallback)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = string.IsNullOrWhiteSpace(fallback) ? secondaryFallback : fallback.Trim();
        }

        if (normalized.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "播放源名称不能超过 256 个字符。");
        }

        return normalized;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MediaSourceReadLease : IDisposable
{
    private MediaSourceClientSession? _session;
    private readonly MediaResource _resource;

    internal MediaSourceReadLease(MediaSourceClientSession session, MediaResource resource)
    {
        _session = session;
        _resource = resource;
    }

    public IMediaByteStream Open()
    {
        var session = Volatile.Read(ref _session) ??
            throw new ObjectDisposedException(nameof(MediaSourceReadLease));
        return session.Client.OpenReadAsync(_resource).AsTask().GetAwaiter().GetResult();
    }

    public ValueTask<IReadOnlyList<MediaSubtitleMatch>> FindMatchingSubtitlesAsync(
        string? preferredLanguages,
        string mode,
        CancellationToken cancellationToken = default)
    {
        var session = Volatile.Read(ref _session) ??
            throw new ObjectDisposedException(nameof(MediaSourceReadLease));
        return ExternalSubtitleDiscovery.FindAsync(
            session.Client, _resource, preferredLanguages, mode, cancellationToken: cancellationToken);
    }

    public MediaSourceReadLease AcquireSibling(MediaResource resource)
    {
        var session = Volatile.Read(ref _session) ??
            throw new ObjectDisposedException(nameof(MediaSourceReadLease));
        return session.Acquire(resource);
    }

    public async ValueTask<IReadOnlyList<MediaResource>> FindVideoSiblingsAsync(
        CancellationToken cancellationToken = default)
    {
        var session = Volatile.Read(ref _session) ??
            throw new ObjectDisposedException(nameof(MediaSourceReadLease));
        if (!ExternalSubtitleMatcher.IsVideoFile(_resource.Name)) return [_resource];
        var siblings = await session.Client.BrowseAsync(new Uri(_resource.Uri, "."), cancellationToken);
        var videos = siblings.Where(static resource =>
                !resource.IsDirectory && ExternalSubtitleMatcher.IsVideoFile(resource.Name))
            .OrderBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static resource => resource.Name, StringComparer.Ordinal)
            .ToArray();
        return videos.Any(resource => resource.Uri == _resource.Uri) ? videos : [_resource];
    }

    public async ValueTask<MediaResource?> FindSubtitleCompanionAsync(
        MediaResource subtitle,
        CancellationToken cancellationToken = default)
    {
        var session = Volatile.Read(ref _session) ??
            throw new ObjectDisposedException(nameof(MediaSourceReadLease));
        var siblings = await session.Client.BrowseAsync(new Uri(subtitle.Uri, "."), cancellationToken);
        var stem = Path.GetFileNameWithoutExtension(subtitle.Name);
        return siblings.FirstOrDefault(resource => !resource.IsDirectory &&
            Path.GetExtension(resource.Name).Equals(".sub", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileNameWithoutExtension(resource.Name).Equals(stem, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => Interlocked.Exchange(ref _session, null)?.Release();
}

public sealed class MediaImageSequence : IDisposable
{
    private readonly MediaSourceClientSession _session;
    private IDisposable? _retention;

    internal MediaImageSequence(MediaSourceClientSession session, IReadOnlyList<MediaResource> items, int initialIndex)
    {
        _session = session;
        _retention = session.Retain();
        Items = items;
        InitialIndex = initialIndex;
    }

    public IReadOnlyList<MediaResource> Items { get; }

    public int InitialIndex { get; }

    public ValueTask<IMediaByteStream> OpenReadAsync(int index, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_retention is null, this);
        return _session.Client.OpenReadAsync(Items[index], cancellationToken);
    }

    public void Dispose() => Interlocked.Exchange(ref _retention, null)?.Dispose();
}

internal sealed class MediaSourceClientSession
{
    private readonly Lock _sync = new();
    private IMediaSourceClient? _client;
    private int _referenceCount = 1;

    public MediaSourceClientSession(IMediaSourceClient client)
    {
        _client = client;
    }

    public IMediaSourceClient Client
    {
        get
        {
            lock (_sync)
            {
                return _client ?? throw new ObjectDisposedException(nameof(MediaSourceClientSession));
            }
        }
    }

    public MediaSourceReadLease Acquire(MediaResource resource)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_client is null, this);
            _referenceCount++;
            return new MediaSourceReadLease(this, resource);
        }
    }

    public IDisposable Retain()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_client is null, this);
            _referenceCount++;
            return new SessionRetention(this);
        }
    }

    private sealed class SessionRetention(MediaSourceClientSession session) : IDisposable
    {
        private MediaSourceClientSession? _session = session;

        public void Dispose() => Interlocked.Exchange(ref _session, null)?.Release();
    }

    public void Release()
    {
        IMediaSourceClient? client = null;
        lock (_sync)
        {
            if (_referenceCount <= 0)
            {
                return;
            }

            _referenceCount--;
            if (_referenceCount == 0)
            {
                client = _client;
                _client = null;
            }
        }

        if (client is null)
        {
            return;
        }

        try
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // A retired source must not make switching source or closing the app fail.
        }
    }
}

public sealed class MediaResourceItemViewModel(MediaResource resource) : INotifyPropertyChanged
{
    private bool _hasPlaybackHistory;
    private double _playbackProgress;
    private string _playbackProgressText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaResource Resource { get; } = resource;

    public string Name => Resource.Name;

    public bool IsDirectory => Resource.IsDirectory;

    public bool IsPlayable => !IsDirectory && MediaFileClassifier.IsPlayable(Name);

    public bool IsVideo => !IsDirectory && ExternalSubtitleMatcher.IsVideoFile(Name);

    public bool HasPlaybackHistory => _hasPlaybackHistory;

    public double PlaybackProgress => _playbackProgress;

    public string PlaybackProgressText => _playbackProgressText;

    public bool IsImage => !IsDirectory && MediaFileClassifier.IsImage(Name);

    public bool IsSubtitle => !IsDirectory && MediaFileClassifier.IsSubtitle(Name);

    public string Type => IsDirectory
        ? "文件夹"
        : IsSubtitle
            ? "字幕"
            : IsImage
                ? "图片"
            : IsPlayable
                ? "媒体"
                : Resource.ContentType ?? "文件";

    public string Size => Resource.Length is { } length && !IsDirectory
        ? FormatSize(length)
        : string.Empty;

    public string Modified => Resource.LastModified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    public void SetPlaybackProgress(PlaybackRecentItemViewModel? item)
    {
        var hasHistory = IsVideo && item is not null;
        var progress = hasHistory ? item!.Progress : 0;
        var text = hasHistory ? $"{item!.ProgressText} · {item.LastPlayedText}" : string.Empty;
        if (_hasPlaybackHistory != hasHistory)
        {
            _hasPlaybackHistory = hasHistory;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlaybackHistory)));
        }

        if (_playbackProgress != progress)
        {
            _playbackProgress = progress;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaybackProgress)));
        }

        if (_playbackProgressText != text)
        {
            _playbackProgressText = text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaybackProgressText)));
        }
    }

    private static string FormatSize(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}

public sealed record RemoteSubtitleChoiceViewModel(MediaResource? Resource, string Label)
{
    public static RemoteSubtitleChoiceViewModel Auto { get; } = new(null, "自动匹配");

    public static RemoteSubtitleChoiceViewModel None { get; } = new(null, "不加载字幕");

    public override string ToString() => Label;
}

public sealed class MediaSourceFavoriteViewModel(MediaSourceFavorite favorite)
{
    public MediaSourceFavorite Favorite { get; } = favorite;

    public string Name => Favorite.Name;

    public string Location => Uri.UnescapeDataString(Favorite.Location.AbsolutePath);

    public override string ToString() => $"{Name} — {Location}";
}

public enum MediaEntryTypeFilter
{
    All,
    Directory,
    Media,
    Image,
    Subtitle,
    Other,
}

public enum MediaEntrySortOrder
{
    Name,
    Type,
    Progress,
    Size,
    Modified,
}

public sealed record MediaEntryTypeFilterOption(MediaEntryTypeFilter Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record MediaEntrySortOption(MediaEntrySortOrder Value, string Label)
{
    public override string ToString() => Label;
}

internal static class MediaFileClassifier
{
    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".bmp" or ".gif" or ".jpeg" or ".jpg" or ".png" or ".webp";

    public static bool IsPlayable(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".3gp" or ".aac" or ".ac3" or ".aiff" or ".ape" or ".asf" or ".avi" or
        ".flac" or ".flv" or ".m2ts" or ".m4a" or ".m4v" or ".mka" or ".mkv" or
        ".mov" or ".mp2" or ".mp3" or ".mp4" or ".mpeg" or ".mpg" or ".mts" or
        ".oga" or ".ogg" or ".ogm" or ".ogv" or ".opus" or ".ts" or ".wav" or
        ".webm" or ".wma" or ".wmv";

    public static bool IsSubtitle(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".ass" or ".idx" or ".smi" or ".srt" or ".ssa" or ".sub" or ".sup" or ".vtt";
}
