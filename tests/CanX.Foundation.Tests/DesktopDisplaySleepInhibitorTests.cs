// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DesktopDisplaySleepInhibitorTests
{
    [Fact]
    public async Task WindowsRequestsCanBeAcquiredIndependentlyAndReleasedOnAnotherThread()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var first = await DesktopDisplaySleepInhibitor.AcquireAsync(default);
        using var second = await DesktopDisplaySleepInhibitor.AcquireAsync(default);
        await Task.Run(first.Dispose);
        await Task.Run(second.Dispose);
    }
}
