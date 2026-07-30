using System.Collections.Concurrent;
using Arrivals.Clients;
using Arrivals.Models;
using Arrivals.Options;
using Microsoft.Extensions.Options;

namespace Arrivals.Services;

public sealed class ArrivalsService
{
    private readonly YamtrackClient _yamtrack;
    private readonly SonarrClient _sonarr;
    private readonly RadarrClient _radarr;
    private readonly JellyfinClient _jellyfin;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ArrivalsService> _logger;
    private readonly TimeZoneInfo _timeZone;

    public ArrivalsService(
        YamtrackClient yamtrack,
        SonarrClient sonarr,
        RadarrClient radarr,
        JellyfinClient jellyfin,
        IOptions<DashboardOptions> options,
        TimeProvider timeProvider,
        ILogger<ArrivalsService> logger)
    {
        _yamtrack = yamtrack;
        _sonarr = sonarr;
        _radarr = radarr;
        _jellyfin = jellyfin;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeZone = ResolveTimeZone(_options.TimeZoneId);
    }

    public TimeZoneInfo TimeZone => _timeZone;

    public async Task<ArrivalResult> GetArrivalsAsync(
        ArrivalQuery query,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, _timeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var from = today.AddDays(-Math.Clamp(query.PastDays, 0, 3650));
        var to = today.AddDays(Math.Clamp(query.FutureDays, 0, 3650));
        var warnings = new ConcurrentBag<string>();

        var yamtrackTask = TryLoadAsync(
            "Yamtrack calendar",
            () => _yamtrack.GetCalendarAsync(
                from,
                to,
                query.ForceRefresh,
                cancellationToken),
            Array.Empty<YamtrackClient.YamtrackCalendarEvent>(),
            warnings);
        var sonarrTask = TryLoadAsync(
            "Sonarr calendar",
            () => _sonarr.GetCalendarAsync(
                from,
                to,
                _options.IncludeUnmonitored,
                query.ForceRefresh,
                cancellationToken),
            Array.Empty<SonarrRelease>(),
            warnings);
        var radarrTask = TryLoadAsync(
            "Radarr calendar",
            () => _radarr.GetCalendarAsync(
                from,
                to,
                _options.IncludeUnmonitored,
                query.ForceRefresh,
                cancellationToken),
            Array.Empty<RadarrRelease>(),
            warnings);

        await Task.WhenAll(yamtrackTask, sonarrTask, radarrTask);

        var items = new Dictionary<MediaKey, ArrivalItem>();
        AddYamtrackEvents(items, await yamtrackTask);
        AddSonarrEvents(items, await sonarrTask);
        AddRadarrEvents(items, await radarrTask);

        foreach (var item in items.Values)
        {
            SelectReleaseDate(item, from, to);
        }

        var candidates = items.Values
            .Where(item => item.ReleaseAt is not null)
            .Where(item => IsInDateRange(item.ReleaseAt!.Value, from, to))
            .ToList();

        await EnrichFromYamtrackAsync(
            candidates,
            query.ForceRefresh,
            warnings,
            cancellationToken);
        await EnrichFromJellyfinAsync(
            candidates,
            query.ForceRefresh,
            warnings,
            cancellationToken);

        var filtered = candidates
            .Where(item => query.IncludeWatched || !item.Watched)
            .Where(item => MatchesMediaType(item, query.MediaType))
            .Where(item => MatchesAvailability(item, query.Availability))
            .Where(item => query.IncludeSpecials || !IsSpecial(item))
            .OrderBy(static item => item.ReleaseAt)
            .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Key.SeasonNumber)
            .ThenBy(static item => item.Key.EpisodeNumber)
            .ToList();

