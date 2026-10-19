// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using CanX.Tooling;

namespace CanX.Application.Plugins;

public static class PluginWorkspaceToolCatalog
{
    public static IReadOnlyList<IToolHandler> Create(PluginWorkspaceManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return
        [
            Tool("plugins.workspace.list", "List isolated Agent plugin workspaces and their validation state.", ToolRisk.ReadOnly, Empty(),
                (_, _, token) => manager.ListAsync(token)),
            Tool("plugins.workspace.create", "Create an isolated plugin workspace, optionally from an installed plugin.", ToolRisk.LocalWrite,
                CreateSchema(), async (args, _, token) => await manager.CreateAsync(
                    Required(args, "pluginId"), Required(args, "name"), Optional(args, "fromPluginId"), token).ConfigureAwait(false)),
            Tool("plugins.workspace.files", "List files in one isolated plugin workspace.", ToolRisk.ReadOnly,
                WorkspaceSchema(), (args, _, token) => manager.ListFilesAsync(Required(args, "workspaceId"), token)),
            Tool("plugins.workspace.read", "Read a bounded UTF-8 text file from an isolated plugin workspace.", ToolRisk.ReadOnly,
                PathSchema(), (args, _, token) => manager.ReadAsync(
                    Required(args, "workspaceId"), Required(args, "path"), token)),
            Tool("plugins.workspace.write", "Atomically write one bounded text file inside an isolated plugin workspace.", ToolRisk.LocalWrite,
                WriteSchema(), (args, _, token) => manager.WriteAsync(
                    Required(args, "workspaceId"), Required(args, "path"), RequiredPresent(args, "content"), token)),
            Tool("plugins.workspace.delete", "Delete one file inside an isolated plugin workspace.", ToolRisk.Destructive,
                PathSchema(), (args, _, token) => manager.DeleteAsync(
                    Required(args, "workspaceId"), Required(args, "path"), token)),
            Tool("plugins.workspace.diff", "Show a bounded diff between the workspace and its initial or last-installed baseline.", ToolRisk.ReadOnly,
                WorkspaceSchema(), (args, _, token) => manager.DiffAsync(Required(args, "workspaceId"), token)),
            Tool("plugins.workspace.run", "Run one fixed workspace command: lint, build, test, or pack. No arbitrary command or shell is accepted.", ToolRisk.LocalWrite,
                RunSchema(), (args, _, token) => manager.RunAsync(
                    Required(args, "workspaceId"), Required(args, "command"), token)),
            Tool("plugins.workspace.prepareInstall", "Lint, build, sandbox-test, package, and return the exact permission/diff review required for installation.", ToolRisk.LocalWrite,
                WorkspaceSchema(), (args, _, token) => manager.PrepareInstallAsync(Required(args, "workspaceId"), token)),
            Tool("plugins.workspace.install", "Install the exact previously reviewed workspace build; rejects stale review tokens or changed files.", ToolRisk.Destructive,
                InstallSchema(), (args, _, token) => manager.InstallAsync(
                    Required(args, "workspaceId"), Required(args, "reviewToken"), token)),
            Tool("plugins.workspace.discard", "Permanently discard one isolated plugin workspace and its generated artifacts.", ToolRisk.Destructive,
                WorkspaceSchema(), (args, _, token) => manager.DiscardAsync(Required(args, "workspaceId"), token)),
        ];
    }

    private static IToolHandler Tool(
        string name,
        string description,
        ToolRisk risk,
        JsonObject schema,
        Func<JsonObject, ToolCallContext, CancellationToken, ValueTask<JsonObject>> execute) =>
        new DelegateTool(
            new ToolDescriptor(name, description, risk, schema, new JsonObject { ["type"] = "object" }),
            async (arguments, context, token) => ToolResult.Success(await execute(arguments, context, token).ConfigureAwait(false)));

