// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.MediaAccess;

namespace CanX.App.ViewModels;

internal sealed class PlaybackHistoryProgressLookup
{
    private readonly Dictionary<(string Source, string? ProfileId), PlaybackRecentItemViewModel> _items = [];

    public PlaybackHistoryProgressLookup(IReadOnlyList<PlaybackRecentItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items.OrderByDescending(static item => item.LastPlayedAt))
        {
            if (TryGetUri(item.Source, out var uri))
            {
                var profileId = uri.IsFile || !item.IsMediaSource ? null : item.MediaSourceProfileId;
                _items.TryAdd((uri.AbsoluteUri, profileId), item);
            }
        }
    }

    public PlaybackRecentItemViewModel? Find(MediaResource resource, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var source = resource.Uri.AbsoluteUri;
        var matchingProfileId = resource.Uri.IsFile ? null : profileId;
        if (_items.TryGetValue((source, matchingProfileId), out var item))
        {
            return item;
        }

        return matchingProfileId is not null && _items.TryGetValue((source, null), out item)
            ? item
            : null;
    }

    private static bool TryGetUri(string source, out Uri uri)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out uri!))
        {
            return uri.IsFile || uri.Scheme is "smb" or "http" or "https" or "ftp" or "ftps";
        }

        if (Path.IsPathFullyQualified(source))
        {
            uri = new Uri(Path.GetFullPath(source));
            return true;
        }

        uri = null!;
        return false;
    }
}
