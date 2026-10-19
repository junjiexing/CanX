// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using CanX.Tooling;

namespace CanX.Agent;

public static class AgentContextEstimator
{
    // A conservative fallback for compatible endpoints that do not report usage.
    // UTF-8 bytes account for CJK text more reliably than character count.
    public static int EstimateText(string? text) => string.IsNullOrEmpty(text)
        ? 0 : checked((Encoding.UTF8.GetByteCount(text) + 2) / 3);

    public static int EstimateMessages(IEnumerable<AgentMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            total += 8 + EstimateText(message.Content);
            foreach (var call in message.ToolCalls)
                total += 12 + EstimateText(call.Name) + EstimateText(call.ArgumentsJson);
        }
        return (int)Math.Min(total, int.MaxValue);
    }

    public static int EstimateRequest(
        IEnumerable<AgentMessage> messages,
        IEnumerable<ToolDescriptor> tools)
    {
        long total = EstimateMessages(messages);
        foreach (var tool in tools)
            total += 20 + EstimateText(tool.Name) + EstimateText(tool.Description) +
                EstimateText(tool.InputSchema.ToJsonString());
        return (int)Math.Min(total, int.MaxValue);
    }
}
