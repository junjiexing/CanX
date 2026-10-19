using CanX.Spikes.Aria2Interop;

namespace CanX.Spikes.Streaming;

internal sealed class AriaDownloadCoordinator
{
    private const int DownloadComplete = 3;
    private const int DownloadError = 4;

    private readonly string _sourceUri;
    private readonly string _destinationPath;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _completedLength;
    private long _totalLength;
    private int _status;

    public AriaDownloadCoordinator(string sourceUri, string destinationPath)
    {
        _sourceUri = sourceUri;
        _destinationPath = destinationPath;
    }

    public Task Completion => _completion.Task;

    public long TotalLength => Interlocked.Read(ref _totalLength);

    public long AvailableLength
    {
        get
        {
            var reported = Interlocked.Read(ref _completedLength);
            try
            {
                return Math.Min(reported, new FileInfo(_destinationPath).Length);
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    public bool IsComplete => Volatile.Read(ref _status) == DownloadComplete;

    public bool IsFailed => Volatile.Read(ref _status) == DownloadError;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CanX aria2 spike engine",
        };
        thread.Start();
        await _started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task<long> WaitForMetadataAsync(CancellationToken cancellationToken)
    {
        while (TotalLength <= 0)
        {
            await ThrowIfFailedAndDelayAsync(cancellationToken);
        }
        return TotalLength;
    }

    public async Task<long> WaitForOffsetAsync(long offset, CancellationToken cancellationToken)
    {
        while (AvailableLength <= offset && !IsComplete)
        {
            await ThrowIfFailedAndDelayAsync(cancellationToken);
        }
        return AvailableLength;
    }

    private async Task ThrowIfFailedAndDelayAsync(CancellationToken cancellationToken)
    {
        if (IsFailed)
        {
            throw new IOException("aria2 download failed");
        }
        await Task.Delay(10, cancellationToken);
    }

    private unsafe void Run()
    {
        nint session = 0;
        var initialized = false;

        try
        {
            Ensure(Aria2Native.LibraryInit(), session, "library init");
            initialized = true;

            using var sessionOptions = new NativeOptionList(
                ("enable-color", "false"),
                ("auto-file-renaming", "false"),
                ("allow-overwrite", "true"),
                ("max-concurrent-downloads", "1"));
            Ensure(
                Aria2Native.SessionCreate(
                    sessionOptions.Pointer,
                    sessionOptions.Count,
                    0,
                    0,
                    &session),
                session,
                "session create");

            var destinationDirectory = Path.GetDirectoryName(_destinationPath)
                ?? throw new InvalidOperationException("destination directory is missing");
            var destinationName = Path.GetFileName(_destinationPath);
            Directory.CreateDirectory(destinationDirectory);

            using var taskOptions = new NativeOptionList(
                ("dir", destinationDirectory),
                ("out", destinationName),
                ("file-allocation", "none"),
                ("split", "1"),
                ("stream-piece-selector", "inorder"),
                ("piece-length", "1M"));

            ulong gid = 0;
            Ensure(
                Aria2Native.AddUri(
                    session,
                    _sourceUri,
                    taskOptions.Pointer,
                    taskOptions.Count,
                    -1,
                    &gid),
                session,
                "add URI");
            _started.TrySetResult();

            while (true)
            {
                EnsureRunResult(Aria2Native.RunOnce(session), session);
                var status = GetStatus(session, gid);
                Interlocked.Exchange(ref _completedLength, checked((long)status.CompletedLength));
                Interlocked.Exchange(ref _totalLength, checked((long)status.TotalLength));
                Volatile.Write(ref _status, status.Status);

                if (status.Status == DownloadComplete)
                {
                    Console.WriteLine(
                        $"CANX_STREAM_DOWNLOAD_COMPLETE bytes={status.CompletedLength} gid={gid:x16}");
                    _completion.TrySetResult();
                    break;
                }
                if (status.Status == DownloadError)
                {
                    throw new IOException($"aria2 download failed with error {status.ErrorCode}");
                }
            }

            Ensure(Aria2Native.Shutdown(session, 0), session, "shutdown");
            while (Aria2Native.RunOnce(session) > 0)
            {
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _status, DownloadError);
            _started.TrySetException(exception);
            _completion.TrySetException(exception);
        }
        finally
        {
            if (session != 0)
            {
                Aria2Native.SessionDestroy(session);
            }
            if (initialized)
            {
                Aria2Native.LibraryDeinit();
            }
        }
    }

    private static unsafe Aria2Status GetStatus(nint session, ulong gid)
    {
        var status = new Aria2Status { StructSize = (uint)sizeof(Aria2Status) };
        Ensure(Aria2Native.GetStatus(session, gid, &status), session, "get status");
        return status;
    }

    private static void EnsureRunResult(int result, nint session)
    {
        if (result < 0)
        {
            Ensure(result, session, "run once");
        }
    }

    private static void Ensure(int result, nint session, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed ({result}): {Aria2Native.GetLastError(session)}");
        }
    }
}