    private static JsonObject Empty() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
    };

    private static JsonObject WorkspaceSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("workspaceId"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["workspaceId"] = Identifier() },
    };

    private static JsonObject PathSchema()
    {
        var schema = WorkspaceSchema();
        ((JsonArray)schema["required"]!).Add((JsonNode?)JsonValue.Create("path"));
        ((JsonObject)schema["properties"]!)["path"] = new JsonObject
        {
            ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256,
        };
        return schema;
    }

    private static JsonObject CreateSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("pluginId", "name"),
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["pluginId"] = new JsonObject { ["type"] = "string", ["minLength"] = 3, ["maxLength"] = 128 },
            ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 80 },
            ["fromPluginId"] = new JsonObject { ["type"] = "string", ["minLength"] = 3, ["maxLength"] = 128 },
        },
    };

    private static JsonObject WriteSchema()
    {
        var schema = PathSchema();
        ((JsonArray)schema["required"]!).Add((JsonNode?)JsonValue.Create("content"));
        ((JsonObject)schema["properties"]!)["content"] = new JsonObject
        {
            ["type"] = "string", ["maxLength"] = PluginWorkspaceManager.MaximumTextFileCharacters,
        };
        return schema;
    }

    private static JsonObject RunSchema()
    {
        var schema = WorkspaceSchema();
        ((JsonArray)schema["required"]!).Add((JsonNode?)JsonValue.Create("command"));
        ((JsonObject)schema["properties"]!)["command"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("lint", "build", "test", "pack"),
        };
        return schema;
    }

    private static JsonObject InstallSchema()
    {
        var schema = WorkspaceSchema();
        ((JsonArray)schema["required"]!).Add((JsonNode?)JsonValue.Create("reviewToken"));
        ((JsonObject)schema["properties"]!)["reviewToken"] = Identifier();
        return schema;
    }

    private static JsonObject Identifier() => new()
    {
        ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 32, ["pattern"] = "^[0-9a-f]{32}$",
    };

    private static string Required(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"'{name}' is required.");

    private static string RequiredPresent(JsonObject arguments, string name) =>
        arguments.TryGetPropertyValue(name, out var node) && node is not null
            ? node.GetValue<string>()
            : throw new ArgumentException($"'{name}' is required.");

    private static string? Optional(JsonObject arguments, string name) => arguments[name]?.GetValue<string>();

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

public sealed partial class PluginWorkspaceManager
{
    public const int MaximumTextFileCharacters = 1_048_576;
    private const long MaximumWorkspaceBytes = 16 * 1024 * 1024;
    private const int MaximumWorkspaceFiles = 1024;
    private const int MaximumDiffCharacters = 512 * 1024;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _root;
    private readonly PluginLifecycleManager _plugins;
    private readonly IPluginRuntimeHostFactory _trialHostFactory;
    private readonly string? _typescriptCompilerPath;
    private readonly Func<CancellationToken, Task> _refreshPlugins;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, WorkspaceState> _states = new(StringComparer.Ordinal);

    public PluginWorkspaceManager(
        string rootDirectory,
        PluginLifecycleManager plugins,
        IPluginRuntimeHostFactory trialHostFactory,
        string? typescriptCompilerPath = null,
        Func<CancellationToken, Task>? refreshPlugins = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _trialHostFactory = trialHostFactory ?? throw new ArgumentNullException(nameof(trialHostFactory));
        _typescriptCompilerPath = string.IsNullOrWhiteSpace(typescriptCompilerPath)
            ? null
            : Path.GetFullPath(typescriptCompilerPath);
        _refreshPlugins = refreshPlugins ?? (_ => Task.CompletedTask);
        Directory.CreateDirectory(_root);
        LoadStates();
    }

