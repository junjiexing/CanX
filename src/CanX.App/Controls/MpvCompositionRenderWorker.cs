// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using CanX.Playback.Engine;
using static Avalonia.OpenGL.GlConsts;

namespace CanX.App.Controls;

/// <summary>
/// Renders libmpv directly into textures that Avalonia's compositor can import.
/// The compositor still snapshots the imported texture, but CanX no longer
/// performs an intermediate framebuffer blit or a blocking GL finish.
/// </summary>
internal sealed class MpvCompositionRenderWorker : IDisposable
{
    private readonly IMpvRenderSource _renderSource;
    private readonly IGlContext _glContext;
    private readonly IOpenGlTextureSharingRenderInterfaceContextFeature _sharingFeature;
    private readonly MpvPresentationMetrics _metrics;
    private readonly Action _initialized;
    private readonly Action<CompositionRenderTarget, bool> _frameReady;
    private readonly Action<CompositionRenderTarget> _retirementRequested;
    private readonly Action<string> _failed;
    private readonly AutoResetEvent _renderRequested = new(false);
    private readonly object _requestGate = new();
    private readonly CompositionRenderTarget[] _targets;
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
    private int _ready;
    private int _stopRequested;
    private int _disposed;
    private long _sequence;

    public MpvCompositionRenderWorker(
        IMpvRenderSource renderSource,
        IGlContext glContext,
        IOpenGlTextureSharingRenderInterfaceContextFeature sharingFeature,
        MpvPresentationMetrics metrics,
        Action initialized,
        Action<CompositionRenderTarget, bool> frameReady,
        Action<CompositionRenderTarget> retirementRequested,
        Action<string> failed)
    {
        _renderSource = renderSource;
        _glContext = glContext;
        _sharingFeature = sharingFeature;
        _metrics = metrics;
        _initialized = initialized;
        _frameReady = frameReady;
        _retirementRequested = retirementRequested;
        _failed = failed;
        _targets = Enumerable.Range(0, SelectBufferCount(OperatingSystem.IsAndroid()))
            .Select(static index => new CompositionRenderTarget(index))
            .ToArray();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CanX libmpv composition renderer",
        };
    }

    public bool IsReady => Volatile.Read(ref _ready) != 0;

    public IReadOnlyList<CompositionRenderTarget> Targets => _targets;

    internal static int SelectBufferCount(bool isAndroid) => isAndroid ? 2 : 3;

    public void Start() => _thread.Start();

    public void SetTargetSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_requestGate)
        {
            if (_requestedWidth == width && _requestedHeight == height)
            {
                return;
            }

            _requestedWidth = width;
            _requestedHeight = height;
            _requestedSizeVersion++;
        }

        _renderRequested.Set();
    }

    public void CompletePresentation(CompositionRenderTarget target)
    {
        if (target.CompletePresentation() && Volatile.Read(ref _disposed) == 0)
        {
            Interlocked.Increment(ref _pendingSwapReports);
            _renderRequested.Set();
        }
    }

    public void CompleteRetirement(CompositionRenderTarget target)
    {
        target.CompleteRetirement();
        if (Volatile.Read(ref _disposed) == 0)
        {
            _renderRequested.Set();
        }
    }

    public void ReleaseReadyTarget(CompositionRenderTarget target)
    {
        target.ReleaseWithoutPresentation();
        if (Volatile.Read(ref _disposed) == 0)
        {
            _renderRequested.Set();
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
                    _glHandle = GCHandle.Alloc(gl);
                    _selfHandle = GCHandle.Alloc(this);
                    _renderContext = _renderSource.CreateOpenGlRenderContext(
                        (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&GetProcAddress,
                        GCHandle.ToIntPtr(_glHandle));
                    _renderContext.SetUpdateCallback(
                        (nint)(delegate* unmanaged[Cdecl]<nint, void>)&HandleRenderUpdate,
                        GCHandle.ToIntPtr(_selfHandle));
                    Volatile.Write(ref _ready, 1);
                    _initialized();

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
                        target.ReleaseWorkerResources(gl);
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
        if (!hasPendingMpvFrame && sizeVersion == _renderedSizeVersion)
        {
            return;
        }

        if (hasPendingMpvFrame && sizeVersion == _renderedSizeVersion &&
            _renderContext.GetNextFrameInfo().IsRedraw)
        {
            _renderContext.SkipRender();
            return;
        }

        var size = new PixelSize(width, height);
        CompositionRenderTarget? target = null;
        foreach (var candidate in _targets)
        {
            var result = candidate.TryBeginRender(gl, _glContext, _sharingFeature, size);
            if (result == CompositionTargetAcquireResult.Acquired)
            {
                target = candidate;
                break;
            }

            if (result == CompositionTargetAcquireResult.RetirementRequired)
            {
                _retirementRequested(candidate);
            }
        }

        if (target is null)
        {
            if (hasPendingMpvFrame)
            {
                Interlocked.Exchange(ref _pendingRenderUpdate, 1);
            }

            return;
        }

        try
        {
            var startedTimestamp = Stopwatch.GetTimestamp();
            _renderContext.Render(target.Framebuffer, width, height, flipY: true);
            gl.Flush();
            _metrics.RecordRender(startedTimestamp, usedBlockingFinish: false);
            target.Publish(Interlocked.Increment(ref _sequence));
            _renderedSizeVersion = sizeVersion;
            _frameReady(target, hasPendingMpvFrame);
        }
        catch
        {
            target.ReleaseWithoutPresentation();
            throw;
        }

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
            var worker = (MpvCompositionRenderWorker?)GCHandle.FromIntPtr(context).Target;
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
}

internal enum CompositionTargetAcquireResult
{
    Unavailable,
    Acquired,
    RetirementRequired,
}

internal sealed class CompositionRenderTarget(int index)
{
    private readonly object _gate = new();
    private CompositionRenderTargetState _state;
    private ICompositionImportedGpuImage? _imported;
    private Task? _presentationTask;

    public int Index { get; } = index;

    public int Framebuffer { get; private set; }

    public ICompositionImportableOpenGlSharedTexture? Texture { get; private set; }

    public PixelSize Size { get; private set; }

    public long Sequence { get; private set; }

    public CompositionTargetAcquireResult TryBeginRender(
        GlInterface gl,
        IGlContext context,
        IOpenGlTextureSharingRenderInterfaceContextFeature sharingFeature,
        PixelSize size)
    {
        lock (_gate)
        {
            if (_state != CompositionRenderTargetState.Available)
            {
                return CompositionTargetAcquireResult.Unavailable;
            }

            if (Texture is not null && Size != size)
            {
                if (_imported is not null)
                {
                    _state = CompositionRenderTargetState.Retiring;
                    return CompositionTargetAcquireResult.RetirementRequired;
                }

                ReleaseTexture(gl);
            }

            if (Texture is null)
            {
                if (Framebuffer != 0)
                {
                    gl.DeleteFramebuffer(Framebuffer);
                    Framebuffer = 0;
                }

                Texture = sharingFeature.CreateSharedTextureForComposition(context, size);
                Size = size;
                Framebuffer = gl.GenFramebuffer();
                gl.BindFramebuffer(GL_FRAMEBUFFER, Framebuffer);
                gl.FramebufferTexture2D(
                    GL_FRAMEBUFFER,
                    GL_COLOR_ATTACHMENT0,
                    GL_TEXTURE_2D,
                    Texture.TextureId,
                    0);
                if (gl.CheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
                {
                    ReleaseTexture(gl);
                    throw new InvalidOperationException("Could not create the shared composition framebuffer.");
                }
            }

            _state = CompositionRenderTargetState.Rendering;
            return CompositionTargetAcquireResult.Acquired;
        }
    }

    public void Publish(long sequence)
    {
        lock (_gate)
        {
            if (_state != CompositionRenderTargetState.Rendering)
            {
                throw new InvalidOperationException("The composition target is not being rendered.");
            }

            Sequence = sequence;
            _state = CompositionRenderTargetState.Ready;
        }
    }

    public bool TryBeginPresentation(
        Func<ICompositionImportableOpenGlSharedTexture, ICompositionImportedGpuImage> import,
        out ICompositionImportedGpuImage? imported)
    {
        lock (_gate)
        {
            if (_state != CompositionRenderTargetState.Ready || Texture is null)
            {
                imported = null;
                return false;
            }

            _imported ??= import(Texture);
            imported = _imported;
            _state = CompositionRenderTargetState.Presenting;
            return true;
        }
    }

    public void SetPresentationTask(Task presentationTask)
    {
        lock (_gate)
        {
            _presentationTask = presentationTask;
        }
    }

    public bool CompletePresentation()
    {
        lock (_gate)
        {
            if (_state != CompositionRenderTargetState.Presenting)
            {
                return false;
            }

            _presentationTask = null;
            _state = CompositionRenderTargetState.Available;
            return true;
        }
    }

    public void ReleaseWithoutPresentation()
    {
        lock (_gate)
        {
            if (_state is CompositionRenderTargetState.Ready or CompositionRenderTargetState.Rendering)
            {
                _state = CompositionRenderTargetState.Available;
            }
        }
    }

    public async Task RetireImportedTextureAsync()
    {
        ICompositionImportedGpuImage? imported;
        ICompositionImportableOpenGlSharedTexture? texture;
        lock (_gate)
        {
            if (_state != CompositionRenderTargetState.Retiring)
            {
                return;
            }

            imported = _imported;
            texture = Texture;
        }

        if (imported is not null)
        {
            await imported.DisposeAsync();
        }

        texture?.Dispose();
        lock (_gate)
        {
            _imported = null;
            Texture = null;
            Size = default;
            _state = CompositionRenderTargetState.Available;
        }
    }

    public async Task DisposeImportedTextureAsync()
    {
        ICompositionImportedGpuImage? imported;
        ICompositionImportableOpenGlSharedTexture? texture;
        Task? presentationTask;
        lock (_gate)
        {
            imported = _imported;
            texture = Texture;
            presentationTask = _presentationTask;
        }

        if (presentationTask is not null)
        {
            try
            {
                await presentationTask;
            }
            catch
            {
                // Context loss is handled by the owning surface.
            }
        }

        if (imported is not null)
        {
            try
            {
                await imported.DisposeAsync();
            }
            catch
            {
                // Context loss is handled by the owning surface.
            }
        }

        texture?.Dispose();
        lock (_gate)
        {
            _imported = null;
            Texture = null;
            Size = default;
            _presentationTask = null;
            _state = CompositionRenderTargetState.Available;
        }
    }

    public void CompleteRetirement()
    {
        // RetireImportedTextureAsync already returned the slot to Available.
    }

    public void ReleaseWorkerResources(GlInterface gl)
    {
        lock (_gate)
        {
            if (Framebuffer != 0)
            {
                gl.DeleteFramebuffer(Framebuffer);
                Framebuffer = 0;
            }

            if (_imported is null)
            {
                Texture?.Dispose();
                Texture = null;
                Size = default;
            }
        }
    }

    private void ReleaseTexture(GlInterface gl)
    {
        if (Framebuffer != 0)
        {
            gl.DeleteFramebuffer(Framebuffer);
            Framebuffer = 0;
        }

        Texture?.Dispose();
        Texture = null;
        Size = default;
    }

    private enum CompositionRenderTargetState
    {
        Available,
        Rendering,
        Ready,
        Presenting,
        Retiring,
    }
}
