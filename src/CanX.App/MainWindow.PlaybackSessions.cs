// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using CanX.Application.Playback;

namespace CanX.App;

public sealed partial class MainWindow
{
    private DesktopPlaybackSession? _primaryPlaybackSession;
    private DesktopPlaybackSession? _activePlaybackSession;
    private Func<DesktopPlaybackSession>? _playbackSessionFactory;
    private readonly List<DesktopPlaybackSession> _playbackSessions = [];
    private readonly List<Task> _playbackSessionDisposals = [];
    private readonly SemaphoreSlim _playbackSessionGate = new(1, 1);
    private bool _playbackSessionsClosing;

    private DesktopPlaybackSession ActivePlaybackSession =>
        _activePlaybackSession ?? _primaryPlaybackSession ?? throw new InvalidOperationException("播放器尚未初始化。");

    internal IReadOnlyList<DesktopPlaybackSession> PlaybackSessions => _playbackSessions;

    private void ActivatePlaybackSession(DesktopPlaybackSession session)
    {
        if (session.IsClosing || _playbackSessionsClosing) return;
        _activePlaybackSession = session;
        session.Activate();
        _playbackSessions.Remove(session);
        _playbackSessions.Add(session);
    }

    private PlaybackWindow ShowSessionWindow(DesktopPlaybackSession session)
    {
        if (session.Window is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            ActivatePlaybackSession(session);
            return existing;
        }

        var window = new PlaybackWindow(
            session.ViewModel, session.RenderSource, ViewModel.PluginUi, ViewModel.MediaSources,
            (lease, subtitle, profileId) => AddSelectedNetworkSubtitleAsync(session, lease, subtitle, profileId),
            ViewModel.OnlineSubtitles, subtitle => AddOnlineSubtitleAsync(session, subtitle),
            _streamBroker, ViewModel.Settings.ActiveMpvSettings);
        session.ViewModel.ConfigureRememberedSubtitleLoader((source, select, token) =>
            RestoreRememberedNetworkSubtitleAsync(session, source, select, token));
        session.Window = window;
        session.IsClosing = false;
        window.Activated += (_, _) => ActivatePlaybackSession(session);
        window.Closing += (_, _) => session.IsClosing = true;
        window.Closed += (_, _) =>
        {
            session.Window = null;
            session.HasOpenedMedia = false;
            if (session.OwnsViewModel)
            {
                _playbackSessions.Remove(session);
                _playbackSessionDisposals.Add(session.DisposeAsync().AsTask());
            }
            else
            {
                session.ReleaseResources();
                session.IsClosing = false;
            }
            if (ReferenceEquals(_activePlaybackSession, session))
            {
                _activePlaybackSession = _playbackSessions.LastOrDefault(candidate =>
                    !candidate.IsClosing && candidate.Window is not null) ?? _primaryPlaybackSession;
                _activePlaybackSession?.Activate();
            }
        };
        window.ShowForPlayback(this);
        window.Activate();
        ActivatePlaybackSession(session);
        return window;
    }

    private async Task<DesktopPlaybackSession> PreparePlaybackSessionAsync(CancellationToken cancellationToken = default)
    {
        await _playbackSessionGate.WaitAsync(cancellationToken);
        DesktopPlaybackSession? created = null;
        try
        {
            ObjectDisposedException.ThrowIf(_playbackSessionsClosing, this);
            var session = ActivePlaybackSession;
            if (session.IsClosing ||
                (ViewModel.Settings.OpenMediaInNewWindow && session.Window is not null &&
                 (session.HasOpenedMedia || session.ViewModel.HasMedia)))
            {
                session = created = (_playbackSessionFactory ?? throw new InvalidOperationException("无法创建新的播放器。"))();
                _playbackSessions.Add(session);
            }
            if (!session.ViewModel.IsReady) await session.ViewModel.InitializeAsync(cancellationToken);
            ObjectDisposedException.ThrowIf(_playbackSessionsClosing, this);
            if (session.ViewModel.HasMedia) await session.ViewModel.StopAsync(cancellationToken);
            session.ReleaseResources();
            session.HasOpenedMedia = true;
            ShowSessionWindow(session);
            return session;
        }
        catch
        {
            if (created is not null)
            {
                _playbackSessions.Remove(created);
                await created.DisposeAsync();
            }
            throw;
        }
        finally { _playbackSessionGate.Release(); }
    }

    internal async Task<IPlaybackController> ResolvePlaybackToolControllerAsync(string toolName, CancellationToken cancellationToken)
    {
        var opensMedia = toolName is "playback.open" or "playback.playlist.open" or
            "playback.folder.open" or "playback.recent.open" or "playback.bookmark.open";
        var session = opensMedia ? await PreparePlaybackSessionAsync(cancellationToken) : ActivePlaybackSession;
        if (opensMedia) await session.Window!.WaitForPlaybackSurfaceAsync(cancellationToken);
        return session.ViewModel.Controller;
    }

    internal async Task DisposePlaybackSessionsAsync()
    {
        _playbackSessionsClosing = true;
        await Task.WhenAll(_playbackSessions.ToArray().Select(session => session.DisposeAsync().AsTask())
            .Concat(_playbackSessionDisposals.ToArray())).ConfigureAwait(false);
    }
}
