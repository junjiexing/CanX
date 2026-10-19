// SPDX-License-Identifier: GPL-3.0-or-later

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using Avalonia.Controls;
using AndroidOrientation = Android.Content.Res.Orientation;

namespace CanX.App.Android;

[Activity(
    Label = "CanX Player",
    Theme = "@style/CanXPlaybackTheme",
    Exported = false,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.UiMode
        | ConfigChanges.Density
        | ConfigChanges.KeyboardHidden)]
public sealed class PlaybackActivity : Activity, IActivityResultHandler
{
    private const string ManualOrientationStateKey = "canx.playback.manual-orientation";
    private static int _launchPending;
    private AvaloniaView? _view;
    private Control? _content;
    private MobileAppHost? _host;
    private ScreenOrientation? _manualOrientation;
    private int _closeRequested;

    internal static PlaybackActivity? Current { get; private set; }

    public Action<int, Result, Intent?>? ActivityResult { get; set; }

    public Action<int, string[], Permission[]>? RequestPermissionsResult { get; set; }

    internal static void Show(Activity owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Current is not null || Interlocked.CompareExchange(ref _launchPending, 1, 0) != 0)
        {
            return;
        }
        try
        {
            owner.StartActivity(new Intent(owner, typeof(PlaybackActivity)));
        }
        catch
        {
            Interlocked.Exchange(ref _launchPending, 0);
            throw;
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::Android.Util.Log.Info("CanX", "PlaybackActivity.OnCreate begin");
        base.OnCreate(savedInstanceState);
        Interlocked.Exchange(ref _launchPending, 0);
        Current = this;
        try
        {
            RestoreManualOrientation(savedInstanceState);
            MainActivity.ConfigureSmoothPlaybackDisplayMode(this);

            _host = (Avalonia.Application.Current as App)?.Host
                ?? throw new InvalidOperationException("CanX mobile host is not initialized.");
            _view = new AvaloniaView(this);
            if (OperatingSystem.IsAndroidVersionAtLeast(36) &&
                MainActivity.RequestedDisplayRefreshRate is { } requestedRefreshRate)
            {
                _view.RequestedFrameRate = (float)requestedRefreshRate;
            }

            SetContentView(_view);
            ConfigureFullscreenWindow();
            // Let OnCreate return so Android can attach and focus the window before
            // the comparatively large player/options visual tree is constructed.
            _view.Post(AttachPlaybackContent);
            global::Android.Util.Log.Info("CanX", "PlaybackActivity window attached");
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("CanX", $"PlaybackActivity initialization failed: {exception}");
            ClosePlayback();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        CanXForegroundService.NotifyActivityResumed(this);
        ConfigureFullscreenWindow();
        if (_view is { } view)
        {
            view.Post(() => MainActivity.ConfigureSmoothPlaybackSurface(view, SynchronizeDisplayRefreshRate));
        }
    }

    protected override void OnPause()
    {
        CanXForegroundService.NotifyActivityPaused(this);
        base.OnPause();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            ConfigureFullscreenWindow();
        }
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ConfigureFullscreenWindow();
        if (_view is { } view)
        {
            view.Post(() => MainActivity.ConfigureSmoothPlaybackSurface(view, SynchronizeDisplayRefreshRate));
        }

        global::Android.Util.Log.Info(
            "CanX",
            $"Playback orientation changed: {newConfig.Orientation}; manual={_manualOrientation?.ToString() ?? "system"}.");
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        ReleasePlaybackContent();
        base.OnDestroy();
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        if (_manualOrientation is { } orientation)
        {
            outState.PutInt(ManualOrientationStateKey, (int)orientation);
        }

        base.OnSaveInstanceState(outState);
    }

#pragma warning disable CS0672 // Android still dispatches the classic callback on the supported API range.
    public override void OnBackPressed()
#pragma warning restore CS0672
    {
        ClosePlayback();
    }

