using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CanX.Spikes.MpvAvalonia;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var arguments = desktop.Args ?? [];
            var smokeIndex = Array.IndexOf(arguments, "--smoke");
            var smokeMedia = smokeIndex >= 0 && smokeIndex + 1 < arguments.Length
                ? arguments[smokeIndex + 1]
                : null;

            desktop.MainWindow = new MainWindow(smokeMedia, smokeIndex >= 0);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
