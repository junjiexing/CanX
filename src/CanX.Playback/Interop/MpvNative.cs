// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace CanX.Playback.Interop;

internal static partial class MpvNative
{
    private const string LibraryName = "mpv";

    [LibraryImport(LibraryName, EntryPoint = "mpv_client_api_version")]
    internal static partial ulong ClientApiVersion();

    [LibraryImport(LibraryName, EntryPoint = "mpv_create")]
    internal static partial nint Create();

    [LibraryImport(LibraryName, EntryPoint = "mpv_initialize")]
    internal static partial int Initialize(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SetOptionString(nint handle, string name, string value);

    [LibraryImport(LibraryName, EntryPoint = "mpv_command")]
    internal static unsafe partial int Command(nint handle, nint* arguments);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int GetProperty(nint handle, string name, MpvFormat format, void* data);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetPropertyString(nint handle, string name);

    [LibraryImport(LibraryName, EntryPoint = "mpv_free")]
    internal static partial void Free(nint data);

    [LibraryImport(LibraryName, EntryPoint = "mpv_free_node_contents")]
    internal static unsafe partial void FreeNodeContents(MpvNode* node);

    [LibraryImport(LibraryName, EntryPoint = "mpv_wait_event")]
    internal static partial nint WaitEvent(nint handle, double timeout);

    [LibraryImport(LibraryName, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RequestLogMessages(nint handle, string minimumLevel);

    [LibraryImport(LibraryName, EntryPoint = "mpv_stream_cb_add_ro", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamCallbackAddReadOnly(
        nint handle,
        string protocol,
        nint userData,
        nint openCallback);

    [LibraryImport(LibraryName, EntryPoint = "mpv_error_string")]
    private static partial nint ErrorString(int error);

    [LibraryImport(LibraryName, EntryPoint = "mpv_terminate_destroy")]
    internal static partial void TerminateDestroy(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "mpv_destroy")]
    internal static partial void Destroy(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_create")]
    internal static unsafe partial int RenderContextCreate(
        nint* renderContext,
        nint handle,
        MpvRenderParam* parameters);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_set_update_callback")]
    internal static partial void RenderContextSetUpdateCallback(
        nint renderContext,
        nint callback,
        nint callbackContext);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_render")]
    internal static unsafe partial int RenderContextRender(
        nint renderContext,
        MpvRenderParam* parameters);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_update")]
    internal static partial ulong RenderContextUpdate(nint renderContext);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_get_info")]
    internal static unsafe partial int RenderContextGetInfo(
        nint renderContext,
        MpvRenderParam parameter);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_report_swap")]
    internal static partial void RenderContextReportSwap(nint renderContext);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_free")]
    internal static partial void RenderContextFree(nint renderContext);

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SetEnvironmentVariable(string name, string value, int overwrite);

    internal static string GetError(int error)
    {
        var pointer = ErrorString(error);
        return pointer == 0 ? $"mpv error {error}" : Marshal.PtrToStringUTF8(pointer) ?? $"mpv error {error}";
    }

    internal static unsafe int RunCommand(nint handle, params string[] arguments)
    {
        var nativeArguments = stackalloc nint[arguments.Length + 1];
        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                nativeArguments[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            }

            nativeArguments[arguments.Length] = 0;
            return Command(handle, nativeArguments);
        }
        finally
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                Marshal.FreeCoTaskMem(nativeArguments[index]);
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvStreamCallbackInfo
{
    public nint Cookie;
    public nint Read;
    public nint Seek;
    public nint Size;
    public nint Close;
    public nint Cancel;
}

internal enum MpvFormat
{
    None = 0,
    String = 1,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
}

[StructLayout(LayoutKind.Explicit)]
internal struct MpvNodeValue
{
    [FieldOffset(0)]
    public nint String;

    [FieldOffset(0)]
    public int Flag;

    [FieldOffset(0)]
    public long Int64;

    [FieldOffset(0)]
    public double Double;

    [FieldOffset(0)]
    public nint List;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNode
{
    public MpvNodeValue Value;
    public MpvFormat Format;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNodeList
{
    public int Count;
    public nint Values;
    public nint Keys;
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Seek = 20,
    PlaybackRestart = 21,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public nint Prefix;
    public nint Level;
    public nint Text;
    public int LogLevel;
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam(MpvRenderParamType type, nint data)
{
    public MpvRenderParamType Type = type;
    public nint Data = data;
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    AdvancedControl = 10,
    NextFrameInfo = 11,
    SkipRendering = 13,
}

[Flags]
internal enum MpvRenderUpdateFlags : ulong
{
    None = 0,
    Frame = 1 << 0,
}

[Flags]
internal enum MpvRenderFrameInfoFlags : ulong
{
    None = 0,
    Present = 1 << 0,
    Redraw = 1 << 1,
    Repeat = 1 << 2,
    BlockVSync = 1 << 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderFrameInfoNative
{
    public MpvRenderFrameInfoFlags Flags;
    public long TargetTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlInitParams
{
    public nint GetProcAddress;
    public nint Context;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Framebuffer;
    public int Width;
    public int Height;
    public int InternalFormat;
}
