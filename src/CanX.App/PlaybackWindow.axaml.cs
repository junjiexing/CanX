// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CanX.App.ViewModels;
using CanX.Application.Discovery;
using CanX.Core.Configuration;
using CanX.Core.Playback;
using CanX.MediaAccess;
using CanX.Playback.Engine;
using CanX.Plugins.Ui;
using CanX.Streaming;

namespace CanX.App;

public sealed partial class PlaybackWindow : Window
{
    private const double MinimumVideoWidth = 320;
    private const double MinimumVideoHeight = 180;
    private static readonly TimeSpan InitialSizeSettleDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ResizeCompletionDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MousePositionInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan GestureAdjustmentInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan GestureFeedbackHideDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan SeekConfirmationTimeout = TimeSpan.FromSeconds(2);

    private readonly bool _useNativeFrame;
    private MediaSourceBrowserViewModel? _mediaSources;
    private Func<MediaSourceReadLease, MediaResource, string?, Task>? _addNetworkSubtitle;
    private OnlineSubtitleCatalog? _onlineSubtitles;
    private Func<OnlineSubtitleSource, Task>? _addOnlineSubtitle;
    private readonly DispatcherTimer _initialSizeSettleTimer = new() { Interval = InitialSizeSettleDelay };
    private readonly DispatcherTimer _mousePositionTimer = new() { Interval = MousePositionInterval };
    private readonly DispatcherTimer _gestureAdjustmentTimer = new() { Interval = GestureAdjustmentInterval };
    private readonly DispatcherTimer _gestureFeedbackHideTimer = new() { Interval = GestureFeedbackHideDelay };
    private readonly DispatcherTimer _resizeCompletionTimer = new() { Interval = ResizeCompletionDelay };
    private readonly Action<Size, WindowResizeReason> _frameworkResizeHandler;
    private readonly Dictionary<Key, string> _pressedMpvKeys = [];
    private readonly Dictionary<MouseButton, string> _pressedMpvMouseButtons = [];
    private WindowEdge? _activeResizeEdge;
    private ResizeConstraint? _activeResizeConstraint;
    private Size? _pendingInitialClientSize;
    private Size _lastUserClientSize;
    private double _videoAspectRatio;
    private bool _hasAppliedInitialVideoSize;
    private bool _isApplyingAspectConstraint;
    private bool _isApplyingInitialWaylandSize;
    private bool _deferInitialWaylandReveal;
    private PlaybackState _lastPlaybackState;
    private Window? _showOwner;
    private bool _hasBeenShown;
    private int _focusRegion = -1;
    private bool _allowClose;
    private bool _playbackOverlayShown;
    private bool _pointerInPlaybackOverlayZone;
    private bool _isPreparingToClose;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private PixelPoint? _pendingMpvMousePosition;
    private IPointer? _gesturePointer;
    private Point? _gestureStartPoint;
    private PlaybackGestureKind _gestureKind;
    private double _gestureStartPosition;
    private double _gestureDuration;
    private double _gestureStartVolume;
    private double _gestureStartBrightness;
    private double _gestureSeekTarget;
    private double? _pendingGestureVolume;
    private double? _pendingGestureBrightness;
    private long? _videoTrackForRenderRecovery;
    private TaskCompletionSource? _renderRecoveryCompletion;
    private bool _playbackPositionInteractionActive;
    private bool _playbackPositionSeekPending;
    private double _playbackPositionInteractionValue;
    private double? _playbackPositionPendingTarget;
    private double _playbackPositionBeforeSeek;
    private long _playbackPositionConfirmationDeadline;
    private IPointer? _playbackVolumePointer;
    private int? _playbackSpeedPendingTenths;

