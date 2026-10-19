// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CanX.Downloads;

internal static partial class SafeDownloadFileDeletion
{
    private const int LinuxOpenFlags = 0x10000 | 0x20000 | 0x80000; // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
    private const int NotFoundError = 2;

    public static void Delete(string rootDirectory, string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var path = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete a file outside '{root}'.");
        }

        if (OperatingSystem.IsLinux())
        {
            DeleteLinux(root, relative);
            return;
        }

        DeleteWithReparsePointChecks(root, relative);
    }

    private static void DeleteLinux(string root, string relative)
    {
        var directoryHandle = Open(root, LinuxOpenFlags);
        if (directoryHandle < 0)
        {
            ThrowIoError($"Cannot securely open download directory '{root}'.");
        }

        try
        {
            var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var nextHandle = OpenAt(directoryHandle, segments[index], LinuxOpenFlags);
                if (nextHandle < 0)
                {
                    if (Marshal.GetLastPInvokeError() == NotFoundError)
                    {
                        return;
                    }
                    ThrowIoError($"Refusing to traverse unsafe download path '{relative}'.");
                }

                _ = Close(directoryHandle);
                directoryHandle = nextHandle;
            }

            if (UnlinkAt(directoryHandle, segments[^1], 0) != 0 &&
                Marshal.GetLastPInvokeError() != NotFoundError)
            {
                ThrowIoError($"Cannot securely delete download file '{relative}'.");
            }
        }
        finally
        {
            _ = Close(directoryHandle);
        }
    }

    private static void DeleteWithReparsePointChecks(string root, string relative)
    {
        RejectReparsePoint(root);
        var current = root;
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!Directory.Exists(current))
            {
                return;
            }
            RejectReparsePoint(current);
        }

        var path = Path.Combine(current, segments[^1]);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Refusing to traverse symbolic link or reparse point '{path}'.");
        }
    }

    private static void ThrowIoError(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new IOException(message, new Win32Exception(error));
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(int directoryHandle, string path, int flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(int directoryHandle, string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int handle);
}
