// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using CanX.Plugins.Http;
using CanX.Plugins.Services;

namespace CanX.Application.Discovery;

public sealed record OnlineSubtitleResult(
    string ProviderId,
    string PluginId,
    string Provider,
    string Id,
    string Title,
    string? Language,
    string? Format,
    string? Description,
    JsonNode? ResolveData = null)
{
    public string Details => string.Join(" · ",
        new[] { Provider, Language, Format, Description }.Where(static value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record OnlineSubtitleSearchPage(
    IReadOnlyList<OnlineSubtitleResult> Items,
    IReadOnlyList<string> Errors);

public sealed record OnlineSubtitleProvider(string Id, string Title)
{
    public override string ToString() => Title;
}

public sealed record OnlineSubtitleSource(string Name, Uri Uri);

/// <summary>Connects the player subtitle picker to enabled subtitle service plugins.</summary>
public sealed class OnlineSubtitleCatalog(PluginServiceRegistry services)
{
    private const string SearchContract = "org.canx.subtitle.search";
    private const string ResolveContract = "org.canx.media.resolve";
    private readonly PluginServiceRegistry _services = services ?? throw new ArgumentNullException(nameof(services));

    public IReadOnlyList<OnlineSubtitleProvider> Providers =>
        _services.Find(SearchContract, 1, PluginServiceAudience.Ai)
            .OrderBy(static provider => provider.Title, StringComparer.CurrentCulture)
            .Select(static provider => new OnlineSubtitleProvider(provider.Id, provider.Title))
            .ToArray();

    public bool HasProviders => Providers.Count > 0;

    public Task<OnlineSubtitleSearchPage> SearchAsync(
        string query,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length is < 3 or > 500)
        {
            throw new ArgumentException("请输入至少 3 个字符的字幕关键词。", nameof(query));
        }

        var provider = _services.Find(SearchContract, 1, PluginServiceAudience.Ai)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, providerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("选中的字幕搜索接口已不可用，请重新选择。");
        return SearchProviderAsync(provider, query, cancellationToken);
    }

    public async Task<IReadOnlyList<OnlineSubtitleSource>> ResolveAsync(
        OnlineSubtitleResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var resolver = _services.Find(ResolveContract, 1, PluginServiceAudience.Ai)
            .FirstOrDefault(candidate => string.Equals(candidate.PluginId, result.PluginId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{result.Provider} 没有提供字幕地址解析服务。");
        var request = new JsonObject { ["id"] = result.Id };
        if (result.ResolveData is { } data) request["data"] = data.DeepClone();
        using var input = JsonDocument.Parse(request.ToJsonString());
        var output = await _services.InvokeAsync(
            resolver.Id,
            input.RootElement,
            PluginServiceCallContext.ForAi("subtitle-picker"),
            cancellationToken).ConfigureAwait(false);
        if (Text(output, "error") is { } error)
        {
            throw new InvalidOperationException($"{result.Provider}：{error}");
        }
        if (!output.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("字幕插件没有返回有效的文件列表。");
        }

        var choices = new List<OnlineSubtitleSource>();
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object ||
                !string.Equals(Text(source, "mediaType"), "subtitle", StringComparison.Ordinal) ||
                Text(source, "uri") is not { } address ||
                !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                continue;
            }
            var name = Text(source, "label") ?? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (string.IsNullOrWhiteSpace(name)) name = "subtitle.srt";
            choices.Add(new OnlineSubtitleSource(name, uri));
        }
        return choices.Count > 0
            ? choices
            : throw new InvalidDataException("此结果没有可直接加载的字幕文件。");
    }

    public Task<byte[]> DownloadAsync(OnlineSubtitleSource source, CancellationToken cancellationToken = default) =>
        PublicSubtitleDownloader.DownloadAsync(source.Uri, cancellationToken);

    public static string SuggestedQuery(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title == "尚未播放") return string.Empty;
        var fileName = Uri.TryCreate(title, UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath))
            : Path.GetFileName(title);
        return Path.GetFileNameWithoutExtension(fileName).Replace('.', ' ').Replace('_', ' ').Trim();
    }

    private async Task<OnlineSubtitleSearchPage> SearchProviderAsync(
        PluginServiceDescriptor provider,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            using var input = JsonDocument.Parse(new JsonObject
            {
                ["query"] = query,
                ["limit"] = 15,
            }.ToJsonString());
            var output = await _services.InvokeAsync(
                provider.Id,
                input.RootElement,
                PluginServiceCallContext.ForAi("subtitle-picker"),
                cancellationToken).ConfigureAwait(false);
            if (Text(output, "error") is { } error)
            {
                return new OnlineSubtitleSearchPage([], [$"{provider.Title}：{error}"]);
            }
            if (!output.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("搜索结果格式无效。");
            }

            var results = new List<OnlineSubtitleResult>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    Text(item, "id") is not { } id ||
                    Text(item, "title") is not { } title) continue;
                results.Add(new OnlineSubtitleResult(
                    provider.Id, provider.PluginId, provider.Title, id, title,
                    Text(item, "language"), Text(item, "format"), Text(item, "description"),
                    item.TryGetProperty("resolveData", out var data)
                        ? JsonNode.Parse(data.GetRawText()) : null));
            }
            return new OnlineSubtitleSearchPage(results, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new OnlineSubtitleSearchPage([], [$"{provider.Title}：{exception.Message}"]);
        }
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}
