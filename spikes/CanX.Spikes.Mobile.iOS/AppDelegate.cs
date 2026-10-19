using Avalonia;
using Avalonia.iOS;
using Foundation;
using UIKit;

namespace CanX.Spikes.Mobile.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711
public sealed partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
    public override void OnActivated(UIApplication application)
    {
        base.OnActivated(application);
        IosDisplaySleepInhibitor.Reapply();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder);
}
