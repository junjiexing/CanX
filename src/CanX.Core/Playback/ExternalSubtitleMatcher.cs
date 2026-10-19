// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace CanX.Core.Playback;

public sealed record ExternalSubtitleCandidate(string Source, string FileName, bool IsSameDirectory = true);

public sealed record ExternalSubtitleMatch(
    ExternalSubtitleCandidate Candidate,
    string? Language,
    int MatchStrength,
    bool IsPreferredLanguage,
    bool SelectOverEmbedded);

/// <summary>Ranks sidecar subtitles without treating a filename substring as a language or episode match.</summary>
public static partial class ExternalSubtitleMatcher
{
    public const string DefaultLanguages = "zh-CN,zh-TW,en";

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".idx", ".smi", ".srt", ".ssa", ".sub", ".sup", ".vtt",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".asf", ".avi", ".flv", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg",
        ".mpg", ".mts", ".ogm", ".ogv", ".ts", ".webm", ".wmv",
    };

    private static readonly HashSet<string> Qualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "sdh", "cc", "hi", "signs", "songs", "full", "default", "commentary",
    };

    private static readonly HashSet<string> ReleaseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "bluray", "bdrip", "brrip", "webdl", "webrip", "hdtv", "remux", "dvdrip",
        "x264", "x265", "h264", "h265", "hevc", "av1", "avc", "hdr", "hdr10",
        "10bit", "8bit", "flac", "aac", "ac3", "dts", "opus", "2160p", "1080p", "720p",
        "480p", "4k", "uhd", "fhd",
    };

    public static bool IsSubtitleFile(string fileName) =>
        SubtitleExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsVideoFile(string fileName) =>
        VideoExtensions.Contains(Path.GetExtension(fileName));

    public static string EffectiveLanguages(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultLanguages : configured.Trim();

    public static string ToMpvLanguages(string? configured)
    {
        var tokens = EffectiveLanguages(configured).Split([',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Any(static token => NormalizeLanguage(token) is null && !LanguageTagPattern().IsMatch(token)))
        {
            throw new InvalidDataException("字幕语言请填写语言代码，并用逗号分隔，例如 zh-CN,zh-TW,en。");
        }

        return string.Join(',', ParseLanguages(configured).Select(static language => language switch
        {
            "zh-Hans" => "zh-CN",
            "zh-Hant" => "zh-TW",
            _ => language,
        }));
    }

    public static IReadOnlyList<ExternalSubtitleMatch> Rank(
        string videoFileName,
        IEnumerable<ExternalSubtitleCandidate> candidates,
        string? preferredLanguages,
        string mode = "exact",
        int videoCountInDirectory = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoFileName);
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.Equals(mode, "no", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var videoStem = Path.GetFileNameWithoutExtension(videoFileName);
        var preferences = ParseLanguages(preferredLanguages);
        var videoIdentity = Identity(videoStem, preferences);
        var sourceList = candidates.Where(candidate => IsSubtitleFile(candidate.FileName)).ToList();
        var pairedSub = sourceList
            .Where(candidate => Path.GetExtension(candidate.FileName).Equals(".idx", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => PairSourceKey(candidate.Source, ".sub"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pairedIndex = sourceList
            .Where(candidate => Path.GetExtension(candidate.FileName).Equals(".sub", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => PairSourceKey(candidate.Source, ".idx"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new List<ExternalSubtitleMatch>();
        foreach (var candidate in sourceList)
        {
            if (Path.GetExtension(candidate.FileName).Equals(".sub", StringComparison.OrdinalIgnoreCase) &&
                pairedSub.Contains(PairSourceKey(candidate.Source, ".sub")))
            {
                continue;
            }
            if (Path.GetExtension(candidate.FileName).Equals(".idx", StringComparison.OrdinalIgnoreCase) &&
                !pairedIndex.Contains(PairSourceKey(candidate.Source, ".idx")))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(candidate.FileName);
            var strength = MatchStrength(videoStem, videoIdentity, stem, videoCountInDirectory, mode, preferences);
            if (strength < 0)
            {
                continue;
            }

            var language = FindTrailingLanguage(stem, videoStem, preferences);
            var languageRank = LanguageRank(language, preferences);
            var preferred = languageRank < 100;
            matches.Add(new ExternalSubtitleMatch(
                candidate,
                language,
                strength,
                preferred,
                preferred || language is null && strength == 0));
        }

        return matches
            .OrderBy(static match => match.MatchStrength)
            .ThenBy(match => LanguageRank(match.Language, preferences))
            .ThenBy(static match => match.Candidate.IsSameDirectory ? 0 : 1)
            .ThenBy(static match => FormatRank(Path.GetExtension(match.Candidate.FileName)))
            .ThenBy(static match => match.Candidate.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseLanguages(string? configured)
    {
        var values = EffectiveLanguages(configured).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Select(static value => NormalizeLanguage(value) ??
                (LanguageTagPattern().IsMatch(value) ? value.ToLowerInvariant() : null))
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsPreferredLanguage(string? language, string? configured)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var preferences = ParseLanguages(configured);
        var normalized = NormalizeLanguage(language, preferences);
        return normalized is not null && LanguageRank(normalized, preferences) < 100;
    }

    private static int MatchStrength(
        string videoStem,
        string videoIdentity,
        string subtitleStem,
        int videoCount,
        string mode,
        IReadOnlyList<string> preferences)
    {
        if (subtitleStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (subtitleStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase) &&
            subtitleStem.Length > videoStem.Length &&
            IsSeparator(subtitleStem[videoStem.Length]) &&
            AreAnnotationsOnly(subtitleStem[videoStem.Length..], preferences))
        {
            return 0;
        }

        if (videoCount == 1 && AreAnnotationsOnly(subtitleStem, preferences))
        {
            return 1;
        }

        var subtitleIdentity = Identity(subtitleStem, preferences);
        if (videoIdentity.Length > 0 && subtitleIdentity.Equals(videoIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(mode, "fuzzy", StringComparison.OrdinalIgnoreCase) &&
            videoIdentity.Length > 0 && subtitleIdentity.Length > 0 &&
            (videoIdentity.StartsWith(subtitleIdentity + " ", StringComparison.OrdinalIgnoreCase) ||
             subtitleIdentity.StartsWith(videoIdentity + " ", StringComparison.OrdinalIgnoreCase)) &&
            SameEpisodeAndYear(videoStem, subtitleStem))
        {
            return 2;
        }

        return -1;
    }

    private static bool SameEpisodeAndYear(string videoStem, string subtitleStem)
    {
        var videoEpisode = EpisodePattern().Match(videoStem).Value;
        var subtitleEpisode = EpisodePattern().Match(subtitleStem).Value;
        if (!string.Equals(videoEpisode, subtitleEpisode, StringComparison.OrdinalIgnoreCase)) return false;
        var videoYear = YearPattern().Match(videoStem).Value;
        var subtitleYear = YearPattern().Match(subtitleStem).Value;
        return string.Equals(videoYear, subtitleYear, StringComparison.OrdinalIgnoreCase);
    }

    private static string Identity(string stem, IReadOnlyList<string> preferences)
    {
        // Fansub group names are release metadata, not part of the title.
        stem = LeadingGroupPattern().Replace(stem, string.Empty);
        var tokens = Tokens(stem).ToList();
        while (tokens.Count > 0 && IsAnnotation(tokens[^1], preferences))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return string.Join(' ', tokens).ToLowerInvariant();
    }

    private static string? FindTrailingLanguage(
        string stem,
        string videoStem,
        IReadOnlyList<string> preferences)
    {
        var suffix = stem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase) &&
            stem.Length > videoStem.Length && IsSeparator(stem[videoStem.Length])
                ? stem[videoStem.Length..]
                : stem;
        var tokens = Tokens(suffix);
        for (var index = tokens.Count - 1; index >= 0 && IsAnnotation(tokens[index], preferences); index--)
        {
            if (index > 0)
            {
                var compound = NormalizeLanguage($"{tokens[index - 1]}-{tokens[index]}", preferences);
                if (compound is not null) return compound;
            }

            var language = NormalizeLanguage(tokens[index], preferences);
            if (language is not null) return language;
        }

        return null;
    }

    private static bool AreAnnotationsOnly(string value, IReadOnlyList<string> preferences)
    {
        var tokens = Tokens(value);
        return tokens.Count > 0 && tokens.All(token => IsAnnotation(token, preferences) && !IsReleaseTag(token)) &&
            (tokens.Any(token => NormalizeLanguage(token, preferences) is not null) ||
             tokens.Count > 1 && NormalizeLanguage($"{tokens[0]}-{tokens[1]}", preferences) is not null);
    }

    private static bool IsAnnotation(string token, IReadOnlyList<string> preferences) =>
        NormalizeLanguage(token, preferences) is not null || Qualifiers.Contains(token) || IsReleaseTag(token) ||
        preferences.Any(preference => preference.EndsWith($"-{token}", StringComparison.OrdinalIgnoreCase)) ||
        token.Equals("hans", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("hant", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("cn", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("tw", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("hk", StringComparison.OrdinalIgnoreCase);

    private static bool IsReleaseTag(string token) =>
        ReleaseTags.Contains(token) || ResolutionPattern().IsMatch(token);

    private static IReadOnlyList<string> Tokens(string value) =>
        TokenPattern().Matches(value).Select(static match => match.Value).ToArray();

    private static bool IsSeparator(char value) => value is '.' or '_' or '-' or ' ' or '[' or '(';

    private static string? NormalizeLanguage(string value, IReadOnlyList<string>? preferences = null)
    {
        var known = NormalizeKnownLanguage(value);
        if (known is not null) return known;
        if (preferences is null) return null;
        return preferences.FirstOrDefault(preference =>
            preference.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            preference.StartsWith(value + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeKnownLanguage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "zh-cn" or "zh-hans" or "sc" or "chs" or "gb" or "gbk" or "jpsc" or "简体" or "简中" => "zh-Hans",
        "zh-tw" or "zh-hk" or "zh-hant" or "tc" or "cht" or "big5" or "jptc" or "繁体" or "繁中" => "zh-Hant",
        "zh" or "chi" or "zho" or "cn" or "中文" => "zh",
        "en" or "eng" or "english" or "英语" or "英文" => "en",
        "ja" or "jp" or "jpn" or "japanese" or "日语" or "日文" => "ja",
        "ko" or "kor" or "korean" or "韩语" or "韩文" => "ko",
        "fr" or "fre" or "fra" or "french" => "fr",
        "de" or "ger" or "deu" or "german" => "de",
        "es" or "spa" or "spanish" => "es",
        "ru" or "rus" or "russian" => "ru",
        _ => null,
    };

    private static int LanguageRank(string? language, IReadOnlyList<string> preferences)
    {
        if (language is null) return 100;
        for (var index = 0; index < preferences.Count; index++)
        {
            if (language.Equals(preferences[index], StringComparison.OrdinalIgnoreCase)) return index * 2;
        }

        if (language == "zh")
        {
            var chineseRanks = preferences.Select((value, index) => (value, index))
                .Where(static entry => entry.value is "zh-Hans" or "zh-Hant")
                .Select(static entry => entry.index)
                .ToArray();
            if (chineseRanks.Length > 0) return chineseRanks.Max() * 2 + 1;
        }

        return 200;
    }

    private static int FormatRank(string extension) => extension.ToLowerInvariant() switch
    {
        ".ass" => 0,
        ".ssa" => 1,
        ".srt" => 2,
        ".vtt" => 3,
        ".smi" => 4,
        ".idx" => 5,
        ".sub" => 6,
        ".sup" => 7,
        _ => 8,
    };

    private static string PairSourceKey(string source, string extension) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? Path.ChangeExtension(uri.GetLeftPart(UriPartial.Path), extension)
            : Path.ChangeExtension(source, extension);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^\[[^\]]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingGroupPattern();

    [GeneratedRegex(@"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"^(?:\d{3,4}p|\d{3,4}x\d{3,4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagPattern();
}
