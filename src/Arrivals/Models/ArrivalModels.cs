namespace Arrivals.Models;

public enum ArrivalMediaKind
{
    Movie,
    Episode
}

public readonly record struct MediaKey(
    ArrivalMediaKind Kind,
    string Source,
    string MediaId,
    int? SeasonNumber = null,
    int? EpisodeNumber = null)
{
    public static MediaKey Movie(string mediaId, string source = "tmdb") =>
        new(ArrivalMediaKind.Movie, source, mediaId);

    public static MediaKey Episode(
        string mediaId,
        int seasonNumber,
        int episodeNumber,
        string source = "tmdb") =>
        new(ArrivalMediaKind.Episode, source, mediaId, seasonNumber, episodeNumber);

    public string StableId => Kind == ArrivalMediaKind.Movie
        ? $"movie:{Source}:{MediaId}"
        : $"episode:{Source}:{MediaId}:S{SeasonNumber:D2}E{EpisodeNumber:D2}";
}

public sealed class ArrivalItem
{
    public required MediaKey Key { get; init; }
    public string Title { get; set; } = string.Empty;
    public string? EpisodeTitle { get; set; }

    public DateTimeOffset? ReleaseAt { get; set; }
    public string ReleaseLabel { get; set; } = string.Empty;
    public DateTimeOffset? YamtrackReleaseAt { get; set; }
    public DateTimeOffset? SonarrReleaseAt { get; set; }
    public DateTimeOffset? CinemaReleaseAt { get; set; }
    public DateTimeOffset? DigitalReleaseAt { get; set; }
    public DateTimeOffset? PhysicalReleaseAt { get; set; }

    public bool? Acquired { get; set; }
    public bool? InJellyfin { get; set; }
    public bool Watched { get; set; }
    public DateTimeOffset? WatchedAt { get; set; }

    public bool FromYamtrack { get; set; }
    public bool FromSonarr { get; set; }
    public bool FromRadarr { get; set; }

    public string? YamtrackUrl { get; set; }
    public string? SonarrUrl { get; set; }
    public string? RadarrUrl { get; set; }
    public string? JellyfinUrl { get; set; }

    public string EpisodeCode => Key.Kind == ArrivalMediaKind.Episode
        ? $"S{Key.SeasonNumber:D2}E{Key.EpisodeNumber:D2}"
        : string.Empty;
}

public sealed record ArrivalQuery(
    int PastDays,
    int FutureDays,
    bool IncludeWatched,
    bool IncludeSpecials,
    string MediaType,
    string Availability,
    bool ForceRefresh);

public sealed class ArrivalResult
{
    public required IReadOnlyList<ArrivalItem> Items { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required DateOnly FromDate { get; init; }
    public required DateOnly ToDate { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}
