// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using CanX.Plugins.Services;
using CanX.Tooling;

namespace CanX.Application.Plugins;

public static class PluginServiceToolCatalog
{
    private static readonly IReadOnlySet<string> ProtectedSearchContracts = new HashSet<string>(StringComparer.Ordinal)
    {
        "org.canx.media.search",
        "org.canx.media.resolve",
        "org.canx.subtitle.search",
        "org.canx.bittorrent.search",
    };

    public static IReadOnlyList<IToolHandler> Create(PluginServiceRegistry services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return
        [
            new DelegateTool(
                new ToolDescriptor(
                    "services.list",
                    "List plugin services exposed to AI, optionally filtered by contract and major version.",
                    ToolRisk.ReadOnly,
                    QuerySchema(),
                    new JsonObject { ["type"] = "object" }),
                (arguments, _, _) => ValueTask.FromResult(ToolResult.Success(List(services, arguments)))),
            new DelegateTool(
                new ToolDescriptor(
                    "services.describe",
                    "Get the complete JSON schemas and metadata for one AI-accessible plugin service.",
                    ToolRisk.ReadOnly,
                    ServiceIdSchema(),
                    new JsonObject { ["type"] = "object" }),
                (arguments, _, _) => ValueTask.FromResult(Describe(services, arguments))),
            new DelegateTool(
                new ToolDescriptor(
                    "services.invoke",
                    "Invoke an AI-accessible plugin service with JSON input validated against its schema.",
                    ToolRisk.Network,
                    InvokeSchema(),
                    new JsonObject()),
                async (arguments, context, cancellationToken) =>
                {
                    var id = RequiredString(arguments, "serviceId");
                    var descriptor = services.GetDescriptor(id);
                    if (!descriptor.Audiences.Contains(PluginServiceAudience.Ai))
                    {
                        return ToolResult.Failure("service_not_visible", "Service is not exposed to AI.");
                    }
                    if (ProtectedSearchContracts.Contains(descriptor.Contract))
                    {
                        return ToolResult.Failure(
                            "use_search_tools",
                            "Standard search and resolver services must be used through the cached search.* Tools.");
                    }
                    var input = CloneElement(arguments["input"]);
                    var result = await services.InvokeAsync(
                        id,
                        input,
                        PluginServiceCallContext.ForAi(context.Caller),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(JsonNode.Parse(result.GetRawText()));
                }),
        ];
    }

    private static JsonObject List(PluginServiceRegistry registry, JsonObject arguments)
    {
        var contract = arguments["contract"]?.GetValue<string>();
        var majorVersion = arguments["majorVersion"]?.GetValue<int>();
        return new JsonObject
        {
            ["services"] = new JsonArray(registry.Find(contract, majorVersion, PluginServiceAudience.Ai)
                .Select(service => (JsonNode?)Summary(service)).ToArray()),
        };
    }

    private static ToolResult Describe(PluginServiceRegistry registry, JsonObject arguments)
    {
        var descriptor = registry.GetDescriptor(RequiredString(arguments, "serviceId"));
        return descriptor.Audiences.Contains(PluginServiceAudience.Ai)
            ? ToolResult.Success(Descriptor(descriptor))
            : ToolResult.Failure("service_not_visible", "Service is not exposed to AI.");
    }

    private static JsonObject Summary(PluginServiceDescriptor descriptor) => new()
    {
        ["id"] = descriptor.Id,
        ["contract"] = descriptor.Contract,
        ["contractVersion"] = descriptor.ContractVersion,
        ["title"] = descriptor.Title,
        ["description"] = descriptor.Description,
        ["risk"] = Risk(descriptor.Risk),
    };

    private static JsonObject Descriptor(PluginServiceDescriptor descriptor)
    {
        var result = Summary(descriptor);
        result["pluginId"] = descriptor.PluginId;
        result["localId"] = descriptor.LocalId;
        result["audiences"] = new JsonArray(descriptor.Audiences.Select(audience =>
            (JsonNode?)JsonValue.Create(audience == PluginServiceAudience.Plugin ? "plugin" : "ai")).ToArray());
        result["inputSchema"] = JsonNode.Parse(descriptor.InputSchema.GetRawText());
        result["outputSchema"] = JsonNode.Parse(descriptor.OutputSchema.GetRawText());
        return result;
    }

    private static string Risk(PluginServiceRisk risk) => risk switch
    {
        PluginServiceRisk.ReadOnly => "readOnly",
        PluginServiceRisk.Network => "network",
        PluginServiceRisk.LocalWrite => "localWrite",
        PluginServiceRisk.Destructive => "destructive",
        _ => throw new ArgumentOutOfRangeException(nameof(risk)),
    };

    private static JsonElement CloneElement(JsonNode? node)
    {
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private static string RequiredString(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"'{name}' is required.")
            : value;
    }

    private static JsonObject QuerySchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["contract"] = new JsonObject { ["type"] = "string" },
            ["majorVersion"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
        },
    };

    private static JsonObject ServiceIdSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("serviceId"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["serviceId"] = new JsonObject { ["type"] = "string" },
        },
    };

    private static JsonObject InvokeSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("serviceId", "input"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["serviceId"] = new JsonObject { ["type"] = "string" },
            ["input"] = new JsonObject(),
        },
    };

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
