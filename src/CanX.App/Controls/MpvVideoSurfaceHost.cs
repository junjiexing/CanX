// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using CanX.Core.Playback;
using CanX.Playback.Engine;

namespace CanX.App.Controls;

/// <summary>
/// Selects the best available libmpv presentation backend and keeps the
/// established OpenGlControlBase renderer as a compatibility fallback.
/// </summary>
public sealed class MpvVideoSurfaceHost : Grid
{
    private IMpvRenderSource? _renderSource;
    private MpvCompositionSurface? _compositionSurface;
    private MpvVideoSurface? _legacySurface;
    private int _renderContextGeneration;
    private string? _initializationError;

    public event Action<string>? InitializationFailed;

    public event Action<string>? BackendFallback;

    public event Action<int>? FrameRendered;

    public event Action? RenderContextInvalidated;

    public event Action<int>? RenderContextReady;

    public event Action<VideoPresentationDiagnostics>? DiagnosticsUpdated;

    public bool HasRenderContext =>
        _compositionSurface?.HasRenderContext == true || _legacySurface?.HasRenderContext == true;

    public string? InitializationError => _initializationError;

    public int RenderedFrameCount =>
        _compositionSurface?.RenderedFrameCount ?? _legacySurface?.RenderedFrameCount ?? 0;

    public int PresentedFrameCount =>
        _compositionSurface?.PresentedFrameCount ?? _legacySurface?.PresentedFrameCount ?? 0;

    public int RenderContextGeneration => Volatile.Read(ref _renderContextGeneration);

    public string RenderBackend => _compositionSurface is not null
        ? "composition-shared"
        : _legacySurface?.RenderBackend ?? "unavailable";

    public VideoPresentationDiagnostics PresentationDiagnostics =>
        _compositionSurface?.PresentationDiagnostics ??
        _legacySurface?.PresentationDiagnostics ??
        new VideoPresentationDiagnostics();

    public void Attach(IMpvRenderSource renderSource)
    {
        ArgumentNullException.ThrowIfNull(renderSource);
        if (_renderSource is not null && !ReferenceEquals(_renderSource, renderSource))
        {
            throw new InvalidOperationException("The video surface is already attached to another render source.");
        }

        if (_renderSource is not null)
        {
            return;
        }

        _renderSource = renderSource;
        var preference = ReadBackendPreference();
        if (preference is "shared-worker-fence" or "shared-worker-finish" or "inline")
        {
            ActivateLegacySurface(
                allowSharedWorker: preference != "inline",
                allowFenceSynchronization: preference == "shared-worker-fence");
        }
        else
        {
            ActivateCompositionSurface();
        }
    }

    internal void RequestRenderContextRecreation()
    {
        if (_compositionSurface is not null)
        {
            _compositionSurface.RequestRenderContextRecreation();
            return;
        }

        _legacySurface?.RequestRenderContextRecreation();
    }

    private static string ReadBackendPreference()
    {
        var value = Environment.GetEnvironmentVariable("CANX_MPV_PRESENTATION_BACKEND")?.Trim().ToLowerInvariant();
        return value switch
        {
            "composition-shared" => value,
            "shared-worker-fence" => value,
            "shared-worker" or "shared-worker-finish" => "shared-worker-finish",
            "inline" => value,
            _ => "auto",
        };
    }

    private void ActivateCompositionSurface()
    {
        _initializationError = null;
        var source = _renderSource ?? throw new InvalidOperationException("The render source is not attached.");
        var surface = new MpvCompositionSurface(source);
        _compositionSurface = surface;
        surface.InitializationFailed += OnCompositionInitializationFailed;
        surface.FrameRendered += OnFrameRendered;
        surface.RenderContextInvalidated += OnRenderContextInvalidated;
        surface.RenderContextReady += OnRenderContextReady;
        surface.DiagnosticsUpdated += OnDiagnosticsUpdated;
        Children.Add(surface);
    }

    private void ActivateLegacySurface(bool allowSharedWorker, bool allowFenceSynchronization)
    {
        _initializationError = null;
        var source = _renderSource ?? throw new InvalidOperationException("The render source is not attached.");
        var surface = new MpvVideoSurface
        {
            AllowSharedWorker = allowSharedWorker,
            AllowFenceSynchronization = allowFenceSynchronization,
        };
        _legacySurface = surface;
        surface.InitializationFailed += OnLegacyInitializationFailed;
        surface.FrameRendered += OnFrameRendered;
        surface.RenderContextInvalidated += OnRenderContextInvalidated;
        surface.RenderContextReady += OnRenderContextReady;
        surface.DiagnosticsUpdated += OnDiagnosticsUpdated;
        surface.Attach(source);
        Children.Add(surface);
    }

    private async void OnCompositionInitializationFailed(string message)
    {
        if (_compositionSurface is null)
        {
            return;
        }

        BackendFallback?.Invoke($"composition-shared 初始化失败，回退到 shared-worker-finish：{message}");
        RenderContextInvalidated?.Invoke();
        var failed = _compositionSurface;
        _compositionSurface = null;
        failed.InitializationFailed -= OnCompositionInitializationFailed;
        failed.FrameRendered -= OnFrameRendered;
        failed.RenderContextInvalidated -= OnRenderContextInvalidated;
        failed.RenderContextReady -= OnRenderContextReady;
        failed.DiagnosticsUpdated -= OnDiagnosticsUpdated;
        Children.Remove(failed);
        try
        {
            await failed.ShutdownAsync();
        }
        finally
        {
            ActivateLegacySurface(allowSharedWorker: true, allowFenceSynchronization: true);
        }
    }

    private void OnLegacyInitializationFailed(string message)
    {
        _initializationError = message;
        InitializationFailed?.Invoke(message);
    }

    private void OnFrameRendered(int count) => FrameRendered?.Invoke(count);

    private void OnRenderContextInvalidated() => RenderContextInvalidated?.Invoke();

    private void OnRenderContextReady(int _)
    {
        _initializationError = null;
        RenderContextReady?.Invoke(Interlocked.Increment(ref _renderContextGeneration));
    }

    private void OnDiagnosticsUpdated(VideoPresentationDiagnostics diagnostics) =>
        DiagnosticsUpdated?.Invoke(diagnostics);
}
