// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace CanX.App.Android;

public sealed partial class App : Avalonia.Application
{
    private MobileAppHost? _host;
    private MobileShellView? _mainView;

    internal MobileAppHost? Host => _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime lifetime)
        {
            lifetime.MainViewFactory = () =>
            {
                global::Android.Util.Log.Info("CanX", "Creating mobile composition root");
                _host ??= MobileAppHost.Create();
                global::Android.Util.Log.Info("CanX", "Mobile composition root ready");
                // Android can destroy and recreate the Activity while the Application and
                // native engines remain alive. Reusing the detached Avalonia control keeps
                // initialization and view-model subscriptions process-scoped as well.
                return _mainView ??= new MobileShellView(_host);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
