// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginHotReloadServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "CanX.PluginHotReloadTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChangedModule_RestartsEnabledPluginAfterDebounce()
    {
        var factory = new FakeHostFactory();
        await using var manager = new PluginLifecycleManager(root, factory);
        await manager.InitializeAsync();
        using var package = CreatePackage();
        var installed = await manager.InstallAsync(package);
        await manager.ApproveAndEnableAsync(installed.Manifest.Id);
        var reloaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new PluginHotReloadService(manager);
        service.Reloaded += pluginId => reloaded.TrySetResult(pluginId);
        service.Start();

        await File.AppendAllTextAsync(
            Path.Combine(installed.DirectoryPath, "main.mjs"),
            "\n// changed");
        var pluginId = await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(installed.Manifest.Id, pluginId);
        Assert.Equal(2, factory.Hosts.Count);
        Assert.Equal(1, factory.Hosts[0].DisposeCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MemoryStream CreatePackage()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "canx-plugin.json",
                """
                {
                  "manifestVersion": 1,
                  "id": "org.canx.hot-reload-test",
                  "name": "Hot Reload Test",
                  "version": "1.0.0",
                  "apiVersion": "2.0",
                  "entry": "main.mjs"
                }
                """);
            WriteEntry(archive, "main.mjs", "export const active = true;");
        }
        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
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
