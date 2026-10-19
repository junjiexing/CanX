// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Playback.Engine;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class NativeMpvEngineTests
{
    [Fact]
    public void DisabledAudioEnumerationUsesTheSystemRouteWithoutLoadingMpv()
    {
        using var engine = new NativeMpvEngine(enumerateAudioDevices: false);

        var device = Assert.Single(engine.GetAudioDevices());

        Assert.Equal("auto", device.Name);
        Assert.Equal("Android system output", device.Description);
        Assert.True(device.IsCurrent);
    }
}
