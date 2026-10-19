// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using CanX.Streaming;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace CanX.MediaAccess;

public sealed class SmbMediaSourceClient : IMediaSourceClient
{
    internal const int DefaultSmbPort = 445;
    private readonly NetworkCredential _credential;
    private readonly SmbEndpoint _endpoint;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private SmbConnection? _connection;
    private bool _disposed;

    public SmbMediaSourceClient(Uri root, NetworkCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        _endpoint = SmbEndpoint.Parse(root);
        Root = _endpoint.Root;
        _credential = credential is null
            ? new NetworkCredential(string.Empty, string.Empty)
            : new NetworkCredential(credential.UserName, credential.Password, credential.Domain);
    }

    public MediaSourceProtocol Protocol => MediaSourceProtocol.Smb;

    public Uri Root { get; }

    public static SmbMediaSourceClient Create(Uri root, NetworkCredential? credential = null) =>
        new(root, credential);

    public async ValueTask<IReadOnlyList<MediaResource>> BrowseAsync(
        Uri? directory = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = _endpoint.Resolve(directory ?? Root, directory: true);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                    return await Task.Run(
                        () => BrowseCore(connection, target, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested && NetworkMediaRetry.IsTransient(exception))
                {
                    ResetConnection();
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

    public async ValueTask<MediaResource> GetAsync(
        Uri resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        var target = _endpoint.Resolve(
            resource,
            directory: resource.AbsolutePath.EndsWith("/", StringComparison.Ordinal));
        var parent = string.IsNullOrEmpty(target.RelativePath)
            ? _endpoint.BuildUri(shareName: null, relativePath: string.Empty, directory: true)
            : _endpoint.BuildUri(target.ShareName, GetParentPath(target.RelativePath), directory: true);
        var entries = await BrowseAsync(parent, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(candidate => UriEquals(candidate.Uri, target.Uri))
            ?? throw new FileNotFoundException(
                "The SMB server did not return metadata for the requested resource.");
    }

    public async ValueTask<IMediaByteStream> OpenReadAsync(
        MediaResource resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        var target = _endpoint.Resolve(resource.Uri, directory: false);
        if (resource.IsDirectory)
        {
            throw new ArgumentException("An SMB directory cannot be opened as media.", nameof(resource));
        }

        var resolved = resource.Length is null
            ? await GetAsync(resource.Uri, cancellationToken).ConfigureAwait(false)
            : resource;
        if (target.ShareName is null)
        {
            throw new ArgumentException("The SMB resource must identify a file within a share.", nameof(resource));
        }

        return new RetryingMediaByteStream(
            () => new SmbMediaByteStream(
                () => SmbConnection.Open(_endpoint, _credential, target.ShareName),
                target.RelativePath,
                resolved.Length),
            resolved.Length,
            canSeek: true);
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
            ResetConnection();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task<SmbConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        _connection = await Task.Run(
            () => SmbConnection.Open(_endpoint, _credential, shareName: null),
            cancellationToken).ConfigureAwait(false);
        return _connection;
    }

    private void ResetConnection()
    {
        try
        {
            _connection?.Dispose();
        }
        catch (Exception)
        {
            // A failed SMB transport may already have torn down its socket.
        }

        _connection = null;
    }

    private IReadOnlyList<MediaResource> BrowseCore(
        SmbConnection connection,
        SmbResolvedPath target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.ShareName is null)
        {
            var shareNames = connection.ListShares();
            return shareNames
                .Where(static shareName =>
                    SmbEndpoint.IsSafeSegment(shareName) &&
                    !shareName.Equals("IPC$", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static shareName => shareName, StringComparer.OrdinalIgnoreCase)
                .Select(shareName => new MediaResource
                {
                    Uri = _endpoint.BuildUri(shareName, relativePath: string.Empty, directory: true),
                    Name = shareName,
                    IsDirectory = true,
                })
                .ToArray();
        }

        connection.ConnectShare(target.ShareName);
        var fileStore = connection.FileStore;
        object? handle = null;
        try
        {
            var status = fileStore.CreateFile(
                out handle,
                out _,
                target.RelativePath,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                SmbFileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            EnsureSuccess(status, "open directory");

            status = fileStore.QueryDirectory(
                out var entries,
                handle,
                "*",
                FileInformationClass.FileDirectoryInformation);
            if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
            {
                EnsureSuccess(status, "list directory");
            }

            var resources = new List<MediaResource>(entries.Count);
            foreach (var entry in entries.OfType<FileDirectoryInformation>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FileName is "." or ".." || !SmbEndpoint.IsSafeSegment(entry.FileName))
                {
                    continue;
                }

                var isDirectory = (entry.FileAttributes & SmbFileAttributes.Directory) != 0;
                var relativePath = CombinePath(target.RelativePath, entry.FileName);
                resources.Add(new MediaResource
                {
                    Uri = _endpoint.BuildUri(target.ShareName, relativePath, isDirectory),
                    Name = entry.FileName,
                    IsDirectory = isDirectory,
                    Length = isDirectory || entry.EndOfFile < 0 ? null : entry.EndOfFile,
                    LastModified = ToDateTimeOffset(entry.LastWriteTime),
                });
            }

            return resources
                .OrderByDescending(static resource => resource.IsDirectory)
                .ThenBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            if (handle is not null)
            {
                _ = fileStore.CloseFile(handle);
            }
        }
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static string CombinePath(string directory, string name) =>
        string.IsNullOrEmpty(directory) ? name : $"{directory.TrimEnd('\\')}\\{name}";

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static void EnsureSuccess(NTStatus status, string operation)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new SmbStatusException(operation, status);
        }
    }

    private static bool UriEquals(Uri left, Uri right) =>
        Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
}

internal sealed record SmbResolvedPath(Uri Uri, string? ShareName, string RelativePath);

internal sealed class SmbEndpoint
{
    private SmbEndpoint(Uri root, string host, int port, string? shareName, string[] rootSegments)
    {
        Root = root;
        Host = host;
        Port = port;
        ShareName = shareName;
        RootSegments = rootSegments;
    }

    public Uri Root { get; private set; }

    public string Host { get; }

    public int Port { get; }

    public string? ShareName { get; }

    public string[] RootSegments { get; }

    public static SmbEndpoint Parse(Uri root)
    {
        ValidateUri(root, nameof(root));
        var segments = ReadSegments(root);
        var port = root.Port < 0 ? SmbMediaSourceClient.DefaultSmbPort : root.Port;
        var endpoint = new SmbEndpoint(
            root,
            root.IdnHost,
            port,
            segments.Length == 0 ? null : segments[0],
            segments.Length == 0 ? [] : segments[1..]);
        endpoint.Root = endpoint.BuildUri(
            endpoint.ShareName,
            JoinPath(endpoint.RootSegments),
            directory: true);
        return endpoint;
    }

    public SmbResolvedPath Resolve(Uri uri, bool directory)
    {
        ValidateUri(uri, nameof(uri));
        var port = uri.Port < 0 ? SmbMediaSourceClient.DefaultSmbPort : uri.Port;
        if (!string.Equals(uri.IdnHost, Host, StringComparison.OrdinalIgnoreCase) || port != Port)
        {
            throw new ArgumentException("The SMB resource must remain on the configured server.", nameof(uri));
        }

        var segments = ReadSegments(uri);
        if (ShareName is null)
        {
            if (segments.Length == 0)
            {
                return new SmbResolvedPath(
                    BuildUri(shareName: null, relativePath: string.Empty, directory: true),
                    ShareName: null,
                    RelativePath: string.Empty);
            }

            var selectedShare = segments[0];
            var selectedRelativePath = JoinPath(segments[1..]);
            return new SmbResolvedPath(
                BuildUri(selectedShare, selectedRelativePath, directory),
                selectedShare,
                selectedRelativePath);
        }

        if (segments.Length == 0 ||
            !string.Equals(segments[0], ShareName, StringComparison.OrdinalIgnoreCase) ||
            segments.Length - 1 < RootSegments.Length)
        {
            throw new ArgumentException("The SMB resource must remain within the configured share root.", nameof(uri));
        }

        for (var index = 0; index < RootSegments.Length; index++)
        {
            if (!string.Equals(segments[index + 1], RootSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The SMB resource must remain within the configured share root.", nameof(uri));
            }
        }

        var relativePath = JoinPath(segments[1..]);
        return new SmbResolvedPath(BuildUri(ShareName, relativePath, directory), ShareName, relativePath);
    }

    public Uri BuildUri(string? shareName, string relativePath, bool directory)
    {
        if (shareName is not null && !IsSafeSegment(shareName))
        {
            throw new ArgumentException("The SMB share name is invalid.", nameof(shareName));
        }

        var segments = string.IsNullOrEmpty(relativePath)
            ? Array.Empty<string>()
            : relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => !IsSafeSegment(segment)))
        {
            throw new ArgumentException("SMB paths cannot contain traversal or separator segments.", nameof(relativePath));
        }

        if (shareName is null && segments.Length != 0)
        {
            throw new ArgumentException("An SMB path cannot be built without a share name.", nameof(relativePath));
        }

        var pathSegments = shareName is null
            ? Enumerable.Empty<string>()
            : new[] { shareName }.Concat(segments).Select(Uri.EscapeDataString);
        var builder = new UriBuilder("smb", Host)
        {
            Port = Port == SmbMediaSourceClient.DefaultSmbPort ? -1 : Port,
            Path = "/" + string.Join('/', pathSegments) +
                (shareName is not null && directory ? "/" : string.Empty),
        };
        return builder.Uri;
    }

    public static bool IsSafeSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment) &&
        segment is not "." and not ".." &&
        segment.IndexOfAny(['/', '\\', '\0']) < 0 &&
        !segment.Any(char.IsControl);

    private static string[] ReadSegments(Uri uri)
    {
        try
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            if (segments.Any(static segment => !IsSafeSegment(segment)))
            {
                throw new ArgumentException("SMB paths cannot contain traversal or separator segments.", nameof(uri));
            }

            return segments;
        }
        catch (UriFormatException exception)
        {
            throw new ArgumentException("The SMB endpoint contains invalid path escaping.", nameof(uri), exception);
        }
    }

