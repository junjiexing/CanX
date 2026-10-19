// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class ExternalSubtitleMatcherTests
{
    [Fact]
    public void Rank_PrefersConfiguredLanguageOverUntaggedSameName()
    {
        var candidates = new[]
        {
            Candidate("Movie.srt"), Candidate("Movie.zh.ass"), Candidate("Movie.en.srt"),
        };

        var chinese = ExternalSubtitleMatcher.Rank("Movie.mkv", candidates, "zh-CN,en");
        var english = ExternalSubtitleMatcher.Rank("Movie.mkv", candidates, "en,zh-CN");

        Assert.Equal("Movie.zh.ass", chinese[0].Candidate.FileName);
        Assert.Equal("Movie.en.srt", english[0].Candidate.FileName);
        Assert.Equal("Movie.srt", chinese[2].Candidate.FileName);
        Assert.True(chinese[0].SelectOverEmbedded);
    }

    [Fact]
    public void Rank_AcceptsSameEpisodeAcrossReleaseTagsButNeverAnotherEpisodeOrYear()
    {
        var matches = ExternalSubtitleMatcher.Rank(
            "Show.S01E02.2160p.mkv",
            [Candidate("Show.S01E03.zh.srt"), Candidate("Show.S01E02.sc.ass"), Candidate("Other.S01E02.zh.srt")],
            null);
        Assert.Equal("Show.S01E02.sc.ass", Assert.Single(matches).Candidate.FileName);

        Assert.Empty(ExternalSubtitleMatcher.Rank(
            "Movie.2022.2160p.mkv", [Candidate("Movie.2023.zh.srt")], null));
    }

    [Fact]
    public void Rank_RequiresWholeLanguageTokenAndSingleVideoForGenericFilename()
    {
        var candidates = new[] { Candidate("Movie.Science.srt"), Candidate("sc.srt") };

        Assert.Equal("sc.srt", Assert.Single(ExternalSubtitleMatcher.Rank(
            "Movie.mkv", candidates, null, videoCountInDirectory: 1)).Candidate.FileName);
        Assert.Empty(ExternalSubtitleMatcher.Rank(
            "Movie.mkv", candidates, null, videoCountInDirectory: 2));
    }

    [Fact]
    public void Rank_PairsIdxAndSubWithoutOfferingDuplicateTracks()
    {
        var matches = ExternalSubtitleMatcher.Rank(
            "Movie.mkv",
            [Candidate("Movie.zh.idx"), Candidate("Movie.zh.sub"), Candidate("Movie.en.idx")],
            null);

        Assert.Equal("Movie.zh.idx", Assert.Single(matches).Candidate.FileName);
    }

    [Fact]
    public void Rank_UsesCustomConfiguredLanguageTags()
    {
        var matches = ExternalSubtitleMatcher.Rank(
            "Movie.mkv",
            [Candidate("Movie.it-IT.srt"), Candidate("Movie.en.srt")],
            "it-IT,en");

        Assert.Equal("Movie.it-IT.srt", matches[0].Candidate.FileName);
        Assert.True(ExternalSubtitleMatcher.IsPreferredLanguage("it", "it-IT,en"));
    }

    [Fact]
    public void Rank_RecognizesJapaneseChineseFansubSuffixes()
    {
        const string stem = "[Nekomoe kissaten&VCB-Studio] Dungeon Meshi [05][Ma10p_1080p][x265_flac]";
        var matches = ExternalSubtitleMatcher.Rank($"{stem}.mkv",
        [
            Candidate($"{stem}.JPTC.ass"),
            Candidate($"{stem}.JPSC.ass"),
            Candidate(stem.Replace("[05]", "[06]", StringComparison.Ordinal) + ".JPSC.ass"),
        ], "zh-CN,zh-TW,en", videoCountInDirectory: 24);

        Assert.Equal(["zh-Hans", "zh-Hant"], matches.Select(static match => match.Language));
        Assert.All(matches, static match => Assert.True(match.SelectOverEmbedded));
    }

    [Fact]
    public void Rank_PairsRemoteIdxAndSubWhenUrisHaveQueries()
    {
        var matches = ExternalSubtitleMatcher.Rank("Movie.mkv",
        [
            new ExternalSubtitleCandidate("https://dav.test/Movie.idx?token=a.b", "Movie.idx"),
            new ExternalSubtitleCandidate("https://dav.test/Movie.sub?token=c.d", "Movie.sub"),
        ], null);

        Assert.Equal("Movie.idx", Assert.Single(matches).Candidate.FileName);
    }

    [Fact]
    public void Rank_UsesSameDirectoryThenFormatForEqualLanguageAndKeepsEmbeddedForOtherLanguage()
    {
        var matches = ExternalSubtitleMatcher.Rank(
            "Movie.mkv",
            [Candidate("Movie.fr.srt"), Candidate("Movie.zh.srt", false), Candidate("Movie.zh.ass")],
            "zh-CN,en");

        Assert.Equal(["Movie.zh.ass", "Movie.zh.srt", "Movie.fr.srt"],
            matches.Select(static match => match.Candidate.FileName));
        Assert.False(matches[^1].SelectOverEmbedded);
    }

    private static ExternalSubtitleCandidate Candidate(string fileName, bool sameDirectory = true) =>
        new($"/subtitles/{fileName}", fileName, sameDirectory);
}
