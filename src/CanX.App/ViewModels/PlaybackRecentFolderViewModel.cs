// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;
using System.ComponentModel;

namespace CanX.App.ViewModels;

public sealed class PlaybackRecentFolderViewModel(
    Uri Directory,
    string Name,
    string Location,
    DateTimeOffset LastPlayedAt,
    string? MediaSourceProfileId) : INotifyPropertyChanged
{
    public Uri Directory { get; } = Directory;
    public string Name { get; } = Name;
    public string Location { get; } = Location;
    public DateTimeOffset LastPlayedAt { get; private set; } = LastPlayedAt;
    public string? MediaSourceProfileId { get; } = MediaSourceProfileId;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string LastPlayedText => LastPlayedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    internal void UpdateLastPlayedAt(DateTimeOffset value)
    {
        if (LastPlayedAt == value) return;
        LastPlayedAt = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPlayedAt)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPlayedText)));
    }
}

public static class PlaybackRecentFolders
{
    public static IReadOnlyList<PlaybackRecentFolderViewModel> Build(
        IReadOnlyList<PlaybackRecentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var folders = new Dictionary<(string Directory, string? ProfileId), PlaybackRecentFolderViewModel>();
        foreach (var item in items)
        {
            if (!TryGetDirectory(item, out var directory))
            {
                continue;
            }

            var profileId = item.IsMediaSource ? item.MediaSourceProfileId : null;
            var key = (directory.AbsoluteUri, profileId);
            if (folders.TryGetValue(key, out var existing) &&
                existing.LastPlayedAt >= item.LastPlayedAt)
            {
                continue;
            }

            var path = Uri.UnescapeDataString(directory.AbsolutePath).TrimEnd('/');
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
            {
                name = directory.IsFile ? directory.LocalPath : directory.Host;
            }

            var location = directory.IsFile
                ? directory.LocalPath
                : $"{directory.Scheme}://{directory.Authority}{Uri.UnescapeDataString(directory.AbsolutePath)}";
            folders[key] = new PlaybackRecentFolderViewModel(
                directory, name, location, item.LastPlayedAt, profileId);
        }

        return folders.Values
            .OrderByDescending(static folder => folder.LastPlayedAt)
            .ThenBy(static folder => folder.Location, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetDirectory(PlaybackRecentItem item, out Uri directory)
    {
        directory = null!;
        if (string.IsNullOrWhiteSpace(item.Source))
        {
            return false;
        }

        Uri? video;
        if (!Uri.TryCreate(item.Source, UriKind.Absolute, out video))
        {
            if (!Path.IsPathFullyQualified(item.Source))
            {
                return false;
            }

            video = new Uri(Path.GetFullPath(item.Source));
        }

        if (!video.IsFile && (!item.IsMediaSource ||
            video.Scheme is not ("smb" or "http" or "https" or "ftp" or "ftps")))
        {
            return false;
        }

        if (!ExternalSubtitleMatcher.IsVideoFile(Uri.UnescapeDataString(video.AbsolutePath)))
        {
            return false;
        }

        directory = new Uri(video, ".");
        return true;
    }
}
