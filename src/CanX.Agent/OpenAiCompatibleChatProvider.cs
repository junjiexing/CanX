// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http.Headers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CanX.Tooling;

namespace CanX.Agent;

public sealed class OpenAiCompatibleChatProvider(
    HttpClient httpClient,
    IAgentApiKeyProvider? apiKeyProvider = null) : IAgentChatProvider
{
    private const long MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumResponseContentCharacters = 4_000_000;
    private const int MaximumResponseToolCalls = 256;
    private const int MaximumToolArgumentCharacters = 1_048_576;
    private const int MaximumErrorDetailBytes = 8_192;
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IAgentApiKeyProvider _apiKeyProvider = apiKeyProvider ?? new EmptyAgentApiKeyProvider();

    public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
        AgentProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Configuration.Validate();
        ValidateMessages(request.Messages);
        var toolNames = new ToolNameMap(request.Tools);

        var apiKey = await _apiKeyProvider.GetApiKeyForEndpointAsync(
            request.Configuration.BaseUri,
            cancellationToken).ConfigureAwait(false);
        using var response = await SendWithCompatibilityAsync(request, toolNames, apiKey, cancellationToken)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new AgentProviderException("response_too_large", "LLM endpoint response exceeds the size limit.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var item in ReadJsonResponseAsync(response, cancellationToken).ConfigureAwait(false))
            {
                yield return MapToolNames(item, toolNames);
            }
            yield break;
        }

        await foreach (var item in ReadEventStreamAsync(response, cancellationToken).ConfigureAwait(false))
        {
            yield return MapToolNames(item, toolNames);
        }
    }

    private async Task<HttpResponseMessage> SendWithCompatibilityAsync(
        AgentProviderRequest request,
        ToolNameMap toolNames,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var includeStreamOptions = true;
        var includeTemperature = true;
        var includeToolChoice = true;
        while (true)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, CompletionUri(request.Configuration.BaseUri));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (!string.IsNullOrWhiteSpace(apiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            message.Content = new StringContent(
                BuildRequest(request, toolNames, includeStreamOptions, includeTemperature, includeToolChoice)
                    .ToJsonString(), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                var detail = await ReadErrorDetailAsync(response, apiKey, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.BadRequest && IsUnsupportedParameter(detail))
                {
                    if (includeStreamOptions && detail.Contains("stream_options", StringComparison.OrdinalIgnoreCase))
                    {
                        includeStreamOptions = false;
                        continue;
                    }
                    if (includeTemperature && detail.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        includeTemperature = false;
                        continue;
                    }
                    if (includeToolChoice && detail.Contains("tool_choice", StringComparison.OrdinalIgnoreCase))
                    {
                        includeToolChoice = false;
                        continue;
                    }
                }
                throw new AgentProviderException(
                    "http_error",
                    $"LLM endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})" +
                    (detail.Length == 0 ? "." : $": {detail}"));
            }
        }
    }

    private static async Task<string> ReadErrorDetailAsync(
        HttpResponseMessage response, string? apiKey, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumErrorDetailBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        var body = Encoding.UTF8.GetString(buffer, 0, length);
        string detail;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            detail = ReadErrorMessage(root) ?? body;
        }
        catch (JsonException)
        {
            detail = body;
        }
        if (!string.IsNullOrEmpty(apiKey))
            detail = detail.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        detail = new string(detail.Select(static character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return detail.Length <= 500 ? detail : detail[..500] + "…";
    }

    private static string? ReadErrorMessage(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
            return string.Join("; ", value.EnumerateArray().Select(ReadErrorMessage).Where(static text => !string.IsNullOrWhiteSpace(text)));
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in new[] { "message", "detail", "error", "msg" })
        {
            if (value.TryGetProperty(field, out var nested) && ReadErrorMessage(nested) is { Length: > 0 } text)
            {
                if (value.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String)
                    text += $" (parameter: {param.GetString()})";
                if (value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                    text += $" (code: {code.GetString()})";
                return text;
            }
        }
        return null;
    }

    private static bool IsUnsupportedParameter(string detail) =>
        detail.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("not allowed", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("extra inputs", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("不支持", StringComparison.Ordinal) ||
        detail.Contains("未知参数", StringComparison.Ordinal);

    private static AgentProviderEvent MapToolNames(AgentProviderEvent item, ToolNameMap toolNames) =>
        item is AgentProviderCompleted completed
            ? completed with
            {
                ToolCalls = completed.ToolCalls.Select(call => call with { Name = toolNames.FromWire(call.Name) }).ToArray(),
            }
            : item;

    private static async IAsyncEnumerable<AgentProviderEvent> ReadEventStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new BoundedReadStream(responseStream, MaximumResponseBytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        var content = new StringBuilder();
        var toolCalls = new SortedDictionary<int, ToolCallBuilder>();
        var usage = new AgentUsage(0, 0);
        var completed = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                completed = true;
                break;
            }
            if (data.Length == 0) continue;

            using var document = ParseProviderJson(data);
            var root = document.RootElement;
            usage = ReadUsage(root, usage);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                {
                    var text = contentElement.GetString()!;
                    content.Append(text);
                    if (content.Length > MaximumResponseContentCharacters)
                    {
                        throw new AgentProviderException("response_too_large", "LLM response content exceeds the size limit.");
                    }
                    yield return new AgentProviderTextDelta(text);
                }
                ReadToolCallDeltas(delta, toolCalls);
            }
        }

        if (!completed) throw new AgentProviderException("incomplete_stream", "LLM response stream ended before [DONE].");
        yield return new AgentProviderCompleted(content.Length == 0 ? null : content.ToString(), BuildToolCalls(toolCalls), usage);
    }

    private static async IAsyncEnumerable<AgentProviderEvent> ReadJsonResponseAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new BoundedReadStream(responseStream, MaximumResponseBytes);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("message", out var message))
        {
            throw new AgentProviderException("invalid_response", "LLM endpoint returned an invalid chat completion response.");
        }
        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;
        if (content?.Length > MaximumResponseContentCharacters)
        {
            throw new AgentProviderException("response_too_large", "LLM response content exceeds the size limit.");
        }
        if (!string.IsNullOrEmpty(content)) yield return new AgentProviderTextDelta(content);
        yield return new AgentProviderCompleted(content, ReadCompleteToolCalls(message), ReadUsage(root, new AgentUsage(0, 0)));
    }

    private static JsonObject BuildRequest(
        AgentProviderRequest request,
        ToolNameMap toolNames,
        bool includeStreamOptions,
        bool includeTemperature,
        bool includeToolChoice)
    {
        var root = new JsonObject
        {
            ["model"] = request.Configuration.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray(request.Messages.Select(message => ToJson(message, toolNames)).Cast<JsonNode?>().ToArray()),
        };
        if (includeStreamOptions)
            root["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (includeTemperature)
            root["temperature"] = request.Configuration.Temperature;
        if (request.Tools.Count > 0)
        {
            root["tools"] = new JsonArray(request.Tools.Select(tool => ToJson(tool, toolNames)).Cast<JsonNode?>().ToArray());
            if (includeToolChoice) root["tool_choice"] = "auto";
        }
        return root;
    }

    private static JsonObject ToJson(AgentMessage message, ToolNameMap toolNames)
    {
        var result = new JsonObject { ["role"] = Role(message.Role) };
        if (message.Content is not null) result["content"] = message.Content;
        if (message.Role == AgentMessageRole.Tool)
        {
            result["tool_call_id"] = message.ToolCallId;
        }
        if (message.ToolCalls.Count > 0)
        {
            result["tool_calls"] = new JsonArray(message.ToolCalls.Select(call => (JsonNode?)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolNames.ToWire(call.Name),
                    ["arguments"] = call.ArgumentsJson,
                },
            }).ToArray());
        }
        return result;
    }

    private static JsonObject ToJson(ToolDescriptor tool, ToolNameMap toolNames) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = toolNames.ToWire(tool.Name),
            ["description"] = tool.Description,
            ["parameters"] = tool.InputSchema.DeepClone(),
        },
    };

    private static void ReadToolCallDeltas(JsonElement delta, SortedDictionary<int, ToolCallBuilder> calls)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array) return;
        foreach (var element in toolCalls.EnumerateArray())
        {
            var index = element.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : 0;
            if (index is < 0 or >= MaximumResponseToolCalls)
            {
                throw new AgentProviderException("invalid_response", "LLM response contains an invalid tool-call index.");
            }
            if (!calls.TryGetValue(index, out var builder))
            {
                if (calls.Count >= MaximumResponseToolCalls)
                {
                    throw new AgentProviderException("response_too_large", "LLM response contains too many tool calls.");
                }
                builder = new ToolCallBuilder();
                calls[index] = builder;
            }
            if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) builder.Id.Append(id.GetString());
            if (!element.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) continue;
            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) builder.Name.Append(name.GetString());
            if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String) builder.Arguments.Append(arguments.GetString());
            ValidateToolCallSize(builder.Id.Length, builder.Name.Length, builder.Arguments.Length);
        }
    }

    private static IReadOnlyList<AgentToolCall> BuildToolCalls(SortedDictionary<int, ToolCallBuilder> builders) =>
        builders.Values.Select((builder, index) => new AgentToolCall
        {
            Id = builder.Id.Length == 0 ? $"call_{index}" : builder.Id.ToString(),
            Name = builder.Name.ToString(),
            ArgumentsJson = builder.Arguments.Length == 0 ? "{}" : builder.Arguments.ToString(),
        }).ToArray();

    private static IReadOnlyList<AgentToolCall> ReadCompleteToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return [];
        var result = new List<AgentToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (result.Count >= MaximumResponseToolCalls)
            {
                throw new AgentProviderException("response_too_large", "LLM response contains too many tool calls.");
            }
            if (!call.TryGetProperty("function", out var function)) continue;
            var idValue = call.TryGetProperty("id", out var id) ? id.GetString() ?? $"call_{result.Count}" : $"call_{result.Count}";
            var nameValue = function.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
            var argumentsValue = function.TryGetProperty("arguments", out var arguments) ? arguments.GetString() ?? "{}" : "{}";
            ValidateToolCallSize(idValue.Length, nameValue.Length, argumentsValue.Length);
            result.Add(new AgentToolCall
            {
                Id = idValue,
                Name = nameValue,
                ArgumentsJson = argumentsValue,
            });
        }
        return result;
    }

    private static AgentUsage ReadUsage(JsonElement root, AgentUsage fallback)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return fallback;
        return new AgentUsage(
            ReadInt(usage, "prompt_tokens", "input_tokens"),
            ReadInt(usage, "completion_tokens", "output_tokens"));
    }

    private static int ReadInt(JsonElement value, string primary, string alternate)
    {
        if (value.TryGetProperty(primary, out var first) && first.TryGetInt32(out var result)) return result;
        return value.TryGetProperty(alternate, out var second) && second.TryGetInt32(out result) ? result : 0;
    }

    private static void ValidateToolCallSize(int idLength, int nameLength, int argumentsLength)
    {
        if (idLength > 256 || nameLength > 256 || argumentsLength > MaximumToolArgumentCharacters)
        {
            throw new AgentProviderException("response_too_large", "LLM tool call exceeds the size limit.");
        }
    }

    private static JsonDocument ParseProviderJson(string value)
    {
        try
        {
            return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw new AgentProviderException("invalid_response", "LLM endpoint returned malformed JSON.", exception);
        }
    }

    private static Uri CompletionUri(Uri baseUri) => new(
        baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? baseUri : new Uri(baseUri.AbsoluteUri + "/"),
        "chat/completions");

    private static string Role(AgentMessageRole role) => role switch
    {
        AgentMessageRole.System => "system",
        AgentMessageRole.User => "user",
        AgentMessageRole.Assistant => "assistant",
        AgentMessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private sealed class ToolNameMap
    {
        private readonly Dictionary<string, string> _wireToHost = new(StringComparer.Ordinal);

        public ToolNameMap(IReadOnlyList<ToolDescriptor> tools)
        {
            foreach (var tool in tools)
            {
                var wireName = ToWire(tool.Name);
                if (!_wireToHost.TryAdd(wireName, tool.Name))
                    throw new ArgumentException("Tool names produce a duplicate OpenAI function name.", nameof(tools));
            }
        }

        public string ToWire(string name)
        {
            var normalized = new string(name.Select(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
            if (normalized.Length > 30) normalized = normalized[..30];
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..24];
            return $"canx_{normalized}_{hash}";
        }

        public string FromWire(string name) => _wireToHost.TryGetValue(name, out var hostName) ? hostName : name;
    }

    private static void ValidateMessages(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count is < 1 or > 512) throw new ArgumentException("Agent request must contain between 1 and 512 messages.");
        long characters = 0;
        foreach (var message in messages)
        {
            characters += message.Content?.Length ?? 0;
            characters += message.ToolCalls.Sum(static call => (long)call.ArgumentsJson.Length + call.Name.Length + call.Id.Length);
        }
        if (characters > 4_000_000) throw new ArgumentException("Agent request exceeds the message size limit.");
    }

    private sealed class ToolCallBuilder
    {
        public StringBuilder Id { get; } = new();
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }

    private sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));

        private int Count(int read)
        {
            _bytesRead = checked(_bytesRead + read);
            if (_bytesRead > maximumBytes)
            {
                throw new AgentProviderException("response_too_large", "LLM endpoint response exceeds the size limit.");
            }
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}

public sealed class AgentProviderException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
