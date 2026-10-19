// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using CanX.Core.Playback;
using CanX.Playback.Engine;
using static Avalonia.OpenGL.GlConsts;

namespace CanX.App.Controls;

public sealed class MpvVideoSurface : OpenGlControlBase
{
    private const int MaximumWorkerRecoveryAttempts = 3;

    private readonly MpvPresentationMetrics _presentationMetrics = new();
    private IMpvRenderSource? _renderSource;
    private IMpvRenderContext? _renderContext;
    private MpvOpenGlRenderWorker? _renderWorker;
    private IGlContext? _preparedWorkerContext;
    private GCHandle _glHandle;
    private GCHandle _selfHandle;
    private int _presentationReadFramebuffer;
    private int _renderedFrameCount;
    private int _presentedFrameCount;
    private int _pendingRenderUpdate;
    private int _inlineRenderedWidth;
    private int _inlineRenderedHeight;
    private int _renderContextRecreationRequested;
    private int _attachmentVersion;
    private int _renderContextGeneration;
    private int _workerRecoveryAttempts;
    private int _workerNotificationScheduled;
    private int _workerReadyNotificationPending;
    private int _inlineRenderNotificationScheduled;
    private int _diagnosticsNotificationScheduled;
    private int _lastNotifiedFrameCount;
    private long _lastPresentedWorkerSequence;
    private long _lastDiagnosticsNotificationTimestamp;
    private MpvOpenGlSync _presentationSync;
    private bool _workerContextPreparationCompleted;
    private bool _supportsFramebufferBlit;
    private bool _isAttached;
    private bool _initializationFailed;

    public event Action<string>? InitializationFailed;

    public event Action<int>? FrameRendered;

    public event Action? RenderContextInvalidated;

    public event Action<int>? RenderContextReady;

    public event Action<VideoPresentationDiagnostics>? DiagnosticsUpdated;

    public bool HasRenderContext =>
        Volatile.Read(ref _renderContext) is not null || _renderWorker?.IsReady == true;

    public int RenderedFrameCount => Volatile.Read(ref _renderedFrameCount);

    public int PresentedFrameCount => Volatile.Read(ref _presentedFrameCount);

    public int RenderContextGeneration => Volatile.Read(ref _renderContextGeneration);

    public string RenderBackend => _renderWorker is null ? "inline" : "shared-worker";

    internal bool AllowSharedWorker { get; init; } = true;

    internal bool AllowFenceSynchronization { get; init; } = true;

    public VideoPresentationDiagnostics PresentationDiagnostics
    {
        get
        {
            var backend = _renderWorker is not null
                ? _renderWorker.UsesFenceSynchronization
                    ? "shared-worker-fence"
                    : "shared-worker-finish"
                : _renderContext is not null
                    ? "inline"
                    : "unavailable";
            return _presentationMetrics.Snapshot(
                backend,
                backend switch
                {
                    "shared-worker-fence" => "gpu-fence+consumer-finish",
                    "unavailable" => "unavailable",
                    _ => "gl-finish",
                },
                usesCpuReadback: false,
                gpuCopiesPerFrame: backend.StartsWith("shared-worker", StringComparison.Ordinal) ? 1 : 0,
                compositorGpuCopiesPerFrame: backend == "unavailable" ? 0 : 1);
        }
    }

    public void Attach(IMpvRenderSource renderSource)
    {
        ArgumentNullException.ThrowIfNull(renderSource);
        if (_renderSource is not null && !ReferenceEquals(_renderSource, renderSource))
        {
            throw new InvalidOperationException("The video surface is already attached to another render source.");
        }

        _renderSource = renderSource;
        RequestNextFrameRendering();
    }

