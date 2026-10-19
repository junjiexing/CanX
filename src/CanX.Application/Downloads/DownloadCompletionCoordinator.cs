// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Channels;
using CanX.Core.Downloads;
using CanX.Core.Events;

namespace CanX.Application.Downloads;

public sealed class DownloadCompletionCoordinator : IAsyncDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IDownloadCompletionHandler _handler;
    private readonly Channel<DownloadTaskSnapshot> _queue = Channel.CreateUnbounded<DownloadTaskSnapshot>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly HashSet<Guid> _handled = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IDisposable _subscription;
    private readonly Task _worker;

    public DownloadCompletionCoordinator(IEventBus eventBus, IDownloadCompletionHandler handler)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _subscription = eventBus.Subscribe<DownloadTaskChanged>(OnTaskChanged);
        _worker = Task.Run(() => RunAsync(_shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        _queue.Writer.TryComplete();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _shutdown.Cancel();
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private void OnTaskChanged(DownloadTaskChanged message)
    {
        var snapshot = message.Snapshot;
        lock (_gate)
        {
            if (snapshot.State != DownloadState.Complete)
            {
                _handled.Remove(snapshot.Id);
                return;
            }

            if (!snapshot.NotifyOnCompletion && snapshot.CompletionAction == DownloadCompletionAction.None)
            {
                return;
            }

            if (!_handled.Add(snapshot.Id))
            {
                return;
            }
        }

        _queue.Writer.TryWrite(snapshot);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var result = await _handler.HandleAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    _eventBus.Publish(new DownloadCompletionHandled(
                        snapshot.Id,
                        result.NotificationSent,
                        result.ActionExecuted,
                        null));
                }
                catch (Exception exception)
                {
                    _eventBus.Publish(new DownloadCompletionHandled(snapshot.Id, false, false, exception.Message));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
