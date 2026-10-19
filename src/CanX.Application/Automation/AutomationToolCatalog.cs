// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Tooling;

namespace CanX.Application.Automation;

public interface IMediaAutomationService
{
    ValueTask<JsonArray> ListSourcesAsync(CancellationToken cancellationToken);

    ValueTask<JsonObject> AddSourceAsync(
        string name,
        string protocol,
        string endpoint,
        CancellationToken cancellationToken);

    ValueTask<bool> RemoveSourceAsync(string sourceId, CancellationToken cancellationToken);

    ValueTask<JsonObject> BrowseAsync(string sourceId, string? location, CancellationToken cancellationToken);

    ValueTask PlayAsync(string sourceId, string location, string? subtitleLocation, CancellationToken cancellationToken);
}

public interface IPluginAutomationService
{
    JsonArray List();

    ValueTask EnableAsync(string pluginId, CancellationToken cancellationToken);

    ValueTask DisableAsync(string pluginId, CancellationToken cancellationToken);

    ValueTask ReloadAsync(string pluginId, CancellationToken cancellationToken);

    ValueTask RollbackAsync(string pluginId, CancellationToken cancellationToken);

    ValueTask UninstallAsync(string pluginId, CancellationToken cancellationToken);
}

public interface ISettingsAutomationService
{
    JsonObject Get(string section);

    ValueTask<JsonObject> SetAsync(string section, JsonObject values, CancellationToken cancellationToken);
}

public interface IDiagnosticsAutomationService
{
    JsonObject GetSnapshot();

    ValueTask<JsonArray> ReadLogAsync(string component, int maximumLines, CancellationToken cancellationToken);
}

public interface IUiAutomationService
{
    JsonObject GetContext();

    ValueTask NavigateAsync(string page, CancellationToken cancellationToken);

    ValueTask NotifyAsync(string message, string severity, CancellationToken cancellationToken);
}

