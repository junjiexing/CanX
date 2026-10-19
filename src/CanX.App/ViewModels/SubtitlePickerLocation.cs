// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Platform.Storage;

namespace CanX.App.ViewModels;

public static class SubtitlePickerLocation
{
    public static FilePickerFileType SubtitleFileType { get; } = new("字幕文件")
    {
        Patterns =
        [
            "*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub", "*.idx", "*.sup",
            "*.smi", "*.sami", "*.lrc", "*.ttml", "*.dfxp",
        ],
        MimeTypes = ["application/x-subrip", "text/vtt", "text/plain", "text/x-ssa"],
    };

    public static string? GetLocalVideoDirectory(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var path = Path.IsPathFullyQualified(source) ? source
            : Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath : null;
        if (path is null || !Path.IsPathFullyQualified(path)) return null;
        var directory = Path.GetDirectoryName(path);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }
}
