// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.App.Automation;
using CanX.Core.Configuration;
using CanX.Core.Diagnostics;
using CanX.Infrastructure.Configuration;
using CanX.MediaAccess;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DesktopAutomationServiceTests
{
    [Fact]
    public async Task MediaTools_UseOpaqueHandlesAndNeverReturnCredentialsOrPaths()
    {
        using var directory = new TemporaryDirectory();
        var mediaPath = Path.Combine(directory.Path, "movie.mkv");
        await File.WriteAllTextAsync(mediaPath, "media");
        var profile = new MediaSourceConnectionProfile
        {
            Id = "local-source",
            Name = "Private library",
            Protocol = MediaSourceProtocol.LocalFolder,
            Endpoint = new Uri(directory.Path + Path.DirectorySeparatorChar),
            UserName = "should-not-leak",
            Password = "top-secret",
        };
        await using var plugins = new PluginLifecycleManager(
            Path.Combine(directory.Path, "plugins"),
            new FakeHostFactory());
        var played = new List<ConfiguredMediaPlaybackRequest>();
        var service = CreateService(new StubProfileStore(profile), plugins, played.Add);

        var sources = await service.ListSourcesAsync(CancellationToken.None);
        var browse = await service.BrowseAsync(profile.Id, null, CancellationToken.None);
        var item = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(browse["items"]).Single());
        var handle = item["location"]!.GetValue<string>();
        await service.PlayAsync(profile.Id, handle, null, CancellationToken.None);

        Assert.DoesNotContain(directory.Path, sources.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", sources.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, browse.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(32, handle.Length);
        Assert.Equal(mediaPath, Assert.Single(played).LocalPath);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PlayAsync(profile.Id, mediaPath, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SettingsTools_RejectUnknownAndSecretProperties()
    {
        using var directory = new TemporaryDirectory();
        await using var plugins = new PluginLifecycleManager(
            Path.Combine(directory.Path, "plugins"),
            new FakeHostFactory());
        var settings = new StubSettingsStore(new ApplicationSettings());
        var service = CreateService(new StubProfileStore(), plugins, _ => { }, settings);

        var changed = await service.SetAsync(
            "general",
            new JsonObject { ["colorTheme"] = "Dark", ["enableLogging"] = true },
            CancellationToken.None);

        Assert.Equal("Dark", changed["colorTheme"]?.GetValue<string>());
        Assert.True(changed["enableLogging"]?.GetValue<bool>());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync(
            "agent",
            new JsonObject { ["apiKey"] = "must-not-be-accepted" },
            CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("general", "downloadDirectory", "/tmp/elsewhere")]
    [InlineData("agent", "baseUrl", "https://attacker.example/v1")]
    [InlineData("agent", "autoApproveToolCalls", "true")]
    [InlineData("playback", "advancedOptions", "scripts=/tmp/payload.lua")]
    public async Task SettingsTools_RejectUserOnlySettings(string section, string name, string value)
    {
        using var directory = new TemporaryDirectory();
        await using var plugins = new PluginLifecycleManager(
            Path.Combine(directory.Path, "plugins"),
            new FakeHostFactory());
        var settings = new StubSettingsStore(new ApplicationSettings());
        var service = CreateService(new StubProfileStore(), plugins, _ => { }, settings);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync(
            section,
            new JsonObject { [name] = name == "autoApproveToolCalls" ? JsonValue.Create(true) : JsonValue.Create(value) },
            CancellationToken.None).AsTask());

        Assert.Contains("only be changed by the user", exception.Message, StringComparison.Ordinal);
    }

    private static DesktopAutomationService CreateService(
        IMediaSourceProfileStore profiles,
        PluginLifecycleManager plugins,
        Action<ConfiguredMediaPlaybackRequest> played,
        IApplicationSettingsStore? settings = null) => new(
            profiles,
            plugins,
            settings ?? new StubSettingsStore(new ApplicationSettings()),
            NullApplicationLog.Instance,
            (request, _) =>
            {
                played(request);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => "downloads",
            (_, _) => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask);

    private sealed class StubProfileStore(params MediaSourceConnectionProfile[] profiles) : IMediaSourceProfileStore
    {
        private readonly Dictionary<string, MediaSourceConnectionProfile> _profiles =
            profiles.ToDictionary(static profile => profile.Id, StringComparer.Ordinal);

        public ValueTask<IReadOnlyList<MediaSourceConnectionProfile>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<MediaSourceConnectionProfile>>(_profiles.Values.ToArray());

        public ValueTask<MediaSourceConnectionProfile?> LoadProfileAsync(string id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_profiles.GetValueOrDefault(id));

        public ValueTask SaveProfileAsync(MediaSourceConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            _profiles[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_profiles.Remove(id));
    }

    private sealed class StubSettingsStore(ApplicationSettings settings) : IApplicationSettingsStore
    {
        private ApplicationSettings _settings = settings;

        public ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_settings);

        public ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHostFactory : IPluginRuntimeHostFactory
    {
        public IPluginRuntimeHost Create(PluginInstallation installation) => new FakeHost(installation.Manifest);
    }

    private sealed class FakeHost(PluginManifest manifest) : IPluginRuntimeHost
    {
        public PluginManifest Manifest { get; } = manifest;
        public PluginHostState State { get; private set; } = PluginHostState.Created;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            State = PluginHostState.Running;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = PluginHostState.Stopped;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-automation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
