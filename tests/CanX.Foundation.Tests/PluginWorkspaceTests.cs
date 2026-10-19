// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Application.Plugins;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PluginWorkspaceTests
{
    [Fact]
    public async Task PrepareAndInstall_RequireExactReviewedDigest()
    {
        using var root = new TemporaryDirectory();
        var factory = new FakeHostFactory();
        await using var lifecycle = new PluginLifecycleManager(Path.Combine(root.Path, "installed"), factory);
        await lifecycle.InitializeAsync();
        var manager = new PluginWorkspaceManager(Path.Combine(root.Path, "workspaces"), lifecycle, factory);
        var created = await manager.CreateAsync("org.canx.agent-test", "Agent Test", null, CancellationToken.None);
        var workspaceId = created["workspaceId"]!.GetValue<string>();

        await manager.WriteAsync(
            workspaceId,
            "main.mjs",
            "export {};\nconsole.log('changed');\n",
            CancellationToken.None);
        var review = await manager.PrepareInstallAsync(workspaceId, CancellationToken.None);
        var token = review["reviewToken"]!.GetValue<string>();
        var trustedReview = await manager.GetInstallReviewAsync(workspaceId, token, CancellationToken.None);
        Assert.Equal("org.canx.agent-test", trustedReview["plugin"]!["id"]!.GetValue<string>());
        Assert.Equal("passed", trustedReview["checks"]!["sandboxTest"]!.GetValue<string>()[..6]);
        var installed = await manager.InstallAsync(workspaceId, token, CancellationToken.None);

        Assert.True(installed["installed"]!.GetValue<bool>());
        Assert.Equal("org.canx.agent-test", Assert.Single(lifecycle.Plugins).Manifest.Id);
        Assert.Empty(Assert.IsType<JsonArray>((await manager.DiffAsync(workspaceId, CancellationToken.None))["changes"]));

        var secondReview = await manager.PrepareInstallAsync(workspaceId, CancellationToken.None);
        var staleToken = secondReview["reviewToken"]!.GetValue<string>();
        await manager.WriteAsync(workspaceId, "notes.md", "changed after review", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InstallAsync(workspaceId, staleToken, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GetInstallReviewAsync(workspaceId, staleToken, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Lint_RejectsImportsOutsideWorkspaceModuleGraph()
    {
        using var root = new TemporaryDirectory();
        var factory = new FakeHostFactory();
        await using var lifecycle = new PluginLifecycleManager(Path.Combine(root.Path, "installed"), factory);
        await lifecycle.InitializeAsync();
        var manager = new PluginWorkspaceManager(Path.Combine(root.Path, "workspaces"), lifecycle, factory);
        var created = await manager.CreateAsync("org.canx.import-test", "Import Test", null, CancellationToken.None);
        var workspaceId = created["workspaceId"]!.GetValue<string>();

        await manager.WriteAsync(workspaceId, "main.mjs", "import '/etc/passwd';", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.RunAsync(workspaceId, "lint", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Review_RejectsTamperedPackageAndWorkspaceSymlinks()
    {
        using var root = new TemporaryDirectory();
        var factory = new FakeHostFactory();
        await using var lifecycle = new PluginLifecycleManager(Path.Combine(root.Path, "installed"), factory);
        await lifecycle.InitializeAsync();
        var manager = new PluginWorkspaceManager(Path.Combine(root.Path, "workspaces"), lifecycle, factory);
        var created = await manager.CreateAsync("org.canx.tamper-test", "Tamper Test", null, CancellationToken.None);
        var workspaceId = created["workspaceId"]!.GetValue<string>();
        var review = await manager.PrepareInstallAsync(workspaceId, CancellationToken.None);
        var token = review["reviewToken"]!.GetValue<string>();
        var package = Path.Combine(root.Path, "workspaces", workspaceId, "artifacts", "plugin.canxplugin");

        await File.AppendAllTextAsync(package, "tampered");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GetInstallReviewAsync(workspaceId, token, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InstallAsync(workspaceId, token, CancellationToken.None).AsTask());

        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(root.Path, "outside.txt");
            await File.WriteAllTextAsync(outside, "outside");
            File.CreateSymbolicLink(
                Path.Combine(root.Path, "workspaces", workspaceId, "content", "linked.txt"),
                outside);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                manager.ListFilesAsync(workspaceId, CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public async Task FileOperations_BlockTraversalSymlinksAndArbitraryCommands()
    {
        using var root = new TemporaryDirectory();
        var factory = new FakeHostFactory();
        await using var lifecycle = new PluginLifecycleManager(Path.Combine(root.Path, "installed"), factory);
        await lifecycle.InitializeAsync();
        var manager = new PluginWorkspaceManager(Path.Combine(root.Path, "workspaces"), lifecycle, factory);
        var created = await manager.CreateAsync("org.canx.safe-test", "Safe Test", null, CancellationToken.None);
        var workspaceId = created["workspaceId"]!.GetValue<string>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.WriteAsync(workspaceId, "../escape.txt", "no", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.WriteAsync(workspaceId, ".hidden", "no", CancellationToken.None).AsTask());

        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers(PluginWorkspaceToolCatalog.Create(manager));
        var result = await registry.InvokeAsync(
            "plugins.workspace.run",
            new JsonObject { ["workspaceId"] = workspaceId, ["command"] = "sh" },
            new ToolCallContext("agent", HasUserApproval: true));

        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(root.Path, "escape.txt")));
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
            cancellationToken.ThrowIfCancellationRequested();
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canx-workspace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