        return new ArrivalResult
        {
            Items = filtered,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            FromDate = from,
            ToDate = to,
            GeneratedAt = now
        };
    }

    private bool IsSpecial(ArrivalItem item)
    {
        return item.Key.Kind == ArrivalMediaKind.Episode && item.Key.SeasonNumber == 0;
    }

    private void AddYamtrackEvents(
        IDictionary<MediaKey, ArrivalItem> items,
        IReadOnlyList<YamtrackClient.YamtrackCalendarEvent> events)
    {
        foreach (var release in events)
        {
            var sourceItem = release.Item;
            if (sourceItem is null || string.IsNullOrWhiteSpace(sourceItem.MediaId))
            {
                continue;
            }

            MediaKey? key = sourceItem.MediaType switch
            {
                "movie" => MediaKey.Movie(sourceItem.MediaId, sourceItem.Source),
                "episode" when sourceItem.SeasonNumber is int season &&
                               sourceItem.EpisodeNumber is int episode =>
                    MediaKey.Episode(
                        sourceItem.MediaId,
                        season,
                        episode,
                        sourceItem.Source),
                _ => null
            };
            if (key is null)
            {
                continue;
            }

            var item = GetOrAdd(items, key.Value, sourceItem.Title);
            item.FromYamtrack = true;
            item.YamtrackReleaseAt = release.DateTime;
        }
    }

    private static void AddSonarrEvents(
        IDictionary<MediaKey, ArrivalItem> items,
        IReadOnlyList<SonarrRelease> releases)
    {
        foreach (var release in releases)
        {
            var key = MediaKey.Episode(
                release.TmdbId,
                release.SeasonNumber,
                release.EpisodeNumber);
            var item = GetOrAdd(items, key, release.SeriesTitle);
            item.Title = Prefer(item.Title, release.SeriesTitle);
            item.EpisodeTitle = Prefer(item.EpisodeTitle, release.EpisodeTitle);
            item.SonarrReleaseAt = release.AirDateUtc;
            item.Acquired = release.HasFile;
            item.FromSonarr = true;
            item.SonarrUrl = release.WebUrl;
        }
    }

    private static void AddRadarrEvents(
        IDictionary<MediaKey, ArrivalItem> items,
        IReadOnlyList<RadarrRelease> releases)
    {
        foreach (var release in releases)
        {
            var key = MediaKey.Movie(release.TmdbId);
            var item = GetOrAdd(items, key, release.Title);
            item.Title = Prefer(item.Title, release.Title);
            item.CinemaReleaseAt = release.CinemaRelease;
            item.DigitalReleaseAt = release.DigitalRelease;
            item.PhysicalReleaseAt = release.PhysicalRelease;
            item.Acquired = release.HasFile;
            item.FromRadarr = true;
            item.RadarrUrl = release.WebUrl;
        }
    }

    private async Task EnrichFromYamtrackAsync(
        IReadOnlyCollection<ArrivalItem> items,
        bool forceRefresh,
        ConcurrentBag<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!_yamtrack.IsConfigured)
        {
            warnings.Add("Yamtrack is not configured; watched state is unavailable.");
            return;
        }

        var warningBag = new ConcurrentBag<string>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrentRequests)
        };

        var episodeGroups = items
            .Where(static item => item.Key.Kind == ArrivalMediaKind.Episode)
            .GroupBy(static item =>
                (item.Key.Source, item.Key.MediaId, item.Key.SeasonNumber!.Value))
            .ToList();

        await Parallel.ForEachAsync(
            episodeGroups,
            parallelOptions,
            async (group, ct) =>
            {
                try
                {
                    var states = await _yamtrack.GetSeasonEpisodesAsync(
                        group.Key.Source,
                        group.Key.MediaId,
                        group.Key.Value,
                        forceRefresh,
                        ct);
                    foreach (var item in group)
                    {
                        if (item.Key.EpisodeNumber is not int episodeNumber ||
                            !states.TryGetValue(episodeNumber, out var state))
                        {
                            continue;
                        }

                        item.Watched = state.Watched;
                        item.WatchedAt = state.WatchedAt;
                        item.EpisodeTitle = Prefer(item.EpisodeTitle, state.Title);
                        item.YamtrackUrl = _yamtrack.GetSeasonWebUrl(
                            item.Key.Source,
                            item.Key.MediaId,
                            item.Title,
                            item.Key.SeasonNumber!.Value);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to load Yamtrack season {MediaId} S{Season}",
                        group.Key.MediaId,
                        group.Key.Value);
                    warningBag.Add(
                        $"Yamtrack could not resolve {group.Key.MediaId} season " +
                        $"{group.Key.Value}; watched state may be incomplete.");
                }
            });

        var movies = items
            .Where(static item => item.Key.Kind == ArrivalMediaKind.Movie)
            .ToList();
        await Parallel.ForEachAsync(
            movies,
            parallelOptions,
            async (item, ct) =>
            {
                try
                {
                    var state = await _yamtrack.GetMovieStateAsync(
                        item.Key.Source,
                        item.Key.MediaId,
                        forceRefresh,
                        ct);
                    item.Watched = state.Watched;
                    item.WatchedAt = state.WatchedAt;
                    item.Title = Prefer(item.Title, state.Title);
                    item.YamtrackUrl = _yamtrack.GetMovieWebUrl(
                        item.Key.Source,
                        item.Key.MediaId,
                        item.Title);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to load Yamtrack movie {MediaId}",
                        item.Key.MediaId);
                    warningBag.Add(
                        $"Yamtrack could not resolve movie {item.Key.MediaId}; " +
                        "watched state may be incomplete.");
                }
            });

        foreach (var warning in warningBag)
        {
            warnings.Add(warning);
        }
    }

    private async Task EnrichFromJellyfinAsync(
        IReadOnlyCollection<ArrivalItem> items,
        bool forceRefresh,
        ConcurrentBag<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!_jellyfin.IsConfigured)
        {
            return;
        }

        try
        {
            // Refresh the shared movie/series provider index once before the
            // parallel item lookups. Individual lookups then reuse that index.
            await _jellyfin.RefreshLibraryIndexAsync(
                forceRefresh,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to index the Jellyfin library");
            warnings.Add("Jellyfin availability could not be checked.");
            return;
        }

        var warningBag = new ConcurrentBag<string>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrentRequests)
        };

        var episodeGroups = items
            .Where(static item =>
                item.Key.Kind == ArrivalMediaKind.Episode && item.Key.Source == "tmdb")
            .GroupBy(static item => item.Key.MediaId)
            .ToList();
        await Parallel.ForEachAsync(
            episodeGroups,
            parallelOptions,
            async (group, ct) =>
            {
                try
                {
                    var libraryEpisodes = await _jellyfin.GetSeriesEpisodesAsync(
                        group.Key,
                        forceRefresh,
                        ct);
                    foreach (var item in group)
                    {
                        var episodeKey = (
                            item.Key.SeasonNumber!.Value,
                            item.Key.EpisodeNumber!.Value);
                        if (libraryEpisodes.TryGetValue(episodeKey, out var match))
                        {
                            item.InJellyfin = true;
                            item.JellyfinUrl = match.WebUrl;
                        }
                        else
                        {
                            item.InJellyfin = false;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to query Jellyfin series {MediaId}",
                        group.Key);
                    warningBag.Add(
                        $"Jellyfin availability could not be checked for TV {group.Key}.");
                }
            });

        var movies = items
            .Where(static item =>
                item.Key.Kind == ArrivalMediaKind.Movie && item.Key.Source == "tmdb")
            .ToList();
        await Parallel.ForEachAsync(
            movies,
            parallelOptions,
            async (item, ct) =>
            {
                try
                {
                    var match = await _jellyfin.FindMovieAsync(
                        item.Key.MediaId,
                        false,
                        ct);
                    item.InJellyfin = match is not null;
                    item.JellyfinUrl = match?.WebUrl;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to query Jellyfin movie {MediaId}",
                        item.Key.MediaId);
                    warningBag.Add(
                        $"Jellyfin availability could not be checked for movie " +
                        $"{item.Key.MediaId}.");
                }
            });

        foreach (var warning in warningBag)
        {
            warnings.Add(warning);
        }
    }

    private void SelectReleaseDate(ArrivalItem item, DateOnly from, DateOnly to)
    {
        if (item.Key.Kind == ArrivalMediaKind.Episode)
        {
            item.ReleaseAt = item.SonarrReleaseAt ?? item.YamtrackReleaseAt;
            item.ReleaseLabel = "Airs";
            return;
        }

        var cinema = InRange(item.CinemaReleaseAt, from, to);
        var digital = InRange(item.DigitalReleaseAt, from, to);
        var physical = InRange(item.PhysicalReleaseAt, from, to);
        var yamtrack = InRange(item.YamtrackReleaseAt, from, to);

        (item.ReleaseAt, item.ReleaseLabel) = _options.MovieDatePreference switch
        {
            MovieDatePreference.Earliest => Earliest(
                (cinema, "Cinema"),
                (digital, "Digital"),
                (physical, "Physical"),
                (yamtrack, "Release")),
            MovieDatePreference.CinemaThenDigital => First(
                (cinema, "Cinema"),
                (digital, "Digital"),
                (physical, "Physical"),
                (yamtrack, "Release")),
            _ => First(
                (digital, "Digital"),
                (cinema, "Cinema"),
                (physical, "Physical"),
                (yamtrack, "Release"))
        };
    }

    private DateTimeOffset? InRange(
        DateTimeOffset? value,
        DateOnly from,
        DateOnly to) =>
        value is not null && IsInDateRange(value.Value, from, to) ? value : null;

    private bool IsInDateRange(DateTimeOffset value, DateOnly from, DateOnly to)
    {
        var local = TimeZoneInfo.ConvertTime(value, _timeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return date >= from && date <= to;
    }

    private static (DateTimeOffset? Date, string Label) First(
        params (DateTimeOffset? Date, string Label)[] candidates) =>
        candidates.FirstOrDefault(static candidate => candidate.Date is not null);

    private static (DateTimeOffset? Date, string Label) Earliest(
        params (DateTimeOffset? Date, string Label)[] candidates) =>
        candidates
            .Where(static candidate => candidate.Date is not null)
            .OrderBy(static candidate => candidate.Date)
            .FirstOrDefault();

    private static ArrivalItem GetOrAdd(
        IDictionary<MediaKey, ArrivalItem> items,
        MediaKey key,
        string title)
    {
        if (items.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var item = new ArrivalItem
        {
            Key = key,
            Title = title
        };
        items.Add(key, item);
        return item;
    }

    private static string Prefer(string? current, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) ? candidate : current ?? string.Empty;

    private static bool MatchesMediaType(ArrivalItem item, string mediaType) =>
        mediaType.ToLowerInvariant() switch
        {
            "movie" => item.Key.Kind == ArrivalMediaKind.Movie,
            "tv" or "episode" => item.Key.Kind == ArrivalMediaKind.Episode,
            _ => true
        };

    private static bool MatchesAvailability(ArrivalItem item, string availability) =>
        availability.ToLowerInvariant() switch
        {
            "acquired" => item.Acquired == true,
            "missing" => item.Acquired == false,
            _ => true
        };

    private async Task<IReadOnlyList<T>> TryLoadAsync<T>(
        string source,
        Func<Task<IReadOnlyList<T>>> load,
        IReadOnlyList<T> fallback,
        ConcurrentBag<string> warnings)
    {
        try
        {
            return await load();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to load {Source}", source);
            warnings.Add($"{source} could not be loaded: {exception.Message}");
            return fallback;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
