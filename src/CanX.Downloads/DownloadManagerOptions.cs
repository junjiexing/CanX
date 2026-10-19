// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Downloads;

public sealed record DownloadManagerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    public Dictionary<string, string> GlobalOptions { get; init; } = new(StringComparer.Ordinal)
    {
        ["enable-color"] = "false",
        ["max-concurrent-downloads"] = "5",
        ["continue"] = "true",
    };

    /// <summary>Receives recoverable worker failures that were skipped instead of stopping the engine.</summary>
    public Action<Exception>? WorkerFaultHandler { get; init; }
}
