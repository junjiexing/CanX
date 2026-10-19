// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using CanX.Playback.Engine;
using static Avalonia.OpenGL.GlConsts;

namespace CanX.App.Controls;

/// <summary>
/// Renders libmpv frames on a GL context shared with Avalonia's compositor.
/// Keeping the expensive render and its completion wait off the UI thread lets
/// the Wayland configure/commit loop continue while a resized frame is produced.
/// </summary>
internal sealed class MpvOpenGlRenderWorker : IDisposable
{
    private const int TextureAllocationStep = 256;
    private const int GlDrawFramebufferBinding = 0x8CA6;
    private const int GlReadFramebufferBinding = 0x8CAA;

    private readonly IMpvRenderSource _renderSource;
    private readonly IGlContext _glContext;
    private readonly MpvPresentationMetrics _metrics;
    private readonly bool _preferFenceSynchronization;
    private readonly Action<bool> _frameReady;
    private readonly Action<string> _failed;
    private readonly AutoResetEvent _renderRequested = new(false);
    private readonly object _requestGate = new();
    private readonly object _frameGate = new();
    private readonly RenderTarget[] _targets = [new(), new()];
    private readonly Thread _thread;

    private IMpvRenderContext? _renderContext;
    private GCHandle _glHandle;
    private GCHandle _selfHandle;
    private int _requestedWidth;
    private int _requestedHeight;
    private int _requestedSizeVersion;
    private int _renderedSizeVersion = -1;
    private int _pendingRenderUpdate = 1;
    private int _pendingSwapReports;
    private int _publishedTarget = -1;
    private long _publishedSequence;
    private long _lastPresentationQueuedSequence;
    private int _ready;
    private int _stopRequested;
    private int _disposed;
    private MpvOpenGlSync _sync;

