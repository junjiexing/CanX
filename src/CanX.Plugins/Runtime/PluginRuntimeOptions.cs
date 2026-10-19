// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Runtime;

using CanX.Plugins.Storage;
using CanX.Plugins.Http;

public sealed record PluginRuntimeOptions
{
    public nuint MemoryLimitBytes { get; init; } = 16 * 1024 * 1024;

    public nuint StackLimitBytes { get; init; } = 512 * 1024;

    public int EntryModuleLimitBytes { get; init; } = 4 * 1024 * 1024;

    public int TotalModuleLimitBytes { get; init; } = 16 * 1024 * 1024;

    public int MaximumModules { get; init; } = 1024;

    public int MaximumToolPayloadBytes { get; init; } = 256 * 1024;

    public int MaximumUiPagePayloadBytes { get; init; } = 4 * 1024 * 1024;

    public int MaximumHtmlPayloadBytes { get; init; } = 8 * 1024 * 1024;

    public int MaximumPendingToolCalls { get; init; } = 128;

    public int MaximumSecretBytes { get; init; } = 64 * 1024;

    public int MaximumSubscriptions { get; init; } = 8;

    public int MaximumJobsPerTurn { get; init; } = 128;

    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MinimumSubscriptionInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaximumSubscriptionInterval { get; init; } = TimeSpan.FromMinutes(1);

    public PluginStorageOptions Storage { get; init; } = new();

    public PluginHttpOptions Http { get; init; } = new();

    internal void Validate()
    {
        if (MemoryLimitBytes == 0 || StackLimitBytes == 0 || EntryModuleLimitBytes <= 0 ||
            TotalModuleLimitBytes < EntryModuleLimitBytes || MaximumModules <= 0 ||
            MaximumToolPayloadBytes <= 0 || MaximumUiPagePayloadBytes <= 0 || MaximumHtmlPayloadBytes <= 0 ||
            MaximumUiPagePayloadBytes > 16 * 1024 * 1024 ||
            MaximumHtmlPayloadBytes > 16 * 1024 * 1024 ||
            MaximumPendingToolCalls <= 0 || MaximumSecretBytes <= 0 ||
            MaximumSubscriptions <= 0 || MaximumJobsPerTurn <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PluginRuntimeOptions), "Runtime limits must be positive.");
        }

        if (ExecutionTimeout <= TimeSpan.Zero || ExecutionTimeout > TimeSpan.FromMinutes(1) ||
            ToolTimeout <= TimeSpan.Zero || ToolTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(PluginRuntimeOptions), "Runtime timeouts are outside supported limits.");
        }

        if (MinimumSubscriptionInterval < TimeSpan.FromMilliseconds(100) ||
            MaximumSubscriptionInterval < MinimumSubscriptionInterval ||
            MaximumSubscriptionInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PluginRuntimeOptions),
                "Subscription intervals are outside supported limits.");
        }

        Storage.Validate();
        Http.Validate();
    }
}
