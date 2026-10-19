// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CanX.App;

internal static partial class DesktopDisplaySleepInhibitor
{
    internal const string Reason = "CanX video playback";

    public static Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) return Task.FromResult(AcquireWindows());
        if (OperatingSystem.IsMacOS()) return Task.FromResult(AcquireMacOS());
        if (OperatingSystem.IsLinux()) return LinuxDisplaySleepInhibitor.AcquireAsync(cancellationToken);
        throw new PlatformNotSupportedException("Display sleep inhibition is unavailable on this platform.");
    }

    private static IDisposable AcquireWindows()
    {
        var reasonText = Marshal.StringToHGlobalUni(Reason);
        SafeFileHandle? handle = null;
        try
        {
            var context = new ReasonContext { Flags = 1, SimpleReasonString = reasonText };
            handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            // Unlike SetThreadExecutionState, request handles may be released on
            // another thread, and one player's release cannot cancel another player.
            if (!PowerSetRequest(handle, 0) || !PowerSetRequest(handle, 1))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return new WindowsLease(handle);
        }
        catch
        {
            handle?.Dispose();
            throw;
        }
        finally { Marshal.FreeHGlobal(reasonText); }
    }

    private static IDisposable AcquireMacOS()
    {
        var reason = CreateCFString(Reason);
        uint displayAssertion = 0;
        uint systemAssertion = 0;
        try
        {
            displayAssertion = CreateAssertion("PreventUserIdleDisplaySleep", reason);
            systemAssertion = CreateAssertion("PreventUserIdleSystemSleep", reason);
            return new MacLease(displayAssertion, systemAssertion);
        }
        catch
        {
            if (displayAssertion != 0) IOPMAssertionRelease(displayAssertion);
            if (systemAssertion != 0) IOPMAssertionRelease(systemAssertion);
            throw;
        }
        finally { CFRelease(reason); }
    }

    private static uint CreateAssertion(string assertionType, nint reason)
    {
        var type = CreateCFString(assertionType);
        try
        {
            var result = IOPMAssertionCreateWithName(type, 255, reason, out var assertion);
            if (result != 0) throw new InvalidOperationException($"IOPMAssertionCreateWithName failed: {result:X8}.");
            return assertion;
        }
        finally { CFRelease(type); }
    }

    private static nint CreateCFString(string value)
    {
        var result = CFStringCreateWithCString(0, value, 0x08000100);
        return result != 0 ? result : throw new OutOfMemoryException("Unable to create a power-request reason.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        // Reserve the rest of REASON_CONTEXT's union (DETAILED_REASON_CONTEXT).
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public nint ReasonStrings;
    }

    private sealed class WindowsLease(SafeFileHandle handle) : IDisposable
    {
        private SafeFileHandle? _handle = handle;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _handle, null);
            if (current is null) return;
            PowerClearRequest(current, 0);
            PowerClearRequest(current, 1);
            current.Dispose();
        }
    }

    private sealed class MacLease(uint display, uint system) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            IOPMAssertionRelease(display);
            IOPMAssertionRelease(system);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(SafeFileHandle request, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(SafeFileHandle request, int type);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint value);

    [LibraryImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static partial int IOPMAssertionCreateWithName(nint type, uint level, nint reason, out uint assertion);

    [LibraryImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static partial int IOPMAssertionRelease(uint assertion);
}
