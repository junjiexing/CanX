// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Core.Downloads;
using CanX.Tooling;

namespace CanX.Application.Downloads;

public static class DownloadToolCatalog
{
    /// <param name="downloadRoot">
    /// When provided, Tool callers can only place downloads inside this directory (the user's configured
    /// download folder). The user-facing add dialog is not affected.
    /// </param>
    public static IReadOnlyList<IToolHandler> Create(IDownloadManager manager, Func<string>? downloadRoot = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        AddDownloadRequest ParseRequest(JsonObject arguments) => ConstrainDestination(ParseToolRequest(arguments), downloadRoot);
        return
        [
            new DelegateTool(
                Descriptor(
                    "downloads.list",
                    "List download tasks.",
                    ToolRisk.ReadOnly,
                    EmptyObjectSchema(),
                    TypeSchema("array")),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(ToSummaryJson(manager.Tasks)))),
            new DelegateTool(
                Descriptor(
                    "downloads.get",
                    "Read complete task, file, torrent, piece, error, and engine diagnostics for a download.",
                    ToolRisk.ReadOnly,
                    TaskIdSchema()),
                (arguments, _, _) =>
                {
                    var id = RequireId(arguments);
                    var task = manager.Tasks.FirstOrDefault(item => item.Id == id)
                        ?? throw new KeyNotFoundException($"Download task '{id}' was not found.");
                    return ValueTask.FromResult(ToolResult.Success(ToJson(task)));
                }),
            new DelegateTool(
                Descriptor("downloads.global.get", "Read global download status and options.", ToolRisk.ReadOnly, EmptyObjectSchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(ToJson(manager.GlobalStatus)))),
            new DelegateTool(
                Descriptor("downloads.capabilities", "List compiled aria2 features and option metadata.", ToolRisk.ReadOnly, EmptyObjectSchema()),
                (_, _, _) => ValueTask.FromResult(ToolResult.Success(ToJson(manager.Capabilities)))),
            new DelegateTool(
                Descriptor(
                    "downloads.connections",
                    "List active peers and HTTP/FTP servers for a download.",
                    ToolRisk.ReadOnly,
                    TaskIdSchema()),
                async (arguments, _, cancellationToken) =>
                {
                    var details = await manager.GetLiveDetailsAsync(
                        RequireId(arguments),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(ToJson(details));
                }),
            new DelegateTool(
                Descriptor(
                    "downloads.add",
                    "Add one URI, torrent, or Metalink download.",
                    ToolRisk.Network,
                    AddSchema(),
                    TypeSchema("array")),
                async (arguments, _, cancellationToken) =>
                {
                    try
                    {
                        var tasks = await manager.AddAsync(ParseRequest(arguments), cancellationToken).ConfigureAwait(false);
                        return ToolResult.Success(ToJson(tasks));
                    }
                    catch (DuplicateDownloadException exception)
                    {
                        return DuplicateFailure(exception);
                    }
                }),
            new DelegateTool(
                Descriptor(
                    "downloads.addBatch",
                    "Add multiple URI downloads.",
                    ToolRisk.Network,
                    BatchSchema(),
                    TypeSchema("array")),
                async (arguments, _, cancellationToken) =>
                {
                    var sources = RequireArray(arguments, "sources");
                    try
                    {
                        var requests = sources.Select(sourceNode =>
                        {
                            var source = sourceNode?.GetValue<string>()
                                ?? throw new ArgumentException("Every batch source must be a string.");
                            var requestArguments = (JsonObject)arguments.DeepClone();
                            requestArguments.Remove("sources");
                            requestArguments["source"] = source;
                            return ParseRequest(requestArguments);
                        }).ToArray();
                        var added = await manager.AddBatchAsync(requests, cancellationToken).ConfigureAwait(false);
                        return ToolResult.Success(ToJson(added));
                    }
                    catch (DuplicateDownloadException exception)
                    {
                        return DuplicateFailure(exception);
                    }
                }),
            CreateTaskAction(
                manager,
                "downloads.pause",
                "Pause a download.",
                ToolRisk.LocalWrite,
                static (downloadManager, id, cancellationToken) => downloadManager.PauseAsync(id, cancellationToken: cancellationToken)),
            CreateTaskAction(
                manager,
                "downloads.resume",
                "Resume a paused or failed download.",
                ToolRisk.Network,
                static (downloadManager, id, cancellationToken) => downloadManager.ResumeAsync(id, cancellationToken)),
            new DelegateTool(
                Descriptor(
                    "downloads.remove",
                    "Remove a task and optionally its downloaded files.",
                    ToolRisk.Destructive,
                    TaskIdSchema(("deleteFiles", "boolean"), ("force", "boolean"))),
                async (arguments, _, cancellationToken) =>
                {
                    await manager.RemoveAsync(
                        RequireId(arguments),
                        ReadBoolean(arguments, "force"),
                        ReadBoolean(arguments, "deleteFiles"),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            new DelegateTool(
                Descriptor(
                    "downloads.options.set",
                    "Change runtime options for a download.",
                    ToolRisk.LocalWrite,
                    TaskIdSchema(("options", "object"))),
                async (arguments, _, cancellationToken) =>
                {
                    await manager.ChangeOptionsAsync(
                        RequireId(arguments),
                        ReadOptions(arguments),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            new DelegateTool(
                Descriptor(
                    "downloads.completion.set",
                    "Configure notification and safe post-download action for a task.",
                    ToolRisk.LocalWrite,
                    TaskIdSchema(("notify", "boolean"), ("action", "string"))),
                async (arguments, _, cancellationToken) =>
                {
                    await manager.ChangeCompletionPolicyAsync(
                        RequireId(arguments),
                        ReadBoolean(arguments, "notify"),
                        ParseCompletionAction(arguments["action"]?.GetValue<string>()),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            new DelegateTool(
                Descriptor(
                    "downloads.schedule.set",
                    "Schedule a task group for a future UTC time, or clear its schedule.",
                    ToolRisk.LocalWrite,
                    TaskIdSchema(("startAt", "string"))),
                async (arguments, _, cancellationToken) =>
                {
                    await manager.ScheduleAsync(
                        RequireId(arguments),
                        ParseScheduledStart(arguments["startAt"]?.GetValue<string>()),
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success();
                }),
            new DelegateTool(
                Descriptor(
                    "downloads.global.set",
                    "Configure global aria2 options. Startup-only DHT options are persisted for the next engine start.",
                    ToolRisk.LocalWrite,
                    OptionsSchema()),
                async (arguments, _, cancellationToken) =>
                {
                    await manager.ChangeGlobalOptionsAsync(ReadOptions(arguments), cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(ToJson(manager.GlobalStatus));
                }),
            new DelegateTool(
                Descriptor(
                    "downloads.move",
                    "Move a waiting download in the queue.",
                    ToolRisk.LocalWrite,
                    TaskIdSchema(("offset", "integer"), ("mode", "string"))),
                async (arguments, _, cancellationToken) =>
                {
                    var modeText = arguments["mode"]?.GetValue<string>() ?? "set";
                    var mode = modeText.ToLowerInvariant() switch
                    {
                        "set" => DownloadPositionMode.Set,
                        "current" => DownloadPositionMode.Current,
                        "end" => DownloadPositionMode.End,
                        _ => throw new ArgumentException($"Unknown position mode '{modeText}'."),
                    };
                    var position = await manager.MoveAsync(
                        RequireId(arguments),
                        arguments["offset"]?.GetValue<int>() ?? 0,
                        mode,
                        cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success(new JsonObject { ["position"] = position });
                }),
        ];
    }

    private static IToolHandler CreateTaskAction(
        IDownloadManager manager,
        string name,
        string description,
        ToolRisk risk,
        Func<IDownloadManager, Guid, CancellationToken, ValueTask> action) =>
        new DelegateTool(
            Descriptor(name, description, risk, TaskIdSchema()),
            async (arguments, _, cancellationToken) =>
            {
                await action(manager, RequireId(arguments), cancellationToken).ConfigureAwait(false);
                return ToolResult.Success();
            });

    private static AddDownloadRequest ConstrainDestination(AddDownloadRequest request, Func<string>? downloadRoot)
    {
        if (downloadRoot is null)
        {
            return request;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadRoot()));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.DestinationDirectory, root));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(destination, root, comparison) &&
            !destination.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException(
                $"Tool downloads must be saved inside the configured download directory '{root}'.");
        }
        if (request.OutputName is { } outputName &&
            (outputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || outputName is "." or ".." ||
             outputName.Contains('/') || outputName.Contains('\\')))
        {
            throw new ArgumentException("Tool download output names must be plain file names.");
        }
        return request with { DestinationDirectory = destination };
    }

    private static AddDownloadRequest ParseToolRequest(JsonObject arguments)
    {
        var kindText = arguments["kind"]?.GetValue<string>() ?? "uri";
        var kind = kindText.ToLowerInvariant() switch
        {
            "uri" => DownloadSourceKind.Uri,
            "torrent" => DownloadSourceKind.Torrent,
            "metalink" => DownloadSourceKind.Metalink,
            _ => throw new ArgumentException($"Unknown download kind '{kindText}'."),
        };

        return new AddDownloadRequest
        {
            Kind = kind,
            Source = RequireString(arguments, "source"),
            DestinationDirectory = RequireString(arguments, "destinationDirectory"),
            OutputName = arguments["outputName"]?.GetValue<string>(),
            StartPaused = ReadBoolean(arguments, "startPaused"),
            AllowDuplicate = ReadBoolean(arguments, "allowDuplicate"),
            Position = arguments["position"]?.GetValue<int>() ?? -1,
            MirrorUris = ReadStrings(arguments, "mirrorUris"),
            Options = ReadOptions(arguments),
            NotifyOnCompletion = ReadBoolean(arguments, "notifyOnCompletion"),
            CompletionAction = ParseCompletionAction(arguments["completionAction"]?.GetValue<string>()),
            ScheduledStartAt = ParseScheduledStart(arguments["scheduledStartAt"]?.GetValue<string>()),
        };
    }

    private static DateTimeOffset? ParseScheduledStart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var startAt)
            ? startAt.ToUniversalTime()
            : throw new ArgumentException($"'{value}' is not a valid scheduled start time.");
    }

    private static DownloadCompletionAction ParseCompletionAction(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "" or "none" => DownloadCompletionAction.None,
            "openfile" or "open-file" => DownloadCompletionAction.OpenFile,
            "opendirectory" or "open-directory" => DownloadCompletionAction.OpenDirectory,
            _ => throw new ArgumentException($"Unknown completion action '{value}'."),
        };

    private static Dictionary<string, string> ReadOptions(JsonObject arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments["options"] is not JsonObject options)
        {
            return result;
        }

        foreach (var option in options)
        {
            result[option.Key] = option.Value?.GetValue<string>()
                ?? throw new ArgumentException($"Option '{option.Key}' must be a string.");
        }

        DownloadOptionPolicy.EnsureAutomationOptions(result.Keys);
        return result;
    }

    private static Guid RequireId(JsonObject arguments)
    {
        var value = RequireString(arguments, "id");
        return Guid.TryParse(value, out var id)
            ? id
            : throw new ArgumentException($"'{value}' is not a valid download ID.");
    }

    private static string RequireString(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"'{name}' is required.")
            : value;
    }

    private static JsonArray RequireArray(JsonObject arguments, string name) =>
        arguments[name] as JsonArray ?? throw new ArgumentException($"'{name}' must be an array.");

    private static bool ReadBoolean(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<bool>() ?? false;

    private static List<string> ReadStrings(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonArray values)
        {
            return [];
        }

        return values.Select(value => value?.GetValue<string>()
            ?? throw new ArgumentException($"Every '{name}' value must be a string.")).ToList();
    }

    private static ToolDescriptor Descriptor(
        string name,
        string description,
        ToolRisk risk,
        JsonObject inputSchema,
        JsonObject? outputSchema = null) =>
        new(name, description, risk, inputSchema, outputSchema ?? new JsonObject { ["type"] = "object" });

    private static JsonObject EmptyObjectSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
    };

    private static JsonObject AddSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("source", "destinationDirectory"),
        ["properties"] = new JsonObject
        {
            ["kind"] = TypeSchema("string"),
            ["source"] = TypeSchema("string"),
            ["destinationDirectory"] = TypeSchema("string"),
            ["outputName"] = TypeSchema("string"),
            ["startPaused"] = TypeSchema("boolean"),
            ["allowDuplicate"] = TypeSchema("boolean"),
            ["position"] = TypeSchema("integer"),
            ["mirrorUris"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = TypeSchema("string"),
            },
            ["options"] = TypeSchema("object"),
            ["notifyOnCompletion"] = TypeSchema("boolean"),
            ["completionAction"] = TypeSchema("string"),
            ["scheduledStartAt"] = TypeSchema("string"),
        },
    };

    private static ToolResult DuplicateFailure(DuplicateDownloadException exception) => ToolResult.Failure(
        "duplicate_download",
        $"{exception.Message} Existing task ID: {exception.ExistingTaskId:D}; state: {exception.ExistingState}.");

    private static JsonObject BatchSchema()
    {
        var schema = AddSchema();
        schema["required"] = new JsonArray("destinationDirectory", "sources");
        var properties = (JsonObject)schema["properties"]!;
        properties.Remove("source");
        properties["sources"] = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["items"] = TypeSchema("string"),
        };
        return schema;
    }

    private static JsonObject OptionsSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("options"),
        ["properties"] = new JsonObject { ["options"] = TypeSchema("object") },
    };

