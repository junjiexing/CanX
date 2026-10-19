// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Diagnostics;
using CanX.Core.Playback;

namespace CanX.Application.Playback;

/// <summary>Owns a display-sleep request for one playing video, independently of its window.</summary>
public sealed class PlaybackSleepInhibition(
    Func<CancellationToken, Task<IDisposable>> acquire,
    IApplicationLog? applicationLog = null) : IDisposable
{
    private readonly object _sync = new();
    private readonly IApplicationLog _log = applicationLog ?? NullApplicationLog.Instance;
    private IDisposable? _lease;
    private CancellationTokenSource? _acquisition;
    private Task _pending = Task.CompletedTask;
    private DateTimeOffset _retryAfter;
    private bool _required;
    private bool _acquisitionWasStopped;
    private bool _disposed;

    public static bool IsRequired(PlaybackSnapshot snapshot) =>
        snapshot.State == PlaybackState.Playing &&
        !string.IsNullOrWhiteSpace(snapshot.Source) &&
        // A renderer can temporarily deselect video while its background surface is
        // being rebuilt. The media's tracks, not window visibility, define video playback.
        snapshot.Tracks.Any(static track => track.Type == PlaybackTrackType.Video && !track.IsAlbumArt);

    public Task UpdateAsync(PlaybackSnapshot snapshot)
    {
        IDisposable? release = null;
        Task pending;
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            _required = IsRequired(snapshot);
            if (!_required)
            {
                _acquisitionWasStopped = true;
                _acquisition?.Cancel();
                release = _lease;
                _lease = null;
                _retryAfter = default;
            }
            StartAcquisition();
            pending = _pending;
        }
        Release(release);
        return pending;
    }

    // Called with _sync held. Native work never runs on the playback/event thread.
    private void StartAcquisition()
    {
        if (_disposed || !_required || _lease is not null || _acquisition is not null ||
            DateTimeOffset.UtcNow < _retryAfter) return;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _acquisitionWasStopped = false;
        _acquisition = cancellation;
        _pending = Task.Run(() => AcquireAsync(cancellation));
    }

    private async Task AcquireAsync(CancellationTokenSource cancellation)
    {
        IDisposable? acquired = null;
        try
        {
            acquired = await acquire(cancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (!_disposed && _required && !cancellation.IsCancellationRequested)
                {
                    _lease = acquired;
                    acquired = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Cancellation during pause/close is expected. A timed-out backend is retried later.
            lock (_sync)
            {
                if (_required && !_acquisitionWasStopped) _retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }
        catch (Exception exception)
        {
            lock (_sync) _retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            _log.Write(LogComponent.App, ApplicationLogLevel.Warning,
                "Unable to prevent display sleep during video playback; will retry.", exception);
        }
        finally
        {
            Release(acquired);
            lock (_sync)
            {
                _acquisition = null;
                cancellation.Dispose();
                StartAcquisition();
            }
        }
    }

    public void Dispose()
    {
        IDisposable? release;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _required = false;
            _acquisition?.Cancel();
            release = _lease;
            _lease = null;
        }
        Release(release);
    }

    private void Release(IDisposable? lease)
    {
        try { lease?.Dispose(); }
        catch (Exception exception)
        {
            _log.Write(LogComponent.App, ApplicationLogLevel.Warning,
                "Unable to release a playback display-sleep request.", exception);
        }
    }
}
