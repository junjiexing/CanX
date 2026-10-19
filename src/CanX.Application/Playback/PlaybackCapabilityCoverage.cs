// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;

namespace CanX.Application.Playback;

public sealed record PlaybackCapabilityCoverageReport(
    int PropertyTotal,
    int PropertyDedicated,
    int PropertyAdvanced,
    int PropertyBlocked,
    int CommandTotal,
    int CommandDedicated,
    int CommandAdvanced,
    int CommandBlocked,
    IReadOnlyList<string> Uncovered)
{
    public int Total => PropertyTotal + CommandTotal;

    public int Covered => Total - Uncovered.Count;
}

public static class PlaybackCapabilityCoverage
{
    public static PlaybackCapabilityCoverageReport Analyze(PlaybackCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var uncovered = new List<string>();
        AnalyzeNames(manifest.Properties.Select(static item => item.Name), "property", uncovered);
        AnalyzeNames(manifest.Commands.Select(static item => item.Name), "command", uncovered);

        foreach (var property in manifest.Properties)
        {
            if (property.Coverage == PlaybackCapabilityCoverageKind.Unknown ||
                property.Coverage == PlaybackCapabilityCoverageKind.DedicatedTool && property.Tools.Count == 0 ||
                property.Coverage == PlaybackCapabilityCoverageKind.AdvancedAccess && !property.CanRead && !property.CanWrite ||
                property.Coverage == PlaybackCapabilityCoverageKind.Blocked &&
                    (property.CanRead || property.CanWrite || string.IsNullOrWhiteSpace(property.Restriction)))
            {
                uncovered.Add($"property:{DisplayName(property.Name)}");
            }
        }

        foreach (var command in manifest.Commands)
        {
            if (command.Coverage == PlaybackCapabilityCoverageKind.Unknown ||
                command.Coverage == PlaybackCapabilityCoverageKind.DedicatedTool && command.Tools.Count == 0 ||
                command.Coverage == PlaybackCapabilityCoverageKind.AdvancedAccess && !command.CanExecute ||
                command.Coverage == PlaybackCapabilityCoverageKind.Blocked &&
                    (command.CanExecute || string.IsNullOrWhiteSpace(command.Restriction)) ||
                command.Arguments.Any(static argument =>
                    string.IsNullOrWhiteSpace(argument.Name) || string.IsNullOrWhiteSpace(argument.Type)))
            {
                uncovered.Add($"command:{DisplayName(command.Name)}");
            }
        }

        return new PlaybackCapabilityCoverageReport(
            manifest.Properties.Count,
            manifest.Properties.Count(static item => item.Coverage == PlaybackCapabilityCoverageKind.DedicatedTool),
            manifest.Properties.Count(static item => item.Coverage == PlaybackCapabilityCoverageKind.AdvancedAccess),
            manifest.Properties.Count(static item => item.Coverage == PlaybackCapabilityCoverageKind.Blocked),
            manifest.Commands.Count,
            manifest.Commands.Count(static item => item.Coverage == PlaybackCapabilityCoverageKind.DedicatedTool),
            manifest.Commands.Count(static item => item.Coverage == PlaybackCapabilityCoverageKind.AdvancedAccess),
            manifest.Commands.Count(static item => item.Coverage == PlaybackCapabilityCoverageKind.Blocked),
            uncovered.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static void AnalyzeNames(IEnumerable<string> names, string prefix, ICollection<string> uncovered)
    {
        var values = names.ToArray();
        foreach (var _ in values.Where(static name => string.IsNullOrWhiteSpace(name)))
        {
            uncovered.Add($"{prefix}:<unnamed>");
        }

        foreach (var duplicate in values
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(static name => name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key))
        {
            uncovered.Add($"{prefix}:{duplicate}");
        }
    }

    private static string DisplayName(string name) => string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name;
}
