// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Media;

public sealed record PluginMediaPlaybackRequest(string PluginId, string Source);

public sealed class PluginMediaStreamException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class PluginMediaPlaybackBroker
{
    private readonly Lock _gate = new();
    private Func<PluginMediaPlaybackRequest, CancellationToken, ValueTask>? _handler;

    public IDisposable RegisterHandler(
        Func<PluginMediaPlaybackRequest, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_handler is not null)
            {
                throw new InvalidOperationException("A plugin media playback handler is already registered.");
            }
            _handler = handler;
        }
        return new HandlerLease(this, handler);
    }

    internal async ValueTask RequestAsync(
        PluginMediaPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        Func<PluginMediaPlaybackRequest, CancellationToken, ValueTask>? handler;
        lock (_gate)
        {
            handler = _handler;
        }
        if (handler is null)
        {
            throw new PluginMediaStreamException(
                "playback_unavailable",
                "The application cannot open plugin media right now.");
        }
        await handler(request, cancellationToken).ConfigureAwait(false);
    }

    private void Unregister(Func<PluginMediaPlaybackRequest, CancellationToken, ValueTask> handler)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_handler, handler))
            {
                _handler = null;
            }
        }
    }

    private sealed class HandlerLease(
        PluginMediaPlaybackBroker owner,
        Func<PluginMediaPlaybackRequest, CancellationToken, ValueTask> handler) : IDisposable
    {
        private PluginMediaPlaybackBroker? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unregister(handler);
    }
}