    private static string JoinPath(IEnumerable<string> segments) => string.Join('\\', segments);

    private static void ValidateUri(Uri uri, string parameterName)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals("smb", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The SMB endpoint must be an absolute smb:// URI.", parameterName);
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("The SMB endpoint must include a server name.", parameterName);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Credentials must not be embedded in the SMB URI.", parameterName);
        }

        if (!string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.OriginalString.IndexOfAny(['?', '#']) >= 0)
        {
            throw new ArgumentException("SMB endpoints cannot contain a query or fragment.", parameterName);
        }
    }
}

internal sealed class SmbConnection : IDisposable
{
    private readonly PortAwareSmb2Client _client;
    private ISMBFileStore? _fileStore;
    private string? _shareName;
    private int _disposed;

    private SmbConnection(PortAwareSmb2Client client)
    {
        _client = client;
    }

    public ISMBFileStore FileStore => _fileStore ??
        throw new InvalidOperationException("No SMB share is connected.");

    public static SmbConnection Open(
        SmbEndpoint endpoint,
        NetworkCredential credential,
        string? shareName)
    {
        var client = new PortAwareSmb2Client();
        try
        {
            if (!client.Connect(endpoint.Host, endpoint.Port))
            {
                throw new IOException("SMB server connection failed.");
            }

            var (domain, userName) = SplitIdentity(credential);
            var status = client.Login(domain, userName, credential.Password ?? string.Empty);
            EnsureSuccess(status, "authentication");
            var connection = new SmbConnection(client);
            if (shareName is not null)
            {
                connection.ConnectShare(shareName);
            }

            return connection;
        }
        catch
        {
            try
            {
                client.Disconnect();
            }
            catch (Exception)
            {
                // Preserve the original connection/authentication failure.
            }

            throw;
        }
    }

