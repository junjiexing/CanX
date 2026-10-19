// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Runtime;

public sealed record PluginToolRequest(string PluginId, string ToolName, string InputJson);

public sealed record PluginToolDispatchResult(bool IsSuccess, string Json)
{
    public static PluginToolDispatchResult Success(string json) => new(true, json);

    public static PluginToolDispatchResult Failure(string json) => new(false, json);
}

public interface IPluginToolDispatcher
{
    ValueTask<PluginToolDispatchResult> InvokeAsync(
        PluginToolRequest request,
        CancellationToken cancellationToken);
}
