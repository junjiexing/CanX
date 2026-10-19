// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Security;
using CanX.Infrastructure.MediaAccess;
using CanX.MediaAccess;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class MediaSourceProfileSecurityTests
{
    [Fact]
    public async Task ProfileStore_PersistsOnlyCredentialReferenceAndRestoresPassword()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "media-sources.json");
        var credentials = new MemoryCredentialStore();
        var store = new CredentialProtectingMediaSourceProfileStore(path, credentials);
        var profile = new MediaSourceConnectionProfile
        {
            Id = "webdav-primary",
            Name = "Primary WebDAV",
            Protocol = MediaSourceProtocol.WebDav,
            Endpoint = new Uri("https://dav.example.test/media/"),
            UserName = "alice",
            Password = "secret-password",
        };

        await store.SaveProfileAsync(profile);

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("dav.example.test", json, StringComparison.Ordinal);
        Assert.Contains("alice", json, StringComparison.Ordinal);
        Assert.Contains("credentialReference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", json, StringComparison.Ordinal);
        var credential = Assert.Single(credentials.Values);
        Assert.StartsWith("canx.media.webdav.", credential.Key, StringComparison.Ordinal);
        Assert.Equal("secret-password", credential.Value);

        var listed = Assert.Single(await store.ListProfilesAsync());
        Assert.Equal(profile.Endpoint, listed.Endpoint);
        Assert.Equal(string.Empty, listed.Password);
        Assert.Equal(0, credentials.GetCount);

        var restored = await store.LoadProfileAsync(profile.Id);

        Assert.NotNull(restored);
        Assert.Equal(profile.Endpoint, restored.Endpoint);
        Assert.Equal(profile.UserName, restored.UserName);
        Assert.Equal(profile.Password, restored.Password);
        Assert.Equal(1, credentials.GetCount);
    }

    [Fact]
    public async Task ProfileStore_ReplacesAndDeletesObsoleteCredentials()
    {
        using var directory = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var store = new CredentialProtectingMediaSourceProfileStore(
            Path.Combine(directory.Path, "media-sources.json"),
            credentials);
        await store.SaveProfileAsync(Profile("ftp://first.example.test/media/", "first"));
        var firstReference = Assert.Single(credentials.Values).Key;

        await store.SaveProfileAsync(Profile("ftp://second.example.test/media/", "second"));

        var secondCredential = Assert.Single(credentials.Values);
        Assert.NotEqual(firstReference, secondCredential.Key);
        Assert.Equal("second", secondCredential.Value);
        Assert.True(await store.DeleteProfileAsync("ftp-primary"));
        Assert.Empty(credentials.Values);
        Assert.Null(await store.LoadProfileAsync("ftp-primary"));
    }

    [Fact]
    public async Task ProfileStore_PersistsMultipleSourcesOfTheSameProtocolAndLocalFolders()
    {
        using var directory = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var store = new CredentialProtectingMediaSourceProfileStore(
            Path.Combine(directory.Path, "media-sources.json"),
            credentials);
        var first = Profile("ftp://first.example.test/media/", "first") with
        {
            Id = "ftp-first",
            Name = "First FTP",
        };
        var second = Profile("ftp://second.example.test/media/", "second") with
        {
            Id = "ftp-second",
            Name = "Second FTP",
        };
        var local = new MediaSourceConnectionProfile
        {
            Id = "local-media",
            Name = "Local Media",
            Protocol = MediaSourceProtocol.LocalFolder,
            Endpoint = new Uri(directory.Path + Path.DirectorySeparatorChar),
        };

        await store.SaveProfileAsync(first);
        await store.SaveProfileAsync(second);
        await store.SaveProfileAsync(local);

        var profiles = await store.ListProfilesAsync();
        Assert.Equal(3, profiles.Count);
        Assert.Equal(2, credentials.Values.Count);
        Assert.Contains(profiles, profile => profile.Id == local.Id && profile.Endpoint.IsFile);

        Assert.True(await store.DeleteProfileAsync(first.Id));
        Assert.Equal(2, (await store.ListProfilesAsync()).Count);
        Assert.Single(credentials.Values);
        Assert.NotNull(await store.LoadProfileAsync(second.Id));
    }

    [Fact]
    public async Task ProfileStore_DoesNotPersistSensitiveEndpointComponents()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "media-sources.json");
        var credentials = new MemoryCredentialStore();
        var store = new CredentialProtectingMediaSourceProfileStore(path, credentials);
        var profile = Profile("ftp://alice:secret@ftp.example.test/media/?token=value", "password");

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveProfileAsync(profile).AsTask());

        Assert.False(File.Exists(path));
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task ProfileStore_FailsClosedWhenReferencedCredentialIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var store = new CredentialProtectingMediaSourceProfileStore(
            Path.Combine(directory.Path, "media-sources.json"),
            credentials);
        await store.SaveProfileAsync(Profile("ftp://ftp.example.test/media/", "password"));
        credentials.Values.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadProfileAsync("ftp-primary").AsTask());
    }

    [Fact]
    public async Task ProfileStore_DoesNotCreatePlaintextFallbackWhenCredentialStoreFails()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "media-sources.json");
        var store = new CredentialProtectingMediaSourceProfileStore(path, new FailingCredentialStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveProfileAsync(Profile("ftp://ftp.example.test/media/", "secret-password")).AsTask());

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FavoriteStore_PersistsSafeLocationsAndSurvivesCredentialUpdates()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "media-sources.json");
        var credentials = new MemoryCredentialStore();
        var store = new CredentialProtectingMediaSourceProfileStore(path, credentials);
        var endpoint = new Uri("https://dav.example.test/media/");
        var location = new Uri(endpoint, "Series/");

        var favorite = await store.AddAsync(
            MediaSourceProtocol.WebDav,
            endpoint,
            location,
            "Series");
        await store.SaveProfileAsync(new MediaSourceConnectionProfile
        {
            Id = "webdav-primary",
            Name = "Primary WebDAV",
            Protocol = MediaSourceProtocol.WebDav,
            Endpoint = endpoint,
            UserName = "alice",
            Password = "secret-password",
        });

        var restored = Assert.Single(await store.ListAsync(MediaSourceProtocol.WebDav, endpoint));
        Assert.Equal(favorite.Id, restored.Id);
        Assert.Equal(location, restored.Location);
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("Series", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", json, StringComparison.Ordinal);

        Assert.True(await store.DeleteAsync(favorite.Id));
        Assert.Empty(await store.ListAsync(MediaSourceProtocol.WebDav, endpoint));
    }

    [Fact]
    public async Task FavoriteStore_RejectsLocationsOutsideTheSourceRoot()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "media-sources.json");
        var store = new CredentialProtectingMediaSourceProfileStore(path, new MemoryCredentialStore());
        var endpoint = new Uri("https://dav.example.test/media/");

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(
            MediaSourceProtocol.WebDav,
            endpoint,
            new Uri("https://dav.example.test/private/"),
            "Private").AsTask());

        Assert.False(File.Exists(path));
    }

    private static MediaSourceConnectionProfile Profile(string endpoint, string password) => new()
    {
        Id = "ftp-primary",
        Name = "Primary FTP",
        Protocol = MediaSourceProtocol.Ftp,
        Endpoint = new Uri(endpoint),
        UserName = "alice",
        Password = password,
    };

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public int GetCount { get; private set; }

        public ValueTask SetAsync(
            CredentialRef reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[reference.Id] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            return ValueTask.FromResult(Values.GetValueOrDefault(reference.Id));
        }

        public ValueTask<bool> DeleteAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Values.Remove(reference.Id));
        }
    }

    private sealed class FailingCredentialStore : ICredentialStore
    {
        public ValueTask SetAsync(
            CredentialRef reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No system credential store is available.");

        public ValueTask<string?> GetAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No system credential store is available.");

        public ValueTask<bool> DeleteAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No system credential store is available.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-media-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
