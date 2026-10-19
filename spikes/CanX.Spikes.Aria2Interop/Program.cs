using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CanX.Spikes.Aria2Interop;

internal static class Program
{
    private const int DownloadPaused = 2;
    private const int DownloadComplete = 3;
    private const int DownloadError = 4;

    public static unsafe int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: CanX.Spikes.Aria2Interop <uri> <output-directory> <output-name>");
            return 2;
        }

        Directory.CreateDirectory(args[1]);
        var callbackState = new CallbackState();
        var callbackHandle = GCHandle.Alloc(callbackState);
        nint session = 0;
        var initialized = false;

        try
        {
            Ensure(Aria2Native.AbiVersion() == 5 ? 0 : -1, session, "ABI version");
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
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, ulong, void>)&OnDownloadEvent,
                    GCHandle.ToIntPtr(callbackHandle),
                    &session),
                session,
                "session create");

            using var taskOptions = new NativeOptionList(
                ("dir", args[1]),
                ("out", args[2]),
                ("file-allocation", "none"),
                ("piece-length", "1M"));

            ulong gid = 0;
            Ensure(
                Aria2Native.AddUri(session, args[0], taskOptions.Pointer, taskOptions.Count, -1, &gid),
                session,
                "add URI");

            Ensure(Aria2Native.Pause(session, gid, 0), session, "pause");
            Ensure(Aria2Native.RunOnce(session) < 0 ? -1 : 0, session, "run paused task");
            var paused = GetStatus(session, gid);
            Ensure(paused.Status == DownloadPaused ? 0 : -1, session, "observe paused status");
            Ensure(Aria2Native.Unpause(session, gid), session, "unpause");

            using (var changedOptions = new NativeOptionList(("max-download-limit", "0")))
            {
                Ensure(
                    Aria2Native.ChangeOptions(session, gid, changedOptions.Pointer, changedOptions.Count),
                    session,
                    "change options");
            }

            var stopwatch = Stopwatch.StartNew();
            ulong lastReportedLength = 0;
            Aria2Status status;
            var priorityVerified = false;
            var priorityUnsupported = false;
            do
            {
                Ensure(Aria2Native.RunOnce(session) < 0 ? -1 : 0, session, "run once");
                status = GetStatus(session, gid);
                if (!priorityVerified && status.Status == 0 && status.NumPieces > 0)
                {
                    var priorityResult = Aria2Native.PrioritizePieceRange(session, gid, 0, 0);
                    if (priorityResult == -1004)
                    {
                        priorityUnsupported = true;
                    }
                    else
                    {
                        Ensure(priorityResult, session, "prioritize first piece");
                        priorityVerified = true;
                    }
                }
                _ = GetDownloadJson(session, gid);
                if (status.Status != 0 || status.CompletedLength - lastReportedLength >= 1024 * 1024)
                {
                    Console.WriteLine(
                        $"ARIA2_PROGRESS gid={gid:x16} status={status.Status} " +
                        $"completed={status.CompletedLength} total={status.TotalLength}");
                    lastReportedLength = status.CompletedLength;
                }

                if (status.Status == DownloadError)
                {
                    throw new InvalidOperationException($"download failed with aria2 error {status.ErrorCode}");
                }
            }
            while (status.Status != DownloadComplete && stopwatch.Elapsed < TimeSpan.FromSeconds(30));

            Ensure(status.Status == DownloadComplete ? 0 : -1, session, "download completion timeout");
            Ensure(priorityVerified || priorityUnsupported ? 0 : -1, session, "runtime piece priority");
            Ensure(callbackState.Completed > 0 ? 0 : -1, session, "completion callback");

            var globalStatus = new Aria2GlobalStatus { StructSize = (uint)sizeof(Aria2GlobalStatus) };
            Ensure(Aria2Native.GetGlobalStatus(session, &globalStatus), session, "get global status");

            var detail = GetDownloadJson(session, gid);
            Ensure(detail.Contains($"\"gid\":\"{gid:x16}\"", StringComparison.Ordinal) ? 0 : -1, session, "download JSON content");

            Ensure(Aria2Native.Shutdown(session, 0), session, "shutdown");
            while (Aria2Native.RunOnce(session) > 0)
            {
            }

            Console.WriteLine(
                $"CANX_ARIA2_SHIM_OK gid={gid:x16} bytes={status.CompletedLength} " +
                $"events={callbackState.Total} completedEvents={callbackState.Completed}");
            return 0;
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
            callbackHandle.Free();
        }
    }

    private static unsafe Aria2Status GetStatus(nint session, ulong gid)
    {
        var status = new Aria2Status { StructSize = (uint)sizeof(Aria2Status) };
        Ensure(Aria2Native.GetStatus(session, gid, &status), session, "get status");
        return status;
    }

    private static unsafe string GetDownloadJson(nint session, ulong gid)
    {
        var json = new Aria2String();
        Ensure(Aria2Native.GetDownloadJson(session, gid, &json), session, "get download JSON");
        try
        {
            return Marshal.PtrToStringUTF8(json.Data, checked((int)json.Length))
                ?? throw new InvalidOperationException("download JSON was null");
        }
        finally
        {
            Aria2Native.FreeString(&json);
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDownloadEvent(nint context, int downloadEvent, ulong gid)
    {
        try
        {
            var state = (CallbackState?)GCHandle.FromIntPtr(context).Target;
            if (state is null)
            {
                return;
            }

            Interlocked.Increment(ref state.Total);
            if (downloadEvent == 4)
            {
                Interlocked.Increment(ref state.Completed);
            }
        }
        catch
        {
            // Exceptions must never cross the C callback boundary.
        }
    }

    private sealed class CallbackState
    {
        public int Total;
        public int Completed;
    }
}