    static PlaybackWindow()
    {
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, eventArgs) =>
        {
            if (TopLevel.GetTopLevel(control) is PlaybackWindow window)
                window.OnPlaybackToolTipOpening(control, eventArgs);
        }, RoutingStrategies.Direct);
    }

    public PlaybackWindow()
    {
        InitializeComponent();
        PluginPlaybackContributionHost.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == Visual.IsVisibleProperty &&
                !PluginPlaybackContributionHost.IsVisible)
            {
                PluginPlaybackSidebar.IsVisible = false;
            }
        };
        var platformImpl = PlatformImpl ?? throw new InvalidOperationException("Playback window backend is unavailable.");
        _frameworkResizeHandler = platformImpl.Resized ??
            throw new InvalidOperationException("Playback window resize handler is unavailable.");
        platformImpl.Resized = OnPlatformResized;
        PlaybackVideoSurface.InitializationFailed += message =>
        {
            if (DataContext is PlaybackViewModel viewModel)
            {
                viewModel.ReportSurfaceError(message);
            }
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        PlaybackPositionSlider.AddHandler(
            PointerPressedEvent,
            OnPlaybackPositionPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackPositionSlider.AddHandler(
            PointerReleasedEvent,
            OnPlaybackPositionReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlaybackPositionSlider.PointerCaptureLost += (_, _) =>
        {
            if (!_playbackPositionInteractionActive) return;
            // The release event may still be bubbling through the slider.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_playbackPositionInteractionActive) return;
                _playbackPositionInteractionActive = false;
                HidePlaybackOverlayAfterPointerInteraction();
            });
        };
        PlaybackPositionSlider.ValueChanged += OnPlaybackPositionValueChanged;
        PlaybackChapterMarkerOverlay.TargetSlider = PlaybackPositionSlider;
        PlaybackVolumeSlider.AddHandler(
            PointerPressedEvent,
            OnPlaybackVolumePressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackVolumeSlider.AddHandler(
            PointerReleasedEvent,
            OnPlaybackVolumeReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlaybackVolumeSlider.PointerCaptureLost += (_, _) => EndPlaybackVolumeInteraction();
        PlaybackWindowRoot.AddHandler(
            PointerMovedEvent,
            OnPlaybackPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        InitializePlaybackCursorAutoHide();
        PlaybackView.AddHandler(
            PointerPressedEvent,
            OnPlaybackPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackView.AddHandler(
            PointerMovedEvent,
            OnMpvPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackView.AddHandler(
            PointerPressedEvent,
            OnMpvPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackView.AddHandler(
            PointerReleasedEvent,
            OnMpvPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackView.AddHandler(
            PointerWheelChangedEvent,
            OnMpvPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackView.AddHandler(
            PointerExitedEvent,
            OnMpvPointerExited,
            RoutingStrategies.Direct,
            handledEventsToo: true);
        PlaybackWindowRoot.AddHandler(
            PointerEnteredEvent,
            OnPlaybackPointerEntered,
            RoutingStrategies.Direct,
            handledEventsToo: true);
        PlaybackWindowRoot.AddHandler(
            PointerExitedEvent,
            OnPlaybackPointerExited,
            RoutingStrategies.Direct,
            handledEventsToo: true);
        _mousePositionTimer.Tick += (_, _) => FlushMpvMousePosition();
        _gestureAdjustmentTimer.Tick += (_, _) =>
        {
            _gestureAdjustmentTimer.Stop();
            _ = FlushGestureAdjustmentAsync();
        };
        _gestureFeedbackHideTimer.Tick += (_, _) =>
        {
            _gestureFeedbackHideTimer.Stop();
            GestureFeedback.IsVisible = false;
        };
        _initialSizeSettleTimer.Tick += (_, _) =>
        {
            _initialSizeSettleTimer.Stop();
            _pendingInitialClientSize = null;
        };
        _resizeCompletionTimer.Tick += (_, _) =>
        {
            _resizeCompletionTimer.Stop();
            _activeResizeEdge = null;
            // Keep the selected axis until BeginResize starts the next gesture.
            // Wayland may pause configure events while the pointer is still held,
            // so a timer cannot reliably decide that the gesture has ended.
        };

        _useNativeFrame = OperatingSystem.IsMacOS();
        WindowDecorations = _useNativeFrame
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        CustomTitleBar.IsVisible = !_useNativeFrame;
        UpdateWindowChrome();
        PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == WindowStateProperty)
            {
                UpdateWindowChrome();
                TryApplyInitialVideoSize();
            }
        };
        Resized += OnWindowResized;
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            StopPlaybackCursorAutoHide();
            _initialSizeSettleTimer.Stop();
            _mousePositionTimer.Stop();
            _gestureAdjustmentTimer.Stop();
            _gestureFeedbackHideTimer.Stop();
            _resizeCompletionTimer.Stop();
            PluginPlaybackContributionHost.Dispose();
            PluginPlaybackStatusHost.Dispose();
            PluginPlaybackMenuHost.Dispose();
        };
    }

    public PlaybackWindow(
        PlaybackViewModel viewModel,
        IMpvRenderSource renderSource,
        PluginUiRegistry? pluginUiRegistry = null,
        MediaSourceBrowserViewModel? mediaSources = null,
        Func<MediaSourceReadLease, MediaResource, string?, Task>? addNetworkSubtitle = null,
        OnlineSubtitleCatalog? onlineSubtitles = null,
        Func<OnlineSubtitleSource, Task>? addOnlineSubtitle = null,
        MediaStreamBroker? streamBroker = null,
        MpvSettings? mpvSettings = null)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _mediaSources = mediaSources;
        _addNetworkSubtitle = addNetworkSubtitle;
        _onlineSubtitles = onlineSubtitles;
        _addOnlineSubtitle = addOnlineSubtitle;
        InitializeSeekPreview(streamBroker, mpvSettings ?? new MpvSettings());
        PlaybackVideoSurface.Attach(renderSource);
        PluginPlaybackContributionHost.Registry = pluginUiRegistry;
        PluginPlaybackStatusHost.Registry = pluginUiRegistry;
        PluginPlaybackMenuHost.Registry = pluginUiRegistry;
        PlaybackVideoSurface.RenderContextInvalidated += OnRenderContextInvalidated;
        PlaybackVideoSurface.RenderContextReady += OnRenderContextReady;
        PlaybackVideoSurface.DiagnosticsUpdated += OnPresentationDiagnosticsUpdated;
        PlaybackVideoSurface.BackendFallback += viewModel.ReportSurfaceFallback;
        viewModel.UpdatePresentationDiagnostics(PlaybackVideoSurface.PresentationDiagnostics);
        _lastPlaybackState = viewModel.State;
        viewModel.PropertyChanged += OnPlaybackViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            ReleaseSeekPreview();
            viewModel.PropertyChanged -= OnPlaybackViewModelPropertyChanged;
            PlaybackVideoSurface.RenderContextInvalidated -= OnRenderContextInvalidated;
            PlaybackVideoSurface.RenderContextReady -= OnRenderContextReady;
            PlaybackVideoSurface.DiagnosticsUpdated -= OnPresentationDiagnosticsUpdated;
            PlaybackVideoSurface.BackendFallback -= viewModel.ReportSurfaceFallback;
        };
        UpdateVideoAspectRatio();
        SynchronizePlaybackPositionSlider();
    }

    private PlaybackViewModel ViewModel => (PlaybackViewModel)DataContext!;

    internal int RenderedFrameCount => PlaybackVideoSurface.RenderedFrameCount;

    internal int PresentedFrameCount => PlaybackVideoSurface.PresentedFrameCount;

    internal string RenderBackend => PlaybackVideoSurface.RenderBackend;

    internal VideoPresentationDiagnostics PresentationDiagnostics =>
        PlaybackVideoSurface.PresentationDiagnostics;

    internal int RenderContextGeneration => PlaybackVideoSurface.RenderContextGeneration;

    internal Size VideoViewportSize => PlaybackVideoBorder.Bounds.Size;

    internal void ShowForPlayback(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _showOwner = owner;
        _deferInitialWaylandReveal = IsWaylandWindow();
        if (_deferInitialWaylandReveal)
        {
            Opacity = 0;
        }

        ShowAsIndependentWindow();
    }

    internal async Task WaitForPlaybackSurfaceAsync(CancellationToken cancellationToken)
    {
        var surface = PlaybackVideoSurface;
        if (surface.HasRenderContext)
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(int _) => ready.TrySetResult();
        void OnFailed(string message) => ready.TrySetException(
            new InvalidOperationException($"视频渲染初始化失败：{message}"));
        surface.RenderContextReady += OnReady;
        surface.InitializationFailed += OnFailed;
        try
        {
            if (surface.HasRenderContext)
            {
                return;
            }

            if (surface.InitializationError is { } error)
            {
                throw new InvalidOperationException($"视频渲染初始化失败：{error}");
            }

            await ready.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            surface.RenderContextReady -= OnReady;
            surface.InitializationFailed -= OnFailed;
        }
    }

    internal async Task WaitForPlaybackFramesAsync(int frameCount, CancellationToken cancellationToken)
    {
        var target = PlaybackVideoSurface.RenderedFrameCount + frameCount;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameRendered(int rendered)
        {
            if (rendered >= target)
            {
                completion.TrySetResult();
            }
        }

        PlaybackVideoSurface.FrameRendered += OnFrameRendered;
        try
        {
            if (PlaybackVideoSurface.RenderedFrameCount >= target)
            {
                completion.TrySetResult();
            }

            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            PlaybackVideoSurface.FrameRendered -= OnFrameRendered;
        }
    }

    internal async Task ExercisePresentationResizeAsync(CancellationToken cancellationToken)
    {
        var originalSize = ClientSize;
        var narrower = CalculateAspectConstrainedSize(
            new Size(Math.Max(MinWidth, originalSize.Width * 0.72), originalSize.Height),
            widthDriven: true);
        var wider = CalculateAspectConstrainedSize(
            new Size(Math.Min(originalSize.Width * 1.18, originalSize.Width + 240), originalSize.Height),
            widthDriven: true);

        foreach (var size in new[] { narrower, wider, originalSize })
        {
            ApplyClientSize(size, centerOnScreen: false, screen: null);
            await Task.Delay(140, cancellationToken);
        }

        await WaitForPlaybackFramesAsync(2, cancellationToken);
    }

    internal async Task RecreatePlaybackSurfaceAsync(CancellationToken cancellationToken)
    {
        var previousGeneration = PlaybackVideoSurface.RenderContextGeneration;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _renderRecoveryCompletion = completion;
        PlaybackVideoSurface.RequestRenderContextRecreation();
        await WaitForRenderRecoveryAsync(previousGeneration, completion, cancellationToken);
    }

    internal async Task SuspendAndResumePresentationAsync(CancellationToken cancellationToken)
    {
        Hide();
        await Task.Delay(250, cancellationToken);

        ShowAsIndependentWindow();

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await WaitForPlaybackSurfaceAsync(cancellationToken);
    }

    private void ShowAsIndependentWindow()
    {
        // The player is an independent top-level window: an owned window is
        // kept above its owner and minimized together with it on Windows and
        // X11, and AppKit ignores fullscreen requests for an owned
        // (FullScreenAuxiliary) NSWindow. MainWindow closes the player when it
        // closes, so ownership is not needed for application shutdown.
        if (!_hasBeenShown && _showOwner is { IsVisible: true } owner &&
            double.IsFinite(Width) && double.IsFinite(Height) &&
            (Screens.ScreenFromWindow(owner) ?? Screens.ScreenFromPoint(owner.Position)) is { } screen &&
            !IsWaylandWindow())
        {
            // Open on the main window's screen, as the owned window used to.
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            var size = PixelSize.FromSize(new Size(Width, Height), scaling);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = screen.WorkingArea.CenterRect(new PixelRect(size)).Position;
        }

        _hasBeenShown = true;
        Show();
    }

    internal async Task<int> VisitAvailableScreensAsync(CancellationToken cancellationToken)
    {
        var screens = Screens.All;
        if (IsWaylandWindow() || screens.Count < 2)
        {
            return screens.Count == 0 ? 0 : 1;
        }

        var visited = 0;
        foreach (var screen in screens)
        {
            var pixelWidth = (int)Math.Round(ClientSize.Width * screen.Scaling);
            var pixelHeight = (int)Math.Round(ClientSize.Height * screen.Scaling);
            Position = new PixelPoint(
                screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - pixelWidth) / 2),
                screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - pixelHeight) / 2));
            await Task.Delay(250, cancellationToken);
            var actual = Screens.ScreenFromWindow(this);
            if (actual is null || actual.WorkingArea != screen.WorkingArea)
            {
                throw new InvalidOperationException(
                    $"The playback window did not move to screen {screen.WorkingArea}.");
            }

            await WaitForPlaybackFramesAsync(2, cancellationToken);
            visited++;
        }

        return visited;
    }

    private async Task WaitForRenderRecoveryAsync(
        int previousGeneration,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await completion.Task.WaitAsync(cancellationToken);
            if (!PlaybackVideoSurface.HasRenderContext ||
                PlaybackVideoSurface.RenderContextGeneration <= previousGeneration)
            {
                throw new InvalidOperationException("The playback render context was not recreated.");
            }
        }
        finally
        {
            if (ReferenceEquals(_renderRecoveryCompletion, completion))
            {
                _renderRecoveryCompletion = null;
            }
        }
    }

    private void OnRenderContextInvalidated()
    {
        _videoTrackForRenderRecovery ??= ViewModel.Tracks.FirstOrDefault(static track =>
            track.Type == PlaybackTrackType.Video && track.IsSelected)?.Id;
    }

    private async void OnRenderContextReady(int generation)
    {
        try
        {
            if (_videoTrackForRenderRecovery is { } trackId && ViewModel.HasMedia)
            {
                _videoTrackForRenderRecovery = null;
                await ViewModel.RestoreVideoOutputAsync(trackId);
            }

            _renderRecoveryCompletion?.TrySetResult();
        }
        catch (Exception exception)
        {
            _renderRecoveryCompletion?.TrySetException(exception);
            ViewModel.ReportSurfaceError($"渲染上下文第 {generation} 次初始化后恢复视频失败：{exception.Message}");
        }
    }

    private void OnPresentationDiagnosticsUpdated(VideoPresentationDiagnostics diagnostics) =>
        ViewModel.UpdatePresentationDiagnostics(diagnostics);

    private async void OnTogglePlaybackClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.TogglePauseAsync());

    private async void OnStopPlaybackClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.StopAsync());

    private async void OnPlayPreviousClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.PlayPreviousAsync());

    private async void OnPlayNextClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.PlayNextAsync());

    private async void OnSeekNextChapterClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.SeekNextChapterAsync());

    private async void OnSeekPreviousChapterClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.SeekPreviousChapterAsync());

    private async void OnSeekBackwardClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.SeekAsync(-5, relative: true));

    private async void OnSeekForwardClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.SeekAsync(5, relative: true));

    private async void OnToggleMuteClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ToggleMuteAsync());

    private void OnPlaybackPositionPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (ViewModel.CanSeek)
        {
            _playbackPositionInteractionActive = true;
            _playbackPositionInteractionValue = PlaybackPositionSlider.Value;
        }
    }

    private void OnPlaybackPositionValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_playbackPositionInteractionActive)
        {
            _playbackPositionInteractionValue = eventArgs.NewValue;
        }
    }

    private async void OnPlaybackPositionReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_playbackPositionInteractionActive)
        {
            return;
        }

        var commit = CommitPlaybackPositionAsync();
        HidePlaybackOverlayAfterPointerInteraction();
        await commit;
    }

    private void OnPlaybackPositionKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (IsSliderNavigationKey(eventArgs.Key) && ViewModel.CanSeek)
        {
            _playbackPositionInteractionActive = true;
            _playbackPositionInteractionValue = PlaybackPositionSlider.Value;
        }
    }

    private async void OnPlaybackPositionKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (IsSliderNavigationKey(eventArgs.Key))
        {
            await CommitPlaybackPositionAsync();
        }
    }

    private async Task CommitPlaybackPositionAsync()
    {
        if (_playbackPositionSeekPending)
        {
            return;
        }

        _playbackPositionSeekPending = true;
        _playbackPositionInteractionActive = false;
        var targetSeconds = Math.Clamp(
            _playbackPositionInteractionValue,
            PlaybackPositionSlider.Minimum,
            PlaybackPositionSlider.Maximum);
        _playbackPositionBeforeSeek = ViewModel.PositionSeconds;
        _playbackPositionPendingTarget = targetSeconds;
        _playbackPositionConfirmationDeadline = Environment.TickCount64 +
            (long)SeekConfirmationTimeout.TotalMilliseconds;
        PlaybackPositionSlider.Value = targetSeconds;
        try
        {
            await RunPlaybackActionAsync(() => ViewModel.SeekAsync(targetSeconds, exact: false));
        }
        finally
        {
            _playbackPositionSeekPending = false;
        }
    }

    private static bool IsSliderNavigationKey(Key key) =>
        key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private async void OnPlaybackVolumeValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (DataContext is PlaybackViewModel { IsReady: true } model && sender is Slider slider &&
            Math.Abs(eventArgs.NewValue - Math.Clamp(model.Volume, slider.Minimum, slider.Maximum)) > 0.0001)
        {
            ShowGestureFeedback($"音量 {eventArgs.NewValue:0}%");
            if (_playbackVolumePointer is null)
                StartGestureFeedbackHide();
            await RunPlaybackActionAsync(() => model.SetVolumeAsync(eventArgs.NewValue));
        }
    }

    private void OnPlaybackVolumePressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (DataContext is not PlaybackViewModel { IsReady: true })
            return;

        _playbackVolumePointer = eventArgs.Pointer;
        ShowGestureFeedback($"音量 {PlaybackVolumeSlider.Value:0}%");
    }

    private void OnPlaybackVolumeReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (ReferenceEquals(_playbackVolumePointer, eventArgs.Pointer))
            EndPlaybackVolumeInteraction();
    }

    private void EndPlaybackVolumeInteraction()
    {
        if (_playbackVolumePointer is null)
            return;

        _playbackVolumePointer = null;
        StartGestureFeedbackHide();
        HidePlaybackOverlayAfterPointerInteraction();
    }

    private async void OnSpeedDecreaseClick(object? sender, RoutedEventArgs eventArgs) =>
        await AdjustPlaybackSpeedAsync(-1);

    private async void OnSpeedIncreaseClick(object? sender, RoutedEventArgs eventArgs) =>
        await AdjustPlaybackSpeedAsync(1);

    private async Task AdjustPlaybackSpeedAsync(int step)
    {
        var currentTenths = _playbackSpeedPendingTenths ??
            Math.Clamp((int)Math.Round(ViewModel.Speed * 10, MidpointRounding.AwayFromZero), 1, 40);
        var targetTenths = Math.Clamp(currentTenths + step, 1, 40);
        if (targetTenths == currentTenths)
        {
            return;
        }

        _playbackSpeedPendingTenths = targetTenths;
        var target = targetTenths / 10d;
        var succeeded = await RunPlaybackActionAsync(() => ViewModel.SetSpeedAsync(target));
        if (_playbackSpeedPendingTenths == targetTenths &&
            (!succeeded || Math.Abs(ViewModel.Speed - target) < 0.001))
        {
            _playbackSpeedPendingTenths = null;
        }
    }

    private void OnToggleFullScreenClick(object? sender, RoutedEventArgs eventArgs) => ToggleFullScreen();

    private void OnToggleTrackPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        PluginPlaybackSidebar.IsVisible = false;
        PlaylistPanelHost.IsVisible = false;
        ChapterBookmarkPanelHost.IsVisible = false;
        AdvancedPanelHost.IsVisible = false;
        DiagnosticsPanelHost.IsVisible = false;
        TrackPanelHost.IsVisible = !TrackPanelHost.IsVisible;
    }

    private void OnCloseTrackPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        TrackPanelHost.IsVisible = false;
        TrackPanelButton.Focus();
    }

    private void OnTogglePlaylistPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        PluginPlaybackSidebar.IsVisible = false;
        TrackPanelHost.IsVisible = false;
        ChapterBookmarkPanelHost.IsVisible = false;
        AdvancedPanelHost.IsVisible = false;
        DiagnosticsPanelHost.IsVisible = false;
        PlaylistPanelHost.IsVisible = !PlaylistPanelHost.IsVisible;
    }

    private void OnClosePlaylistPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        PlaylistPanelHost.IsVisible = false;
        PlaylistPanelButton.Focus();
    }

    private async void OnPlayerPlaylistDoubleTapped(object? sender, TappedEventArgs eventArgs) =>
        await PlaySelectedPlaylistItemAsync();

    private async void OnPlaySelectedPlaylistItemClick(object? sender, RoutedEventArgs eventArgs) =>
        await PlaySelectedPlaylistItemAsync();

    private async Task PlaySelectedPlaylistItemAsync()
    {
        if (ViewModel.SelectedPlaylistItem is { } item)
        {
            await RunPlaybackActionAsync(() => ViewModel.PlayPlaylistItemAsync(item));
        }
    }

    private async void OnRemoveSelectedPlaylistItemClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.SelectedPlaylistItem is { } item)
        {
            await RunPlaybackActionAsync(() => ViewModel.RemovePlaylistItemAsync(item));
        }
    }

    private async void OnClearPlayerPlaylistClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ClearPlaylistAsync());

    private void OnToggleChapterBookmarkPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        PluginPlaybackSidebar.IsVisible = false;
        TrackPanelHost.IsVisible = false;
        PlaylistPanelHost.IsVisible = false;
        AdvancedPanelHost.IsVisible = false;
        DiagnosticsPanelHost.IsVisible = false;
        ChapterBookmarkPanelHost.IsVisible = !ChapterBookmarkPanelHost.IsVisible;
    }

    private void OnCloseChapterBookmarkPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        ChapterBookmarkPanelHost.IsVisible = false;
        ChapterBookmarkPanelButton.Focus();
    }

    private async void OnChapterDoubleTapped(object? sender, TappedEventArgs eventArgs) =>
        await OpenSelectedChapterAsync();

    private async void OnOpenSelectedChapterClick(object? sender, RoutedEventArgs eventArgs) =>
        await OpenSelectedChapterAsync();

    private Task OpenSelectedChapterAsync() =>
        ViewModel.SelectedChapterItem is { } item
            ? RunPlaybackActionAsync(() => ViewModel.OpenChapterAsync(item))
            : Task.CompletedTask;

    private async void OnAddBookmarkClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.AddBookmarkAsync());

    private async void OnBookmarkDoubleTapped(object? sender, TappedEventArgs eventArgs) =>
        await OpenSelectedBookmarkAsync();

    private async void OnOpenSelectedBookmarkClick(object? sender, RoutedEventArgs eventArgs) =>
        await OpenSelectedBookmarkAsync();

    private Task OpenSelectedBookmarkAsync() =>
        ViewModel.SelectedBookmarkItem is { } item
            ? RunPlaybackActionAsync(() => ViewModel.OpenBookmarkAsync(item))
            : Task.CompletedTask;

    private async void OnRenameSelectedBookmarkClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.RenameBookmarkAsync());

    private async void OnRemoveSelectedBookmarkClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.SelectedBookmarkItem is { } item)
        {
            await RunPlaybackActionAsync(() => ViewModel.RemoveBookmarkAsync(item));
        }
    }

    private async void OnClearBookmarksClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ClearBookmarksAsync());

    private void OnToggleAdvancedPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        PluginPlaybackSidebar.IsVisible = false;
        TrackPanelHost.IsVisible = false;
        PlaylistPanelHost.IsVisible = false;
        ChapterBookmarkPanelHost.IsVisible = false;
        DiagnosticsPanelHost.IsVisible = false;
        AdvancedPanelHost.IsVisible = !AdvancedPanelHost.IsVisible;
    }

    private void OnCloseAdvancedPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        AdvancedPanelHost.IsVisible = false;
        AdvancedPanelButton.Focus();
    }

    private void OnToggleDiagnosticsPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        PluginPlaybackSidebar.IsVisible = false;
        TrackPanelHost.IsVisible = false;
        PlaylistPanelHost.IsVisible = false;
        ChapterBookmarkPanelHost.IsVisible = false;
        AdvancedPanelHost.IsVisible = false;
        DiagnosticsPanelHost.IsVisible = !DiagnosticsPanelHost.IsVisible;
    }

    private void OnCloseDiagnosticsPanelClick(object? sender, RoutedEventArgs eventArgs)
    {
        DiagnosticsPanelHost.IsVisible = false;
        DiagnosticsPanelButton.Focus();
    }

    private void OnTogglePluginSidebarClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!PluginPlaybackContributionHost.IsVisible) return;
        var open = !PluginPlaybackSidebar.IsVisible;
        TrackPanelHost.IsVisible = false;
        PlaylistPanelHost.IsVisible = false;
        ChapterBookmarkPanelHost.IsVisible = false;
        AdvancedPanelHost.IsVisible = false;
        DiagnosticsPanelHost.IsVisible = false;
        PluginPlaybackSidebar.IsVisible = open;
    }

    private void OnClosePluginSidebarClick(object? sender, RoutedEventArgs eventArgs)
    {
        PluginPlaybackSidebar.IsVisible = false;
        PluginSidebarButton.Focus();
    }

    private async void OnApplyColorSettingsClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ApplyColorSettingsAsync());

    private async void OnReadRuntimePropertyClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ReadRuntimePropertyAsync());

    private async void OnWriteRuntimePropertyClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.WriteRuntimePropertyAsync());

    private async void OnExecuteRuntimeCommandClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ExecuteRuntimeCommandAsync());

    private async void OnPreviousFrameClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.StepFrameAsync(backward: true));

    private async void OnNextFrameClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.StepFrameAsync());

    private async void OnSetAbLoopStartClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.SetAbLoopStartAsync());

    private async void OnSetAbLoopEndClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.SetAbLoopEndAsync());

    private async void OnClearAbLoopClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ClearAbLoopAsync());

    private async void OnScreenshotSubtitlesClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.TakeScreenshotAsync(PlaybackScreenshotMode.Subtitles));

    private async void OnScreenshotVideoClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.TakeScreenshotAsync(PlaybackScreenshotMode.Video));

    private async void OnScreenshotWindowClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.TakeScreenshotAsync(PlaybackScreenshotMode.Window));

    private async void OnAudioDeviceDropDownClosed(object? sender, EventArgs eventArgs) =>
        await ApplyAudioDeviceSelectionAsync();

    private async void OnAudioDeviceKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.Enter)
        {
            await ApplyAudioDeviceSelectionAsync();
        }
    }

    private Task ApplyAudioDeviceSelectionAsync() =>
        AudioDeviceComboBox.SelectedItem is PlaybackAudioDeviceChoiceViewModel choice
            ? RunPlaybackActionAsync(() => ViewModel.SelectAudioDeviceAsync(choice))
            : Task.CompletedTask;

    private async void OnApplyVideoFiltersClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ApplyVideoFiltersAsync());

    private async void OnClearVideoFiltersClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ClearVideoFiltersAsync());

    private async void OnApplyAudioFiltersClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ApplyAudioFiltersAsync());

    private async void OnClearAudioFiltersClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ClearAudioFiltersAsync());

    private async void OnAddShaderClick(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await OwnedStoragePicker.OpenFilePickerAsync(this, new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "选择用户 Shader",
            FileTypeFilter =
            [
                new FilePickerFileType("mpv Shader")
                {
                    Patterns = ["*.glsl", "*.hook"],
                    MimeTypes = ["text/plain"],
                },
            ],
        });
        foreach (var file in files)
        {
            await RunPlaybackActionAsync(() => ViewModel.AddShaderAsync(file.Path.LocalPath));
        }
    }

    private async void OnRemoveShaderClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.RemoveSelectedShaderAsync());

    private async void OnClearShadersClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunPlaybackActionAsync(() => ViewModel.ClearShadersAsync());

    private async void OnTrackDropDownClosed(object? sender, EventArgs eventArgs) =>
        await ApplyTrackSelectionAsync(sender);

    private async void OnTrackKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.Enter)
        {
            await ApplyTrackSelectionAsync(sender);
        }
    }

    private Task ApplyTrackSelectionAsync(object? sender)
    {
        if (sender is ComboBox { SelectedItem: PlaybackTrackChoiceViewModel choice })
        {
            return RunPlaybackActionAsync(() => ViewModel.SelectTrackAsync(choice));
        }

        return Task.CompletedTask;
    }

    private async void OnDiscoveredSubtitleSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is not ComboBox { SelectedItem: DiscoveredSubtitleChoiceViewModel choice } comboBox)
        {
            return;
        }

        comboBox.SelectedIndex = -1;
        await RunPlaybackActionAsync(() => ViewModel.SelectDiscoveredSubtitleAsync(choice));
    }

    private async void OnAddSubtitleClick(object? sender, RoutedEventArgs eventArgs) =>
        await OpenSubtitlePickerAsync(startOnline: false);

    private async void OnSearchSubtitleClick(object? sender, RoutedEventArgs eventArgs) =>
        await OpenSubtitlePickerAsync(startOnline: true);

    private async Task OpenSubtitlePickerAsync(bool startOnline)
    {
        if (_mediaSources is null)
        {
            return;
        }

        var playbackSource = ViewModel.Source;
        var result = await new SubtitlePickerDialog(_mediaSources, ViewModel, _onlineSubtitles, startOnline).ShowDialog<SubtitlePickerResult?>(this);
        if (!string.Equals(playbackSource, ViewModel.Source, StringComparison.Ordinal) || !ViewModel.CanControl)
        {
            result?.NetworkLease?.Dispose();
            return;
        }
        if (result?.LocalPaths is { } paths)
        {
            await AddSubtitleFilesAsync(paths);
        }
        else if (result is { NetworkLease: { } lease, NetworkResource: { } resource })
        {
            if (_addNetworkSubtitle is null)
            {
                lease.Dispose();
                return;
            }

            await RunPlaybackActionAsync(() => _addNetworkSubtitle(lease, resource, result.NetworkProfileId));
        }
        else if (result?.OnlineSource is { } online && _addOnlineSubtitle is not null)
        {
            await RunPlaybackActionAsync(() => _addOnlineSubtitle(online));
        }
    }

    private async Task AddSubtitleFilesAsync(IEnumerable<string> paths)
    {
        var select = true;
        foreach (var path in paths.Where(IsSupportedSubtitleFile))
        {
            await RunPlaybackActionAsync(() => ViewModel.AddSubtitleAsync(path, select));
            select = false;
        }
    }

    private async void OnSubtitleVisibilityClick(object? sender, RoutedEventArgs eventArgs)
    {
        await RunPlaybackActionAsync(() =>
            ViewModel.SetSubtitleVisibleAsync(SubtitleVisibilityCheckBox.IsChecked == true));
    }

    private async void OnSubtitleDelayChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (DataContext is PlaybackViewModel model && IsUserSliderChange(sender, eventArgs, model.SubtitleDelaySeconds))
            await RunPlaybackActionAsync(() => model.SetSubtitleDelayAsync(eventArgs.NewValue));
    }

    private async void OnSubtitleScaleChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (DataContext is PlaybackViewModel model && IsUserSliderChange(sender, eventArgs, model.SubtitleScale))
            await RunPlaybackActionAsync(() => model.SetSubtitleScaleAsync(eventArgs.NewValue));
    }

    private async void OnSubtitlePositionChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (DataContext is PlaybackViewModel model && IsUserSliderChange(sender, eventArgs, model.SubtitlePosition))
            await RunPlaybackActionAsync(() => model.SetSubtitlePositionAsync(eventArgs.NewValue));
    }

    private bool IsUserSliderChange(object? sender, RangeBaseValueChangedEventArgs eventArgs, double currentValue) =>
        ViewModel.CanControl && sender is Slider slider &&
        Math.Abs(eventArgs.NewValue - Math.Clamp(currentValue, slider.Minimum, slider.Maximum)) > 0.0001;

    private void OnPlaybackDragOver(object? sender, DragEventArgs eventArgs)
    {
        var files = eventArgs.DataTransfer.TryGetFiles();
        eventArgs.DragEffects = ViewModel.CanControl && files is { Length: > 0 } &&
            files.All(file => IsSupportedSubtitleFile(file.Path.LocalPath))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void OnPlaybackDrop(object? sender, DragEventArgs eventArgs)
    {
        var files = eventArgs.DataTransfer.TryGetFiles();
        if (ViewModel.CanControl && files is { Length: > 0 } &&
            files.All(file => IsSupportedSubtitleFile(file.Path.LocalPath)))
        {
            await AddSubtitleFilesAsync(files.Select(file => file.Path.LocalPath));
        }

        eventArgs.Handled = true;
    }

    private static bool IsSupportedSubtitleFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is
            ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub" or ".idx" or ".sup" or
            ".smi" or ".sami" or ".lrc" or ".ttml" or ".dfxp";
    }

    private void OnPlaybackPointerEntered(object? sender, PointerEventArgs eventArgs) =>
        OnPlaybackPointerActivity(eventArgs);

    private void OnPlaybackPointerMoved(object? sender, PointerEventArgs eventArgs) =>
        OnPlaybackPointerActivity(eventArgs);

    private void OnPlaybackPointerActivity(PointerEventArgs eventArgs)
    {
        UpdatePlaybackOverlayForPointer(eventArgs);
        NotePlaybackCursorActivity(eventArgs);
    }

    private void UpdatePlaybackOverlayForPointer(PointerEventArgs eventArgs)
    {
        if (IsFromAnotherTopLevel(eventArgs)) return;

        // Keep the overlay arranged while hidden so Bounds tracks its actual
        // height as the playback window and control row change size.
        _pointerInPlaybackOverlayZone =
            PlaybackOverlay.Bounds.Contains(eventArgs.GetPosition(PlaybackView));
        if (_pointerInPlaybackOverlayZone ||
            _playbackPositionInteractionActive || _playbackVolumePointer is not null)
            ShowPlaybackOverlay();
        else
            HidePlaybackOverlay();
    }

    private async void OnPlaybackPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!ViewModel.CanControl ||
            IsFromAnotherTopLevel(eventArgs) ||
            !eventArgs.GetCurrentPoint(PlaybackView).Properties.IsRightButtonPressed)
        {
            return;
        }

        // mpv 0.41 maps the right mouse button to its context menu. CanX uses
        // right-click as a host-level play/pause gesture across the whole video
        // area, including the controls overlay, as required by the application UX.
        eventArgs.Handled = true;
        await RunMpvInputAsync(() => ViewModel.TogglePauseAsync());
    }

    private void OnPlaybackToolTipOpening(object? sender, CancelRoutedEventArgs eventArgs)
    {
        if (eventArgs.Source is not Control control || ToolTip.GetTip(control) is not { } content)
            return;

        var toolTip = content as ToolTip ?? new ToolTip { Content = content };
        toolTip.IsHitTestVisible = false;
        toolTip.AttachedToVisualTree -= OnPlaybackToolTipAttached;
        toolTip.AttachedToVisualTree += OnPlaybackToolTipAttached;
        ToolTip.SetTip(control, toolTip);
    }

    private static void OnPlaybackToolTipAttached(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        if (sender is ToolTip toolTip && TopLevel.GetTopLevel(toolTip) is PopupRoot popupRoot)
            popupRoot.IsHitTestVisible = false;
    }

    private void OnPlaybackPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (IsFromAnotherTopLevel(eventArgs) ||
            new Rect(PlaybackWindowRoot.Bounds.Size).Contains(eventArgs.GetPosition(PlaybackWindowRoot)))
        {
            return;
        }

        StopPlaybackCursorAutoHide();
        _pointerInPlaybackOverlayZone = false;
        HidePlaybackOverlayAfterPointerInteraction();
    }

    private void HidePlaybackOverlayAfterPointerInteraction()
    {
        if (!_pointerInPlaybackOverlayZone &&
            !_playbackPositionInteractionActive && _playbackVolumePointer is null)
            HidePlaybackOverlay();
    }

    private void ShowPlaybackOverlay()
    {
        if (_playbackOverlayShown) return;
        _playbackOverlayShown = true;
        PlaybackOverlay.IsEnabled = true;
        PlaybackOverlay.IsHitTestVisible = true;
        PlaybackOverlay.Opacity = 1;
    }

    private void HidePlaybackOverlay()
    {
        if (!_playbackOverlayShown) return;
        _playbackOverlayShown = false;
        HideSeekPreview();
        PlaybackOverlay.IsHitTestVisible = false;
        PlaybackOverlay.IsEnabled = false;
        PlaybackOverlay.Opacity = 0;
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        var mpvKey = MpvInputKeyMapper.Map(eventArgs.Key, eventArgs.KeySymbol, eventArgs.KeyModifiers);
        var trackDropDownOpen = VideoTrackComboBox.IsDropDownOpen ||
            AudioTrackComboBox.IsDropDownOpen ||
            SubtitleTrackComboBox.IsDropDownOpen ||
            AudioDeviceComboBox.IsDropDownOpen;
        if (mpvKey == "ESC" && trackDropDownOpen)
        {
            return;
        }

        // An open panel can be closed from the keyboard even when the pointer
        // has left the bottom zone. Restore its trigger before moving focus.
        if (mpvKey == "ESC" &&
            (TrackPanelHost.IsVisible || PlaylistPanelHost.IsVisible ||
             ChapterBookmarkPanelHost.IsVisible || AdvancedPanelHost.IsVisible ||
             DiagnosticsPanelHost.IsVisible || PluginPlaybackSidebar.IsVisible))
            ShowPlaybackOverlay();

        if (mpvKey == "ESC" && TrackPanelHost.IsVisible)
        {
            TrackPanelHost.IsVisible = false;
            TrackPanelButton.Focus();
            eventArgs.Handled = true;
            return;
        }

        if (mpvKey == "ESC" && PlaylistPanelHost.IsVisible)
        {
            PlaylistPanelHost.IsVisible = false;
            PlaylistPanelButton.Focus();
            eventArgs.Handled = true;
            return;
        }

        if (mpvKey == "ESC" && ChapterBookmarkPanelHost.IsVisible)
        {
            ChapterBookmarkPanelHost.IsVisible = false;
            ChapterBookmarkPanelButton.Focus();
            eventArgs.Handled = true;
            return;
        }

        if (mpvKey == "ESC" && AdvancedPanelHost.IsVisible)
        {
            AdvancedPanelHost.IsVisible = false;
            AdvancedPanelButton.Focus();
            eventArgs.Handled = true;
            return;
        }

        if (mpvKey == "ESC" && DiagnosticsPanelHost.IsVisible)
        {
            DiagnosticsPanelHost.IsVisible = false;
            DiagnosticsPanelButton.Focus();
            eventArgs.Handled = true;
            return;
        }

        if (mpvKey == "ESC" && PluginPlaybackSidebar.IsVisible)
        {
            PluginPlaybackSidebar.IsVisible = false;
            PluginSidebarButton.Focus();
            eventArgs.Handled = true;
            return;
        }

        if ((TrackPanelHost.IsKeyboardFocusWithin ||
             PlaylistPanelHost.IsKeyboardFocusWithin ||
             ChapterBookmarkPanelHost.IsKeyboardFocusWithin ||
             AdvancedPanelHost.IsKeyboardFocusWithin ||
             DiagnosticsPanelHost.IsKeyboardFocusWithin ||
             PluginPlaybackSidebar.IsKeyboardFocusWithin) &&
            eventArgs.Key is not (Key.F11 or Key.F6))
        {
            return;
        }

        if (eventArgs.Key == Key.F11 ||
            eventArgs.Key == Key.F && eventArgs.KeyModifiers == KeyModifiers.None ||
            mpvKey == "f" ||
            mpvKey == "ESC" && WindowState == WindowState.FullScreen)
        {
            ToggleFullScreen();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.F6)
        {
            ShowPlaybackOverlay();
            Control[] regions = TrackPanelHost.IsVisible
                ?
                [
                    VideoTrackComboBox,
                    AudioTrackComboBox,
                    SubtitleTrackComboBox,
                    SubtitleVisibilityCheckBox,
                    SubtitleDelaySlider,
                    SubtitleScaleSlider,
                    SubtitlePositionSlider,
                    TrackPanelButton,
                ]
                : PlaylistPanelHost.IsVisible
                    ? [PlayerPlaylistList, PlaylistPanelButton]
                    : ChapterBookmarkPanelHost.IsVisible
                        ? [ChapterList, BookmarkTitleTextBox, BookmarkList, ChapterBookmarkPanelButton]
                        : AdvancedPanelHost.IsVisible
                            ?
                            [
                                PreviousFrameButton,
                                AudioDeviceComboBox,
                                VideoFilterTextBox,
                                AudioFilterTextBox,
                                ShaderList,
                                AdvancedPanelButton,
                            ]
                            : DiagnosticsPanelHost.IsVisible
                                ?
                                [
                                    TargetColorspaceHintComboBox,
                                    RuntimePropertyNameTextBox,
                                    RuntimePropertyValueTextBox,
                                    RuntimeCommandNameTextBox,
                                    RuntimeCommandArgumentsTextBox,
                                    DiagnosticsPanelButton,
                                ]
                                : PluginPlaybackSidebar.IsVisible
                                    ? [PluginPlaybackSidebar, PluginSidebarButton]
                                    :
                                    [
                                        PlaybackPositionSlider,
                                        PlaybackVolumeSlider,
                                        PlaylistPanelButton,
                                        TrackPanelButton,
                                        ChapterBookmarkPanelButton,
                                        AdvancedPanelButton,
                                        DiagnosticsPanelButton,
                                    ];
            if (PluginSidebarButton.IsVisible &&
                !TrackPanelHost.IsVisible &&
                !PlaylistPanelHost.IsVisible &&
                !ChapterBookmarkPanelHost.IsVisible &&
                !AdvancedPanelHost.IsVisible &&
                !DiagnosticsPanelHost.IsVisible &&
                !PluginPlaybackSidebar.IsVisible)
            {
                regions = [.. regions, PluginSidebarButton];
            }
            var offset = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            _focusRegion = (_focusRegion + offset + regions.Length) % regions.Length;
            regions[_focusRegion].Focus();
            eventArgs.Handled = true;
            return;
        }

        if (mpvKey is "q" or "Ctrl+w" or "Ctrl+c" or "STOP")
        {
            eventArgs.Handled = true;
            Close();
            return;
        }

        if (mpvKey == "Q")
        {
            eventArgs.Handled = true;
            await RunPlaybackActionAsync(() => ViewModel.WriteWatchLaterAsync());
            Close();
            return;
        }

        if (mpvKey == "T")
        {
            Topmost = !Topmost;
            eventArgs.Handled = true;
            return;
        }

        if (mpvKey is "Alt+0" or "Alt+1" or "Alt+2")
        {
            ApplyVideoScale(mpvKey[^1] switch
            {
                '0' => 0.5,
                '2' => 2,
                _ => 1,
            });
            eventArgs.Handled = true;
            return;
        }

        // PGUP/PGDWN are mpv's built-in chapter bindings. The seek slider would
        // otherwise consume them as large value changes when it owns focus.
        var isControlSource = eventArgs.Source is Slider or Button;
        if (mpvKey is null || (isControlSource && mpvKey is not ("PGUP" or "PGDWN")))
        {
            return;
        }

        eventArgs.Handled = true;
        if (!_pressedMpvKeys.TryAdd(eventArgs.Key, mpvKey))
        {
            return;
        }

        await RunMpvInputAsync(() => ViewModel.SendKeyDownAsync(mpvKey));
    }

    private async void OnWindowKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (!_pressedMpvKeys.Remove(eventArgs.Key, out var mpvKey))
        {
            return;
        }

        eventArgs.Handled = true;
        await RunMpvInputAsync(() => ViewModel.SendKeyUpAsync(mpvKey));
    }

    private void OnWindowDeactivated(object? sender, EventArgs eventArgs)
    {
        StopPlaybackCursorAutoHide();
        HideSeekPreview();
        _playbackVolumePointer = null;
        CancelPlaybackGesture();
        if (_pressedMpvKeys.Count == 0 && _pressedMpvMouseButtons.Count == 0)
        {
            return;
        }

        _pressedMpvKeys.Clear();
        _pressedMpvMouseButtons.Clear();
        _ = RunMpvInputAsync(() => ViewModel.SendKeyUpAsync());
    }

    private void OnMpvPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_gestureStartPoint is not null)
        {
            eventArgs.Handled = true;
            UpdatePlaybackGesture(eventArgs.GetPosition(PlaybackVideoSurface));
            return;
        }

        if (IsFromAnotherTopLevel(eventArgs))
        {
            return;
        }

        _pendingMpvMousePosition = GetMpvMousePosition(eventArgs);
        if (!_mousePositionTimer.IsEnabled)
        {
            _mousePositionTimer.Start();
        }
    }

    private async void OnMpvPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.Handled || !ViewModel.IsReady || IsPlaybackControlEvent(eventArgs))
        {
            return;
        }

        var point = eventArgs.GetCurrentPoint(PlaybackVideoSurface);
        var button = point.Properties switch
        {
            { IsMiddleButtonPressed: true } => MouseButton.Middle,
            { IsXButton1Pressed: true } => MouseButton.XButton1,
            { IsXButton2Pressed: true } => MouseButton.XButton2,
            { IsLeftButtonPressed: true } => MouseButton.Left,
            _ => (MouseButton?)null,
        };
        if (button is null)
        {
            return;
        }

        eventArgs.Handled = true;
        if (button == MouseButton.Left && eventArgs.ClickCount == 2 &&
            eventArgs.KeyModifiers == KeyModifiers.None)
        {
            CancelPlaybackGesture();
            ToggleFullScreen();
            return;
        }

        if (button == MouseButton.Left && eventArgs.KeyModifiers == KeyModifiers.None && ViewModel.IsReady)
        {
            BeginPlaybackGesture(eventArgs.Pointer, eventArgs.GetPosition(PlaybackVideoSurface));
            eventArgs.Pointer.Capture(PlaybackView);
            return;
        }

        var position = GetMpvMousePosition(eventArgs);
        await RunMpvInputAsync(() => ViewModel.SetMousePositionAsync(position.X, position.Y));

        var key = button switch
        {
            MouseButton.Middle => "MBTN_MID",
            MouseButton.XButton1 => "MBTN_BACK",
            MouseButton.XButton2 => "MBTN_FORWARD",
            _ => "MBTN_LEFT",
        };
        key = MpvInputKeyMapper.AddModifiers(key, eventArgs.KeyModifiers);
        if (!_pressedMpvMouseButtons.TryAdd(button.Value, key))
        {
            return;
        }

        eventArgs.Pointer.Capture(PlaybackView);
        await RunMpvInputAsync(() => ViewModel.SendKeyDownAsync(key));
    }

    private async void OnMpvPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (eventArgs.InitialPressMouseButton == MouseButton.Left && _gestureStartPoint is not null)
        {
            eventArgs.Handled = true;
            UpdatePlaybackGesture(eventArgs.GetPosition(PlaybackVideoSurface));
            await CompletePlaybackGestureAsync(GetMpvMousePosition(eventArgs));
            return;
        }

        if (!_pressedMpvMouseButtons.Remove(eventArgs.InitialPressMouseButton, out var key))
        {
            return;
        }

        eventArgs.Handled = true;
        await RunMpvInputAsync(() => ViewModel.SendKeyUpAsync(key));
        if (_pressedMpvMouseButtons.Count == 0)
        {
            eventArgs.Pointer.Capture(null);
        }
    }

    private void BeginPlaybackGesture(IPointer pointer, Point position)
    {
        _gesturePointer = pointer;
        _gestureStartPoint = position;
        _gestureKind = PlaybackGestureKind.None;
        _gestureStartPosition = ViewModel.PositionSeconds;
        _gestureDuration = ViewModel.DurationSeconds;
        _gestureStartVolume = ViewModel.Volume;
        _gestureStartBrightness = ViewModel.Brightness;
        _gestureSeekTarget = _gestureStartPosition;
        _pendingGestureVolume = null;
        _pendingGestureBrightness = null;
        _gestureAdjustmentTimer.Stop();
        _gestureFeedbackHideTimer.Stop();
        GestureFeedback.IsVisible = false;
    }

    private void UpdatePlaybackGesture(Point position)
    {
        if (_gestureStartPoint is not { } start)
        {
            return;
        }

        var width = PlaybackVideoSurface.Bounds.Width;
        var height = PlaybackVideoSurface.Bounds.Height;
        if (_gestureKind == PlaybackGestureKind.None)
        {
            _gestureKind = PlaybackGestureCalculator.Detect(start, position, width);
        }
        switch (_gestureKind)
        {
            case PlaybackGestureKind.Seek:
                if (!ViewModel.IsSeekable || _gestureDuration <= 0)
                {
                    ShowGestureFeedback("当前媒体不支持跳转");
                    return;
                }

                _gestureSeekTarget = PlaybackGestureCalculator.CalculateSeekTarget(
                    _gestureStartPosition,
                    _gestureDuration,
                    position.X - start.X,
                    width);
                var offset = _gestureSeekTarget - _gestureStartPosition;
                ShowGestureFeedback(
                    $"{(offset >= 0 ? "快进" : "快退")} {FormatGestureTime(Math.Abs(offset))}\n" +
                    $"{FormatGestureTime(_gestureSeekTarget)} / {FormatGestureTime(_gestureDuration)}");
                break;
            case PlaybackGestureKind.Brightness:
                _pendingGestureBrightness = PlaybackGestureCalculator.CalculateVerticalValue(
                    _gestureStartBrightness,
                    position.Y - start.Y,
                    height,
                    -100,
                    100);
                ShowGestureFeedback($"亮度 {_pendingGestureBrightness:+0;-0;0}");
                QueueGestureAdjustment();
                break;
            case PlaybackGestureKind.Volume:
                _pendingGestureVolume = PlaybackGestureCalculator.CalculateVerticalValue(
                    _gestureStartVolume,
                    position.Y - start.Y,
                    height,
                    0,
                    130);
                ShowGestureFeedback($"音量 {_pendingGestureVolume:0}%");
                QueueGestureAdjustment();
                break;
        }
    }

    private async Task CompletePlaybackGestureAsync(PixelPoint mousePosition)
    {
        var kind = _gestureKind;
        var seekTarget = _gestureSeekTarget;
        _gesturePointer?.Capture(null);
        _gesturePointer = null;
        _gestureAdjustmentTimer.Stop();
        await FlushGestureAdjustmentAsync();

        if (kind == PlaybackGestureKind.Seek && ViewModel.IsSeekable && _gestureDuration > 0 &&
            Math.Abs(seekTarget - _gestureStartPosition) >= 0.5)
        {
            await RunMpvInputAsync(() => ViewModel.SeekAsync(seekTarget));
        }
        else if (kind == PlaybackGestureKind.None)
        {
            // Preserve mpv's scoped single-click bindings (for example discnav
            // and interactive scripts) when the pointer did not become a gesture.
            await RunMpvInputAsync(() => ViewModel.SetMousePositionAsync(mousePosition.X, mousePosition.Y));
            await RunMpvInputAsync(() => ViewModel.SendKeyPressAsync("MBTN_LEFT"));
        }

        _gestureStartPoint = null;
        _gestureKind = PlaybackGestureKind.None;
        if (kind == PlaybackGestureKind.None)
        {
            GestureFeedback.IsVisible = false;
        }
        else
        {
            StartGestureFeedbackHide();
        }
    }

    private void CancelPlaybackGesture()
    {
        _gesturePointer?.Capture(null);
        _gesturePointer = null;
        _gestureStartPoint = null;
        _gestureKind = PlaybackGestureKind.None;
        _pendingGestureVolume = null;
        _pendingGestureBrightness = null;
        _gestureAdjustmentTimer.Stop();
        _gestureFeedbackHideTimer.Stop();
        GestureFeedback.IsVisible = false;
    }

    private void QueueGestureAdjustment()
    {
        if (!_gestureAdjustmentTimer.IsEnabled)
        {
            _gestureAdjustmentTimer.Start();
        }
    }

    private async Task FlushGestureAdjustmentAsync()
    {
        var brightness = _pendingGestureBrightness;
        var volume = _pendingGestureVolume;
        _pendingGestureBrightness = null;
        _pendingGestureVolume = null;

        if (brightness is { } brightnessValue)
        {
            await RunMpvInputAsync(() => ViewModel.SetBrightnessAsync(brightnessValue));
        }

        if (volume is { } volumeValue)
        {
            await RunMpvInputAsync(() => ViewModel.SetVolumeAsync(volumeValue));
        }
    }

    private void ShowGestureFeedback(string text)
    {
        GestureFeedbackText.Text = text;
        GestureFeedback.IsVisible = true;
        _gestureFeedbackHideTimer.Stop();
    }

    private void StartGestureFeedbackHide()
    {
        _gestureFeedbackHideTimer.Stop();
        _gestureFeedbackHideTimer.Start();
    }

    private static string FormatGestureTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private async void OnMpvPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (!ViewModel.IsReady || IsPlaybackControlEvent(eventArgs))
        {
            return;
        }

        eventArgs.Handled = true;
        var position = GetMpvMousePosition(eventArgs);
        await RunMpvInputAsync(() => ViewModel.SetMousePositionAsync(position.X, position.Y));
        if (eventArgs.Delta.Y != 0)
        {
            var key = MpvInputKeyMapper.AddModifiers(
                eventArgs.Delta.Y > 0 ? "WHEEL_UP" : "WHEEL_DOWN",
                eventArgs.KeyModifiers);
            await RunMpvInputAsync(() => ViewModel.SendKeyPressAsync(key, Math.Abs(eventArgs.Delta.Y)));
        }

        if (eventArgs.Delta.X != 0)
        {
            var key = MpvInputKeyMapper.AddModifiers(
                eventArgs.Delta.X > 0 ? "WHEEL_RIGHT" : "WHEEL_LEFT",
                eventArgs.KeyModifiers);
            await RunMpvInputAsync(() => ViewModel.SendKeyPressAsync(key, Math.Abs(eventArgs.Delta.X)));
        }
    }

    private void OnMpvPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (_pressedMpvMouseButtons.Count > 0 || _gestureStartPoint is not null)
        {
            return;
        }

        _pendingMpvMousePosition = null;
        _mousePositionTimer.Stop();
        if (ViewModel.IsReady)
        {
            _ = RunMpvInputAsync(() => ViewModel.SetMousePositionAsync(-1, -1));
        }
    }

    private PixelPoint GetMpvMousePosition(PointerEventArgs eventArgs)
    {
        var position = eventArgs.GetPosition(PlaybackVideoSurface);
        var scale = TopLevel.GetTopLevel(PlaybackVideoSurface)?.RenderScaling ?? 1;
        return new PixelPoint(
            (int)Math.Round(position.X * scale),
            (int)Math.Round(position.Y * scale));
    }

    private bool IsPointerOverPlaybackControls() =>
        (_playbackOverlayShown && PlaybackOverlay.IsPointerOver) ||
        TrackPanelHost.IsPointerOver ||
        PlaylistPanelHost.IsPointerOver ||
        ChapterBookmarkPanelHost.IsPointerOver ||
        AdvancedPanelHost.IsPointerOver ||
        DiagnosticsPanelHost.IsPointerOver ||
        PluginPlaybackSidebar.IsPointerOver ||
        PluginPlaybackStatusHost.IsPointerOver ||
        PluginPlaybackMenuHost.IsPointerOver;

    private bool IsPlaybackControlEvent(PointerEventArgs eventArgs)
    {
        if (IsFromAnotherTopLevel(eventArgs))
        {
            return true;
        }

        if (eventArgs.Source is Visual visual &&
            (ContainsVisual(PlaybackOverlay, visual) ||
             ContainsVisual(TrackPanelHost, visual) ||
             ContainsVisual(PlaylistPanelHost, visual) ||
             ContainsVisual(ChapterBookmarkPanelHost, visual) ||
             ContainsVisual(AdvancedPanelHost, visual) ||
             ContainsVisual(DiagnosticsPanelHost, visual) ||
             ContainsVisual(PluginPlaybackSidebar, visual) ||
             ContainsVisual(PluginPlaybackStatusHost, visual) ||
             ContainsVisual(PluginPlaybackMenuHost, visual)))
        {
            return true;
        }

        if (_playbackOverlayShown &&
            new Rect(PlaybackOverlay.Bounds.Size).Contains(eventArgs.GetPosition(PlaybackOverlay)))
        {
            return true;
        }

        return IsPointerOverPlaybackControls();
    }

    private static bool ContainsVisual(Visual root, Visual candidate) =>
        ReferenceEquals(root, candidate) || root.IsVisualAncestorOf(candidate);

    // Windows popups (ComboBox drop-downs, flyouts, tooltips) are separate
    // PopupRoot windows whose events still tunnel through this window's
    // handlers. They are never part of the video surface, so video gestures
    // must not handle or capture them.
    private bool IsFromAnotherTopLevel(PointerEventArgs eventArgs) =>
        eventArgs.Source is Visual visual &&
        !ReferenceEquals(TopLevel.GetTopLevel(visual), this);

    private void FlushMpvMousePosition()
    {
        _mousePositionTimer.Stop();
        if (_pendingMpvMousePosition is not { } position || !ViewModel.IsReady)
        {
            return;
        }

        _pendingMpvMousePosition = null;
        _ = RunMpvInputAsync(() => ViewModel.SetMousePositionAsync(position.X, position.Y));
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            ToggleMaximized();
            eventArgs.Handled = true;
            return;
        }

        BeginMoveDrag(eventArgs);
        eventArgs.Handled = true;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs eventArgs) => ToggleMaximized();

    private void OnCloseClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void OnResizeTop(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.North, eventArgs);

    private void OnResizeBottom(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.South, eventArgs);

    private void OnResizeLeft(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.West, eventArgs);

    private void OnResizeRight(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.East, eventArgs);

    private void OnResizeTopLeft(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.NorthWest, eventArgs);

    private void OnResizeTopRight(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.NorthEast, eventArgs);

    private void OnResizeBottomLeft(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.SouthWest, eventArgs);

    private void OnResizeBottomRight(object? sender, PointerPressedEventArgs eventArgs) => BeginResize(WindowEdge.SouthEast, eventArgs);

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
        {
            _initialSizeSettleTimer.Stop();
            _pendingInitialClientSize = null;
            _activeResizeEdge = edge;
            _activeResizeConstraint = edge switch
            {
                WindowEdge.West or WindowEdge.East => ResizeConstraint.Width,
                WindowEdge.North or WindowEdge.South => ResizeConstraint.Height,
                _ => null,
            };
            _lastUserClientSize = ClientSize;
            _resizeCompletionTimer.Stop();
            BeginResizeDrag(edge, eventArgs);
            eventArgs.Handled = true;
        }
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ToggleFullScreen()
    {
        var enterFullScreen = WindowState != WindowState.FullScreen;
        if (enterFullScreen)
        {
            _windowStateBeforeFullScreen = WindowState;
        }

        WindowState = enterFullScreen ? WindowState.FullScreen : _windowStateBeforeFullScreen;
        UpdateWindowChrome();
    }

    private void ApplyVideoScale(double scale)
    {
        if (_videoAspectRatio <= 0 || ViewModel.VideoWidth <= 0 || ViewModel.VideoHeight <= 0)
        {
            return;
        }

        WindowState = WindowState.Normal;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var renderScale = screen?.Scaling is > 0 ? screen.Scaling : 1;
        var chrome = GetVideoChromeSize();
        var target = new Size(
            ViewModel.VideoWidth / renderScale * scale + chrome.Width,
            ViewModel.VideoHeight / renderScale * scale + chrome.Height);
        ApplyClientSize(CalculateAspectConstrainedSize(target, widthDriven: true), centerOnScreen: false, screen: null);
    }

    private void OnPlaybackViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(PlaybackViewModel.Source) &&
            !string.Equals(_seekPreviewSource, ViewModel.Source, StringComparison.Ordinal))
        {
            ResetSeekPreviewSource(ViewModel.Source);
        }
        else if (eventArgs.PropertyName == nameof(PlaybackViewModel.CanSeek) && !ViewModel.CanSeek)
        {
            HideSeekPreview();
        }

        if (eventArgs.PropertyName == nameof(PlaybackViewModel.Speed) &&
            _playbackSpeedPendingTenths is { } targetTenths &&
            Math.Abs(ViewModel.Speed - targetTenths / 10d) < 0.001)
        {
            _playbackSpeedPendingTenths = null;
        }

        if ((eventArgs.PropertyName is nameof(PlaybackViewModel.PositionSeconds) or
            nameof(PlaybackViewModel.DurationSeconds)) &&
            !_playbackPositionInteractionActive)
        {
            if (TryConfirmPlaybackPositionSeek())
            {
                SynchronizePlaybackPositionSlider();
            }
            else
            {
                PlaybackPositionSlider.Maximum = ViewModel.DurationMaximum;
            }
        }

        if (eventArgs.PropertyName == nameof(PlaybackViewModel.State))
        {
            var state = ViewModel.State;
            if (state != _lastPlaybackState)
            {
                RefreshPlaybackCursorAutoHide();
            }
            if (state != _lastPlaybackState &&
                state is (PlaybackState.Idle or PlaybackState.Loading or PlaybackState.Ended or PlaybackState.Error))
            {
                CancelPlaybackGesture();
            }

            if (state == PlaybackState.Loading && _lastPlaybackState != PlaybackState.Loading)
            {
                _hasAppliedInitialVideoSize = false;
                _videoAspectRatio = 0;
                _initialSizeSettleTimer.Stop();
                _pendingInitialClientSize = null;
                _activeResizeConstraint = null;
            }

            if (_deferInitialWaylandReveal &&
                state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Error)
            {
                if (ViewModel.VideoWidth > 0 && ViewModel.VideoHeight > 0)
                {
                    UpdateVideoAspectRatio();
                }
                else
                {
                    RevealPlaybackWindow();
                }
            }

            _lastPlaybackState = state;
        }

        if (eventArgs.PropertyName is nameof(PlaybackViewModel.VideoWidth) or nameof(PlaybackViewModel.VideoHeight))
        {
            UpdateVideoAspectRatio();
        }
    }

    private void SynchronizePlaybackPositionSlider()
    {
        PlaybackPositionSlider.Maximum = ViewModel.DurationMaximum;
        PlaybackPositionSlider.Value = Math.Clamp(
            ViewModel.PositionSeconds,
            PlaybackPositionSlider.Minimum,
            PlaybackPositionSlider.Maximum);
    }

    private bool TryConfirmPlaybackPositionSeek()
    {
        if (_playbackPositionPendingTarget is not { } targetSeconds)
        {
            return !_playbackPositionSeekPending;
        }

        var currentSeconds = ViewModel.PositionSeconds;
        var reachedTarget = Math.Abs(currentSeconds - targetSeconds) <= 1.5;
        var movedFromOldPosition = Math.Abs(currentSeconds - _playbackPositionBeforeSeek) >= 2;
        var timedOut = Environment.TickCount64 >= _playbackPositionConfirmationDeadline;
        if (!reachedTarget && !movedFromOldPosition && !timedOut)
        {
            PlaybackPositionSlider.Value = targetSeconds;
            return false;
        }

        _playbackPositionPendingTarget = null;
        return true;
    }

    private void UpdateVideoAspectRatio()
    {
        if (ViewModel.VideoWidth <= 0 || ViewModel.VideoHeight <= 0)
        {
            return;
        }

        _videoAspectRatio = ViewModel.VideoWidth / (double)ViewModel.VideoHeight;
        TryApplyInitialVideoSize();
    }

    private void TryApplyInitialVideoSize()
    {
        if (_hasAppliedInitialVideoSize || _videoAspectRatio <= 0 ||
            WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var chrome = GetVideoChromeSize();
        var naturalWidth = ViewModel.VideoWidth / scaling;
        var naturalHeight = ViewModel.VideoHeight / scaling;
        var availableWidth = Math.Max(1, screen.WorkingArea.Width / scaling * 0.9 - chrome.Width);
        var availableHeight = Math.Max(1, screen.WorkingArea.Height / scaling * 0.9 - chrome.Height);
        var maximumScale = Math.Min(availableWidth / naturalWidth, availableHeight / naturalHeight);
        var minimumScale = Math.Max(MinimumVideoWidth / naturalWidth, MinimumVideoHeight / naturalHeight);
        var scale = Math.Min(maximumScale, Math.Max(1, minimumScale));
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return;
        }

        var targetSize = new Size(
            naturalWidth * scale + chrome.Width,
            naturalHeight * scale + chrome.Height);
        _hasAppliedInitialVideoSize = true;
        _pendingInitialClientSize = targetSize;
        _initialSizeSettleTimer.Stop();
        _initialSizeSettleTimer.Start();
        if (_deferInitialWaylandReveal)
        {
            _ = ApplyInitialWaylandSizeAsync(targetSize);
            return;
        }

        ApplyClientSize(targetSize, centerOnScreen: true, screen);
    }

    private async Task ApplyInitialWaylandSizeAsync(Size targetSize)
    {
        if (_isApplyingInitialWaylandSize)
        {
            return;
        }

        _isApplyingInitialWaylandSize = true;
        try
        {
            Hide();

            // Let Avalonia release the old EGL surface before a new Wayland
            // xdg_toplevel is created with the final video-sized geometry.
            for (var attempt = 0; attempt < 25 && PlaybackVideoSurface.HasRenderContext; attempt++)
            {
                await Task.Delay(10);
            }

            Width = targetSize.Width;
            Height = targetSize.Height;
            ClientSize = targetSize;

            // The player is an independent top-level (see ShowAsIndependentWindow),
            // so the recreated xdg_toplevel has no parent to replay.
            Show();

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            for (var attempt = 0; attempt < 25 && !PlaybackVideoSurface.HasRenderContext; attempt++)
            {
                await Task.Delay(10);
            }

            RevealPlaybackWindow();
        }
        catch (Exception exception)
        {
            RevealPlaybackWindow();
            ViewModel.ReportOperationError(exception);
        }
        finally
        {
            _isApplyingInitialWaylandSize = false;
        }
    }

    private void RevealPlaybackWindow()
    {
        _deferInitialWaylandReveal = false;
        Opacity = 1;
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs eventArgs)
    {
        var previousSize = _lastUserClientSize;
        _lastUserClientSize = eventArgs.ClientSize;

        // Wayland's configure size is filtered in OnPlatformResized before it
        // reaches Avalonia layout. A second correction here would reintroduce
        // the visible one-frame bounce and sluggish resize loop.
        if (IsWaylandWindow())
        {
            return;
        }

        if (!_isApplyingAspectConstraint &&
            eventArgs.Reason != WindowResizeReason.Application &&
            WindowState == WindowState.Normal &&
            _pendingInitialClientSize is { } initialSize &&
            !AreClose(initialSize, eventArgs.ClientSize))
        {
            // A configure queued before the media dimensions became available can
            // arrive after our first resize request. Re-assert the initial size
            // during this short settling period so the old configure cannot win.
            _initialSizeSettleTimer.Stop();
            _initialSizeSettleTimer.Start();
            ApplyClientSize(initialSize, centerOnScreen: false, screen: null);
            return;
        }

        // Wayland reports compositor-driven interactive resizes as Layout, while
        // X11 commonly reports Unspecified, so reason alone cannot identify one.
        if (_isApplyingAspectConstraint || eventArgs.Reason == WindowResizeReason.Application ||
            WindowState != WindowState.Normal || _videoAspectRatio <= 0)
        {
            return;
        }

        _resizeCompletionTimer.Stop();
        _resizeCompletionTimer.Start();
        var widthDriven = IsWidthDrivenResize(eventArgs.ClientSize, previousSize);
        var targetSize = CalculateAspectConstrainedSize(eventArgs.ClientSize, widthDriven);
        if (Math.Abs(targetSize.Width - eventArgs.ClientSize.Width) < 0.5 &&
            Math.Abs(targetSize.Height - eventArgs.ClientSize.Height) < 0.5)
        {
            return;
        }

        ApplyClientSize(targetSize, centerOnScreen: false, screen: null);
    }

    private void OnPlatformResized(Size clientSize, WindowResizeReason reason)
    {
        var previousSize = _lastUserClientSize;
        if (IsWaylandWindow() && !_isApplyingAspectConstraint &&
            WindowState == WindowState.Normal && _videoAspectRatio > 0)
        {
            _resizeCompletionTimer.Stop();
            _resizeCompletionTimer.Start();
            var constrainedSize = CalculateAspectConstrainedSize(
                clientSize,
                IsWidthDrivenResize(clientSize, previousSize));
            if (!AreClose(clientSize, constrainedSize))
            {
                // Keep the backend's own size in sync without raising a second
                // framework resize. On Wayland this determines the buffer size
                // committed in response to the compositor's configure event.
                if (PlatformImpl is { } platformImpl)
                {
                    _isApplyingAspectConstraint = true;
                    try
                    {
                        platformImpl.Resized = null;
                        platformImpl.Resize(constrainedSize, WindowResizeReason.Application);
                    }
                    finally
                    {
                        platformImpl.Resized = OnPlatformResized;
                        _isApplyingAspectConstraint = false;
                    }
                }

                clientSize = constrainedSize;
            }
        }

        _lastUserClientSize = clientSize;
        _frameworkResizeHandler(clientSize, reason);
    }

    private bool IsWidthDrivenResize(Size clientSize, Size previousSize)
    {
        if (_activeResizeConstraint is { } constraint)
        {
            return constraint == ResizeConstraint.Width;
        }

        var widthDriven = previousSize.Width <= 0 || previousSize.Height <= 0 ||
            Math.Abs(clientSize.Width - previousSize.Width) >= 0.5 ||
            Math.Abs(clientSize.Height - previousSize.Height) < 0.5;

        if (_activeResizeEdge is not null)
        {
            // Match mpv's Wayland strategy: choose the controlling dimension
            // from the first effective configure in a resize gesture, then keep
            // it until the next gesture. Re-evaluating it for every configure can
            // alternate between two valid aspect projections and visibly bounce
            // the window toward its original size.
            _activeResizeConstraint = widthDriven
                ? ResizeConstraint.Width
                : ResizeConstraint.Height;
        }

        return widthDriven;
    }

    private static bool AreClose(Size left, Size right) =>
        Math.Abs(left.Width - right.Width) < 0.5 && Math.Abs(left.Height - right.Height) < 0.5;

    private Size CalculateAspectConstrainedSize(Size clientSize, bool widthDriven)
    {
        var chrome = GetVideoChromeSize();
        var videoWidth = Math.Max(1, clientSize.Width - chrome.Width);
        var videoHeight = Math.Max(1, clientSize.Height - chrome.Height);
        if (widthDriven)
        {
            videoWidth = Math.Max(videoWidth, Math.Max(MinimumVideoWidth, MinimumVideoHeight * _videoAspectRatio));
            videoHeight = videoWidth / _videoAspectRatio;
        }
        else
        {
            videoHeight = Math.Max(videoHeight, Math.Max(MinimumVideoHeight, MinimumVideoWidth / _videoAspectRatio));
            videoWidth = videoHeight * _videoAspectRatio;
        }

        return new Size(videoWidth + chrome.Width, videoHeight + chrome.Height);
    }

    private Size GetVideoChromeSize()
    {
        var border = WindowFrame.BorderThickness;
        var titleBarHeight = CustomTitleBar.IsVisible ? CustomTitleBar.Height : 0;
        return new Size(border.Left + border.Right, border.Top + border.Bottom + titleBarHeight);
    }

    private void ApplyClientSize(Size targetSize, bool centerOnScreen, Avalonia.Platform.Screen? screen)
    {
        _isApplyingAspectConstraint = true;
        try
        {
            var currentSize = ClientSize;
            var position = Position;
            var scaling = DesktopScaling > 0 ? DesktopScaling : 1;
            var canSetPosition = !IsWaylandWindow();
            if (!centerOnScreen && canSetPosition)
            {
                if (_activeResizeEdge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest)
                {
                    position = position.WithX(position.X + (int)Math.Round((currentSize.Width - targetSize.Width) * scaling));
                }

                if (_activeResizeEdge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast)
                {
                    position = position.WithY(position.Y + (int)Math.Round((currentSize.Height - targetSize.Height) * scaling));
                }
            }

            // Keep Avalonia's layout constraints in sync first (both invalidations
            // are coalesced), then submit one complete client size to the backend.
            Width = targetSize.Width;
            Height = targetSize.Height;
            ClientSize = targetSize;
            if (centerOnScreen && screen is not null && canSetPosition)
            {
                var pixelWidth = (int)Math.Round(targetSize.Width * screen.Scaling);
                var pixelHeight = (int)Math.Round(targetSize.Height * screen.Scaling);
                position = new PixelPoint(
                    screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - pixelWidth) / 2),
                    screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - pixelHeight) / 2));
            }

            if (canSetPosition)
            {
                Position = position;
            }
        }
        finally
        {
            _isApplyingAspectConstraint = false;
        }
    }

    private bool IsWaylandWindow() => OperatingSystem.IsLinux() && TryGetPlatformHandle() is null;

    private enum ResizeConstraint
    {
        Width,
        Height,
    }

    private void UpdateWindowChrome()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        ResizeLayer.IsVisible = !_useNativeFrame && WindowState == WindowState.Normal;
        CustomTitleBar.IsVisible = !_useNativeFrame && WindowState != WindowState.FullScreen;
        WindowFrame.BorderThickness = new Avalonia.Thickness(
            _useNativeFrame || isMaximized || WindowState == WindowState.FullScreen ? 0 : 1);
        MaximizeButton.Classes.Set("restore", isMaximized);
        Avalonia.Automation.AutomationProperties.SetName(
            MaximizeButton,
            isMaximized ? "还原播放器窗口" : "最大化播放器窗口");
        ToolTip.SetTip(MaximizeButton, isMaximized ? "还原" : "最大化");
    }

    private async Task<bool> RunPlaybackActionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception exception)
        {
            ViewModel.ReportOperationError(exception);
            if (!_isPreparingToClose && IsVisible && PlatformImpl is not null)
            {
                await new ErrorMessageDialog("播放器操作失败", exception.Message).ShowDialog(this);
            }

            return false;
        }
    }

    private async Task RunMpvInputAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ViewModel.ReportOperationError(exception);
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        CancelPlaybackGesture();

        // The application is shutting down, so the close must not be cancelled
        // while this window performs its normal two-phase teardown; cancelling it
        // would abort the shutdown.
        if (_allowClose || eventArgs.CloseReason is
                WindowCloseReason.OwnerWindowClosing or
                WindowCloseReason.ApplicationShutdown or
                WindowCloseReason.OSShutdown)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_isPreparingToClose)
        {
            return;
        }

        _isPreparingToClose = true;
        var hadRenderContext = PlaybackVideoSurface.HasRenderContext;
        VideoHost.Children.Remove(PlaybackVideoSurface);
        _ = CompleteCloseAsync(hadRenderContext);
    }

    private async Task CompleteCloseAsync(bool waitForRenderRelease)
    {
        if (waitForRenderRelease)
        {
            await Task.Delay(150);
        }

        if (DataContext is PlaybackViewModel viewModel && viewModel.IsReady && viewModel.HasMedia)
        {
            try
            {
                await viewModel.StopAsync();
            }
            catch (Exception exception)
            {
                viewModel.ReportOperationError(exception);
            }
        }

        _allowClose = true;
        Close();
    }
}
