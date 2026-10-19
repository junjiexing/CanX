// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.ViewModels;
using CanX.Agent;
using CanX.Application.Playback;
using CanX.Core.Configuration;
using CanX.Core.Diagnostics;
using CanX.Core.Playback;
using CanX.Core.Security;
using CanX.Infrastructure.Configuration;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task SeekPreviewSettings_PersistAndNotifyOnSave()
    {
        var store = new StubSettingsStore(new ApplicationSettings());
        var settings = new SettingsViewModel(store, Path.GetTempPath(), _ => { }, _ => { });
        var applied = new List<MpvSettings>();
        settings.SeekPreviewSettingsChanged += applied.Add;

        await settings.InitializeAsync();
        Assert.True(settings.Mpv.SeekPreviewEnabled);
        Assert.Equal(180m, settings.Mpv.SeekPreviewDelayMilliseconds);

        settings.Mpv.SeekPreviewEnabled = false;
        settings.Mpv.SeekPreviewDelayMilliseconds = 300;
        settings.SaveCommand.Execute(null);
        await WaitUntilAsync(() => store.SaveCount == 1 && applied.Count == 1 &&
            settings.SaveCommand.CanExecute(null));
        Assert.False(store.Settings.Mpv.SeekPreviewEnabled);
        Assert.Equal(300, applied[0].SeekPreviewDelayMilliseconds);

        settings.Mpv.SeekPreviewEnabled = true;
        settings.Mpv.SeekPreviewDelayMilliseconds = 0;
        settings.SaveCommand.Execute(null);
        await WaitUntilAsync(() => store.SaveCount == 2 && applied.Count == 2 &&
            settings.SaveCommand.CanExecute(null));
        Assert.True(store.Settings.Mpv.SeekPreviewEnabled);
        Assert.Equal(0, applied[1].SeekPreviewDelayMilliseconds);

        var reopened = new SettingsViewModel(store, Path.GetTempPath(), _ => { }, _ => { });
        await reopened.InitializeAsync();
        Assert.True(reopened.Mpv.SeekPreviewEnabled);
        Assert.Equal(0m, reopened.Mpv.SeekPreviewDelayMilliseconds);
    }

    [Fact]
    public async Task PlaybackWindowMode_AppliesOnSaveAndSurvivesReload()
    {
        var store = new StubSettingsStore(new ApplicationSettings());
        var settings = new SettingsViewModel(store, Path.GetTempPath(), _ => { }, _ => { });
        await settings.InitializeAsync();
        Assert.False(settings.OpenMediaInNewWindow);
        settings.SelectedPlaybackWindowMode = settings.PlaybackWindowModes.Single(option => option.OpenNewWindow);
        Assert.False(settings.OpenMediaInNewWindow);

        settings.SaveCommand.Execute(null);
        await WaitUntilAsync(() => settings.SaveCommand.CanExecute(null));
        Assert.True(settings.OpenMediaInNewWindow);
        Assert.True(store.Settings.OpenMediaInNewWindow);

        var reopened = new SettingsViewModel(store, Path.GetTempPath(), _ => { }, _ => { });
        await reopened.InitializeAsync();
        Assert.True(reopened.SelectedPlaybackWindowMode.OpenNewWindow);
        reopened.SelectedPlaybackWindowMode = reopened.PlaybackWindowModes.Single(option => !option.OpenNewWindow);
        reopened.SaveCommand.Execute(null);
        await WaitUntilAsync(() => reopened.SaveCommand.CanExecute(null));
        Assert.False(reopened.OpenMediaInNewWindow);
        Assert.False(store.Settings.OpenMediaInNewWindow);
    }

    [Fact]
    public async Task SaveCommand_PersistsAgentOptionsAndPendingKeyInOneSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-agent-settings-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonApplicationSettingsStore(path);
            var credentials = new RecordingCredentialStore();
            var provider = new CredentialAgentApiKeyProvider(credentials);
            var viewModel = new SettingsViewModel(store, directory, _ => { }, _ => { },
                agentApiKeyProvider: provider);
            await viewModel.InitializeAsync();
            Assert.False(viewModel.Agent.ShowToolCalls);
            Assert.False(viewModel.Agent.CreateRunOptions().AutoApproveToolCalls);
            viewModel.Agent.BaseUrl = "https://gateway.test/v1";
            viewModel.Agent.Model = "test-model";
            viewModel.Agent.ApiKeyInput = "  entered-api-key  ";
            viewModel.Agent.ShowToolCalls = true;
            viewModel.Agent.AutoApproveToolCalls = true;

            viewModel.SaveCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SaveCommand.CanExecute(null));

            Assert.Equal("设置已保存", viewModel.StatusMessage);
            Assert.Empty(viewModel.Agent.ApiKeyInput);
            Assert.True(viewModel.Agent.HasStoredApiKey);
            Assert.Equal("entered-api-key", await provider.GetApiKeyForEndpointAsync(new Uri(viewModel.Agent.BaseUrl)));
            var stored = await store.LoadAsync();
            Assert.True(stored.Agent.ShowToolCalls);
            Assert.True(stored.Agent.AutoApproveToolCalls);
            Assert.DoesNotContain("entered-api-key", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            var writes = credentials.WriteCount;

            viewModel.SaveCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SaveCommand.CanExecute(null));

            Assert.Equal(writes, credentials.WriteCount);
            Assert.Equal("entered-api-key", await provider.GetApiKeyAsync());
            var reopened = new SettingsViewModel(store, directory, _ => { }, _ => { },
                agentApiKeyProvider: provider);
            await reopened.InitializeAsync();
            Assert.True(reopened.Agent.ShowToolCalls);
            Assert.True(reopened.Agent.CreateRunOptions().AutoApproveToolCalls);
            Assert.True(reopened.Agent.HasStoredApiKey);
            Assert.Empty(reopened.Agent.ApiKeyInput);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyAgentSettings_DefaultToHiddenToolsAndManualApproval()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"canx-agent-settings-legacy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path,
                """{"SchemaVersion":6,"Agent":{"BaseUrl":"https://gateway.test/v1","Model":"test-model"}}""");
            var settings = await new JsonApplicationSettingsStore(path).LoadAsync();

            Assert.Equal("test-model", settings.Agent.Model);
            Assert.False(settings.Agent.ShowToolCalls);
            Assert.False(settings.Agent.AutoApproveToolCalls);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveCommand_CredentialFailureKeepsPendingKeyAndDoesNotSaveSettings()
    {
        var store = new StubSettingsStore(new ApplicationSettings());
        var credentials = new RecordingCredentialStore { FailWrites = true };
        var viewModel = new SettingsViewModel(store, Path.GetTempPath(), _ => { }, _ => { },
            agentApiKeyProvider: new CredentialAgentApiKeyProvider(credentials));
        await viewModel.InitializeAsync();
        viewModel.Agent.BaseUrl = "https://gateway.test/v1";
        viewModel.Agent.Model = "test-model";
        viewModel.Agent.ApiKeyInput = "pending-key";

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SaveCommand.CanExecute(null));

        Assert.Equal(0, store.SaveCount);
        Assert.Equal("pending-key", viewModel.Agent.ApiKeyInput);
        Assert.Contains("设置保存失败", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("API Key 保存失败", viewModel.Agent.CredentialStatus, StringComparison.Ordinal);
        Assert.False(viewModel.Agent.HasStoredApiKey);
    }

    [Fact]
    public async Task SaveCommand_InvalidSettingsDoNotWritePendingKey()
    {
        var store = new StubSettingsStore(new ApplicationSettings());
        var credentials = new RecordingCredentialStore();
        var viewModel = new SettingsViewModel(store, Path.GetTempPath(), _ => { }, _ => { },
            agentApiKeyProvider: new CredentialAgentApiKeyProvider(credentials));
        await viewModel.InitializeAsync();
        viewModel.Agent.BaseUrl = "https://gateway.test/v1";
        viewModel.Agent.Model = "test-model";
        viewModel.Agent.ApiKeyInput = "pending-key";
        viewModel.Agent.ToolTimeoutSeconds = -1;

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SaveCommand.CanExecute(null));

        Assert.Equal(0, credentials.WriteCount);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal("pending-key", viewModel.Agent.ApiKeyInput);
        Assert.Contains("设置保存失败", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeferredAgentCredentialStatus_IsReadOnlyWhenSettingsAreOpened()
    {
        var credentials = new CountingCredentialStore();
        var viewModel = new SettingsViewModel(
            new StubSettingsStore(new ApplicationSettings()),
            Path.GetTempPath(),
            _ => { },
            _ => { },
            agentApiKeyProvider: new CredentialAgentApiKeyProvider(credentials),
            deferAgentCredentialStatusLoad: true);

        await viewModel.InitializeAsync();

        Assert.Equal(0, credentials.ReadCount);
        await viewModel.EnsureAgentCredentialStatusLoadedAsync();
        Assert.Equal(1, credentials.ReadCount);
        Assert.True(viewModel.Agent.HasStoredApiKey);
        await viewModel.EnsureAgentCredentialStatusLoadedAsync();
        Assert.Equal(1, credentials.ReadCount);
    }

    [Fact]
    public async Task MpvSettings_AreValidatedConfiguredAndPersisted()
    {
        var store = new StubSettingsStore(new ApplicationSettings
        {
            Mpv = new MpvSettings
            {
                HardwareDecoding = "vaapi",
                TargetTransfer = "pq",
                ToneMapping = "bt.2390",
                DemuxerMaxBackBytesMiB = "72",
                CachePause = true,
                CachePauseInitial = true,
                CachePauseWaitSeconds = "3.5",
                AdvancedOptions = "tone-mapping=auto",
            },
        });
        var playback = new StubPlaybackController();
        var validator = new StubMpvOptionValidator();
        var viewModel = new SettingsViewModel(
            store,
            Path.GetTempPath(),
            _ => { },
            _ => { },
            playbackController: playback,
            mpvOptionValidator: validator);

        Assert.Null(await viewModel.InitializeAsync());
        Assert.Equal("vaapi", playback.StartupOptions["hwdec"]);
        Assert.Equal("bt.2390", playback.StartupOptions["tone-mapping"]);
        Assert.Equal("pq", playback.StartupOptions["target-trc"]);
        Assert.Equal(1, validator.ValidationCount);
        Assert.Equal("vaapi", viewModel.ActiveMpvSettings.HardwareDecoding);

        playback.IsStarted = true;
        viewModel.Mpv.SelectedHardwareDecoding = viewModel.Mpv.HardwareDecodingOptions
            .Single(option => option.Value == "nvdec");
        viewModel.Mpv.DemuxerMaxBackBytesMiB = "96";
        viewModel.Mpv.CachePause = false;
        viewModel.Mpv.CachePauseInitial = false;
        viewModel.Mpv.CachePauseWaitSeconds = "2.5";
        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => store.SaveCount == 1 &&
            viewModel.ActiveMpvSettings.HardwareDecoding == "nvdec");

        Assert.Equal("nvdec", store.Settings.Mpv.HardwareDecoding);
        Assert.Equal("pq", store.Settings.Mpv.TargetTransfer);
        Assert.Equal("bt.2390", store.Settings.Mpv.ToneMapping);
        Assert.Equal("96", store.Settings.Mpv.DemuxerMaxBackBytesMiB);
        Assert.False(store.Settings.Mpv.CachePause);
        Assert.False(store.Settings.Mpv.CachePauseInitial);
        Assert.Equal("2.5", store.Settings.Mpv.CachePauseWaitSeconds);
        Assert.Equal("nvdec", viewModel.ActiveMpvSettings.HardwareDecoding);
        Assert.True(playback.LastAutoPlayNext);
        Assert.Contains("下次启动", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(2, validator.ValidationCount);
    }

    [Fact]
    public async Task InitializeAndSave_AppliesThemeAndDefaultDownloadDirectory()
    {
        var initialDirectory = Path.Combine(Path.GetTempPath(), "canx-initial-downloads");
        var changedDirectory = Path.Combine(Path.GetTempPath(), "canx-changed-downloads");
        var store = new StubSettingsStore(new ApplicationSettings
        {
            ColorTheme = "Dark",
            DownloadDirectory = initialDirectory,
            EnableLogging = true,
        });
        var applicationLog = new StubApplicationLog();
        var appliedThemes = new List<string>();
        var appliedDirectories = new List<string>();
        var viewModel = new SettingsViewModel(
            store,
            Path.GetTempPath(),
            appliedThemes.Add,
            appliedDirectories.Add,
            applicationLog);

        var loadError = await viewModel.InitializeAsync();

        Assert.Null(loadError);
        Assert.Empty(viewModel.StatusMessage);
        Assert.False(viewModel.HasStatusMessage);
        Assert.Equal("Dark", viewModel.SelectedTheme.Value);
        Assert.Equal(Path.GetFullPath(initialDirectory), viewModel.DownloadDirectory);
        Assert.True(viewModel.IsLoggingEnabled);
        Assert.True(applicationLog.IsEnabled);
        Assert.Equal("Dark", Assert.Single(appliedThemes));
        Assert.Equal(Path.GetFullPath(initialDirectory), Assert.Single(appliedDirectories));

        viewModel.SelectedTheme = viewModel.ThemeOptions.Single(option => option.Value == "Light");
        viewModel.DownloadDirectory = changedDirectory;
        viewModel.IsLoggingEnabled = false;
        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => store.SaveCount == 1);

        Assert.Equal("Light", store.Settings.ColorTheme);
        Assert.Equal(Path.GetFullPath(changedDirectory), store.Settings.DownloadDirectory);
        Assert.False(store.Settings.EnableLogging);
        Assert.Equal([true, false], applicationLog.Changes);
        Assert.Equal("Light", appliedThemes[^1]);
        Assert.Equal(Path.GetFullPath(changedDirectory), appliedDirectories[^1]);
        Assert.Equal("设置已保存", viewModel.StatusMessage);
        Assert.True(viewModel.HasStatusMessage);
    }

    [Fact]
    public async Task SaveCommand_AlsoSavesPendingAria2Settings()
    {
        var store = new StubSettingsStore(new ApplicationSettings());
        var aria2SaveCount = 0;
        var viewModel = new SettingsViewModel(
            store,
            Path.GetTempPath(),
            _ => { },
            _ => { },
            saveDownloadSettings: _ =>
            {
                aria2SaveCount++;
                return ValueTask.FromResult(true);
            });
        await viewModel.InitializeAsync();

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => store.SaveCount == 1 && aria2SaveCount == 1);

        Assert.Contains("aria2", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_ReturnsLoadErrorAndAppliesDefaults()
    {
        var defaultDirectory = Path.Combine(Path.GetTempPath(), "canx-default-downloads");
        var appliedThemes = new List<string>();
        var appliedDirectories = new List<string>();
        var viewModel = new SettingsViewModel(
            new FailingSettingsStore(),
            defaultDirectory,
            appliedThemes.Add,
            appliedDirectories.Add);

        var loadError = await viewModel.InitializeAsync();

        Assert.Contains("设置读取失败", loadError, StringComparison.Ordinal);
        Assert.Contains("invalid settings", loadError, StringComparison.Ordinal);
        Assert.Equal("System", viewModel.SelectedTheme.Value);
        Assert.Equal(Path.GetFullPath(defaultDirectory), viewModel.DownloadDirectory);
        Assert.Equal("System", Assert.Single(appliedThemes));
        Assert.Equal(Path.GetFullPath(defaultDirectory), Assert.Single(appliedDirectories));
        Assert.Empty(viewModel.StatusMessage);
        Assert.False(viewModel.HasStatusMessage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class StubSettingsStore(ApplicationSettings settings) : IApplicationSettingsStore
    {
        public ApplicationSettings Settings { get; private set; } = settings;

        public int SaveCount { get; private set; }

        public ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Settings);

        public ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            SaveCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public int WriteCount { get; private set; }
        public bool FailWrites { get; init; }

        public ValueTask SetAsync(CredentialRef reference, string secret, CancellationToken cancellationToken = default)
        {
            if (FailWrites) throw new IOException("Credential store unavailable");
            WriteCount++;
            _values[reference.Id] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(CredentialRef reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.GetValueOrDefault(reference.Id));

        public ValueTask<bool> DeleteAsync(CredentialRef reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.Remove(reference.Id));
    }

    private sealed class CountingCredentialStore : ICredentialStore
    {
        public int ReadCount { get; private set; }

        public ValueTask SetAsync(
            CredentialRef reference,
            string secret,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<string?> GetAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult<string?>("saved-api-key");
        }

        public ValueTask<bool> DeleteAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class FailingSettingsStore : IApplicationSettingsStore
    {
        public ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApplicationSettings>(new InvalidDataException("invalid settings"));

        public ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class StubApplicationLog : IApplicationLog
    {
        public bool IsEnabled { get; private set; }

        public string DirectoryPath => "/tmp/canx-logs";

        public List<bool> Changes { get; } = [];

        public event Action<bool>? EnabledChanged;

        public string GetFilePath(LogComponent component) => Path.Combine(DirectoryPath, $"{component}.log");

        public void SetEnabled(bool enabled)
        {
            if (IsEnabled == enabled)
            {
                return;
            }

            IsEnabled = enabled;
            Changes.Add(enabled);
            EnabledChanged?.Invoke(enabled);
        }

        public void Write(
            LogComponent component,
            ApplicationLogLevel level,
            string message,
            Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubMpvOptionValidator : IMpvOptionValidator
    {
        public int ValidationCount { get; private set; }

        public void Validate(IReadOnlyDictionary<string, string> options) => ValidationCount++;
    }

    private sealed class StubPlaybackController : IPlaybackController
    {
        public bool IsStarted { get; set; }

        public PlaybackSnapshot Snapshot { get; } = new();

        public PlaybackCapabilityManifest Capabilities { get; } = new();

        public IReadOnlyDictionary<string, string> StartupOptions { get; private set; } =
            new Dictionary<string, string>();

        public bool? LastAutoPlayNext { get; private set; }

        public void ConfigureStartupOptions(IReadOnlyDictionary<string, string> options) =>
            StartupOptions = new Dictionary<string, string>(options);

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenAsync(string source, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenMediaSourceAsync(string playbackSource, string historySource, string? mediaSourceProfileId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenMediaSourcePlaylistAsync(IReadOnlyList<PlaybackMediaSource> sources, int startIndex = 0, string? mediaSourceProfileId = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenPlaylistAsync(IReadOnlyList<string> sources, int startIndex = 0, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask AddToPlaylistAsync(IReadOnlyList<string> sources, bool playNow = false, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenFolderAsync(string directory, bool recursive = false, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PlayPlaylistItemAsync(int index, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PlayNextAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetAutoPlayNextAsync(bool autoPlayNext, CancellationToken cancellationToken = default)
        {
            LastAutoPlayNext = autoPlayNext;
            return ValueTask.CompletedTask;
        }

        public ValueTask PlayPreviousAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemovePlaylistItemAsync(int index, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask MovePlaylistItemAsync(int index, int newIndex, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ClearPlaylistAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenRecentAsync(string source, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveRecentAsync(string source, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveRecentAsync(string source, bool isMediaSource, string? mediaSourceProfileId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ClearRecentAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SeekToChapterAsync(int index, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask AddBookmarkAsync(string? title = null, double? positionSeconds = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OpenBookmarkAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RenameBookmarkAsync(string id, string title, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveBookmarkAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ClearBookmarksAsync(bool all = false, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask TogglePauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SeekAsync(
            double seconds,
            bool relative = false,
            bool exact = true,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StepFrameAsync(bool backward = false, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetAbLoopAsync(double? startSeconds = null, double? endSeconds = null, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ClearAbLoopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<string> TakeScreenshotAsync(
            string? outputPath = null,
            PlaybackScreenshotMode mode = PlaybackScreenshotMode.Subtitles,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(outputPath ?? "/tmp/canx.png");

        public ValueTask SetVolumeAsync(double volume, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetBrightnessAsync(double brightness, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetSpeedAsync(double speed, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SelectAudioDeviceAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetVideoFiltersAsync(string filters, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetAudioFiltersAsync(string filters, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask AddShaderAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask RemoveShaderAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ClearShadersAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetColorSettingsAsync(PlaybackColorSettings settings, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<string> GetRuntimePropertyAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(string.Empty);

        public ValueTask SetRuntimePropertyAsync(string name, string value, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ExecuteCommandAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SelectTrackAsync(
            PlaybackTrackType type,
            long? trackId,
            CancellationToken cancellationToken = default,
            bool remember = true) => ValueTask.CompletedTask;

        public ValueTask RestoreVideoOutputAsync(long trackId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask AddSubtitleAsync(
            string source,
            bool select = true,
            CancellationToken cancellationToken = default,
            PlaybackExternalSubtitleSource? persistentSource = null,
            bool remember = true) => ValueTask.CompletedTask;

        public ValueTask SetSubtitleVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetSubtitleFontSizeAsync(double size, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetSubtitleScaleAsync(double scale, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetSubtitlePositionAsync(double position, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendKeyDownAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendKeyUpAsync(string? key = null, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendKeyPressAsync(
            string key,
            double scale = 1,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetMousePositionAsync(int x, int y, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteWatchLaterAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
