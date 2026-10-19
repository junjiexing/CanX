// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Playback;
using System.Globalization;

namespace CanX.Playback;

public sealed partial class PlaybackController
{
    private const int RememberedSubtitleLimit = 16;
    private readonly Dictionary<string, PlaybackExternalSubtitleSource> _externalSubtitleSources =
        new(SourceComparer.Instance);

    private void RememberCurrentPreferences(Func<PlaybackMediaPreferences, PlaybackMediaPreferences> update)
    {
        if (!CaptureRecent(Snapshot, completed: false)) return;
        var identity = GetHistoryIdentity(Snapshot.Source);
        var index = _recentItems.FindIndex(item => RecentMatches(item, identity));
        if (index < 0) return;
        var recent = _recentItems[index];
        var preferences = update(recent.Preferences ?? new PlaybackMediaPreferences());
        _recentItems[index] = recent with { Preferences = preferences };
        _stateDirty = true;
        Publish(Snapshot with { RecentItems = _recentItems.ToList() });
    }

    private void RememberSelectedTrack(PlaybackTrackType type, long? trackId,
        IReadOnlyList<PlaybackTrack> tracks)
    {
        PlaybackTrackPreference? selection = trackId is null
            ? new PlaybackTrackPreference { IsDisabled = true }
            : CreateTrackPreference(tracks.First(track => track.Type == type && track.Id == trackId));
        RememberCurrentPreferences(preferences => type switch
        {
            PlaybackTrackType.Video => preferences with { VideoTrack = selection },
            PlaybackTrackType.Audio => preferences with { AudioTrack = selection },
            PlaybackTrackType.Subtitle => preferences with
            {
                SubtitleTrack = selection,
                ExternalSubtitles = AddRememberedSubtitle(preferences.ExternalSubtitles,
                    selection?.ExternalSubtitle),
            },
            _ => preferences,
        });
    }

    private PlaybackTrackPreference? CreateTrackPreference(PlaybackTrack track)
    {
        PlaybackExternalSubtitleSource? external = null;
        if (track.Type == PlaybackTrackType.Subtitle && track.IsExternal)
        {
            _externalSubtitleSources.TryGetValue(track.ExternalFileName, out external);
            if (external is null && !string.IsNullOrWhiteSpace(track.ExternalFileName) &&
                ShouldPersistSource(track.ExternalFileName))
                external = new PlaybackExternalSubtitleSource { Source = track.ExternalFileName };
            if (external is null) return null;
        }
        return new PlaybackTrackPreference
        {
            Id = track.Id,
            Title = track.Title,
            Language = track.Language,
            Codec = track.Codec,
            ExternalSubtitle = external,
        };
    }

    private void RememberAddedSubtitle(string runtimeSource, bool select,
        IReadOnlyList<PlaybackTrack> tracks)
    {
        if (!_externalSubtitleSources.TryGetValue(runtimeSource, out var external)) return;
        var loaded = tracks.FirstOrDefault(track => track.Type == PlaybackTrackType.Subtitle &&
            track.IsExternal && SourceComparer.Instance.Equals(track.ExternalFileName, runtimeSource));
        RememberCurrentPreferences(preferences => preferences with
        {
            ExternalSubtitles = AddRememberedSubtitle(preferences.ExternalSubtitles, external),
            SubtitleTrack = select
                ? loaded is null
                    ? new PlaybackTrackPreference { ExternalSubtitle = external }
                    : CreateTrackPreference(loaded)
                : preferences.SubtitleTrack,
        });
    }

    private static List<PlaybackExternalSubtitleSource> AddRememberedSubtitle(
        IReadOnlyList<PlaybackExternalSubtitleSource> sources,
        PlaybackExternalSubtitleSource? source)
    {
        if (source is null) return sources.ToList();
        return sources.Where(item => !string.Equals(item.Source, source.Source, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(item.MediaSourceProfileId, source.MediaSourceProfileId, StringComparison.Ordinal))
            .Append(source).TakeLast(RememberedSubtitleLimit).ToList();
    }

    private void RestoreMediaPreferences(string source)
    {
        var identity = GetHistoryIdentity(source);
        var preferences = _recentItems.FirstOrDefault(item => RecentMatches(item, identity))?.Preferences;
        if (preferences is null) return;

        var tracks = _engine.GetTracks();
        RestoreTrack(preferences.VideoTrack, PlaybackTrackType.Video, "vid", tracks);
        RestoreTrack(preferences.AudioTrack, PlaybackTrackType.Audio, "aid", tracks);
        if (preferences.SubtitleTrack?.ExternalSubtitle is null)
            RestoreTrack(preferences.SubtitleTrack, PlaybackTrackType.Subtitle, "sid", tracks);
        RestoreSubtitleValue("sub-visibility", preferences.SubtitleVisible is { } visible
            ? visible ? "yes" : "no" : null);
        RestoreSubtitleValue("sub-delay", FormatPreference(preferences.SubtitleDelaySeconds, -3600, 3600));
        RestoreSubtitleValue("sub-font-size", FormatPreference(preferences.SubtitleFontSize, 1, 9000));
        if (preferences.SubtitleScale is not null)
        {
            if (ReadString("sub-ass-override", "scale") is "no" or "yes")
                RestoreSubtitleValue("sub-ass-override", "scale");
            if (_engine.TryGetFlag("sub-scale-signs", out _))
                RestoreSubtitleValue("sub-scale-signs", "yes");
        }
        RestoreSubtitleValue("sub-scale", FormatPreference(preferences.SubtitleScale, 0, 100));
        RestoreSubtitleValue("sub-pos", FormatPreference(preferences.SubtitlePosition, 0, 150));
    }

    private void RestoreTrack(PlaybackTrackPreference? preference, PlaybackTrackType type,
        string property, IReadOnlyList<PlaybackTrack> tracks)
    {
        if (preference is null) return;
        if (preference.IsDisabled)
        {
            RestoreSubtitleValue(property, "no");
            return;
        }

        var matching = tracks.Where(track => track.Type == type && TrackMatches(track, preference)).ToArray();
        var selected = matching.FirstOrDefault(track => track.Id == preference.Id) ??
            (matching.Length == 1 ? matching[0] : null);
        if (selected is not null)
            RestoreSubtitleValue(property, selected.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TrackMatches(PlaybackTrack track, PlaybackTrackPreference preference) =>
        (string.IsNullOrEmpty(preference.Title) || string.Equals(track.Title, preference.Title, StringComparison.Ordinal)) &&
        (string.IsNullOrEmpty(preference.Language) || string.Equals(track.Language, preference.Language, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(preference.Codec) || string.Equals(track.Codec, preference.Codec, StringComparison.OrdinalIgnoreCase));

    private void RestoreSubtitleValue(string property, string? value)
    {
        if (value is null) return;
        try { _engine.Command("set", property, value); }
        catch (Exception) { /* A removed track or unsupported option must not interrupt playback. */ }
    }

    private static string? FormatPreference(double? value, double minimum, double maximum) =>
        value is { } number && double.IsFinite(number) && number >= minimum && number <= maximum
            ? number.ToString("R", CultureInfo.InvariantCulture) : null;
}
