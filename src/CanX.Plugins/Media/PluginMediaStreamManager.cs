// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CanX.Plugins.Http;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Streaming;

namespace CanX.Plugins.Media;

internal sealed partial class PluginMediaStreamManager : IDisposable
{
    private const int MaximumSessions = 16;
    private const int MaximumAllowedHosts = 32;
    private const int MaximumHeaders = 32;
    private const int MaximumPlaylistBytes = 2 * 1024 * 1024;
    private const int MaximumPlaylistResources = 10_000;
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Origin", "Referer", "User-Agent",
    };

    private readonly string _pluginId;
    private readonly PluginPermissionSet _permissions;
    private readonly MediaStreamBroker _broker;
    private readonly PluginMediaPlaybackBroker _playbackBroker;
    private readonly Func<HttpMessageHandler> _transportFactory;
    private readonly Dictionary<string, ManagedSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _disposed;

    public PluginMediaStreamManager(
        string pluginId,
        PluginPermissionSet permissions,
        MediaStreamBroker broker,
        PluginMediaPlaybackBroker playbackBroker,
        Func<HttpMessageHandler>? transportFactory = null)
    {
        _pluginId = pluginId;
        _permissions = permissions;
        _broker = broker;
        _playbackBroker = playbackBroker;
        _transportFactory = transportFactory ?? CreateSafeTransport;
    }

    public string Create(string requestJson)
    {
        var recipe = ParseRecipe(requestJson);
        var session = new ManagedSession(_broker, _permissions, recipe, _transportFactory());
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sessions.Count >= MaximumSessions)
                {
                    throw new PluginMediaStreamException(
                        "stream_limit_exceeded",
                        $"A plugin can keep at most {MaximumSessions} managed media streams open.");
                }
                _sessions.Add(id, session);
            }
        }
        catch { session.Dispose(); throw; }
        return new JsonObject { ["id"] = id, ["source"] = session.Source }.ToJsonString();
    }

    public bool Release(string id)
    {
        ManagedSession? session;
        lock (_gate)
        {
            session = _sessions.Remove(id, out var removed) ? removed : null;
        }
        session?.Dispose();
        return session is not null;
    }

    public async ValueTask<string> RequestPlayAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var request = ParseObject(requestJson);
        var recipeJson = request.RootElement.TryGetProperty("recipe", out var recipe)
            ? recipe.GetRawText()
            : throw Invalid("Managed media playback requires a recipe.");
        var created = Create(recipeJson);
        using var result = JsonDocument.Parse(created);
        var id = result.RootElement.GetProperty("id").GetString()!;
        var source = result.RootElement.GetProperty("source").GetString()!;
        try
        {
            await _playbackBroker.RequestAsync(
                new PluginMediaPlaybackRequest(_pluginId, source),
                cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch
        {
            Release(id);
            throw;
        }
    }

    public void Dispose()
    {
        ManagedSession[] sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    private Recipe ParseRecipe(string requestJson)
    {
        using var request = ParseObject(requestJson);
        var root = request.RootElement;
        var url = RequiredString(root, "url", 8192);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) ||
            source.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(source.UserInfo))
        {
            throw Invalid("Managed media URL must be absolute HTTP(S) without embedded credentials.");
        }
        _permissions.Demand(PluginCapabilities.MediaStream);
        _permissions.DemandHttp(source);

        var displayName = OptionalString(root, "displayName", 256) ?? DisplayName(source);
        var format = OptionalString(root, "format", 16) ?? "auto";
        if (format is not ("auto" or "file" or "hls"))
        {
            throw Invalid("Managed media format must be auto, file, or hls.");
        }
        var isHls = format == "hls" || format == "auto" && IsHls(source);

        var allowedHosts = ReadHosts(root, source);
        var headers = ReadHeaders(root, "headers");
        var headersByHost = ReadHeadersByHost(root, allowedHosts);
        return new Recipe(source, displayName, isHls, allowedHosts, headers, headersByHost);
    }

    private IReadOnlySet<string> ReadHosts(JsonElement root, Uri source)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source.IdnHost };
        if (root.TryGetProperty("allowedHosts", out var element))
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("Managed media allowedHosts must be an array.");
            }
            foreach (var item in element.EnumerateArray())
            {
                var host = item.ValueKind == JsonValueKind.String
                    ? NormalizeHost(item.GetString())
                    : throw Invalid("Managed media host entries must be strings.");
                if (!hosts.Add(host)) continue;
                if (hosts.Count > MaximumAllowedHosts)
                {
                    throw Invalid($"Managed media supports at most {MaximumAllowedHosts} allowed hosts.");
                }
            }
        }
        foreach (var host in hosts)
        {
            _permissions.DemandHttp(new Uri($"https://{host}/"));
        }
        return hosts;
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"Managed media {property} must be an object.");
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = 0;
        foreach (var item in element.EnumerateObject())
        {
            if (!AllowedHeaders.Contains(item.Name) || item.Value.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"Managed media header '{item.Name}' is not allowed.");
            }
            var value = item.Value.GetString() ?? string.Empty;
            if (value.Contains('\r') || value.Contains('\n'))
            {
                throw Invalid("Managed media header values cannot contain newlines.");
            }
            bytes += Encoding.UTF8.GetByteCount(item.Name) + Encoding.UTF8.GetByteCount(value);
            if (headers.Count >= MaximumHeaders || bytes > 16 * 1024)
            {
                throw Invalid("Managed media headers exceed their limit.");
            }
            headers[item.Name] = value;
        }
        return headers;
    }

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadHeadersByHost(
        JsonElement root,
        IReadOnlySet<string> allowedHosts)
    {
        if (!root.TryGetProperty("headersByHost", out var element))
        {
            return new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Managed media headersByHost must be an object.");
        }
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            var host = NormalizeHost(property.Name);
            if (!allowedHosts.Contains(host))
            {
                throw Invalid("Managed media host-specific headers must target an allowed host.");
            }
            using var wrapper = JsonDocument.Parse($$"""{"headers":{{property.Value.GetRawText()}}}""");
            result[host] = ReadHeaders(wrapper.RootElement, "headers");
        }
        return result;
    }

    private static JsonDocument ParseObject(string requestJson)
    {
        try
        {
            var document = JsonDocument.Parse(requestJson, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Invalid("Managed media request must be an object.");
            }
            return document;
        }
        catch (JsonException)
        {
            throw Invalid("Managed media request is not valid JSON.");
        }
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength) =>
        OptionalString(root, name, maximumLength) ?? throw Invalid($"Managed media request requires '{name}'.");

    private static string? OptionalString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.String) throw Invalid($"Managed media '{name}' must be a string.");
        var value = element.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 && value.Length <= maximumLength
            ? value
            : throw Invalid($"Managed media '{name}' is empty or too long.");
    }

    private static string NormalizeHost(string? value)
    {
        var text = value?.Trim().TrimEnd('.') ?? string.Empty;
        if (!Uri.TryCreate($"https://{text}/", UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.IdnHost) || uri.IdnHost.Contains(':') ||
            !string.Equals(uri.Host, text, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Managed media hosts must be exact DNS host names.");
        }
        return uri.IdnHost.ToLowerInvariant();
    }

    private static string DisplayName(Uri uri)
    {
        var name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
        return string.IsNullOrWhiteSpace(name) ? "media" : name;
    }

    private static bool IsHls(Uri uri) => uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static HttpMessageHandler CreateSafeTransport()
    {
        var guard = new PluginNetworkGuard();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            UseProxy = true,
            ConnectCallback = guard.ConnectAsync,
        };
    }

    private static PluginMediaStreamException Invalid(string message) =>
        new("invalid_media_stream", message);

    private sealed record Recipe(
        Uri Source,
        string DisplayName,
        bool IsHls,
        IReadOnlySet<string> AllowedHosts,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> HeadersByHost);

    private sealed class ManagedSession : IDisposable
    {
        private readonly MediaStreamBroker _broker;
        private readonly PluginPermissionSet _permissions;
        private readonly Recipe _recipe;
        private readonly HttpClient _client;
        private readonly MediaStreamRegistration _root;
        private readonly Dictionary<string, MediaStreamRegistration> _resources = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private bool _disposed;

        public ManagedSession(
            MediaStreamBroker broker,
            PluginPermissionSet permissions,
            Recipe recipe,
            HttpMessageHandler transport)
        {
            _broker = broker;
            _permissions = permissions;
            _recipe = recipe;
            _client = new HttpClient(new RedirectHandler(this, transport), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            var displayName = recipe.IsHls && !recipe.DisplayName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? recipe.DisplayName + ".m3u8"
                : recipe.DisplayName;
            try
            {
                _root = broker.Register(displayName, token => Open(recipe.Source, recipe.IsHls, token),
                    transport: recipe.IsHls ? MediaStreamTransport.LoopbackHttp : MediaStreamTransport.Callback);
            }
            catch { _client.Dispose(); _lifetime.Dispose(); throw; }
        }

        public string Source => _root.Source;

        public void Dispose()
        {
            MediaStreamRegistration[] resources;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                resources = [.. _resources.Values];
                _resources.Clear();
            }
            _root.Dispose();
            foreach (var resource in resources) resource.Dispose();
            _client.Dispose();
            _lifetime.Dispose();
        }

        private IMediaByteStream Open(Uri source, bool playlist, CancellationToken cancellationToken,
            int rangeBufferSize = 2 * 1024 * 1024)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateTarget(source);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            return playlist
                ? OpenPlaylist(source, cancellation.Token)
                : HttpMediaByteStream.OpenAsync(
                    _client,
                    source,
                    ownsClient: false,
                    rangeBufferSize: rangeBufferSize,
                    cancellationToken: cancellation.Token).AsTask().GetAwaiter().GetResult();
        }

        private IMediaByteStream OpenPlaylist(Uri source, CancellationToken cancellationToken)
        {
            using var response = _client.GetAsync(
                source,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStream(cancellationToken);
            using var content = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = stream.ReadAsync(buffer, cancellationToken).AsTask().GetAwaiter().GetResult();
                if (read == 0) break;
                if (content.Length + read > MaximumPlaylistBytes)
                {
                    throw new IOException("HLS playlist exceeds the 2 MiB limit.");
                }
                content.Write(buffer, 0, read);
            }
            var finalUri = response.RequestMessage?.RequestUri ?? source;
            var rewritten = RewritePlaylist(Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length)), finalUri);
            return new MemoryMediaByteStream(Encoding.UTF8.GetBytes(rewritten));
        }

        private string RewritePlaylist(string playlist, Uri baseUri)
        {
            var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var variantFollows = false;
            var rangeLine = -1;
            Uri? previousRangeTarget = null;
            long previousRangeEnd = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.Length == 0) continue;
                if (!trimmed.StartsWith('#'))
                {
                    if (rangeLine >= 0)
                    {
                        var target = new Uri(baseUri, trimmed);
                        var range = ParseByteRange(lines[rangeLine].Trim()[17..],
                            previousRangeTarget == target ? previousRangeEnd : null);
                        // Give each logical byte-range segment its own resource identity.
                        // FFmpeg's HTTP connection reuse for identical segment URLs can
                        // end fMP4 playback after the first range. Keep offsets explicit
                        // because implicit offsets require the previous URI to match.
                        lines[rangeLine] = FormattableString.Invariant($"#EXT-X-BYTERANGE:{range.Length}@{range.Offset}");
                        lines[index] = ReplaceUri(baseUri, trimmed, false, range);
                        previousRangeTarget = target;
                        previousRangeEnd = checked(range.Offset + range.Length);
                        rangeLine = -1;
                    }
                    else
                    {
                        lines[index] = ReplaceUri(baseUri, trimmed, variantFollows || IsHlsUri(trimmed));
                        previousRangeTarget = null;
                    }
                    variantFollows = false;
                    continue;
                }
                if (trimmed.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal)) rangeLine = index;
                if (trimmed.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) variantFollows = true;
                var playlistAttribute = trimmed.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#EXT-X-RENDITION-REPORT:", StringComparison.Ordinal);
                lines[index] = UriAttributeRegex().Replace(lines[index], match =>
                    $"{match.Groups[1].Value}{ReplaceUri(baseUri, match.Groups[2].Value, playlistAttribute || IsHlsUri(match.Groups[2].Value))}{match.Groups[3].Value}");
            }
            return string.Join('\n', lines);
        }

        private static (long Offset, long Length) ParseByteRange(string value, long? implicitOffset)
        {
            var parts = value.Split('@');
            if (parts.Length is < 1 or > 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0)
                throw new IOException("HLS byte range has an invalid length.");
            var offset = implicitOffset;
            if (parts.Length == 2)
            {
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var explicitOffset))
                    throw new IOException("HLS byte range has an invalid offset.");
                offset = explicitOffset;
            }
            if (offset is null || offset > long.MaxValue - length)
                throw new IOException("HLS byte range has no valid preceding range or overflows.");
            return (offset.Value, length);
        }

        private string ReplaceUri(Uri baseUri, string value, bool playlist, (long Offset, long Length)? byteRange = null)
        {
            if (!Uri.TryCreate(baseUri, value, out var target) || target.Scheme is not ("http" or "https"))
            {
                throw new PluginMediaStreamException("stream_host_denied", "HLS resources must use approved HTTP(S) targets.");
            }
            ValidateTarget(target);
            var key = FormattableString.Invariant($"{(playlist ? 'p' : 'b')}:{byteRange?.Offset ?? -1}:{byteRange?.Length ?? -1}:{target.AbsoluteUri}");
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_resources.TryGetValue(key, out var existing)) return _broker.GetPlaybackSource(existing.Source);
                if (_resources.Count >= MaximumPlaylistResources)
                {
                    throw new IOException("HLS playlist contains too many resources.");
                }
                var name = DisplayName(target);
                if (playlist && !name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) name += ".m3u8";
                var readAhead = (int)Math.Min(byteRange?.Length ?? 2 * 1024 * 1024, 2 * 1024 * 1024);
                var registration = _broker.Register(name, token => Open(target, playlist, token, readAhead), transport: MediaStreamTransport.LoopbackHttp);
                _resources.Add(key, registration);
                return _broker.GetPlaybackSource(registration.Source);
            }
        }

        private void ValidateTarget(Uri uri)
        {
            if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                !_recipe.AllowedHosts.Any(host => HostMatches(uri.IdnHost, host)))
            {
                throw new PluginMediaStreamException("stream_host_denied", "Managed media target is outside the approved hosts.");
            }
            _permissions.DemandHttp(uri);
        }

        private IReadOnlyDictionary<string, string> HeadersFor(Uri uri)
        {
            var headers = new Dictionary<string, string>(_recipe.Headers, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _recipe.HeadersByHost)
            {
                if (!HostMatches(uri.IdnHost, pair.Key)) continue;
                foreach (var header in pair.Value)
                {
                    if (header.Value.Length == 0) headers.Remove(header.Key);
                    else headers[header.Key] = header.Value;
                }
            }
            return headers;
        }

        private static bool HostMatches(string candidate, string allowed) =>
            string.Equals(candidate, allowed, StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith('.' + allowed, StringComparison.OrdinalIgnoreCase);

        private static bool IsHlsUri(string value)
        {
            var query = value.IndexOfAny(['?', '#']);
            var path = query < 0 ? value : value[..query];
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class RedirectHandler : HttpMessageHandler
        {
            private const int MaximumRedirects = 5;
            private readonly ManagedSession _session;
            private readonly HttpMessageInvoker _transport;

            public RedirectHandler(ManagedSession session, HttpMessageHandler transport)
            {
                _session = session;
                _transport = new HttpMessageInvoker(transport, disposeHandler: true);
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var current = request;
                for (var redirects = 0; ; redirects++)
                {
                    var uri = current.RequestUri ?? throw new HttpRequestException("Managed media request has no URL.");
                    _session.ValidateTarget(uri);
                    ApplyHeaders(current, _session.HeadersFor(uri));
                    var response = await _transport.SendAsync(current, cancellationToken).ConfigureAwait(false);
                    if (!IsRedirect(response.StatusCode)) return response;
                    if (redirects >= MaximumRedirects)
                    {
                        response.Dispose();
                        throw new HttpRequestException("Managed media redirect limit was exceeded.");
                    }
                    var location = response.Headers.Location ?? throw new HttpRequestException("Managed media redirect has no Location header.");
                    var redirected = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    response.Dispose();
                    var next = new HttpRequestMessage(HttpMethod.Get, redirected);
                    if (current.Headers.Range is { } range) next.Headers.Range = range;
                    current = next;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _transport.Dispose();
                base.Dispose(disposing);
            }

            private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
            {
                foreach (var name in AllowedHeaders) request.Headers.Remove(name);
                foreach (var header in headers)
                {
                    if (header.Value.Length > 0 && !request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        throw new HttpRequestException($"Managed media header '{header.Key}' is invalid.");
                    }
                }
            }

            private static bool IsRedirect(HttpStatusCode status) => status is
                HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
        }
    }

    [GeneratedRegex("(URI=[\\\"])([^\\\"]+)([\\\"])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriAttributeRegex();
}
