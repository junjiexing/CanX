// SPDX-License-Identifier: GPL-3.0-or-later

using Android.Content;
using Android.OS;

namespace CanX.App.Android;

internal static class AndroidDisplaySleepInhibitor
{
    public static Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = global::Android.App.Application.Context;
        var power = (PowerManager?)context.GetSystemService(Context.PowerService)
            ?? throw new InvalidOperationException("Android power manager is unavailable.");
        // KEEP_SCREEN_ON is ignored in the background. This application-scoped
        // screen lock deliberately survives Activity transitions during playback.
        // Do not use ACQUIRE_CAUSES_WAKEUP: manual power-button sleep must still work.
#pragma warning disable CS0618 // The window flag replacement cannot keep background video awake.
        var wakeLock = power.NewWakeLock(WakeLockFlags.ScreenBright, "CanX:VideoPlayback")
            ?? throw new InvalidOperationException("Unable to create a playback wake lock.");
#pragma warning restore CS0618
        try
        {
            wakeLock.SetReferenceCounted(false);
            wakeLock.Acquire();
            return Task.FromResult<IDisposable>(new Lease(wakeLock));
        }
        catch
        {
            wakeLock.Dispose();
            throw;
        }
    }

    private sealed class Lease(PowerManager.WakeLock wakeLock) : IDisposable
    {
        private PowerManager.WakeLock? _wakeLock = wakeLock;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _wakeLock, null);
            if (current is null) return;
            try { if (current.IsHeld) current.Release(); }
            finally { current.Dispose(); }
        }
    }
}