    internal void RequestRenderContextRecreation()
    {
        if (!_isAttached)
        {
            throw new InvalidOperationException("The video surface is not attached.");
        }

        Interlocked.Exchange(ref _renderContextRecreationRequested, 1);
        RequestNextFrameRendering();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = true;
        _workerContextPreparationCompleted = false;
        _initializationFailed = false;
        _workerRecoveryAttempts = 0;
        base.OnAttachedToVisualTree(eventArgs);
        var attachmentVersion = Interlocked.Increment(ref _attachmentVersion);
        _ = PrepareWorkerContextAsync(attachmentVersion);
        RequestNextFrameRendering();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        if (HasRenderContext)
        {
            RenderContextInvalidated?.Invoke();
        }

        _isAttached = false;
        Interlocked.Increment(ref _attachmentVersion);
        base.OnDetachedFromVisualTree(eventArgs);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        CreatePresentationResources(gl);
        TryInitialize(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        if (Interlocked.Exchange(ref _renderContextRecreationRequested, 0) != 0)
        {
            RecreateRenderPipeline(gl, framebuffer);
            return;
        }

        TryInitialize(gl);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var pixelSize = PixelSize.FromSize(Bounds.Size, scaling);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            return;
        }

        if (_renderWorker is { } worker)
        {
            worker.SetTargetSize(pixelSize.Width, pixelSize.Height);
            if (!worker.Present(
                    gl,
                    _presentationReadFramebuffer,
                    framebuffer,
                    pixelSize.Width,
                    pixelSize.Height,
                    _presentationSync,
                    out var publishedSequence))
            {
                ClearFrame(gl, framebuffer, pixelSize.Width, pixelSize.Height);
            }
            else if (publishedSequence != Volatile.Read(ref _lastPresentedWorkerSequence))
            {
                Volatile.Write(ref _lastPresentedWorkerSequence, publishedSequence);
                Interlocked.Increment(ref _presentedFrameCount);
                _presentationMetrics.RecordPresentedFrame();
                worker.ReportPresentation(publishedSequence);
            }

            ScheduleDiagnosticsUpdate();
            return;
        }

        if (_renderContext is not null)
        {
            var updateRequested = Interlocked.Exchange(ref _pendingRenderUpdate, 0) != 0;
            var hasPendingMpvFrame = updateRequested && _renderContext.Update();
            var sizeChanged = pixelSize.Width != _inlineRenderedWidth ||
                              pixelSize.Height != _inlineRenderedHeight;
            if (hasPendingMpvFrame || sizeChanged)
            {
                var startedTimestamp = Stopwatch.GetTimestamp();
                _renderContext.Render(framebuffer, pixelSize.Width, pixelSize.Height, flipY: true);
                // libmpv does not preserve OpenGL state. OpenGlControlBase
                // expects its swapchain framebuffer to be current when this
                // callback returns.
                gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
                // Fallback for platforms that cannot create a compositor-shared worker
                // context. Avalonia does not expose a cross-context fence here.
                gl.Finish();
                _presentationMetrics.RecordRender(startedTimestamp, usedBlockingFinish: true);
                _renderContext.ReportSwap();
                Interlocked.Increment(ref _presentedFrameCount);
                _presentationMetrics.RecordPresentedFrame();
                _inlineRenderedWidth = pixelSize.Width;
                _inlineRenderedHeight = pixelSize.Height;
                if (hasPendingMpvFrame)
                {
                    var renderedFrameCount = Interlocked.Increment(ref _renderedFrameCount);
                    FrameRendered?.Invoke(renderedFrameCount);
                }

                ScheduleDiagnosticsUpdate();
            }
        }
        else
        {
            ClearFrame(gl, framebuffer, pixelSize.Width, pixelSize.Height);
        }
    }

