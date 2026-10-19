// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using CanX.Plugins.Runtime;
using CanX.Tooling;

namespace CanX.Application.Plugins;

public sealed class ToolRegistryPluginDispatcher(ToolRegistry registry) : IPluginToolDispatcher
{
    private static readonly HashSet<string> UserConfirmationOnlyTools = new(StringComparer.Ordinal)
    {
        "downloads.add",
        "downloads.addBatch",
        "search.download",
    };

    // A manifest scope is approved once at install time, so it cannot stand in for the per-call
    // confirmation that the Agent path requires. Plugins may therefore invoke read-only Tools plus
    // this fixed set of reversible playback/download controls; everything else (settings, plugin
    // management, workspace, mpv property/command, aria2 options, completion actions, file removal)
    // is host- or Agent-only regardless of what the manifest declares. Filter Tools are included
    // because PlaybackController rejects filters that read files.
    public static IReadOnlySet<string> PluginInvocableWriteTools { get; } = new[]
    {
        "playback.play",
        "playback.pause",
        "playback.togglePause",
        "playback.stop",
        "playback.seek",
        "playback.playlist.play",
        "playback.playlist.next",
        "playback.playlist.previous",
        "playback.volume.set",
        "playback.brightness.set",
        "playback.mute.set",
        "playback.speed.set",
        "playback.audioDevice.set",
        "playback.filter.video.set",
        "playback.filter.audio.set",
        "playback.filter.clear",
        "playback.track.select",
        "playback.subtitle.visibility.set",
        "playback.subtitle.delay.set",
        "playback.subtitle.style.set",
        "playback.color.set",
        "downloads.pause",
        "downloads.resume",
        "downloads.schedule.set",
        "downloads.move",
    }.ToFrozenSet(StringComparer.Ordinal);

    public async ValueTask<PluginToolDispatchResult> InvokeAsync(
        PluginToolRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PluginId) || request.PluginId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.ToolName) || request.ToolName.Length > 256 ||
            request.InputJson.Length > 1_048_576 || Encoding.UTF8.GetByteCount(request.InputJson) > 1_048_576)
        {
            return Failure("invalid_request", "Plugin Tool request exceeds its identity or payload limits.");
        }
        if (UserConfirmationOnlyTools.Contains(request.ToolName))
        {
            return Failure(
                "user_confirmation_required",
                "Plugins must open the standard download confirmation page through downloads.prompt.");
        }
        if (registry.TryGetDescriptor(request.ToolName, out var descriptor) &&
            descriptor is not null &&
            descriptor.Risk != ToolRisk.ReadOnly &&
            !PluginInvocableWriteTools.Contains(request.ToolName))
        {
            return Failure(
                "tool_not_plugin_invocable",
                $"Tool '{request.ToolName}' ({descriptor.Risk}) can only be invoked by the host or an approved Agent call.");
        }
        JsonObject? arguments;
        try
        {
            var node = JsonNode.Parse(request.InputJson, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
            arguments = node as JsonObject;
            if (node is not null && arguments is null)
            {
                return Failure("invalid_arguments", "Tool arguments must be a JSON object or null.");
            }
        }
        catch (JsonException)
        {
            return Failure("invalid_arguments", "Tool arguments are not valid JSON.");
        }

        var result = await registry.InvokeAsync(
            request.ToolName,
            arguments,
            new ToolCallContext(
                $"plugin:{request.PluginId}",
                HasUserApproval: false,
                CallerKind: ToolCallerKind.Plugin,
                CorrelationId: Guid.NewGuid().ToString("N"),
                Grants: new HashSet<string>([request.ToolName], StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(60),
                MaximumOutputBytes: 1_048_576),
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return PluginToolDispatchResult.Success(result.Value?.ToJsonString() ?? "null");
        }

        return Failure(
            result.ErrorCode ?? "tool_failure",
            result.ErrorMessage ?? "The Tool call failed.");
    }

    private static PluginToolDispatchResult Failure(string code, string message)
    {
        var json = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        }.ToJsonString();
        return PluginToolDispatchResult.Failure(json);
    }
}
