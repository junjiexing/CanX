// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DesktopDisplayRefreshRateTests
{
    [Fact]
    public void ParseXrandr_PrefersTheCurrentModeOfThePrimaryOutput()
    {
        const string output =
            """
            Screen 0: minimum 16 x 16, current 7040 x 2880, maximum 32767 x 32767
            DP-1 connected 1920x1080+5120+0
               1920x1080     74.97*+
            HDMI-2 connected primary 5120x2880+0+0
               5120x2880     59.98*+
               3840x2160     59.98
            """;

        Assert.Equal(59.98, DesktopDisplayRefreshRate.ParseXrandr(output));
    }

    [Fact]
    public void ParseXrandr_FallsBackToTheFirstCurrentMode()
    {
        const string output =
            """
            Screen 0: minimum 320 x 200, current 1920 x 1080, maximum 16384 x 16384
            Virtual-1 connected 1920x1080+0+0
               1920x1080     60.00*+
            """;

        Assert.Equal(60, DesktopDisplayRefreshRate.ParseXrandr(output));
    }
}
