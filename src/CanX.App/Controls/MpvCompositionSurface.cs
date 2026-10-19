// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using CanX.Core.Playback;
using CanX.Playback.Engine;

namespace CanX.App.Controls;

internal sealed class MpvCompositionSurface : Control
{
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(80);

    private readonly IMpvRenderSource _renderSource;
    private readonly MpvPresentationMetrics _metrics = new();
    private readonly DispatcherTimer _resizeSettleTimer = new() { Interval = ResizeSettleDelay };
    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _drawingSurface;
    private ICompositionGpuInterop? _gpuInterop;
    private MpvCompositionRenderWorker? _worker;
    private int _attachmentVersion;
    private int _renderContextGeneration;
    private int _renderedFrameCount;
    private int _presentedFrameCount;
    private int _diagnosticsNotificationScheduled;
    private long _lastDiagnosticsNotificationTimestamp;
    private bool _isAttached;
    private bool _isInitializing;
    private bool _failed;

    public MpvCompositionSurface(IMpvRenderSource renderSource)
    {
        _renderSource = renderSource ?? throw new ArgumentNullException(nameof(renderSource));
        ClipToBounds = true;
        _resizeSettleTimer.Tick += (_, _) =>
        {
            _resizeSettleTimer.Stop();
            UpdateWorkerTargetSize();
        };
    }

    public event Action<string>? InitializationFailed;

    public event Action<int>? FrameRendered;

    public event Action? RenderContextInvalidated;

    public event Action<int>? RenderContextReady;

    public event Action<VideoPresentationDiagnostics>? DiagnosticsUpdated;

    public bool HasRenderContext => _worker?.IsReady == true;

    public int RenderedFrameCount => Volatile.Read(ref _renderedFrameCount);

    public int PresentedFrameCount => Volatile.Read(ref _presentedFrameCount);

    public int RenderContextGeneration => Volatile.Read(ref _renderContextGeneration);

    public VideoPresentationDiagnostics PresentationDiagnostics => _metrics.Snapshot(
        HasRenderContext ? "composition-shared" : "unavailable",
        HasRenderContext ? "automatic-gl-flush" : "unavailable",
        usesCpuReadback: false,
        gpuCopiesPerFrame: 0,
        compositorGpuCopiesPerFrame: HasRenderContext ? 1 : 0);

    public void RequestRenderContextRecreation()
    {
        if (!_isAttached)
        {
            throw new InvalidOperationException("The video surface is not attached.");
        }

        _ = RecreateAsync();
    }

    public Task ShutdownAsync() => CleanupAsync();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        _isAttached = true;
        _failed = false;
        var version = Interlocked.Increment(ref _attachmentVersion);
        _ = TryInitializeAsync(version);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = false;
        _resizeSettleTimer.Stop();
        Interlocked.Increment(ref _attachmentVersion);
        if (HasRenderContext)
        {
            RenderContextInvalidated?.Invoke();
        }