    public MpvOpenGlRenderWorker(
        IMpvRenderSource renderSource,
        IGlContext glContext,
        MpvPresentationMetrics metrics,
        bool preferFenceSynchronization,
        Action<bool> frameReady,
        Action<string> failed)
    {
        _renderSource = renderSource;
        _glContext = glContext;
        _metrics = metrics;
        _preferFenceSynchronization = preferFenceSynchronization;
        _frameReady = frameReady;
        _failed = failed;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CanX libmpv OpenGL renderer",
        };
    }

    public bool IsReady => Volatile.Read(ref _ready) != 0;

    public bool UsesFenceSynchronization => _preferFenceSynchronization && _sync.IsAvailable;

    public void Start() => _thread.Start();

    public void SetTargetSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var changed = false;
        lock (_requestGate)
        {
            if (_requestedWidth != width || _requestedHeight != height)
            {
                _requestedWidth = width;
                _requestedHeight = height;
                _requestedSizeVersion++;
                changed = true;
            }
        }

        if (changed)
        {
            _renderRequested.Set();
        }
    }

    public bool Present(
        GlInterface gl,
        int readFramebuffer,
        int destinationFramebuffer,
        int destinationWidth,
        int destinationHeight,
        MpvOpenGlSync presentationSync,
        out long publishedSequence)
    {
        var startedTimestamp = Stopwatch.GetTimestamp();
        lock (_frameGate)
        {
            if (_publishedTarget < 0)
            {
                publishedSequence = 0;
                return false;
            }

            var target = _targets[_publishedTarget];
            if (target.TakeFence() is { } fence && fence != 0)
            {
                presentationSync.WaitAndDelete(fence);
            }

            // OpenGlControlBase renders into a framebuffer owned by Avalonia's
            // compositor. Its read and draw bindings are not guaranteed to be
            // the same. Restoring both to the destination framebuffer leaked
            // our blit state into the rest of the compositor on Android and
            // eventually made unrelated UI sample video/corrupt textures.
            gl.GetIntegerv(GlReadFramebufferBinding, out var previousReadFramebuffer);
            gl.GetIntegerv(GlDrawFramebufferBinding, out var previousDrawFramebuffer);
            gl.GetIntegerv(GL_SCISSOR_TEST, out var scissorEnabled);
            gl.Disable(GL_SCISSOR_TEST);
            try
            {
                gl.BindFramebuffer(GL_READ_FRAMEBUFFER, readFramebuffer);
                gl.FramebufferTexture2D(
                    GL_READ_FRAMEBUFFER,
                    GL_COLOR_ATTACHMENT0,
                    GL_TEXTURE_2D,
                    target.Texture,
                    0);
                if (gl.CheckFramebufferStatus(GL_READ_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
                {
                    throw new InvalidOperationException("The completed mpv framebuffer cannot be presented.");
                }

                gl.BindFramebuffer(GL_DRAW_FRAMEBUFFER, destinationFramebuffer);
                gl.BlitFramebuffer(
                    0,
                    0,
                    target.ContentWidth,
                    target.ContentHeight,
                    0,
                    0,
                    destinationWidth,
                    destinationHeight,
                    GL_COLOR_BUFFER_BIT,
                    GL_LINEAR);

                // The producer fence removes the worker-side blocking wait, but
                // Avalonia 12 cannot consume that GLsync for its later snapshot.
                // Complete this final compatibility blit before returning the FBO.
                gl.Finish();
                _metrics.RecordSubmit(
                    startedTimestamp,
                    performedGpuCopy: true,
                    usedBlockingFinish: true);
            }
            finally
            {
                gl.BindFramebuffer(GL_READ_FRAMEBUFFER, readFramebuffer);
                gl.FramebufferTexture2D(
                    GL_READ_FRAMEBUFFER,
                    GL_COLOR_ATTACHMENT0,
                    GL_TEXTURE_2D,
                    0,
                    0);
                gl.BindFramebuffer(GL_READ_FRAMEBUFFER, previousReadFramebuffer);
                gl.BindFramebuffer(GL_DRAW_FRAMEBUFFER, previousDrawFramebuffer);
                if (scissorEnabled != 0)
                {
                    gl.Enable(GL_SCISSOR_TEST);
                }
            }

            publishedSequence = _publishedSequence;
            return true;
        }
    }

    public void ReportPresentation(long publishedSequence)
    {
        if (publishedSequence <= 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        while (true)
        {
            var previous = Interlocked.Read(ref _lastPresentationQueuedSequence);
            if (publishedSequence <= previous)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _lastPresentationQueuedSequence,
                    publishedSequence,
                    previous) == previous)
            {
                Interlocked.Increment(ref _pendingSwapReports);
                _renderRequested.Set();
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _stopRequested, 1);
        _renderRequested.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _glContext.Dispose();
        _renderRequested.Dispose();
    }

    private unsafe void Run()
    {
        try
        {
            using (_glContext.MakeCurrent())
            {
                var gl = _glContext.GlInterface;
                try
                {
                    if (_preferFenceSynchronization)
                    {
                        _ = MpvOpenGlSync.TryCreate(gl, out _sync);
                    }

                    _glHandle = GCHandle.Alloc(gl);
                    _selfHandle = GCHandle.Alloc(this);
                    _renderContext = _renderSource.CreateOpenGlRenderContext(
                        (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&GetProcAddress,
                        GCHandle.ToIntPtr(_glHandle));
                    _renderContext.SetUpdateCallback(
                        (nint)(delegate* unmanaged[Cdecl]<nint, void>)&HandleRenderUpdate,
                        GCHandle.ToIntPtr(_selfHandle));
                    Volatile.Write(ref _ready, 1);
                    NotifyFrameReady(countAsVideoFrame: false);

                    while (Volatile.Read(ref _stopRequested) == 0)
                    {
                        _renderRequested.WaitOne();
                        if (Volatile.Read(ref _stopRequested) != 0)
                        {
                            break;
                        }

                        RenderLatest(gl);
                    }
                }
                finally
                {
                    _renderContext?.Dispose();
                    _renderContext = null;
                    foreach (var target in _targets)
                    {
                        target.Release(gl, _sync);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            NotifyFailure(exception.Message);
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }

            if (_glHandle.IsAllocated)
            {
                _glHandle.Free();
            }
        }
    }

    private void RenderLatest(GlInterface gl)
    {
        ReportPendingSwaps();

        int width;
        int height;
        int sizeVersion;
        lock (_requestGate)
        {
            width = _requestedWidth;
            height = _requestedHeight;
            sizeVersion = _requestedSizeVersion;
        }

        if (width <= 0 || height <= 0 || _renderContext is null)
        {
            return;
        }

        var updateRequested = Interlocked.Exchange(ref _pendingRenderUpdate, 0) != 0;
        var hasPendingMpvFrame = updateRequested && _renderContext.Update();
        if (!hasPendingMpvFrame && sizeVersion == _renderedSizeVersion &&
            Volatile.Read(ref _publishedTarget) >= 0)
        {
            return;
        }

        // A seek resets mpv's renderer and queues a redraw of the old frame
        // before the first decoded frame at the new position is available.
        // Rendering that redraw into the published double buffer can expose a
        // transient empty target (and on Android can ask AImageReader for an
        // already invalidated image). Consume it so mpv completes its reset,
        // but keep displaying the last valid texture until a real frame arrives.
        if (hasPendingMpvFrame &&
            sizeVersion == _renderedSizeVersion &&
            Volatile.Read(ref _publishedTarget) >= 0 &&
            _renderContext.GetNextFrameInfo().IsRedraw)
        {
            _renderContext.SkipRender();
            return;
        }

        int targetIndex;
        lock (_frameGate)
        {
            targetIndex = _publishedTarget == 0 ? 1 : 0;
        }

        var target = _targets[targetIndex];
        target.EnsureCapacity(gl, width, height);
        var startedTimestamp = Stopwatch.GetTimestamp();
        _renderContext.Render(target.Framebuffer, width, height, flipY: true);

        if (UsesFenceSynchronization)
        {
            target.SetFence(_sync.CreateFence());
            gl.Flush();
            _metrics.RecordRender(startedTimestamp, usedBlockingFinish: false);
        }
        else
        {
            // Compatibility path for GL implementations without sync objects.
            gl.Finish();
            _metrics.RecordRender(startedTimestamp, usedBlockingFinish: true);
        }
        target.ContentWidth = width;
        target.ContentHeight = height;

        lock (_frameGate)
        {
            _publishedTarget = targetIndex;
            _publishedSequence++;
        }

        _renderedSizeVersion = sizeVersion;
        NotifyFrameReady(hasPendingMpvFrame);

        lock (_requestGate)
        {
            if (_requestedSizeVersion != _renderedSizeVersion)
            {
                _renderRequested.Set();
            }
        }
    }

    private void ReportPendingSwaps()
    {
        var count = Interlocked.Exchange(ref _pendingSwapReports, 0);
        for (var index = 0; index < count; index++)
        {
            _renderContext?.ReportSwap();
        }
    }

    private void NotifyFrameReady(bool countAsVideoFrame)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _frameReady(countAsVideoFrame);
        }
    }

    private void NotifyFailure(string message)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _failed(message);
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
            var worker = (MpvOpenGlRenderWorker?)GCHandle.FromIntPtr(context).Target;
            if (worker is not null && Volatile.Read(ref worker._disposed) == 0)
            {
                Interlocked.Exchange(ref worker._pendingRenderUpdate, 1);
                worker._renderRequested.Set();
            }
        }
        catch
        {
            // Exceptions must never cross a native callback boundary.
        }
    }

    private sealed class RenderTarget
    {
        public int Framebuffer { get; private set; }

        public int Texture { get; private set; }

        public int CapacityWidth { get; private set; }

        public int CapacityHeight { get; private set; }

        public int ContentWidth { get; set; }

        public int ContentHeight { get; set; }

        private nint Fence { get; set; }

        public void EnsureCapacity(GlInterface gl, int width, int height)
        {
            if (Framebuffer != 0 && width <= CapacityWidth && height <= CapacityHeight)
            {
                return;
            }

            Release(gl, default);
            CapacityWidth = RoundCapacity(width);
            CapacityHeight = RoundCapacity(height);
            Texture = gl.GenTexture();
            gl.BindTexture(GL_TEXTURE_2D, Texture);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
            var internalFormat = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES &&
                                 gl.ContextInfo.Version.Major == 2
                ? GL_RGBA
                : GL_RGBA8;
            gl.TexImage2D(
                GL_TEXTURE_2D,
                0,
                internalFormat,
                CapacityWidth,
                CapacityHeight,
                0,
                GL_RGBA,
                GL_UNSIGNED_BYTE,
                0);

            Framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(GL_FRAMEBUFFER, Framebuffer);
            gl.FramebufferTexture2D(
                GL_FRAMEBUFFER,
                GL_COLOR_ATTACHMENT0,
                GL_TEXTURE_2D,
                Texture,
                0);
            if (gl.CheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
            {
                Release(gl, default);
                throw new InvalidOperationException("Could not create the off-screen mpv framebuffer.");
            }
        }

        public void SetFence(nint fence)
        {
            if (fence == 0)
            {
                throw new InvalidOperationException("OpenGL failed to create a synchronization fence.");
            }

            Fence = fence;
        }

        public nint TakeFence()
        {
            var fence = Fence;
            Fence = 0;
            return fence;
        }

        public void Release(GlInterface gl, MpvOpenGlSync sync)
        {
            sync.Delete(TakeFence());
            if (Framebuffer != 0)
            {
                gl.DeleteFramebuffer(Framebuffer);
                Framebuffer = 0;
            }

            if (Texture != 0)
            {
                gl.DeleteTexture(Texture);
                Texture = 0;
            }

            CapacityWidth = 0;
            CapacityHeight = 0;
            ContentWidth = 0;
            ContentHeight = 0;
        }

        private static int RoundCapacity(int value) =>
            checked((value + TextureAllocationStep - 1) / TextureAllocationStep * TextureAllocationStep);
    }
}
