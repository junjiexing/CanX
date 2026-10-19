using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CanX.Spikes.MpvAvalonia;

internal static partial class MpvNative
{
    private const string LibraryName = "mpv";

    [LibraryImport(LibraryName, EntryPoint = "mpv_create")]
    internal static partial nint Create();

    [LibraryImport(LibraryName, EntryPoint = "mpv_initialize")]
    internal static partial int Initialize(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SetOptionString(nint handle, string name, string value);

    [LibraryImport(LibraryName, EntryPoint = "mpv_command")]
    internal static unsafe partial int Command(nint handle, nint* arguments);

    [LibraryImport(LibraryName, EntryPoint = "mpv_error_string")]
    private static partial nint ErrorString(int error);

    [LibraryImport(LibraryName, EntryPoint = "mpv_terminate_destroy")]
    internal static partial void TerminateDestroy(nint handle);

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

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_update")]
    internal static partial ulong RenderContextUpdate(nint renderContext);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_render")]
    internal static unsafe partial int RenderContextRender(
        nint renderContext,
        MpvRenderParam* parameters);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_report_swap")]
    internal static partial void RenderContextReportSwap(nint renderContext);

    [LibraryImport(LibraryName, EntryPoint = "mpv_render_context_free")]
    internal static partial void RenderContextFree(nint renderContext);

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
internal struct MpvRenderParam
{
    public MpvRenderParam(MpvRenderParamType type, nint data)
    {
        Type = type;
        Data = data;
    }

    public MpvRenderParamType Type;
    public nint Data;
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvOpenGlInitParams
{
    public delegate* unmanaged[Cdecl]<nint, nint, nint> GetProcAddress;
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
