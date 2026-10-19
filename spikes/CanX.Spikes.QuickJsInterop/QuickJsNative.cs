using System.Runtime.InteropServices;

namespace CanX.Spikes.QuickJsInterop;

internal static partial class QuickJsNative
{
    private const string LibraryName = "canx_quickjs";

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_create")]
    internal static unsafe partial int Create(
        nuint memoryLimit,
        nuint stackLimit,
        nint callback,
        nint callbackContext,
        nint* runtime);

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_destroy")]
    internal static partial void Destroy(nint runtime);

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_add_module", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AddModule(nint runtime, string moduleName, string source);

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_eval", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Eval(
        nint runtime,
        string source,
        string filename,
        int asModule,
        uint timeoutMilliseconds);

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_resolve_tool", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ResolveTool(
        nint runtime,
        ulong requestId,
        string resultJson,
        int isError);

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_pump_jobs")]
    internal static unsafe partial int PumpJobs(
        nint runtime,
        int maxJobs,
        uint timeoutMilliseconds,
        int* executedJobs);

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_get_global_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int GetGlobalString(
        nint runtime,
        string propertyName,
        byte* output,
        nuint outputSize,
        nuint* requiredSize);

    [LibraryImport(LibraryName, EntryPoint = "canx_quickjs_last_error")]
    private static partial nint LastError(nint runtime);

    internal static string GetLastError(nint runtime) =>
        Marshal.PtrToStringUTF8(LastError(runtime)) ?? "unknown QuickJS error";
}
