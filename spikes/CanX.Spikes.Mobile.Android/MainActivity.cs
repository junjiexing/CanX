using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Android;

namespace CanX.Spikes.Mobile.Android;

[Activity(
    Label = "CanX Stage 0",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private AvaloniaView? _view;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // AvaloniaMainActivity creates AvaloniaView before calling Android's
        // Activity.OnCreate. That ordering deadlocks during window creation on
        // Android 16, so initialize the Android activity first.
        base.OnCreate(savedInstanceState);

        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as IActivityApplicationLifetime;
        var content = lifetime?.MainViewFactory?.Invoke()
            ?? throw new InvalidOperationException("Avalonia application content was not initialized.");

        _view = new AvaloniaView(this);
        SetContentView(_view);
        _view.Content = content;
    }

    protected override void OnDestroy()
    {
        if (_view is not null)
        {
            _view.Content = null;
            _view = null;
        }

        base.OnDestroy();
    }
}
