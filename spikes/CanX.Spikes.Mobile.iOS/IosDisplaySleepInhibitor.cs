// SPDX-License-Identifier: GPL-3.0-or-later

using CoreFoundation;
using UIKit;

namespace CanX.Spikes.Mobile.iOS;

/// <summary>Native adapter for the future iOS playback host; all UIKit access stays on the main thread.</summary>
internal static class IosDisplaySleepInhibitor
{
    private static int _leaseCount;
    private static bool _previousIdleTimerDisabled;

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (completion.Task.IsCompleted) return;
            try
            {
                var application = UIApplication.SharedApplication;
                if (_leaseCount == 0) _previousIdleTimerDisabled = application.IdleTimerDisabled;
                application.IdleTimerDisabled = true;
                _leaseCount++;
                var lease = new Lease();
                if (!completion.TrySetResult(lease)) lease.Dispose();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return await completion.Task.ConfigureAwait(false);
    }

    internal static void Reapply()
    {
        if (_leaseCount > 0) UIApplication.SharedApplication.IdleTimerDisabled = true;
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (--_leaseCount == 0)
                    UIApplication.SharedApplication.IdleTimerDisabled = _previousIdleTimerDisabled;
            });
        }
    }
}
