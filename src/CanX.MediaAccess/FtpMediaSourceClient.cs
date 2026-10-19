// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using FluentFTP;

namespace CanX.MediaAccess;

public sealed class FtpMediaSourceClient : IMediaSourceClient
{
    private const int DefaultFtpPort = 21;
    private readonly NetworkCredential _credential;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private AsyncFtpClient _client;
    private bool _disposed;

    public FtpMediaSourceClient(Uri root, NetworkCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateFtpUri(root, nameof(root));

        Root = EnsureDirectoryUri(root);
        _credential = credential is null
            ? new NetworkCredential("anonymous", "anonymous@")
            : new NetworkCredential(credential.UserName, credential.Password, credential.Domain);
        _client = CreateClient();
    }

    public MediaSourceProtocol Protocol => MediaSourceProtocol.Ftp;

    public Uri Root { get; }

    public static FtpMediaSourceClient Create(Uri root, NetworkCredential? credential = null) =>
        new(root, credential);

    public async ValueTask<IReadOnlyList<MediaResource>> BrowseAsync(
        Uri? directory = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = EnsureDirectoryUri(directory ?? Root);
        EnsureWithinRoot(target);
        var path = GetServerPath(target, directory: true);
        var entries = await ExecuteWithReconnectAsync(
            (client, token) => client.GetListing(path, token),
            cancellationToken).ConfigureAwait(false);
        var resources = new List<MediaResource>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Type is not (FtpObjectType.File or FtpObjectType.Directory) ||
                entry.Name is "." or "..")
            {
                continue;
            }

