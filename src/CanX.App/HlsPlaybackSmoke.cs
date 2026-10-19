// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls.ApplicationLifetimes;
using CanX.App.ViewModels;
using CanX.Core.Playback;
using CanX.Playback.Engine;
using CanX.Plugins.Manifest;
using CanX.Plugins.Media;
using CanX.Plugins.Security;
using CanX.Streaming;

namespace CanX.App;

public sealed partial class App
{
    // Exercise the real plugin manager, broker, native HLS demuxer and rendered
    // playback together. Synthetic fixtures need no network service or credentials.
    private static async Task RunPlaybackHlsSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, ShellViewModel viewModel,
        NativeMpvEngine engine, MediaStreamBroker broker, string[] arguments, int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 2 ||
                !double.TryParse(arguments[argumentIndex + 2], CultureInfo.InvariantCulture, out var expectedDuration) ||
                expectedDuration < 18)
                throw new ArgumentException("--playback-hls-smoke requires a playlist file and its duration (at least 18 seconds).");

            var path = Path.GetFullPath(arguments[argumentIndex + 1]);
            var manifest = new PluginManifest(1, "org.canx.hls-smoke", "HLS smoke", null, "1.0.0",
                new PluginApiVersion(2, 0), "main.mjs", [
                    new PluginPermission(PluginCapabilities.MediaStream, []),
                    new PluginPermission(PluginCapabilities.Http, ["fixture.example.test"]),
                ]);
            using var manager = new PluginMediaStreamManager(manifest.Id, PluginPermissionSet.FromManifest(manifest),
                broker, new PluginMediaPlaybackBroker(), () => new HlsFixtureHandler(Path.GetDirectoryName(path)!));
            using var created = JsonDocument.Parse(manager.Create(new JsonObject
            {
                ["url"] = "https://fixture.example.test/" + Uri.EscapeDataString(Path.GetFileName(path)),
                ["format"] = "hls",
            }.ToJsonString()));
            var source = created.RootElement.GetProperty("source").GetString()!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            await viewModel.InitializeAsync(timeout.Token);
            EnsureSmoke(viewModel.Playback.IsReady, viewModel.Playback.StatusMessage);
            engine.Command("set", "volume", "0");
            var playbackWindow = window.ShowPlaybackWindow();
            await playbackWindow.WaitForPlaybackSurfaceAsync(timeout.Token);
            await viewModel.Playback.OpenAsync(source, timeout.Token);
            await WaitForPlaybackStateAsync(viewModel.Playback, PlaybackState.Playing, timeout.Token);
            await playbackWindow.WaitForPlaybackFramesAsync(24, timeout.Token);
            await WaitForPlaybackConditionAsync(viewModel.Playback,
                () => Math.Abs(viewModel.Playback.DurationSeconds - expectedDuration) < 0.5, "whole HLS duration", timeout.Token);
            EnsureSmoke(engine.TryGetString("file-format", out var format) && format == "hls", $"Expected HLS demuxer, got {format}");
            EnsureSmoke(engine.GetTracks().Any(track => track.Type == PlaybackTrackType.Audio), "HLS audio track was not demuxed");

            var maxStall = 0d;
            async Task WatchUntil(double target)
            {
                var previous = viewModel.Playback.PositionSeconds;
                var progress = Stopwatch.StartNew();
                while (viewModel.Playback.PositionSeconds < target)
                {
                    await Task.Delay(100, timeout.Token);
                    var position = viewModel.Playback.PositionSeconds;
                    EnsureSmoke(position >= previous - 0.5, "HLS position reset at a segment boundary");
                    EnsureSmoke(Math.Abs(viewModel.Playback.DurationSeconds - expectedDuration) < 0.5, "HLS duration changed at a segment boundary");
                    EnsureSmoke(engine.TryGetInt64("playlist-count", out var count) && count == 1, "HLS segments became separate playlist entries");
                    EnsureSmoke(engine.TryGetString("path", out var current) && current == source, "HLS lost its opaque public source");
                    if (position > previous + 0.01)
                    {
                        maxStall = Math.Max(maxStall, progress.Elapsed.TotalSeconds);
                        progress.Restart();
                        previous = position;
                    }
                    EnsureSmoke(progress.Elapsed < TimeSpan.FromSeconds(3), "HLS stalled across a segment boundary");
                }
            }

            await WatchUntil(7);
            var forward = expectedDuration - 6;
            await viewModel.Playback.SeekAsync(forward, cancellationToken: timeout.Token);
            await WaitForPlaybackConditionAsync(viewModel.Playback,
                () => Math.Abs(viewModel.Playback.PositionSeconds - forward) < 1, "forward HLS seek", timeout.Token);
            await WatchUntil(forward + 2);
            await viewModel.Playback.SeekAsync(2, cancellationToken: timeout.Token);
            await WaitForPlaybackConditionAsync(viewModel.Playback,
                () => Math.Abs(viewModel.Playback.PositionSeconds - 2) < 1, "backward HLS seek", timeout.Token);
            await WatchUntil(7);
            EnsureSmoke(playbackWindow.RenderedFrameCount > 100, "HLS did not render enough frames");
            await viewModel.Playback.StopAsync(timeout.Token);
            Console.WriteLine($"CANX_PLAYBACK_HLS_OK duration={expectedDuration} format={format} forward={forward} backward=2 maxStall={maxStall:F3} frames={playbackWindow.RenderedFrameCount}");
            playbackWindow.Close();
            window.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_PLAYBACK_HLS_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }

    private sealed class HlsFixtureHandler(string directory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'))));
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture escaped its directory.");
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var range = request.Headers.Range?.Ranges.Single();
            var start = checked((int)(range?.From ?? 0));
            var end = checked((int)Math.Min(range?.To ?? bytes.Length - 1, bytes.Length - 1));
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes[start..(end + 1)]),
            };
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            return response;
        }
    }
}
