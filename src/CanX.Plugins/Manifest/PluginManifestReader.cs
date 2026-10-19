// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CanX.Plugins.Manifest;

public static partial class PluginManifestReader
{
    public const int CurrentManifestVersion = 1;
    public const int MaximumManifestBytes = 256 * 1024;

    public static PluginApiVersion CurrentApiVersion { get; } = new(2, 0);

    public static IReadOnlySet<string> BuiltInUiIcons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "plugin", "home", "search", "globe", "library", "folder", "file", "link",
        "download", "upload", "play", "music", "video", "subtitles", "image",
        "list", "grid", "history", "favorite", "settings", "tools", "info", "warning",
    };

    public static PluginManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw Invalid("manifest is empty");
        }

        if (utf8Json.Length > MaximumManifestBytes)
        {
            throw Invalid($"manifest exceeds {MaximumManifestBytes} bytes");
        }

        PluginManifestDocument document;
        try
        {
            document = JsonSerializer.Deserialize(
                utf8Json,
                PluginManifestJsonContext.Default.PluginManifestDocument)
                ?? throw Invalid("manifest must contain a JSON object");
        }
        catch (JsonException exception)
        {
            throw Invalid($"invalid JSON at {exception.Path ?? "$"}: {exception.Message}");
        }

        return Validate(document);
    }

    private static PluginManifest Validate(PluginManifestDocument document)
    {
        var errors = new List<string>();

        if (document.ManifestVersion != CurrentManifestVersion)
        {
            errors.Add($"manifestVersion must be {CurrentManifestVersion}");
        }

        var id = Required(document.Id, "id", 128, errors);
        if (id.Length > 0 && !PluginIdPattern().IsMatch(id))
        {
            errors.Add("id must be a lowercase reverse-domain identifier");
        }

        var name = Required(document.Name, "name", 80, errors);
        var description = Optional(document.Description, "description", 500, errors);

        var version = Required(document.Version, "version", 64, errors);
        if (version.Length > 0 && !SemanticVersionPattern().IsMatch(version))
        {
            errors.Add("version must be a SemVer 2.0 version");
        }

        var apiVersion = ParseApiVersion(document.ApiVersion, errors);
        var entry = Required(document.Entry, "entry", 256, errors);
        ValidateEntry(entry, errors);
        var permissions = ValidatePermissions(document.Permissions, errors);
        var ui = ValidateUi(document.Ui, permissions, errors);
        var configuration = ValidateConfiguration(document.Configuration, name, permissions, errors);

        if (errors.Count > 0)
        {
            throw new PluginManifestException(errors);
        }

        return new PluginManifest(
            document.ManifestVersion,
            id,
            name,
            description,
            version,
            apiVersion,
            entry,
            permissions,
            ui,
            configuration);
    }

    private static PluginConfigurationManifest? ValidateConfiguration(
        PluginConfigurationManifestDocument? document,
        string pluginName,
        IReadOnlyList<PluginPermission> permissions,
        ICollection<string> errors)
    {
        var hasPermission = permissions.Any(permission =>
            permission.Capability == PluginCapabilities.UiConfiguration);
        if (document is null)
        {
            if (hasPermission)
            {
                errors.Add("permission 'ui.configuration' requires a manifest configuration declaration");
            }
            return null;
        }
        if (!hasPermission)
        {
            errors.Add("manifest configuration declaration requires permission 'ui.configuration'");
        }
        var title = string.IsNullOrWhiteSpace(document.Title)
            ? $"{pluginName} settings"
            : Optional(document.Title, "configuration.title", 48, errors) ?? $"{pluginName} settings";
        return new PluginConfigurationManifest(title);
    }

    private static PluginUiManifest? ValidateUi(
        PluginUiManifestDocument? document,
        IReadOnlyList<PluginPermission> permissions,
        ICollection<string> errors)
    {
        var hasPermission = permissions.Any(permission => permission.Capability == PluginCapabilities.UiPage);
        if (document is null)
        {
            if (hasPermission)
            {
                errors.Add("permission 'ui.page' requires a manifest ui declaration");
            }
            return null;
        }
        if (!hasPermission)
        {
            errors.Add("manifest ui declaration requires permission 'ui.page'");
        }

        var title = Required(document.Title, "ui.title", 32, errors);
        if (document.Icon is null)
        {
            errors.Add("ui.icon is required");
            return null;
        }

        var builtIn = string.IsNullOrWhiteSpace(document.Icon.Builtin) ? null : document.Icon.Builtin.Trim();
        var asset = string.IsNullOrWhiteSpace(document.Icon.Asset) ? null : document.Icon.Asset.Trim();
        if (string.IsNullOrEmpty(builtIn) == string.IsNullOrEmpty(asset))
        {
            errors.Add("ui.icon must declare exactly one of builtin or asset");
            return null;
        }
        if (builtIn is not null)
        {
            if (!BuiltInUiIcons.Contains(builtIn))
            {
                errors.Add($"ui.icon builtin '{builtIn}' is not supported");
            }
            return new PluginUiManifest(title, new PluginUiIcon(PluginUiIconKind.BuiltIn, builtIn));
        }

        asset ??= string.Empty;
        if (!IsPortableUiAssetPath(asset) ||
            !asset.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            !asset.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !asset.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !asset.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("ui.icon asset must be a portable PNG, JPEG, or WebP path below assets/");
        }
        return new PluginUiManifest(title, new PluginUiIcon(PluginUiIconKind.Asset, asset));
    }

    private static bool IsPortableUiAssetPath(string path) =>
        path.Length is > 7 and <= 256 &&
        path.StartsWith("assets/", StringComparison.Ordinal) &&
        !path.Contains('\\') &&
        !path.Contains(':') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static PluginApiVersion ParseApiVersion(string? value, ICollection<string> errors)
    {
        var text = Required(value, "apiVersion", 16, errors);
        var match = ApiVersionPattern().Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            if (text.Length > 0)
            {
                errors.Add("apiVersion must use major.minor format");
            }

            return default;
        }

        if (major != CurrentApiVersion.Major || minor != CurrentApiVersion.Minor)
        {
            errors.Add($"apiVersion {major}.{minor} is not supported; host requires API {CurrentApiVersion}");
        }

        return new PluginApiVersion(major, minor);
    }

    private static IReadOnlyList<PluginPermission> ValidatePermissions(
        PluginPermissionDocument[]? documents,
        ICollection<string> errors)
    {
        if (documents is null)
        {
            return [];
        }

        if (documents.Length > 64)
        {
            errors.Add("permissions cannot contain more than 64 entries");
        }

        var permissions = new List<PluginPermission>(documents.Length);
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index];
            var prefix = $"permissions[{index}]";
            var capability = Required(document.Capability, $"{prefix}.capability", 64, errors);
            if (capability.Length == 0)
            {
                continue;
            }

            if (!PluginCapabilities.All.Contains(capability))
            {
                errors.Add($"{prefix}.capability '{capability}' is not supported");
            }

            if (!capabilities.Add(capability))
            {
                errors.Add($"permission '{capability}' is duplicated");
            }

            var scopes = document.Scopes ?? [];
            if (PluginCapabilities.RequiresScope(capability) && scopes.Length == 0)
            {
                errors.Add($"permission '{capability}' requires at least one scope");
            }
            else if (!PluginCapabilities.AllowsScope(capability) && scopes.Length > 0)
            {
                errors.Add($"permission '{capability}' does not accept scopes");
            }

            if (scopes.Length > 128)
            {
                errors.Add($"{prefix}.scopes cannot contain more than 128 entries");
            }

            var normalizedScopes = new List<string>(scopes.Length);
            var uniqueScopes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawScope in scopes)
            {
                var scope = (rawScope ?? string.Empty).Trim();
                if (scope.Length is 0 or > 256)
                {
                    errors.Add($"{prefix}.scopes contains an empty or oversized value");
                    continue;
                }

                ValidateScope(capability, scope, prefix, errors);
                if (!uniqueScopes.Add(scope))
                {
                    errors.Add($"{prefix}.scopes contains duplicate '{scope}'");
                    continue;
                }

                normalizedScopes.Add(scope);
            }

            permissions.Add(new PluginPermission(capability, normalizedScopes));
        }

        return permissions;
    }

    private static void ValidateScope(
        string capability,
        string scope,
        string prefix,
        ICollection<string> errors)
    {
        if (capability == PluginCapabilities.Http && scope != "*" && !HttpDomainPattern().IsMatch(scope))
        {
            errors.Add($"{prefix}.scopes HTTP value '{scope}' must be '*', a hostname, or a wildcard subdomain");
        }
        else if (capability == PluginCapabilities.UiRemoteImages &&
                 scope != "*" && !HttpDomainPattern().IsMatch(scope))
        {
            errors.Add(
                $"{prefix}.scopes remote image value '{scope}' must be '*', a hostname, or a wildcard subdomain");
        }
        else if (capability is PluginCapabilities.ToolsInvoke or PluginCapabilities.Secrets &&
                 !ScopeNamePattern().IsMatch(scope))
        {
            errors.Add($"{prefix}.scopes value '{scope}' is not a valid scoped name");
        }
        else if (capability is PluginCapabilities.ServicesProvide or PluginCapabilities.ServicesInvoke &&
                 scope != "*" && !ServiceScopePattern().IsMatch(scope))
        {
            errors.Add($"{prefix}.scopes value '{scope}' must be '*', a service contract, or a full service id");
        }
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]{0,126}[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceScopePattern();

    private static void ValidateEntry(string entry, ICollection<string> errors)
    {
        if (entry.Length == 0)
        {
            return;
        }

        if (entry.StartsWith('/') ||
            entry.Contains('\\') ||
            entry.Contains(':'))
        {
            errors.Add("entry must be a portable relative path");
            return;
        }

        var segments = entry.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            errors.Add("entry cannot contain empty, '.' or '..' path segments");
        }

        if (!entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
            !entry.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("entry must point to a .js or .mjs module");
        }
    }

    private static string Required(
        string? value,
        string field,
        int maximumLength,
        ICollection<string> errors)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            errors.Add($"{field} is required");
        }
        else if (normalized.Length > maximumLength)
        {
            errors.Add($"{field} cannot exceed {maximumLength} characters");
        }

        return normalized;
    }

    private static string? Optional(
        string? value,
        string field,
        int maximumLength,
        ICollection<string> errors)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            errors.Add($"{field} cannot exceed {maximumLength} characters");
        }

        return normalized.Length == 0 ? null : normalized;
    }

    private static PluginManifestException Invalid(string error) => new([error]);

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62})(?:\.[a-z0-9](?:[a-z0-9-]{0,62})){1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();

    [GeneratedRegex(@"^(?:\*\.)?(?:[a-z0-9](?:[a-z0-9-]{0,62})\.)+[a-z0-9](?:[a-z0-9-]{0,62})$", RegexOptions.CultureInvariant)]
    private static partial Regex HttpDomainPattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._:-]{0,254}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeNamePattern();

}
