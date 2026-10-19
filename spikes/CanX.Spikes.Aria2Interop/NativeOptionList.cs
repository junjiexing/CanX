using System.Runtime.InteropServices;

namespace CanX.Spikes.Aria2Interop;

internal sealed unsafe class NativeOptionList : IDisposable
{
    private readonly nint[] _strings;
    private readonly nint _options;
    private bool _disposed;

    public NativeOptionList(params (string Key, string Value)[] options)
    {
        Count = (nuint)options.Length;
        _strings = new nint[options.Length * 2];
        _options = Marshal.AllocHGlobal(sizeof(Aria2Option) * options.Length);
        var target = (Aria2Option*)_options;

        for (var index = 0; index < options.Length; index++)
        {
            var key = Marshal.StringToCoTaskMemUTF8(options[index].Key);
            var value = Marshal.StringToCoTaskMemUTF8(options[index].Value);
            _strings[index * 2] = key;
            _strings[(index * 2) + 1] = value;
            target[index] = new Aria2Option { Key = key, Value = value };
        }
    }

    public Aria2Option* Pointer => (Aria2Option*)_options;

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
