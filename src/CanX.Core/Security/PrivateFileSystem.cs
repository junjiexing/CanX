// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Security;

public static class PrivateFileSystem
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, DirectoryMode);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    public static void EnsurePrivateParentDirectory(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        EnsurePrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
    }

    public static string PreparePrivateFilePath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath ?? throw new ArgumentNullException(nameof(filePath)));
        var directory = Path.GetDirectoryName(fullPath)!;
        if (Directory.Exists(directory) && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, DirectoryMode);
        }
        HardenFile(fullPath);
        return fullPath;
    }

    public static void HardenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }
}
