// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace CanX.Downloads.Interop;

internal static partial class Aria2Native
{
    internal const uint ExpectedAbiVersion = 6;
    private const string LibraryName = "canx_aria2";

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_library_init")]
    internal static partial int LibraryInit();

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_library_deinit")]
    internal static partial int LibraryDeinit();

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_session_create")]
    internal static unsafe partial int SessionCreate(
        NativeOption* options,
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
        NativeOption* options,
        nuint optionCount,
        int position,
        ulong* gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_add_uris")]
    internal static unsafe partial int AddUris(
        nint session,
        nint* uris,
        nuint uriCount,
        NativeOption* options,
        nuint optionCount,
        int position,
        ulong* gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_add_torrent", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int AddTorrent(
        nint session,
        string torrentFile,
        NativeOption* options,
        nuint optionCount,
        int position,
        ulong* gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_add_metalink", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int AddMetalink(
        nint session,
        string metalinkFile,
        NativeOption* options,
        nuint optionCount,
        int position,
        NativeGidList* gids);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_gid_list_free")]
    internal static unsafe partial void FreeGidList(NativeGidList* gids);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_run_once")]
    internal static partial int RunOnce(nint session);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_download_json")]
    internal static unsafe partial int GetDownloadJson(nint session, ulong gid, NativeString* json);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_global_options_json")]
    internal static unsafe partial int GetGlobalOptionsJson(nint session, NativeString* json);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_capabilities_json")]
    internal static unsafe partial int GetCapabilitiesJson(nint session, NativeString* json);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_peers_json")]
    internal static unsafe partial int GetPeersJson(nint session, ulong gid, NativeString* json);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_servers_json")]
    internal static unsafe partial int GetServersJson(nint session, ulong gid, NativeString* json);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_string_free")]
    internal static unsafe partial void FreeString(NativeString* value);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_get_global_status")]
    internal static unsafe partial int GetGlobalStatus(nint session, NativeGlobalStatus* status);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_pause")]
    internal static partial int Pause(nint session, ulong gid, int force);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_unpause")]
    internal static partial int Resume(nint session, ulong gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_remove")]
    internal static partial int Remove(nint session, ulong gid, int force);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_remove_result")]
    internal static partial int RemoveResult(nint session, ulong gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_change_options")]
    internal static unsafe partial int ChangeOptions(
        nint session,
        ulong gid,
        NativeOption* options,
        nuint optionCount);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_change_global_options")]
    internal static unsafe partial int ChangeGlobalOptions(
        nint session,
        NativeOption* options,
        nuint optionCount);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_change_position")]
    internal static unsafe partial int ChangePosition(
        nint session,
        ulong gid,
        int offset,
        int mode,
        int* position);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_prioritize_piece_range")]
    internal static partial int PrioritizePieceRange(
        nint session,
        ulong gid,
        nuint firstPiece,
        nuint lastPiece);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_clear_piece_priority")]
    internal static partial int ClearPiecePriority(nint session, ulong gid);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_shutdown")]
    internal static partial int Shutdown(nint session, int force);

    [LibraryImport(LibraryName, EntryPoint = "canx_aria2_last_error")]
    private static partial nint LastError(nint session);

    internal static string GetLastError(nint session) =>
        Marshal.PtrToStringUTF8(LastError(session)) ?? "Unknown libaria2 bridge error.";
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOption
{
    public nint Key;
    public nint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGidList
{
    public nint Items;
    public nuint Count;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeString
{
    public nint Data;
    public nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGlobalStatus
{
    public uint StructSize;
    public int DownloadSpeed;
    public int UploadSpeed;
    public int ActiveCount;
    public int WaitingCount;
    public int StoppedCount;
}
