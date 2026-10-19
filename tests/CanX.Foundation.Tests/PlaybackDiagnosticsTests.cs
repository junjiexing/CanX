// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PlaybackDiagnosticsTests
{
    [Fact]
    public void FrameTransferPath_RecognizesAndroidAImageReaderInteropWithoutHardwarePixelFormat()
    {
        var diagnostics = new PlaybackDiagnostics
        {
            HardwareDecoder = "mediacodec",
            HardwareInterop = "aimagereader",
            HardwarePixelFormat = string.Empty,
        };

        Assert.Equal("hardware-zero-copy-interop", diagnostics.FrameTransferPath);
    }

    [Fact]
    public void FrameTransferPath_StillClassifiesCopyDecoderAsCpuTransfer()
    {
        var diagnostics = new PlaybackDiagnostics
        {
            HardwareDecoder = "mediacodec-copy",
            HardwareInterop = "aimagereader",
        };

        Assert.Equal("hardware-decode-cpu-copy-gpu-upload", diagnostics.FrameTransferPath);
    }
}
