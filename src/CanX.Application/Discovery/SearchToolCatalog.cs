// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CanX.Core.Downloads;
using CanX.Plugins.Services;
using CanX.Tooling;

namespace CanX.Application.Discovery;

public interface ISearchActionService
{
    ValueTask PlayResolvedAsync(Uri media, Uri? subtitle, CancellationToken cancellationToken);

    ValueTask DownloadResolvedAsync(string source, CancellationToken cancellationToken);
}

public static class SearchToolCatalog
{
    public static IReadOnlyList<IToolHandler> Create(
        PluginServiceRegistry services,
        ISearchActionService actions)
    {
        var coordinator = new SearchCoordinator(services, actions);
        return
        [
            SearchTool(coordinator, "search.media", SearchKind.Media,
                "Search all enabled AI-visible video providers, then normalize, deduplicate, and score the results."),
            SearchTool(coordinator, "search.subtitles", SearchKind.Subtitle,
                "Search all enabled AI-visible subtitle providers, then normalize, deduplicate, and score the results."),
            SearchTool(coordinator, "search.torrents", SearchKind.Torrent,
                "Search all enabled AI-visible BitTorrent providers, then normalize, deduplicate, and score the results. Results include a shareable magnetUri or torrentUrl when available; use search.download with the result IDs when no public address is shown."),
            new DelegateTool(
                Descriptor(
                    "search.play",
                    "Resolve one cached video result and an optional cached subtitle result, then stream them to the player. Result IDs must come from prior search calls.",
                    ToolRisk.Network,
                    PlaySchema()),
                async (arguments, _, cancellationToken) =>
                {
                    await coordinator.PlayAsync(
                        RequiredString(arguments, "mediaSearchId"),
                        RequiredString(arguments, "mediaResultId"),
                        OptionalString(arguments, "subtitleSearchId"),
                        OptionalString(arguments, "subtitleResultId"),
                        arguments["sourceIndex"]?.GetValue<int>() ?? 0,
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(new JsonObject { ["started"] = true });
                }),
            new DelegateTool(
                Descriptor(
                    "search.download",
                    "Add one cached BitTorrent search result as a download, including results whose private provider URL is not shown to AI. Result IDs must come from a prior torrent search.",
                    ToolRisk.Network,
                    ResultReferenceSchema("searchId", "resultId")),
                async (arguments, _, cancellationToken) =>
                {
                    await coordinator.DownloadAsync(
                        RequiredString(arguments, "searchId"),
                        RequiredString(arguments, "resultId"),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(new JsonObject { ["added"] = true });
                }),
        ];
    }

    private static IToolHandler SearchTool(
        SearchCoordinator coordinator,
        string name,
        SearchKind kind,
        string description) => new DelegateTool(
        Descriptor(name, description, ToolRisk.Network, SearchSchema()),
        async (arguments, _, cancellationToken) => ToolResult.Success(await coordinator.SearchAsync(
            kind,
            RequiredString(arguments, "query"),
            arguments["limit"]?.GetValue<int>() ?? 20,
            ReadStrings(arguments, "preferredLanguages"),
            cancellationToken).ConfigureAwait(false)));

    private static ToolDescriptor Descriptor(
        string name,
        string description,
        ToolRisk risk,
        JsonObject inputSchema) => new(
        name,
        description,
        risk,
        inputSchema,
        new JsonObject { ["type"] = "object" });

    private static JsonObject SearchSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("query"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 500 },
            ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
            ["preferredLanguages"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = 16,
                ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 32 },
            },
        },
    };

    private static JsonObject PlaySchema()
    {
        var schema = ResultReferenceSchema("mediaSearchId", "mediaResultId");
        var properties = (JsonObject)schema["properties"]!;
        properties["subtitleSearchId"] = IdentifierSchema();
        properties["subtitleResultId"] = IdentifierSchema();
        properties["sourceIndex"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 32 };
        return schema;
    }

    private static JsonObject ResultReferenceSchema(string searchName, string resultName) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray(searchName, resultName),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            [searchName] = IdentifierSchema(),
            [resultName] = IdentifierSchema(),
        },
    };

    private static JsonObject IdentifierSchema() => new()
    {
        ["type"] = "string",
        ["minLength"] = 16,
        ["maxLength"] = 64,
        ["pattern"] = "^[A-Za-z0-9_-]+$",
    };

    private static string RequiredString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"'{name}' is required.");

    private static string? OptionalString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>();

    private static IReadOnlyList<string> ReadStrings(JsonObject arguments, string name) =>
        arguments[name] is JsonArray array
            ? array.Select(static item => item!.GetValue<string>()).ToArray()
            : [];

    private sealed class DelegateTool(
        ToolDescriptor descriptor,
        Func<JsonObject, ToolCallContext, CancellationToken, ValueTask<ToolResult>> execute) : IToolHandler
    {
        public ToolDescriptor Descriptor { get; } = descriptor;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken) => execute(arguments, context, cancellationToken);
    }
}

