// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Logging;
using Avalonia.Threading;
using System.Runtime.CompilerServices;

namespace CanX.App;

internal static class Program
{
#if CANX_SMOKE_TESTS
    public const string SmokeArgument = "--smoke";
    public const string DownloadSmokeArgument = "--download-smoke";
    public const string DownloadFixtureSmokeArgument = "--download-fixture-smoke";
    public const string PlaybackSmokeArgument = "--playback-smoke";
    public const string MultiWindowPlaybackSmokeArgument = "--multiwindow-playback-smoke";
    public const string SubtitleSliderSmokeArgument = "--subtitle-slider-smoke";
    public const string PlaybackStreamSmokeArgument = "--playback-stream-smoke";
    public const string PlaybackHlsSmokeArgument = "--playback-hls-smoke";
    public const string DownloadPlaybackSmokeArgument = "--download-playback-smoke";
    public const string FtpPlaybackSmokeArgument = "--ftp-playback-smoke";
    public const string SmbPlaybackSmokeArgument = "--smb-playback-smoke";
    public const string PlaybackStabilitySmokeArgument = "--playback-stability-smoke";
#endif

    /// <summary>
    /// Primary-instance gate on Windows; <see cref="App"/> listens on it for files
    /// opened from Explorer while CanX is already running.
    /// </summary>
    internal static SingleInstanceGate? InstanceGate { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux() && !RuntimeFeature.IsDynamicCodeSupported)
        {
            // X11 starts its AT-SPI bridge before Application.RegisterServices.
            // Install the UI synchronization context early so the asynchronous
            // bridge initialization cannot resume on a worker thread under AOT.
            AvaloniaSynchronizationContext.InstallIfNeeded();
        }

        if (OperatingSystem.IsWindows() && !args.Any(static argument => argument.StartsWith("--", StringComparison.Ordinal)))
        {
            InstanceGate = SingleInstanceGate.TryAcquire(App.GetApplicationDataDirectory(), args);
            if (InstanceGate is null)
            {
                return;
            }
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceGate?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder
            .Configure<App>()
            .UsePlatformDetect();

        if (OperatingSystem.IsWindows())
        {
            // The Windows NativeAOT image statically links ANGLE together with
            // SkiaSharp and HarfBuzzSharp. Keep software as the fallback for
            // systems on which a D3D-backed EGL context cannot be created.
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode =
                [
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Software,
                ],
                CompositionMode =
                [
                    Win32CompositionMode.RedirectionSurface,
                ],
            });
        }

        if (OperatingSystem.IsMacOS())
        {
            // The playback surface needs Avalonia's OpenGL context factory.
            // Another graphics renderer can draw the shell while leaving the
            // player permanently waiting for an OpenGL render context.
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl],
            });
        }

        // Screenshot and accessibility checks run in a deterministic Xvfb
        // session. Production Linux sessions still prefer Wayland and retain
        // Avalonia's X11 fallback.
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CANX_FORCE_X11"),
                "1",
                StringComparison.Ordinal))
        {
            builder = builder.UseWaylandWithFallback();
        }

        var writeUiTrace = string.Equals(
            Environment.GetEnvironmentVariable("CANX_UI_TRACE"),
            "1",
            StringComparison.Ordinal);
        Logger.Sink = new AvaloniaApplicationLogSink(writeUiTrace);
        return builder;
    }
}
