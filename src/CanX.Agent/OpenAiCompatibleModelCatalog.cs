// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CanX.Agent;

public sealed record AgentModelInfo(string Id, int? ContextWindowTokens, double? DefaultTemperature);

public interface IAgentModelCatalog
{
    Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(
        Uri baseUri, string? enteredApiKey = null, CancellationToken cancellationToken = default);

    Task<AgentModelInfo?> GetModelAsync(
        Uri baseUri, string modelId, string? enteredApiKey = null, CancellationToken cancellationToken = default);
}

public sealed class OpenAiCompatibleModelCatalog(
    HttpClient httpClient,
    IAgentApiKeyProvider? apiKeyProvider = null) : IAgentModelCatalog
{
    private const long MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IAgentApiKeyProvider _apiKeyProvider = apiKeyProvider ?? new EmptyAgentApiKeyProvider();

    public async Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(
        Uri baseUri, string? enteredApiKey = null, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(baseUri, "models", enteredApiKey, false, cancellationToken)
            .ConfigureAwait(false);
        var root = document?.RootElement ?? throw new AgentProviderException("model_catalog_unavailable", "模型列表接口没有返回数据。");
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
            items = root;
        else if (root.ValueKind == JsonValueKind.Object &&
                 (TryArray(root, "data", out items) || TryArray(root, "models", out items)))
        {
            // OpenAI uses data; several compatible servers use models.
        }
        else
            throw new AgentProviderException("invalid_model_catalog", "模型列表接口未返回可识别的列表。");

        var models = new Dictionary<string, AgentModelInfo>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var model = ParseModel(item);
            if (model is not null && models.Count < 2000) models[model.Id] = model;
        }
        return models.Values.OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<AgentModelInfo?> GetModelAsync(
        Uri baseUri, string modelId, string? enteredApiKey = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 200)
            throw new ArgumentException("模型 ID 无效。", nameof(modelId));
        using var document = await GetJsonAsync(
            baseUri, $"models/{Uri.EscapeDataString(modelId)}", enteredApiKey, true, cancellationToken)
            .ConfigureAwait(false);
        if (document is null) return null;
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
            root = data;
        return ParseModel(root, modelId);
    }

    private async Task<JsonDocument?> GetJsonAsync(
        Uri baseUri, string relativePath, string? enteredApiKey, bool allowMissing, CancellationToken cancellationToken)
    {
        AgentProviderConfiguration.ValidateBaseUri(baseUri);
        var basePath = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(basePath, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var key = string.IsNullOrWhiteSpace(enteredApiKey)
            ? await _apiKeyProvider.GetApiKeyForEndpointAsync(baseUri, cancellationToken).ConfigureAwait(false)
            : enteredApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new AgentProviderException("model_catalog_http_error",
                $"模型接口返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new AgentProviderException("model_catalog_too_large", "模型列表响应过大。");
        try
        {
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException)
        {
            throw new AgentProviderException("invalid_model_catalog", "模型接口返回的数据无效。", exception);
        }
    }

    private static AgentModelInfo? ParseModel(JsonElement value, string? fallbackId = null)
    {
        var id = value.ValueKind == JsonValueKind.String ? value.GetString() :
            value.ValueKind == JsonValueKind.Object ? ReadString(value, "id") ?? ReadString(value, "model") : null;
        id ??= fallbackId;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return null;
        var context = ReadContext(value);
        var temperature = ReadDouble(value, "default_temperature") ?? ReadDouble(value, "temperature");
        if (temperature is null && TryObject(value, "default_parameters", out var parameters))
            temperature = ReadDouble(parameters, "temperature");
        if (temperature is < 0 or > 2) temperature = null;
        return new AgentModelInfo(id, context, temperature);
    }

    private static int? ReadContext(JsonElement value)
    {
        foreach (var field in new[]
        {
            "context_length", "context_window", "context_size", "max_context_length",
            "max_context_tokens", "max_model_len", "input_token_limit",
        })
        {
            var count = ReadInt(value, field);
            if (count is > 0 and <= 100_000_000) return count;
        }
        foreach (var group in new[] { "top_provider", "model_info", "limits", "capabilities" })
        {
            if (TryObject(value, group, out var nested) && ReadContext(nested) is { } count) return count;
        }
        return null;
    }

    private static bool TryArray(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out result) &&
            result.ValueKind == JsonValueKind.Array;
    }

    private static bool TryObject(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out result) &&
            result.ValueKind == JsonValueKind.Object;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static int? ReadInt(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var count) ? count : null;

    private static double? ReadDouble(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