internal enum SearchKind
{
    Media,
    Subtitle,
    Torrent,
}

internal sealed partial class SearchCoordinator
{
    private const int MaximumSessions = 100;
    private const int MaximumDescriptionLength = 1000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly PluginServiceRegistry _services;
    private readonly ISearchActionService _actions;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SearchSession> _sessions = new(StringComparer.Ordinal);

    public SearchCoordinator(PluginServiceRegistry services, ISearchActionService actions)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public async ValueTask<JsonObject> SearchAsync(
        SearchKind kind,
        string query,
        int limit,
        IReadOnlyList<string> preferredLanguages,
        CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length is 0 or > 500) throw new ArgumentException("Search query must contain 1 to 500 characters.", nameof(query));
        limit = Math.Clamp(limit, 1, 100);
        var contract = Contract(kind);
        var providers = _services.Find(contract, 1, PluginServiceAudience.Ai);
        var invocations = providers.Select(provider => InvokeProviderAsync(
            provider,
            kind,
            query,
            Math.Min(limit, 50),
            preferredLanguages,
            cancellationToken)).ToArray();
        var pages = await Task.WhenAll(invocations).ConfigureAwait(false);
        var normalized = pages.SelectMany(static page => page.Results).ToArray();
        var ranked = normalized
            .GroupBy(result => DeduplicationKey(result, kind), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(static result => result.Score)
                .ThenBy(static result => result.ProviderTitle, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
        var searchId = CreateId();
        var stored = ranked.Select(result => result with { ResultId = CreateId() }).ToArray();
        lock (_gate)
        {
            RemoveExpiredSessions();
            if (_sessions.Count >= MaximumSessions)
            {
                foreach (var id in _sessions.OrderBy(static item => item.Value.ExpiresAt).Take(20).Select(static item => item.Key).ToArray())
                {
                    _sessions.Remove(id);
                }
            }
            _sessions.Add(searchId, new SearchSession(kind, stored, DateTimeOffset.UtcNow.Add(SessionLifetime)));
        }

        return new JsonObject
        {
            ["untrusted"] = true,
            ["searchId"] = searchId,
            ["kind"] = KindName(kind),
            ["query"] = query,
            ["providerCount"] = providers.Count,
            ["errors"] = new JsonArray(pages.Where(static page => page.Error is not null).Select(page => (JsonNode?)new JsonObject
            {
                ["providerId"] = page.Provider.Id,
                ["message"] = page.Error,
            }).ToArray()),
            ["items"] = new JsonArray(stored.Select(result => (JsonNode?)ResultJson(result, kind)).ToArray()),
        };
    }

    public async ValueTask PlayAsync(
        string mediaSearchId,
        string mediaResultId,
        string? subtitleSearchId,
        string? subtitleResultId,
        int sourceIndex,
        CancellationToken cancellationToken)
    {
        var media = RequireResult(mediaSearchId, mediaResultId, SearchKind.Media);
        var mediaSources = await ResolveAsync(media, "video", cancellationToken).ConfigureAwait(false);
        if (sourceIndex < 0 || sourceIndex >= mediaSources.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex), "The selected media source index is unavailable.");
        }