            try
            {
                resources.Add(ToResource(entry, path));
            }
            catch (ArgumentException)
            {
                // Ignore malformed or out-of-root entries returned by a server.
            }
        }

        return resources
            .OrderByDescending(static resource => resource.IsDirectory)
            .ThenBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async ValueTask<MediaResource> GetAsync(
        Uri resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        EnsureWithinRoot(resource);
        var path = GetServerPath(resource, directory: false);
        return await ExecuteWithReconnectAsync(async (client, token) =>
        {
            if (client.HasFeature(FtpCapability.MLST))
            {
                var item = await client.GetObjectInfo(path, true, token).ConfigureAwait(false);
                if (item is not null)
                {
                    return ToResource(item, GetParentPath(path));
                }
            }

            var parentPath = GetParentPath(path);
            var entries = await client.GetListing(parentPath, token).ConfigureAwait(false);
            return entries
                .Where(static entry => entry.Type is FtpObjectType.File or FtpObjectType.Directory)
                .Select(entry => ToResource(entry, parentPath))
                .FirstOrDefault(candidate => UriEquals(candidate.Uri, resource))
                ?? throw new FileNotFoundException(
                    "The FTP server did not return metadata for the requested resource.",
                    path);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CanX.Streaming.IMediaByteStream> OpenReadAsync(
        MediaResource resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        EnsureWithinRoot(resource.Uri);
        if (resource.IsDirectory)
        {
            throw new ArgumentException("An FTP directory cannot be opened as media.", nameof(resource));
        }

        var resolved = resource.Length is null
            ? await GetAsync(resource.Uri, cancellationToken).ConfigureAwait(false)
            : resource;
        var canSeek = await ExecuteWithReconnectAsync(
            SupportsRestartAsync,
            cancellationToken).ConfigureAwait(false);
        var path = GetServerPath(resolved.Uri, directory: false);
        return new RetryingMediaByteStream(
            () => new FtpMediaByteStream(CreateClient, path, resolved.Length, canSeek),
            resolved.Length,
            canSeek);
    }

    private static async Task<bool> SupportsRestartAsync(
        AsyncFtpClient client,
        CancellationToken cancellationToken)
    {
        if (client.HasFeature(FtpCapability.REST))
        {
            return true;
        }

        // FEAT is advisory and several interoperable servers (including rclone)
        // implement REST without advertising "REST STREAM". Probe the command
        // directly so seeking is not disabled by an incomplete feature list.
        var typeReply = await client.Execute("TYPE I", cancellationToken).ConfigureAwait(false);
        if (typeReply.Code is not { Length: 3 } || typeReply.Code[0] != '2')
        {
            return false;
        }

        var reply = await client.Execute("REST 0", cancellationToken).ConfigureAwait(false);
        return reply.Code is { Length: 3 } && reply.Code[0] is '2' or '3';
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeClient();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private static async Task EnsureConnectedAsync(
        AsyncFtpClient client,
        CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> ExecuteWithReconnectAsync<T>(
        Func<AsyncFtpClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(_client, cancellationToken).ConfigureAwait(false);
                    return await operation(_client, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested && NetworkMediaRetry.IsTransient(exception))
                {
                    ResetClient();
                    if (attempt >= NetworkMediaRetry.MaximumAttempts)
                    {
                        throw;
                    }

                    await NetworkMediaRetry.DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ResetClient()
    {
        DisposeClient();
        _client = CreateClient();
    }

    private void DisposeClient()
    {
        try
        {
            _client.Dispose();
        }
        catch (Exception)
        {
            // A failed FTP transport may already have torn down its socket.
        }
    }

    private AsyncFtpClient CreateClient()
    {
        var config = new FtpConfig
        {
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            EncryptionMode = Root.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase)
                ? FtpEncryptionMode.Explicit
                : FtpEncryptionMode.None,
            ConnectTimeout = 15_000,
            ReadTimeout = 30_000,
            DataConnectionConnectTimeout = 15_000,
            DataConnectionReadTimeout = 30_000,
        };
        return new AsyncFtpClient(Root.IdnHost, _credential, EffectivePort(Root), config);
    }

    private MediaResource ToResource(FtpListItem item, string listingPath)
    {
        var isDirectory = item.Type == FtpObjectType.Directory;
        var path = string.IsNullOrWhiteSpace(item.FullName)
            ? CombinePath(listingPath, item.Name, isDirectory)
            : NormalizePath(item.FullName, isDirectory);
        var uri = BuildUri(path);
        EnsureWithinRoot(uri);
        return new MediaResource
        {
            Uri = uri,
            Name = item.Name,
            IsDirectory = isDirectory,
            Length = isDirectory || item.Size < 0 ? null : item.Size,
            LastModified = item.Modified == DateTime.MinValue
                ? null
                : new DateTimeOffset(item.Modified),
        };
    }

    private void EnsureWithinRoot(Uri uri)
    {
        ValidateFtpUri(uri, nameof(uri));
        if (!HasSameOrigin(uri, Root))
        {
            throw new ArgumentException("The FTP resource must remain within the configured server.", nameof(uri));
        }

        var rootPath = GetServerPath(Root, directory: true);
        var resourcePath = GetServerPath(uri, uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal));
        if (!resourcePath.StartsWith(rootPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The FTP resource must remain within the configured root.", nameof(uri));
        }
    }

    private Uri BuildUri(string path)
    {
        var builder = new UriBuilder(Root.Scheme, Root.IdnHost, EffectivePort(Root))
        {
            Path = path,
        };
        return builder.Uri;
    }

    private static Uri EnsureDirectoryUri(Uri uri)
    {
        ValidateFtpUri(uri, nameof(uri));
        var builder = new UriBuilder(uri)
        {
            Path = NormalizePath(Uri.UnescapeDataString(uri.AbsolutePath), directory: true),
        };
        if (builder.Port < 0)
        {
            builder.Port = DefaultFtpPort;
        }

        return builder.Uri;
    }

    private static string GetServerPath(Uri uri, bool directory) =>
        NormalizePath(Uri.UnescapeDataString(uri.AbsolutePath), directory);

    private static string NormalizePath(string path, bool directory)
    {
        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("FTP paths cannot contain traversal segments.", nameof(path));
        }

        var normalized = "/" + string.Join('/', segments);
        if (directory && !normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return normalized;
    }

    private static string CombinePath(string directory, string name, bool isDirectory) =>
        NormalizePath(directory.TrimEnd('/') + "/" + name, isDirectory);

    private static string GetParentPath(string path)
    {
        var normalized = NormalizePath(path, directory: false);
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? "/" : normalized[..(separator + 1)];
    }

    private static int EffectivePort(Uri uri) => uri.Port < 0 ? DefaultFtpPort : uri.Port;

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        EffectivePort(left) == EffectivePort(right);

    private static void ValidateFtpUri(Uri uri, string parameterName)
    {
        if (!uri.IsAbsoluteUri ||
            (!uri.Scheme.Equals(Uri.UriSchemeFtp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The FTP endpoint must be an absolute ftp:// or ftps:// URI.", parameterName);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Credentials must not be embedded in the FTP URI.", parameterName);
        }

        if (!string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.OriginalString.IndexOfAny(['?', '#']) >= 0)
        {
            throw new ArgumentException("FTP endpoints cannot contain a query or fragment.", parameterName);
        }
    }

    private static bool UriEquals(Uri left, Uri right) =>
        Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.Ordinal) == 0;
}
