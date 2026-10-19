// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using CanX.App.ViewModels;
using CanX.MediaAccess;
using CanX.Streaming;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class MediaSourceBrowserViewModelTests
{
    [Fact]
    public async Task ConnectBrowseAndOpen_KeepCredentialsInSessionAndMatchSubtitle()
    {
        var root = new Uri("https://dav.example.test/media/");
        var folder = Resource("Series/", isDirectory: true);
        var movie = Resource("movie.mkv", length: 4);
        var subtitle = Resource("movie.zh-Hans.ass", length: 3);
        var unrelatedSubtitle = Resource("movie2.srt", length: 3);
        var client = new FakeMediaSourceClient(root, [folder, movie, unrelatedSubtitle, subtitle]);
        NetworkCredential? receivedCredential = null;
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (endpoint, credential) =>
            {
                Assert.Equal(root, endpoint);
                receivedCredential = credential;
                return client;
            })
        {
            EndpointText = root.AbsoluteUri,
            UserName = "alice",
            Password = "secret",
        };

        await viewModel.ConnectAsync();

        Assert.True(viewModel.IsConnected);
        Assert.Equal("alice", receivedCredential?.UserName);
        Assert.Equal("secret", receivedCredential?.Password);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal(4, viewModel.Entries.Count);
        Assert.Equal(4, viewModel.SubtitleChoices.Count);
        Assert.Equal(root.AbsolutePath, viewModel.CurrentLocation);

        viewModel.SelectedEntry = viewModel.Entries.Single(entry => entry.Name == movie.Name);

        Assert.True(viewModel.CanOpenSelected);
        Assert.Same(RemoteSubtitleChoiceViewModel.Auto, viewModel.SelectedSubtitle);
        using var stream = await viewModel.OpenReadAsync(movie);
        Assert.Equal(4, stream.Length);
        Assert.Same(movie, client.LastOpenedResource);

        viewModel.SelectedEntry = viewModel.Entries.Single(entry => entry.IsDirectory);
        await viewModel.OpenSelectedDirectoryAsync();

        Assert.Equal(folder.Uri, client.LastBrowseDirectory);
        Assert.Empty(viewModel.Entries);
        Assert.True(viewModel.CanNavigateUp);
    }

    [Fact]
    public async Task ConnectAsync_RejectsRelativeEndpointBeforeCreatingClient()
    {
        var factoryCalled = false;
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) =>
            {
                factoryCalled = true;
                throw new InvalidOperationException();
            })
        {
            EndpointText = "dav/media",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => viewModel.ConnectAsync());

        Assert.False(factoryCalled);
        Assert.False(viewModel.IsConnected);
    }

    [Fact]
    public async Task Browse_ShowsIdxSubPairAsOneManualChoice()
    {
        var root = new Uri("https://dav.example.test/media/");
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) => new FakeMediaSourceClient(root,
            [Resource("movie.mkv"), Resource("movie.zh.idx"), Resource("movie.zh.sub")]))
        {
            EndpointText = root.AbsoluteUri,
        };

        await viewModel.ConnectAsync();

        Assert.Equal(["自动匹配", "不加载字幕", "movie.zh.idx"],
            viewModel.SubtitleChoices.Select(static choice => choice.Label));
    }

    [Fact]
    public async Task SelectProtocolAsync_ClearsPreviousSessionAndUsesFtpFactory()
    {
        var root = new Uri("ftp://ftp.example.test/media/");
        var ftpClient = new FakeMediaSourceClient(root, []);
        var ftpFactoryCalled = false;
        await using var viewModel = new MediaSourceBrowserViewModel(
            ftpFactory: (endpoint, _) =>
            {
                ftpFactoryCalled = true;
                Assert.Equal(root, endpoint);
                return ftpClient;
            });

        await viewModel.SelectProtocolAsync(MediaSourceProtocol.Ftp);
        viewModel.EndpointText = root.AbsoluteUri;
        await viewModel.ConnectAsync();

        Assert.True(ftpFactoryCalled);
        Assert.Equal(MediaSourceProtocol.Ftp, viewModel.Protocol);
        Assert.Equal("FTP / FTPS 媒体源", viewModel.PageTitle);
        Assert.True(viewModel.IsConnected);
    }

    [Fact]
    public async Task SelectProtocolAsync_UsesSmbFactory()
    {
        var root = new Uri("smb://server.example/media/");
        var smbClient = new FakeMediaSourceClient(root, []);
        var smbFactoryCalled = false;
        await using var viewModel = new MediaSourceBrowserViewModel(
            smbFactory: (endpoint, _) =>
            {
                smbFactoryCalled = true;
                Assert.Equal(root, endpoint);
                return smbClient;
            });

        await viewModel.SelectProtocolAsync(MediaSourceProtocol.Smb);
        viewModel.EndpointText = root.AbsoluteUri;
        await viewModel.ConnectAsync();

        Assert.True(smbFactoryCalled);
        Assert.Equal(MediaSourceProtocol.Smb, viewModel.Protocol);
        Assert.Equal("SMB 媒体源", viewModel.PageTitle);
        Assert.True(viewModel.IsConnected);
    }

    [Fact]
    public async Task ReadLease_KeepsPreviousConnectionAliveAcrossProtocolSwitch()
    {
        var webDavRoot = new Uri("https://dav.example.test/media/");
        var media = Resource("movie.mkv", length: 4);
        var webDavClient = new FakeMediaSourceClient(webDavRoot, [media]);
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) => webDavClient);

        viewModel.EndpointText = webDavRoot.AbsoluteUri;
        await viewModel.ConnectAsync();
        using var lease = viewModel.AcquireRead(media);

        await viewModel.SelectProtocolAsync(MediaSourceProtocol.Ftp);

        Assert.False(webDavClient.IsDisposed);
        using var stream = lease.Open();
        Assert.Equal(4, stream.Length);

        lease.Dispose();
        Assert.True(webDavClient.IsDisposed);
    }

    [Fact]
    public async Task SelectProtocolAsync_LoadsRememberedConnectionFromProfileStore()
    {
        var root = new Uri("https://dav.example.test/media/");
        var profileStore = new FakeMediaSourceProfileStore
        {
            Profile = new MediaSourceConnectionProfile
            {
                Id = "remembered-webdav",
                Name = "Remembered WebDAV",
                Protocol = MediaSourceProtocol.WebDav,
                Endpoint = root,
                UserName = "alice",
                Password = "secret",
            },
        };
        await using var viewModel = new MediaSourceBrowserViewModel(profileStore: profileStore);

        await viewModel.SelectProtocolAsync(MediaSourceProtocol.WebDav);

        Assert.Equal(root.AbsoluteUri, viewModel.EndpointText);
        Assert.Equal("alice", viewModel.UserName);
        Assert.Equal("secret", viewModel.Password);
        Assert.Equal(1, profileStore.LoadCount);
        Assert.True(viewModel.RememberCredentials);
    }

    [Fact]
    public async Task ConnectAsync_RemembersProfileOnlyAfterSuccessfulConnection()
    {
        var root = new Uri("https://dav.example.test/media/");
        var profileStore = new FakeMediaSourceProfileStore();
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: profileStore,
            webDavFactory: (_, _) => new FakeMediaSourceClient(root, []))
        {
            EndpointText = root.AbsoluteUri,
            UserName = "alice",
            Password = "secret",
            RememberCredentials = true,
        };

        await viewModel.ConnectAsync();

        Assert.True(viewModel.IsConnected);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal("alice", profileStore.Profile?.UserName);
        Assert.Equal("secret", profileStore.Profile?.Password);
        Assert.Equal(profileStore.Profile?.Id, viewModel.ActiveProfileId);
        Assert.Null(viewModel.ConsumeCredentialPersistenceWarning());
    }

    [Fact]
    public async Task ConnectAsync_KeepsSessionWhenCredentialStoreIsUnavailable()
    {
        var root = new Uri("https://dav.example.test/media/");
        var profileStore = new FakeMediaSourceProfileStore { FailSave = true };
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: profileStore,
            webDavFactory: (_, _) => new FakeMediaSourceClient(root, []))
        {
            EndpointText = root.AbsoluteUri,
            UserName = "alice",
            Password = "secret",
            RememberCredentials = true,
        };

        await viewModel.ConnectAsync();

        Assert.True(viewModel.IsConnected);
        Assert.False(viewModel.RememberCredentials);
        Assert.Null(viewModel.ActiveProfileId);
        Assert.NotNull(viewModel.ConsumeCredentialPersistenceWarning());
        Assert.Null(viewModel.ConsumeCredentialPersistenceWarning());
    }

    [Fact]
    public async Task TestConnectionAsync_DoesNotPersistOrCreateAnActiveSession()
    {
        var root = new Uri("https://dav.example.test/media/");
        var testedClient = new FakeMediaSourceClient(root, [Resource("movie.mkv", length: 4)]);
        var profileStore = new FakeMediaSourceProfileStore();
        NetworkCredential? receivedCredential = null;
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: profileStore,
            webDavFactory: (_, credential) =>
            {
                receivedCredential = credential;
                return testedClient;
            })
        {
            EndpointText = root.AbsoluteUri,
            UserName = "alice",
            Password = "secret",
            RememberCredentials = true,
        };

        await viewModel.TestConnectionAsync();

        Assert.False(viewModel.IsConnected);
        Assert.Empty(viewModel.Entries);
        Assert.True(testedClient.IsDisposed);
        Assert.Equal("alice", receivedCredential?.UserName);
        Assert.Equal("secret", receivedCredential?.Password);
        Assert.Equal("secret", viewModel.Password);
        Assert.Null(profileStore.Profile);
        Assert.Contains("连接测试成功", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectoryNavigation_MaintainsBackAndForwardHistory()
    {
        var root = new Uri("https://dav.example.test/media/");
        var folder = Resource("Series/", isDirectory: true);
        var client = new FakeMediaSourceClient(root, [folder]);
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) => client)
        {
            EndpointText = root.AbsoluteUri,
        };
        await viewModel.ConnectAsync();
        viewModel.SelectedEntry = viewModel.Entries.Single();

        await viewModel.OpenSelectedDirectoryAsync();

        Assert.True(viewModel.CanNavigateBack);
        Assert.False(viewModel.CanNavigateForward);
        Assert.Equal(folder.Uri, client.LastBrowseDirectory);

        await viewModel.NavigateBackAsync();

        Assert.False(viewModel.CanNavigateBack);
        Assert.True(viewModel.CanNavigateForward);
        Assert.Equal(root, client.LastBrowseDirectory);

        await viewModel.NavigateForwardAsync();

        Assert.True(viewModel.CanNavigateBack);
        Assert.False(viewModel.CanNavigateForward);
        Assert.Equal(folder.Uri, client.LastBrowseDirectory);
    }

    [Fact]
    public async Task Favorites_AddOpenAndRemoveDirectoriesForTheConnectedSource()
    {
        var root = new Uri("https://dav.example.test/media/");
        var folder = Resource("Series/", isDirectory: true);
        var client = new FakeMediaSourceClient(root, [folder]);
        var favoriteStore = new FakeMediaSourceFavoriteStore();
        await using var viewModel = new MediaSourceBrowserViewModel(
            favoriteStore: favoriteStore,
            webDavFactory: (_, _) => client)
        {
            EndpointText = root.AbsoluteUri,
        };
        await viewModel.ConnectAsync();

        await viewModel.ToggleCurrentFavoriteAsync();
        Assert.True(viewModel.IsCurrentLocationFavorite);
        Assert.Equal("取消收藏", viewModel.FavoriteActionText);

        viewModel.SelectedEntry = viewModel.Entries.Single();
        await viewModel.OpenSelectedDirectoryAsync();
        await viewModel.ToggleCurrentFavoriteAsync();
        Assert.Equal(2, viewModel.Favorites.Count);

        viewModel.SelectedFavorite = viewModel.Favorites.Single(item => item.Favorite.Location == root);
        await viewModel.OpenSelectedFavoriteAsync();
        Assert.Equal(root, client.LastBrowseDirectory);

        await viewModel.ToggleCurrentFavoriteAsync();
        Assert.Single(viewModel.Favorites);
        Assert.False(viewModel.IsCurrentLocationFavorite);
    }

    [Fact]
    public async Task EntryView_FiltersByNameAndTypeAndSortsDirectoriesFirst()
    {
        var root = new Uri("https://dav.example.test/media/");
        var resources = new[]
        {
            Resource("z-folder/", isDirectory: true),
            Resource("episode-02.mkv", length: 300, modified: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            Resource("episode-01.mp4", length: 100, modified: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Resource("episode-01.zh.srt", length: 20),
            Resource("notes.txt", length: 10),
        };
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) => new FakeMediaSourceClient(root, resources))
        {
            EndpointText = root.AbsoluteUri,
        };
        await viewModel.ConnectAsync();

        Assert.Equal("z-folder", viewModel.Entries[0].Name);
        viewModel.EntryFilterText = "EPISODE-01";
        Assert.Equal(2, viewModel.Entries.Count);
        Assert.Equal("显示 2 / 5 项", viewModel.EntryViewSummary);
        Assert.Equal(["episode-01.mp4", "episode-02.mkv"],
            viewModel.GetVideoSiblings(resources[2]).Select(static resource => resource.Name));

        viewModel.SelectedEntryTypeFilter = viewModel.EntryTypeFilterOptions.Single(option =>
            option.Value == MediaEntryTypeFilter.Media);
        Assert.Single(viewModel.Entries);
        Assert.Equal("episode-01.mp4", viewModel.Entries[0].Name);

        viewModel.EntryFilterText = string.Empty;
        viewModel.SelectedEntryTypeFilter = viewModel.EntryTypeFilterOptions[0];
        viewModel.SelectedEntrySort = viewModel.EntrySortOptions.Single(option =>
            option.Value == MediaEntrySortOrder.Size);
        viewModel.SortDescending = true;
        Assert.Equal(
            ["z-folder", "episode-02.mkv", "episode-01.mp4", "episode-01.zh.srt", "notes.txt"],
            viewModel.Entries.Select(entry => entry.Name));
        Assert.Equal(["episode-02.mkv", "episode-01.mp4"],
            viewModel.GetVideoSiblings(resources[2]).Select(static resource => resource.Name));
    }

    [Fact]
    public async Task HeaderSort_TogglesDirectionAndSortsEachDisplayedColumn()
    {
        var root = new Uri("https://dav.example.test/media/");
        var resources = new[]
        {
            Resource("folder/", isDirectory: true),
            Resource("a.mkv", length: 300, modified: new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)),
            Resource("b.mp4", length: 100, modified: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Resource("c.txt", length: 200, modified: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
        };
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) => new FakeMediaSourceClient(root, resources))
        {
            EndpointText = root.AbsoluteUri,
        };
        await viewModel.ConnectAsync();
        viewModel.SelectedEntry = viewModel.Entries.Single(static entry => entry.Name == "b.mp4");

        Assert.Equal("▲", viewModel.NameSortIndicator);
        viewModel.SortEntriesBy(MediaEntrySortOrder.Name);
        Assert.Equal("▼", viewModel.NameSortIndicator);
        Assert.Equal(["folder", "c.txt", "b.mp4", "a.mkv"],
            viewModel.Entries.Select(static entry => entry.Name));
        viewModel.SortEntriesBy(MediaEntrySortOrder.Name);
        Assert.Equal("▲", viewModel.NameSortIndicator);
        viewModel.SortEntriesBy(MediaEntrySortOrder.Size);
        Assert.Equal("▲", viewModel.SizeSortIndicator);
        Assert.Equal(string.Empty, viewModel.NameSortIndicator);
        Assert.Equal(["folder", "b.mp4", "c.txt", "a.mkv"],
            viewModel.Entries.Select(static entry => entry.Name));
        viewModel.SortEntriesBy(MediaEntrySortOrder.Size);
        Assert.Equal("▼", viewModel.SizeSortIndicator);
        Assert.Equal(["folder", "a.mkv", "c.txt", "b.mp4"],
            viewModel.Entries.Select(static entry => entry.Name));
        Assert.Equal("b.mp4", viewModel.SelectedEntry?.Name);

        viewModel.SortEntriesBy(MediaEntrySortOrder.Modified);
        Assert.Equal("▲", viewModel.ModifiedSortIndicator);
        Assert.Equal(["folder", "b.mp4", "c.txt", "a.mkv"],
            viewModel.Entries.Select(static entry => entry.Name));
        viewModel.SortEntriesBy(MediaEntrySortOrder.Type);
        Assert.Equal("▲", viewModel.TypeSortIndicator);
        Assert.Equal(["folder", "a.mkv", "b.mp4", "c.txt"],
            viewModel.Entries.Select(static entry => entry.Name));
        viewModel.SortEntriesBy(MediaEntrySortOrder.Type);
        Assert.Equal("▼", viewModel.TypeSortIndicator);
        Assert.Equal(["folder", "c.txt", "b.mp4", "a.mkv"],
            viewModel.Entries.Select(static entry => entry.Name));

        viewModel.SetPlaybackHistory(
        [
            new PlaybackRecentItemViewModel(resources[1].Uri.AbsoluteUri, "a", 25, 100,
                DateTimeOffset.UtcNow, false, true, null),
            new PlaybackRecentItemViewModel(resources[2].Uri.AbsoluteUri, "b", 75, 100,
                DateTimeOffset.UtcNow, false, true, null),
        ]);
        viewModel.SortEntriesBy(MediaEntrySortOrder.Progress);
        Assert.Equal("▲", viewModel.ProgressSortIndicator);
        Assert.Equal(["folder", "a.mkv", "b.mp4", "c.txt"],
            viewModel.Entries.Select(static entry => entry.Name));
        viewModel.SortEntriesBy(MediaEntrySortOrder.Progress);
        Assert.Equal("▼", viewModel.ProgressSortIndicator);
        Assert.Equal(["folder", "b.mp4", "a.mkv", "c.txt"],
            viewModel.Entries.Select(static entry => entry.Name));
        viewModel.SetPlaybackHistory([]);
        Assert.Equal(["folder", "c.txt", "b.mp4", "a.mkv"],
            viewModel.Entries.Select(static entry => entry.Name));
    }

    [Fact]
    public async Task ImageSequence_UsesVisibleFileOrderAndKeepsSourceAlive()
    {
        var root = new Uri("https://dav.example.test/media/");
        var resources = new[]
        {
            Resource("folder/", isDirectory: true),
            Resource("small.jpg", length: 10),
            Resource("large.png", length: 30),
            Resource("middle.webp", length: 20),
            Resource("movie.mkv", length: 40),
            Resource("notes.txt", length: 5),
        };
        var client = new FakeMediaSourceClient(root, resources);
        await using var viewModel = new MediaSourceBrowserViewModel(webDavFactory: (_, _) => client)
        {
            EndpointText = root.AbsoluteUri,
        };
        await viewModel.ConnectAsync();
        viewModel.SelectedEntryTypeFilter = viewModel.EntryTypeFilterOptions.Single(option =>
            option.Value == MediaEntryTypeFilter.Image);
        viewModel.SelectedEntrySort = viewModel.EntrySortOptions.Single(option =>
            option.Value == MediaEntrySortOrder.Size);
        viewModel.SortDescending = true;

        Assert.Equal(["large.png", "middle.webp", "small.jpg"],
            viewModel.Entries.Select(static entry => entry.Name));
        viewModel.SelectedEntry = viewModel.Entries[1];
        Assert.True(viewModel.CanOpenSelected);
        Assert.Equal("查看", viewModel.SelectedActionText);
        using var sequence = viewModel.AcquireImageSequence(viewModel.SelectedEntry.Resource);
        Assert.Equal(1, sequence.InitialIndex);
        Assert.Equal(["large.png", "middle.webp", "small.jpg"],
            sequence.Items.Select(static item => item.Name));

        await viewModel.SelectProtocolAsync(MediaSourceProtocol.Ftp);
        Assert.False(client.IsDisposed);
        using (var stream = await sequence.OpenReadAsync(0))
        {
            Assert.Equal(30, stream.Length);
        }
        Assert.Equal("large.png", client.LastOpenedResource?.Name);
        sequence.Dispose();
        Assert.True(client.IsDisposed);
    }

    [Fact]
    public async Task InitializeAndAddLocalFolder_ShowSavedSourcesInOneList()
    {
        using var directory = new TemporaryDirectory();
        var store = new FakeMediaSourceProfileStore
        {
            Profile = new MediaSourceConnectionProfile
            {
                Id = "saved-webdav",
                Name = "家庭媒体库",
                Protocol = MediaSourceProtocol.WebDav,
                Endpoint = new Uri("https://dav.example.test/media/"),
            },
        };
        await using var viewModel = new MediaSourceBrowserViewModel(profileStore: store);

        await viewModel.InitializeAsync();
        var local = await viewModel.AddLocalFolderAsync(directory.Path, "本地动画");

        Assert.Equal(2, viewModel.Sources.Count);
        Assert.Equal("本地动画", local.Name);
        Assert.True(local.IsLocalFolder);
        Assert.Equal(directory.Path, local.LocalPath?.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(viewModel.CanOpenSelectedSource);
        Assert.Equal(2, store.Profiles.Count);
    }

    [Fact]
    public async Task OpenLocalFolder_ListsEveryEntryAndOnlyEnablesPlayableFiles()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "Season 1"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "episode.mkv"), [1, 2, 3, 4]);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "episode.zh-CN.ass"), "subtitle");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "notes.txt"), "notes");

        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: new FakeMediaSourceProfileStore());
        await viewModel.AddLocalFolderAsync(directory.Path, "本地测试");

        await viewModel.OpenSelectedSourceAsync();

        Assert.True(viewModel.IsConnected);
        Assert.Equal(MediaSourceProtocol.LocalFolder, viewModel.Protocol);
        Assert.Equal("本地媒体文件夹", viewModel.PageTitle);
        Assert.Equal(
            ["Season 1", "episode.mkv", "episode.zh-CN.ass", "notes.txt"],
            viewModel.Entries.Select(static entry => entry.Name));

        viewModel.SelectedEntry = viewModel.Entries.Single(static entry => entry.Name == "notes.txt");
        Assert.False(viewModel.CanOpenSelected);

        viewModel.SelectedEntry = viewModel.Entries.Single(static entry => entry.Name == "episode.mkv");
        Assert.True(viewModel.CanOpenSelected);
        Assert.Same(RemoteSubtitleChoiceViewModel.Auto, viewModel.SelectedSubtitle);

        viewModel.SelectedEntry = viewModel.Entries.Single(static entry => entry.Name == "Season 1");
        Assert.True(viewModel.CanOpenSelected);
        Assert.Equal("进入", viewModel.SelectedActionText);
        await viewModel.OpenSelectedDirectoryAsync();
        Assert.Empty(viewModel.Entries);
        Assert.True(viewModel.CanNavigateUp);
    }

    [Fact]
    public async Task SubtitleBrowser_StartsBesideVideoAndKeepsSelectedFileAfterClosing()
    {
        using var directory = new TemporaryDirectory();
        var season = Path.Combine(directory.Path, "Season 1");
        Directory.CreateDirectory(season);
        Directory.CreateDirectory(Path.Combine(season, "Subs"));
        var video = Path.Combine(season, "episode.mkv");
        var subtitle = Path.Combine(season, "episode.zh-CN.ass");
        await File.WriteAllTextAsync(video, "video");
        await File.WriteAllTextAsync(subtitle, "subtitle");
        await File.WriteAllTextAsync(Path.Combine(season, "notes.txt"), "notes");

        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: new FakeMediaSourceProfileStore());
        var source = await viewModel.AddLocalFolderAsync(directory.Path, "本地测试");
        var browser = await viewModel.OpenSubtitleSourceAsync(source.Id, new Uri(video));

        Assert.Equal(new Uri(season + Path.DirectorySeparatorChar), browser.CurrentDirectory);
        Assert.Equal(["Subs", "episode.zh-CN.ass"], browser.Entries.Select(entry => entry.Name));
        Assert.True(browser.CanNavigateUp);
        using var lease = browser.AcquireRead(browser.Entries.Single(entry => !entry.IsDirectory));
        await browser.DisposeAsync();
        using var stream = lease.Open();
        Assert.Equal("subtitle".Length, stream.Length);
    }

    [Fact]
    public void SubtitlePickerLocation_UsesVideoFolderOnlyForLocalFiles()
    {
        using var directory = new TemporaryDirectory();
        var video = Path.Combine(directory.Path, "movie.mkv");

        Assert.Equal(directory.Path, SubtitlePickerLocation.GetLocalVideoDirectory(video));
        Assert.Equal(directory.Path, SubtitlePickerLocation.GetLocalVideoDirectory(new Uri(video).AbsoluteUri));
        Assert.Null(SubtitlePickerLocation.GetLocalVideoDirectory("https://example.test/movie.mkv"));
        Assert.Null(SubtitlePickerLocation.GetLocalVideoDirectory("canx://stream/123"));
    }

    [Fact]
    public async Task SubtitleBrowser_OpensSavedNetworkSourceAtVideoFolderWithCredentials()
    {
        var root = new Uri("https://dav.example.test/media/");
        var profile = new MediaSourceConnectionProfile
        {
            Id = "subtitle-source",
            Name = "WebDAV 字幕",
            Protocol = MediaSourceProtocol.WebDav,
            Endpoint = root,
            UserName = "alice",
            Password = "secret",
        };
        var store = new FakeMediaSourceProfileStore { Profile = profile };
        var subtitle = Resource("movie.zh.srt", length: 8);
        FakeMediaSourceClient? client = null;
        NetworkCredential? credential = null;
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: store,
            webDavFactory: (endpoint, suppliedCredential) =>
            {
                Assert.Equal(root, endpoint);
                credential = suppliedCredential;
                return client = new FakeMediaSourceClient(root,
                    [subtitle, Resource("movie.mkv"), Resource("Subs/", isDirectory: true)]);
            });
        await viewModel.InitializeAsync();

        var browser = await viewModel.OpenSubtitleSourceAsync(profile.Id,
            new Uri(root, "Series/movie.mkv"));
        Assert.Equal(new Uri(root, "Series/"), client!.LastBrowseDirectory);
        Assert.Equal("alice", credential?.UserName);
        Assert.Equal("secret", credential?.Password);
        await browser.NavigateUpAsync();
        Assert.Equal(["Subs", "movie.zh.srt"], browser.Entries.Select(entry => entry.Name));

        using var lease = browser.AcquireRead(subtitle);
        await browser.DisposeAsync();
        Assert.False(client.IsDisposed);
        lease.Dispose();
        Assert.True(client.IsDisposed);

        viewModel.SelectedSource = Assert.Single(viewModel.Sources);
        await viewModel.OpenSelectedSourceAsync();
        var activeClient = client;
        var profileLoads = store.LoadCount;
        await using var activeBrowser = await viewModel.OpenSubtitleSourceAsync(profile.Id);
        Assert.Same(activeClient, client);
        Assert.Equal(profileLoads, store.LoadCount);
    }

    [Fact]
    public async Task AddNetworkSource_ValidatesConnectionBeforeSavingAndCanBeReopened()
    {
        var root = new Uri("https://dav.example.test/media/");
        var clients = new List<FakeMediaSourceClient>();
        var store = new FakeMediaSourceProfileStore();
        NetworkCredential? openedCredential = null;
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: store,
            webDavFactory: (_, credential) =>
            {
                openedCredential = credential;
                var client = new FakeMediaSourceClient(root, [Resource("movie.mkv", length: 4)]);
                clients.Add(client);
                return client;
            });

        var source = await viewModel.AddNetworkSourceAsync(
            MediaSourceProtocol.WebDav,
            root.AbsoluteUri,
            "远程电影",
            "alice",
            "secret");

        Assert.True(clients[0].IsDisposed);
        Assert.Single(store.Profiles);
        Assert.Equal("远程电影", source.Name);

        await viewModel.OpenSelectedSourceAsync();

        Assert.True(viewModel.IsConnected);
        Assert.Single(viewModel.Entries);
        Assert.Equal("远程电影 · WebDAV", viewModel.SourceLabel);
        Assert.Equal("secret", openedCredential?.Password);
        Assert.Equal(1, store.LoadCount);
    }

    [Fact]
    public async Task HistoryRead_ReopensSavedSourceWithoutOpeningBrowser()
    {
        var root = new Uri("https://dav.example.test/media/");
        var movie = Resource("movie.mkv", length: 4);
        var earlierMovie = Resource("01.mp4", length: 4);
        var subtitle = Resource("movie.zh.srt", length: 4);
        var store = new FakeMediaSourceProfileStore
        {
            Profile = new MediaSourceConnectionProfile
            {
                Id = "saved-source",
                Name = "Saved source",
                Protocol = MediaSourceProtocol.WebDav,
                Endpoint = root,
                UserName = "alice",
                Password = "secret",
            },
        };
        FakeMediaSourceClient? client = null;
        NetworkCredential? credential = null;
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: store,
            webDavFactory: (endpoint, suppliedCredential) =>
            {
                Assert.Equal(root, endpoint);
                credential = suppliedCredential;
                return client = new FakeMediaSourceClient(root, [movie, subtitle, earlierMovie]);
            });

        var (resource, lease) = await viewModel.AcquireHistoryReadAsync(movie.Uri, "saved-source");
        Assert.Equal(movie, resource);
        Assert.Equal("secret", credential?.Password);
        Assert.False(viewModel.IsConnected);
        Assert.False(client!.IsDisposed);
        Assert.Equal(["01.mp4", "movie.mkv"],
            (await lease.FindVideoSiblingsAsync()).Select(static sibling => sibling.Name));
        using (var stream = lease.Open())
        {
            Assert.Equal(4, stream.Length);
        }

        lease.Dispose();
        Assert.True(client.IsDisposed);
    }

    [Fact]
    public async Task HistoryRead_UsesCurrentUnsavedConnection()
    {
        var root = new Uri("https://dav.example.test/media/");
        var movie = Resource("movie.mkv", length: 4);
        var client = new FakeMediaSourceClient(root, [movie]);
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) => client)
        {
            EndpointText = root.AbsoluteUri,
        };
        await viewModel.ConnectAsync();

        var (resource, lease) = await viewModel.AcquireHistoryReadAsync(movie.Uri, null);
        Assert.Equal(movie, resource);
        await viewModel.SelectProtocolAsync(MediaSourceProtocol.Ftp);
        Assert.False(client.IsDisposed);

        lease.Dispose();
        Assert.True(client.IsDisposed);
    }

    [Fact]
    public async Task RecentFolder_OpensLocalDirectoryWithoutSavingAPlaybackSource()
    {
        using var directory = new TemporaryDirectory();
        var folderPath = Path.Combine(directory.Path, "Season 1");
        Directory.CreateDirectory(folderPath);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "episode.mkv"), "video");
        await using var viewModel = new MediaSourceBrowserViewModel();
        var folder = new PlaybackRecentFolderViewModel(
            new Uri(folderPath + Path.DirectorySeparatorChar), "Season 1", folderPath,
            DateTimeOffset.UtcNow, null);

        await viewModel.OpenRecentFolderAsync(folder);

        Assert.True(viewModel.IsConnected);
        Assert.Equal(MediaSourceProtocol.LocalFolder, viewModel.Protocol);
        Assert.Equal(["episode.mkv"], viewModel.Entries.Select(static entry => entry.Name));
        Assert.Empty(viewModel.Sources);
    }

    [Fact]
    public async Task PlaybackHistoryProgress_UpdatesVisibleVideoWithoutMarkingAudioAsPlayed()
    {
        using var directory = new TemporaryDirectory();
        var video = Path.Combine(directory.Path, "movie.mkv");
        var audio = Path.Combine(directory.Path, "song.flac");
        await File.WriteAllTextAsync(video, "video");
        await File.WriteAllTextAsync(audio, "audio");
        await using var viewModel = new MediaSourceBrowserViewModel();
        viewModel.SetPlaybackHistory(
        [
            new PlaybackRecentItemViewModel(video, "movie", 30, 120,
                DateTimeOffset.UtcNow, false, false, null),
            new PlaybackRecentItemViewModel(audio, "song", 60, 120,
                DateTimeOffset.UtcNow, false, false, null),
        ]);
        await viewModel.OpenRecentFolderAsync(new PlaybackRecentFolderViewModel(
            new Uri(directory.Path + Path.DirectorySeparatorChar), "Downloads", directory.Path,
            DateTimeOffset.UtcNow, null));

        var videoEntry = viewModel.Entries.Single(static entry => entry.Name == "movie.mkv");
        var audioEntry = viewModel.Entries.Single(static entry => entry.Name == "song.flac");
        Assert.True(videoEntry.HasPlaybackHistory);
        Assert.Equal(25, videoEntry.PlaybackProgress);
        Assert.False(audioEntry.HasPlaybackHistory);

        viewModel.SetPlaybackHistory([]);
        Assert.False(videoEntry.HasPlaybackHistory);
    }

    [Fact]
    public async Task RecentFolder_UsesSavedProfileCredentialsAndStartsAtVideoDirectory()
    {
        var root = new Uri("https://dav.example.test/media/");
        var target = new Uri(root, "Series/");
        var store = new FakeMediaSourceProfileStore
        {
            Profile = new MediaSourceConnectionProfile
            {
                Id = "history-source",
                Name = "History source",
                Protocol = MediaSourceProtocol.WebDav,
                Endpoint = root,
                UserName = "alice",
                Password = "secret",
            },
        };
        FakeMediaSourceClient? client = null;
        NetworkCredential? credential = null;
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: store,
            webDavFactory: (endpoint, suppliedCredential) =>
            {
                Assert.Equal(root, endpoint);
                credential = suppliedCredential;
                return client = new FakeMediaSourceClient(root, []);
            });
        var folder = new PlaybackRecentFolderViewModel(target, "Series", target.AbsoluteUri,
            DateTimeOffset.UtcNow, "history-source");

        await viewModel.OpenRecentFolderAsync(folder);

        Assert.Equal(target, client!.LastBrowseDirectory);
        Assert.Equal("secret", credential?.Password);
        Assert.Equal("history-source", viewModel.ActiveProfileId);
        Assert.True(viewModel.CanNavigateUp);
        await viewModel.NavigateUpAsync();
        Assert.Equal(root, client.LastBrowseDirectory);
    }

    [Fact]
    public async Task RecentFolder_AcceptsSavedEndpointWithoutTrailingSlash()
    {
        var endpoint = new Uri("https://dav.example.test/media");
        var normalizedRoot = new Uri("https://dav.example.test/media/");
        var target = new Uri(normalizedRoot, "Series/");
        var store = new FakeMediaSourceProfileStore
        {
            Profile = new MediaSourceConnectionProfile
            {
                Id = "history-source",
                Name = "History source",
                Protocol = MediaSourceProtocol.WebDav,
                Endpoint = endpoint,
            },
        };
        FakeMediaSourceClient? client = null;
        await using var viewModel = new MediaSourceBrowserViewModel(
            profileStore: store,
            webDavFactory: (suppliedEndpoint, _) =>
            {
                Assert.Equal(endpoint, suppliedEndpoint);
                return client = new FakeMediaSourceClient(normalizedRoot, []);
            });

        await viewModel.OpenRecentFolderAsync(new PlaybackRecentFolderViewModel(
            target, "Series", target.AbsoluteUri, DateTimeOffset.UtcNow, "history-source"));

        Assert.Equal(target, client!.LastBrowseDirectory);
    }

    [Fact]
    public async Task RecentFolder_ReusesActiveAnonymousConnection()
    {
        var root = new Uri("https://dav.example.test/media/");
        var target = new Uri(root, "Series/");
        var client = new FakeMediaSourceClient(root, []);
        var factoryCalls = 0;
        await using var viewModel = new MediaSourceBrowserViewModel(
            webDavFactory: (_, _) =>
            {
                factoryCalls++;
                return client;
            })
        {
            EndpointText = root.AbsoluteUri,
        };
        await viewModel.ConnectAsync();

        await viewModel.OpenRecentFolderAsync(new PlaybackRecentFolderViewModel(
            target, "Series", target.AbsoluteUri, DateTimeOffset.UtcNow, null));

        Assert.Equal(1, factoryCalls);
        Assert.Equal(target, client.LastBrowseDirectory);
        Assert.True(viewModel.CanNavigateBack);
        Assert.False(client.IsDisposed);
    }

    private static MediaResource Resource(
        string name,
        bool isDirectory = false,
        long? length = null,
        DateTimeOffset? modified = null) => new()
        {
            Uri = new Uri($"https://dav.example.test/media/{name}"),
            Name = name.TrimEnd('/'),
            IsDirectory = isDirectory,
            Length = length,
            LastModified = modified,
        };

    private sealed class FakeMediaSourceClient(
        Uri root,
        IReadOnlyList<MediaResource> rootResources) : IMediaSourceClient
    {
        public MediaSourceProtocol Protocol => MediaSourceProtocol.WebDav;

        public Uri Root { get; } = root;

        public Uri? LastBrowseDirectory { get; private set; }

        public MediaResource? LastOpenedResource { get; private set; }

        public bool IsDisposed { get; private set; }

        public ValueTask<IReadOnlyList<MediaResource>> BrowseAsync(
            Uri? directory = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastBrowseDirectory = directory;
            return ValueTask.FromResult(directory is null || directory == Root
                ? rootResources
                : (IReadOnlyList<MediaResource>)[]);
        }

        public ValueTask<MediaResource> GetAsync(
            Uri resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(rootResources.Single(item => item.Uri == resource));

        public ValueTask<IMediaByteStream> OpenReadAsync(
            MediaResource resource,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOpenedResource = resource;
            return ValueTask.FromResult<IMediaByteStream>(
                new DotNetMediaByteStream(new MemoryStream(new byte[resource.Length ?? 0])));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMediaSourceProfileStore : IMediaSourceProfileStore
    {
        private readonly List<MediaSourceConnectionProfile> _profiles = [];

        public IReadOnlyList<MediaSourceConnectionProfile> Profiles => _profiles;

        public MediaSourceConnectionProfile? Profile
        {
            get => _profiles.FirstOrDefault();
            set
            {
                _profiles.Clear();
                if (value is not null)
                {
                    _profiles.Add(value);
                }
            }
        }

        public bool FailSave { get; init; }

        public int LoadCount { get; private set; }

        public ValueTask<IReadOnlyList<MediaSourceConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<MediaSourceConnectionProfile>>(
                _profiles.Select(profile => profile with { Password = string.Empty }).ToList());
        }

        public ValueTask<MediaSourceConnectionProfile?> LoadProfileAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return ValueTask.FromResult(_profiles.FirstOrDefault(profile => profile.Id == id));
        }

        public ValueTask SaveProfileAsync(
            MediaSourceConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave)
            {
                throw new InvalidOperationException("Credential store unavailable.");
            }

            _profiles.RemoveAll(existing => existing.Id == profile.Id);
            _profiles.Add(profile);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteProfileAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profiles.RemoveAll(profile => profile.Id == id) > 0);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-source-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FakeMediaSourceFavoriteStore : IMediaSourceFavoriteStore
    {
        private readonly List<MediaSourceFavorite> _favorites = [];

        public ValueTask<IReadOnlyList<MediaSourceFavorite>> ListAsync(
            MediaSourceProtocol protocol,
            Uri endpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<MediaSourceFavorite>>(_favorites
                .Where(favorite => favorite.Protocol == protocol && favorite.Endpoint == endpoint)
                .ToList());
        }

        public ValueTask<MediaSourceFavorite> AddAsync(
            MediaSourceProtocol protocol,
            Uri endpoint,
            Uri location,
            string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var favorite = new MediaSourceFavorite
            {
                Id = Guid.NewGuid().ToString("N"),
                Protocol = protocol,
                Endpoint = endpoint,
                Location = location,
                Name = name,
            };
            _favorites.RemoveAll(item => item.Location == location);
            _favorites.Add(favorite);
            return ValueTask.FromResult(favorite);
        }

        public ValueTask<bool> DeleteAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_favorites.RemoveAll(favorite => favorite.Id == id) > 0);
        }
    }
}
