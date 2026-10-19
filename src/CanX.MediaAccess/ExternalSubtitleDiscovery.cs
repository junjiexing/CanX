// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;

namespace CanX.MediaAccess;

public sealed record MediaSubtitleMatch(MediaResource Resource, ExternalSubtitleMatch Match);

public static class ExternalSubtitleDiscovery
{
    private static readonly HashSet<string> SubtitleFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Subs", "Subtitles", "字幕",
    };

    public static Task<IReadOnlyList<ExternalSubtitleMatch>> FindLocalAsync(
        string videoPath,
        string? preferredLanguages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        return Task.Run(() => FindLocal(videoPath, preferredLanguages, cancellationToken), cancellationToken);
    }

    public static async ValueTask<IReadOnlyList<MediaSubtitleMatch>> FindAsync(
        IMediaSourceClient client,
        MediaResource video,
        string? preferredLanguages,
        string mode = "exact",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(video);
        if (video.IsDirectory || string.Equals(mode, "no", StringComparison.OrdinalIgnoreCase)) return [];

        var parent = new Uri(video.Uri, ".");
        var siblings = await client.BrowseAsync(parent, cancellationToken).ConfigureAwait(false);
        var subtitles = new List<MediaResource>();
        var candidates = new List<ExternalSubtitleCandidate>();
        AddFiles(siblings, sameDirectory: true);
        foreach (var folder in siblings.Where(static resource => resource.IsDirectory)
                     .Where(resource => SubtitleFolderNames.Contains(resource.Name.TrimEnd('/')))
                     .Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AddFiles(await client.BrowseAsync(folder.Uri, cancellationToken).ConfigureAwait(false), sameDirectory: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A missing or unreadable optional subtitle folder does not prevent video playback.
            }
        }

        var videoCount = siblings.Count(static resource => !resource.IsDirectory &&
            ExternalSubtitleMatcher.IsVideoFile(resource.Name));
        var ranked = ExternalSubtitleMatcher.Rank(video.Name, candidates, preferredLanguages, mode, videoCount);
        var byUri = subtitles.ToDictionary(static resource => resource.Uri.AbsoluteUri, StringComparer.Ordinal);
        return ranked.Select(match => new MediaSubtitleMatch(byUri[match.Candidate.Source], match)).ToArray();

        void AddFiles(IReadOnlyList<MediaResource> contents, bool sameDirectory)
        {
            foreach (var resource in contents.Take(10_000))
            {
                if (resource.IsDirectory || !ExternalSubtitleMatcher.IsSubtitleFile(resource.Name)) continue;
                subtitles.Add(resource);
                candidates.Add(new ExternalSubtitleCandidate(resource.Uri.AbsoluteUri, resource.Name, sameDirectory));
            }
        }
    }

    private static IReadOnlyList<ExternalSubtitleMatch> FindLocal(
        string videoPath,
        string? preferredLanguages,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (directory is null || !Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory).Take(10_000).ToArray();
        var candidates = new List<ExternalSubtitleCandidate>();
        AddFiles(files, sameDirectory: true);
        foreach (var folder in Directory.EnumerateDirectories(directory).Where(folder =>
                     SubtitleFolderNames.Contains(Path.GetFileName(folder))).Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                {
                    AddFiles(Directory.EnumerateFiles(folder).Take(10_000), sameDirectory: false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Optional subtitle folders should not hide matches beside the video.
            }
        }

        var videoCount = files.Count(ExternalSubtitleMatcher.IsVideoFile);
        return ExternalSubtitleMatcher.Rank(
            Path.GetFileName(videoPath), candidates, preferredLanguages, videoCountInDirectory: videoCount);

        void AddFiles(IEnumerable<string> paths, bool sameDirectory)
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ExternalSubtitleMatcher.IsSubtitleFile(path))
                {
                    candidates.Add(new ExternalSubtitleCandidate(path, Path.GetFileName(path), sameDirectory));
                }
            }
        }
    }
}
