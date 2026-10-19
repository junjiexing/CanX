// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Downloads;

public static class DownloadCompletionTargets
{
    // "Open file" hands the path to the platform shell, which executes programs, shortcuts and scripts.
    // Only media and subtitle files are opened directly; anything else falls back to its folder.
    private static readonly HashSet<string> OpenableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".aac", ".ass", ".avi", ".flac", ".flv", ".m2ts", ".m4a", ".m4v", ".mka", ".mkv", ".mov",
        ".mp3", ".mp4", ".mpeg", ".mpg", ".ogg", ".ogv", ".opus", ".srt", ".ssa", ".ts", ".vtt", ".wav",
        ".webm", ".wma", ".wmv",
    };

    /// <summary>
    /// Resolves the path a completion action may open, or <see langword="null"/> when there is none.
    /// Files outside the task directory or with a non-media extension resolve to their directory instead.
    /// </summary>
    public static string? Resolve(DownloadTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.DestinationDirectory))
        {
            return null;
        }

        var directory = Path.GetFullPath(snapshot.DestinationDirectory);
        if (snapshot.CompletionAction == DownloadCompletionAction.OpenDirectory)
        {
            return directory;
        }
        if (snapshot.CompletionAction != DownloadCompletionAction.OpenFile)
        {
            return null;
        }

        var file = snapshot.Files.FirstOrDefault(static file => file.IsSelected)?.Path;
        if (string.IsNullOrWhiteSpace(file))
        {
            return directory;
        }

        var path = Path.GetFullPath(file, directory);
        var root = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root, comparison))
        {
            return directory;
        }
        return OpenableExtensions.Contains(Path.GetExtension(path))
            ? path
            : Path.GetDirectoryName(path) ?? directory;
    }
}
