// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace CanX.Streaming;

/// <summary>
/// HLS nested I/O is performed by FFmpeg, which cannot use mpv's stream callbacks.
/// Exposes only opted-in broker registrations, on an ephemeral IPv4 loopback port.
/// Each request has its own seekable/cancellable lease; no media is written to disk.
/// </summary>
internal sealed class MediaStreamHttpGateway : IDisposable
{
    internal delegate bool OpenStream(string path, CancellationToken cancellationToken, out MediaStreamLease lease);

    private const int MaximumConnections = 32;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly SemaphoreSlim _slots = new(MaximumConnections);
    private readonly OpenStream _open;
    private readonly string _path;
    private readonly string _authority;
    private int _disposed;

    public MediaStreamHttpGateway(string path, OpenStream open)
    {
        _path = path;
        _open = open;
        _listener.Start(MaximumConnections);
        _authority = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        BaseUri = $"http://{_authority}{path}";
        _ = RunAsync();
    }

    public string BaseUri { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener.Stop();
    }

    private async Task RunAsync()
    {
        var requestId = 0;
        try
        {
            while (true)
            {
                await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }
                var id = ++requestId;
                // Opening and reading a broker stream intentionally uses blocking I/O.
                var task = Task.Run(() => ServeAsync(client, _shutdown.Token));
                _requests[id] = task;
                _ = task.ContinueWith(completed =>
                {
                    _requests.TryRemove(id, out _);
                    _slots.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Closing the final HLS registration stops the listener and its requests.
        }
        finally
        {
            await Task.WhenAll(_requests.Values).ConfigureAwait(false);
            _shutdown.Dispose();
            // Continuations may still release slots after their tasks complete.
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken shutdown)
    {
        using (client)
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            client.NoDelay = true;
            using var closeOnCancel = cancellation.Token.Register(client.Dispose);
            var token = cancellation.Token;
            var sentHeaders = false;
            try
            {
                var stream = client.GetStream();
                cancellation.CancelAfter(TimeSpan.FromSeconds(15));
                var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
                cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                if (request is null)
                {
                    await WriteHeadersAsync(stream, 404, 0, null, false, token).ConfigureAwait(false);
                    return;
                }

                // A seek/stop closes the HTTP request. Propagate that cancellation to
                // a blocking upstream read, including while a new stream is opening.
                var disconnect = WatchDisconnectAsync(stream, cancellation);
                try
                {
                    if (!_open(request.Path, token, out var lease))
                    {
                        await WriteHeadersAsync(stream, 404, 0, null, false, token).ConfigureAwait(false);
                        return;
                    }
                    using (lease)
                    using (token.Register(lease.Cancel))
                    {
                        var length = lease.Length;
                        var start = 0L;
                        long? end = length - 1;
                        var partial = request.Range is not null;
                        if (partial)
                        {
                            var range = request.Range!.Ranges.Single();
                            if (range.From is { } from)
                            {
                                start = from;
                                end = range.To is { } to ? (end is { } last ? Math.Min(last, to) : to) : end;
                            }
                            else if (length is { } size && range.To is > 0)
                            {
                                start = Math.Max(0, size - range.To.Value);
                            }
                            else
                            {
                                await WriteHeadersAsync(stream, 416, 0, length is { } total ? $"bytes */{total}" : null, false, token).ConfigureAwait(false);
                                return;
                            }
                            if ((length is { } known && start >= known) || end < start)
                            {
                                await WriteHeadersAsync(stream, 416, 0, length is { } total ? $"bytes */{total}" : null, false, token).ConfigureAwait(false);
                                return;
                            }
                            // An open-ended range with unknown size may legally be ignored.
                            if (end is null) { partial = false; start = 0; }
                        }
                        var buffer = new byte[64 * 1024];
                        if (start > 0 && request.Method != "HEAD")
                        {
                            if (lease.CanSeek) lease.Seek(start);
                            else
                            {
                                var remaining = start;
                                while (remaining > 0)
                                {
                                    var read = lease.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
                                    if (read == 0) throw new EndOfStreamException();
                                    remaining -= read;
                                }
                            }
                        }
                        var contentLength = end is { } lastByte ? lastByte - start + 1 : (long?)null;
                        var contentRange = partial ? $"bytes {start}-{end}/{length?.ToString(CultureInfo.InvariantCulture) ?? "*"}" : null;
                        await WriteHeadersAsync(stream, partial ? 206 : 200, contentLength, contentRange,
                            request.Path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase), token).ConfigureAwait(false);
                        sentHeaders = true;
                        if (request.Method == "HEAD") return;
                        var left = contentLength ?? long.MaxValue;
                        while (left > 0)
                        {
                            var read = lease.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, left)));
                            if (read == 0) break;
                            await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            left -= read;
                        }
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    if (!sentHeaders)
                        await WriteHeadersAsync(stream, 502, 0, null, false, token).ConfigureAwait(false);
                }
                finally
                {
                    cancellation.Cancel();
                    await disconnect.ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Never expose upstream URLs, headers or exceptions to an HTTP consumer.
                // An interrupted response is closed so FFmpeg sees an error, not a fake EOF.
                if (!sentHeaders && !token.IsCancellationRequested)
                {
                    try { await WriteHeadersAsync(client.GetStream(), 502, 0, null, false, token).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
        }
    }

    private async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) return null;
            count += read;
            var text = Encoding.ASCII.GetString(buffer, 0, count);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var lines = text[..end].Split("\r\n", StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3 || first[0] is not ("GET" or "HEAD") ||
                first[2] is not ("HTTP/1.0" or "HTTP/1.1") ||
                !first[1].StartsWith(_path, StringComparison.Ordinal)) return null;
            string? host = null;
            RangeHeaderValue? range = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) return null;
                var name = line[..colon];
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (host is not null) return null;
                    host = value;
                }
                if (name.Equals("Range", StringComparison.OrdinalIgnoreCase))
                {
                    if (range is not null || !RangeHeaderValue.TryParse(value, out range) ||
                        range.Unit != "bytes" || range.Ranges.Count != 1) return null;
                }
                if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0") return null;
            }
            return host == _authority ? new Request(first[0], first[1][_path.Length..], range) : null;
        }
        return null;
    }

    private static async Task WatchDisconnectAsync(NetworkStream stream, CancellationTokenSource cancellation)
    {
        try { await stream.ReadAsync(new byte[1], cancellation.Token).ConfigureAwait(false); }
        catch (Exception) { }
        finally { cancellation.Cancel(); }
    }

    private static ValueTask WriteHeadersAsync(NetworkStream stream, int status, long? length,
        string? range, bool playlist, CancellationToken token)
    {
        var reason = status switch { 200 => "OK", 206 => "Partial Content", 404 => "Not Found", 416 => "Range Not Satisfiable", _ => "Bad Gateway" };
        var headers = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nCache-Control: no-store\r\n");
        headers.Append("Content-Type: ").Append(playlist ? "application/vnd.apple.mpegurl" : "application/octet-stream").Append("\r\n");
        if (length is { } size) headers.Append(CultureInfo.InvariantCulture, $"Content-Length: {size}\r\n");
        if (range is not null) headers.Append("Content-Range: ").Append(range).Append("\r\n");
        if (length is not null) headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append("\r\n");
        return stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), token);
    }

    private sealed record Request(string Method, string Path, RangeHeaderValue? Range);
}
