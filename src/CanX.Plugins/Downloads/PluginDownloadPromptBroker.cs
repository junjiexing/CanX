// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Downloads;

public sealed record PluginDownloadPromptRequest(
    string PluginId,
    string Kind,
    string Source);

public sealed class PluginDownloadPromptException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class PluginDownloadPromptBroker
{
    private readonly Lock gate = new();
    private Func<PluginDownloadPromptRequest, CancellationToken, ValueTask>? handler;

    public IDisposable RegisterHandler(
        Func<PluginDownloadPromptRequest, CancellationToken, ValueTask> promptHandler)
    {
        ArgumentNullException.ThrowIfNull(promptHandler);
        lock (gate)
        {
            if (handler is not null)
            {
                throw new InvalidOperationException("A plugin download prompt handler is already registered.");
            }
            handler = promptHandler;
        }
        return new HandlerLease(this, promptHandler);
    }

    internal async ValueTask RequestAsync(
        PluginDownloadPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        Func<PluginDownloadPromptRequest, CancellationToken, ValueTask>? current;
        lock (gate)
        {
            current = handler;
        }
        if (current is null)
        {
            throw new PluginDownloadPromptException(
                "prompt_unavailable",
                "The application cannot show a download confirmation right now.");
        }
        await current(request, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(PluginDownloadPromptRequest request)
    {
        if (request.Source.Length is < 1 or > 8192)
        {
            throw new PluginDownloadPromptException(
                "invalid_download_request",
                "Download source must contain 1 to 8192 characters.");
        }
        if (request.Kind == "magnet")
        {
            if (!request.Source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginDownloadPromptException(
                    "invalid_download_request",
                    "Magnet download source must use the magnet scheme.");
            }
            return;
        }
        if (request.Kind == "http" &&
            Uri.TryCreate(request.Source, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            return;
        }
        throw new PluginDownloadPromptException(
            "invalid_download_request",
            "Download kind must be magnet or http and match the source URI.");
    }

    private void Unregister(Func<PluginDownloadPromptRequest, CancellationToken, ValueTask> registeredHandler)
    {
        lock (gate)
        {
            if (ReferenceEquals(handler, registeredHandler))
            {
                handler = null;
            }
        }
    }

    private sealed class HandlerLease(
        PluginDownloadPromptBroker owner,
        Func<PluginDownloadPromptRequest, CancellationToken, ValueTask> registeredHandler) : IDisposable
    {
        private PluginDownloadPromptBroker? currentOwner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref currentOwner, null)?.Unregister(registeredHandler);
    }
}
