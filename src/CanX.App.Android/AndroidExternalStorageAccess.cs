// SPDX-License-Identifier: GPL-3.0-or-later

using Android.Provider;
using Avalonia.Platform.Storage;
using AndroidEnvironment = Android.OS.Environment;

namespace CanX.App.Android;

internal static class AndroidExternalStorageAccess
{
    public static async Task EnsureGrantedAsync()
    {
        var activity = MainActivity.Current
            ?? throw new InvalidOperationException("Android Activity 当前不可用，请返回应用后重试。");
        if (await activity.EnsureExternalStorageAccessAsync().ConfigureAwait(true)) return;

        throw new UnauthorizedAccessException(
            "CanX 需要“所有文件访问权限”才能让 aria2 写入所选目录，并让 libmpv 直接读取其中的媒体和字幕。");
    }

    public static string ResolveLocalFolderPath(IStorageFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var localPath = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath)) return ValidateDirectory(localPath);

        var uri = folder.Path;
        if (!string.Equals(uri.Scheme, "content", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "com.android.externalstorage.documents", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "该目录不是设备上的本地文件夹。云盘等文档提供程序目前只能通过“打开媒体”选择单个文件。");
        }

        var androidUri = global::Android.Net.Uri.Parse(uri.AbsoluteUri)
            ?? throw new InvalidDataException("Android 返回了无效的目录地址。");
        var documentId = DocumentsContract.GetTreeDocumentId(androidUri);
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new InvalidDataException("Android 返回的目录地址缺少文档 ID。");
        }

        var separator = documentId.IndexOf(':');
        var volume = separator < 0 ? documentId : documentId[..separator];
        var relativePath = separator < 0 ? string.Empty : documentId[(separator + 1)..];
        var root = string.Equals(volume, "primary", StringComparison.OrdinalIgnoreCase)
            ? AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath
            : Path.Combine("/storage", volume);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new DirectoryNotFoundException("Android 主存储目录不可用。");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, fullRoot, StringComparison.Ordinal) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Android 返回的目录超出了所选存储卷。");
        }

        return ValidateDirectory(fullPath);
    }

    private static string ValidateDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"所选目录不存在或 CanX 尚未获得访问权限：{fullPath}");
        }
        return fullPath;
    }
}
