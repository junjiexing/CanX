// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Configuration;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class MpvOptionConfigurationTests
{
    [Fact]
    public void Build_DefaultsToDisplaySyncWithoutInterpolationAndWithBilinearDownscale()
    {
        var options = MpvOptionConfiguration.Build(new MpvSettings());

        Assert.Equal("display-resample", options["video-sync"]);
        Assert.Equal("no", options["interpolation"]);
        Assert.Equal("bilinear", options["dscale"]);
        Assert.Equal("zh-CN,zh-TW,en", options["slang"]);
        Assert.Equal("no", options["sub-auto"]);
    }

    [Fact]
    public void Build_UsesNativeColorspaceHintDefaultsAndPreservesExplicitChoices()
    {
        var defaults = MpvOptionConfiguration.Build(new MpvSettings());
        Assert.False(defaults.ContainsKey("target-colorspace-hint"));
        Assert.False(defaults.ContainsKey("target-colorspace-hint-mode"));

        var explicitChoices = MpvOptionConfiguration.Build(new MpvSettings
        {
            TargetColorspaceHint = "yes",
            TargetColorspaceHintMode = "source",
        });
        Assert.Equal("yes", explicitChoices["target-colorspace-hint"]);
        Assert.Equal("source", explicitChoices["target-colorspace-hint-mode"]);
    }

    [Fact]
    public void Build_NormalizesSubtitleLanguageAliasesForEmbeddedTracks()
    {
        var options = MpvOptionConfiguration.Build(new MpvSettings { SubtitleLanguages = "sc,eng" });

        Assert.Equal("zh-CN,en", options["slang"]);
    }

    [Fact]
    public void Build_RejectsInvalidSubtitleLanguageCode()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            MpvOptionConfiguration.Build(new MpvSettings { SubtitleLanguages = "zh-CN,not-a-language" }));

        Assert.Contains("字幕语言", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAdvancedOptions_AcceptsCommentsPrefixesAndEqualsInValues()
    {
        var options = MpvOptionConfiguration.ParseAdvancedOptions(
            """
            # renderer tuning
            --tone-mapping=bt.2390 # inline comment

            http-header-fields="Referer=https://example.test/video#fragment"
            deband
            """);

        Assert.Equal("bt.2390", options["tone-mapping"]);
        Assert.Equal("Referer=https://example.test/video#fragment", options["http-header-fields"]);
        Assert.Equal("yes", options["deband"]);
    }

    [Theory]
    [InlineData("scale=lanczos\nscale=bilinear", "重复")]
    [InlineData("scale value", "只能包含")]
    [InlineData("[gpu-hq]", "profile")]
    [InlineData("vo=gpu-next", "宿主管理")]
    [InlineData("gpu-api=vulkan", "宿主管理")]
    [InlineData("osd-bar=yes", "宿主管理")]
    [InlineData("scripts=/tmp/payload.lua", "加载脚本")]
    [InlineData("script=/tmp/payload.js", "加载脚本")]
    [InlineData("scripts-append=/tmp/payload.lua", "加载脚本")]
    [InlineData("input-ipc-server=/tmp/mpv.sock", "加载脚本")]
    [InlineData("script-opts=ytdl_hook-ytdl_path=/tmp/x", "加载脚本")]
    [InlineData("include=/tmp/other.conf", "加载脚本")]
    [InlineData("log-file=/tmp/out.log", "加载脚本")]
    [InlineData("ytdl-raw-options=exec=x", "加载脚本")]
    public void ParseAdvancedOptions_RejectsAmbiguousOrHostControlledInput(string text, string message)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            MpvOptionConfiguration.ParseAdvancedOptions(text));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CommonControlsOverrideAdvancedTextAndConvertMiB()
    {
        var options = MpvOptionConfiguration.Build(new MpvSettings
        {
            HardwareDecoding = "nvdec",
            Deband = true,
            CacheSeconds = "120.5",
            DemuxerMaxBytesMiB = "256.5",
            DemuxerMaxBackBytesMiB = "64",
            CachePause = false,
            CachePauseInitial = true,
            CachePauseWaitSeconds = "4.5",
            TargetPrimaries = "bt.2020",
            TargetTransfer = "pq",
            TargetPeak = "1000",
            ToneMapping = "bt.2390",
            GamutMappingMode = "perceptual",
            HdrComputePeak = "yes",
            AdvancedOptions = "hwdec=no\ndeband=no\ntone-mapping=clip",
        });

        Assert.Equal("nvdec", options["hwdec"]);
        Assert.Equal("yes", options["deband"]);
        Assert.Equal("120.5", options["cache-secs"]);
        Assert.Equal("256.5MiB", options["demuxer-max-bytes"]);
        Assert.Equal("64MiB", options["demuxer-max-back-bytes"]);
        Assert.Equal("no", options["cache-pause"]);
        Assert.Equal("yes", options["cache-pause-initial"]);
        Assert.Equal("4.5", options["cache-pause-wait"]);
        Assert.Equal("bt.2020", options["target-prim"]);
        Assert.Equal("pq", options["target-trc"]);
        Assert.Equal("1000", options["target-peak"]);
        Assert.Equal("bt.2390", options["tone-mapping"]);
        Assert.Equal("perceptual", options["gamut-mapping-mode"]);
        Assert.Equal("yes", options["hdr-compute-peak"]);
    }

    [Theory]
    [InlineData("-1", "50", "1", "cache-secs")]
    [InlineData("10", "-1", "1", "demuxer-max-back-bytes")]
    [InlineData("10", "50", "NaN", "cache-pause-wait")]
    public void Build_RejectsInvalidCacheLimits(
        string cacheSeconds,
        string backBytesMiB,
        string pauseWaitSeconds,
        string optionName)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            MpvOptionConfiguration.Build(new MpvSettings
            {
                CacheSeconds = cacheSeconds,
                DemuxerMaxBackBytesMiB = backBytesMiB,
                CachePauseWaitSeconds = pauseWaitSeconds,
            }));

        Assert.Contains(optionName, exception.Message, StringComparison.Ordinal);
    }

}
