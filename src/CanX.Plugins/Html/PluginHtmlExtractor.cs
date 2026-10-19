// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CanX.Plugins.Html;

internal sealed class PluginHtmlException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class PluginHtmlExtractor
{
    private const int MaximumHtmlBytes = 4 * 1024 * 1024;
    private const int MaximumSelectorLength = 1024;
    private const int MaximumFields = 32;
    private const int MaximumRows = 1000;
    private const int MaximumValuesPerField = 1000;
    private const int MaximumOutputBytes = 4 * 1024 * 1024;

    public static string Extract(string requestJson)
    {
        using var request = Parse(requestJson);
        var root = request.RootElement;
        var html = RequiredString(root, "html", MaximumHtmlBytes);
        if (Encoding.UTF8.GetByteCount(html) > MaximumHtmlBytes)
        {
            throw Invalid("HTML document exceeds the 4 MiB limit.");
        }

        var selector = RequiredString(root, "selector", MaximumSelectorLength);
        var limit = root.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var requestedLimit)
            ? Math.Clamp(requestedLimit, 1, MaximumRows)
            : 100;
        if (!root.TryGetProperty("fields", out var fieldsElement) ||
            fieldsElement.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("HTML extraction requires a fields object.");
        }

        var fields = ParseFields(fieldsElement);
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        var document = parser.ParseDocument(html);
        IHtmlCollection<IElement> selected;
        try
        {
            selected = document.QuerySelectorAll(selector);
        }
        catch (Exception exception)
        {
            throw Invalid($"Invalid row selector: {exception.Message}");
        }

        var rows = new JsonArray();
        foreach (var element in selected.Take(limit))
        {
            var row = new JsonObject();
            foreach (var field in fields)
            {
                row[field.Name] = field.All
                    ? ExtractMany(element, field)
                    : JsonValue.Create(ExtractOne(element, field));
            }
            rows.Add((JsonNode)row);
        }

        var result = new JsonObject { ["rows"] = rows }.ToJsonString();
        if (Encoding.UTF8.GetByteCount(result) > MaximumOutputBytes)
        {
            throw new PluginHtmlException("html_result_too_large", "HTML extraction result exceeds the 4 MiB limit.");
        }
        return result;
    }

    private static IReadOnlyList<Field> ParseFields(JsonElement fieldsElement)
    {
        var fields = new List<Field>();
        foreach (var property in fieldsElement.EnumerateObject())
        {
            if (fields.Count >= MaximumFields)
            {
                throw Invalid($"HTML extraction supports at most {MaximumFields} fields.");
            }
            if (property.Name.Length is < 1 or > 64 || property.Value.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("HTML extraction field names and definitions are invalid.");
            }

            var definition = property.Value;
            var selector = OptionalString(definition, "selector", MaximumSelectorLength);
            var value = OptionalString(definition, "value", 16) ?? "text";
            if (value is not ("text" or "html" or "outerHtml" or "attribute"))
            {
                throw Invalid($"HTML extraction field '{property.Name}' has an unsupported value kind.");
            }
            var attribute = OptionalString(definition, "attribute", 128);
            if ((value == "attribute") != (attribute is not null))
            {
                throw Invalid($"HTML extraction field '{property.Name}' must use attribute only with value='attribute'.");
            }
            var all = definition.TryGetProperty("all", out var allElement) &&
                allElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                allElement.GetBoolean();
            fields.Add(new Field(property.Name, selector, value, attribute, all));
        }
        if (fields.Count == 0)
        {
            throw Invalid("HTML extraction requires at least one field.");
        }
        return fields;
    }

    private static JsonArray ExtractMany(IElement root, Field field)
    {
        var values = new JsonArray();
        foreach (var element in Select(root, field.Selector).Take(MaximumValuesPerField))
        {
            values.Add((JsonNode?)JsonValue.Create(ExtractValue(element, field)));
        }
        return values;
    }

    private static string? ExtractOne(IElement root, Field field)
    {
        var element = Select(root, field.Selector).FirstOrDefault();
        return element is null ? null : ExtractValue(element, field);
    }

    private static IEnumerable<IElement> Select(IElement root, string? selector)
    {
        if (selector is null)
        {
            return [root];
        }
        try
        {
            return root.QuerySelectorAll(selector);
        }
        catch (Exception exception)
        {
            throw Invalid($"Invalid field selector: {exception.Message}");
        }
    }

    private static string? ExtractValue(IElement element, Field field) => field.Value switch
    {
        "text" => element.TextContent.Trim(),
        "html" => element.InnerHtml,
        "outerHtml" => element.OuterHtml,
        "attribute" => element.GetAttribute(field.Attribute!),
        _ => throw new UnreachableException(),
    };

    private static JsonDocument Parse(string requestJson)
    {
        try
        {
            var document = JsonDocument.Parse(requestJson, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Invalid("HTML extraction request must be an object.");
            }
            return document;
        }
        catch (JsonException)
        {
            throw Invalid("HTML extraction request is not valid JSON.");
        }
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength) =>
        OptionalString(root, name, maximumLength) ?? throw Invalid($"HTML extraction requires '{name}'.");

    private static string? OptionalString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"HTML extraction '{name}' must be a string.");
        }
        var value = element.GetString() ?? string.Empty;
        if (value.Length is < 1 || value.Length > maximumLength)
        {
            throw Invalid($"HTML extraction '{name}' is empty or too long.");
        }
        return value;
    }

    private static PluginHtmlException Invalid(string message) => new("invalid_html_request", message);

    private sealed record Field(string Name, string? Selector, string Value, string? Attribute, bool All);
}
