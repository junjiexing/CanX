// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Http;

public sealed record PluginHttpOptions
{
    public int MaximumConcurrentRequests { get; init; } = 4;

    public int MaximumRedirects { get; init; } = 5;

    public int MaximumRequestBodyBytes { get; init; } = 256 * 1024;

    public int MaximumResponseBodyBytes { get; init; } = 4 * 1024 * 1024;

    public int MaximumHeaderBytes { get; init; } = 64 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaximumConcurrentRequests <= 0 || MaximumConcurrentRequests > 64 ||
            MaximumRedirects < 0 || MaximumRedirects > 20 || MaximumRequestBodyBytes <= 0 ||
            MaximumResponseBodyBytes <= 0 || MaximumHeaderBytes <= 0 ||
            Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(PluginHttpOptions), "HTTP limits are outside supported ranges.");
        }
    }
}
