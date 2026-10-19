// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.OpenGL;

namespace CanX.App.Controls;

internal readonly unsafe struct MpvOpenGlSync
{
    private const uint GlSyncGpuCommandsComplete = 0x9117;
    private const ulong GlTimeoutIgnored = ulong.MaxValue;

    private readonly nint _fenceSync;
    private readonly nint _waitSync;
    private readonly nint _deleteSync;

    private MpvOpenGlSync(nint fenceSync, nint waitSync, nint deleteSync)
    {
        _fenceSync = fenceSync;
        _waitSync = waitSync;
        _deleteSync = deleteSync;
    }

    public bool IsAvailable => _fenceSync != 0 && _waitSync != 0 && _deleteSync != 0;

    public static bool TryCreate(GlInterface gl, out MpvOpenGlSync sync)
    {
        var fenceSync = gl.GetProcAddress("glFenceSync");
        var waitSync = gl.GetProcAddress("glWaitSync");
        var deleteSync = gl.GetProcAddress("glDeleteSync");
        sync = new MpvOpenGlSync(fenceSync, waitSync, deleteSync);
        return sync.IsAvailable;
    }

    public nint CreateFence()
    {
        if (!IsAvailable)
        {
            return 0;
        }

        return ((delegate* unmanaged<uint, uint, nint>)_fenceSync)(GlSyncGpuCommandsComplete, 0);
    }

    public void WaitAndDelete(nint fence)
    {
        if (fence == 0)
        {
            return;
        }

        ((delegate* unmanaged<nint, uint, ulong, void>)_waitSync)(fence, 0, GlTimeoutIgnored);
        ((delegate* unmanaged<nint, void>)_deleteSync)(fence);
    }

    public void Delete(nint fence)
    {
        if (fence != 0)
        {
            ((delegate* unmanaged<nint, void>)_deleteSync)(fence);
        }
    }
}
