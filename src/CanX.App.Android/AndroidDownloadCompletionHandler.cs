// SPDX-License-Identifier: GPL-3.0-or-later

using Android.App;
using Android.Content;
using Android.OS;
using Android.Webkit;
using AndroidX.Core.Content;
using CanX.Core.Downloads;
using System.Runtime.Versioning;

namespace CanX.App.Android;

internal sealed class AndroidDownloadCompletionHandler : IDownloadCompletionHandler
{
    private const string ChannelId = "canx-downloads";

    public ValueTask<(bool NotificationSent, bool ActionExecuted)> HandleAsync(
        DownloadTaskSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var notified = snapshot.NotifyOnCompletion && TryNotify(snapshot);
        var actionExecuted = snapshot.CompletionAction != DownloadCompletionAction.None &&
            TryExecuteAction(snapshot);
        return ValueTask.FromResult((notified, actionExecuted));
    }

    private static bool TryNotify(DownloadTaskSnapshot snapshot)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (manager is null) return false;
            var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? CreateModernBuilder(context, manager)
                : CreateLegacyBuilder(context);
            var notification = builder
                .SetContentTitle("下载完成")
                .SetContentText(snapshot.DisplayName)
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
                .SetContentIntent(CreateOpenAppPendingIntent(context, snapshot.Id.GetHashCode()))
                .SetAutoCancel(true)
                .Build();
            manager.Notify(snapshot.Id.GetHashCode(), notification);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("android26.0")]
    private static Notification.Builder CreateModernBuilder(
        Context context,
        NotificationManager manager)
    {
        manager.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            "CanX 下载",
            NotificationImportance.Default));
        return new Notification.Builder(context, ChannelId);
    }

    [UnsupportedOSPlatform("android26.0")]
    private static Notification.Builder CreateLegacyBuilder(Context context) => new(context);

    private static bool TryExecuteAction(DownloadTaskSnapshot snapshot)
    {
        try
        {
            var path = DownloadCompletionTargets.Resolve(snapshot);
            if (string.IsNullOrWhiteSpace(path)) return false;
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path)) return false;

            var context = global::Android.App.Application.Context;
            var uri = FileProvider.GetUriForFile(
                context,
                $"{context.PackageName}.files",
                new Java.IO.File(path));
            var mime = isDirectory
                ? "resource/folder"
                : MimeTypeMap.Singleton?.GetMimeTypeFromExtension(Path.GetExtension(path).TrimStart('.').ToLowerInvariant())
                    ?? "application/octet-stream";
            var intent = new Intent(Intent.ActionView)
                .SetDataAndType(uri, mime)
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            context.StartActivity(intent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PendingIntent? CreateOpenAppPendingIntent(Context context, int requestCode)
    {
        var intent = new Intent(context, typeof(MainActivity))
            // Show the existing main screen without destroying a player that
            // is still present in the same task.
            .SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
        return PendingIntent.GetActivity(
            context,
            requestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }
}
