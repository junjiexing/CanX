// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using CanX.Core.Playback;

namespace CanX.Core.Configuration;

public static class MpvOptionConfiguration
{
    private static readonly HashSet<string> HostControlledOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "config",
        "gpu-api",
        "gpu-context",
        "idle",
        "input-default-bindings",
        "input-builtin-bindings",
        "input-vo-keyboard",
        "keep-open",
        "osd-bar",
        "terminal",
        "vo",
        "wid",
    };

    // Options that load code, open control channels, or read/write arbitrary files. The advanced option text
    // is plain configuration: anything that makes libmpv execute scripts or touch paths outside the media the
    // host opens must go through a dedicated host feature instead. Names were taken from the M_OPT_FILE,
    // script, IPC and ytdl option tables of the pinned mpv revision; the prefix list also covers aliases.
    public static IReadOnlySet<string> CodeAndFileOptions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio-file-paths", "audio-files", "chapters-file", "config-dir", "cookies-file", "cover-art-files",
        "demuxer-cache-dir", "dump-stats", "external-files", "glsl-shader", "glsl-shaders", "gpu-shader-cache-dir",
        "icc-cache-dir", "icc-profile", "image-lut", "include", "input-commands", "input-conf", "input-ipc-client",
        "input-ipc-server", "input-test", "js-memory-report", "load-auto-profiles", "load-commands",
        "load-context-menu", "load-osd-console", "load-positioning", "load-scripts", "load-select",
        "load-stats-overlay", "log-file", "lut", "o", "ordered-chapters-files", "osc", "playlist", "screenshot-dir",
        "screenshot-directory", "screenshot-template", "script", "script-opts", "scripts", "stream-dump",
        "stream-record", "sub-ass-styles", "sub-file-paths", "sub-files", "target-lut", "tls-ca-file",
        "tls-cert-file", "tls-key-file", "use-filedir-conf", "vo-image-outdir", "watch-history-path",
        "watch-later-dir", "watch-later-directory", "ytdl", "ytdl-format", "ytdl-raw-options",
    };

    private static readonly string[] CodeAndFileOptionPrefixes =
    [
        "script-", "scripts-", "input-ipc-", "ytdl-", "sub-files-", "audio-files-", "external-files-",
        "glsl-shaders-", "script-opts-",
    ];

    public static bool IsCodeOrFileOption(string name) =>
        CodeAndFileOptions.Contains(name) ||
        CodeAndFileOptionPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, string> Build(MpvSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new Dictionary<string, string>(
            ParseAdvancedOptions(settings.AdvancedOptions),
            StringComparer.OrdinalIgnoreCase);

        Set(options, "hwdec", settings.HardwareDecoding);
        Set(options, "video-sync", settings.VideoSync);
        Set(options, "deinterlace", settings.Deinterlace);
        Set(options, "save-position-on-quit", ToMpvBoolean(settings.SavePositionOnQuit));
        Set(options, "resume-playback", ToMpvBoolean(settings.ResumePlayback));
        Set(options, "keep-open", settings.AutoPlayNext ? "yes" : "always");

        Set(options, "interpolation", ToMpvBoolean(settings.Interpolation));
        Set(options, "scale", settings.Scale);
        SetOrRemove(options, "cscale", settings.ChromaScale);
        Set(options, "dscale", settings.Downscale);
        Set(options, "deband", ToMpvBoolean(settings.Deband));
        Set(options, "sigmoid-upscaling", ToMpvBoolean(settings.SigmoidUpscaling));
        // Older libmpv builds accept yes/no here but reject auto and do not
        // expose hint-mode. The native defaults already express these choices.
        SetUnlessDefault(options, "target-colorspace-hint", settings.TargetColorspaceHint, "auto");
        SetUnlessDefault(options, "target-colorspace-hint-mode", settings.TargetColorspaceHintMode, "target");
        Set(options, "target-prim", settings.TargetPrimaries);
        Set(options, "target-trc", settings.TargetTransfer);
        Set(options, "target-peak", settings.TargetPeak);
        Set(options, "tone-mapping", settings.ToneMapping);
        Set(options, "gamut-mapping-mode", settings.GamutMappingMode);
        Set(options, "hdr-compute-peak", settings.HdrComputePeak);

        Set(options, "audio-device", settings.AudioDevice);
        Set(options, "audio-channels", settings.AudioChannels);
        Set(options, "audio-exclusive", ToMpvBoolean(settings.AudioExclusive));
        Set(options, "audio-pitch-correction", ToMpvBoolean(settings.AudioPitchCorrection));
        SetIfNotEmpty(options, "alang", settings.AudioLanguages);

        Set(options, "slang", ExternalSubtitleMatcher.ToMpvLanguages(settings.SubtitleLanguages));
        // CanX handles the recommended matching mode for local and browsable media.
        // Disable mpv's scanner so the same sidecar is not added twice.
        Set(options, "sub-auto", string.Equals(settings.SubtitleAuto, "exact", StringComparison.OrdinalIgnoreCase)
            ? "no"
            : settings.SubtitleAuto);
        Set(options, "sub-font", settings.SubtitleFont);
        Set(options, "sub-font-size", settings.SubtitleFontSize);
        Set(options, "sub-scale-with-window", ToMpvBoolean(settings.SubtitleScaleWithWindow));

        Set(options, "cache", settings.Cache);
        Set(options, "cache-secs", ToNonNegativeNumber(settings.CacheSeconds, "cache-secs"));
        Set(options, "demuxer-max-bytes", ToMebibytes(settings.DemuxerMaxBytesMiB, "demuxer-max-bytes"));
        Set(
            options,
            "demuxer-max-back-bytes",
            ToMebibytes(settings.DemuxerMaxBackBytesMiB, "demuxer-max-back-bytes"));
        Set(options, "cache-pause", ToMpvBoolean(settings.CachePause));
        Set(options, "cache-pause-initial", ToMpvBoolean(settings.CachePauseInitial));
        Set(
            options,
            "cache-pause-wait",
            ToNonNegativeNumber(settings.CachePauseWaitSeconds, "cache-pause-wait"));
        Set(options, "network-timeout", settings.NetworkTimeoutSeconds);
        return options;
    }

    public static IReadOnlyDictionary<string, string> ParseAdvancedOptions(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                throw InvalidLine(index, "暂不支持 profile 段，请填写普通 option=value 选项");
            }

            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                line = line[2..];
            }

            var separator = line.IndexOf('=');
            var name = (separator < 0 ? line.Split('#', 2)[0] : line[..separator]).Trim();
            var value = separator < 0 ? "yes" : ParseValue(line[(separator + 1)..], index);
            if (name.Length == 0)
            {
                throw InvalidLine(index, "选项名不能为空");
            }

            if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            {
                throw InvalidLine(index, $"选项名 {name} 只能包含字母、数字、下划线和连字符");
            }

            if (HostControlledOptions.Contains(name))
            {
                throw InvalidLine(index, $"{name} 由 CanX 播放宿主管理，不能覆盖");
            }

            if (IsCodeOrFileOption(name))
            {
                throw InvalidLine(index, $"{name} 会加载脚本、打开控制通道或读写任意文件，不能通过高级选项设置");
            }

            if (!result.TryAdd(name, value))
            {
                throw InvalidLine(index, $"选项 {name} 重复");
            }
        }

        return result;
    }

    private static string ParseValue(string input, int zeroBasedIndex)
    {
        var value = input.TrimStart();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value[0] is '\'' or '"')
        {
            var quote = value[0];
            var end = value.IndexOf(quote, 1);
            if (end < 0)
            {
                throw InvalidLine(zeroBasedIndex, "字符串缺少结束引号");
            }

            EnsureOnlyCommentRemains(value[(end + 1)..], zeroBasedIndex);
            return value[1..end];
        }

        if (value[0] == '%')
        {
            var lengthEnd = value.IndexOf('%', 1);
            if (lengthEnd <= 1 ||
                !int.TryParse(value.AsSpan(1, lengthEnd - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 0 ||
                value.Length - lengthEnd - 1 < length)
            {
                throw InvalidLine(zeroBasedIndex, "固定长度字符串必须使用 %长度%内容 格式");
            }

            var contentStart = lengthEnd + 1;
            EnsureOnlyCommentRemains(value[(contentStart + length)..], zeroBasedIndex);
            return value.Substring(contentStart, length);
        }

        var comment = value.IndexOf('#');
        return (comment < 0 ? value : value[..comment]).Trim();
    }

    private static void EnsureOnlyCommentRemains(string remainder, int zeroBasedIndex)
    {
        remainder = remainder.TrimStart();
        if (remainder.Length != 0 && !remainder.StartsWith('#'))
        {
            throw InvalidLine(zeroBasedIndex, "引号后的内容必须为空或注释");
        }
    }

    private static InvalidDataException InvalidLine(int zeroBasedIndex, string message) =>
        new($"mpv 高级配置第 {zeroBasedIndex + 1} 行无效：{message}。");

    private static void Set(IDictionary<string, string> options, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"mpv 常用选项 {name} 的值不能为空。");
        }

        options[name] = value.Trim();
    }

    private static void SetIfNotEmpty(IDictionary<string, string> options, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            options[name] = value.Trim();
        }
    }

    private static void SetOrRemove(IDictionary<string, string> options, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            options.Remove(name);
            return;
        }

        options[name] = value.Trim();
    }

    private static void SetUnlessDefault(
        IDictionary<string, string> options,
        string name,
        string? value,
        string defaultValue)
    {
        if (string.Equals(value, defaultValue, StringComparison.OrdinalIgnoreCase))
        {
            options.Remove(name);
            return;
        }

        Set(options, name, value);
    }

    private static string ToMpvBoolean(bool value) => value ? "yes" : "no";

    private static string ToMebibytes(string value, string optionName)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mebibytes) ||
            !double.IsFinite(mebibytes) ||
            mebibytes < 0)
        {
            throw new InvalidDataException($"mpv 常用选项 {optionName} 必须是大于或等于 0 的 MiB 数值。");
        }

        return $"{mebibytes.ToString("R", CultureInfo.InvariantCulture)}MiB";
    }

    private static string ToNonNegativeNumber(string value, string optionName)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) ||
            number < 0)
        {
            throw new InvalidDataException($"mpv 常用选项 {optionName} 必须是大于或等于 0 的数值。");
        }

        return number.ToString("R", CultureInfo.InvariantCulture);
    }
}