    public async ValueTask<JsonObject> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new JsonObject
            {
                ["workspaces"] = new JsonArray(_states.Values
                    .OrderByDescending(static state => state.UpdatedAt)
                    .Select(state => (JsonNode?)Summary(state)).ToArray()),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> CreateAsync(
        string pluginId,
        string name,
        string? fromPluginId,
        CancellationToken cancellationToken)
    {
        var manifest = ValidateIdentity(pluginId, name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workspaceId = CreateId();
            var root = WorkspaceRoot(workspaceId);
            var content = ContentRoot(workspaceId);
            var baseline = BaselineRoot(workspaceId);
            Directory.CreateDirectory(content);
            Directory.CreateDirectory(baseline);
            try
            {
                if (!string.IsNullOrWhiteSpace(fromPluginId))
                {
                    var installed = _plugins.Plugins.SingleOrDefault(status =>
                        string.Equals(status.Manifest.Id, fromPluginId, StringComparison.Ordinal))
                        ?? throw new KeyNotFoundException($"Installed plugin '{fromPluginId}' was not found.");
                    CopyTree(installed.DirectoryPath, content);
                    CopyTree(installed.DirectoryPath, baseline);
                    manifest = installed.Manifest;
                }
                else
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(content, "canx-plugin.json"),
                        NewManifestJson(manifest),
                        Utf8NoBom,
                        cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        Path.Combine(content, "main.mjs"),
                        NewEntrySource(name),
                        Utf8NoBom,
                        cancellationToken).ConfigureAwait(false);
                }
                var state = new WorkspaceState(
                    workspaceId,
                    manifest.Id,
                    manifest.Name,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null,
                    null,
                    null);
                _states.Add(workspaceId, state);
                SaveState(state);
                return Summary(state);
            }
            catch
            {
                TryDeleteDirectory(root);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> ListFilesAsync(string workspaceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            var files = EnumerateFiles(ContentRoot(state.Id));
            return new JsonObject
            {
                ["workspaceId"] = state.Id,
                ["files"] = new JsonArray(files.Select(file => (JsonNode?)new JsonObject
                {
                    ["path"] = file.RelativePath,
                    ["size"] = file.Length,
                    ["text"] = IsTextPath(file.RelativePath),
                    ["sha256"] = file.Sha256,
                }).ToArray()),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> ReadAsync(string workspaceId, string relativePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            var path = ResolvePath(ContentRoot(state.Id), relativePath, mustExist: true);
            if (!IsTextPath(relativePath)) throw new InvalidOperationException("Only UTF-8 text workspace files can be read by Agent.");
            var info = new FileInfo(path);
            if (info.Length > MaximumTextFileCharacters * 4L) throw new InvalidOperationException("Workspace file exceeds the read limit.");
            var content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (content.Length > MaximumTextFileCharacters) throw new InvalidOperationException("Workspace file exceeds the read limit.");
            return new JsonObject { ["path"] = NormalizeRelativePath(relativePath), ["content"] = content };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> WriteAsync(
        string workspaceId,
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        if (content.Length > MaximumTextFileCharacters) throw new ArgumentOutOfRangeException(nameof(content));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            var path = ResolvePath(ContentRoot(state.Id), relativePath, mustExist: false);
            if (!IsTextPath(relativePath)) throw new InvalidOperationException("Agent may only write approved text file types.");
            EnsureWorkspaceBudget(ContentRoot(state.Id), Utf8NoBom.GetByteCount(content), File.Exists(path) ? new FileInfo(path).Length : 0);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            state = InvalidateReview(state);
            _states[state.Id] = state;
            SaveState(state);
            return new JsonObject { ["path"] = NormalizeRelativePath(relativePath), ["sha256"] = HashFile(path) };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> DeleteAsync(string workspaceId, string relativePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            var path = ResolvePath(ContentRoot(state.Id), relativePath, mustExist: true);
            if (Directory.Exists(path)) throw new InvalidOperationException("Workspace delete accepts files only.");
            File.Delete(path);
            DeleteEmptyParents(Path.GetDirectoryName(path)!, ContentRoot(state.Id));
            state = InvalidateReview(state);
            _states[state.Id] = state;
            SaveState(state);
            return new JsonObject { ["deleted"] = true, ["path"] = NormalizeRelativePath(relativePath) };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> DiffAsync(string workspaceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            cancellationToken.ThrowIfCancellationRequested();
            return Diff(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> RunAsync(string workspaceId, string command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            return command switch
            {
                "lint" => Lint(state),
                "build" => await BuildAsync(state, cancellationToken).ConfigureAwait(false),
                "test" => await TestAsync(state, cancellationToken).ConfigureAwait(false),
                "pack" => Pack(state),
                _ => throw new ArgumentException("Supported commands are lint, build, test, and pack.", nameof(command)),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> PrepareInstallAsync(string workspaceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            var lint = Lint(state);
            var build = await BuildAsync(state, cancellationToken).ConfigureAwait(false);
            var test = await TestAsync(state, cancellationToken).ConfigureAwait(false);
            var package = Pack(state);
            var digest = WorkspaceDigest(ContentRoot(state.Id));
            var token = CreateId();
            var packageDigest = package["sha256"]!.GetValue<string>();
            var preparedAt = DateTimeOffset.UtcNow;
            state = state with
            {
                ReviewToken = token,
                ReviewDigest = digest,
                ReviewPackageDigest = packageDigest,
                ReviewPreparedAt = preparedAt,
                UpdatedAt = preparedAt,
                LastCheck = "passed",
            };
            _states[state.Id] = state;
            SaveState(state);
            var manifest = ReadManifest(state);
            return new JsonObject
            {
                ["workspaceId"] = state.Id,
                ["reviewToken"] = token,
                ["digest"] = digest,
                ["manifest"] = ManifestSummary(manifest),
                ["diff"] = Diff(state),
                ["checks"] = new JsonObject { ["lint"] = lint, ["build"] = build, ["test"] = test, ["pack"] = package },
                ["warning"] = "Installation replaces an existing plugin with the same ID and requires a separate confirmation.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> GetInstallReviewAsync(
        string workspaceId,
        string reviewToken,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireReviewedState(workspaceId, reviewToken);
            EnsureReviewedPackage(state);
            var manifest = ReadManifest(state);
            return new JsonObject
            {
                ["plugin"] = ManifestSummary(manifest),
                ["diff"] = Diff(state),
                ["checks"] = new JsonObject
                {
                    ["lint"] = "passed",
                    ["build"] = "passed",
                    ["sandboxTest"] = "passed; network, tools, and credentials denied",
                    ["package"] = "passed",
                },
                ["workspaceDigest"] = state.ReviewDigest,
                ["packageSha256"] = state.ReviewPackageDigest,
                ["preparedAt"] = state.ReviewPreparedAt?.ToString("O"),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> InstallAsync(
        string workspaceId,
        string reviewToken,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireReviewedState(workspaceId, reviewToken);
            var packagePath = PackagePath(state.Id);
            EnsureReviewedPackage(state);
            await using var package = File.OpenRead(packagePath);
            var status = await _plugins.InstallAsync(package, allowUpgrade: true, cancellationToken).ConfigureAwait(false);
            ReplaceBaseline(state.Id);
            state = state with
            {
                PluginId = status.Manifest.Id,
                Name = status.Manifest.Name,
                UpdatedAt = DateTimeOffset.UtcNow,
                ReviewToken = null,
                ReviewDigest = null,
                ReviewPackageDigest = null,
                ReviewPreparedAt = null,
                LastCheck = "installed",
            };
            _states[state.Id] = state;
            SaveState(state);
            await _refreshPlugins(cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["installed"] = true,
                ["plugin"] = ManifestSummary(status.Manifest),
                ["enabled"] = status.IsEnabled,
                ["requiresPermissionApproval"] = status.RequiresPermissionApproval,
                ["rollbackVersion"] = status.RollbackVersion,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JsonObject> DiscardAsync(string workspaceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireState(workspaceId);
            cancellationToken.ThrowIfCancellationRequested();
            _states.Remove(state.Id);
            TryDeleteDirectory(WorkspaceRoot(state.Id));
            return new JsonObject { ["discarded"] = true };
        }
        finally
        {
            _gate.Release();
        }
    }

    private JsonObject Lint(WorkspaceState state)
    {
        var files = EnumerateFiles(ContentRoot(state.Id));
        var manifest = ReadManifest(state);
        if (!files.Any(file => string.Equals(file.RelativePath, manifest.Entry, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Manifest entry '{manifest.Entry}' does not exist with exact casing.");
        }
        if (manifest.Ui?.Icon is { Kind: PluginUiIconKind.Asset } icon &&
            !files.Any(file => string.Equals(file.RelativePath, icon.Value, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Manifest UI icon '{icon.Value}' does not exist with exact casing.");
        }
        foreach (var file in files.Where(file => IsJavaScriptPath(file.RelativePath)))
        {
            ValidateImports(File.ReadAllText(file.FullPath, Encoding.UTF8), file.RelativePath);
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["command"] = "lint",
            ["plugin"] = ManifestSummary(manifest),
            ["fileCount"] = files.Count,
            ["totalBytes"] = files.Sum(static file => file.Length),
        };
    }

    private async ValueTask<JsonObject> BuildAsync(WorkspaceState state, CancellationToken cancellationToken)
    {
        _ = Lint(state);
        var root = ContentRoot(state.Id);
        var typescriptFiles = EnumerateFiles(root)
            .Where(static file => file.RelativePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                !file.RelativePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (typescriptFiles.Length == 0)
        {
            return new JsonObject { ["ok"] = true, ["command"] = "build", ["compiler"] = "not-required", ["compiledFiles"] = 0 };
        }
        if (_typescriptCompilerPath is null || !File.Exists(_typescriptCompilerPath))
        {
            throw new InvalidOperationException("The bundled TypeScript compiler is unavailable; use JavaScript or install the plugin SDK assets.");
        }
        var node = FindExecutable("node") ?? throw new InvalidOperationException("Node.js is required to compile TypeScript workspaces.");
        var hostRoot = Path.Combine(WorkspaceRoot(state.Id), "host");
        Directory.CreateDirectory(hostRoot);
        var runner = Path.Combine(hostRoot, "transpile.mjs");
        await File.WriteAllTextAsync(runner, TranspileScript, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        var output = new StringBuilder();
        foreach (var source in typescriptFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = source.RelativePath.StartsWith("src/", StringComparison.Ordinal)
                ? source.RelativePath[4..]
                : source.RelativePath;
            var destination = ResolvePath(root, $"dist/{Path.ChangeExtension(relative, ".js")}", mustExist: false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var processResult = await RunProcessAsync(
                node,
                [runner, _typescriptCompilerPath, source.FullPath, destination],
                hostRoot,
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
            output.AppendLine(processResult);
        }
        EnsureWorkspaceBudget(root, 0, 0);
        return new JsonObject
        {
            ["ok"] = true,
            ["command"] = "build",
            ["compiler"] = "typescript.transpileModule",
            ["compiledFiles"] = typescriptFiles.Length,
            ["output"] = Limit(output.ToString().Trim(), 16_384),
        };
    }

    private async ValueTask<JsonObject> TestAsync(WorkspaceState state, CancellationToken cancellationToken)
    {
        var lint = Lint(state);
        var manifest = ReadManifest(state);
        var installation = new PluginInstallation(
            manifest,
            ContentRoot(state.Id),
            WorkspaceDigest(ContentRoot(state.Id)),
            DateTimeOffset.UtcNow);
        await using var host = _trialHostFactory.Create(installation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true,
            ["command"] = "test",
            ["runtimeState"] = host.State.ToString(),
            ["lint"] = lint,
            ["network"] = "blocked",
            ["tools"] = "denied",
            ["credentials"] = "unavailable",
        };
    }

    private JsonObject Pack(WorkspaceState state)
    {
        _ = Lint(state);
        var packagePath = PackagePath(state.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        var temporary = $"{packagePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in EnumerateFiles(ContentRoot(state.Id)))
                {
                    var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var input = File.OpenRead(file.FullPath);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
            }
            File.Move(temporary, packagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["command"] = "pack",
            ["sha256"] = HashFile(packagePath),
            ["bytes"] = new FileInfo(packagePath).Length,
        };
    }

    private JsonObject Diff(WorkspaceState state)
    {
        var current = EnumerateFiles(ContentRoot(state.Id)).ToDictionary(static file => file.RelativePath, StringComparer.Ordinal);
        var baseline = EnumerateFiles(BaselineRoot(state.Id)).ToDictionary(static file => file.RelativePath, StringComparer.Ordinal);
        var paths = current.Keys.Concat(baseline.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changes = new JsonArray();
        var text = new StringBuilder();
        foreach (var path in paths)
        {
            var hasCurrent = current.TryGetValue(path, out var currentFile);
            var hasBaseline = baseline.TryGetValue(path, out var baselineFile);
            var change = !hasBaseline ? "added" : !hasCurrent ? "deleted" :
                string.Equals(currentFile!.Sha256, baselineFile!.Sha256, StringComparison.Ordinal) ? "unchanged" : "modified";
            if (change == "unchanged") continue;
            changes.Add((JsonNode)new JsonObject { ["path"] = path, ["change"] = change });
            if (text.Length >= MaximumDiffCharacters || !IsTextPath(path)) continue;
            text.Append("--- baseline/").Append(path).AppendLine();
            text.Append("+++ workspace/").Append(path).AppendLine();
            AppendPrefixed(text, hasBaseline ? File.ReadAllLines(baselineFile!.FullPath) : [], '-');
            AppendPrefixed(text, hasCurrent ? File.ReadAllLines(currentFile!.FullPath) : [], '+');
        }
        var diff = text.ToString();
        if (diff.Length > MaximumDiffCharacters) diff = $"{diff[..MaximumDiffCharacters]}\n... diff truncated ...";
        return new JsonObject { ["changes"] = changes, ["unifiedDiff"] = diff };
    }

    private PluginManifest ReadManifest(WorkspaceState state)
    {
        var path = ResolvePath(ContentRoot(state.Id), "canx-plugin.json", mustExist: true);
        var bytes = File.ReadAllBytes(path);
        var manifest = PluginManifestReader.Parse(bytes);
        if (!string.Equals(manifest.Id, state.PluginId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Workspace manifest ID cannot be changed after workspace creation.");
        }
        return manifest;
    }

    private static PluginManifest ValidateIdentity(string pluginId, string name)
    {
        var json = new JsonObject
        {
            ["manifestVersion"] = 1,
            ["id"] = pluginId.Trim(),
            ["name"] = name.Trim(),
            ["version"] = "0.1.0",
            ["apiVersion"] = "2.0",
            ["entry"] = "main.mjs",
            ["permissions"] = new JsonArray(),
        };
        return PluginManifestReader.Parse(Utf8NoBom.GetBytes(json.ToJsonString()));
    }

    private static string NewManifestJson(PluginManifest manifest) => new JsonObject
    {
        ["manifestVersion"] = manifest.ManifestVersion,
        ["id"] = manifest.Id,
        ["name"] = manifest.Name,
        ["description"] = "Created in the CanX Agent plugin workspace.",
        ["version"] = manifest.Version,
        ["apiVersion"] = manifest.ApiVersion.ToString(),
        ["entry"] = manifest.Entry,
        ["permissions"] = new JsonArray(),
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    private static string NewEntrySource(string name) => $$"""
        // SPDX-License-Identifier: GPL-3.0-or-later

        export {};

        console.log({{JsonValue.Create(name)!.ToJsonString()}} + " loaded");
        """;

    private static JsonObject ManifestSummary(PluginManifest manifest) => new()
    {
        ["id"] = manifest.Id,
        ["name"] = manifest.Name,
        ["version"] = manifest.Version,
        ["entry"] = manifest.Entry,
        ["apiVersion"] = manifest.ApiVersion.ToString(),
        ["permissions"] = new JsonArray(manifest.Permissions.Select(permission => (JsonNode?)new JsonObject
        {
            ["capability"] = permission.Capability,
            ["scopes"] = new JsonArray(permission.Scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray()),
        }).ToArray()),
    };

    private static JsonObject Summary(WorkspaceState state) => new()
    {
        ["workspaceId"] = state.Id,
        ["pluginId"] = state.PluginId,
        ["name"] = state.Name,
        ["createdAt"] = state.CreatedAt.ToString("O"),
        ["updatedAt"] = state.UpdatedAt.ToString("O"),
        ["lastCheck"] = state.LastCheck,
        ["installPrepared"] = state.ReviewToken is not null,
    };

    private IReadOnlyList<WorkspaceFile> EnumerateFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var files = new List<WorkspaceFile>();
        var directories = new Stack<string>();
        directories.Push(root);
        long total = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(prefix, comparison)) throw new InvalidDataException("Workspace path escaped its root.");
                var attributes = File.GetAttributes(fullPath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Workspace symbolic links are not allowed.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (string.Equals(Path.GetFileName(fullPath), "node_modules", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("node_modules is not allowed inside an Agent workspace.");
                    }
                    directories.Push(fullPath);
                    continue;
                }
                var info = new FileInfo(fullPath);
                total = checked(total + info.Length);
                if (files.Count >= MaximumWorkspaceFiles || total > MaximumWorkspaceBytes)
                {
                    throw new InvalidDataException("Workspace exceeds its file-count or total-size limit.");
                }
                var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
                files.Add(new WorkspaceFile(relative, fullPath, info.Length, HashFile(fullPath)));
            }
        }
        return files.OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private void EnsureWorkspaceBudget(string root, long incomingBytes, long replacedBytes)
    {
        var files = EnumerateFiles(root);
        if (files.Count + (replacedBytes == 0 ? 1 : 0) > MaximumWorkspaceFiles ||
            files.Sum(static file => file.Length) - replacedBytes + incomingBytes > MaximumWorkspaceBytes)
        {
            throw new InvalidOperationException("Workspace exceeds its file-count or total-size limit.");
        }
    }

    private static void ValidateImports(string source, string relativePath)
    {
        foreach (Match match in ModuleSpecifierPattern().Matches(source))
        {
            var specifier = match.Groups[1].Value;
            if (!specifier.StartsWith(".", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Module import '{specifier}' in '{relativePath}' is not a workspace-relative module.");
            }
            var parent = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
            var resolved = NormalizeRelativePath(Path.Combine(parent, specifier));
            if (resolved.StartsWith("../", StringComparison.Ordinal) || resolved == "..")
            {
                throw new InvalidDataException($"Module import in '{relativePath}' escapes the workspace.");
            }
        }
    }

    private string ResolvePath(string root, string relativePath, bool mustExist)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length is 0 or > 256 || normalized.StartsWith(".", StringComparison.Ordinal) ||
            normalized.Contains(':') || normalized.Contains('\\'))
        {
            throw new ArgumentException("Workspace path is not a portable relative path.", nameof(relativePath));
        }
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("Workspace path contains an unsafe segment.", nameof(relativePath));
        }
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(prefix, comparison)) throw new ArgumentException("Workspace path escapes its root.", nameof(relativePath));
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException("Workspace symbolic links are not allowed.");
                }
            }
        }
        if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("Workspace path does not exist.");
        }
        return fullPath;
    }

    private static string NormalizeRelativePath(string value) => value.Trim().Replace('\\', '/');

    private static bool IsTextPath(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".js" or ".mjs" or ".ts" or ".json" or ".md" or ".txt" or ".css";

    private static bool IsJavaScriptPath(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".js" or ".mjs" or ".ts";

    private static void AppendPrefixed(StringBuilder output, IEnumerable<string> lines, char prefix)
    {
        foreach (var line in lines)
        {
            if (output.Length >= MaximumDiffCharacters) return;
            output.Append(prefix).AppendLine(line);
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private string WorkspaceDigest(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateFiles(root))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData([0]);
            using var input = File.OpenRead(file.FullPath);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["LANG"] = "C";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the fixed plugin build command.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = $"{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}".Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Fixed build command failed: {Limit(output, 16_384)}");
        return Limit(output, 16_384);
    }

    private static string? FindExecutable(string name)
    {
        var executable = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private void LoadStates()
    {
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(directory);
            if (!WorkspaceIdPattern().IsMatch(id)) continue;
            try
            {
                var state = ReadState(id);
                _states[id] = state;
            }
            catch
            {
                // Invalid workspace metadata is ignored and never exposed to Agent.
            }
        }
    }

    private WorkspaceState ReadState(string id)
    {
        var node = JsonNode.Parse(File.ReadAllText(StatePath(id), Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException("Workspace state is invalid.");
        return new WorkspaceState(
            id,
            node["pluginId"]?.GetValue<string>() ?? throw new InvalidDataException("Workspace plugin ID is missing."),
            node["name"]?.GetValue<string>() ?? throw new InvalidDataException("Workspace name is missing."),
            DateTimeOffset.Parse(node["createdAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(node["updatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            node["reviewToken"]?.GetValue<string>(),
            node["reviewDigest"]?.GetValue<string>(),
            node["reviewPackageDigest"]?.GetValue<string>(),
            node["reviewPreparedAt"] is JsonValue preparedAt
                ? DateTimeOffset.Parse(preparedAt.GetValue<string>(), CultureInfo.InvariantCulture)
                : null,
            node["lastCheck"]?.GetValue<string>());
    }

    private void SaveState(WorkspaceState state)
    {
        var path = StatePath(state.Id);
        var node = new JsonObject
        {
            ["pluginId"] = state.PluginId,
            ["name"] = state.Name,
            ["createdAt"] = state.CreatedAt.ToString("O"),
            ["updatedAt"] = state.UpdatedAt.ToString("O"),
            ["reviewToken"] = state.ReviewToken,
            ["reviewDigest"] = state.ReviewDigest,
            ["reviewPackageDigest"] = state.ReviewPackageDigest,
            ["reviewPreparedAt"] = state.ReviewPreparedAt?.ToString("O"),
            ["lastCheck"] = state.LastCheck,
        };
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, node.ToJsonString(), Utf8NoBom);
        File.Move(temporary, path, overwrite: true);
    }

    private WorkspaceState RequireState(string id)
    {
        if (!WorkspaceIdPattern().IsMatch(id) || !_states.TryGetValue(id, out var state))
        {
            throw new KeyNotFoundException("Plugin workspace was not found.");
        }
        return state;
    }

    private WorkspaceState RequireReviewedState(string workspaceId, string reviewToken)
    {
        var state = RequireState(workspaceId);
        var digest = WorkspaceDigest(ContentRoot(state.Id));
        if (state.ReviewToken is null || state.ReviewDigest is null || state.ReviewPackageDigest is null ||
            state.ReviewPreparedAt is null || reviewToken.Length != state.ReviewToken.Length ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(state.ReviewToken),
                Encoding.ASCII.GetBytes(reviewToken)) ||
            !string.Equals(state.ReviewDigest, digest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workspace changed or the install review token is invalid; prepare installation again.");
        }
        return state;
    }

    private void EnsureReviewedPackage(WorkspaceState state)
    {
        var packagePath = PackagePath(state.Id);
        if (!File.Exists(packagePath) ||
            !string.Equals(state.ReviewPackageDigest, HashFile(packagePath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reviewed package changed or is missing; prepare installation again.");
        }
    }

    private WorkspaceState InvalidateReview(WorkspaceState state) => state with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        ReviewToken = null,
        ReviewDigest = null,
        ReviewPackageDigest = null,
        ReviewPreparedAt = null,
        LastCheck = null,
    };

    private void ReplaceBaseline(string workspaceId)
    {
        var baseline = BaselineRoot(workspaceId);
        TryDeleteDirectory(baseline);
        Directory.CreateDirectory(baseline);
        CopyTree(ContentRoot(workspaceId), baseline);
    }

    private static void CopyTree(string source, string destination)
    {
        var root = Path.GetFullPath(source);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Symbolic-link roots are not allowed.");
        Directory.CreateDirectory(destination);
        var files = 0;
        long bytes = 0;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Symbolic links are not allowed.");
                var relative = Path.GetRelativePath(root, path);
                var target = Path.Combine(destination, relative);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.CreateDirectory(target);
                    directories.Push(path);
                    continue;
                }
                var length = new FileInfo(path).Length;
                if (++files > MaximumWorkspaceFiles || (bytes = checked(bytes + length)) > MaximumWorkspaceBytes)
                {
                    throw new InvalidDataException("Plugin exceeds Agent workspace limits.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target, overwrite: false);
            }
        }
    }

    private static void DeleteEmptyParents(string directory, string root)
    {
        while (!string.Equals(directory, root, StringComparison.Ordinal) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup is best effort; the workspace is already inaccessible after state removal.
        }
    }

    private string WorkspaceRoot(string id) => Path.Combine(_root, id);
    private string ContentRoot(string id) => Path.Combine(WorkspaceRoot(id), "content");
    private string BaselineRoot(string id) => Path.Combine(WorkspaceRoot(id), "baseline");
    private string StatePath(string id) => Path.Combine(WorkspaceRoot(id), "state.json");
    private string PackagePath(string id) => Path.Combine(WorkspaceRoot(id), "artifacts", "plugin.canxplugin");
    private static string CreateId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private const string TranspileScript = """
        import fs from "node:fs";
        import { pathToFileURL } from "node:url";
        const [compilerPath, sourcePath, outputPath] = process.argv.slice(2);
        const module = await import(pathToFileURL(compilerPath));
        const ts = module.default ?? module;
        const source = fs.readFileSync(sourcePath, "utf8");
        const result = ts.transpileModule(source, {
          compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022, sourceMap: false },
          fileName: sourcePath,
          reportDiagnostics: true,
        });
        const diagnostics = (result.diagnostics ?? []).filter(item => item.category === ts.DiagnosticCategory.Error);
        if (diagnostics.length) {
          for (const item of diagnostics) console.error(ts.flattenDiagnosticMessageText(item.messageText, "\n"));
          process.exit(1);
        }
        fs.writeFileSync(outputPath, result.outputText, "utf8");
        console.log(`compiled ${sourcePath}`);
        """;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceIdPattern();

    [GeneratedRegex("(?:import\\s*(?:[^'\"]*?from\\s*)?|export\\s+[^'\"]*?from\\s*|import\\s*\\()\\s*['\"]([^'\"]+)['\"]", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleSpecifierPattern();

    private sealed record WorkspaceFile(string RelativePath, string FullPath, long Length, string Sha256);

    private sealed record WorkspaceState(
        string Id,
        string PluginId,
        string Name,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? ReviewToken,
        string? ReviewDigest,
        string? ReviewPackageDigest,
        DateTimeOffset? ReviewPreparedAt,
        string? LastCheck);
}