    private unsafe void TryInitialize(GlInterface gl)
    {
        if (_renderContext is not null || _renderWorker is not null || _initializationFailed ||
            _renderSource is not { IsInitialized: true } renderSource)
        {
            return;
        }

        if (!_workerContextPreparationCompleted)
        {
            return;
        }

        try
        {
            if (_preparedWorkerContext is { } workerContext && _supportsFramebufferBlit)
            {
                _preparedWorkerContext = null;
                _presentationMetrics.Reset();
                _renderWorker = new MpvOpenGlRenderWorker(
                    renderSource,
                    workerContext,
                    _presentationMetrics,
                    AllowFenceSynchronization && _presentationSync.IsAvailable,
                    OnWorkerFrameReady,
                    OnWorkerFailed);
                _renderWorker.Start();
                return;
            }

            _preparedWorkerContext?.Dispose();
            _preparedWorkerContext = null;
            _presentationMetrics.Reset();
            _glHandle = GCHandle.Alloc(gl);
            _selfHandle = GCHandle.Alloc(this);
            _renderContext = renderSource.CreateOpenGlRenderContext(
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&GetProcAddress,
                GCHandle.ToIntPtr(_glHandle));
            _renderContext.SetUpdateCallback(
                (nint)(delegate* unmanaged[Cdecl]<nint, void>)&HandleRenderUpdate,
                GCHandle.ToIntPtr(_selfHandle));
            OnRenderContextReady();
            // The callback only schedules work. Advanced-control update/render calls
            // must run later with this GL context current.
            RequestNextFrameRendering();
        }
        catch (Exception exception)
        {
            _initializationFailed = true;
            ReleaseResources(gl);
            InitializationFailed?.Invoke(exception.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (HasRenderContext)
        {
            RenderContextInvalidated?.Invoke();
        }

        ReleaseResources(gl);
    }

    protected override void OnOpenGlLost()
    {
        RenderContextInvalidated?.Invoke();
        ReleaseResources(null);
        if (_isAttached)
        {
            _initializationFailed = false;
            _workerRecoveryAttempts = 0;
            _workerContextPreparationCompleted = false;
            var attachmentVersion = Interlocked.Increment(ref _attachmentVersion);
            _ = PrepareWorkerContextAsync(attachmentVersion);
        }
    }

    private void ReleaseResources(GlInterface? gl)
    {
        Interlocked.Exchange(ref _pendingRenderUpdate, 0);
        _inlineRenderedWidth = 0;
        _inlineRenderedHeight = 0;
        _renderWorker?.Dispose();
        _renderWorker = null;
        _preparedWorkerContext?.Dispose();
        _preparedWorkerContext = null;
        _renderContext?.Dispose();
        _renderContext = null;
        if (gl is not null && _presentationReadFramebuffer != 0)
        {
            gl.DeleteFramebuffer(_presentationReadFramebuffer);
        }

        _presentationReadFramebuffer = 0;
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        if (_glHandle.IsAllocated)
        {
            _glHandle.Free();
        }
    }

    private void CreatePresentationResources(GlInterface gl)
    {
        _supportsFramebufferBlit = gl.GetProcAddress("glBlitFramebuffer") != 0;
        _ = MpvOpenGlSync.TryCreate(gl, out _presentationSync);
        if (_supportsFramebufferBlit)
        {
            _presentationReadFramebuffer = gl.GenFramebuffer();
        }
    }

    private void RecreateRenderPipeline(GlInterface gl, int framebuffer)
    {
        RenderContextInvalidated?.Invoke();
        ReleaseResources(gl);
        _initializationFailed = false;
        _workerRecoveryAttempts = 0;
        _workerContextPreparationCompleted = false;
        CreatePresentationResources(gl);
        var attachmentVersion = Interlocked.Increment(ref _attachmentVersion);
        _ = PrepareWorkerContextAsync(attachmentVersion);

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var pixelSize = PixelSize.FromSize(Bounds.Size, scaling);
        if (pixelSize.Width > 0 && pixelSize.Height > 0)
        {
            ClearFrame(gl, framebuffer, pixelSize.Width, pixelSize.Height);
        }
    }

    private async Task PrepareWorkerContextAsync(int attachmentVersion)
    {
        IGlContext? context = null;
        try
        {
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
            var sharingFeature = compositor is null
                ? null
                : (IOpenGlTextureSharingRenderInterfaceContextFeature?)await compositor
                    .TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature));
            if (AllowSharedWorker && sharingFeature?.CanCreateSharedContext == true)
            {
                context = sharingFeature.CreateSharedContext();
            }
        }
        catch
        {
            // The synchronous renderer below remains available as a fallback.
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_isAttached || attachmentVersion != Volatile.Read(ref _attachmentVersion))
            {
                context?.Dispose();
                return;
            }

            _preparedWorkerContext = context;
            _workerContextPreparationCompleted = true;
            RequestNextFrameRendering();
        });
    }

    private void OnWorkerFrameReady(bool countAsVideoFrame)
    {
        if (countAsVideoFrame)
        {
            Interlocked.Increment(ref _renderedFrameCount);
        }
        else
        {
            Interlocked.Exchange(ref _workerReadyNotificationPending, 1);
        }

        if (Interlocked.Exchange(ref _workerNotificationScheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _workerNotificationScheduled, 0);
            if (!_isAttached || _renderWorker is null)
            {
                return;
            }

            var renderedFrameCount = Volatile.Read(ref _renderedFrameCount);
            if (renderedFrameCount > _lastNotifiedFrameCount)
            {
                _lastNotifiedFrameCount = renderedFrameCount;
                FrameRendered?.Invoke(renderedFrameCount);
            }

            if (Interlocked.Exchange(ref _workerReadyNotificationPending, 0) != 0 &&
                _renderWorker.IsReady)
            {
                _workerRecoveryAttempts = 0;
                OnRenderContextReady();
            }

            RequestNextFrameRendering();
        // On Android a Render-priority dispatcher item can remain queued until
        // some unrelated UI invalidation wakes the compositor. That reduced
        // video presentation to the 500 ms playback-status polling cadence.
        // Normal priority wakes the platform looper immediately; the actual GL
        // work is still deferred to OpenGlControlBase's next render callback.
        }, DispatcherPriority.Normal);
    }

    private void OnWorkerFailed(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isAttached)
            {
                return;
            }

            RenderContextInvalidated?.Invoke();
            var failedWorker = _renderWorker;
            _renderWorker = null;
            failedWorker?.Dispose();
            _preparedWorkerContext?.Dispose();
            _preparedWorkerContext = null;
            _workerContextPreparationCompleted = false;

            _workerRecoveryAttempts++;
            if (_workerRecoveryAttempts > MaximumWorkerRecoveryAttempts)
            {
                _initializationFailed = true;
                InitializationFailed?.Invoke(
                    $"{message}（渲染上下文自动恢复 {MaximumWorkerRecoveryAttempts} 次后仍失败）");
                return;
            }

            var attachmentVersion = Interlocked.Increment(ref _attachmentVersion);
            _ = RecoverWorkerContextAsync(attachmentVersion, _workerRecoveryAttempts);
        });
    }

    private void ScheduleInlineRender()
    {
        if (Interlocked.Exchange(ref _inlineRenderNotificationScheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _inlineRenderNotificationScheduled, 0);
            if (_isAttached && _renderContext is not null)
            {
                RequestNextFrameRendering();
            }
        }, DispatcherPriority.Normal);
    }

    private async Task RecoverWorkerContextAsync(int attachmentVersion, int attempt)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
        if (!_isAttached || attachmentVersion != Volatile.Read(ref _attachmentVersion))
        {
            return;
        }

        await PrepareWorkerContextAsync(attachmentVersion);
    }

    private void OnRenderContextReady()
    {
        var generation = Interlocked.Increment(ref _renderContextGeneration);
        ScheduleDiagnosticsUpdate(force: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isAttached || generation != Volatile.Read(ref _renderContextGeneration))
            {
                return;
            }

            RenderContextReady?.Invoke(generation);
        });
    }

    private void ScheduleDiagnosticsUpdate(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force)
        {
            var previous = Interlocked.Read(ref _lastDiagnosticsNotificationTimestamp);
            if (previous != 0 && now - previous < Stopwatch.Frequency)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _lastDiagnosticsNotificationTimestamp,
                    now,
                    previous) != previous)
            {
                return;
            }
        }
        else
        {
            Interlocked.Exchange(ref _lastDiagnosticsNotificationTimestamp, now);
        }

        if (Interlocked.Exchange(ref _diagnosticsNotificationScheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _diagnosticsNotificationScheduled, 0);
            if (_isAttached)
            {
                DiagnosticsUpdated?.Invoke(PresentationDiagnostics);
            }
        }, DispatcherPriority.Background);
    }

    private static void ClearFrame(
        GlInterface gl,
        int framebuffer,
        int width,
        int height)
    {
        gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GL_COLOR_BUFFER_BIT);
        gl.Finish();
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
                Interlocked.Exchange(ref control._pendingRenderUpdate, 1);
                control.ScheduleInlineRender();
            }
        }
        catch
        {
            // Exceptions must never cross a native callback boundary.
        }
    }
}
