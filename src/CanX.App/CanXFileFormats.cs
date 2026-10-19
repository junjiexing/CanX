// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.App;

internal static class CanXFileFormats
{
    public static readonly string[] Video =
    [
        ".3gp", ".asf", ".avi", ".flv", ".m2ts", ".m4v", ".mkv", ".mov",
        ".mp4", ".mpeg", ".mpg", ".mts", ".ogm", ".ogv", ".ts", ".webm", ".wmv",
    ];

    public static readonly string[] Audio =
    [
        ".aac", ".ac3", ".aiff", ".ape", ".flac", ".m4a", ".mka", ".mp2",
        ".mp3", ".oga", ".ogg", ".opus", ".wav", ".wma",
    ];

    public static readonly string[] Playlists = [".m3u", ".m3u8"];
    public static readonly string[] DownloadMetadata = [".torrent", ".meta4", ".metalink"];

    public static readonly (string Name, string[] Extensions)[] Groups =
    [
        ("视频", Video),
        ("音频", Audio),
        ("播放列表", Playlists),
        ("下载描述", DownloadMetadata),
    ];

    public static bool IsMediaFile(string path) =>
        File.Exists(path) && IsMediaExtension(Path.GetExtension(path));

    public static bool IsMediaExtension(string extension) =>
        Video.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
        Audio.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
        Playlists.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public static bool IsDownloadMetadataFile(string path) =>
        File.Exists(path) && DownloadMetadata.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
