using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace CanX.Spikes.Mobile.iOS;

public sealed class App : Avalonia.Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime lifetime)
        {
            lifetime.MainView = new Border
            {
                Padding = new Thickness(32),
                Child = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "CanX",
                            FontSize = 32,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = ".NET 10 NativeAOT + Avalonia iOS",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
