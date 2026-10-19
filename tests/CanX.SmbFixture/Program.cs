// SPDX-License-Identifier: GPL-3.0-or-later

#nullable disable

using System.Net;
using Microsoft.Win32.SafeHandles;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SmbFileAttributes = SMBLibrary.FileAttributes;

if (args.Length != 5 || !int.TryParse(args[1], out var port))
{
    Console.Error.WriteLine("usage: CanX.SmbFixture <root> <port> <username> <password> <ready-file>");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var userName = args[2];
var password = args[3];
var readyFile = Path.GetFullPath(args[4]);
var shares = new SMBShareCollection
{
    new FileSystemShare("media", new ReadOnlyDirectoryFileStore(root)),
};
var authentication = new IndependentNTLMAuthenticationProvider(
    candidate => string.Equals(candidate, userName, StringComparison.OrdinalIgnoreCase) ? password : null);
var server = new PortAwareSmbServer(shares, new GSSProvider(authentication));
using var stopping = new ManualResetEventSlim();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Set();

try
{
    server.Start(IPAddress.Loopback, port);
    File.WriteAllText(readyFile, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Console.WriteLine($"CANX_SMB_FIXTURE_READY port={port}");
    stopping.Wait();
    return 0;
}
finally
{
    server.Stop();
}

internal sealed class PortAwareSmbServer(SMBShareCollection shares, GSSProvider securityProvider)
    : SMBServer(shares, securityProvider)
{
    public void Start(IPAddress address, int port) =>
        base.Start(
            address,
            SMBTransportType.DirectTCPTransport,
            port,
            enableSMB1: false,
            enableSMB2: true,
            enableSMB3: true,
            connectionInactivityTimeout: TimeSpan.FromSeconds(30));
}

internal sealed class ReadOnlyDirectoryFileStore : INTFileStore
{
    private readonly string _root;
    private readonly string _rootPrefix;

    public ReadOnlyDirectoryFileStore(string root)
    {
        _root = Path.GetFullPath(root);
        _rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public NTStatus CreateFile(
        out object handle,
        out FileStatus fileStatus,
        string path,
        AccessMask desiredAccess,
        SmbFileAttributes fileAttributes,
        ShareAccess shareAccess,
        CreateDisposition createDisposition,
        CreateOptions createOptions,
        SecurityContext securityContext)
    {
        handle = null;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
        if (createDisposition != CreateDisposition.FILE_OPEN)
        {
            return NTStatus.STATUS_ACCESS_DENIED;
        }

        string localPath;
        try
        {
            localPath = Resolve(path);
        }
        catch (ArgumentException)
        {
            return NTStatus.STATUS_OBJECT_PATH_INVALID;
        }

        var isDirectory = Directory.Exists(localPath);
        var isFile = File.Exists(localPath);
        if (!isDirectory && !isFile)
        {
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
        }

        if (isDirectory && (createOptions & CreateOptions.FILE_NON_DIRECTORY_FILE) != 0)
        {
            return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
        }

        if (isFile && (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0)
        {
            return NTStatus.STATUS_NOT_A_DIRECTORY;
        }

        handle = new FixtureHandle(localPath, isDirectory);
        fileStatus = FileStatus.FILE_OPENED;
        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus CloseFile(object handle)
    {
        (handle as FixtureHandle)?.Dispose();
        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
    {
        data = Array.Empty<byte>();
        if (handle is not FixtureHandle { IsDirectory: false } file || offset < 0 || maxCount < 0)
        {
            return NTStatus.STATUS_INVALID_PARAMETER;
        }

        if (offset >= file.Length)
        {
            return NTStatus.STATUS_END_OF_FILE;
        }

        data = new byte[Math.Min(maxCount, checked((int)Math.Min(int.MaxValue, file.Length - offset)))];
        var read = RandomAccess.Read(file.FileHandle, data, offset);
        if (read == 0)
        {
            data = Array.Empty<byte>();
            return NTStatus.STATUS_END_OF_FILE;
        }

        if (read != data.Length)
        {
            Array.Resize(ref data, read);
        }

        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus QueryDirectory(
        out List<QueryDirectoryFileInformation> result,
        object handle,
        string fileName,
        FileInformationClass informationClass)
    {
        result = [];
        if (handle is not FixtureHandle { IsDirectory: true } directory ||
            informationClass != FileInformationClass.FileDirectoryInformation)
        {
            return NTStatus.STATUS_INVALID_PARAMETER;
        }

        result.Add(CreateDirectoryEntry(".", new DirectoryInfo(directory.Path)));
        var parent = Directory.GetParent(directory.Path);
        result.Add(CreateDirectoryEntry("..", parent ?? new DirectoryInfo(directory.Path)));
        foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & System.IO.FileAttributes.Directory) != 0;
            var info = new FileInfo(path);
            result.Add(new FileDirectoryInformation
            {
                FileName = Path.GetFileName(path),
                CreationTime = File.GetCreationTimeUtc(path),
                LastAccessTime = File.GetLastAccessTimeUtc(path),
                LastWriteTime = File.GetLastWriteTimeUtc(path),
                ChangeTime = File.GetLastWriteTimeUtc(path),
                EndOfFile = isDirectory ? 0 : info.Length,
                AllocationSize = isDirectory ? 0 : info.Length,
                FileAttributes = isDirectory ? SmbFileAttributes.Directory : SmbFileAttributes.Normal,
            });
        }

        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus GetFileInformation(
        out FileInformation result,
        object handle,
        FileInformationClass informationClass)
    {
        result = null;
        if (handle is not FixtureHandle entry ||
            informationClass != FileInformationClass.FileNetworkOpenInformation)
        {
            return NTStatus.STATUS_INVALID_INFO_CLASS;
        }

        var length = entry.IsDirectory ? 0 : entry.Length;
        result = new FileNetworkOpenInformation
        {
            CreationTime = File.GetCreationTimeUtc(entry.Path),
            LastAccessTime = File.GetLastAccessTimeUtc(entry.Path),
            LastWriteTime = File.GetLastWriteTimeUtc(entry.Path),
            ChangeTime = File.GetLastWriteTimeUtc(entry.Path),
            AllocationSize = length,
            EndOfFile = length,
            FileAttributes = entry.IsDirectory ? SmbFileAttributes.Directory : SmbFileAttributes.Normal,
        };
        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
    {
        numberOfBytesWritten = 0;
        return NTStatus.STATUS_ACCESS_DENIED;
    }

    public NTStatus FlushFileBuffers(object handle) => NTStatus.STATUS_ACCESS_DENIED;

    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) =>
        NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus UnlockFile(object handle, long byteOffset, long length) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus SetFileInformation(object handle, FileInformation information) => NTStatus.STATUS_ACCESS_DENIED;

    public NTStatus GetFileSystemInformation(
        out FileSystemInformation result,
        FileSystemInformationClass informationClass)
    {
        result = null;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_ACCESS_DENIED;

    public NTStatus GetSecurityInformation(
        out SecurityDescriptor result,
        object handle,
        SecurityInformation securityInformation)
    {
        result = null;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus SetSecurityInformation(
        object handle,
        SecurityInformation securityInformation,
        SecurityDescriptor securityDescriptor) => NTStatus.STATUS_ACCESS_DENIED;

    public NTStatus NotifyChange(
        out object ioRequest,
        object handle,
        NotifyChangeFilter completionFilter,
        bool watchTree,
        int outputBufferSize,
        OnNotifyChangeCompleted onNotifyChangeCompleted,
        object context)
    {
        ioRequest = null;
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus DeviceIOControl(
        object handle,
        uint ctlCode,
        byte[] input,
        out byte[] output,
        int maxOutputLength)
    {
        output = Array.Empty<byte>();
        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    private FileDirectoryInformation CreateDirectoryEntry(string name, DirectoryInfo directory) => new()
    {
        FileName = name,
        CreationTime = directory.CreationTimeUtc,
        LastAccessTime = directory.LastAccessTimeUtc,
        LastWriteTime = directory.LastWriteTimeUtc,
        ChangeTime = directory.LastWriteTimeUtc,
        FileAttributes = SmbFileAttributes.Directory,
    };

    private string Resolve(string path)
    {
        var segments = path
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment =>
                segment is "." or ".." || segment.IndexOfAny(['/', '\\', ':', '\0']) >= 0))
        {
            throw new ArgumentException("Invalid SMB fixture path.", nameof(path));
        }

        var resolved = Path.GetFullPath(Path.Combine([_root, .. segments]));
        if (!string.Equals(resolved, _root, StringComparison.Ordinal) &&
            !resolved.StartsWith(_rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("SMB fixture path escaped its root.", nameof(path));
        }

        return resolved;
    }

    private sealed class FixtureHandle : IDisposable
    {
        public FixtureHandle(string path, bool isDirectory)
        {
            Path = path;
            IsDirectory = isDirectory;
            if (!isDirectory)
            {
                FileHandle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.RandomAccess);
                Length = RandomAccess.GetLength(FileHandle);
            }
        }

        public string Path { get; }

        public bool IsDirectory { get; }

        public SafeFileHandle FileHandle { get; }

        public long Length { get; }

        public void Dispose() => FileHandle?.Dispose();
    }
}
