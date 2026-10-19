// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CanX.Streaming;

public sealed class MediaStreamBroker : IDisposable
{
    public const string Scheme = "canx";
    private const string Host = "stream";
    private const int TokenSize = 32;

    private readonly ConcurrentDictionary<string, RegistrationState> _registrations =
        new(StringComparer.Ordinal);
    private int _disposed;
    private readonly Lock _httpGate = new();
    private readonly string _httpPath = "/canx-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenSize)) + "/";
    private MediaStreamHttpGateway? _httpGateway;
    private int _httpRegistrations;

    public MediaStreamRegistration Register(
        string displayName,
        Func<IMediaByteStream> streamFactory,
        MediaStreamCachePolicy cachePolicy = MediaStreamCachePolicy.Default,
        MediaStreamTransport transport = MediaStreamTransport.Callback)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        return RegisterCore(displayName, streamFactory, null, null, cachePolicy, transport);
    }

    public MediaStreamRegistration RegisterPair(
        string displayName,
        Func<IMediaByteStream> streamFactory,
        string companionName,
        Func<IMediaByteStream> companionFactory,
        MediaStreamCachePolicy cachePolicy = MediaStreamCachePolicy.Default)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(companionFactory);
        return RegisterCore(displayName, streamFactory, companionName, companionFactory, cachePolicy, MediaStreamTransport.Callback);
    }

    public MediaStreamRegistration Register(
        string displayName,
        Func<CancellationToken, IMediaByteStream> streamFactory,
        MediaStreamCachePolicy cachePolicy = MediaStreamCachePolicy.Default,
        MediaStreamTransport transport = MediaStreamTransport.Callback)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        return RegisterCore(displayName, () => streamFactory(CancellationToken.None), null, null,
            cachePolicy, transport, streamFactory);
    }

    private MediaStreamRegistration RegisterCore(
        string displayName,
        Func<IMediaByteStream> streamFactory,
        string? companionName,
        Func<IMediaByteStream>? companionFactory,
        MediaStreamCachePolicy cachePolicy,
        MediaStreamTransport transport,
        Func<CancellationToken, IMediaByteStream>? cancellableFactory = null)
    {
        if (!Enum.IsDefined(cachePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(cachePolicy));
        }
        if (!Enum.IsDefined(transport)) throw new ArgumentOutOfRangeException(nameof(transport));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var safeDisplayName = NormalizeDisplayName(displayName);
        var safeCompanionName = companionName is null ? null : NormalizeDisplayName(companionName);
        if (safeCompanionName is not null &&
            safeDisplayName.Equals(safeCompanionName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Companion and primary stream names must differ.", nameof(companionName));
        }
        while (true)
        {
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenSize));
            var state = new RegistrationState(safeDisplayName, streamFactory, safeCompanionName, companionFactory, transport, cancellableFactory);
            if (transport == MediaStreamTransport.LoopbackHttp)
            {
                lock (_httpGate)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                    _httpGateway ??= new MediaStreamHttpGateway(_httpPath, TryOpenHttp);
                    _httpRegistrations++;
                }
            }
            if (!_registrations.TryAdd(token, state))
            {
                ReleaseHttp(state);
                continue;
            }

            if (Volatile.Read(ref _disposed) == 1)
            {
                Unregister(token, state);
                throw new ObjectDisposedException(nameof(MediaStreamBroker));
            }

            var cacheQuery = cachePolicy == MediaStreamCachePolicy.Disabled ? "?cache=no" : string.Empty;
            var source = $"{Scheme}://{Host}/{token}/{Uri.EscapeDataString(safeDisplayName)}{cacheQuery}";
            return new MediaStreamRegistration(source, safeDisplayName, () => Unregister(token, state));
        }
    }

    public bool TryOpen(string source, out MediaStreamLease lease)
    {
        lease = null!;
        if (Volatile.Read(ref _disposed) == 1 || !TryReadToken(source, out var token, out var name) ||
            !_registrations.TryGetValue(token, out var state))
        {
            return false;
        }

        return state.TryOpen(name, out lease);
    }

    /// <summary>Resolves only explicitly registered HLS resources to the local HTTP transport.</summary>
    public string GetPlaybackSource(string source)
    {
        lock (_httpGate)
        {
            if (_httpGateway is not null && TryReadToken(source, out var token, out _) &&
                _registrations.TryGetValue(token, out var state) && state.Transport == MediaStreamTransport.LoopbackHttp)
            {
                return _httpGateway.BaseUri + new Uri(source).AbsolutePath.TrimStart('/');
            }
        }
        return source;
    }

    // Restore the public identity even after a handle is released: transient HTTP URLs
    // must never become playlist/history entries. The path is unique to this broker.
    public string GetPublicSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
            uri.Host == "127.0.0.1" && uri.AbsolutePath.StartsWith(_httpPath, StringComparison.Ordinal) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment))
        {
            var candidate = $"{Scheme}://{Host}/" + uri.AbsolutePath[_httpPath.Length..];
            if (TryReadToken(candidate, out _, out _)) return candidate;
        }
        return source;
    }

    private bool TryOpenHttp(string path, CancellationToken cancellationToken, out MediaStreamLease lease)
    {
        lease = null!;
        var source = $"{Scheme}://{Host}/" + path;
        return Volatile.Read(ref _disposed) == 0 && TryReadToken(source, out var token, out var name) &&
            _registrations.TryGetValue(token, out var state) && state.Transport == MediaStreamTransport.LoopbackHttp &&
            state.TryOpen(name, out lease, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_httpGate)
        {
            _httpGateway?.Dispose();
            _httpGateway = null;
        }

        foreach (var registration in _registrations)
        {
            if (_registrations.TryRemove(registration.Key, out var state))
            {
                state.Retire();
            }
        }
    }

    private static string NormalizeDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "media";
        }

        var candidate = displayName.Trim().Replace('\\', '/');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var sourceUri) &&
            sourceUri.Scheme.Length > 1)
        {
            candidate = sourceUri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        }
        else
        {
            var separator = candidate.LastIndexOf('/');
            if (separator >= 0)
            {
                candidate = candidate[(separator + 1)..];
            }
        }

        var query = candidate.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            candidate = candidate[..query];
        }

        candidate = new string(candidate.Where(static character => !char.IsControl(character)).ToArray()).Trim();
        if (candidate.Length > 128)
        {
            var extension = Path.GetExtension(candidate);
            candidate = extension.Length is > 0 and <= 12
                ? candidate[..(128 - extension.Length)] + extension
                : candidate[..128];
        }

        return string.IsNullOrWhiteSpace(candidate) ? "media" : candidate;
    }

    private static bool TryReadToken(string source, out string token, out string name)
    {
        token = string.Empty;
        name = string.Empty;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(uri.Query) &&
             !string.Equals(uri.Query, "?cache=no", StringComparison.Ordinal)) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || segments[0].Length != TokenSize * 2 ||
            !segments[0].All(static character => char.IsAsciiHexDigit(character)))
        {
            return false;
        }

        token = segments[0].ToLowerInvariant();
        name = Uri.UnescapeDataString(segments[1]);
        return true;
    }

    private void Unregister(string token, RegistrationState state)
    {
        if (_registrations.TryRemove(new KeyValuePair<string, RegistrationState>(token, state)))
        {
            state.Retire();
            ReleaseHttp(state);
        }
    }

    private void ReleaseHttp(RegistrationState state)
    {
        if (state.Transport != MediaStreamTransport.LoopbackHttp) return;
        lock (_httpGate)
        {
            if (--_httpRegistrations == 0)
            {
                _httpGateway?.Dispose();
                _httpGateway = null;
            }
        }
    }

    private sealed class RegistrationState(
        string name,
        Func<IMediaByteStream> streamFactory,
        string? companionName,
        Func<IMediaByteStream>? companionFactory,
        MediaStreamTransport transport,
        Func<CancellationToken, IMediaByteStream>? cancellableFactory)
    {
        public MediaStreamTransport Transport { get; } = transport;
        private readonly IReadOnlyDictionary<string, Func<IMediaByteStream>> _factories =
            CreateFactories(name, streamFactory, companionName, companionFactory);
        private readonly Lock _sync = new();
        private readonly HashSet<MediaStreamLease> _leases = [];
        private readonly CancellationTokenSource? _opening = transport == MediaStreamTransport.LoopbackHttp ? new() : null;
        private bool _retired;

        public bool TryOpen(string requestedName, out MediaStreamLease lease, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_retired || !_factories.TryGetValue(requestedName, out var factory))
                {
                    lease = null!;
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                using var opening = _opening is not null
                    ? CancellationTokenSource.CreateLinkedTokenSource(_opening.Token, cancellationToken) : null;
                var stream = (cancellableFactory is not null ? cancellableFactory(opening?.Token ?? cancellationToken) : factory())
                    ?? throw new InvalidOperationException("The stream factory returned null.");
                lease = new MediaStreamLease(stream, OnLeaseDisposed);
                _leases.Add(lease);
                return true;
            }
        }

        private static IReadOnlyDictionary<string, Func<IMediaByteStream>> CreateFactories(
            string name,
            Func<IMediaByteStream> streamFactory,
            string? companionName,
            Func<IMediaByteStream>? companionFactory)
        {
            var result = new Dictionary<string, Func<IMediaByteStream>>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = streamFactory,
            };
            if (companionName is not null && companionFactory is not null)
            {
                result[companionName] = companionFactory;
            }

            return result;
        }

        public void Retire()
        {
            // A pending HLS HTTP open can hold _sync while waiting for headers.
            // Cancel it before acquiring that lock so release/stop never waits on it.
            try { _opening?.Cancel(); }
            catch (AggregateException) { }
            MediaStreamLease[] leases;
            lock (_sync)
            {
                if (_retired)
                {
                    return;
                }

                _retired = true;
                leases = [.. _leases];
            }

            foreach (var lease in leases)
            {
                lease.Cancel();
            }
            _opening?.Dispose();
        }

        private void OnLeaseDisposed(MediaStreamLease lease)
        {
            lock (_sync)
            {
                _leases.Remove(lease);
            }
        }
    }
}

