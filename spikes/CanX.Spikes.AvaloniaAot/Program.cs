using Avalonia;

namespace CanX.Spikes.AvaloniaAot;

internal static class Program
{
    public const string SmokeArgument = "--smoke";

    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .UseWaylandWithFallback()
        .LogToTrace();
}