    protected override void OnActivityResult(
        int requestCode,
        [global::Android.Runtime.GeneratedEnum] Result resultCode,
        Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        ActivityResult?.Invoke(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        RequestPermissionsResult?.Invoke(requestCode, permissions, grantResults);
    }

    private void ConfigureFullscreenWindow()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
#pragma warning disable CA1422 // No-op on Android 15+, still required for Android 11-14.
            Window?.SetDecorFitsSystemWindows(false);
#pragma warning restore CA1422
            // PhoneWindow.getInsetsController throws before its DecorView exists
            // on some Android 16 vendor builds. Access it through the attached
            // decor view and simply retry from OnResume/window-focus otherwise.
            var controller = Window?.DecorView?.WindowInsetsController;
            if (controller is not null)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            return;
        }

#pragma warning disable CS0618 // Required for immersive mode on Android 6-10.
        if (Window?.DecorView is { } decorView)
        {
            decorView.SystemUiVisibility = (StatusBarVisibility)(
                SystemUiFlags.LayoutStable |
                SystemUiFlags.LayoutHideNavigation |
                SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.HideNavigation |
                SystemUiFlags.Fullscreen |
                SystemUiFlags.ImmersiveSticky);
        }
#pragma warning restore CS0618
    }

    private void AttachPlaybackContent()
    {
        if (_view is null || _host is null || IsFinishing || Volatile.Read(ref _closeRequested) != 0)
        {
            return;
        }

        try
        {
            _content = _host.CreatePlaybackActivityView(ClosePlayback, TogglePlaybackOrientation);
            _view.Content = _content;
            _view.Post(() => MainActivity.ConfigureSmoothPlaybackSurface(_view, SynchronizeDisplayRefreshRate));
            global::Android.Util.Log.Info("CanX", "PlaybackActivity player attached");
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("CanX", $"Unable to attach playback content: {exception}");
            ClosePlayback();
        }
    }

    private void ClosePlayback()
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0) return;
        _ = StopAndClosePlaybackAsync();
    }

    private async Task StopAndClosePlaybackAsync()
    {
        try
        {
            if (_host is { } host)
            {
                await host.StopPlaybackAsync();
            }
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("CanX", $"Unable to stop playback while closing player: {exception}");
        }
        finally
        {
            ReleasePlaybackContent();
            if (!IsFinishing && !IsDestroyed) base.Finish();
        }
    }

    private void TogglePlaybackOrientation()
    {
        var isLandscape = Resources?.Configuration?.Orientation == AndroidOrientation.Landscape;
        _manualOrientation = isLandscape
            ? ScreenOrientation.SensorPortrait
            : ScreenOrientation.SensorLandscape;
        RequestedOrientation = _manualOrientation.Value;
        global::Android.Util.Log.Info("CanX", $"Playback orientation override: {_manualOrientation}.");
    }

    private void RestoreManualOrientation(Bundle? savedInstanceState)
    {
        if (savedInstanceState?.ContainsKey(ManualOrientationStateKey) != true)
        {
            return;
        }

        var orientation = (ScreenOrientation)savedInstanceState.GetInt(ManualOrientationStateKey);
        if (orientation is not (ScreenOrientation.SensorLandscape or ScreenOrientation.SensorPortrait))
        {
            return;
        }

        _manualOrientation = orientation;
        RequestedOrientation = orientation;
    }

    private void ReleasePlaybackContent()
    {
        var view = _view;
        var content = _content;
        var host = _host;
        _view = null;
        _content = null;
        _host = null;

        if (view is not null)
        {
            view.Content = null;
        }
        if (host is not null && content is not null)
        {
            host.ReleasePlaybackActivityView(content);
        }
    }

    private async void SynchronizeDisplayRefreshRate()
    {
        if (!ReferenceEquals(Current, this) || _host is null)
        {
            return;
        }

        try
        {
            await _host.SynchronizeDisplayRefreshRateAsync();
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("CanX", $"Unable to synchronize player refresh rate: {exception}");
        }
    }
}
