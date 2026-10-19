using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace CanX.Spikes.Streaming;

internal sealed class RangeGateway : IAsyncDisposable
{
    private readonly AriaDownloadCoordinator _download;
    private readonly string _filePath;
    private readonly Uri _sourceUri;
    private readonly string _contentType;
    private readonly TcpListener _listener;
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
    });
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly TaskCompletionSource _shutdownRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestId;

    public RangeGateway(
        AriaDownloadCoordinator download,
        string filePath,
        Uri sourceUri,
        int port)
    {
        _download = download;
        _filePath = filePath;
        _sourceUri = sourceUri;
        _contentType = Path.GetExtension(sourceUri.AbsolutePath).ToLowerInvariant() switch
        {
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            _ => "video/mp4",
        };
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public Task ShutdownRequested => _shutdownRequested.Task;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                var requestId = Interlocked.Increment(ref _requestId);
                var request = HandleClientAsync(client, cancellationToken);
                _requests[requestId] = request;
                _ = request.ContinueWith(
                    completed => _requests.TryRemove(requestId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(_requests.Values);
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                var request = await ReadRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                if (request.Path == "/shutdown")
                {
                    await WriteSimpleResponseAsync(stream, 200, "OK", "shutting down\n", cancellationToken);
                    _shutdownRequested.TrySetResult();
                    return;
                }
                if (request.Path == "/health")
                {
                    await WriteSimpleResponseAsync(stream, 200, "OK", "ready\n", cancellationToken);
                    return;
                }
                if (request.Path != "/media" || (request.Method != "GET" && request.Method != "HEAD"))
                {
                    await WriteSimpleResponseAsync(stream, 404, "Not Found", "not found\n", cancellationToken);
                    return;
                }

                await ServeMediaAsync(stream, request, cancellationToken);
            }
            catch (IOException)
            {
                // A media client can abandon a request when seeking.
            }
            catch (SocketException)
            {
                // A media client can close a connection after obtaining metadata.
            }
        }
    }

    private async Task ServeMediaAsync(
        NetworkStream clientStream,
        GatewayRequest request,
        CancellationToken cancellationToken)
    {
        var totalLength = await _download.WaitForMetadataAsync(cancellationToken);
        var range = ParseRange(request.Range, totalLength);
        var start = range?.Start ?? 0;
        var end = range?.End ?? totalLength - 1;
        var isPartial = range is not null;

        if (request.Method == "GET" && start >= _download.AvailableLength && start > 0 && !_download.IsComplete)
        {
            await ProxyMissingRangeAsync(clientStream, start, end, cancellationToken);
            return;
        }

        if (request.Method == "GET")
        {
            await _download.WaitForOffsetAsync(start, cancellationToken);
        }

        var contentLength = end - start + 1;
        await WriteMediaHeadersAsync(
            clientStream,
            isPartial ? 206 : 200,
            isPartial ? "Partial Content" : "OK",
            contentLength,
            isPartial ? $"bytes {start}-{end}/{totalLength}" : null,
            _contentType,
            cancellationToken);

        if (request.Method == "HEAD")
        {
            return;
        }

        var availableAtStart = _download.AvailableLength;
        Console.WriteLine(
            $"CANX_GATEWAY_FILE_STREAM start={start} end={end} available={availableAtStart} total={totalLength}");
        if (availableAtStart < totalLength)
        {
            Console.WriteLine(
                $"CANX_GATEWAY_PROGRESSIVE_CONFIRMED available={availableAtStart} total={totalLength}");
        }

        await using var file = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        var position = start;
        while (position <= end)
        {
            var available = await _download.WaitForOffsetAsync(position, cancellationToken);
            var readable = (int)Math.Min(Math.Min(buffer.Length, end - position + 1), available - position);
            if (readable <= 0)
            {
                if (_download.IsComplete)
                {
                    break;
                }
                continue;
            }

            file.Position = position;
            var read = await file.ReadAsync(buffer.AsMemory(0, readable), cancellationToken);
            if (read == 0)
            {
                await Task.Delay(5, cancellationToken);
                continue;
            }
            await clientStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            position += read;
        }
    }

    private async Task ProxyMissingRangeAsync(
        NetworkStream clientStream,
        long start,
        long end,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, _sourceUri);
        message.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            Console.WriteLine(
                $"CANX_GATEWAY_RANGE_UNAVAILABLE status={(int)response.StatusCode} start={start} end={end}");
            await WriteSimpleResponseAsync(
                clientStream,
                503,
                "Service Unavailable",
                "origin does not support byte ranges\n",
                cancellationToken);
            return;
        }

        var length = response.Content.Headers.ContentLength
            ?? throw new IOException("origin range response omitted Content-Length");
        var contentRange = response.Content.Headers.ContentRange?.ToString()
            ?? throw new IOException("origin range response omitted Content-Range");
        await WriteMediaHeadersAsync(
            clientStream,
            206,
            "Partial Content",
            length,
            contentRange,
            response.Content.Headers.ContentType?.ToString() ?? _contentType,
            cancellationToken);
        Console.WriteLine(
            $"CANX_GATEWAY_ORIGIN_RANGE start={start} end={end} available={_download.AvailableLength} total={_download.TotalLength}");
        await using var origin = await response.Content.ReadAsStreamAsync(cancellationToken);
        await origin.CopyToAsync(clientStream, cancellationToken);
    }

    private static async Task<GatewayRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        const int maxHeaderSize = 32 * 1024;
        var bytes = new List<byte>(1024);
        var tail = 0u;
        while (bytes.Count < maxHeaderSize)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }
            bytes.Add(buffer[0]);
            tail = (tail << 8) | buffer[0];
            if (tail == 0x0d0a0d0a)
            {
                break;
            }
        }

        if (bytes.Count >= maxHeaderSize)
        {
            throw new IOException("HTTP request headers are too large");
        }

        var lines = Encoding.ASCII.GetString(bytes.ToArray())
            .Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return null;
        }

        string? range = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                range = line[6..].Trim();
            }
        }
        return new GatewayRequest(requestLine[0], requestLine[1], range);
    }

    private static ByteRange? ParseRange(string? value, long totalLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || value.Contains(','))
        {
            throw new IOException("only one byte range is supported");
        }

        var components = value[6..].Split('-', 2);
        if (components.Length != 2)
        {
            throw new IOException("invalid byte range");
        }

        long start;
        long end;
        if (components[0].Length == 0)
        {
            var suffixLength = long.Parse(components[1], CultureInfo.InvariantCulture);
            start = Math.Max(0, totalLength - suffixLength);
            end = totalLength - 1;
        }
        else
        {
            start = long.Parse(components[0], CultureInfo.InvariantCulture);
            end = components[1].Length == 0
                ? totalLength - 1
                : long.Parse(components[1], CultureInfo.InvariantCulture);
        }

        if (start < 0 || start >= totalLength || end < start)
        {
            throw new IOException("requested byte range is unsatisfiable");
        }
        return new ByteRange(start, Math.Min(end, totalLength - 1));
    }

    private static async Task WriteMediaHeadersAsync(
        NetworkStream stream,
        int status,
        string reason,
        long contentLength,
        string? contentRange,
        string contentType,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Connection: close\r\n");
        if (contentRange is not null)
        {
            builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        }
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken);
    }

    private static async Task WriteSimpleResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private sealed record GatewayRequest(string Method, string Path, string? Range);

    private sealed record ByteRange(long Start, long End);
}
