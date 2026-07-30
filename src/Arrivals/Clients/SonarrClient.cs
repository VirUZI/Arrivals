using System.Text.Json.Serialization;
using Arrivals.Infrastructure;
using Arrivals.Options;
using Microsoft.Extensions.Options;

namespace Arrivals.Clients;

public sealed class SonarrClient
{
    private readonly HttpClient _http;
    private readonly SonarrOptions _options;
    private readonly DashboardOptions _dashboard;
    private readonly ResponseCache _cache;

    public SonarrClient(
        HttpClient http,
        IOptions<SonarrOptions> options,
        IOptions<DashboardOptions> dashboard,
        ResponseCache cache)
    {
        _http = http;
        _options = options.Value;
        _dashboard = dashboard.Value;
        _cache = cache;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        }
    }

    public bool IsConfigured => _options.IsConfigured;

    public Task<IReadOnlyList<SonarrRelease>> GetCalendarAsync(
        DateOnly from,
        DateOnly to,
        bool includeUnmonitored,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Task.FromResult<IReadOnlyList<SonarrRelease>>([]);
        }

        var cacheKey =
            $"arrivals:sonarr:calendar:{from:yyyyMMdd}:{to:yyyyMMdd}:{includeUnmonitored}";
        return _cache.GetOrCreateAsync<IReadOnlyList<SonarrRelease>>(
            cacheKey,
            CacheLifetime,
            forceRefresh,
            async ct =>
            {
                var path =
                    $"api/v3/calendar?start={from:yyyy-MM-dd}&end={to.AddDays(1):yyyy-MM-dd}" +
                    $"&unmonitored={includeUnmonitored.ToString().ToLowerInvariant()}" +
                    "&includeSeries=true&includeEpisodeFile=true&includeEpisodeImages=false";
                var episodes = await HttpJson.GetAsync<List<SonarrEpisodeDto>>(
                    _http,
                    UriHelpers.Combine(_options.BaseUrl, path),
                    ct);

                Dictionary<int, SonarrSeriesDto>? seriesById = null;
                if (episodes.Any(static episode =>
                    episode.Series is null || episode.Series.TmdbId <= 0))
                {
                    seriesById = (await GetSeriesAsync(forceRefresh, ct))
                        .ToDictionary(static series => series.Id);
                }

                return episodes
                    .Select(episode =>
                    {
                        var series = episode.Series;
                        if ((series is null || series.TmdbId <= 0) &&
                            seriesById is not null)
                        {
                            seriesById.TryGetValue(episode.SeriesId, out series);
                        }

                        return series is null || series.TmdbId <= 0
                            ? null
                            : new SonarrRelease(
                                series.TmdbId.ToString(),
                                series.Title,
                                episode.Title,
                                episode.SeasonNumber,
                                episode.EpisodeNumber,
                                episode.AirDateUtc,
                                episode.HasFile || episode.EpisodeFileId > 0,
                                episode.Monitored,
                                string.IsNullOrWhiteSpace(series.TitleSlug)
                                    ? null
                                    : UriHelpers.WebLink(
                                        _options.WebBaseUrl,
                                        $"series/{Uri.EscapeDataString(series.TitleSlug)}"));
                    })
                    .Where(static release => release is not null)
                    .Cast<SonarrRelease>()
                    .ToList();
            },
            cancellationToken);
    }

    private Task<IReadOnlyList<SonarrSeriesDto>> GetSeriesAsync(
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync<IReadOnlyList<SonarrSeriesDto>>(
            "arrivals:sonarr:series",
            TimeSpan.FromMinutes(Math.Max(10, _dashboard.CacheMinutes * 3)),
            forceRefresh,
            async ct => await HttpJson.GetAsync<List<SonarrSeriesDto>>(
                _http,
                UriHelpers.Combine(_options.BaseUrl, "api/v3/series"),
                ct),
            cancellationToken);

    private TimeSpan CacheLifetime =>
        TimeSpan.FromMinutes(Math.Max(1, _dashboard.CacheMinutes));

    private sealed class SonarrEpisodeDto
    {
        [JsonPropertyName("seriesId")]
        public int SeriesId { get; set; }

        [JsonPropertyName("seasonNumber")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("episodeNumber")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("airDateUtc")]
        public DateTimeOffset? AirDateUtc { get; set; }

        [JsonPropertyName("hasFile")]
        public bool HasFile { get; set; }

        [JsonPropertyName("episodeFileId")]
        public int EpisodeFileId { get; set; }

        [JsonPropertyName("monitored")]
        public bool Monitored { get; set; }

        [JsonPropertyName("series")]
        public SonarrSeriesDto? Series { get; set; }
    }

    private sealed class SonarrSeriesDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("tmdbId")]
        public int TmdbId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("titleSlug")]
        public string TitleSlug { get; set; } = string.Empty;
    }
}

public sealed record SonarrRelease(
    string TmdbId,
    string SeriesTitle,
    string EpisodeTitle,
    int SeasonNumber,
    int EpisodeNumber,
    DateTimeOffset? AirDateUtc,
    bool HasFile,
    bool Monitored,
    string? WebUrl);
