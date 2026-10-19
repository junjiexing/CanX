// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace CanX.App.ViewModels;

internal static class PluginDiagnosticConsole
{
    private const int MaximumLinesPerFile = 100;
    private const int MaximumOutputCharacters = 64 * 1024;
    private static readonly string[] FileNames = ["quickjs.log.1", "quickjs.log", "plugin-audit.jsonl"];

    public static string Read(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        var output = new StringBuilder();
        foreach (var fileName in FileNames)
        {
            var path = Path.Combine(logDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var lines = ReadTail(path, MaximumLinesPerFile);
            if (lines.Count == 0)
            {
                continue;
            }
            if (output.Length > 0)
            {
                output.AppendLine();
            }
            output.Append("=== ").Append(fileName).AppendLine(" ===");
            foreach (var line in lines)
            {
                output.AppendLine(line);
                if (output.Length >= MaximumOutputCharacters)
                {
                    return output.ToString(0, MaximumOutputCharacters);
                }
            }
        }

        return output.Length == 0
            ? "暂无插件运行日志。可在“设置 / 通用 / 诊断”中启用日志记录。"
            : output.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ReadTail(string path, int maximumLines)
    {
        var lines = new Queue<string>(maximumLines);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count == maximumLines)
            {
                lines.Dequeue();
            }
            lines.Enqueue(line);
        }
        return lines.ToArray();
    }
}
