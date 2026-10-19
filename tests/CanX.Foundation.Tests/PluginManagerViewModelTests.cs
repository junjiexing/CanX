// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using CanX.App.ViewModels;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PluginManagerViewModelTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "CanX.PluginManagerViewModelTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PluginItem_ExposesManifestDetailsAndBuiltInIcon()
    {
        var manifest = new PluginManifest(
            1,
            "org.canx.details-test",
            "Details Test",
            "A complete plugin description.",
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            [],
            new PluginUiManifest("Details", new PluginUiIcon(PluginUiIconKind.BuiltIn, "info")));
        var status = new PluginStatus(
            manifest,
            root,
            new string('0', 64),
            DateTimeOffset.UtcNow,
            false,
            false,
            PluginHostState.Created,
            null);

        using var item = new PluginItemViewModel(status);

        Assert.Equal("Details Test", item.Name);
        Assert.Equal("A complete plugin description.", item.Description);
        Assert.True(item.HasBuiltinIcon);
        Assert.False(item.HasAssetIcon);
        Assert.Equal("i", item.IconGlyph);
    }

    [Fact]
    public async Task InstallEnableDisableUpgradeAndUninstall_RefreshUiState()
    {
        Directory.CreateDirectory(root);
        var lifecycle = new PluginLifecycleManager(Path.Combine(root, "plugins"), new FakeHostFactory());
        await using var viewModel = new PluginManagerViewModel(
            lifecycle,
            Path.Combine(root, "developer-mode"),
            Path.Combine(root, "logs"));
        await viewModel.InitializeAsync();
        var package = WritePackage("1.0.0", permissionsJson: "[]");

        await viewModel.InstallAsync(package);

        var installed = Assert.Single(viewModel.Plugins);
        Assert.Equal("1.0.0", installed.Version);
        Assert.Same(installed, viewModel.SelectedPlugin);
        await viewModel.EnableSelectedAsync();
        Assert.True(viewModel.SelectedPlugin?.IsEnabled);
        Assert.Equal("运行中", viewModel.SelectedPlugin?.Status);

        var upgrade = WritePackage(
            "1.1.0",
            permissionsJson: "[{\"capability\":\"storage\"}]");
        await viewModel.InstallAsync(upgrade);

        Assert.True(viewModel.SelectedPlugin?.RequiresApproval);
        Assert.True(viewModel.SelectedPlugin?.CanRollback);
        Assert.Equal("1.0.0", viewModel.SelectedPlugin?.RollbackVersion);
        Assert.Contains("+ storage", viewModel.PermissionChanges, StringComparison.Ordinal);
        await viewModel.RollbackSelectedAsync();
        Assert.Equal("1.0.0", viewModel.SelectedPlugin?.Version);
        Assert.True(viewModel.SelectedPlugin?.IsEnabled);
        Assert.Contains("- storage", viewModel.PermissionChanges, StringComparison.Ordinal);
        await viewModel.DisableSelectedAsync();
        await viewModel.UninstallSelectedAsync();
        Assert.Empty(viewModel.Plugins);
    }

    [Fact]
    public async Task DeveloperMode_IsPersistedWithoutApplicationSettingsFile()
    {
        Directory.CreateDirectory(root);
        var modePath = Path.Combine(root, "developer-mode");
        await using (var first = new PluginManagerViewModel(
                         new PluginLifecycleManager(Path.Combine(root, "plugins"), new FakeHostFactory()),
                         modePath,
                         Path.Combine(root, "logs")))
        {
            await first.InitializeAsync();
            first.IsDeveloperMode = true;
        }

        await using var second = new PluginManagerViewModel(
            new PluginLifecycleManager(Path.Combine(root, "plugins"), new FakeHostFactory()),
            modePath,
            Path.Combine(root, "logs"));
        await second.InitializeAsync();

        Assert.True(second.IsDeveloperMode);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string WritePackage(string version, string permissionsJson)
    {
        var path = Path.Combine(root, $"plugin-{version}.canxplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("canx-plugin.json");
        using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
        {
            writer.Write($$"""
                {
                  "manifestVersion": 1,
                  "id": "org.canx.ui-test",
                  "name": "UI Test",
                  "version": "{{version}}",
                  "apiVersion": "2.0",
                  "entry": "main.mjs",
                  "permissions": {{permissionsJson}}
                }
                """);
        }
        var entry = archive.CreateEntry("main.mjs");
        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
        {
            writer.Write("export const plugin = true;");
        }
        return path;
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
}