    private static JsonObject TaskIdSchema(params (string Name, string Type)[] additionalProperties)
    {
        var properties = new JsonObject { ["id"] = TypeSchema("string") };
        foreach (var property in additionalProperties)
        {
            properties[property.Name] = TypeSchema(property.Type);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("id"),
            ["properties"] = properties,
        };
    }

    private static JsonObject TypeSchema(string type) => new() { ["type"] = type };

    private static JsonArray ToJson(IReadOnlyList<DownloadTaskSnapshot> tasks) =>
        new(tasks.Select(snapshot => (JsonNode?)ToJson(snapshot)).ToArray());

    private static JsonArray ToSummaryJson(IReadOnlyList<DownloadTaskSnapshot> tasks) =>
        new(tasks.Select(snapshot => (JsonNode?)new JsonObject
        {
            ["id"] = snapshot.Id.ToString("D"),
            ["gid"] = snapshot.EngineGid,
            ["kind"] = snapshot.Kind.ToString(),
            ["source"] = RedactUri(snapshot.Source),
            ["name"] = snapshot.DisplayName,
            ["state"] = snapshot.State.ToString(),
            ["engineStatus"] = snapshot.EngineStatus,
            ["totalLength"] = snapshot.TotalLength,
            ["completedLength"] = snapshot.CompletedLength,
            ["downloadSpeed"] = snapshot.DownloadSpeed,
            ["uploadSpeed"] = snapshot.UploadSpeed,
            ["etaSeconds"] = JsonValue.Create(snapshot.EtaSeconds),
            ["connections"] = snapshot.Connections,
            ["errorCode"] = snapshot.ErrorCode,
            ["errorMessage"] = snapshot.ErrorMessage,
            ["queuePosition"] = snapshot.QueuePosition,
            ["options"] = RedactedOptions(snapshot.Options),
            ["notifyOnCompletion"] = snapshot.NotifyOnCompletion,
            ["completionAction"] = snapshot.CompletionAction.ToString(),
            ["scheduledStartAt"] = snapshot.ScheduledStartAt?.ToString("O"),
        }).ToArray());

