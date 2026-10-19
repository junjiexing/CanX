// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace CanX.Downloads.Interop;

internal sealed unsafe class NativeStringList : IDisposable
{
    private readonly nint[] _strings;
    private readonly nint _items;
    private bool _disposed;

    public NativeStringList(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Count = (nuint)values.Count;
        _strings = new nint[values.Count];
        _items = values.Count == 0 ? 0 : Marshal.AllocHGlobal(nint.Size * values.Count);
        try
        {
            var target = (nint*)_items;
            for (var index = 0; index < values.Count; index++)
            {
                ArgumentNullException.ThrowIfNull(values[index]);
                var pointer = Marshal.StringToCoTaskMemUTF8(values[index]);
                _strings[index] = pointer;
                target[index] = pointer;
            }
        }
        catch
        {
            Release();
            throw;
        }
    }

    public nint* Pointer => (nint*)_items;

    public nuint Count { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Release();
        _disposed = true;
    }

    private void Release()
    {
        foreach (var pointer in _strings)
        {
            Marshal.FreeCoTaskMem(pointer);
        }

        Marshal.FreeHGlobal(_items);
    }
}
