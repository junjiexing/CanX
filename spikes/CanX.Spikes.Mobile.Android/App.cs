using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace CanX.Spikes.Mobile.Android;

public sealed class App : Avalonia.Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime lifetime)
        {
            lifetime.MainViewFactory = static () =>
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#202020")),
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
                                Foreground = Brushes.White,
                                HorizontalAlignment = HorizontalAlignment.Center,
                            },
                            new TextBlock
                            {
                                Text = ".NET 10 NativeAOT + Avalonia Android",
                                Foreground = Brushes.White,
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