    private static JsonObject ToJson(DownloadTaskSnapshot snapshot) => new()
    {
        ["id"] = snapshot.Id.ToString("D"),
        ["gid"] = snapshot.EngineGid,
        ["kind"] = snapshot.Kind.ToString(),
        ["source"] = RedactUri(snapshot.Source),
        ["mirrorUris"] = new JsonArray(snapshot.MirrorUris
            .Select(static uri => (JsonNode?)JsonValue.Create(RedactUri(uri))).ToArray()),
        ["destinationDirectory"] = snapshot.DestinationDirectory,
        ["outputName"] = snapshot.OutputName,
        ["name"] = snapshot.DisplayName,
        ["state"] = snapshot.State.ToString(),
        ["engineStatus"] = snapshot.EngineStatus,
        ["totalLength"] = snapshot.TotalLength,
        ["completedLength"] = snapshot.CompletedLength,
        ["uploadedLength"] = snapshot.UploadedLength,
        ["downloadSpeed"] = snapshot.DownloadSpeed,
        ["uploadSpeed"] = snapshot.UploadSpeed,
        ["etaSeconds"] = JsonValue.Create(snapshot.EtaSeconds),
        ["pieceLength"] = snapshot.PieceLength,
        ["pieceCount"] = snapshot.PieceCount,
        ["bitfield"] = snapshot.Bitfield,
        ["connections"] = snapshot.Connections,
        ["isSeeder"] = snapshot.IsSeeder,
        ["seederCount"] = snapshot.SeederCount,
        ["errorCode"] = snapshot.ErrorCode,
        ["errorMessage"] = snapshot.ErrorMessage,
        ["infoHash"] = snapshot.InfoHash,
        ["followedBy"] = new JsonArray(snapshot.FollowedBy
            .Select(static gid => (JsonNode?)JsonValue.Create(gid)).ToArray()),
        ["following"] = snapshot.Following,
        ["belongsTo"] = snapshot.BelongsTo,
        ["queuePosition"] = snapshot.QueuePosition,
        ["createdAt"] = snapshot.CreatedAt.ToString("O"),
        ["updatedAt"] = snapshot.UpdatedAt.ToString("O"),
        ["options"] = RedactedOptions(snapshot.Options),
        ["notifyOnCompletion"] = snapshot.NotifyOnCompletion,
        ["completionAction"] = snapshot.CompletionAction.ToString(),
        ["scheduledStartAt"] = snapshot.ScheduledStartAt?.ToString("O"),
        ["files"] = new JsonArray(snapshot.Files.Select(file => (JsonNode?)new JsonObject
        {
            ["index"] = file.Index,
            ["path"] = file.Path,
            ["length"] = file.Length,
            ["completedLength"] = file.CompletedLength,
            ["selected"] = file.IsSelected,
            ["uris"] = new JsonArray(file.Uris.Select(uri => (JsonNode?)new JsonObject
            {
                ["uri"] = RedactUri(uri.Uri),
                ["engineStatus"] = uri.EngineStatus,
                ["used"] = uri.IsUsed,
            }).ToArray()),
        }).ToArray()),
        ["torrent"] = snapshot.Torrent is null ? null : new JsonObject
        {
            ["name"] = snapshot.Torrent.Name,
            ["comment"] = snapshot.Torrent.Comment,
            ["creationDate"] = snapshot.Torrent.CreationDate?.ToString("O"),
            ["mode"] = snapshot.Torrent.Mode,
            ["announceList"] = new JsonArray(snapshot.Torrent.AnnounceList.Select(tier =>
                (JsonNode?)new JsonArray(tier.Select(static tracker =>
                    (JsonNode?)JsonValue.Create(RedactUri(tracker))).ToArray())).ToArray()),
        },
    };

