// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Downloads;

public static class DownloadSecretClassifier
{
    public static bool IsSensitiveOption(string optionName) =>
        optionName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        optionName.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
        optionName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        optionName.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        optionName.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        optionName.Contains("header", StringComparison.OrdinalIgnoreCase) ||
        optionName.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        optionName.EndsWith("-user", StringComparison.OrdinalIgnoreCase);

    public static bool IsSensitiveUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        !uri.IsFile &&
        (uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
            ? IsSensitiveMagnet(value)
            : !string.IsNullOrEmpty(uri.UserInfo) ||
              value.Contains('?', StringComparison.Ordinal) ||
              value.Contains('#', StringComparison.Ordinal) ||
              !string.IsNullOrEmpty(uri.Query) ||
              !string.IsNullOrEmpty(uri.Fragment));

    // A magnet link's query is its public identity (xt, dn, tr, ws, ...), so it only counts as a secret when
    // an embedded tracker or web seed URL itself carries credentials or a private query (for example a
    // passkey). Treating every magnet as secret made long links exceed platform credential size limits.
    private static bool IsSensitiveMagnet(string value)
    {
        var queryIndex = value.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return false;
        }

        foreach (var parameter in value[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = parameter[..separator];
            if (!name.StartsWith("tr", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("ws", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("as", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("xs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var embedded = Uri.UnescapeDataString(parameter[(separator + 1)..]);
            if (Uri.TryCreate(embedded, UriKind.Absolute, out var embeddedUri) &&
                (!string.IsNullOrEmpty(embeddedUri.UserInfo) || !string.IsNullOrEmpty(embeddedUri.Query)))
            {
                return true;
            }
        }
        return false;
    }

    public static string RedactUri(string value)
    {
        var suffixIndex = value.IndexOfAny(['?', '#']);
        var valueWithoutSuffix = suffixIndex >= 0 ? value[..suffixIndex] : value;
        if (!Uri.TryCreate(valueWithoutSuffix, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return value;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri + (suffixIndex >= 0 ? "?<redacted>" : string.Empty);
    }

    public static string RemoveSecretsFromUri(string value)
    {
        var suffixIndex = value.IndexOfAny(['?', '#']);
        var valueWithoutSuffix = suffixIndex >= 0 ? value[..suffixIndex] : value;
        if (!Uri.TryCreate(valueWithoutSuffix, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return value;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }
}