        ElementComposition.SetElementChildVisual(this, null);
        _ = CleanupAsync();
        base.OnDetachedFromVisualTree(eventArgs);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty)
        {
            UpdateVisualSize();
            if (PresentationDiagnostics.RenderOperations == 0)
            {
                UpdateWorkerTargetSize();
            }
            else
            {
                _resizeSettleTimer.Stop();
                _resizeSettleTimer.Start();
            }
        }

        base.OnPropertyChanged(change);
    }

    private async Task TryInitializeAsync(int attachmentVersion)
    {
        if (_isInitializing || _failed || !_isAttached ||
            attachmentVersion != Volatile.Read(ref _attachmentVersion))
        {
            return;
        }

        if (!_renderSource.IsInitialized)
        {
            await Task.Delay(50);
            if (_isAttached && attachmentVersion == Volatile.Read(ref _attachmentVersion))
            {
                await Dispatcher.UIThread.InvokeAsync(() => _ = TryInitializeAsync(attachmentVersion));
            }

            return;
        }

        _isInitializing = true;
        IGlContext? context = null;
        CompositionDrawingSurface? drawingSurface = null;
        try
        {
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor
                ?? throw new InvalidOperationException("Avalonia compositor is unavailable.");
            var sharingTask = compositor.TryGetRenderInterfaceFeature(
                typeof(IOpenGlTextureSharingRenderInterfaceContextFeature));
            var gpuInteropTask = compositor.TryGetCompositionGpuInterop();
            var sharingFeature = (IOpenGlTextureSharingRenderInterfaceContextFeature?)await sharingTask;
            var gpuInterop = await gpuInteropTask;
            if (sharingFeature?.CanCreateSharedContext != true || gpuInterop is null)
            {
                throw new NotSupportedException(
                    "The current Avalonia graphics backend does not support shared composition textures.");
            }

            context = sharingFeature.CreateSharedContext()
                ?? throw new NotSupportedException("The compositor could not create a shared OpenGL context.");
            drawingSurface = compositor.CreateDrawingSurface();
            var visual = compositor.CreateSurfaceVisual();
            visual.Surface = drawingSurface;

            if (!_isAttached || attachmentVersion != Volatile.Read(ref _attachmentVersion))
            {
                drawingSurface.Dispose();
                context.Dispose();
                return;
            }

            _metrics.Reset();
            _gpuInterop = gpuInterop;
            _drawingSurface = drawingSurface;
            _visual = visual;
            UpdateVisualSize();
            ElementComposition.SetElementChildVisual(this, visual);

            MpvCompositionRenderWorker? worker = null;
            worker = new MpvCompositionRenderWorker(
                _renderSource,
                context,
                sharingFeature,
                _metrics,
                () => OnWorkerReady(worker!),
                (target, countAsVideoFrame) => OnWorkerFrameReady(worker!, target, countAsVideoFrame),
                target => OnRetirementRequested(worker!, target),
                message => OnWorkerFailed(worker!, message));
            _worker = worker;
            context = null;
            drawingSurface = null;
            worker.Start();
            UpdateWorkerTargetSize();
        }
        catch (Exception exception)
        {
            context?.Dispose();
            drawingSurface?.Dispose();
            if (_isAttached && attachmentVersion == Volatile.Read(ref _attachmentVersion))
            {
                Fail(exception.Message);
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private async Task RecreateAsync()
    {
        RenderContextInvalidated?.Invoke();
        var version = Interlocked.Increment(ref _attachmentVersion);
        await CleanupAsync();
        if (_isAttached && version == Volatile.Read(ref _attachmentVersion))
        {
            _failed = false;
            await TryInitializeAsync(version);
        }
    }

    private async Task CleanupAsync()
    {
        var worker = _worker;
        _worker = null;
        var targets = worker?.Targets.ToArray() ?? [];
        var drawingSurface = _drawingSurface;
        _drawingSurface = null;
        _gpuInterop = null;
        _visual = null;
        worker?.Dispose();

        foreach (var target in targets)
        {
            await target.DisposeImportedTextureAsync();
        }

        drawingSurface?.Dispose();
    }

    private void OnWorkerReady(MpvCompositionRenderWorker worker)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isAttached || !ReferenceEquals(worker, _worker))
            {
                return;
            }

            var generation = Interlocked.Increment(ref _renderContextGeneration);
            RenderContextReady?.Invoke(generation);
            ScheduleDiagnosticsUpdate(force: true);
        }, DispatcherPriority.Normal);
    }

    private void OnWorkerFrameReady(
        MpvCompositionRenderWorker worker,
        CompositionRenderTarget target,
        bool countAsVideoFrame)
    {
        Dispatcher.UIThread.Post(
            () => _ = PresentFrameAsync(worker, target, countAsVideoFrame),
            DispatcherPriority.Normal);
    }

    private async Task PresentFrameAsync(
        MpvCompositionRenderWorker worker,
        CompositionRenderTarget target,
        bool countAsVideoFrame)
    {
        if (!_isAttached || !ReferenceEquals(worker, _worker) ||
            _gpuInterop is null || _drawingSurface is null)
        {
            worker.ReleaseReadyTarget(target);
            return;
        }

        try
        {
            if (!target.TryBeginPresentation(_gpuInterop.ImportImage, out var imported) || imported is null)
            {
                return;
            }

            if (countAsVideoFrame)
            {
                var rendered = Interlocked.Increment(ref _renderedFrameCount);
                FrameRendered?.Invoke(rendered);
            }

            var startedTimestamp = Stopwatch.GetTimestamp();
            var presentationTask = _drawingSurface.UpdateAsync(imported);
            target.SetPresentationTask(presentationTask);
            await presentationTask;
            _metrics.RecordSubmit(
                startedTimestamp,
                performedGpuCopy: false,
                usedBlockingFinish: false);
            _metrics.RecordPresentedFrame();
            Interlocked.Increment(ref _presentedFrameCount);
            worker.CompletePresentation(target);
            ScheduleDiagnosticsUpdate();
        }
        catch (Exception exception)
        {
            worker.CompletePresentation(target);
            OnWorkerFailed(worker, exception.Message);
        }
    }

    private void OnRetirementRequested(
        MpvCompositionRenderWorker worker,
        CompositionRenderTarget target)
    {
        Dispatcher.UIThread.Post(
            () => _ = RetireTargetAsync(worker, target),
            DispatcherPriority.Normal);
    }

    private async Task RetireTargetAsync(
        MpvCompositionRenderWorker worker,
        CompositionRenderTarget target)
    {
        try
        {
            await target.RetireImportedTextureAsync();
            worker.CompleteRetirement(target);
        }
        catch (Exception exception)
        {
            OnWorkerFailed(worker, exception.Message);
        }
    }

    private void OnWorkerFailed(MpvCompositionRenderWorker worker, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(worker, _worker))
            {
                Fail(message);
            }
        }, DispatcherPriority.Normal);
    }

    private void Fail(string message)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        InitializationFailed?.Invoke(message);
    }

    private void UpdateVisualSize()
    {
        if (_visual is not null)
        {
            _visual.Size = new Avalonia.Vector(Bounds.Width, Bounds.Height);
        }
    }

    private void UpdateWorkerTargetSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var size = PixelSize.FromSize(Bounds.Size, scaling);
        if (size.Width > 0 && size.Height > 0)
        {
            _worker?.SetTargetSize(size.Width, size.Height);
        }
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
}
