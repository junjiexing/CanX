// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;
using CanX.Core.Events;
using CanX.Downloads;
using CanX.Downloads.Engine;
using CanX.Downloads.Persistence;

namespace CanX.Downloads.Integration;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--magnet-shutdown-smoke", var destination])
        {
            return await RunMagnetShutdownSmokeAsync(destination);
        }

        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Usage: CanX.Downloads.Integration <URI> <destination> <expected-file> | " +
                "--magnet-shutdown-smoke <destination>");
            return 2;
        }

        var sessionPath = Path.Combine(args[1], "downloads.session.json");
        var store = new JsonDownloadSessionStore(sessionPath);
        Guid stableId;

        await using (var manager = CreateManager(store))
        {
            await manager.StartAsync();
            Console.WriteLine(
                $"CANX_DOWNLOAD_CAPABILITIES abi={manager.Capabilities.AbiVersion} " +
                $"extended={manager.Capabilities.ExtendedApi.ToString().ToLowerInvariant()} " +
                $"features={manager.Capabilities.Features.Count} options={manager.Capabilities.Options.Count}");
            var tasks = await manager.AddAsync(new AddDownloadRequest
            {
                Source = args[0],
                DestinationDirectory = args[1],
                OutputName = Path.GetFileName(args[2]),
                StartPaused = true,
            });
            var task = RequireSingle(tasks);
            stableId = task.Id;
            Ensure(task.State == DownloadState.Paused, "new task starts paused");

            await manager.ResumeAsync(stableId);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            var lastReport = DateTimeOffset.MinValue;
            while (DateTimeOffset.UtcNow < deadline)
            {
                task = RequireSingle(manager.Tasks);
                if (task.State == DownloadState.Complete)
                {
                    break;
                }

                if (task.State == DownloadState.Error)
                {
                    throw new InvalidOperationException($"Download failed with aria2 error {task.ErrorCode}.");
                }

                if (DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(
                        $"CANX_DOWNLOAD_PROGRESS state={task.State} completed={task.CompletedLength} " +
                        $"total={task.TotalLength} speed={task.DownloadSpeed}");
                    lastReport = DateTimeOffset.UtcNow;
                }

                await Task.Delay(100);
            }

            task = RequireSingle(manager.Tasks);
            Ensure(
                task.State == DownloadState.Complete,
                $"download completes before timeout (state={task.State}, completed={task.CompletedLength}, " +
                $"total={task.TotalLength}, speed={task.DownloadSpeed})");
            Ensure(task.CompletedLength == task.TotalLength, "download length matches");
        }

        await using (var restored = CreateManager(store))
        {
            await restored.StartAsync();
            var task = RequireSingle(restored.Tasks);
            Ensure(task.Id == stableId, "stable task identity survives restart");
            Ensure(task.State == DownloadState.Complete, "completed state survives restart");
            await restored.RemoveAsync(stableId);
            Ensure(restored.Tasks.Count == 0, "removed task leaves visible task list");
        }

        Ensure(File.Exists(args[2]), "downloaded file remains after removing task without deleteFiles");
        Console.WriteLine($"CANX_DOWNLOAD_MANAGER_OK id={stableId:N} file={args[2]}");
        return 0;
    }

    private static async Task<int> RunMagnetShutdownSmokeAsync(string destination)
    {
        Directory.CreateDirectory(destination);
        var store = new JsonDownloadSessionStore(Path.Combine(destination, "magnet-shutdown.session.json"));
        var manager = CreateManager(store);
        await manager.StartAsync();
        await manager.AddAsync(new AddDownloadRequest
        {
            Source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            DestinationDirectory = destination,
        });
        await Task.Delay(250);

        var startedAt = DateTimeOffset.UtcNow;
        await manager.DisposeAsync();
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Ensure(elapsed < TimeSpan.FromSeconds(5), $"Magnet shutdown completes promptly ({elapsed.TotalSeconds:F2}s)");
        var persisted = await store.LoadAsync();
        Ensure(
            persisted.Groups.Count == 1 && persisted.Groups[0].Tasks.Count == 1,
            "active Magnet task is persisted before shutdown");
        Console.WriteLine($"CANX_MAGNET_SHUTDOWN_OK elapsedMs={elapsed.TotalMilliseconds:F0}");
        return 0;
    }

    private static DownloadManager CreateManager(IDownloadSessionStore store) =>
        new(new NativeAria2Engine(), store, new InMemoryEventBus());

    private static DownloadTaskSnapshot RequireSingle(IReadOnlyList<DownloadTaskSnapshot> tasks)
    {
        Ensure(tasks.Count == 1, $"expected one task, got {tasks.Count}");
        return tasks[0];
    }

    private static void Ensure(bool condition, string operation)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Verification failed: {operation}.");
        }
    }
}