    public IReadOnlyList<string> ListShares()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var shares = _client.ListShares(out var status);
        EnsureSuccess(status, "share enumeration");
        return shares ?? throw new IOException("The SMB server returned no share list.");
    }

    public void ConnectShare(string shareName)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(shareName);
        if (_fileStore is not null &&
            string.Equals(_shareName, shareName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_fileStore is not null)
        {
            _ = _fileStore.Disconnect();
            _fileStore = null;
            _shareName = null;
        }

        var fileStore = _client.TreeConnect(shareName, out var status);
        try
        {
            EnsureSuccess(status, "share connection");
            if (fileStore is null)
            {
                throw new IOException("The SMB server did not return a share connection.");
            }

            _fileStore = fileStore;
            _shareName = shareName;
        }
        catch
        {
            if (fileStore is not null)
            {
                _ = fileStore.Disconnect();
            }

            throw;
        }
    }

    public void Abort() => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (_fileStore is not null)
            {
                _ = _fileStore.Disconnect();
            }
        }
        catch (Exception)
        {
            // A cancelled read can close the socket before tree disconnect.
        }

        try
        {
            _ = _client.Logoff();
        }
        catch (Exception)
        {
            // Best-effort protocol shutdown only.
        }

        try
        {
            _client.Disconnect();
        }
        catch (Exception)
        {
            // The socket may already be gone after cancellation or a network failure.
        }
    }

    private static (string Domain, string UserName) SplitIdentity(NetworkCredential credential)
    {
        var domain = credential.Domain ?? string.Empty;
        var userName = credential.UserName ?? string.Empty;
        if (string.IsNullOrEmpty(domain))
        {
            var separator = userName.IndexOf('\\');
            if (separator > 0)
            {
                domain = userName[..separator];
                userName = userName[(separator + 1)..];
            }
        }

        return (domain, userName);
    }

    private static void EnsureSuccess(NTStatus status, string operation)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new SmbStatusException(operation, status);
        }
    }

    private sealed class PortAwareSmb2Client : SMB2Client
    {
        public PortAwareSmb2Client()
            : base(responseTimeoutInMilliseconds: 15_000, enableSMB311Support: true)
        {
        }

        public bool Connect(string host, int port)
        {
            if (port == SmbMediaSourceClient.DefaultSmbPort)
            {
                return base.Connect(host, SMBTransportType.DirectTCPTransport);
            }

            var addresses = Dns.GetHostAddresses(host);
            if (addresses.Length == 0)
            {
                throw new IOException("The SMB server name could not be resolved.");
            }

            return base.Connect(
                addresses.FirstOrDefault(static address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ??
                    addresses[0],
                SMBTransportType.DirectTCPTransport,
                port);
        }
    }
}

internal sealed class SmbStatusException : IOException
{
    public SmbStatusException(string operation, NTStatus status)
        : base($"SMB {operation} failed with status {status}.")
    {
        IsTransient = status is
            NTStatus.STATUS_IO_TIMEOUT or
            NTStatus.STATUS_NETWORK_NAME_DELETED or
            NTStatus.STATUS_USER_SESSION_DELETED;
    }

    public bool IsTransient { get; }
}
