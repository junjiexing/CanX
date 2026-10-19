// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Avalonia.Controls.ApplicationLifetimes;
using CanX.App.Automation;
using CanX.App.ViewModels;
using CanX.Core.Playback;
using CanX.Plugins.Manifest;
using CanX.Plugins.Media;
using CanX.Plugins.Security;
using CanX.Streaming;

namespace CanX.App;

public sealed partial class App
{
    private static async Task RunMultiWindowPlaybackSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, ShellViewModel model,
        MediaStreamBroker broker, PluginMediaPlaybackBroker pluginPlayback,
        string[] arguments, int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 2)
                throw new ArgumentException("Multi-window smoke requires a local video and an HLS playlist.");
            var mediaPath = Path.GetFullPath(arguments[argumentIndex + 1]);
            var hlsPath = Path.GetFullPath(arguments[argumentIndex + 2]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            var token = timeout.Token;
            await model.InitializeAsync(token);

            async Task SetMode(bool multiple)
            {
                model.Settings.SelectedPlaybackWindowMode = model.Settings.PlaybackWindowModes.Single(option => option.OpenNewWindow == multiple);
                model.Settings.SaveCommand.Execute(null);
                while (!model.Settings.SaveCommand.CanExecute(null)) await Task.Delay(20, token);
                EnsureSmoke(model.Settings.OpenMediaInNewWindow == multiple, model.Settings.StatusMessage);
            }

            async Task Ready(DesktopPlaybackSession session)
            {
                using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                readyTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await WaitForPlaybackStateAsync(session.ViewModel, PlaybackState.Playing, readyTimeout.Token);
                    await session.ViewModel.Controller.SetMutedAsync(true, readyTimeout.Token);
                    await session.Window!.WaitForPlaybackFramesAsync(24, readyTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Player did not become ready: state={session.ViewModel.State} source={session.ViewModel.Source} status={session.ViewModel.StatusMessage}");
                }
            }

            await SetMode(true);
            using var local = ConfiguredMediaPlaybackRequest.Local(Path.GetFileName(mediaPath), mediaPath, null);
            await window.PlayConfiguredMediaAsync(local, token);
            var first = window.PlaybackSessions.Single(session => session.Window is not null);
            await Ready(first);
            var firstWindow = first.Window!;

            var bytes = await File.ReadAllBytesAsync(mediaPath, token);
            using var callback = broker.Register("callback.mp4", () => new MemoryMediaByteStream(bytes));
            await window.PlayResolvedMediaAsync(new Uri(callback.Source), null, token);
            var second = window.PlaybackSessions.Single(session => session.Window is not null && session != first);
            second.StreamRegistrations.Add(callback);
            await Ready(second);

            var manifest = new PluginManifest(1, "org.canx.multiwindow-smoke", "Multi-window smoke", null, "1.0.0",
                new PluginApiVersion(2, 0), "main.mjs", [
                    new PluginPermission(PluginCapabilities.MediaStream, []),
                    new PluginPermission(PluginCapabilities.Http, ["fixture.example.test"]),
                ]);
            using var plugin = new PluginMediaStreamManager(manifest.Id, PluginPermissionSet.FromManifest(manifest),
                broker, pluginPlayback, () => new HlsFixtureHandler(Path.GetDirectoryName(hlsPath)!));
            await plugin.RequestPlayAsync(new JsonObject
            {
                ["recipe"] = new JsonObject
                {
                    ["url"] = "https://fixture.example.test/" + Uri.EscapeDataString(Path.GetFileName(hlsPath)),
                    ["format"] = "hls",
                },
            }.ToJsonString(), token);
            var third = window.PlaybackSessions.Single(session => session.Window is not null && session != first && session != second);
            await Ready(third);
            EnsureSmoke(window.PlaybackSessions.Count(session => session.Window is not null) == 3, "Expected three independent players.");
            Console.WriteLine("CANX_MULTIWINDOW_STAGE local_callback_hls");

            await first.ViewModel.TogglePauseAsync(token);
            await first.ViewModel.SetVolumeAsync(25, token);
            await WaitForPlaybackStateAsync(first.ViewModel, PlaybackState.Paused, token);
            var thirdPosition = third.ViewModel.PositionSeconds;
            await WaitForPlaybackConditionAsync(third.ViewModel,
                () => third.ViewModel.PositionSeconds > thirdPosition + 0.5, "independent HLS playback", token);
            EnsureSmoke(second.ViewModel.State == PlaybackState.Playing && second.ViewModel.Volume != 25,
                "Pausing or changing one player's volume affected another player.");

            second.Window!.Close();
            while (second.Window is not null || second.ViewModel.Controller.IsStarted) await Task.Delay(20, token);
            EnsureSmoke(!broker.TryOpen(callback.Source, out _), "Closed player's stream registration was not released.");
            EnsureSmoke(first.ViewModel.State == PlaybackState.Paused, "Closing another window stopped the paused player.");
            thirdPosition = third.ViewModel.PositionSeconds;
            await WaitForPlaybackConditionAsync(third.ViewModel,
                () => third.ViewModel.PositionSeconds > thirdPosition + 0.5, "HLS after closing callback player", token);
            Console.WriteLine("CANX_MULTIWINDOW_STAGE close_isolation");

            await SetMode(false);
            firstWindow.Activate();
            while (!ReferenceEquals(await window.ResolvePlaybackToolControllerAsync("playback.get", token), first.ViewModel.Controller))
                await Task.Delay(20, token);
            await window.PlayConfiguredMediaAsync(local, token);
            await Ready(first);
            EnsureSmoke(ReferenceEquals(first.Window, firstWindow), "Replace mode created a new window.");
            EnsureSmoke(window.PlaybackSessions.Count(session => session.Window is not null) == 2, "Replace mode changed the wrong window count.");
            EnsureSmoke(third.ViewModel.State == PlaybackState.Playing, "Replace mode interrupted the other player.");
            Console.WriteLine("CANX_MULTIWINDOW_PLAYBACK_OK windows=3 controls=isolated close=isolated replacement=active sources=local,callback,plugin-hls");
            desktop.Shutdown();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_MULTIWINDOW_PLAYBACK_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }
}
