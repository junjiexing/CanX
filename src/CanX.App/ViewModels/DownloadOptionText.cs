// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.App.ViewModels;

public static class DownloadOptionText
{
    public static Dictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException($"选项“{line}”必须使用 key=value 格式。");
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                throw new FormatException("aria2 选项名不能为空。");
            }

            if (!result.TryAdd(key, value))
            {
                throw new FormatException($"aria2 选项“{key}”重复。");
            }
        }

        return result;
    }

    public static string Format(IReadOnlyDictionary<string, string> options) =>
        string.Join(Environment.NewLine, options.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => $"{item.Key}={item.Value}"));
}
