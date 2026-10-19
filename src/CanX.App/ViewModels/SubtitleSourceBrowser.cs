// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;
using CanX.MediaAccess;

namespace CanX.App.ViewModels;

/// <summary>Independent subtitle browser; leases remain valid after this browser closes.</summary>
public sealed class SubtitleSourceBrowser : IAsyncDisposable
{
    private readonly MediaSourceClientSession _session;
    private readonly IDisposable? _retention;
    private bool _disposed;

    internal SubtitleSourceBrowser(IMediaSourceClient client)
    {
        _session = new MediaSourceClientSession(client);
        Root = client.Root;
    }

    internal SubtitleSourceBrowser(MediaSourceClientSession session)
    {
        _session = session;
        _retention = session.Retain();
        Root = session.Client.Root;
    }

    public Uri Root { get; }

    public Uri CurrentDirectory { get; private set; } = null!;

    public IReadOnlyList<MediaResource> Entries { get; private set; } = [];

    public bool CanNavigateUp => CurrentDirectory is not null &&
        Uri.Compare(CurrentDirectory, Root, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) != 0;

    public async Task BrowseAsync(Uri? directory = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = directory ?? Root;
        if (!Root.IsBaseOf(target))
        {
            throw new ArgumentException("目标目录不在播放源内。", nameof(directory));
        }

        using var retention = _session.Retain();
        var entries = await _session.Client.BrowseAsync(target, cancellationToken).ConfigureAwait(true);
        Entries = entries.Where(static item => item.IsDirectory ||
                ExternalSubtitleMatcher.IsSubtitleFile(item.Name))
            .OrderByDescending(static item => item.IsDirectory)
            .ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        CurrentDirectory = target;
    }

    public Task NavigateUpAsync(CancellationToken cancellationToken = default) =>
        CanNavigateUp ? BrowseAsync(new Uri(CurrentDirectory, "../"), cancellationToken)
            : Task.CompletedTask;

    public MediaSourceReadLease AcquireRead(MediaResource resource)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (resource.IsDirectory || !ExternalSubtitleMatcher.IsSubtitleFile(resource.Name) ||
            !Root.IsBaseOf(resource.Uri) || !Entries.Contains(resource))
        {
            throw new ArgumentException("请选择字幕文件。", nameof(resource));
        }

        return _session.Acquire(resource);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_retention is not null) _retention.Dispose();
            else _session.Release();
        }

        return ValueTask.CompletedTask;
    }
}
