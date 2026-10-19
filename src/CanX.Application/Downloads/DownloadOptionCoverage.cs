// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;

namespace CanX.Application.Downloads;

public sealed record DownloadOptionCoverageReport(
    int Total,
    int Hidden,
    int Initial,
    int Active,
    int Reserved,
    int Global,
    int ReadOnly,
    IReadOnlyList<string> Uncovered)
{
    public int Covered => Total - Uncovered.Count;
}

public static class DownloadOptionCoverage
{
    public static DownloadOptionCoverageReport Analyze(IReadOnlyList<DownloadOptionDefinition> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var duplicateOrUnnamed = options
            .Where(static option => string.IsNullOrWhiteSpace(option.Name))
            .Select(static _ => "<unnamed>")
            .Concat(options
                .Where(static option => !string.IsNullOrWhiteSpace(option.Name))
                .GroupBy(static option => option.Name, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new DownloadOptionCoverageReport(
            options.Count,
            options.Count(static option => option.Hidden),
            options.Count(static option => option.Initial),
            options.Count(static option => option.ChangeActive),
            options.Count(static option => option.ChangeReserved),
            options.Count(static option => option.ChangeGlobal),
            options.Count(static option =>
                !option.Hidden &&
                !option.Initial &&
                !option.ChangeActive &&
                !option.ChangeReserved &&
                !option.ChangeGlobal),
            duplicateOrUnnamed);
    }
}