public enum MediaStreamCachePolicy
{
    Default,
    Disabled,
}

public enum MediaStreamTransport
{
    Callback,
    LoopbackHttp,
}

public sealed class MediaStreamRegistration : IDisposable
{
    private Action? _unregister;

    internal MediaStreamRegistration(string source, string displayName, Action unregister)
    {
        Source = source;
        DisplayName = displayName;
        _unregister = unregister;
    }

    public string Source { get; }

    public string DisplayName { get; }

    public void Dispose() => Interlocked.Exchange(ref _unregister, null)?.Invoke();
}

public sealed class MediaStreamLease : IDisposable
{
    private readonly IMediaByteStream _stream;
    private readonly CancellationTokenSource _cancellation = new();
    private Action<MediaStreamLease>? _onDisposed;
    private int _disposed;

    internal MediaStreamLease(IMediaByteStream stream, Action<MediaStreamLease> onDisposed)
    {
        _stream = stream;
        _onDisposed = onDisposed;
    }

    public bool CanSeek
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _stream.CanSeek;
        }
    }

    public long? Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _stream.Length;
        }
    }

    public int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return _stream.Read(destination, _cancellation.Token);
    }

    public long Seek(long absoluteOffset)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return _stream.Seek(absoluteOffset, _cancellation.Token);
    }

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // A consumer cancellation callback must not break broker teardown.
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 1)
            {
                // A concurrent native close won the race after the state check.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // Continue closing the byte stream even if a callback is faulty.
            }
            finally
            {
                _stream.Dispose();
            }
        }
        finally
        {
            _cancellation.Dispose();
            Interlocked.Exchange(ref _onDisposed, null)?.Invoke(this);
        }
    }
}
