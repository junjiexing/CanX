// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace CanX.Downloads.Interop;

internal sealed unsafe class NativeOptionList : IDisposable
{
    private readonly nint[] _strings;
    private readonly nint _options;
    private bool _disposed;

    public NativeOptionList(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Count = (nuint)options.Count;
        _strings = new nint[options.Count * 2];
        _options = options.Count == 0 ? 0 : Marshal.AllocHGlobal(sizeof(NativeOption) * options.Count);
        var target = (NativeOption*)_options;
        var index = 0;

        foreach (var option in options.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var key = Marshal.StringToCoTaskMemUTF8(option.Key);
            var value = Marshal.StringToCoTaskMemUTF8(option.Value);
            _strings[index * 2] = key;
            _strings[(index * 2) + 1] = value;
            target[index] = new NativeOption { Key = key, Value = value };
            index++;
        }
    }

    public NativeOption* Pointer => (NativeOption*)_options;

    public nuint Count { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var pointer in _strings)
        {
            Marshal.FreeCoTaskMem(pointer);
        }

        Marshal.FreeHGlobal(_options);
        _disposed = true;
    }
}