    private static JsonObject ToJson(DownloadGlobalSnapshot snapshot) => new()
    {
        ["downloadSpeed"] = snapshot.DownloadSpeed,
        ["uploadSpeed"] = snapshot.UploadSpeed,
        ["activeCount"] = snapshot.ActiveCount,
        ["waitingCount"] = snapshot.WaitingCount,
        ["stoppedCount"] = snapshot.StoppedCount,
        ["options"] = RedactedOptions(snapshot.Options),
    };

    private static JsonObject ToJson(DownloadCapabilityManifest manifest) => new()
    {
        ["abiVersion"] = manifest.AbiVersion,
        ["extendedApi"] = manifest.ExtendedApi,
        ["features"] = new JsonArray(manifest.Features.Select(static feature => (JsonNode?)JsonValue.Create(feature)).ToArray()),
        ["options"] = new JsonArray(manifest.Options.Select(option => (JsonNode?)new JsonObject
        {
            ["name"] = option.Name,
            ["description"] = option.Description,
            ["defaultValue"] = option.DefaultValue,
            ["possibleValues"] = option.PossibleValues,
            ["shortName"] = option.ShortName,
            ["argumentType"] = option.ArgumentType,
            ["hidden"] = option.Hidden,
            ["initial"] = option.Initial,
            ["changeActive"] = option.ChangeActive,
            ["changeReserved"] = option.ChangeReserved,
            ["changeGlobal"] = option.ChangeGlobal,
            ["cumulative"] = option.Cumulative,
        }).ToArray()),
    };

