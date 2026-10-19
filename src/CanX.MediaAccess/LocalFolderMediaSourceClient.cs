// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Streaming;

namespace CanX.MediaAccess;

/// <summary>
/// Browses a user-selected local directory through the same model used by
/// remote media sources. Only paths beneath the selected root are accepted.
/// </summary>
public sealed class LocalFolderMediaSourceClient : IMediaSourceClient
{
    private readonly string _rootPath;
    private bool _disposed;

    public LocalFolderMediaSourceClient(Uri root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || !root.IsFile)
        {
            throw new ArgumentException("The local media source root must be an absolute file URI.", nameof(root));
        }

        _rootPath = NormalizePath(root.LocalPath);
        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException($"Local media folder does not exist: {_rootPath}");
        }

        Root = ToFileUri(_rootPath, isDirectory: true);
    }

    public LocalFolderMediaSourceClient(string root)
        : this(CreateRootUri(root))
    {
    }

    public MediaSourceProtocol Protocol => MediaSourceProtocol.LocalFolder;

    public Uri Root { get; }

    public async ValueTask<IReadOnlyList<MediaResource>> BrowseAsync(
        Uri? directory = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = ResolvePath(directory ?? Root);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Local media folder does not exist: {path}");
        }

        return await Task.Run<IReadOnlyList<MediaResource>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entries = new List<MediaResource>();
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(ToResource(entry));
                }

                return entries;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MediaResource> GetAsync(
        Uri resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(resource);
        FileSystemInfo entry = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : File.Exists(path)
                ? new FileInfo(path)
                : throw new FileNotFoundException("Local media resource does not exist.", path);
        return ValueTask.FromResult(ToResource(entry));
    }

    public ValueTask<IMediaByteStream> OpenReadAsync(
        MediaResource resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        cancellationToken.ThrowIfCancellationRequested();
        if (resource.IsDirectory)
        {
            throw new InvalidOperationException("A directory cannot be opened as a media stream.");
        }

        var path = ResolvePath(resource.Uri);
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
        });
        return ValueTask.FromResult<IMediaByteStream>(new DotNetMediaByteStream(stream));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private string ResolvePath(Uri resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!resource.IsAbsoluteUri || !resource.IsFile)
        {
            throw new ArgumentException("The local media resource must be an absolute file URI.", nameof(resource));
        }

        var path = NormalizePath(resource.LocalPath);
        var relative = Path.GetRelativePath(_rootPath, path);
        if (Path.IsPathRooted(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The requested path is outside the local media source root.");
        }

        return path;
    }

    private static MediaResource ToResource(FileSystemInfo entry)
    {
        var isDirectory = entry is DirectoryInfo;
        return new MediaResource
        {
            Uri = ToFileUri(entry.FullName, isDirectory),
            Name = entry.Name,
            IsDirectory = isDirectory,
            Length = entry is FileInfo file ? file.Length : null,
            LastModified = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero),
        };
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static Uri CreateRootUri(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return ToFileUri(root, isDirectory: true);
    }

    private static Uri ToFileUri(string path, bool isDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        if (isDirectory && !Path.EndsInDirectorySeparator(fullPath))
        {
            fullPath += Path.DirectorySeparatorChar;
        }

        return new Uri(fullPath);
    }
}
