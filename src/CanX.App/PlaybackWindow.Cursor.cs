// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CanX.App;

public sealed partial class PlaybackWindow
{
    private static readonly Cursor HiddenPlaybackCursor = new(StandardCursorType.None);
    private readonly DispatcherTimer _cursorHideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _playbackCursorHidden;

    private void InitializePlaybackCursorAutoHide()
    {
        _cursorHideTimer.Tick += (_, _) =>
        {
            _cursorHideTimer.Stop();
            if (!CanAutoHidePlaybackCursor()) return;
            PlaybackVideoBorder.Cursor = HiddenPlaybackCursor;
            _playbackCursorHidden = true;
        };
        PlaybackWindowRoot.AddHandler(
            PointerPressedEvent,
            (_, eventArgs) => NotePlaybackCursorActivity(eventArgs),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackWindowRoot.AddHandler(
            PointerReleasedEvent,
            (_, eventArgs) => NotePlaybackCursorActivity(eventArgs),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlaybackWindowRoot.AddHandler(
            PointerWheelChangedEvent,
            (_, eventArgs) => NotePlaybackCursorActivity(eventArgs),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Activated += (_, _) => RefreshPlaybackCursorAutoHide();
    }

    private void NotePlaybackCursorActivity(PointerEventArgs eventArgs)
    {
        if (eventArgs.Pointer.Type != PointerType.Mouse || IsFromAnotherTopLevel(eventArgs)) return;
        RestorePlaybackCursor();
        RestartPlaybackCursorHideTimer();
    }

    private void RefreshPlaybackCursorAutoHide()
    {
        if (CanAutoHidePlaybackCursor()) RestartPlaybackCursorHideTimer();
        else StopPlaybackCursorAutoHide();
    }

    private void RestartPlaybackCursorHideTimer()
    {
        _cursorHideTimer.Stop();
        if (CanAutoHidePlaybackCursor()) _cursorHideTimer.Start();
    }

    private bool CanAutoHidePlaybackCursor() =>
        IsActive && DataContext is ViewModels.PlaybackViewModel { IsPlaying: true } &&
        PlaybackVideoBorder.IsPointerOver && !_pointerInPlaybackOverlayZone &&
        !IsPointerOverPlaybackControls() &&
        !_playbackPositionInteractionActive && _playbackVolumePointer is null &&
        _gestureStartPoint is null && _pressedMpvMouseButtons.Count == 0;

    private void StopPlaybackCursorAutoHide()
    {
        _cursorHideTimer.Stop();
        RestorePlaybackCursor();
    }

    private void RestorePlaybackCursor()
    {
        if (!_playbackCursorHidden) return;
        PlaybackVideoBorder.Cursor = null;
        _playbackCursorHidden = false;
    }
}
