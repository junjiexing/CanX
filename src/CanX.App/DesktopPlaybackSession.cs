// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.ViewModels;
using CanX.MediaAccess;
using CanX.Playback.Engine;
using CanX.Streaming;

namespace CanX.App;

public sealed class DesktopPlaybackSession(
    PlaybackViewModel viewModel,
    IMpvRenderSource renderSource,
    bool ownsViewModel,
    Action? activate = null) : IAsyncDisposable
{
    private Task? _disposeTask;
    public PlaybackViewModel ViewModel { get; } = viewModel;
    public IMpvRenderSource RenderSource { get; } = renderSource;
    public PlaybackWindow? Window { get; internal set; }
    public bool HasOpenedMedia { get; internal set; }
    public bool IsClosing { get; internal set; }
    public bool OwnsViewModel { get; } = ownsViewModel;
    internal List<MediaStreamRegistration> StreamRegistrations { get; } = [];
    internal List<MediaSourceReadLease> ReadLeases { get; } = [];

    internal void Activate() => activate?.Invoke();

    internal void ReleaseResources()
    {
        ViewModel.ConfigureMediaSourceSubtitleLoader(null);
        foreach (var registration in StreamRegistrations) registration.Dispose();
        StreamRegistrations.Clear();
        foreach (var lease in ReadLeases) lease.Dispose();
        ReadLeases.Clear();
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        try
        {
            await ViewModel.DisposeAsync().ConfigureAwait(false);
        }
        finally { ReleaseResources(); }
    }
}
