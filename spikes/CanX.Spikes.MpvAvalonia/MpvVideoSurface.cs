using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace CanX.Spikes.MpvAvalonia;

public sealed class MpvVideoSurface : OpenGlControlBase
{
    private const ulong RenderUpdateFrame = 1;
    private const int SmokeFrameTarget = 5;

    private nint _mpv;
    private nint _renderContext;
    private GCHandle _glHandle;
    private GCHandle _selfHandle;
    private int _renderedFrameCount;
    private bool _smokeReported;

    public string? MediaPath { get; set; }

    public event Action<string>? StatusChanged;

    public event Action<string>? SmokePassed;

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        _mpv = MpvNative.Create();
        EnsureSuccess(_mpv == 0 ? -1 : 0, "mpv_create");

        SetOption("config", "no");
        SetOption("terminal", "no");
        SetOption("audio", "no");
        SetOption("vo", "libmpv");
        EnsureSuccess(MpvNative.Initialize(_mpv), "mpv_initialize");

        _glHandle = GCHandle.Alloc(gl);
        _selfHandle = GCHandle.Alloc(this);

        var initParameters = new MpvOpenGlInitParams
        {
            GetProcAddress = &GetProcAddress,
            Context = GCHandle.ToIntPtr(_glHandle),
        };

        var apiType = stackalloc byte[]
        {
            (byte)'o', (byte)'p', (byte)'e', (byte)'n', (byte)'g', (byte)'l', 0,
        };
        var parameters = stackalloc MpvRenderParam[3];
        parameters[0] = new MpvRenderParam(MpvRenderParamType.ApiType, (nint)apiType);
        parameters[1] = new MpvRenderParam(MpvRenderParamType.OpenGlInitParams, (nint)(&initParameters));
        parameters[2] = new MpvRenderParam(MpvRenderParamType.Invalid, 0);

        nint renderContext = 0;
        EnsureSuccess(
            MpvNative.RenderContextCreate(&renderContext, _mpv, parameters),
            "mpv_render_context_create");
        _renderContext = renderContext;

        MpvNative.RenderContextSetUpdateCallback(
            _renderContext,
            (nint)(delegate* unmanaged[Cdecl]<nint, void>)&HandleRenderUpdate,
            GCHandle.ToIntPtr(_selfHandle));

        if (!string.IsNullOrWhiteSpace(MediaPath))
        {
            EnsureSuccess(MpvNative.RunCommand(_mpv, "loadfile", MediaPath), "loadfile");
        }

        StatusChanged?.Invoke($"libmpv initialized; OpenGL {GlVersion}");
        RequestNextFrameRendering();
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        if (_renderContext == 0)
        {
            return;
        }

        var updateFlags = MpvNative.RenderContextUpdate(_renderContext);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var pixelSize = PixelSize.FromSize(Bounds.Size, scaling);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            return;
        }

        var target = new MpvOpenGlFbo
        {
            Framebuffer = framebuffer,
            Width = pixelSize.Width,
            Height = pixelSize.Height,
            InternalFormat = 0,
        };
        var flipY = 1;
        var parameters = stackalloc MpvRenderParam[3];
        parameters[0] = new MpvRenderParam(MpvRenderParamType.OpenGlFbo, (nint)(&target));
        parameters[1] = new MpvRenderParam(MpvRenderParamType.FlipY, (nint)(&flipY));
        parameters[2] = new MpvRenderParam(MpvRenderParamType.Invalid, 0);

        EnsureSuccess(MpvNative.RenderContextRender(_renderContext, parameters), "mpv_render_context_render");
        MpvNative.RenderContextReportSwap(_renderContext);

        if ((updateFlags & RenderUpdateFrame) != 0 && ++_renderedFrameCount >= SmokeFrameTarget && !_smokeReported)
        {
            _smokeReported = true;
            var platformHandle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle();
            var backend = platformHandle?.HandleDescriptor
                ?? (OperatingSystem.IsLinux() ? "Wayland" : "unknown");
            SmokePassed?.Invoke(
                $"CANX_MPV_RENDER_OK backend={backend} frames={_renderedFrameCount} " +
                $"size={pixelSize.Width}x{pixelSize.Height} gl={GlVersion}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_renderContext != 0)
        {
            MpvNative.RenderContextSetUpdateCallback(_renderContext, 0, 0);
            MpvNative.RenderContextFree(_renderContext);
            _renderContext = 0;
        }

        if (_mpv != 0)
        {
            MpvNative.TerminateDestroy(_mpv);
            _mpv = 0;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        if (_glHandle.IsAllocated)
        {
            _glHandle.Free();
        }
    }

    protected override void OnOpenGlLost()
    {
        StatusChanged?.Invoke("OpenGL context lost");
    }

    private void SetOption(string name, string value) =>
        EnsureSuccess(MpvNative.SetOptionString(_mpv, name, value), $"set option {name}");

    private static void EnsureSuccess(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{operation} failed: {MpvNative.GetError(result)} ({result})");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetProcAddress(nint context, nint name)
    {
        try
        {
            var gl = (GlInterface?)GCHandle.FromIntPtr(context).Target;
            var functionName = Marshal.PtrToStringUTF8(name);
            return gl is null || functionName is null ? 0 : gl.GetProcAddress(functionName);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HandleRenderUpdate(nint context)
    {
        try
        {
            var control = (MpvVideoSurface?)GCHandle.FromIntPtr(context).Target;
            if (control is not null)
            {
                Dispatcher.UIThread.Post(control.RequestNextFrameRendering, DispatcherPriority.Render);
            }
        }
        catch
        {
            // Exceptions must never cross a native callback boundary.
        }
    }
}
