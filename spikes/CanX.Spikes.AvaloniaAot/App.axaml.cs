using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CanX.Spikes.AvaloniaAot;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (desktop.Args?.Contains(Program.SmokeArgument, StringComparer.Ordinal) == true)
            {
                window.Opened += (_, _) =>
                {
                    var platformHandle = window.TryGetPlatformHandle();
                    var backend = platformHandle?.HandleDescriptor
                        ?? (OperatingSystem.IsLinux() ? "Wayland" : "unknown");
                    Console.WriteLine(
                        $"CANX_AVALONIA_AOT_OK os={RuntimeInformation.OSDescription} " +
                        $"arch={RuntimeInformation.ProcessArchitecture} " +
                        $"backend={backend}");
                    DispatcherTimer.RunOnce(window.Close, TimeSpan.FromMilliseconds(100));
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
