using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace CanX.Spikes.Mobile.Android;

[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder);
}
