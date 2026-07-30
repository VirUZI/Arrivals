using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Arrivals.Infrastructure;
using Arrivals.Options;
using Microsoft.Extensions.Options;

namespace Arrivals.Clients;

public sealed class YamtrackClient
{
    private const int PageSize = 200;
    private readonly HttpClient _http;
    private readonly YamtrackOptions _options;
    private readonly DashboardOptions _dashboard;
    private readonly ResponseCache _cache;

    public YamtrackClient(
        HttpClient http,
        IOptions<YamtrackOptions> options,
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
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public bool IsConfigured => _options.IsConfigured;

    public Task<IReadOnlyList<YamtrackCalendarEvent>> GetCalendarAsync(
        DateOnly from,
        DateOnly to,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var cacheKey = $"arrivals:yamtrack:calendar:{from:yyyyMMdd}:{to:yyyyMMdd}";
        return _cache.GetOrCreateAsync<IReadOnlyList<YamtrackCalendarEvent>>(
            cacheKey,
            CacheLifetime,
            forceRefresh,
            async ct =>
            {
                var all = new List<YamtrackCalendarEvent>();
                for (var offset = 0; ; offset += PageSize)
                {
                    var path =
                        $"api/v1/calendar/?start_date={from:yyyy-MM-dd}" +
                        $"&end_date={to:yyyy-MM-dd}&limit={PageSize}&offset={offset}";
                    var page = await HttpJson.GetAsync<YamtrackPage<YamtrackCalendarEvent>>(
                        _http,
                        UriHelpers.Combine(_options.BaseUrl, path),
                        ct);
                    all.AddRange(page.Results);
                    if (offset + page.Results.Count >= page.Pagination.Total)
                    {
                        break;
                    }
                }

                return all;
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<int, YamtrackEpisodeState>> GetSeasonEpisodesAsync(
        string source,
        string mediaId,
        int seasonNumber,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var cacheKey = $"arrivals:yamtrack:season:{source}:{mediaId}:{seasonNumber}";
        return _cache.GetOrCreateAsync<IReadOnlyDictionary<int, YamtrackEpisodeState>>(
            cacheKey,
            CacheLifetime,
            forceRefresh,
            async ct =>
            {
                var results = new Dictionary<int, YamtrackEpisodeState>();
                for (var offset = 0; ; offset += PageSize)
                {
                    var path =
                        $"api/v1/media/tv/{Uri.EscapeDataString(source)}/" +
                        $"{Uri.EscapeDataString(mediaId)}/{seasonNumber}/episodes/" +
                        $"?limit={PageSize}&offset={offset}";
                    var page = await HttpJson.GetAsync<YamtrackPage<YamtrackEpisodeResult>>(
                        _http,
                        UriHelpers.Combine(_options.BaseUrl, path),
                        ct);

                    foreach (var episode in page.Results)
                    {
                        if (episode.Item?.EpisodeNumber is int episodeNumber)
                        {
                            results[episodeNumber] = new YamtrackEpisodeState(
                                episode.Tracked,
                                episode.Item.Title,
                                episode.EndDate,
                                episode.Status);
                        }
                    }

                    if (offset + page.Results.Count >= page.Pagination.Total)
                    {
                        break;
                    }
                }

                return results;
            },
            cancellationToken);
    }

    public Task<YamtrackMovieState> GetMovieStateAsync(
        string source,
        string mediaId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var cacheKey = $"arrivals:yamtrack:movie:{source}:{mediaId}";
        return _cache.GetOrCreateAsync(
            cacheKey,
            CacheLifetime,
            forceRefresh,
            async ct =>
            {
                var path =
                    $"api/v1/media/movie/{Uri.EscapeDataString(source)}/" +
                    $"{Uri.EscapeDataString(mediaId)}/";
                var detail = await HttpJson.GetAsync<YamtrackMediaDetail>(
                    _http,
                    UriHelpers.Combine(_options.BaseUrl, path),
                    ct);
                var watchedConsumption = detail.Consumptions
                    .Where(static consumption =>
                        consumption.Status == 3 || consumption.EndDate is not null)
                    .OrderByDescending(static consumption => consumption.EndDate)
                    .FirstOrDefault();

                return new YamtrackMovieState(
                    watchedConsumption is not null,
                    watchedConsumption?.EndDate,
                    detail.Title);
            },
            cancellationToken);
    }

    public async Task MarkEpisodeWatchedAsync(
        string source,
        string mediaId,
        int seasonNumber,
        int episodeNumber,
        DateTimeOffset watchedAt,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var path =
            $"api/v1/media/tv/{Uri.EscapeDataString(source)}/" +
            $"{Uri.EscapeDataString(mediaId)}/{seasonNumber}/{episodeNumber}/history/";
        var payload = new
        {
            end_date = watchedAt.ToString("O")
        };

        await HttpJson.PostAsync(
            _http,
            UriHelpers.Combine(_options.BaseUrl, path),
            payload,
            cancellationToken);

        _cache.Remove($"arrivals:yamtrack:season:{source}:{mediaId}:{seasonNumber}");
    }

    public async Task MarkMovieWatchedAsync(
        string tmdbId,
        DateTimeOffset watchedAt,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var path = "api/v1/media/movie/";
        var payload = new
        {
            source = "tmdb",
            media_id = tmdbId,
            status = 3,
            start_date = watchedAt.ToString("O"),
            end_date = watchedAt.ToString("O")
        };

        await HttpJson.PostAsync(
            _http,
            UriHelpers.Combine(_options.BaseUrl, path),
            payload,
            cancellationToken);

        _cache.Remove($"arrivals:yamtrack:movie:tmdb:{tmdbId}");
    }

    public string GetMovieWebUrl(string source, string mediaId, string title) =>
        UriHelpers.WebLink(
            _options.WebBaseUrl,
            $"details/{Uri.EscapeDataString(source)}/movie/" +
            $"{Uri.EscapeDataString(mediaId)}/{Uri.EscapeDataString(title)}");

    public string GetSeasonWebUrl(
        string source,
        string mediaId,
        string title,
        int seasonNumber) =>
        UriHelpers.WebLink(
            _options.WebBaseUrl,
            $"details/{Uri.EscapeDataString(source)}/tv/" +
            $"{Uri.EscapeDataString(mediaId)}/{Uri.EscapeDataString(title)}/" +
            $"season/{seasonNumber}");

    private TimeSpan CacheLifetime =>
        TimeSpan.FromMinutes(Math.Max(1, _dashboard.CacheMinutes));

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Yamtrack is not configured. Set Yamtrack:BaseUrl and Yamtrack:ApiKey.");
        }
    }

    public sealed class YamtrackPage<T>
    {
        [JsonPropertyName("pagination")]
        public YamtrackPagination Pagination { get; set; } = new();

        [JsonPropertyName("results")]
        public List<T> Results { get; set; } = [];
    }

    public sealed class YamtrackPagination
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }
    }