public static class AutomationToolCatalog
{
    public static IReadOnlyList<IToolHandler> Create(
        IMediaAutomationService media,
        IPluginAutomationService plugins,
        ISettingsAutomationService settings,
        IDiagnosticsAutomationService diagnostics,
        IUiAutomationService ui,
        ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(registry);
        return
        [
            Tool("media.sources.list", "List configured local and network media sources.", ToolRisk.ReadOnly, Empty(),
                async (_, _, token) => ToolResult.Success(await media.ListSourcesAsync(token).ConfigureAwait(false)), ArrayOutput()),
            Tool("media.sources.add", "Add and validate a local folder or anonymous network media source. Credentials must be entered by the user in the host UI.", ToolRisk.Network,
                AddMediaSourceSchema(), async (args, _, token) => ToolResult.Success(await media.AddSourceAsync(
                    RequiredString(args, "name"),
                    RequiredString(args, "protocol"),
                    RequiredString(args, "endpoint"),
                    token).ConfigureAwait(false))),
            Tool("media.sources.remove", "Remove a configured media source and its stored credential reference.", ToolRisk.Destructive,
                SourceIdSchema(), async (args, _, token) => ToolResult.Success(new JsonObject
                {
                    ["removed"] = await media.RemoveSourceAsync(RequiredString(args, "sourceId"), token).ConfigureAwait(false),
                })),
            Tool("media.browse", "Browse one configured media source without exposing its credentials.", ToolRisk.Network,
                ObjectSchema([("sourceId", "string", true), ("location", "string", false)]),
                async (args, _, token) => ToolResult.Success(await media.BrowseAsync(
                    RequiredString(args, "sourceId"), OptionalString(args, "location"), token).ConfigureAwait(false))),
            Tool("media.play", "Play an item from a configured media source and optionally attach a subtitle.", ToolRisk.Network,
                ObjectSchema([("sourceId", "string", true), ("location", "string", true), ("subtitleLocation", "string", false)]),
                async (args, _, token) =>
                {
                    await media.PlayAsync(
                        RequiredString(args, "sourceId"),
                        RequiredString(args, "location"),
                        OptionalString(args, "subtitleLocation"),
                        token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            Tool("plugins.list", "List installed plugins, permissions, signatures, and runtime state.", ToolRisk.ReadOnly, Empty(),
                (args, context, token) => ValueTask.FromResult(ToolResult.Success(plugins.List())), ArrayOutput()),
            Tool("plugins.enable", "Approve the currently declared permissions and enable a plugin.", ToolRisk.Destructive,
                IdSchema(), async (args, _, token) =>
                {
                    await plugins.EnableAsync(RequiredString(args, "id"), token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            Tool("plugins.disable", "Disable a plugin.", ToolRisk.LocalWrite, IdSchema(), async (args, _, token) =>
                {
                    await plugins.DisableAsync(RequiredString(args, "id"), token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            Tool("plugins.reload", "Reload an enabled plugin runtime.", ToolRisk.LocalWrite, IdSchema(), async (args, _, token) =>
                {
                    await plugins.ReloadAsync(RequiredString(args, "id"), token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            Tool("plugins.rollback", "Roll a plugin back to its previous installed version.", ToolRisk.Destructive, IdSchema(), async (args, _, token) =>
                {
                    await plugins.RollbackAsync(RequiredString(args, "id"), token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            Tool("plugins.uninstall", "Uninstall a plugin without deleting its isolated data or credentials.", ToolRisk.Destructive, IdSchema(), async (args, _, token) =>
                {
                    await plugins.UninstallAsync(RequiredString(args, "id"), token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            Tool("settings.get", "Read an automation-safe settings section. Secrets are never returned.", ToolRisk.ReadOnly,
                ObjectSchema([("section", "string", true)]),
                (args, _, _) => ValueTask.FromResult(ToolResult.Success(settings.Get(RequiredString(args, "section"))))),
            Tool("settings.set", "Update an automation-safe settings section. Secrets, the LLM base URL, the download directory, and raw mpv options are user-only.", ToolRisk.LocalWrite,
                SettingsSchema(),
                async (args, _, token) => ToolResult.Success(await settings.SetAsync(
                    RequiredString(args, "section"),
                    (JsonObject)(args["values"]?.DeepClone() ?? new JsonObject()),
                    token).ConfigureAwait(false))),
            Tool("diagnostics.get", "Read a redacted application diagnostics snapshot.", ToolRisk.ReadOnly, Empty(),
                (args, context, token) => ValueTask.FromResult(ToolResult.Success(diagnostics.GetSnapshot()))),
            Tool("diagnostics.logs", "Read a bounded tail of one redacted component log.", ToolRisk.ReadOnly,
                DiagnosticsLogSchema(),
                async (args, _, token) => ToolResult.Success(await diagnostics.ReadLogAsync(
                    RequiredString(args, "component"),
                    args["maximumLines"]?.GetValue<int>() ?? 100,
                    token).ConfigureAwait(false)), ArrayOutput()),
            Tool("diagnostics.tools", "List Tool coverage, schemas, risks, and execution capabilities.", ToolRisk.ReadOnly, Empty(),
                (args, context, token) => ValueTask.FromResult(ToolResult.Success(ToolCoverage(registry)))),
            Tool("ui.context", "Read the current top-level UI context without inspecting arbitrary controls.", ToolRisk.ReadOnly, Empty(),
                (args, context, token) => ValueTask.FromResult(ToolResult.Success(ui.GetContext()))),
            Tool("ui.navigate", "Navigate the main window to a named CanX page.", ToolRisk.LocalWrite,
                ObjectSchema([("page", "string", true)]), async (args, _, token) =>
                {
                    await ui.NavigateAsync(RequiredString(args, "page"), token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            Tool("ui.notify", "Show a bounded non-modal notification to the user.", ToolRisk.LocalWrite,
                UiNotificationSchema(), async (args, _, token) =>
                {
                    await ui.NotifyAsync(
                        RequiredString(args, "message"),
                        OptionalString(args, "severity") ?? "info",
                        token).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
        ];
    }

    private static JsonObject ToolCoverage(ToolRegistry registry) => new()
    {
        ["schemaVersion"] = 1,
        ["total"] = registry.Descriptors.Count,
        ["confirmationRequired"] = registry.Descriptors.Count(static descriptor => descriptor.Risk != ToolRisk.ReadOnly),
        ["namespaces"] = new JsonObject(registry.Descriptors
            .GroupBy(static descriptor => descriptor.Name.Split('.')[0], StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new KeyValuePair<string, JsonNode?>(group.Key, JsonValue.Create(group.Count())))),
        ["risks"] = new JsonObject(registry.Descriptors
            .GroupBy(static descriptor => descriptor.Risk.ToString(), StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new KeyValuePair<string, JsonNode?>(group.Key, JsonValue.Create(group.Count())))),
        ["tools"] = new JsonArray(registry.Descriptors.Select(descriptor => (JsonNode?)new JsonObject
        {
            ["name"] = descriptor.Name,
            ["description"] = descriptor.Description,
            ["version"] = descriptor.Version,
            ["risk"] = descriptor.Risk.ToString(),
            ["requiresConfirmation"] = descriptor.Risk != ToolRisk.ReadOnly,
            ["cancellable"] = descriptor.IsCancellable,
            ["dryRun"] = descriptor.SupportsDryRun,
            ["inputSchema"] = descriptor.InputSchema.DeepClone(),
            ["outputSchema"] = descriptor.OutputSchema.DeepClone(),
        }).ToArray()),
    };

    private static IToolHandler Tool(
        string name,
        string description,
        ToolRisk risk,
        JsonObject inputSchema,
        Func<JsonObject, ToolCallContext, CancellationToken, ValueTask<ToolResult>> execute,
        JsonObject? outputSchema = null) =>
        new DelegateTool(new ToolDescriptor(
            name,
            description,
            risk,
            inputSchema,
            outputSchema ?? new JsonObject { ["type"] = "object" }), execute);

    private static JsonObject Empty() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
    };

    private static JsonObject ArrayOutput() => new() { ["type"] = "array" };

    private static JsonObject IdSchema() => ObjectSchema([("id", "string", true)]);

    private static JsonObject SourceIdSchema() => ObjectSchema([("sourceId", "string", true)]);

    private static JsonObject AddMediaSourceSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("name", "protocol", "endpoint"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 100 },
            ["protocol"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("local", "webdav", "ftp", "smb"),
            },
            ["endpoint"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 },
        },
    };

    private static JsonObject ObjectSchema(IReadOnlyList<(string Name, string Type, bool Required)> fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Name] = new JsonObject { ["type"] = field.Type, ["maxLength"] = 4096 };
            if (field.Required) required.Add((JsonNode?)JsonValue.Create(field.Name));
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = required,
            ["additionalProperties"] = false,
            ["properties"] = properties,
        };
    }

    private static JsonObject SettingsSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("section", "values"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["section"] = new JsonObject { ["type"] = "string", ["maxLength"] = 64 },
            ["values"] = new JsonObject { ["type"] = "object", ["maxProperties"] = 128 },
        },
    };

    private static JsonObject DiagnosticsLogSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("component"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["component"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("app", "aria2", "mpv", "quickjs", "tool-audit", "plugin-audit"),
            },
            ["maximumLines"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500 },
        },
    };

    private static JsonObject UiNotificationSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("message"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["message"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 500 },
            ["severity"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("info", "success", "warning", "error"),
            },
        },
    };

    private static string RequiredString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"'{name}' is required.");

    private static string? OptionalString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>();

    private sealed class DelegateTool(
        ToolDescriptor descriptor,
        Func<JsonObject, ToolCallContext, CancellationToken, ValueTask<ToolResult>> execute) : IToolHandler
    {
        public ToolDescriptor Descriptor { get; } = descriptor;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken) => execute(arguments, context, cancellationToken);
    }
}