    private static JsonObject ToJson(DownloadLiveDetails details) => new()
    {
        ["peers"] = new JsonArray(details.Peers.Select(peer => (JsonNode?)new JsonObject
        {
            ["peerId"] = peer.PeerId,
            ["ip"] = peer.Ip,
            ["port"] = peer.Port,
            ["downloadSpeed"] = peer.DownloadSpeed,
            ["uploadSpeed"] = peer.UploadSpeed,
            ["amChoking"] = peer.AmChoking,
            ["peerChoking"] = peer.PeerChoking,
            ["seeder"] = peer.Seeder,
        }).ToArray()),
        ["servers"] = new JsonArray(details.Servers.Select(server => (JsonNode?)new JsonObject
        {
            ["fileIndex"] = server.FileIndex,
            ["uri"] = RedactUri(server.Uri),
            ["currentUri"] = RedactUri(server.CurrentUri),
            ["downloadSpeed"] = server.DownloadSpeed,
        }).ToArray()),
    };

    private static JsonObject RedactedOptions(IReadOnlyDictionary<string, string> options)
    {
        var result = new JsonObject();
        foreach (var option in options.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            result[option.Key] = DownloadSecretClassifier.IsSensitiveOption(option.Key) ? "<redacted>" : option.Value;
        }

        return result;
    }

    private static string RedactUri(string value) => DownloadSecretClassifier.RedactUri(value);

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
