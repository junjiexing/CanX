// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using CanX.Core.Diagnostics;

namespace CanX.Plugins.Lifecycle;

public sealed class PluginHotReloadService : IDisposable
{
    private readonly PluginLifecycleManager manager;
    private readonly IApplicationLog log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new(StringComparer.Ordinal);
    private readonly FileSystemWatcher watcher;
    private bool disposed;

    public PluginHotReloadService(PluginLifecycleManager manager, IApplicationLog? log = null)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.log = log ?? NullApplicationLog.Instance;
        Directory.CreateDirectory(manager.RootDirectory);
        watcher = new FileSystemWatcher(manager.RootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
    }

    public bool IsRunning => watcher.EnableRaisingEvents;

    public event Action<string>? Reloaded;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (disposed)
        {
            return;
        }
        watcher.EnableRaisingEvents = false;
        foreach (var cancellation in pending.Values)
        {
            cancellation.Cancel();
        }
        pending.Clear();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        Stop();
        disposed = true;
        watcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) => Schedule(eventArgs.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) => Schedule(eventArgs.FullPath);

    private void OnError(object sender, ErrorEventArgs eventArgs) =>
        log.Write(
            LogComponent.QuickJs,
            ApplicationLogLevel.Warning,
            "Plugin hot reload file watcher reported an error.",
            eventArgs.GetException());

    private void Schedule(string path)
    {
        var relative = Path.GetRelativePath(manager.RootDirectory, path);
        var pluginId = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (string.IsNullOrEmpty(pluginId) || pluginId.StartsWith('.'))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = pending.AddOrUpdate(pluginId, cancellation, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return cancellation;
        });
        if (!ReferenceEquals(previous, cancellation))
        {
            return;
        }
        _ = ReloadAfterDelayAsync(pluginId, cancellation);
    }

    private async Task ReloadAfterDelayAsync(string pluginId, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(350, cancellation.Token).ConfigureAwait(false);
            var status = await manager.ReloadAsync(pluginId, cancellation.Token).ConfigureAwait(false);
            if (status.IsEnabled)
            {
                Reloaded?.Invoke(pluginId);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Warning,
                $"Plugin hot reload failed: {pluginId}",
                exception);
        }
        finally
        {
            pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(pluginId, cancellation));
            cancellation.Dispose();
        }
    }
}
