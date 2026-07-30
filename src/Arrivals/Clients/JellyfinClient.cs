using System.Text.Json.Serialization;
using Arrivals.Infrastructure;
using Arrivals.Options;
using Microsoft.Extensions.Options;

namespace Arrivals.Clients;

public sealed class JellyfinClient
{
    private const int PageSize = 1000;
    private const string LibraryIndexCacheKey = "arrivals:jellyfin:provider-index";
    private readonly HttpClient _http;
    private readonly JellyfinOptions _options;
    private readonly DashboardOptions _dashboard;
    private readonly ResponseCache _cache;

    public JellyfinClient(
        HttpClient http,
        IOptions<JellyfinOptions> options,
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
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", _options.ApiKey);
        }
    }

    public bool IsConfigured => _options.IsFullyConfigured;

    public async Task RefreshLibraryIndexAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        _ = await GetLibraryIndexAsync(forceRefresh, cancellationToken);
    }

    public async Task<JellyfinMatch?> FindMovieAsync(
        string tmdbId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var index = await GetLibraryIndexAsync(forceRefresh, cancellationToken);
        return index.MoviesByTmdbId.TryGetValue(tmdbId, out var movie)
            ? new JellyfinMatch(movie.Id, movie.Name, GetWebUrl(movie.Id))
            : null;
    }

    public Task<IReadOnlyDictionary<(int Season, int Episode), JellyfinMatch>>
        GetSeriesEpisodesAsync(
            string tmdbSeriesId,
            bool forceRefresh,
            CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Task.FromResult<
                IReadOnlyDictionary<(int Season, int Episode), JellyfinMatch>>(
                new Dictionary<(int Season, int Episode), JellyfinMatch>());
        }

        var key = $"arrivals:jellyfin:series-episodes:{tmdbSeriesId}";
        return _cache.GetOrCreateAsync<
            IReadOnlyDictionary<(int Season, int Episode), JellyfinMatch>>(
            key,
            CacheLifetime,
            forceRefresh,
            async ct =>
            {
                // The shared provider index is refreshed once by ArrivalsService before
                // the parallel per-series lookups. Do not force-refresh it for every
                // series, otherwise a dashboard refresh would repeatedly rebuild it.
                var index = await GetLibraryIndexAsync(false, ct);
                if (!index.SeriesByTmdbId.TryGetValue(tmdbSeriesId, out var series))
                {
                    return new Dictionary<(int Season, int Episode), JellyfinMatch>();
                }

                var all = new List<JellyfinItem>();
                for (var startIndex = 0; ; startIndex += PageSize)
                {
                    var episodePath =
                        $"Shows/{Uri.EscapeDataString(series.Id)}/Episodes" +
                        $"?UserId={Uri.EscapeDataString(_options.UserId)}" +
                        "&IsMissing=false" +
                        $"&StartIndex={startIndex}&Limit={PageSize}";
                    var page = await HttpJson.GetAsync<JellyfinItemsResponse>(
                        _http,
                        UriHelpers.Combine(_options.BaseUrl, episodePath),
                        ct);
                    all.AddRange(page.Items);
                    if (startIndex + page.Items.Count >= page.TotalRecordCount)
                    {
                        break;
                    }
                }

                return all
                    .Where(static item =>
                        item.ParentIndexNumber is not null && item.IndexNumber is not null)
                    .GroupBy(static item =>
                        (item.ParentIndexNumber!.Value, item.IndexNumber!.Value))
                    .ToDictionary(
                        static group => group.Key,
                        group =>
                        {
                            var item = group.First();
                            return new JellyfinMatch(
                                item.Id,
                                item.Name,
                                GetWebUrl(item.Id));
                        });
            },
            cancellationToken);
    }

    private Task<JellyfinLibraryIndex> GetLibraryIndexAsync(
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync(
            LibraryIndexCacheKey,
            CacheLifetime,
            forceRefresh,
            LoadLibraryIndexAsync,
            cancellationToken);

    private async Task<JellyfinLibraryIndex> LoadLibraryIndexAsync(
        CancellationToken cancellationToken)
    {
        var all = new List<JellyfinItem>();
        for (var startIndex = 0; ; startIndex += PageSize)
        {
            var path =
                "Items?Recursive=true&IncludeItemTypes=Movie,Series" +
                "&Fields=ProviderIds&IsMissing=false" +
                $"&UserId={Uri.EscapeDataString(_options.UserId)}" +
                $"&StartIndex={startIndex}&Limit={PageSize}";
            var page = await HttpJson.GetAsync<JellyfinItemsResponse>(
                _http,
                UriHelpers.Combine(_options.BaseUrl, path),
                cancellationToken);
            all.AddRange(page.Items);
            if (startIndex + page.Items.Count >= page.TotalRecordCount)
            {
                break;
            }
        }

        var moviesByTmdbId = new Dictionary<string, JellyfinItem>(
            StringComparer.OrdinalIgnoreCase);
        var seriesByTmdbId = new Dictionary<string, JellyfinItem>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in all)
        {
            var tmdbId = GetProviderId(item, "Tmdb");
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                continue;
            }

            if (string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase))
            {
                moviesByTmdbId.TryAdd(tmdbId, item);
            }
            else if (string.Equals(
                         item.Type,
                         "Series",
                         StringComparison.OrdinalIgnoreCase))
            {
                seriesByTmdbId.TryAdd(tmdbId, item);
            }
        }

        return new JellyfinLibraryIndex(moviesByTmdbId, seriesByTmdbId);
    }

    private static string? GetProviderId(JellyfinItem item, string provider) =>
        item.ProviderIds
            .FirstOrDefault(pair =>
                string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase))
            .Value?
            .Trim();

    private string GetWebUrl(string itemId) =>
        UriHelpers.WebLink(
            _options.WebBaseUrl,
            $"web/#/details?id={Uri.EscapeDataString(itemId)}");

    private TimeSpan CacheLifetime =>
        TimeSpan.FromMinutes(Math.Max(2, _dashboard.CacheMinutes));

    private sealed record JellyfinLibraryIndex(
        IReadOnlyDictionary<string, JellyfinItem> MoviesByTmdbId,
        IReadOnlyDictionary<string, JellyfinItem> SeriesByTmdbId);

    private sealed class JellyfinItemsResponse
    {
        [JsonPropertyName("Items")]
        public List<JellyfinItem> Items { get; set; } = [];

        [JsonPropertyName("TotalRecordCount")]
        public int TotalRecordCount { get; set; }
    }

    private sealed class JellyfinItem
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("ProviderIds")]
        public Dictionary<string, string?> ProviderIds { get; set; } = [];

        [JsonPropertyName("ParentIndexNumber")]
        public int? ParentIndexNumber { get; set; }

        [JsonPropertyName("IndexNumber")]
        public int? IndexNumber { get; set; }
    }
}

public sealed record JellyfinMatch(string ItemId, string Title, string WebUrl);
