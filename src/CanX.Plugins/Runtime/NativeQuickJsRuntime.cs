// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CanX.Plugins.Runtime;

internal sealed class NativeQuickJsRuntimeFactory : IQuickJsRuntimeFactory
{
    public IQuickJsRuntime Create(
        PluginRuntimeOptions options,
        Action<ulong, string, string> toolCallback) =>
        NativeQuickJsRuntime.Create(options, toolCallback);
}

internal sealed class NativeQuickJsRuntime : IQuickJsRuntime
{
    private nint runtime;
    private GCHandle callbackHandle;

    private NativeQuickJsRuntime(nint runtime, GCHandle callbackHandle)
    {
        this.runtime = runtime;
        this.callbackHandle = callbackHandle;
    }

    internal static unsafe NativeQuickJsRuntime Create(
        PluginRuntimeOptions options,
        Action<ulong, string, string> toolCallback)
    {
        if (QuickJsNative.AbiVersion() != QuickJsNative.ExpectedAbiVersion)
        {
            throw new PluginRuntimeException(
                $"QuickJS ABI mismatch; expected {QuickJsNative.ExpectedAbiVersion}.");
        }

        var callbackState = new NativeCallbackState(toolCallback);
        var callbackHandle = GCHandle.Alloc(callbackState);
        nint runtime = 0;
        var callback = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, nint, nint, void>)&ToolCallback;
        var result = QuickJsNative.Create(
            options.MemoryLimitBytes,
            options.StackLimitBytes,
            callback,
            GCHandle.ToIntPtr(callbackHandle),
            &runtime);
        if (result != 0 || runtime == 0)
        {
            callbackHandle.Free();
            throw new PluginRuntimeException($"QuickJS runtime creation failed ({result}).");
        }

        return new NativeQuickJsRuntime(runtime, callbackHandle);
    }

    public void EvaluateModule(string source, string filename, TimeSpan timeout)
    {
        EnsureSuccess(
            QuickJsNative.Eval(runtime, source, filename, 1, ToMilliseconds(timeout)),
            "evaluate entry module");
    }

    public void AddModule(string moduleName, string source)
    {
        EnsureSuccess(QuickJsNative.AddModule(runtime, moduleName, source), "register module");
    }

    public void EvaluateScript(string source, string filename, TimeSpan timeout)
    {
        EnsureSuccess(
            QuickJsNative.Eval(runtime, source, filename, 0, ToMilliseconds(timeout)),
            "evaluate host script");
    }

    public void ResolveTool(ulong requestId, string resultJson, bool isError)
    {
        EnsureSuccess(
            QuickJsNative.ResolveTool(runtime, requestId, resultJson, isError ? 1 : 0),
            "resolve Tool call");
    }

    public void InvokeCallback(ulong requestId, string registrationId, string inputJson, TimeSpan timeout)
    {
        var registrationLiteral = JsonSerializer.Serialize(
            registrationId,
            PluginRuntimeJsonContext.Default.String);
        var inputLiteral = JsonSerializer.Serialize(inputJson, PluginRuntimeJsonContext.Default.String);
        EvaluateScript(
            $"globalThis.__canxInvokeCallback({requestId}, {registrationLiteral}, {inputLiteral});",
            "canx:callback/invoke",
            timeout);
    }

    public unsafe int PumpJobs(int maximumJobs, TimeSpan timeout)
    {
        var executedJobs = 0;
        EnsureSuccess(
            QuickJsNative.PumpJobs(runtime, maximumJobs, ToMilliseconds(timeout), &executedJobs),
            "pump jobs");
        return executedJobs;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref runtime, 0);
        if (handle != 0)
        {
            QuickJsNative.Destroy(handle);
        }

        if (callbackHandle.IsAllocated)
        {
            callbackHandle.Free();
        }
    }

    private void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
        {
            throw new PluginRuntimeException(
                $"QuickJS failed to {operation} ({result}): {QuickJsNative.GetLastError(runtime)}");
        }
    }

    private static uint ToMilliseconds(TimeSpan timeout) =>
        checked((uint)Math.Clamp((long)Math.Ceiling(timeout.TotalMilliseconds), 1, uint.MaxValue));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ToolCallback(
        nint callbackContext,
        ulong requestId,
        nint toolName,
        nint inputJson)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(callbackContext);
            if (handle.Target is not NativeCallbackState state)
            {
                return;
            }

            var copiedToolName = Marshal.PtrToStringUTF8(toolName) ?? string.Empty;
            var copiedInputJson = Marshal.PtrToStringUTF8(inputJson) ?? string.Empty;
            state.Callback(requestId, copiedToolName, copiedInputJson);
        }
        catch
        {
            // Exceptions must never cross the unmanaged QuickJS callback boundary.
        }
    }

    private sealed record NativeCallbackState(Action<ulong, string, string> Callback);
}
