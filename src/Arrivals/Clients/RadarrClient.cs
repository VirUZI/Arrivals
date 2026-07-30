using System.Text.Json.Serialization;
using Arrivals.Infrastructure;
using Arrivals.Options;
using Microsoft.Extensions.Options;

namespace Arrivals.Clients;

public sealed class RadarrClient
{
    private readonly HttpClient _http;
    private readonly RadarrOptions _options;
    private readonly DashboardOptions _dashboard;
    private readonly ResponseCache _cache;

    public RadarrClient(
        HttpClient http,
        IOptions<RadarrOptions> options,
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

    public Task<IReadOnlyList<RadarrRelease>> GetCalendarAsync(
        DateOnly from,
        DateOnly to,
        bool includeUnmonitored,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Task.FromResult<IReadOnlyList<RadarrRelease>>([]);
        }

        var cacheKey =
            $"arrivals:radarr:calendar:{from:yyyyMMdd}:{to:yyyyMMdd}:{includeUnmonitored}";
        return _cache.GetOrCreateAsync<IReadOnlyList<RadarrRelease>>(
            cacheKey,
            TimeSpan.FromMinutes(Math.Max(1, _dashboard.CacheMinutes)),
            forceRefresh,
            async ct =>
            {
                var path =
                    $"api/v3/calendar?start={from:yyyy-MM-dd}&end={to.AddDays(1):yyyy-MM-dd}" +
                    $"&unmonitored={includeUnmonitored.ToString().ToLowerInvariant()}" +
                    "&includeMovieFile=true";
                var movies = await HttpJson.GetAsync<List<RadarrMovieDto>>(
                    _http,
                    UriHelpers.Combine(_options.BaseUrl, path),
                    ct);

                return movies
                    .Where(static movie => movie.TmdbId > 0)
                    .Select(movie => new RadarrRelease(
                        movie.TmdbId.ToString(),
                        movie.Title,
                        movie.InCinemas,
                        movie.DigitalRelease,
                        movie.PhysicalRelease,
                        movie.HasFile || movie.MovieFileId > 0,
                        movie.Monitored,
                        string.IsNullOrWhiteSpace(movie.TitleSlug)
                            ? null
                            : UriHelpers.WebLink(
                                _options.WebBaseUrl,
                                $"movie/{Uri.EscapeDataString(movie.TitleSlug)}")))
                    .ToList();
            },
            cancellationToken);
    }

    private sealed class RadarrMovieDto
    {
        [JsonPropertyName("tmdbId")]
        public int TmdbId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("titleSlug")]
        public string TitleSlug { get; set; } = string.Empty;

        [JsonPropertyName("inCinemas")]
        public DateTimeOffset? InCinemas { get; set; }

        [JsonPropertyName("digitalRelease")]
        public DateTimeOffset? DigitalRelease { get; set; }

        [JsonPropertyName("physicalRelease")]
        public DateTimeOffset? PhysicalRelease { get; set; }

        [JsonPropertyName("hasFile")]
        public bool HasFile { get; set; }

        [JsonPropertyName("movieFileId")]
        public int MovieFileId { get; set; }

        [JsonPropertyName("monitored")]
        public bool Monitored { get; set; }
    }
}

public sealed record RadarrRelease(
    string TmdbId,
    string Title,
    DateTimeOffset? CinemaRelease,
    DateTimeOffset? DigitalRelease,
    DateTimeOffset? PhysicalRelease,
    bool HasFile,
    bool Monitored,
    string? WebUrl);
