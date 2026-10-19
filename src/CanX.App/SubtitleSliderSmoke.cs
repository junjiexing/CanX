// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using CanX.App.ViewModels;
using CanX.Core.Playback;
using CanX.Playback.Engine;
using SkiaSharp;

namespace CanX.App;

public sealed partial class App
{
    private static async Task RunSubtitleSliderSmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, ShellViewModel model,
        NativeMpvEngine engine, string[] arguments, int argumentIndex)
    {
        try
        {
            if (arguments.Length <= argumentIndex + 2)
                throw new ArgumentException("Subtitle slider smoke requires a video and a subtitle file.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = timeout.Token;
            await model.InitializeAsync(token);
            var playback = model.Playback;
            var player = window.ShowPlaybackWindow();
            await player.WaitForPlaybackSurfaceAsync(token);
            await playback.OpenAsync(Path.GetFullPath(arguments[argumentIndex + 1]), token);
            await WaitForPlaybackStateAsync(playback, PlaybackState.Playing, token);
            await playback.Controller.SetMutedAsync(true, token);
            await playback.AddSubtitleAsync(Path.GetFullPath(arguments[argumentIndex + 2]), cancellationToken: token);
            await player.WaitForPlaybackFramesAsync(24, token);

            async Task Check(Slider slider, string property, double target)
            {
                // SetCurrentValue preserves the binding, as the slider's pointer
                // and keyboard handling does. No release event is raised here.
                slider.SetCurrentValue(RangeBase.ValueProperty, target);
                if (property == "volume")
                {
                    var feedback = player.FindControl<Border>("GestureFeedback")!;
                    var feedbackText = player.FindControl<TextBlock>("GestureFeedbackText")!;
                    EnsureSmoke(feedback.IsVisible && feedbackText.Text == $"音量 {target:0}%",
                        "Volume slider did not show its current percentage.");
                }
                await WaitForPlaybackConditionAsync(playback,
                    () => engine.TryGetDouble(property, out var value) && Math.Abs(value - target) < 0.001,
                    $"live {property}={target}", token);
                for (var tick = 0; tick < 12; tick++)
                {
                    await Task.Delay(50, token);
                    EnsureSmoke(Math.Abs(slider.Value - target) < 0.001, $"{property} slider reverted to {slider.Value}");
                    EnsureSmoke(engine.TryGetDouble(property, out var value) && Math.Abs(value - target) < 0.001,
                        $"{property} was not retained by mpv");
                }
                Console.WriteLine($"CANX_SUBTITLE_SLIDER_OK property={property} value={target.ToString(CultureInfo.InvariantCulture)} release=not-required");
            }

            var volume = player.FindControl<Slider>("PlaybackVolumeSlider")!;
            await Check(volume, "volume", 73);
            await Check(volume, "volume", 0);
            await Check(volume, "volume", 130);
            for (var value = 20; value <= 100; value += 5)
                volume.SetCurrentValue(RangeBase.ValueProperty, (double)value);
            await Check(volume, "volume", 64);
            engine.Command("set", "volume", "35");
            await WaitForPlaybackConditionAsync(playback,
                () => playback.Volume == 35 && volume.Value == 35, "external volume", token);

            var delay = player.FindControl<Slider>("SubtitleDelaySlider")!;
            var size = player.FindControl<Slider>("SubtitleScaleSlider")!;
            var position = player.FindControl<Slider>("SubtitlePositionSlider")!;
            await Check(delay, "sub-delay", -2.5);
            await Check(size, "sub-scale", 1.5);
            await Check(position, "sub-pos", 45);
            for (var value = 50; value <= 200; value += 5)
                size.SetCurrentValue(RangeBase.ValueProperty, value / 100d);
            await Check(size, "sub-scale", 2);

            // An external setting outside the slider's range must not be written
            // back to mpv as the slider's clamped display value.
            engine.Command("set", "sub-scale", "4");
            await WaitForPlaybackConditionAsync(playback,
                () => playback.SubtitleScale == 4 && size.Value == size.Maximum,
                "external subtitle size", token);
            await Task.Delay(400, token);
            EnsureSmoke(engine.TryGetDouble("sub-scale", out var externalSize) && externalSize == 4,
                "Slider synchronization overwrote an external subtitle size.");
            await playback.Controller.SetPausedAsync(true, token);
            await WaitForPlaybackStateAsync(playback, PlaybackState.Paused, token);
            await Check(volume, "volume", 21);
            Console.WriteLine("CANX_VOLUME_SLIDER_OK live=true stable=true external_sync=true paused=true release=not-required");
            await Check(size, "sub-scale", 1);
            await Check(delay, "sub-delay", 0);
            await Check(position, "sub-pos", 100);

            // Compare real mpv output at a fixed frame. A property-only check
            // misses ASS scripts ignoring sub-font-size entirely.
            var directory = Path.Combine(GetApplicationDataDirectory(), "subtitle-render-smoke");
            Directory.CreateDirectory(directory);
            async Task<string> Capture(string name, PlaybackScreenshotMode mode = PlaybackScreenshotMode.Subtitles)
            {
                var path = Path.Combine(directory, name + ".png");
                await playback.Controller.TakeScreenshotAsync(path, mode, token);
                await WaitForPlaybackConditionAsync(playback, () => File.Exists(path), "subtitle screenshot", token);
                return path;
            }

            using var background = SKBitmap.Decode(await Capture("video", PlaybackScreenshotMode.Video));
            (int Width, int Height, int Pixels) Measure(string path)
            {
                using var rendered = SKBitmap.Decode(path);
                EnsureSmoke(rendered is not null && background is not null &&
                    rendered.Width == background.Width && rendered.Height == background.Height, "Screenshot size mismatch.");
                var minX = rendered!.Width;
                var minY = rendered.Height;
                var maxX = -1;
                var maxY = -1;
                var pixels = 0;
                for (var y = 0; y < rendered.Height; y++)
                for (var x = 0; x < rendered.Width; x++)
                {
                    var a = background!.GetPixel(x, y);
                    var b = rendered.GetPixel(x, y);
                    if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) < 90)
                        continue;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                    pixels++;
                }
                EnsureSmoke(pixels > 50, "No visible subtitle pixels in screenshot.");
                return (maxX - minX + 1, maxY - minY + 1, pixels);
            }

            var normal = Measure(await Capture("scale-100"));
            if (Path.GetExtension(arguments[argumentIndex + 2]).ToLowerInvariant() is ".ass" or ".ssa")
            {
                engine.Command("set", "sub-font-size", "76");
                await Task.Delay(400, token);
                var fontOnly = Measure(await Capture("old-font-size-76"));
                EnsureSmoke(fontOnly == normal, "ASS fixture must supply its own size to reproduce the original bug.");
                engine.Command("set", "sub-font-size", "38");
                Console.WriteLine("CANX_SUBTITLE_OLD_BUG_REPRODUCED parameter=76 rendered_size=unchanged");
            }
            await Check(size, "sub-scale", 2);
            var enlarged = Measure(await Capture("scale-200"));
            await Check(size, "sub-scale", 0.75);
            var reduced = Measure(await Capture("scale-75"));
            EnsureSmoke(enlarged.Width > normal.Width * 1.6 && enlarged.Pixels > normal.Pixels * 2,
                $"Subtitles did not enlarge: {normal} -> {enlarged}");
            EnsureSmoke(reduced.Width < normal.Width * 0.9 && reduced.Pixels < normal.Pixels * 0.8,
                $"Subtitles did not shrink: {normal} -> {reduced}");
            Console.WriteLine($"CANX_SUBTITLE_RENDER_OK normal={normal} enlarged={enlarged} reduced={reduced} directory={directory}");
            Console.WriteLine("CANX_SUBTITLE_SLIDERS_OK live=true stable=true external_sync=true paused=true rendered=true");
            desktop.Shutdown();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CANX_SUBTITLE_SLIDERS_FAILED {exception}");
            desktop.Shutdown(1);
        }
    }
}
