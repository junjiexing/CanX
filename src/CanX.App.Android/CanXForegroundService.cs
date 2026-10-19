// SPDX-License-Identifier: GPL-3.0-or-later

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Avalonia.Threading;
using CanX.Core.Playback;
using AndroidPlaybackState = Android.Media.Session.PlaybackState;
using CanXPlaybackState = CanX.Core.Playback.PlaybackState;
using Android.OS;

namespace CanX.App.Android;

[Service(
    Name = "org.canx.app.CanXForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync | ForegroundService.TypeMediaPlayback)]
public sealed class CanXForegroundService : Service
{
    private const long ActivityTransitionDelayMilliseconds = 500;
    private const long StateRefreshIntervalMilliseconds = 2_000;
    private const int NotificationId = 0x43414e58;
    private const string ChannelId = "canx-background";
    private const string StopAction = "org.canx.app.action.STOP_BACKGROUND";
    private const string TogglePlaybackAction = "org.canx.app.action.TOGGLE_PLAYBACK";
    private static readonly Handler ActivityHandler = new(Looper.MainLooper!);
    private static readonly HashSet<Activity> ResumedActivities = [];
    private static Action? _pendingBackgroundStart;
    private Handler? _mainHandler;
    private Action? _stateRefresh;
    private MediaSession? _mediaSession;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateChannel();

        // Android starts the five-second foreground-service deadline before
        // this callback. Publish a minimal notification first: MediaSession
        // setup can call back into the managed playback host and must not be
        // allowed to delay StartForeground().
        StartForeground(NotificationId, BuildNotification());
        _mediaSession = new MediaSession(this, "CanX");
        _mediaSession.SetCallback(new CanXMediaSessionCallback());
        _mediaSession.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        _mediaSession.Active = true;
        UpdateMediaSession();
        StartForeground(NotificationId, BuildNotification());
        _mainHandler = new Handler(Looper.MainLooper!);
        _stateRefresh = RefreshState;
        _mainHandler.PostDelayed(_stateRefresh, StateRefreshIntervalMilliseconds);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (string.Equals(intent?.Action, StopAction, StringComparison.Ordinal))
        {
            StopBackgroundExecution();
            return StartCommandResult.NotSticky;
        }
        if (string.Equals(intent?.Action, TogglePlaybackAction, StringComparison.Ordinal))
        {
            DispatchPlayback(static host => host.Shell.Playback.TogglePauseAsync());
        }
        UpdateMediaSession();
        StartForeground(NotificationId, BuildNotification());
        return StartCommandResult.NotSticky;
    }

    public override global::Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        if (_mainHandler is not null && _stateRefresh is not null)
        {
            _mainHandler.RemoveCallbacks(_stateRefresh);
        }
        _stateRefresh = null;
        _mainHandler?.Dispose();
        _mainHandler = null;
        if (_mediaSession is not null)
        {
            _mediaSession.Active = false;
            _mediaSession.Release();
            _mediaSession.Dispose();
            _mediaSession = null;
        }
        base.OnDestroy();
    }

    internal static void Start(Context context)
    {
        var intent = new Intent(context, typeof(CanXForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    internal static void Stop(Context context) =>
        context.StopService(new Intent(context, typeof(CanXForegroundService)));

    internal static void NotifyActivityResumed(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ResumedActivities.Add(activity);
        CancelPendingBackgroundStart();
        Stop(activity);
    }

    internal static void NotifyActivityPaused(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ResumedActivities.Remove(activity);
        CancelPendingBackgroundStart();

        Action? pending = null;
        pending = () =>
        {
            if (!ReferenceEquals(_pendingBackgroundStart, pending))
            {
                return;
            }

            _pendingBackgroundStart = null;
            if (ResumedActivities.Count != 0 ||
                Avalonia.Application.Current is not App { Host.RequiresBackgroundExecution: true })
            {
                return;
            }

            try
            {
                Start(activity.ApplicationContext ?? activity);
            }
            catch (Exception exception)
            {
                global::Android.Util.Log.Error("CanX", $"Unable to start background service: {exception}");
            }
        };
        _pendingBackgroundStart = pending;
        ActivityHandler.PostDelayed(pending, ActivityTransitionDelayMilliseconds);
    }

    private static void CancelPendingBackgroundStart()
    {
        if (_pendingBackgroundStart is not { } pending)
        {
            return;
        }

        ActivityHandler.RemoveCallbacks(pending);
        _pendingBackgroundStart = null;
    }

    private void RefreshState()
    {
        if (Avalonia.Application.Current is not App { Host.RequiresBackgroundExecution: true })
        {
            StopForegroundCompat();
            StopSelf();
            return;
        }
        UpdateMediaSession();
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification());
        if (_mainHandler is not null && _stateRefresh is not null)
        {
            _mainHandler.PostDelayed(_stateRefresh, StateRefreshIntervalMilliseconds);
        }
    }

    private Notification BuildNotification()
    {
        var playbackIsActive = PlaybackActivity.Current is { IsFinishing: false, IsDestroyed: false } ||
            Avalonia.Application.Current is App { Host: { } host } &&
            host.Shell.Playback.HasMedia &&
            host.Shell.Playback.State is CanXPlaybackState.Loading or
                CanXPlaybackState.Playing or CanXPlaybackState.Paused;
        var openIntent = new Intent(
                this,
                playbackIsActive ? typeof(PlaybackActivity) : typeof(MainActivity))
            // Reorder the existing Activity instead of clearing the task above
            // MainActivity. ClearTop used to destroy PlaybackActivity while a
            // background playback session was being resumed.
            .SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
        var openPending = PendingIntent.GetActivity(
            this,
            0,
            openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Unable to create the CanX activity intent.");
        var stopIntent = new Intent(this, typeof(CanXForegroundService)).SetAction(StopAction);
        var stopPending = PendingIntent.GetService(
            this,
            1,
            stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Unable to create the CanX service intent.");
        var toggleIntent = new Intent(this, typeof(CanXForegroundService)).SetAction(TogglePlaybackAction);
        var togglePending = PendingIntent.GetService(
            this,
            2,
            toggleIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Unable to create the CanX playback intent.");
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload);
        builder.SetContentTitle("CanX 正在后台运行");
        builder.SetContentText("下载或播放任务仍在继续");
        builder.SetContentIntent(openPending);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetCategory(Notification.CategoryService);
        if (_mediaSession is not null)
        {
            builder.SetStyle(new Notification.MediaStyle().SetMediaSession(_mediaSession.SessionToken));
            builder.AddAction(new Notification.Action.Builder(
                Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMediaPause),
                "播放 / 暂停",
                togglePending).Build());
        }
        builder.AddAction(new Notification.Action.Builder(
            Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMenuCloseClearCancel),
            "停止后台运行",
            stopPending).Build());
        return builder.Build() ?? throw new InvalidOperationException("Unable to build the CanX background notification.");
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            "CanX 后台任务",
            NotificationImportance.Low)
        {
            Description = "下载和媒体播放在后台继续运行时显示",
        });
    }

    private void StopForegroundCompat()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        else
        {
#pragma warning disable CS0618
            StopForeground(removeNotification: true);
#pragma warning restore CS0618
        }
    }

    private void StopBackgroundExecution()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (Avalonia.Application.Current is App { Host: { } host })
                {
                    await host.StopBackgroundExecutionAsync();
                }
            }
            catch (Exception exception)
            {
                global::Android.Util.Log.Error("CanX", exception.ToString());
            }
            finally
            {
                StopForegroundCompat();
                StopSelf();
            }
        });
    }

    private void UpdateMediaSession()
    {
        if (_mediaSession is null || Avalonia.Application.Current is not App { Host: { } host }) return;
        var playback = host.Shell.Playback;
        var state = playback.State switch
        {
            CanXPlaybackState.Playing => PlaybackStateCode.Playing,
            CanXPlaybackState.Paused => PlaybackStateCode.Paused,
            CanXPlaybackState.Loading => PlaybackStateCode.Buffering,
            CanXPlaybackState.Ended => PlaybackStateCode.Stopped,
            CanXPlaybackState.Error => PlaybackStateCode.Error,
            _ => PlaybackStateCode.None,
        };
        const long actions = 1L | 2L | 4L | 16L | 32L | 256L | 512L;
        var playbackStateBuilder = new AndroidPlaybackState.Builder();
        playbackStateBuilder.SetActions(actions);
        playbackStateBuilder.SetState(state, (long)(playback.PositionSeconds * 1000), (float)playback.Speed);
        var playbackState = playbackStateBuilder.Build();
        _mediaSession.SetPlaybackState(playbackState);
        var metadataBuilder = new MediaMetadata.Builder();
        metadataBuilder.PutString(MediaMetadata.MetadataKeyTitle, playback.Title);
        metadataBuilder.PutLong(MediaMetadata.MetadataKeyDuration, (long)(playback.DurationSeconds * 1000));
        var metadata = metadataBuilder.Build();
        _mediaSession.SetMetadata(metadata);
    }

    private static void DispatchPlayback(Func<MobileAppHost, Task> action)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (Avalonia.Application.Current is App { Host: { } host })
            {
                try
                {
                    await action(host);
                }
                catch (Exception exception)
                {
                    global::Android.Util.Log.Error("CanX", exception.ToString());
                }
            }
        });
    }

    private sealed class CanXMediaSessionCallback : MediaSession.Callback
    {
        public override void OnPlay() => DispatchPlayback(static host =>
            host.Shell.Playback.State == CanXPlaybackState.Playing
                ? Task.CompletedTask
                : host.Shell.Playback.TogglePauseAsync());

        public override void OnPause() => DispatchPlayback(static host =>
            host.Shell.Playback.State == CanXPlaybackState.Paused
                ? Task.CompletedTask
                : host.Shell.Playback.TogglePauseAsync());

        public override void OnStop() => DispatchPlayback(static host => host.StopPlaybackAsync());

        public override void OnSkipToNext() => DispatchPlayback(static host => host.Shell.Playback.PlayNextAsync());

        public override void OnSkipToPrevious() => DispatchPlayback(static host => host.Shell.Playback.PlayPreviousAsync());

        public override void OnSeekTo(long position) => DispatchPlayback(host =>
            host.Shell.Playback.SeekAsync(position / 1000d));
    }
}
