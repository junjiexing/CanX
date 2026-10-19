// SPDX-License-Identifier: GPL-3.0-or-later

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Android;
using Android.Views;
using System.Runtime.Versioning;
using AndroidEnvironment = Android.OS.Environment;

namespace CanX.App.Android;

[Activity(
    Label = "CanX",
    Theme = "@style/CanXMainTheme",
    MainLauncher = true,
    // SingleTask clears every Activity above the task root when the launcher
    // icon is used again. The player intentionally lives above MainActivity,
    // so keep normal task restoration semantics and only suppress duplicates
    // when MainActivity is already at the top.
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Unspecified,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.UiMode
        | ConfigChanges.Density
        | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity, IActivityResultHandler
{
    private const int NotificationPermissionRequestCode = 1001;
    private const int StoragePermissionRequestCode = 1002;
    private const int ManageStorageRequestCode = 1003;
    private AvaloniaView? _view;
    private TaskCompletionSource<bool>? _storageAccessRequest;

    internal static MainActivity? Current { get; private set; }

    internal static double? RequestedDisplayRefreshRate { get; private set; }

    public Action<int, Result, Intent?>? ActivityResult { get; set; }

    public Action<int, string[], Permission[]>? RequestPermissionsResult { get; set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::Android.Util.Log.Info("CanX", "MainActivity.OnCreate begin");
        base.OnCreate(savedInstanceState);
        Current = this;
        global::Android.Util.Log.Info("CanX", "MainActivity base.OnCreate complete");
        ConfigureSmoothPlaybackDisplayMode(this);
        AndroidNativeBootstrap.Initialize();
        global::Android.Util.Log.Info("CanX", "FFmpeg Java VM registered");

        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as IActivityApplicationLifetime;
        var content = lifetime?.MainViewFactory?.Invoke()
            ?? throw new InvalidOperationException("CanX mobile content was not initialized.");
        global::Android.Util.Log.Info("CanX", "Mobile shell created");

        _view = new AvaloniaView(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(36) &&
            RequestedDisplayRefreshRate is { } requestedRefreshRate)
        {
            // Android 15+ may deliberately ignore the legacy window preferred
            // mode vote. The per-view request is the replacement consumed by
            // SurfaceFlinger for the Avalonia composition surface.
            _view.RequestedFrameRate = (float)requestedRefreshRate;
        }
        SetContentView(_view);
        _view.Content = content;
        _view.Post(() => ConfigureSmoothPlaybackSurface(_view, SynchronizeActualDisplayRefreshRate));
        RequestNotificationPermissionIfNeeded();
        global::Android.Util.Log.Info("CanX", "Mobile shell attached");
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        CanXForegroundService.NotifyActivityResumed(this);
        if (_view is { } view)
        {
            view.Post(() => ConfigureSmoothPlaybackSurface(view, SynchronizeActualDisplayRefreshRate));
        }
        if (Avalonia.Application.Current is App { Host: { } host })
        {
            // The Activity may be recreated while the process-scoped aria2 engine
            // keeps running. Reconcile the observable list from the engine-backed
            // manager whenever the UI returns to the foreground.
            host.Shell.DownloadCenter.SynchronizeTaskList();
        }
    }

    protected override void OnPause()
    {
        CanXForegroundService.NotifyActivityPaused(this);
        base.OnPause();
    }

#pragma warning disable CS0672 // Android still dispatches the classic callback on the supported API range.
    public override void OnBackPressed()
#pragma warning restore CS0672
    {
        if (_view?.Content is MobileShellView { } shell && shell.HandleBackPressed()) return;
#pragma warning disable CA1422 // Preserve the system's default back behavior on the root page.
        base.OnBackPressed();
#pragma warning restore CA1422
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        _storageAccessRequest?.TrySetResult(false);
        _storageAccessRequest = null;
        if (_view is not null)
        {
            _view.Content = null;
            _view = null;
        }

        base.OnDestroy();
    }

    protected override void OnActivityResult(
        int requestCode,
        [global::Android.Runtime.GeneratedEnum] Result resultCode,
        Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == ManageStorageRequestCode)
        {
            CompleteStorageAccessRequest(HasExternalStorageAccess());
            return;
        }
        ActivityResult?.Invoke(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == StoragePermissionRequestCode)
        {
            CompleteStorageAccessRequest(grantResults.Length > 0 &&
                grantResults.All(static result => result == Permission.Granted));
            return;
        }
        RequestPermissionsResult?.Invoke(requestCode, permissions, grantResults);
    }

    internal Task<bool> EnsureExternalStorageAccessAsync()
    {
        if (HasExternalStorageAccess()) return Task.FromResult(true);
        if (_storageAccessRequest is not null) return _storageAccessRequest.Task;

        _storageAccessRequest = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var packageUri = global::Android.Net.Uri.Parse($"package:{PackageName}");
                var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, packageUri);
                try
                {
                    StartActivityForResult(intent, ManageStorageRequestCode);
                }
                catch (ActivityNotFoundException)
                {
                    StartActivityForResult(
                        new Intent(Settings.ActionManageAllFilesAccessPermission),
                        ManageStorageRequestCode);
                }
            }
            else
            {
                RequestPermissions(
                    [
                        global::Android.Manifest.Permission.ReadExternalStorage,
                        global::Android.Manifest.Permission.WriteExternalStorage,
                    ],
                    StoragePermissionRequestCode);
            }
        }
        catch
        {
            _storageAccessRequest = null;
            throw;
        }

        return _storageAccessRequest.Task;
    }

    private bool HasExternalStorageAccess()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return AndroidEnvironment.IsExternalStorageManager;
        }

        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return true;
        return CheckSelfPermission(global::Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted &&
            CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;
    }

    private void CompleteStorageAccessRequest(bool granted)
    {
        var request = _storageAccessRequest;
        _storageAccessRequest = null;
        request?.TrySetResult(granted);
    }

    private void RequestNotificationPermissionIfNeeded()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions(
                [global::Android.Manifest.Permission.PostNotifications],
                NotificationPermissionRequestCode);
        }
    }

    internal static void ConfigureSmoothPlaybackDisplayMode(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        try
        {
#pragma warning disable CS0618 // DefaultDisplay is required for the API 23-compatible mode API.
            var display = activity.WindowManager?.DefaultDisplay;
#pragma warning restore CS0618
            var currentMode = display?.GetMode();
            var modes = display?.GetSupportedModes();
            if (currentMode is null || modes is null || modes.Length == 0)
            {
                return;
            }

            // Prefer the fastest mode at the panel's native resolution. In
            // particular, 120 Hz gives 23.976/24 fps cinema content an even
            // five-refresh cadence and removes the visible 3:2 cadence of a
            // 60 Hz Android window.
            var preferredMode = modes
                .Where(mode => mode.PhysicalWidth == currentMode.PhysicalWidth &&
                               mode.PhysicalHeight == currentMode.PhysicalHeight)
                .OrderByDescending(static mode => mode.RefreshRate)
                .FirstOrDefault();
            if (preferredMode is null || preferredMode.RefreshRate < 20)
            {
                return;
            }

            var attributes = activity.Window?.Attributes;
            if (attributes is null)
            {
                return;
            }

            attributes.PreferredDisplayModeId = preferredMode.ModeId;
            activity.Window!.Attributes = attributes;
            RequestedDisplayRefreshRate = preferredMode.RefreshRate;
            global::Android.Util.Log.Info(
                "CanX",
                $"Requested display mode {preferredMode.ModeId} at {preferredMode.RefreshRate:0.###} Hz");
        }
        catch (Exception exception)
        {
            RequestedDisplayRefreshRate = null;
            global::Android.Util.Log.Warn("CanX", $"Unable to request high-refresh display mode: {exception}");
        }
    }

    internal static void ConfigureSmoothPlaybackSurface(AvaloniaView view, Action synchronizeRefreshRate)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(synchronizeRefreshRate);
        if (RequestedDisplayRefreshRate is not { } refreshRate)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(36))
        {
            view.PropagateRequestedFrameRate((float)refreshRate, true);
            ApplySurfaceFrameRate(view, (float)refreshRate);
        }

        view.PostDelayed(synchronizeRefreshRate, 500);
        view.PostDelayed(synchronizeRefreshRate, 2000);
    }

    [SupportedOSPlatform("android36.0")]
    private static void ApplySurfaceFrameRate(View view, float refreshRate)
    {
        view.RequestedFrameRate = refreshRate;
        if (view is SurfaceView { Holder.Surface: { IsValid: true } surface })
        {
            surface.SetFrameRate(
                refreshRate,
                (int)SurfaceFrameRateCompatibility.Default,
                (int)SurfaceChangeFrameRate.OnlyIfSeamless);
        }

        if (view is not ViewGroup group)
        {
            return;
        }

        for (var index = 0; index < group.ChildCount; index++)
        {
            if (group.GetChildAt(index) is { } child)
            {
                ApplySurfaceFrameRate(child, refreshRate);
            }
        }
    }

    private async void SynchronizeActualDisplayRefreshRate()
    {
        if (!ReferenceEquals(Current, this) ||
            Avalonia.Application.Current is not App { Host: { } host })
        {
            return;
        }

        try
        {
            await host.SynchronizeDisplayRefreshRateAsync();
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("CanX", $"Unable to synchronize display refresh rate: {exception}");
        }
    }
}
