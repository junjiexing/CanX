// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginLifecycleManagerTests
{
    [Fact]
    public async Task InstallAsync_LeavesNewPluginDisabledUntilExplicitEnable()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", []);

        var status = await manager.InstallAsync(package);

        Assert.False(status.IsEnabled);
        Assert.False(status.RequiresPermissionApproval);
        Assert.Equal(PluginHostState.Stopped, status.RuntimeState);
        Assert.Empty(factory.Hosts);
    }

    [Fact]
    public async Task ApproveAndEnableAsync_PersistsApprovalAndRestartsOnNextLaunch()
    {
        using var fixture = new LifecycleFixture();
        var firstFactory = new FakeHostFactory();
        await using (var manager = fixture.CreateManager(firstFactory))
        {
            await manager.InitializeAsync();
            using var package = CreatePackage("1.0.0", ["playback.open"]);
            var installed = await manager.InstallAsync(package);
            Assert.True(installed.RequiresPermissionApproval);

            var enabled = await manager.ApproveAndEnableAsync(PluginId);
            Assert.True(enabled.IsEnabled);
            Assert.False(enabled.RequiresPermissionApproval);
            Assert.Equal(PluginHostState.Running, enabled.RuntimeState);
            Assert.Single(firstFactory.Hosts);
        }

        var secondFactory = new FakeHostFactory();
        await using var restarted = fixture.CreateManager(secondFactory);
        await restarted.InitializeAsync();

        var restored = Assert.Single(restarted.Plugins);
        Assert.True(restored.IsEnabled);
        Assert.Equal(PluginHostState.Running, restored.RuntimeState);
        Assert.Single(secondFactory.Hosts);
    }

    [Fact]
    public async Task InstallAsync_UpgradeWithAddedPermissionStopsAndRequiresApproval()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using (var first = CreatePackage("1.0.0", ["playback.open"]))
        {
            await manager.InstallAsync(first);
        }
        await manager.ApproveAndEnableAsync(PluginId);
        var originalHost = Assert.Single(factory.Hosts);

        using var upgrade = CreatePackage("2.0.0", ["playback.open", "downloads.add"]);
        var status = await manager.InstallAsync(upgrade, allowUpgrade: true);

        Assert.False(status.IsEnabled);
        Assert.True(status.RequiresPermissionApproval);
        Assert.Equal("2.0.0", status.Manifest.Version);
        Assert.Equal(1, originalHost.DisposeCount);
        Assert.Single(factory.Hosts);
    }

    [Fact]
    public async Task InstallAsync_UpgradeWithoutPrivilegeIncreaseKeepsPluginEnabled()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using (var first = CreatePackage("1.0.0", ["playback.open", "downloads.add"]))
        {
            await manager.InstallAsync(first);
        }
        await manager.ApproveAndEnableAsync(PluginId);

        using var upgrade = CreatePackage("1.1.0", ["playback.open"]);
        var status = await manager.InstallAsync(upgrade, allowUpgrade: true);

        Assert.True(status.IsEnabled);
        Assert.False(status.RequiresPermissionApproval);
        Assert.Equal(PluginHostState.Running, status.RuntimeState);
        Assert.Equal(2, factory.Hosts.Count);
    }

    [Fact]
    public async Task InstallAsync_UpgradeFromDifferentSignerRequiresApprovalAgain()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using var originalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var first = CreatePackage("1.0.0", ["playback.open"], originalKey))
        {
            await manager.InstallAsync(first);
        }
        await manager.ApproveAndEnableAsync(PluginId);

        using var replacement = CreatePackage("1.1.0", ["playback.open"], otherKey);
        var status = await manager.InstallAsync(replacement, allowUpgrade: true);

        Assert.False(status.IsEnabled);
        Assert.True(status.RequiresPermissionApproval);
    }

    [Fact]
    public async Task InstallAsync_UpgradeFromSameSignerKeepsApproval()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var first = CreatePackage("1.0.0", ["playback.open"], key))
        {
            await manager.InstallAsync(first);
        }
        await manager.ApproveAndEnableAsync(PluginId);

        using var upgrade = CreatePackage("1.1.0", ["playback.open"], key);
        var status = await manager.InstallAsync(upgrade, allowUpgrade: true);

        Assert.True(status.IsEnabled);
        Assert.False(status.RequiresPermissionApproval);
    }

    [Fact]
    public async Task RollbackAsync_RestoresPreviousVersionApprovalAndWritesDetailedAudit()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        var audit = new CollectingAuditSink();
        await using var manager = fixture.CreateManager(factory, audit);
        await manager.InitializeAsync();
        using (var first = CreatePackage("1.0.0", ["playback.open"]))
        {
            await manager.InstallAsync(first);
        }
        await manager.ApproveAndEnableAsync(PluginId);
        using (var upgrade = CreatePackage("2.0.0", ["playback.open", "downloads.add"]))
        {
            var upgraded = await manager.InstallAsync(upgrade, allowUpgrade: true);
            Assert.Equal("1.0.0", upgraded.RollbackVersion);
            Assert.True(upgraded.RequiresPermissionApproval);
        }

        var restored = await manager.RollbackAsync(PluginId);

        Assert.Equal("1.0.0", restored.Manifest.Version);
        Assert.Equal("2.0.0", restored.RollbackVersion);
        Assert.True(restored.IsEnabled);
        Assert.False(restored.RequiresPermissionApproval);
        Assert.Equal(PluginHostState.Running, restored.RuntimeState);
        Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(restored.DirectoryPath, "main.mjs")), StringComparison.Ordinal);
        var upgradeAudit = Assert.Single(
            audit.Entries,
            entry => entry.Operation == "upgrade" && entry.Phase == PluginAuditPhase.Completed);
        Assert.Equal("1.0.0", upgradeAudit.PreviousVersion);
        Assert.Contains(upgradeAudit.PermissionChanges!, change => change.Contains("downloads.add", StringComparison.Ordinal));
        var rollbackAudit = Assert.Single(
            audit.Entries,
            entry => entry.Operation == "rollback" && entry.Phase == PluginAuditPhase.Completed);
        Assert.Equal("2.0.0", rollbackAudit.PreviousVersion);
        Assert.Contains(rollbackAudit.PermissionChanges!, change => change.Contains("downloads.add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RollbackVersion_PersistsAcrossManagerRestart()
    {
        using var fixture = new LifecycleFixture();
        await using (var manager = fixture.CreateManager(new FakeHostFactory()))
        {
            await manager.InitializeAsync();
            using (var first = CreatePackage("1.0.0", []))
            {
                await manager.InstallAsync(first);
            }
            using var upgrade = CreatePackage("2.0.0", []);
            await manager.InstallAsync(upgrade, allowUpgrade: true);
        }

        await using var restarted = fixture.CreateManager(new FakeHostFactory());
        await restarted.InitializeAsync();

        Assert.Equal("1.0.0", Assert.Single(restarted.Plugins).RollbackVersion);
        Assert.Equal("1.0.0", (await restarted.RollbackAsync(PluginId)).Manifest.Version);
    }

    [Fact]
    public async Task DisableAsync_StopsHostButKeepsPermissionApproval()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", ["playback.open"]);
        await manager.InstallAsync(package);
        await manager.ApproveAndEnableAsync(PluginId);
        var host = Assert.Single(factory.Hosts);

        var status = await manager.DisableAsync(PluginId);

        Assert.False(status.IsEnabled);
        Assert.False(status.RequiresPermissionApproval);
        Assert.Equal(PluginHostState.Stopped, status.RuntimeState);
        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task UninstallAsync_RemovesPluginAndPersistedState()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", []);
        var installed = await manager.InstallAsync(package);

        await manager.UninstallAsync(PluginId);

        Assert.Empty(manager.Plugins);
        Assert.False(Directory.Exists(installed.DirectoryPath));
        Assert.DoesNotContain(PluginId, File.ReadAllText(Path.Combine(fixture.Root, ".plugin-state.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReloadAsync_ReplacesRunningHostAndWritesAuditTrail()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        var audit = new CollectingAuditSink();
        await using var manager = fixture.CreateManager(factory, audit);
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", []);
        await manager.InstallAsync(package);
        await manager.ApproveAndEnableAsync(PluginId);
        var originalHost = Assert.Single(factory.Hosts);

        var status = await manager.ReloadAsync(PluginId);

        Assert.Equal(PluginHostState.Running, status.RuntimeState);
        Assert.Equal(1, originalHost.DisposeCount);
        Assert.Equal(2, factory.Hosts.Count);
        Assert.Equal(
            ["install", "approve-enable", "reload"],
            audit.Entries
                .Where(entry => entry.Phase == PluginAuditPhase.Completed)
                .Select(entry => entry.Operation));
    }

    [Fact]
    public async Task JsonLinesAuditSink_WritesStructuredDurableRecords()
    {
        using var fixture = new LifecycleFixture();
        var auditPath = Path.Combine(fixture.Root, "logs", "plugin-audit.jsonl");
        using var audit = new JsonLinesPluginAuditSink(auditPath);
        await using var manager = fixture.CreateManager(new FakeHostFactory(), audit);
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", ["playback.open"]);

        await manager.InstallAsync(package);

        var lines = File.ReadAllLines(auditPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"phase\":\"Started\"", lines[0], StringComparison.Ordinal);
        var line = lines[1];
        Assert.Contains("\"phase\":\"Completed\"", line, StringComparison.Ordinal);
        Assert.Contains("\"operation\":\"install\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"pluginId\":\"{PluginId}\"", line, StringComparison.Ordinal);
        Assert.Contains("\"permissionChanges\"", line, StringComparison.Ordinal);
        Assert.Contains("playback.open", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_RequiredAuditFailureRollsBackInstallation()
    {
        using var fixture = new LifecycleFixture();
        await using var manager = fixture.CreateManager(new FakeHostFactory(), new ThrowingAuditSink());
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", []);

        await Assert.ThrowsAsync<IOException>(() => manager.InstallAsync(package));

        Assert.Empty(manager.Plugins);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, PluginId)));
    }

    [Fact]
    public async Task UpgradeAsync_RequiredAuditFailureKeepsTheExistingRuntime()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        var audit = new FailOperationAuditSink("upgrade");
        await using var manager = fixture.CreateManager(factory, audit);
        await manager.InitializeAsync();
        using (var package = CreatePackage("1.0.0", []))
        {
            await manager.InstallAsync(package);
        }
        await manager.ApproveAndEnableAsync(PluginId);
        var originalHost = Assert.Single(factory.Hosts);
        using var upgrade = CreatePackage("2.0.0", []);

        await Assert.ThrowsAsync<IOException>(() => manager.InstallAsync(upgrade, allowUpgrade: true));

        var restored = Assert.Single(manager.Plugins);
        Assert.Equal("1.0.0", restored.Manifest.Version);
        Assert.Equal(PluginHostState.Running, restored.RuntimeState);
        Assert.Equal(0, originalHost.DisposeCount);
        Assert.Single(factory.Hosts);
        Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(restored.DirectoryPath, "main.mjs")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_CompletionAuditFailureDoesNotReportCommittedInstallAsFailed()
    {
        using var fixture = new LifecycleFixture();
        var audit = new FailAfterFirstAuditSink();
        await using var manager = fixture.CreateManager(new FakeHostFactory(), audit);
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", []);

        var status = await manager.InstallAsync(package);

        Assert.Equal(PluginId, status.Manifest.Id);
        Assert.True(Directory.Exists(status.DirectoryPath));
        Assert.Equal(PluginAuditPhase.Started, Assert.Single(audit.Entries).Phase);
    }

    [Fact]
    public async Task InstallAsync_StateSaveFailureRemovesActivatedPlugin()
    {
        using var fixture = new LifecycleFixture();
        await using var manager = fixture.CreateManager(new FakeHostFactory());
        await manager.InitializeAsync();
        var statePath = Path.Combine(fixture.Root, ".plugin-state.json");
        File.Delete(statePath);
        Directory.CreateDirectory(statePath);
        using var package = CreatePackage("1.0.0", []);

        AssertStateWriteFailure(await Record.ExceptionAsync(() => manager.InstallAsync(package)));

        Assert.Empty(manager.Plugins);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, PluginId)));
    }

    [Fact]
    public async Task UninstallAsync_StateSaveFailureRestoresDirectoryAndRunningHost()
    {
        using var fixture = new LifecycleFixture();
        var factory = new FakeHostFactory();
        await using var manager = fixture.CreateManager(factory);
        await manager.InitializeAsync();
        using var package = CreatePackage("1.0.0", []);
        var installed = await manager.InstallAsync(package);
        await manager.ApproveAndEnableAsync(PluginId);
        var statePath = Path.Combine(fixture.Root, ".plugin-state.json");
        File.Delete(statePath);
        Directory.CreateDirectory(statePath);

        AssertStateWriteFailure(await Record.ExceptionAsync(() => manager.UninstallAsync(PluginId)));

        Assert.Single(manager.Plugins);
        Assert.True(Directory.Exists(installed.DirectoryPath));
        Assert.Equal(PluginHostState.Running, factory.Hosts[^1].State);
    }

    // Replacing the state file with a directory fails as an I/O error on Unix and as access denied on Windows.
    private static void AssertStateWriteFailure(Exception? exception) =>
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected a state write failure, got {exception?.GetType().Name ?? "no exception"}.");

    private static MemoryStream CreatePackage(string version, string[] toolScopes, ECDsa? signingKey = null)
    {
        var permission = toolScopes.Length == 0
            ? string.Empty
            : $$"""
              ,
                "permissions": [
                  { "capability": "tools.invoke", "scopes": [{{string.Join(',', toolScopes.Select(scope => $"\"{scope}\""))}}] }
                ]
              """;
        var manifest =
            $$"""
            {
              "manifestVersion": 1,
              "id": "{{PluginId}}",
              "name": "Lifecycle Test",
              "version": "{{version}}",
              "apiVersion": "2.0",
              "entry": "main.mjs"{{permission}}
            }
            """;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["canx-plugin.json"] = new UTF8Encoding(false).GetBytes(manifest),
            ["main.mjs"] = new UTF8Encoding(false).GetBytes($"export const version = \"{version}\";"),
        };
        if (signingKey is not null)
        {
            files[PluginSignatureVerifier.SignatureFileName] = PluginPackageSigner.CreateSignatureDocument(files, signingKey);
        }

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                entryStream.Write(file.Value);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private const string PluginId = "org.canx.lifecycle-test";

    private sealed class LifecycleFixture : IDisposable
    {
        public LifecycleFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "CanX.PluginLifecycleTests", Guid.NewGuid().ToString("N"));
        }

        public string Root { get; }

        public PluginLifecycleManager CreateManager(
            IPluginRuntimeHostFactory factory,
            IPluginAuditSink? audit = null) => new(Root, factory, audit: audit);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class CollectingAuditSink : IPluginAuditSink
    {
        public List<PluginAuditEntry> Entries { get; } = [];

        public ValueTask WriteAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAuditSink : IPluginAuditSink
    {
        public ValueTask WriteAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Audit storage is unavailable."));
    }

    private sealed class FailAfterFirstAuditSink : IPluginAuditSink
    {
        public List<PluginAuditEntry> Entries { get; } = [];

        public ValueTask WriteAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (Entries.Count > 0)
            {
                return ValueTask.FromException(new IOException("Audit storage is unavailable."));
            }

            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOperationAuditSink(string operation) : IPluginAuditSink
    {
        public ValueTask WriteAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default) =>
            entry.Operation == operation && entry.Phase == PluginAuditPhase.Started
                ? ValueTask.FromException(new IOException("Audit storage is unavailable."))
                : ValueTask.CompletedTask;
    }

    private sealed class FakeHostFactory : IPluginRuntimeHostFactory
    {
        public List<FakeHost> Hosts { get; } = [];

        public IPluginRuntimeHost Create(PluginInstallation installation)
        {
            var host = new FakeHost(installation.Manifest);
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class FakeHost(PluginManifest manifest) : IPluginRuntimeHost
    {
        public PluginManifest Manifest { get; } = manifest;

        public PluginHostState State { get; private set; } = PluginHostState.Created;

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PluginHostState.Running;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            State = PluginHostState.Stopped;
            return ValueTask.CompletedTask;
        }
    }
}