        Uri? subtitleUri = null;
        if (!string.IsNullOrWhiteSpace(subtitleSearchId) || !string.IsNullOrWhiteSpace(subtitleResultId))
        {
            if (string.IsNullOrWhiteSpace(subtitleSearchId) || string.IsNullOrWhiteSpace(subtitleResultId))
            {
                throw new ArgumentException("Both subtitle search and result IDs are required.");
            }
            var subtitle = RequireResult(subtitleSearchId, subtitleResultId, SearchKind.Subtitle);
            var subtitleSources = await ResolveAsync(subtitle, "subtitle", cancellationToken).ConfigureAwait(false);
            subtitleUri = subtitleSources[0];
        }
        await _actions.PlayResolvedAsync(mediaSources[sourceIndex], subtitleUri, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DownloadAsync(
        string searchId,
        string resultId,
        CancellationToken cancellationToken)
    {
        var result = RequireResult(searchId, resultId, SearchKind.Torrent);
        var source = DownloadSource(result) ??
            throw new InvalidOperationException("The selected result does not contain a safe downloadable source.");
        await _actions.DownloadResolvedAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderPage> InvokeProviderAsync(
        PluginServiceDescriptor provider,
        SearchKind kind,
        string query,
        int limit,
        IReadOnlyList<string> preferredLanguages,
        CancellationToken cancellationToken)
    {
        try
        {
            using var inputDocument = JsonDocument.Parse(new JsonObject
            {
                ["query"] = query,
                ["limit"] = limit,
            }.ToJsonString());
            var output = await _services.InvokeAsync(
                provider.Id,
                inputDocument.RootElement.Clone(),
                PluginServiceCallContext.ForAi("agent-search"),
                cancellationToken).ConfigureAwait(false);
            if (!output.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new PluginServiceException("invalid_search_result", "Search service did not return an items array.");
            }
            var results = new List<SearchResult>();
            foreach (var item in items.EnumerateArray().Take(100))
            {
                var id = String(item, "id");
                var title = String(item, "title");
                if (id is null || title is null) continue;
                var payload = JsonNode.Parse(item.GetRawText()) as JsonObject ?? new JsonObject();
                var language = String(item, "language");
                results.Add(new SearchResult(
                    string.Empty,
                    provider.Id,
                    provider.PluginId,
                    provider.Title,
                    id,
                    Limit(title, 1000),
                    LimitNullable(String(item, "description"), MaximumDescriptionLength),
                    language,
                    String(item, "format"),
                    Number(item, "sizeBytes"),
                    Natural(item, "seeders"),
                    Score(kind, query, title, language, preferredLanguages, item),
                    payload));
            }
            return new ProviderPage(provider, results, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProviderPage(provider, [], Limit(exception.Message, 500));
        }
    }

    private async ValueTask<IReadOnlyList<Uri>> ResolveAsync(
        SearchResult result,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var resolver = _services.Find("org.canx.media.resolve", 1, PluginServiceAudience.Ai)
            .FirstOrDefault(candidate => string.Equals(candidate.PluginId, result.PluginId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Provider '{result.ProviderTitle}' has no matching media resolver.");
        var input = new JsonObject { ["id"] = result.OriginalId };
        if (result.Payload["resolveData"] is { } data) input["data"] = data.DeepClone();
        using var inputDocument = JsonDocument.Parse(input.ToJsonString());
        var output = await _services.InvokeAsync(
            resolver.Id,
            inputDocument.RootElement.Clone(),
            PluginServiceCallContext.ForAi("agent-search"),
            cancellationToken).ConfigureAwait(false);
        if (!output.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            throw new PluginServiceException("invalid_resolution", "Resolver did not return a sources array.");
        }
        var resultUris = new List<Uri>();
        foreach (var source in sources.EnumerateArray().Take(32))
        {
            if (!string.Equals(String(source, "mediaType"), mediaType, StringComparison.Ordinal)) continue;
            var text = String(source, "uri");
            if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "canx") || !string.IsNullOrEmpty(uri.UserInfo))
            {
                continue;
            }
            resultUris.Add(uri);
        }
        return resultUris.Count > 0
            ? resultUris
            : throw new InvalidOperationException("Resolver returned no safe matching media source.");
    }

    private SearchResult RequireResult(string searchId, string resultId, SearchKind expectedKind)
    {
        lock (_gate)
        {
            RemoveExpiredSessions();
            if (!_sessions.TryGetValue(searchId, out var session) || session.Kind != expectedKind)
            {
                throw new ArgumentException("Invalid or expired search ID.", nameof(searchId));
            }
            return session.Results.FirstOrDefault(result => string.Equals(result.ResultId, resultId, StringComparison.Ordinal))
                ?? throw new ArgumentException("Invalid result ID.", nameof(resultId));
        }
    }

    private void RemoveExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _sessions.Where(item => item.Value.ExpiresAt <= now).Select(static item => item.Key).ToArray())
        {
            _sessions.Remove(id);
        }
    }

    private static JsonObject ResultJson(SearchResult result, SearchKind kind)
    {
        var value = new JsonObject
        {
            ["resultId"] = result.ResultId,
            ["providerId"] = result.ProviderId,
            ["provider"] = result.ProviderTitle,
            ["title"] = result.Title,
            ["description"] = result.Description,
            ["score"] = result.Score,
        };
        if (result.Language is not null) value["language"] = result.Language;
        if (result.Format is not null) value["format"] = result.Format;
        if (result.SizeBytes is not null) value["sizeBytes"] = result.SizeBytes;
        if (result.Seeders is not null) value["seeders"] = result.Seeders;
        if (kind == SearchKind.Torrent)
        {
            var magnetUri = PublicMagnetUri(result);
            var torrentUrl = ShareableTorrentUrl(result);
            if (magnetUri is not null) value["magnetUri"] = magnetUri;
            if (torrentUrl is not null) value["torrentUrl"] = torrentUrl;
            value["downloadable"] = DownloadSource(result) is not null;
        }
        return value;
    }

    private static double Score(
        SearchKind kind,
        string query,
        string title,
        string? language,
        IReadOnlyList<string> preferredLanguages,
        JsonElement item)
    {
        var queryTokens = Tokens(query);
        var titleTokens = Tokens(title);
        var matched = queryTokens.Count(token => titleTokens.Contains(token));
        var score = queryTokens.Count == 0 ? 0 : matched * 60d / queryTokens.Count;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 20;
        if (kind == SearchKind.Subtitle && language is not null)
        {
            var preference = preferredLanguages
                .Select(static item => item.Trim())
                .ToList()
                .FindIndex(item => string.Equals(item, language, StringComparison.OrdinalIgnoreCase));
            if (preference >= 0) score += Math.Max(5, 20 - preference * 2);
        }
        if (kind == SearchKind.Torrent)
        {
            var seeders = Natural(item, "seeders") ?? 0;
            score += Math.Min(20, Math.Log10(seeders + 1) * 5);
        }
        if (QualityPattern().IsMatch(title)) score += 5;
        return Math.Round(score, 3);
    }

    private static string DeduplicationKey(SearchResult result, SearchKind kind)
    {
        if (kind == SearchKind.Torrent)
        {
            var hash = result.Payload["infoHash"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(hash)) return $"hash:{hash.ToUpperInvariant()}";
        }
        return $"{Normalize(result.Title)}|{Normalize(result.Language ?? string.Empty)}|{result.SizeBytes?.ToString(CultureInfo.InvariantCulture)}";
    }

    private static HashSet<string> Tokens(string value) => Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(static token => token.Length > 1)
        .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value) => NonWordPattern().Replace(value.ToLowerInvariant(), " ").Trim();

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) && result >= 0
            ? result
            : null;

    private static int? Natural(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) && result >= 0
            ? result
            : null;

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static string? LimitNullable(string? value, int maximum) => value is null ? null : Limit(value, maximum);

    private static string Contract(SearchKind kind) => kind switch
    {
        SearchKind.Media => "org.canx.media.search",
        SearchKind.Subtitle => "org.canx.subtitle.search",
        SearchKind.Torrent => "org.canx.bittorrent.search",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string KindName(SearchKind kind) => kind.ToString().ToLowerInvariant();

    private static string CreateId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string? DownloadSource(SearchResult result)
    {
        var magnet = result.Payload["magnetUri"]?.GetValue<string>();
        if (magnet is not null && IsSafeDownloadSource(magnet)) return magnet;
        var torrent = result.Payload["torrentUrl"]?.GetValue<string>();
        if (torrent is not null && IsSafeDownloadSource(torrent)) return torrent;
        return PublicMagnetUri(result);
    }

    private static string? PublicMagnetUri(SearchResult result)
    {
        var hash = InfoHashFromMagnet(result.Payload["magnetUri"]?.GetValue<string>()) ??
            NormalizeInfoHash(result.Payload["infoHash"]?.GetValue<string>());
        return hash is null ? null : $"magnet:?xt=urn:btih:{hash}";
    }

    private static string? InfoHashFromMagnet(string? magnet)
    {
        if (magnet?.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) != true) return null;
        foreach (var parameter in magnet[8..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=');
            if (separator <= 0 || !parameter[..separator].Equals("xt", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var value = Uri.UnescapeDataString(parameter[(separator + 1)..].Replace('+', ' '));
                const string prefix = "urn:btih:";
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeInfoHash(value[prefix.Length..]) is { } hash)
                {
                    return hash;
                }
            }
            catch (UriFormatException)
            {
                // Ignore malformed untrusted provider data.
            }
        }
        return null;
    }

    private static string? NormalizeInfoHash(string? value)
    {
        if (value is null) return null;
        return value.Length switch
        {
            40 when value.All(Uri.IsHexDigit) => value.ToLowerInvariant(),
            32 when value.All(static character => char.IsAsciiLetter(character) || character is >= '2' and <= '7') =>
                value.ToUpperInvariant(),
            _ => null,
        };
    }

    private static string? ShareableTorrentUrl(SearchResult result)
    {
        var source = result.Payload["torrentUrl"]?.GetValue<string>();
        if (source is null || !IsSafeDownloadSource(source) || DownloadSecretClassifier.IsSensitiveUri(source) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var uri) || IsPrivateHost(uri))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static bool IsPrivateHost(Uri uri)
    {
        if (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(uri.Host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] is 0 or 10 or 127 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 169 && bytes[1] == 254 ||
                bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2 ||
                bytes[0] == 198 && bytes[1] is 18 or 19 ||
                bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
                bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
                bytes[0] >= 224;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            bytes[0] is 0xfc or 0xfd ||
            bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8 ||
            (bytes[0] & 0xe0) != 0x20;
    }

    private static bool IsSafeDownloadSource(string source)
    {
        if (source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return source.Length <= 8192;
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            source.Length <= 8192;
    }

    [GeneratedRegex("[^\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWordPattern();

    [GeneratedRegex(@"\b(?:2160p|1080p|720p|4k|uhd|bluray|web-?dl)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QualityPattern();

    private sealed record ProviderPage(
        PluginServiceDescriptor Provider,
        IReadOnlyList<SearchResult> Results,
        string? Error);

    private sealed record SearchSession(
        SearchKind Kind,
        IReadOnlyList<SearchResult> Results,
        DateTimeOffset ExpiresAt);

    private sealed record SearchResult(
        string ResultId,
        string ProviderId,
        string PluginId,
        string ProviderTitle,
        string OriginalId,
        string Title,
        string? Description,
        string? Language,
        string? Format,
        long? SizeBytes,
        int? Seeders,
        double Score,
        JsonObject Payload);
}
