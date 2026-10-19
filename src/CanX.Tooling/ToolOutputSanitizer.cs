// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CanX.Tooling;

public static partial class ToolOutputSanitizer
{
    private const string Redacted = "<redacted>";
    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.Ordinal)
    {
        "apikey",
        "authorization",
        "clientsecret",
        "cookie",
        "password",
        "passwd",
        "refreshtoken",
        "accesstoken",
        "secret",
        "setcookie",
    };

    public static ToolResult Sanitize(ToolResult result) => result with
    {
        Value = Sanitize(result.Value),
        ErrorMessage = result.ErrorMessage is null ? null : SanitizeText(result.ErrorMessage),
    };

    public static JsonNode? Sanitize(JsonNode? value) => value switch
    {
        JsonObject source => SanitizeObject(source),
        JsonArray source => new JsonArray(source.Select(Sanitize).ToArray()),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => JsonValue.Create(SanitizeText(text)),
        null => null,
        _ => value.DeepClone(),
    };

    private static JsonObject SanitizeObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var property in source)
        {
            result[property.Key] = IsSensitiveProperty(property.Key)
                ? JsonValue.Create(Redacted)
                : Sanitize(property.Value);
        }
        return result;
    }

    private static bool IsSensitiveProperty(string name)
    {
        var normalized = new string(name.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return SensitivePropertyNames.Contains(normalized);
    }

    private static string SanitizeText(string value)
    {
        var sanitized = UriUserInfoRegex().Replace(value, "${scheme}<redacted>@");
        sanitized = AuthorizationRegex().Replace(sanitized, "${name}${separator}<redacted>");
        sanitized = SensitiveAssignmentRegex().Replace(sanitized, "${name}${separator}<redacted>");
        return SensitiveQueryRegex().Replace(sanitized, "${prefix}<redacted>");
    }

    [GeneratedRegex(
        @"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/@:]+(?::[^\s/@]*)?@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex(
        @"(?<name>\bAuthorization)(?<separator>\s*[:=]\s*)(?:Bearer\s+)?[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"(?<name>\b(?:password|passwd|access_token|refresh_token|secret|cookie|set-cookie|api[-_]?key))(?<separator>\s*[:=]\s*)[^\s,;&#]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(
        @"(?<prefix>[?&](?:password|passwd|access_token|refresh_token|secret|cookie|api[-_]?key)=)[^&#\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryRegex();
}
