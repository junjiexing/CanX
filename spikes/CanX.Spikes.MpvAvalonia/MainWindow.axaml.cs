using Avalonia.Controls;
using Avalonia.Threading;

namespace CanX.Spikes.MpvAvalonia;

public sealed partial class MainWindow : Window
{
    private readonly bool _smokeMode;

    public MainWindow()
        : this(null, false)
    {
    }

    public MainWindow(string? mediaPath, bool smokeMode)
    {
        _smokeMode = smokeMode;
        InitializeComponent();

        VideoSurface.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => StatusText.Text = status);
        VideoSurface.SmokePassed += result =>
            Dispatcher.UIThread.Post(() => HandleSmokePassed(result));

        if (!string.IsNullOrWhiteSpace(mediaPath))
        {
            VideoSurface.MediaPath = Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? mediaPath
                    : Path.GetFullPath(mediaPath);
        }
    }

    private void HandleSmokePassed(string result)
    {
        Console.WriteLine(result);
        if (_smokeMode)
        {
            VideoHost.Children.Remove(VideoSurface);
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(1));
        }
    }
}
