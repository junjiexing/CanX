// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CanX.Plugins.Services;

internal static class PluginJsonSchema
{
    private const int MaximumDepth = 32;
    private static readonly IReadOnlySet<string> JsonTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "null", "boolean", "object", "array", "number", "integer", "string",
    };

    public static void ValidateDefinition(JsonElement schema)
    {
        ValidateDefinition(schema, "$", 0);
    }

    public static void ValidateInstance(JsonElement schema, JsonElement value, string label)
    {
        if (!Matches(schema, value, "$", 0, out var error))
        {
            throw new PluginServiceException("schema_validation_failed", $"{label} {error}");
        }
    }

    private static void ValidateDefinition(JsonElement schema, string path, int depth)
    {
        if (depth > MaximumDepth)
        {
            throw Invalid($"Schema exceeds the maximum nesting depth at {path}.");
        }
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"Schema at {path} must be an object or boolean.");
        }

        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                ValidateType(type.GetString(), path);
            }
            else if (type.ValueKind == JsonValueKind.Array && type.GetArrayLength() > 0)
            {
                foreach (var item in type.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw Invalid($"Schema type array at {path} must contain strings.");
                    }
                    ValidateType(item.GetString(), path);
                }
            }
            else
            {
                throw Invalid($"Schema type at {path} must be a string or non-empty array.");
            }
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"Schema properties at {path} must be an object.");
            }
            foreach (var property in properties.EnumerateObject())
            {
                ValidateDefinition(property.Value, $"{path}.properties.{property.Name}", depth + 1);
            }
        }
        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array ||
                required.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
            {
                throw Invalid($"Schema required at {path} must be an array of strings.");
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            ValidateDefinition(items, $"{path}.items", depth + 1);
        }
        if (schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            ValidateDefinition(additional, $"{path}.additionalProperties", depth + 1);
        }
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out var choices))
            {
                continue;
            }
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                throw Invalid($"Schema {keyword} at {path} must be a non-empty array.");
            }
            var index = 0;
            foreach (var choice in choices.EnumerateArray())
            {
                ValidateDefinition(choice, $"{path}.{keyword}[{index++}]", depth + 1);
            }
        }
        if (schema.TryGetProperty("not", out var not))
        {
            ValidateDefinition(not, $"{path}.not", depth + 1);
        }
        if (schema.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"Schema pattern at {path} must be a string.");
            }
            try
            {
                _ = new Regex(pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                throw Invalid($"Schema pattern at {path} is invalid: {exception.Message}");
            }
        }
    }

    private static bool Matches(
        JsonElement schema,
        JsonElement value,
        string path,
        int depth,
        out string error)
    {
        if (depth > MaximumDepth)
        {
            error = $"exceeds the maximum nesting depth at {path}.";
            return false;
        }
        if (schema.ValueKind == JsonValueKind.True)
        {
            error = string.Empty;
            return true;
        }
        if (schema.ValueKind == JsonValueKind.False)
        {
            error = $"is rejected by the schema at {path}.";
            return false;
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            error = $"has the wrong type at {path}.";
            return false;
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
        {
            error = $"does not match const at {path}.";
            return false;
        }
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array &&
            !values.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            error = $"is not an allowed enum value at {path}.";
            return false;
        }
        if (!MatchesCompositions(schema, value, path, depth, out error))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object &&
            !MatchesObject(schema, value, path, depth, out error))
        {
            return false;
        }
        if (value.ValueKind == JsonValueKind.Array &&
            !MatchesArray(schema, value, path, depth, out error))
        {
            return false;
        }
        if (value.ValueKind == JsonValueKind.String && !MatchesString(schema, value, path, out error))
        {
            return false;
        }
        if (value.ValueKind == JsonValueKind.Number && !MatchesNumber(schema, value, path, out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool MatchesCompositions(
        JsonElement schema,
        JsonElement value,
        string path,
        int depth,
        out string error)
    {
        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var choice in allOf.EnumerateArray())
            {
                if (!Matches(choice, value, path, depth + 1, out error))
                {
                    return false;
                }
            }
        }
        if (schema.TryGetProperty("anyOf", out var anyOf) &&
            !anyOf.EnumerateArray().Any(choice => Matches(choice, value, path, depth + 1, out _)))
        {
            error = $"does not match anyOf at {path}.";
            return false;
        }
        if (schema.TryGetProperty("oneOf", out var oneOf) &&
            oneOf.EnumerateArray().Count(choice => Matches(choice, value, path, depth + 1, out _)) != 1)
        {
            error = $"does not match exactly one oneOf schema at {path}.";
            return false;
        }
        if (schema.TryGetProperty("not", out var not) && Matches(not, value, path, depth + 1, out _))
        {
            error = $"matches a forbidden schema at {path}.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool MatchesObject(
        JsonElement schema,
        JsonElement value,
        string path,
        int depth,
        out string error)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(static item => item.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    error = $"is missing required property '{name}' at {path}.";
                    return false;
                }
            }
        }
        var propertySchemas = schema.TryGetProperty("properties", out var properties)
            ? properties
            : default;
        var hasProperties = propertySchemas.ValueKind == JsonValueKind.Object;
        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && propertySchemas.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!Matches(propertySchema, property.Value, $"{path}.{property.Name}", depth + 1, out error))
                {
                    return false;
                }
                continue;
            }
            if (schema.TryGetProperty("additionalProperties", out var additional))
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    error = $"contains unsupported property '{property.Name}' at {path}.";
                    return false;
                }
                if (additional.ValueKind == JsonValueKind.Object &&
                    !Matches(additional, property.Value, $"{path}.{property.Name}", depth + 1, out error))
                {
                    return false;
                }
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool MatchesArray(
        JsonElement schema,
        JsonElement value,
        string path,
        int depth,
        out string error)
    {
        var length = value.GetArrayLength();
        if (TryInteger(schema, "minItems", out var minimum) && length < minimum)
        {
            error = $"has fewer than {minimum} items at {path}.";
            return false;
        }
        if (TryInteger(schema, "maxItems", out var maximum) && length > maximum)
        {
            error = $"has more than {maximum} items at {path}.";
            return false;
        }
        if (schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (!Matches(items, item, $"{path}[{index++}]", depth + 1, out error))
                {
                    return false;
                }
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool MatchesString(JsonElement schema, JsonElement value, string path, out string error)
    {
        var text = value.GetString() ?? string.Empty;
        if (TryInteger(schema, "minLength", out var minimum) && text.Length < minimum)
        {
            error = $"is shorter than {minimum} characters at {path}.";
            return false;
        }
        if (TryInteger(schema, "maxLength", out var maximum) && text.Length > maximum)
        {
            error = $"is longer than {maximum} characters at {path}.";
            return false;
        }
        if (schema.TryGetProperty("pattern", out var pattern) &&
            !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            error = $"does not match the required pattern at {path}.";
            return false;
        }
        if (schema.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
        {
            var formatName = format.GetString();
            var valid = formatName switch
            {
                "uri" => Uri.TryCreate(text, UriKind.Absolute, out _),
                "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                _ => true,
            };
            if (!valid)
            {
                error = $"does not match format '{formatName}' at {path}.";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool MatchesNumber(JsonElement schema, JsonElement value, string path, out string error)
    {
        if (!value.TryGetDouble(out var number))
        {
            error = $"is not a finite number at {path}.";
            return false;
        }
        if (schema.TryGetProperty("minimum", out var minimum) && minimum.TryGetDouble(out var min) && number < min)
        {
            error = $"is less than {min} at {path}.";
            return false;
        }
        if (schema.TryGetProperty("maximum", out var maximum) && maximum.TryGetDouble(out var max) && number > max)
        {
            error = $"is greater than {max} at {path}.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool MatchesType(JsonElement type, JsonElement value)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return IsType(type.GetString()!, value);
        }
        return type.EnumerateArray().Any(item => IsType(item.GetString()!, value));
    }

    private static bool IsType(string type, JsonElement value) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "string" => value.ValueKind == JsonValueKind.String,
        _ => false,
    };

    private static void ValidateType(string? type, string path)
    {
        if (type is null || !JsonTypes.Contains(type))
        {
            throw Invalid($"Schema at {path} contains unsupported type '{type}'.");
        }
    }

    private static bool TryInteger(JsonElement schema, string name, out int value)
    {
        value = 0;
        return schema.TryGetProperty(name, out var element) && element.TryGetInt32(out value);
    }

    private static PluginServiceException Invalid(string message) => new("invalid_service_schema", message);
}