    public sealed class YamtrackCalendarEvent
    {
        [JsonPropertyName("datetime")]
        public DateTimeOffset DateTime { get; set; }

        [JsonPropertyName("content_number")]
        public int? ContentNumber { get; set; }

        [JsonPropertyName("item")]
        public YamtrackItem? Item { get; set; }
    }

    public sealed class YamtrackItem
    {
        [JsonPropertyName("media_id")]
        public string MediaId { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("media_type")]
        public string MediaType { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("season_number")]
        public int? SeasonNumber { get; set; }

        [JsonPropertyName("episode_number")]
        public int? EpisodeNumber { get; set; }
    }

    public sealed class YamtrackEpisodeResult
    {
        [JsonPropertyName("tracked")]
        public bool Tracked { get; set; }

        [JsonPropertyName("item")]
        public YamtrackItem? Item { get; set; }

        [JsonPropertyName("status")]
        public int? Status { get; set; }

        [JsonPropertyName("end_date")]
        public DateTimeOffset? EndDate { get; set; }
    }

    public sealed class YamtrackMediaDetail
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("consumptions")]
        public List<YamtrackConsumption> Consumptions { get; set; } = [];
    }

    public sealed class YamtrackConsumption
    {
        [JsonPropertyName("status")]
        public int? Status { get; set; }

        [JsonPropertyName("end_date")]
        public DateTimeOffset? EndDate { get; set; }
    }
}

public sealed record YamtrackEpisodeState(
    bool Watched,
    string Title,
    DateTimeOffset? WatchedAt,
    int? Status);

public sealed record YamtrackMovieState(
    bool Watched,
    DateTimeOffset? WatchedAt,
    string Title);
