// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;
using CanX.Core.Security;
using CanX.Downloads.Persistence;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DownloadSessionSecurityTests
{
    [Fact]
    public async Task SessionStore_MigratesSchemaOneBeforeReturningCredentials()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "downloads.json");
        var jsonStore = new JsonDownloadSessionStore(path);
        var source = "https://example.test/video.mp4?signature=legacy-secret";
        await jsonStore.SaveAsync(new DownloadSessionDocument
        {
            SchemaVersion = 1,
            Groups =
            [
                new DownloadSessionGroup
                {
                    GroupId = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    Request = new AddDownloadRequest
                    {
                        Source = source,
                        DestinationDirectory = directory.Path,
                    },
                },
            ],
        });
        var store = new CredentialProtectingDownloadSessionStore(jsonStore, new MemoryCredentialStore());

        var loaded = await store.LoadAsync();

        Assert.Equal(source, Assert.Single(loaded.Groups).Request.Source);
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionStore_MovesCredentialsOutOfJsonAndRestoresThem()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "downloads.json");
        var secrets = new MemoryCredentialStore();
        var store = new CredentialProtectingDownloadSessionStore(
            new JsonDownloadSessionStore(path),
            secrets);
        var groupId = Guid.NewGuid();
        var source = "ftp://alice:p%40ss@example.test/video.mp4?token=source-token";
        var mirror = "https://mirror.test/video.mp4?signature=mirror-token";
        var document = new DownloadSessionDocument
        {
            GlobalOptions = new Dictionary<string, string>
            {
                ["rpc-secret"] = "global-secret",
                ["max-overall-download-limit"] = "20M",
            },
            Groups =
            [
                new DownloadSessionGroup
                {
                    GroupId = groupId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Request = new AddDownloadRequest
                    {
                        Source = source,
                        MirrorUris = [mirror],
                        DestinationDirectory = directory.Path,
                        Options = new Dictionary<string, string>
                        {
                            ["http-user"] = "download-user",
                            ["http-passwd"] = "download-password",
                            ["header"] = "Authorization: Bearer header-token",
                            ["split"] = "8",
                        },
                    },
                    Tasks =
                    [
                        new DownloadTaskSnapshot
                        {
                            Id = Guid.NewGuid(),
                            EngineGid = "0000000000000001",
                            Kind = DownloadSourceKind.Uri,
                            Source = source,
                            MirrorUris = [mirror],
                            DestinationDirectory = directory.Path,
                            DisplayName = "video.mp4",
                            Options = new Dictionary<string, string>
                            {
                                ["http-passwd"] = "download-password",
                                ["split"] = "8",
                            },
                            Files =
                            [
                                new DownloadFileSnapshot
                                {
                                    Index = 1,
                                    Path = Path.Combine(directory.Path, "video.mp4"),
                                    Uris =
                                    [
                                        new DownloadUriSnapshot { Uri = source },
                                    ],
                                },
                            ],
                            Torrent = new TorrentSnapshot
                            {
                                AnnounceList = [["https://tracker.test/announce?token=tracker-token"]],
                            },
                        },
                    ],
                },
            ],
        };

        await store.SaveAsync(document);

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("p%40ss", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mirror-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("download-user", json, StringComparison.Ordinal);
        Assert.DoesNotContain("download-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("header-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("global-secret", json, StringComparison.Ordinal);
        Assert.Contains("\"split\": \"8\"", json, StringComparison.Ordinal);
        Assert.Contains("credentialReferences", json, StringComparison.Ordinal);

        var restored = await store.LoadAsync();

        Assert.Equal("global-secret", restored.GlobalOptions["rpc-secret"]);
        var request = Assert.Single(restored.Groups).Request;
        Assert.Equal(source, request.Source);
        Assert.Equal(mirror, Assert.Single(request.MirrorUris));
        Assert.Equal("download-user", request.Options["http-user"]);
        Assert.Equal("download-password", request.Options["http-passwd"]);
        Assert.Equal("Authorization: Bearer header-token", request.Options["header"]);
        Assert.Equal("8", request.Options["split"]);

        await store.SaveAsync(new DownloadSessionDocument());
        Assert.Empty(secrets.Values);
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public ValueTask SetAsync(
            CredentialRef reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            Values[reference.Id] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Values.GetValueOrDefault(reference.Id));

        public ValueTask<bool> DeleteAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Values.Remove(reference.Id));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
