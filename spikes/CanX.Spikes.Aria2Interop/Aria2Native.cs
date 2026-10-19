using System.Runtime.InteropServices;

namespace CanX.Spikes.Aria2Interop;

internal static partial class Aria2Native
{
    private const string LibraryName = "canx_aria2";

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_library_init")]
    internal static partial int LibraryInit();

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_library_deinit")]
    internal static partial int LibraryDeinit();

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_session_create")]
    internal static unsafe partial int SessionCreate(
        Aria2Option* options,
        nuint optionCount,
        nint callback,
        nint callbackContext,
        nint* session);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_session_destroy")]
    internal static partial int SessionDestroy(nint session);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_add_uri", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int AddUri(
        nint session,
        string uri,
        Aria2Option* options,
        nuint optionCount,
        int position,
        ulong* gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_download_json")]
    internal static unsafe partial int GetDownloadJson(nint session, ulong gid, Aria2String* json);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_global_status")]
    internal static unsafe partial int GetGlobalStatus(nint session, Aria2GlobalStatus* status);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_change_options")]
    internal static unsafe partial int ChangeOptions(
        nint session,
        ulong gid,
        Aria2Option* options,
        nuint optionCount);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_string_free")]
    internal static unsafe partial void FreeString(Aria2String* value);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_run_once")]
    internal static partial int RunOnce(nint session);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_status")]
    internal static unsafe partial int GetStatus(nint session, ulong gid, Aria2Status* status);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_pause")]
    internal static partial int Pause(nint session, ulong gid, int force);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_unpause")]
    internal static partial int Unpause(nint session, ulong gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_prioritize_piece_range")]
    internal static partial int PrioritizePieceRange(
        nint session,
        ulong gid,
        nuint firstPiece,
        nuint lastPiece);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_shutdown")]
    internal static partial int Shutdown(nint session, int force);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_last_error")]
    private static partial nint LastError(nint session);

    internal static string GetLastError(nint session) =>
        Marshal.PtrToStringUTF8(LastError(session)) ?? "unknown aria2 shim error";
}

[StructLayout(LayoutKind.Sequential)]
internal struct Aria2Option
{
    public nint Key;
    public nint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Aria2Status
{
    public uint StructSize;
    public int Status;
    public ulong TotalLength;
    public ulong CompletedLength;
    public ulong UploadedLength;
    public ulong PieceLength;
    public int DownloadSpeed;
    public int UploadSpeed;
    public int NumPieces;
    public int Connections;
    public int ErrorCode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Aria2GlobalStatus
{
    public uint StructSize;
    public int DownloadSpeed;
    public int UploadSpeed;
    public int NumActive;
    public int NumWaiting;
    public int NumStopped;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Aria2String
{
    public nint Data;
    public nuint Length;
}
