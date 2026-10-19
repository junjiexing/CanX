// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CanX.Playback.Engine;
using CanX.Streaming;

namespace CanX.Playback.Interop;

internal sealed unsafe class MpvStreamCallbackBridge : IDisposable
{
    private const int MinimumClientApiMajor = 1;
    private const int MinimumClientApiMinor = 106;
    private const int ErrorLoadingFailed = -13;
    private const int ErrorUnsupported = -18;
    private const int ErrorGeneric = -20;

    private readonly MediaStreamBroker _broker;
    private readonly MpvStreamCallbackCounters _counters;
    private GCHandle _brokerHandle;

    public MpvStreamCallbackBridge(MediaStreamBroker broker, MpvStreamCallbackCounters counters)
    {
        _broker = broker;
        _counters = counters;
    }

    public void Register(nint mpvHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(mpvHandle);
        if (_brokerHandle.IsAllocated)
        {
            throw new InvalidOperationException("The mpv stream callback is already registered.");
        }

        if (Unsafe.SizeOf<MpvStreamCallbackInfo>() != IntPtr.Size * 6)
        {
            throw new TypeLoadException("mpv_stream_cb_info does not match the native pointer layout.");
        }

        var apiVersion = MpvNative.ClientApiVersion();
        var major = (int)(apiVersion >> 16);
        var minor = (int)(apiVersion & 0xffff);
        if (major < MinimumClientApiMajor ||
            major == MinimumClientApiMajor && minor < MinimumClientApiMinor)
        {
            throw new NotSupportedException(
                $"libmpv Client API {major}.{minor} does not provide cancellable stream callbacks.");
        }

        _brokerHandle = GCHandle.Alloc(new CallbackContext(_broker, _counters));
        var result = MpvNative.StreamCallbackAddReadOnly(
            mpvHandle,
            MediaStreamBroker.Scheme,
            GCHandle.ToIntPtr(_brokerHandle),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, MpvStreamCallbackInfo*, int>)&OpenCallback);
        if (result >= 0)
        {
            return;
        }

        _brokerHandle.Free();
        throw new InvalidOperationException(
            $"mpv_stream_cb_add_ro failed: {MpvNative.GetError(result)} ({result}).");
    }

    public void Dispose()
    {
        if (_brokerHandle.IsAllocated)
        {
            _brokerHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OpenCallback(nint userData, nint uriPointer, MpvStreamCallbackInfo* info)
    {
        MediaStreamLease? lease = null;
        GCHandle leaseHandle = default;
        try
        {
            if (userData == 0 || uriPointer == 0 || info is null ||
                GCHandle.FromIntPtr(userData).Target is not CallbackContext context)
            {
                return ErrorLoadingFailed;
            }

            var uri = Marshal.PtrToStringUTF8(uriPointer);
            if (string.IsNullOrEmpty(uri) || !context.Broker.TryOpen(uri, out lease))
            {
                return ErrorLoadingFailed;
            }

            leaseHandle = GCHandle.Alloc(new StreamContext(lease, context.Counters));
            *info = new MpvStreamCallbackInfo
            {
                Cookie = GCHandle.ToIntPtr(leaseHandle),
                Read = (nint)(delegate* unmanaged[Cdecl]<nint, nint, ulong, long>)&ReadCallback,
                Seek = (nint)(delegate* unmanaged[Cdecl]<nint, long, long>)&SeekCallback,
                Size = (nint)(delegate* unmanaged[Cdecl]<nint, long>)&SizeCallback,
                Close = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&CloseCallback,
                Cancel = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&CancelCallback,
            };
            context.Counters.IncrementOpen();
            return 0;
        }
        catch
        {
            if (leaseHandle.IsAllocated)
            {
                leaseHandle.Free();
            }

            lease?.Dispose();
            return ErrorLoadingFailed;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long ReadCallback(nint cookie, nint buffer, ulong byteCount)
    {
        try
        {
            if (buffer == 0 && byteCount != 0 || byteCount > int.MaxValue ||
                GetStreamContext(cookie) is not { } context)
            {
                return -1;
            }

            context.Counters.IncrementRead();
            var read = context.Lease.Read(new Span<byte>((void*)buffer, (int)byteCount));
            if (read == 0)
            {
                context.Counters.IncrementEndOfStream();
            }

            return read;
        }
        catch
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long SeekCallback(nint cookie, long offset)
    {
        try
        {
            var context = GetStreamContext(cookie);
            if (context is null)
            {
                return ErrorGeneric;
            }

            context.Counters.IncrementSeek();
            return context.Lease.CanSeek ? context.Lease.Seek(offset) : ErrorUnsupported;
        }
        catch (NotSupportedException)
        {
            return ErrorUnsupported;
        }
        catch
        {
            return ErrorGeneric;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long SizeCallback(nint cookie)
    {
        try
        {
            var context = GetStreamContext(cookie);
            if (context is null)
            {
                return ErrorGeneric;
            }

            context.Counters.IncrementSize();
            return context.Lease.Length ?? ErrorUnsupported;
        }
        catch (NotSupportedException)
        {
            return ErrorUnsupported;
        }
        catch
        {
            return ErrorGeneric;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CloseCallback(nint cookie)
    {
        if (cookie == 0)
        {
            return;
        }

        var leaseHandle = GCHandle.FromIntPtr(cookie);
        try
        {
            if (leaseHandle.Target is StreamContext context)
            {
                context.Counters.IncrementClose();
                context.Lease.Dispose();
            }
        }
        catch
        {
            // Exceptions must never cross the native callback boundary.
        }
        finally
        {
            leaseHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CancelCallback(nint cookie)
    {
        try
        {
            var context = GetStreamContext(cookie);
            if (context is not null)
            {
                context.Counters.IncrementCancel();
                _ = ThreadPool.UnsafeQueueUserWorkItem(
                    static state => state.Lease.Cancel(),
                    context,
                    preferLocal: false);
            }
        }
        catch
        {
            // mpv requires this callback to return immediately and never throw.
        }
    }

    private static StreamContext? GetStreamContext(nint cookie) =>
        cookie == 0 ? null : GCHandle.FromIntPtr(cookie).Target as StreamContext;

    private sealed record CallbackContext(MediaStreamBroker Broker, MpvStreamCallbackCounters Counters);

    private sealed record StreamContext(MediaStreamLease Lease, MpvStreamCallbackCounters Counters);
}
