// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CanX.Core.Diagnostics;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;

namespace CanX.Plugins.Http;

internal sealed class PluginHttpClient : IDisposable
{
    private const int MaximumCachedProxyClients = 8;
    private const int MaximumRemoteImageBytes = 2 * 1024 * 1024;
    private const int MaximumCachedRemoteImages = 64;
    private const int MaximumRemoteImageCacheBytes = 32 * 1024 * 1024;
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT",
    };
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Content-Length", "Host", "Keep-Alive", "Proxy-Authenticate",
        "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };
    private static readonly HashSet<string> SensitiveRedirectHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Proxy-Authorization",
    };

    private readonly PluginPermissionSet permissions;
    private readonly PluginHttpOptions options;
    private readonly PluginNetworkGuard networkGuard;
    private readonly IApplicationLog log;
    private readonly string pluginId;
    private readonly HttpClient client;
    private readonly Func<Uri, HttpMessageHandler> proxyHandlerFactory;
    private readonly Dictionary<string, HttpClient> proxyClients = new(StringComparer.Ordinal);
    private readonly object proxyClientsGate = new();
    private readonly Dictionary<string, byte[]> remoteImageCache = new(StringComparer.Ordinal);
    private readonly Queue<string> remoteImageCacheOrder = [];
    private readonly object remoteImageCacheGate = new();
    private readonly SemaphoreSlim concurrency;
    private Uri? defaultProxy;

    public PluginHttpClient(
        PluginPermissionSet permissions,
        PluginHttpOptions options,
        HttpMessageHandler? handler = null,
        Func<Uri, HttpMessageHandler>? proxyHandlerFactory = null,
        PluginNetworkGuard? networkGuard = null,
        IApplicationLog? log = null,
        string? pluginId = null)
    {
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
        this.networkGuard = networkGuard ?? new PluginNetworkGuard();
        this.log = log ?? NullApplicationLog.Instance;
        this.pluginId = string.IsNullOrWhiteSpace(pluginId) ? "unknown" : pluginId;
        handler ??= CreateHandler(null, this.networkGuard);
        this.proxyHandlerFactory = proxyHandlerFactory ?? new Func<Uri, HttpMessageHandler>(proxy =>
            CreateHandler(proxy, this.networkGuard, permissions.Allows(PluginCapabilities.Http, "*")));
        client = CreateClient(handler);
        concurrency = new SemaphoreSlim(options.MaximumConcurrentRequests, options.MaximumConcurrentRequests);
    }

    public async ValueTask<string> SendAsync(string requestJson, CancellationToken cancellationToken)
    {
        PluginHttpRequest request;
        try
        {
            request = JsonSerializer.Deserialize(requestJson, PluginHttpJsonContext.Default.PluginHttpRequest)
                ?? throw InvalidRequest("HTTP request must be an object.");
        }
        catch (JsonException)
        {
            throw InvalidRequest("HTTP request is not valid JSON.");
        }

        var uri = ValidateUri(request.Url);
        var proxy = request.Proxy is null ? Volatile.Read(ref defaultProxy) : ValidateProxy(request.Proxy);
        var method = NormalizeMethod(request.Method);
        var responseType = NormalizeResponseType(request.ResponseType);
        var maximumResponseBytes = request.MaximumResponseBytes ?? options.MaximumResponseBodyBytes;
        if (maximumResponseBytes is < 1 || maximumResponseBytes > options.MaximumResponseBodyBytes)
        {
            throw InvalidRequest(
                $"HTTP maximumResponseBytes must be between 1 and {options.MaximumResponseBodyBytes}.");
        }
        var headers = ValidateHeaders(request.Headers);
        var body = request.Body;
        if (body is not null && Encoding.UTF8.GetByteCount(body) > options.MaximumRequestBodyBytes)
        {
            throw new PluginHttpException(
                "request_too_large",
                $"HTTP request body exceeds {options.MaximumRequestBodyBytes} bytes.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        await concurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var requestClient = GetClient(proxy);
            return await SendWithRedirectsAsync(
                requestClient,
                uri,
                method,
                headers,
                body,
                responseType,
                maximumResponseBytes,
                request.TruncateResponse,
                timeout.Token).ConfigureAwait(false);
        }
        catch (PluginPermissionDeniedException)
        {
            throw;
        }
        catch (PluginHttpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            LogTransportFailure(uri, "request", exception);
            throw new PluginHttpException(
                "http_failure",
                $"The HTTP request failed ({TransportFailureCategory(exception)}).",
                exception);
        }
        finally
        {
            concurrency.Release();
        }
    }

    internal void SetDefaultProxy(string? value) =>
        Volatile.Write(ref defaultProxy, ValidateProxy(value));

    internal async ValueTask<ReadOnlyMemory<byte>> LoadRemoteImageAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var uri = ValidateRemoteImageUri(source);
        var proxy = Volatile.Read(ref defaultProxy);
        var cacheKey = $"{proxy?.AbsoluteUri ?? "direct"}|{uri.AbsoluteUri}";
        lock (remoteImageCacheGate)
        {
            if (remoteImageCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        await concurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            lock (remoteImageCacheGate)
            {
                if (remoteImageCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            var bytes = await LoadRemoteImageWithRedirectsAsync(uri, GetClient(proxy), timeout.Token).ConfigureAwait(false);
            CacheRemoteImage(cacheKey, bytes);
            return bytes;
        }
        catch (PluginPermissionDeniedException)
        {
            throw;
        }
        catch (PluginHttpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            LogTransportFailure(uri, "remote image", exception);
            throw new PluginHttpException(
                "remote_image_failure",
                $"The remote image request failed ({TransportFailureCategory(exception)}).",
                exception);
        }
        finally
        {
            concurrency.Release();
        }
    }

    public void Dispose()
    {
        lock (proxyClientsGate)
        {
            foreach (var proxyClient in proxyClients.Values)
            {
                proxyClient.Dispose();
            }
            proxyClients.Clear();
        }
        lock (remoteImageCacheGate)
        {
            remoteImageCache.Clear();
            remoteImageCacheOrder.Clear();
        }
        client.Dispose();
        concurrency.Dispose();
    }

    private async Task<byte[]> LoadRemoteImageWithRedirectsAsync(
        Uri initialUri,
        HttpClient requestClient,
        CancellationToken cancellationToken)
    {
        var uri = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            permissions.DemandRemoteImage(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await requestClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return await ReadRemoteImageAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (redirectCount >= options.MaximumRedirects)
            {
                throw new PluginHttpException("too_many_redirects", "Remote image redirect limit was exceeded.");
            }

            var location = response.Headers.Location
                ?? throw new PluginHttpException("invalid_redirect", "Remote image redirect has no Location header.");
            uri = ValidateRemoteImageUri(
                location.IsAbsoluteUri ? location.ToString() : new Uri(uri, location).ToString());
        }
    }

    private static async Task<byte[]> ReadRemoteImageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new PluginHttpException(
                "remote_image_status",
                $"Remote image request returned HTTP {(int)response.StatusCode}.");
        }
        if (response.Content.Headers.ContentLength > MaximumRemoteImageBytes)
        {
            throw new PluginHttpException(
                "remote_image_too_large",
                $"Remote image exceeds {MaximumRemoteImageBytes} bytes.");
        }

        var declaredMediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(declaredMediaType) &&
            declaredMediaType is not ("image/png" or "image/jpeg" or "image/webp" or "application/octet-stream"))
        {
            throw new PluginHttpException(
                "invalid_remote_image",
                "Remote image Content-Type must be PNG, JPEG, or WebP.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = MaximumRemoteImageBytes - checked((int)body.Length);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (read > remaining)
            {
                throw new PluginHttpException(
                    "remote_image_too_large",
                    $"Remote image exceeds {MaximumRemoteImageBytes} bytes.");
            }
            body.Write(buffer, 0, read);
        }

        var bytes = body.ToArray();
        var detectedMediaType = DetectRasterImageType(bytes);
        if (detectedMediaType is null ||
            declaredMediaType is "image/png" or "image/jpeg" or "image/webp" &&
            !string.Equals(declaredMediaType, detectedMediaType, StringComparison.Ordinal))
        {
            throw new PluginHttpException(
                "invalid_remote_image",
                "Remote image bytes do not contain the declared PNG, JPEG, or WebP format.");
        }
        return bytes;
    }

    private static string? DetectRasterImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }
        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    private void CacheRemoteImage(string key, byte[] bytes)
    {
        lock (remoteImageCacheGate)
        {
            if (remoteImageCache.ContainsKey(key))
            {
                return;
            }

            var cachedBytes = remoteImageCache.Values.Sum(static value => value.Length);
            while (remoteImageCacheOrder.Count > 0 &&
                   (remoteImageCache.Count >= MaximumCachedRemoteImages ||
                    cachedBytes + bytes.Length > MaximumRemoteImageCacheBytes))
            {
                var oldest = remoteImageCacheOrder.Dequeue();
                if (remoteImageCache.Remove(oldest, out var removed))
                {
                    cachedBytes -= removed.Length;
                }
            }
            remoteImageCache.Add(key, bytes);
            remoteImageCacheOrder.Enqueue(key);
        }
    }

    private Uri ValidateRemoteImageUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.IdnHost) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw InvalidRequest(
                "Remote image URL must be an absolute http/https URL without embedded credentials.");
        }

        permissions.DemandRemoteImage(uri);
        return uri;
    }

    private async Task<string> SendWithRedirectsAsync(
        HttpClient requestClient,
        Uri initialUri,
        string initialMethod,
        IReadOnlyDictionary<string, string> initialHeaders,
        string? initialBody,
        string responseType,
        int maximumResponseBytes,
        bool truncateResponse,
        CancellationToken cancellationToken)
    {
        var uri = initialUri;
        var method = initialMethod;
        var headers = new Dictionary<string, string>(initialHeaders, StringComparer.OrdinalIgnoreCase);
        var body = initialBody;
        for (var redirectCount = 0; ; redirectCount++)
        {
            permissions.DemandHttp(uri);
            using var request = CreateRequest(uri, method, headers, body);
            using var response = await requestClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return await ReadResponseAsync(
                    response,
                    uri,
                    responseType,
                    maximumResponseBytes,
                    truncateResponse,
                    cancellationToken).ConfigureAwait(false);
            }

            if (redirectCount >= options.MaximumRedirects)
            {
                throw new PluginHttpException("too_many_redirects", "HTTP redirect limit was exceeded.");
            }

            var location = response.Headers.Location
                ?? throw new PluginHttpException("invalid_redirect", "HTTP redirect has no Location header.");
            Uri redirected;
            try
            {
                redirected = ValidateUri(location.IsAbsoluteUri ? location.ToString() : new Uri(uri, location).ToString());
            }
            catch (UriFormatException)
            {
                throw new PluginHttpException("invalid_redirect", "HTTP redirect Location is invalid.");
            }

            permissions.DemandHttp(redirected);
            if (!HasSameOrigin(uri, redirected))
            {
                foreach (var header in SensitiveRedirectHeaders)
                {
                    headers.Remove(header);
                }
            }

            if (response.StatusCode == HttpStatusCode.SeeOther ||
                response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect && method == "POST")
            {
                method = "GET";
                body = null;
                headers.Remove("Content-Type");
            }

            uri = redirected;
        }
    }

    private HttpRequestMessage CreateRequest(
        Uri uri,
        string method,
        IReadOnlyDictionary<string, string> headers,
        string? body)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), uri);
        if (body is not null)
        {
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        foreach (var (name, value) in headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.Remove(name);
                if (!message.Content.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Dispose();
                    throw InvalidRequest("HTTP request contains an unsupported header.");
                }
            }
        }

        return message;
    }

    private async Task<string> ReadResponseAsync(
        HttpResponseMessage response,
        Uri finalUri,
        string responseType,
        int maximumResponseBytes,
        bool truncateResponse,
        CancellationToken cancellationToken)
    {
        var truncated = response.Content.Headers.ContentLength > maximumResponseBytes;
        if (truncated && !truncateResponse)
        {
            throw new PluginHttpException(
                "response_too_large",
                $"HTTP response body exceeds {maximumResponseBytes} bytes.");
        }

        var responseHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = 0;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var values = header.Value.ToArray();
            headerBytes += Encoding.UTF8.GetByteCount(header.Key) +
                values.Sum(Encoding.UTF8.GetByteCount);
            if (headerBytes > options.MaximumHeaderBytes)
            {
                throw new PluginHttpException("headers_too_large", "HTTP response headers exceed the configured limit.");
            }

            responseHeaders[header.Key] = values;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maximumResponseBytes - checked((int)body.Length);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                truncated = false;
                break;
            }

            if (read > remaining)
            {
                if (!truncateResponse)
                {
                    throw new PluginHttpException(
                        "response_too_large",
                        $"HTTP response body exceeds {maximumResponseBytes} bytes.");
                }
                body.Write(buffer, 0, remaining);
                truncated = true;
                break;
            }

            body.Write(buffer, 0, read);
            if (truncated && body.Length == maximumResponseBytes)
            {
                break;
            }
        }

        var model = new PluginHttpResponse
        {
            Status = (int)response.StatusCode,
            Url = finalUri.AbsoluteUri,
            Headers = responseHeaders,
            Body = responseType == "base64"
                ? Convert.ToBase64String(body.GetBuffer(), 0, checked((int)body.Length))
                : Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length)),
            BodyEncoding = responseType,
            Truncated = truncated,
        };
        return JsonSerializer.Serialize(model, PluginHttpJsonContext.Default.PluginHttpResponse);
    }

    private Uri ValidateUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.IdnHost) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw InvalidRequest("HTTP URL must be an absolute http/https URL without embedded credentials.");
        }

        permissions.DemandHttp(uri);
        return uri;
    }

    private Uri? ValidateProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || string.IsNullOrEmpty(uri.IdnHost) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is < 1 or > 65535)
        {
            throw InvalidRequest(
                "HTTP proxy must be an absolute http URL without credentials, a path, query, or fragment.");
        }

        permissions.DemandHttp(uri);
        return uri;
    }

    private HttpClient GetClient(Uri? proxy)
    {
        if (proxy is null)
        {
            return client;
        }

        lock (proxyClientsGate)
        {
            var key = proxy.AbsoluteUri;
            if (proxyClients.TryGetValue(key, out var cached))
            {
                return cached;
            }
            if (proxyClients.Count >= MaximumCachedProxyClients)
            {
                throw InvalidRequest($"A plugin can use at most {MaximumCachedProxyClients} HTTP proxies per session.");
            }

            var created = CreateClient(proxyHandlerFactory(proxy));
            proxyClients.Add(key, created);
            return created;
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler, disposeHandler: true)
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private static SocketsHttpHandler CreateHandler(
        Uri? proxy,
        PluginNetworkGuard networkGuard,
        bool allowPrivateProxy = false) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        Proxy = proxy is null ? null : new PluginWebProxy(proxy),
        UseProxy = proxy is not null,
        ConnectCallback = proxy is { } proxyAddress && allowPrivateProxy
            ? (context, cancellationToken) => networkGuard.ConnectProxyAsync(context, proxyAddress, cancellationToken)
            : networkGuard.ConnectAsync,
    };

    private void LogTransportFailure(Uri uri, string operation, Exception exception)
    {
        var message =
            $"Plugin HTTP {operation} failed: {pluginId} -> {uri.IdnHost}:{uri.Port} " +
            $"({TransportFailureCategory(exception)}).";
        log.Write(
            LogComponent.QuickJs,
            ApplicationLogLevel.Error,
            message,
            exception);
    }

    private static string TransportFailureCategory(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http && http.HttpRequestError != HttpRequestError.Unknown)
            {
                return http.HttpRequestError.ToString();
            }
            if (current is System.Net.Sockets.SocketException socket)
            {
                return $"Socket.{socket.SocketErrorCode}";
            }
        }

        return exception.GetType().Name;
    }

    private IReadOnlyDictionary<string, string> ValidateHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (headers.Count > 64)
        {
            throw InvalidRequest("HTTP request cannot contain more than 64 headers.");
        }

        var totalBytes = 0;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (name is null || value is null)
            {
                throw InvalidRequest("HTTP request contains an invalid or forbidden header.");
            }

            if (string.IsNullOrWhiteSpace(name) || ForbiddenHeaders.Contains(name) ||
                name.Any(character => !IsHeaderNameCharacter(character)) ||
                value.Contains('\r') || value.Contains('\n'))
            {
                throw InvalidRequest("HTTP request contains an invalid or forbidden header.");
            }

            totalBytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value);
            if (totalBytes > options.MaximumHeaderBytes)
            {
                throw InvalidRequest("HTTP request headers exceed the configured limit.");
            }

            result[name] = value;
        }

        return result;
    }

    private static string NormalizeMethod(string? method)
    {
        var normalized = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();
        if (!AllowedMethods.Contains(normalized))
        {
            throw InvalidRequest("HTTP method is not supported.");
        }

        return normalized;
    }

    private static string NormalizeResponseType(string? responseType)
    {
        var normalized = string.IsNullOrWhiteSpace(responseType) ? "text" : responseType;
        if (normalized is not ("text" or "base64"))
        {
            throw InvalidRequest("HTTP responseType must be 'text' or 'base64'.");
        }
        return normalized;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool HasSameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;

    private static bool IsHeaderNameCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%' or '&' or '\'' or
            '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static PluginHttpException InvalidRequest(string message) => new("invalid_request", message);
}

internal sealed class PluginWebProxy(Uri address) : IWebProxy
{
    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination) => address;

    public bool IsBypassed(Uri host) => false;
}

internal sealed class PluginHttpException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

internal sealed class PluginHttpRequest
{
    public string? Url { get; init; }

    public string? Method { get; init; }

    public Dictionary<string, string>? Headers { get; init; }

    public string? Body { get; init; }

    public string? ResponseType { get; init; }

    public string? Proxy { get; init; }

    public int? MaximumResponseBytes { get; init; }

    public bool TruncateResponse { get; init; }
}

internal sealed class PluginHttpResponse
{
    public int Status { get; init; }

    public string Url { get; init; } = string.Empty;

    public Dictionary<string, string[]> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Body { get; init; } = string.Empty;

    public string BodyEncoding { get; init; } = "text";

    public bool Truncated { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginHttpRequest))]
[JsonSerializable(typeof(PluginHttpResponse))]
internal sealed partial class PluginHttpJsonContext : JsonSerializerContext;
