// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Frozen;

namespace CanX.Core.Downloads;

public static class DownloadOptionPolicy
{
    // aria2 options that automation (Agent and plugin Tool calls) may set. The user-facing advanced editors
    // keep full access; this list excludes anything that writes files (log, save-session, save-cookies,
    // server-stat-of, dir, out), reroutes or weakens transport (proxies, check-certificate, CA files,
    // interfaces), runs programs (on-download-* hooks), or feeds credentials. Keep it in sync with the
    // common options shown in the download center.
    private static readonly FrozenSet<string> AutomationOptions = new[]
    {
        "max-download-limit",
        "max-upload-limit",
        "max-overall-download-limit",
        "max-overall-upload-limit",
        "max-concurrent-downloads",
        "max-connection-per-server",
        "split",
        "min-split-size",
        "checksum",
        "seed-ratio",
        "seed-time",
        "bt-tracker",
        "bt-exclude-tracker",
        "bt-tracker-connect-timeout",
        "bt-tracker-timeout",
        "bt-tracker-interval",
        "bt-max-peers",
        "enable-peer-exchange",
        "bt-save-metadata",
        "bt-metadata-only",
        "follow-torrent",
        "select-file",
        "pause-metadata",
        "continue",
        "max-tries",
        "retry-wait",
        "timeout",
        "connect-timeout",
        "lowest-speed-limit",
        "file-allocation",
        "user-agent",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool RequiresEngineRestart(string optionName) => optionName is
        "enable-dht" or
        "enable-dht6" or
        "dht-listen-port";

    public static bool IsAutomationOption(string optionName) => AutomationOptions.Contains(optionName);

    public static void EnsureAutomationOptions(IEnumerable<string> optionNames)
    {
        ArgumentNullException.ThrowIfNull(optionNames);
        var rejected = optionNames.Where(static name => !AutomationOptions.Contains(name)).ToArray();
        if (rejected.Length > 0)
        {
            throw new ArgumentException(
                $"aria2 option(s) {string.Join(", ", rejected.Select(static name => $"'{name}'"))} can only be " +
                "changed by the user in the CanX download center.");
        }
    }
}
