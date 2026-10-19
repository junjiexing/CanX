// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;

namespace CanX.Plugins.Ui;

internal static class PluginUiSchema
{
    private const int MaximumDepth = 8;
    private const int MaximumNodes = 256;
    private const int MaximumPageBytes = 4 * 1024 * 1024;
    private const int MaximumUpdatesPerCall = 64;
    private const int MaximumInlineImageBytes = 128 * 1024;
    private static readonly IReadOnlyDictionary<PluginUiContributionKind, IReadOnlySet<string>> Slots =
        new Dictionary<PluginUiContributionKind, IReadOnlySet<string>>
        {
            [PluginUiContributionKind.Page] = Set("navigation.plugins"),
            [PluginUiContributionKind.Panel] = Set("home.panel", "downloads.details", "playback.sidebar"),
            [PluginUiContributionKind.Command] = Set("command.palette"),
            [PluginUiContributionKind.Menu] = Set("menu.app", "menu.downloads", "menu.playback"),
            [PluginUiContributionKind.Setting] = Set("settings.plugins"),
            [PluginUiContributionKind.StatusCard] = Set("home.status", "downloads.status", "playback.status"),
        };

    internal static IReadOnlyDictionary<PluginUiContributionKind, IReadOnlySet<string>> SupportedSlots => Slots;
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ComponentProperties =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["stack"] = Set("orientation", "spacing", "horizontalAlignment"),
            ["viewStack"] = Set("active"),
            ["grid"] = Set("columns", "spacing"),
            ["scroll"] = Set("maxHeight"),
            ["card"] = Set("title"),
            ["separator"] = Set(),
            ["spacer"] = Set("size"),
            ["expander"] = Set("title", "expanded"),
            ["tabs"] = Set("selected"),
            ["tab"] = Set("label"),
            ["text"] = Set("text", "tone", "style", "wrap"),
            ["link"] = Set("label", "url"),
            ["icon"] = Set("name", "size", "tone"),
            ["image"] = Set("asset", "source", "alt", "width", "height"),
            ["badge"] = Set("text", "tone"),
            ["progress"] = Set("value", "label", "indeterminate"),
            ["spinner"] = Set("label"),
            ["alert"] = Set("title", "message", "tone"),
            ["emptyState"] = Set("title", "message", "icon"),
            ["keyValue"] = Set("items"),
            ["button"] = Set("label", "action", "variant", "disabled"),
            ["textInput"] = Set("label", "key", "placeholder", "default", "disabled", "password"),
            ["searchInput"] = Set("label", "key", "placeholder", "default", "disabled"),
            ["textArea"] = Set("label", "key", "placeholder", "default", "rows", "disabled"),
            ["numberInput"] = Set("label", "key", "placeholder", "default", "min", "max", "step", "disabled"),
            ["toggle"] = Set("label", "key", "default", "disabled"),
            ["select"] = Set("label", "key", "options", "default", "disabled"),
            ["radioGroup"] = Set("label", "key", "options", "default", "disabled"),
            ["slider"] = Set("label", "key", "default", "min", "max", "step", "disabled"),
            ["list"] = Set("items", "emptyText", "action", "actionLabel"),
            ["gallery"] = Set("items", "emptyText", "action", "itemWidth", "imageHeight", "spacing"),
            ["dataTable"] = Set("columns", "rows", "emptyText", "action"),
        };
    internal static IReadOnlySet<string> SupportedComponentTypes { get; } =
        new HashSet<string>(ComponentProperties.Keys, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> LayoutProperties = Set(
        "column", "columnSpan", "row", "rowSpan", "horizontalAlignment", "verticalAlignment", "width");
    private static readonly IReadOnlySet<string> ContributionComponentTypes = Set(
        "stack", "text", "button", "badge", "progress", "icon", "separator", "toggle", "select", "textInput");
    private static readonly IReadOnlySet<string> Tones = Set(
        "default", "muted", "accent", "success", "warning", "error");
    private static readonly IReadOnlySet<string> TextStyles = Set("body", "caption", "title", "heading", "mono");
    private static readonly IReadOnlySet<string> Spacing = Set("none", "xs", "sm", "md", "lg");
    private static readonly IReadOnlySet<string> Variants = Set("primary", "secondary", "quiet", "danger");
    private static readonly IReadOnlySet<string> IconSizes = Set("small", "medium", "large");
    private static readonly IReadOnlySet<string> Icons = new HashSet<string>(
        PluginManifestReader.BuiltInUiIcons.Concat(["pause", "check"]),
        StringComparer.Ordinal);

    public static PluginUiContribution Parse(
        string pluginId,
        string json,
        PluginPermissionSet permissions,
        bool hasHandler)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Invalid("UI contribution is not valid JSON.");
        }
        using (document)
        {
            var root = document.RootElement;
            DemandObject(root, "UI contribution");
            DemandProperties(root, "id", "title", "kind", "slot", "content", "interactive");
            var localId = RequireString(root, "id", 64);
            if (!PluginUiRegistry.IsValidLocalId(localId))
            {
                throw Invalid("UI contribution id must be a lowercase identifier of at most 64 characters.");
            }
            var title = RequireString(root, "title", 100);
            var kind = ParseKind(RequireString(root, "kind", 32));
            var slot = RequireString(root, "slot", 64);
            if (!Slots[kind].Contains(slot))
            {
                throw Invalid($"Slot '{slot}' is not valid for {kind} contributions.");
            }
            var requiresHandler = kind is PluginUiContributionKind.Command or PluginUiContributionKind.Menu;
            if (requiresHandler && !hasHandler)
            {
                throw Invalid($"{kind} contribution requires an invocation handler.");
            }

            if (!root.TryGetProperty("content", out var content))
            {
                throw Invalid("UI contribution requires content.");
            }
            var nodeCount = 0;
            ValidateNode(
                content,
                kind,
                isManifestPage: false,
                parentType: null,
                depth: 1,
                ref nodeCount,
                permissions);
            return new PluginUiContribution(
                $"{pluginId}/{localId}",
                pluginId,
                localId,
                title,
                kind,
                slot,
                content.Clone(),
                hasHandler);
        }
    }

    public static JsonElement ParsePageContent(string json, PluginPermissionSet permissions)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPageBytes)
        {
            throw Invalid($"Plugin page content exceeds {MaximumPageBytes} bytes.");
        }
        using var document = ParseObject(json, "Plugin page content");
        var nodeCount = 0;
        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        ValidateNode(
            document.RootElement,
            PluginUiContributionKind.Page,
            isManifestPage: true,
            parentType: null,
            depth: 1,
            ref nodeCount,
            permissions,
            componentIds);
        return document.RootElement.Clone();
    }

    public static JsonElement ApplyPageUpdates(
        JsonElement content,
        string json,
        PluginPermissionSet permissions,
        out IReadOnlyList<string> updatedComponentIds)
    {
        JsonDocument updatesDocument;
        try
        {
            updatesDocument = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Invalid("Plugin page update is not valid JSON.");
        }
        using (updatesDocument)
        {
            var updates = updatesDocument.RootElement;
            if (updates.ValueKind != JsonValueKind.Array ||
                updates.GetArrayLength() is < 1 or > MaximumUpdatesPerCall)
            {
                throw Invalid($"Plugin page update must contain 1 to {MaximumUpdatesPerCall} patches.");
            }
            var root = JsonNode.Parse(content.GetRawText()) as JsonObject
                ?? throw Invalid("Plugin page content is invalid.");
            var targets = IndexNodes(root);
            var updatedTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var update in updates.EnumerateArray())
            {
                DemandObject(update, "Plugin page patch");
                DemandProperties(update, "target", "props");
                var target = RequireString(update, "target", 64);
                if (!updatedTargets.Add(target))
                {
                    throw Invalid($"Plugin page patch target '{target}' is duplicated.");
                }
                if (!targets.TryGetValue(target, out var node))
                {
                    throw Invalid($"Plugin page component '{target}' was not found.");
                }
                if (!update.TryGetProperty("props", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid("Plugin page patch props must be an object.");
                }
                var targetProperties = node["props"] as JsonObject;
                if (targetProperties is null)
                {
                    targetProperties = [];
                    node["props"] = targetProperties;
                }
                foreach (var property in properties.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        targetProperties.Remove(property.Name);
                    }
                    else
                    {
                        targetProperties[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    }
                }
            }
            updatedComponentIds = updatedTargets.ToArray();
            return ParsePageContent(root.ToJsonString(), permissions);
        }
    }

    private static Dictionary<string, JsonObject> IndexNodes(JsonObject root)
    {
        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var pending = new Stack<JsonObject>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id))
            {
                nodes.Add(id, node);
            }
            if (node["children"] is JsonArray children)
            {
                foreach (var child in children.OfType<JsonObject>())
                {
                    pending.Push(child);
                }
            }
        }
        return nodes;
    }

    private static void ValidateNode(
        JsonElement node,
        PluginUiContributionKind contributionKind,
        bool isManifestPage,
        string? parentType,
        int depth,
        ref int nodeCount,
        PluginPermissionSet permissions,
        HashSet<string>? componentIds = null)
    {
        DemandObject(node, "UI component");
        if (depth > MaximumDepth || ++nodeCount > MaximumNodes)
        {
            throw Invalid("UI component tree exceeds its depth or node limit.");
        }
        DemandProperties(node, "id", "type", "props", "children", "layout");
        var type = RequireString(node, "type", 32);
        if (!ComponentProperties.TryGetValue(type, out var allowedProperties))
        {
            throw Invalid($"UI component type '{type}' is not supported.");
        }
        if (!isManifestPage && !ContributionComponentTypes.Contains(type))
        {
            throw Invalid($"UI component '{type}' is only allowed in manifest plugin pages.");
        }
        if (type == "tab" && parentType != "tabs")
        {
            throw Invalid("Tab components may only be direct children of tabs.");
        }
        if (parentType == "tabs" && type != "tab")
        {
            throw Invalid("Tabs may only contain tab components.");
        }
        if (!isManifestPage && contributionKind != PluginUiContributionKind.Setting &&
            type is "toggle" or "select" or "textInput" or "searchInput" or "textArea" or
                "numberInput" or "radioGroup" or "slider")
        {
            throw Invalid($"UI component '{type}' is only allowed in settings or page contributions.");
        }

        if (node.TryGetProperty("id", out var idElement))
        {
            if (idElement.ValueKind != JsonValueKind.String ||
                !PluginUiRegistry.IsValidLocalId(idElement.GetString() ?? string.Empty))
            {
                throw Invalid("UI component id must be a lowercase identifier of at most 64 characters.");
            }
            if (componentIds is not null && !componentIds.Add(idElement.GetString()!))
            {
                throw Invalid($"UI component id '{idElement.GetString()}' is duplicated.");
            }
        }

        if (node.TryGetProperty("props", out var properties))
        {
            DemandObject(properties, "UI component props");
            foreach (var property in properties.EnumerateObject())
            {
                if (!allowedProperties.Contains(property.Name))
                {
                    throw Invalid($"Property '{property.Name}' is not supported by UI component '{type}'.");
                }
                ValidateProperty(type, property, permissions);
            }
            if (type == "image" &&
                properties.TryGetProperty("asset", out _) &&
                properties.TryGetProperty("source", out _))
            {
                throw Invalid("Image components cannot specify both an asset and a source.");
            }
        }

        if (node.TryGetProperty("layout", out var layout))
        {
            DemandObject(layout, "UI component layout");
            foreach (var property in layout.EnumerateObject())
            {
                if (!LayoutProperties.Contains(property.Name))
                {
                    throw Invalid($"Layout property '{property.Name}' is not supported.");
                }
                ValidateProperty(type, property, permissions);
            }
        }

        if (node.TryGetProperty("children", out var children))
        {
            if (children.ValueKind != JsonValueKind.Array ||
                type is not ("stack" or "viewStack" or "grid" or "scroll" or "card" or "expander" or "tabs" or "tab"))
            {
                throw Invalid($"UI component '{type}' cannot contain children.");
            }
            if (type is "scroll" or "expander" && children.GetArrayLength() > 1)
            {
                throw Invalid($"UI component '{type}' accepts at most one child.");
            }
            foreach (var child in children.EnumerateArray())
            {
                ValidateNode(
                    child,
                    contributionKind,
                    isManifestPage,
                    type,
                    depth + 1,
                    ref nodeCount,
                    permissions,
                    componentIds);
            }
        }

        if (type == "viewStack")
        {
            if (!node.TryGetProperty("children", out var views) || views.GetArrayLength() == 0 ||
                !node.TryGetProperty("props", out var viewStackProperties))
            {
                throw Invalid("View stack components require props.active and at least one child view.");
            }
            var active = RequireString(viewStackProperties, "active", 64);
            var viewIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var view in views.EnumerateArray())
            {
                if (!view.TryGetProperty("id", out var viewId) || viewId.ValueKind != JsonValueKind.String)
                {
                    throw Invalid("Every direct child of a view stack must have an id.");
                }
                viewIds.Add(viewId.GetString()!);
            }
            if (!viewIds.Contains(active))
            {
                throw Invalid("View stack props.active must match a direct child id.");
            }
        }
    }

    private static void ValidateProperty(
        string componentType,
        JsonProperty property,
        PluginPermissionSet permissions)
    {
        switch (property.Name)
        {
            case "value" when componentType == "progress":
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetDouble(out var progress) || progress is < 0 or > 1)
                {
                    throw Invalid("Progress value must be between 0 and 1.");
                }
                break;
            case "default" when componentType == "toggle":
            case "expanded":
            case "disabled":
            case "indeterminate":
            case "password":
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Invalid($"UI property '{property.Name}' must be boolean.");
                }
                break;
            case "options":
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() is < 1 or > 50 ||
                    property.Value.EnumerateArray().Any(option =>
                        option.ValueKind != JsonValueKind.String || option.GetString()!.Length is < 1 or > 100))
                {
                    throw Invalid("Select options must contain 1 to 50 short strings.");
                }
                break;
            case "columns" when componentType == "grid":
                DemandInteger(property, 1, 12);
                break;
            case "selected":
            case "rows" when componentType == "textArea":
            case "column":
            case "row":
                DemandInteger(property, 0, 1000);
                break;
            case "columnSpan":
            case "rowSpan":
                DemandInteger(property, 1, 12);
                break;
            case "width":
            case "height":
            case "maxHeight":
                DemandNumber(property, 1, 4096);
                break;
            case "itemWidth":
                DemandNumber(property, 96, 360);
                break;
            case "imageHeight":
                DemandNumber(property, 96, 540);
                break;
            case "default" when componentType is "numberInput" or "slider":
            case "min":
            case "max":
            case "step":
                DemandNumber(property, -1_000_000_000, 1_000_000_000);
                break;
            case "items":
                ValidateItems(componentType, property.Value, permissions);
                break;
            case "columns":
                ValidateTableColumns(property.Value);
                break;
            case "rows":
                ValidateTableRows(property.Value);
                break;
            case "asset":
                ValidateRasterAsset(property.Value);
                break;
            case "source" when componentType == "image":
                ValidateRasterImageSource(property.Value, permissions);
                break;
            case "url" when componentType == "link":
                ValidateHttpsLink(property.Value);
                break;
            default:
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 1024)
                {
                    throw Invalid($"UI property '{property.Name}' must be a string of at most 1024 characters.");
                }
                ValidateToken(property.Name, property.Value.GetString()!);
                break;
        }
    }

    private static void ValidateToken(string property, string value)
    {
        var allowed = property switch
        {
            "tone" => Tones,
            "style" => TextStyles,
            "spacing" => Spacing,
            "variant" => Variants,
            "size" => IconSizes,
            "orientation" => Set("horizontal", "vertical"),
            "horizontalAlignment" => Set("left", "center", "right", "stretch"),
            "verticalAlignment" => Set("top", "center", "bottom", "stretch"),
            "name" => Icons,
            _ => null,
        };
        if (allowed is not null && !allowed.Contains(value))
        {
            throw Invalid($"UI token '{value}' is not allowed for '{property}'.");
        }
    }

    private static void DemandInteger(JsonProperty property, int minimum, int maximum)
    {
        if (!property.Value.TryGetInt32(out var value) || value < minimum || value > maximum)
        {
            throw Invalid($"UI property '{property.Name}' must be an integer from {minimum} to {maximum}.");
        }
    }

    private static void DemandNumber(JsonProperty property, double minimum, double maximum)
        => DemandNumber(property.Name, property.Value, minimum, maximum);

    private static void DemandNumber(string name, JsonElement element, double minimum, double maximum)
    {
        if (!element.TryGetDouble(out var value) || !double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw Invalid($"UI property '{name}' must be a finite number from {minimum} to {maximum}.");
        }
    }

    private static void ValidateItems(
        string componentType,
        JsonElement items,
        PluginPermissionSet permissions)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 1000)
        {
            throw Invalid($"UI component '{componentType}' items must be an array of at most 1000 entries.");
        }
        foreach (var item in items.EnumerateArray())
        {
            DemandObject(item, $"UI component '{componentType}' item");
            var allowed = componentType == "keyValue"
                ? Set("key", "value")
                : Set("id", "title", "description", "badge", "image");
            DemandProperties(item, [.. allowed]);
            foreach (var property in item.EnumerateObject())
            {
                if (componentType is "list" or "gallery" && property.Name == "image")
                {
                    ValidateRasterImageSource(property.Value, permissions);
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 1024)
                {
                    throw Invalid($"UI component '{componentType}' item values must be short strings.");
                }
            }
            if (!item.TryGetProperty(componentType == "keyValue" ? "key" : "id", out _) ||
                !item.TryGetProperty(componentType == "keyValue" ? "value" : "title", out _))
            {
                throw Invalid($"UI component '{componentType}' item is missing required fields.");
            }
        }
    }

    private static void ValidateRasterImageSource(
        JsonElement image,
        PluginPermissionSet permissions)
    {
        if (image.ValueKind != JsonValueKind.String || image.GetString() is not { } value)
        {
            throw Invalid("Image source must be a base64 PNG/JPEG/WebP data URI or an authorized HTTP(S) URL.");
        }
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            ValidateInlineRasterImage(image);
            return;
        }
        if (value.Length is < 1 or > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.IdnHost) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Invalid("Remote image source must be an absolute HTTP(S) URL without embedded credentials.");
        }

        permissions.DemandRemoteImage(uri);
    }

    private static void ValidateInlineRasterImage(JsonElement image)
    {
        if (image.ValueKind != JsonValueKind.String || image.GetString() is not { } value)
        {
            throw Invalid("List item image must be a base64 PNG, JPEG, or WebP data URI.");
        }

        var prefixes = new[]
        {
            "data:image/png;base64,",
            "data:image/jpeg;base64,",
            "data:image/webp;base64,",
        };
        var prefix = prefixes.FirstOrDefault(value.StartsWith);
        if (prefix is null || value.Length > MaximumInlineImageBytes * 4 / 3 + 64)
        {
            throw Invalid("List item image must be a base64 PNG, JPEG, or WebP data URI.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value[prefix.Length..]);
        }
        catch (FormatException)
        {
            throw Invalid("List item image contains invalid base64 data.");
        }
        if (bytes.Length is 0 or > MaximumInlineImageBytes || !HasExpectedImageSignature(prefix, bytes))
        {
            throw Invalid($"List item image must contain at most {MaximumInlineImageBytes} bytes of matching raster data.");
        }
    }

    private static void ValidateHttpsLink(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length is < 1 or > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw Invalid("Link URL must be an absolute HTTPS URL of at most 2048 characters.");
        }
    }

    private static bool HasExpectedImageSignature(string prefix, ReadOnlySpan<byte> bytes) => prefix switch
    {
        "data:image/png;base64," => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "data:image/jpeg;base64," => bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "data:image/webp;base64," => bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };

    private static void ValidateTableColumns(JsonElement columns)
    {
        if (columns.ValueKind != JsonValueKind.Array || columns.GetArrayLength() is < 1 or > 20)
        {
            throw Invalid("Data table columns must contain 1 to 20 entries.");
        }
        foreach (var column in columns.EnumerateArray())
        {
            DemandObject(column, "Data table column");
            DemandProperties(column, "key", "title", "width");
            RequireString(column, "key", 64);
            RequireString(column, "title", 100);
            if (column.TryGetProperty("width", out var width))
            {
                DemandNumber("width", width, 40, 1000);
            }
        }
    }

    private static void ValidateTableRows(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1000)
        {
            throw Invalid("Data table rows must be an array of at most 1000 entries.");
        }
        foreach (var row in rows.EnumerateArray())
        {
            DemandObject(row, "Data table row");
            foreach (var property in row.EnumerateObject())
            {
                if (property.Value.ValueKind is not (
                        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or
                        JsonValueKind.False or JsonValueKind.Null) ||
                    property.Name.Length > 64 || property.Value.GetRawText().Length > 1024)
                {
                    throw Invalid("Data table cells must contain short scalar values.");
                }
            }
        }
    }

    private static void ValidateRasterAsset(JsonElement asset)
    {
        if (asset.ValueKind != JsonValueKind.String || asset.GetString() is not { } path ||
            path.Length is < 1 or > 256 || path.Contains('\\') || path.StartsWith('/') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            throw Invalid("Image asset must be a portable package-relative PNG, JPEG, or WebP path.");
        }
    }

    private static PluginUiContributionKind ParseKind(string kind) => kind switch
    {
        "page" => PluginUiContributionKind.Page,
        "panel" => PluginUiContributionKind.Panel,
        "command" => PluginUiContributionKind.Command,
        "menu" => PluginUiContributionKind.Menu,
        "setting" => PluginUiContributionKind.Setting,
        "statusCard" => PluginUiContributionKind.StatusCard,
        _ => throw Invalid($"UI contribution kind '{kind}' is not supported."),
    };

    private static void DemandObject(JsonElement element, string subject)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{subject} must be an object.");
        }
    }

    private static JsonDocument ParseObject(string json, string subject)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Invalid($"{subject} must be an object.");
            }
            return document;
        }
        catch (JsonException)
        {
            throw Invalid($"{subject} is not valid JSON.");
        }
    }

    private static void DemandProperties(JsonElement element, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Invalid($"Property '{property.Name}' is not supported in declarative UI.");
            }
        }
    }

    private static string RequireString(JsonElement element, string property, int maximumLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximumLength)
        {
            throw Invalid($"UI property '{property}' must be a string of at most {maximumLength} characters.");
        }
        return value.GetString()!;
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static PluginUiException Invalid(string message) => new("invalid_ui_contribution", message);
}
