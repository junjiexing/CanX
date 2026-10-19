// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Application.Playback;
using CanX.Core.Playback;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackCapabilityCoverageTests
{
    [Fact]
    public void Analyze_AccountsForEveryClassifiedPropertyAndCommand()
    {
        var manifest = new PlaybackCapabilityManifest
        {
            Properties =
            [
                new PlaybackPropertyCapability
                {
                    Name = "pause",
                    CanRead = true,
                    CanWrite = true,
                    Coverage = PlaybackCapabilityCoverageKind.DedicatedTool,
                    Tools = ["playback.pause"],
                },
                new PlaybackPropertyCapability
                {
                    Name = "video-sync",
                    CanRead = true,
                    CanWrite = true,
                    Coverage = PlaybackCapabilityCoverageKind.AdvancedAccess,
                },
                new PlaybackPropertyCapability
                {
                    Name = "path",
                    Coverage = PlaybackCapabilityCoverageKind.Blocked,
                    Restriction = "May contain credentials.",
                },
            ],
            Commands =
            [
                new PlaybackCommandCapability
                {
                    Name = "seek",
                    CanExecute = true,
                    Coverage = PlaybackCapabilityCoverageKind.DedicatedTool,
                    Tools = ["playback.seek"],
                },
                new PlaybackCommandCapability
                {
                    Name = "show-text",
                    CanExecute = true,
                    Coverage = PlaybackCapabilityCoverageKind.AdvancedAccess,
                    Arguments =
                    [
                        new PlaybackCommandArgumentCapability { Name = "text", Type = "String" },
                    ],
                },
                new PlaybackCommandCapability
                {
                    Name = "quit",
                    Coverage = PlaybackCapabilityCoverageKind.Blocked,
                    Restriction = "Terminates the host.",
                },
            ],
        };

        var coverage = PlaybackCapabilityCoverage.Analyze(manifest);

        Assert.Equal(6, coverage.Total);
        Assert.Equal(6, coverage.Covered);
        Assert.Equal(1, coverage.PropertyDedicated);
        Assert.Equal(1, coverage.PropertyAdvanced);
        Assert.Equal(1, coverage.PropertyBlocked);
        Assert.Equal(1, coverage.CommandDedicated);
        Assert.Equal(1, coverage.CommandAdvanced);
        Assert.Equal(1, coverage.CommandBlocked);
        Assert.Empty(coverage.Uncovered);
    }

    [Fact]
    public void Analyze_ReportsMalformedDuplicateAndUnclassifiedEntries()
    {
        var manifest = new PlaybackCapabilityManifest
        {
            Properties =
            [
                new PlaybackPropertyCapability { Name = "duplicate" },
                new PlaybackPropertyCapability { Name = "duplicate" },
                new PlaybackPropertyCapability { Name = "" },
            ],
            Commands =
            [
                new PlaybackCommandCapability
                {
                    Name = "bad-command",
                    CanExecute = true,
                    Coverage = PlaybackCapabilityCoverageKind.AdvancedAccess,
                    Arguments = [new PlaybackCommandArgumentCapability { Name = "value" }],
                },
            ],
        };

        var coverage = PlaybackCapabilityCoverage.Analyze(manifest);

        Assert.Contains("property:<unnamed>", coverage.Uncovered);
        Assert.Contains("property:duplicate", coverage.Uncovered);
        Assert.Contains("command:bad-command", coverage.Uncovered);
    }
}
