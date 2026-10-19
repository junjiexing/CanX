// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CanX.Tooling;

public static class ToolSchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonNode? value, JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new List<string>();
        ValidateNode(value, schema, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonNode? value, JsonObject schema, string path, List<string> errors)
    {
        var expectedTypes = ReadTypes(schema["type"]);
        if (expectedTypes.Count > 0 && !expectedTypes.Any(type => MatchesType(value, type)))
        {
            var description = string.Join(" or ", expectedTypes);
            errors.Add($"{path} must be {Article(description)} {description}.");
            return;
        }

        if (schema["enum"] is JsonArray allowed &&
            !allowed.Any(candidate => JsonNode.DeepEquals(candidate, value)))
        {
            errors.Add($"{path} is not one of the allowed values.");
            return;
        }

        if (value is JsonObject objectValue)
        {
            ValidateObject(objectValue, schema, path, errors);
        }
        else if (value is JsonArray arrayValue)
        {
            ValidateArray(arrayValue, schema, path, errors);
        }
        else if (TryGetString(value, out var stringValue))
        {
            ValidateString(stringValue, schema, path, errors);
        }
        else if (TryGetNumber(value, out var numberValue))
        {
            ValidateNumber(numberValue, schema, path, errors);
        }
    }

    private static void ValidateObject(JsonObject value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["required"] is JsonArray required)
        {
            foreach (var item in required)
            {
                var name = item?.GetValue<string>();
                if (!string.IsNullOrEmpty(name) && !value.ContainsKey(name))
                {
                    errors.Add($"{path}.{name} is required.");
                }
            }
        }

        var properties = schema["properties"] as JsonObject;
        if (schema["additionalProperties"] is JsonValue additionalProperties &&
            additionalProperties.TryGetValue<bool>(out var allowAdditional) && !allowAdditional)
        {
            foreach (var property in value)
            {
                if (properties is null || !properties.ContainsKey(property.Key))
                {
                    errors.Add($"{path}.{property.Key} is not allowed.");
                }
            }
        }

        if (properties is not null)
        {
            foreach (var property in properties)
            {
                if (value.TryGetPropertyValue(property.Key, out var child) && property.Value is JsonObject childSchema)
                {
                    ValidateNode(child, childSchema, $"{path}.{property.Key}", errors);
                }
            }
        }

        if (schema["minProperties"] is JsonValue minPropertiesValue &&
            minPropertiesValue.TryGetValue<int>(out var minProperties) && value.Count < minProperties)
        {
            errors.Add($"{path} must contain at least {minProperties} properties.");
        }
        if (schema["maxProperties"] is JsonValue maxPropertiesValue &&
            maxPropertiesValue.TryGetValue<int>(out var maxProperties) && value.Count > maxProperties)
        {
            errors.Add($"{path} cannot contain more than {maxProperties} properties.");
        }
    }

    private static void ValidateArray(JsonArray value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["minItems"] is JsonValue minItemsValue &&
            minItemsValue.TryGetValue<int>(out var minItems) && value.Count < minItems)
        {
            errors.Add($"{path} must contain at least {minItems} items.");
        }
        if (schema["maxItems"] is JsonValue maxItemsValue &&
            maxItemsValue.TryGetValue<int>(out var maxItems) && value.Count > maxItems)
        {
            errors.Add($"{path} cannot contain more than {maxItems} items.");
        }
        if (schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < value.Count; index++)
            {
                ValidateNode(value[index], itemSchema, $"{path}[{index}]", errors);
            }
        }
    }

    private static void ValidateString(string value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["minLength"] is JsonValue minLengthValue &&
            minLengthValue.TryGetValue<int>(out var minLength) && value.Length < minLength)
        {
            errors.Add($"{path} must contain at least {minLength} characters.");
        }
        if (schema["maxLength"] is JsonValue maxLengthValue &&
            maxLengthValue.TryGetValue<int>(out var maxLength) && value.Length > maxLength)
        {
            errors.Add($"{path} cannot contain more than {maxLength} characters.");
        }
        if (schema["pattern"] is JsonValue patternValue && patternValue.TryGetValue<string>(out var pattern))
        {
            try
            {
                if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    errors.Add($"{path} does not match the required pattern.");
                }
            }
            catch (ArgumentException)
            {
                errors.Add($"{path} uses an invalid schema pattern.");
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add($"{path} could not be validated within the pattern time limit.");
            }
        }
    }

    private static void ValidateNumber(decimal value, JsonObject schema, string path, List<string> errors)
    {
        if (TryGetDecimal(schema["minimum"], out var minimum) && value < minimum)
        {
            errors.Add($"{path} must be at least {minimum.ToString(CultureInfo.InvariantCulture)}.");
        }
        if (TryGetDecimal(schema["maximum"], out var maximum) && value > maximum)
        {
            errors.Add($"{path} cannot exceed {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static bool MatchesType(JsonNode? value, string expectedType) => expectedType switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => TryGetString(value, out _),
        "boolean" => value is JsonValue booleanValue && booleanValue.TryGetValue<bool>(out _),
        "integer" => IsInteger(value),
        "number" => TryGetNumber(value, out _),
        "null" => value is null,
        _ => true,
    };

    private static IReadOnlyList<string> ReadTypes(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var type)) return [type];
        if (value is JsonArray array)
        {
            return array
                .OfType<JsonValue>()
                .Select(item => item.TryGetValue<string>(out var itemType) ? itemType : string.Empty)
                .Where(static item => item.Length > 0)
                .ToArray();
        }
        return [];
    }

    private static bool IsInteger(JsonNode? value) =>
        value is JsonValue jsonValue &&
        (jsonValue.TryGetValue<int>(out _) || jsonValue.TryGetValue<long>(out _) ||
         jsonValue.TryGetValue<uint>(out _) || jsonValue.TryGetValue<ulong>(out _));

    private static bool TryGetString(JsonNode? value, out string result)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            result = text;
            return true;
        }
        result = string.Empty;
        return false;
    }

    private static bool TryGetNumber(JsonNode? value, out decimal result)
    {
        try
        {
            if (value is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<decimal>(out result)) return true;
                if (jsonValue.TryGetValue<int>(out var intValue))
                {
                    result = intValue;
                    return true;
                }
                if (jsonValue.TryGetValue<long>(out var longValue))
                {
                    result = longValue;
                    return true;
                }
                if (jsonValue.TryGetValue<uint>(out var uintValue))
                {
                    result = uintValue;
                    return true;
                }
                if (jsonValue.TryGetValue<ulong>(out var ulongValue))
                {
                    result = ulongValue;
                    return true;
                }
                if (jsonValue.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue) &&
                    doubleValue >= (double)decimal.MinValue && doubleValue <= (double)decimal.MaxValue)
                {
                    result = (decimal)doubleValue;
                    return true;
                }
            }
        }
        catch (FormatException) { }
        catch (InvalidOperationException) { }
        catch (OverflowException) { }
        result = 0;
        return false;
    }

    private static bool TryGetDecimal(JsonNode? value, out decimal result) => TryGetNumber(value, out result);

    private static string Article(string value) => value.Length > 0 && "aeiou".Contains(value[0]) ? "an" : "a";
}
