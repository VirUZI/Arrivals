using Arrivals.Clients;
using Arrivals.Models;
using Arrivals.Options;
using Arrivals.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Arrivals.Pages;

public sealed class IndexModel : PageModel
{
    private readonly ArrivalsService _arrivalsService;
    private readonly YamtrackClient _yamtrack;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _timeProvider;

    public IndexModel(
        ArrivalsService arrivalsService,
        YamtrackClient yamtrack,
        IOptions<DashboardOptions> options,
        TimeProvider timeProvider)
    {
        _arrivalsService = arrivalsService;
        _yamtrack = yamtrack;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    [BindProperty(SupportsGet = true)]
    public int? PastDays { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? FutureDays { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool IncludeWatched { get; set; }
    
    [BindProperty(SupportsGet = true)]
    public bool IncludeSpecials { get; set; }

    [BindProperty(SupportsGet = true)]
    public string MediaType { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string Availability { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public bool ForceRefresh { get; set; }

    public ArrivalResult Result { get; private set; } = new()
    {
        Items = [],
        Warnings = [],
        FromDate = default,
        ToDate = default,
        GeneratedAt = default
    };

    public bool MovieMarkWatchedEnabled =>
        _options.EnableMovieMarkWatched && _yamtrack.IsConfigured;

    public bool EpisodeMarkWatchedEnabled =>
        _options.EnableEpisodeMarkWatched && _yamtrack.IsConfigured;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        PastDays ??= _options.PastDays;
        FutureDays ??= _options.FutureDays;
        Result = await _arrivalsService.GetArrivalsAsync(
            BuildQuery(),
            cancellationToken);
    }

    public async Task<IActionResult> OnPostMarkEpisodeWatchedAsync(
        string source,
        string mediaId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken)
    {
        if (!EpisodeMarkWatchedEnabled)
        {
            return BadRequest("Episode write actions are disabled.");
        }

        if (string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(mediaId) ||
            seasonNumber < 0 ||
            episodeNumber <= 0)
        {
            return BadRequest("Invalid episode identifier.");
        }

        await _yamtrack.MarkEpisodeWatchedAsync(
            source,
            mediaId,
            seasonNumber,
            episodeNumber,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        TempData["Success"] = "Marked episode as watched in Yamtrack.";

        return RedirectToPage(new
        {
            PastDays,
            FutureDays,
            IncludeWatched,
            IncludeSpecials,
            MediaType,
            Availability
        });
    }

    public async Task<IActionResult> OnPostMarkMovieWatchedAsync(
        string tmdbId,
        CancellationToken cancellationToken)
    {
        if (!MovieMarkWatchedEnabled)
        {
            return BadRequest("Movie write actions are disabled.");
        }

        if (string.IsNullOrWhiteSpace(tmdbId) ||
            !tmdbId.All(char.IsAsciiDigit))
        {
            return BadRequest("Invalid TMDB movie ID.");
        }

        await _yamtrack.MarkMovieWatchedAsync(
            tmdbId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        TempData["Success"] = "Marked as watched in Yamtrack.";

        return RedirectToPage(new
        {
            PastDays,
            FutureDays,
            IncludeWatched,
            IncludeSpecials,
            MediaType,
            Availability
        });
    }

    public string FormatDate(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _arrivalsService.TimeZone)
            .ToString("dd-MMM-yyyy");

    public string FormatTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _arrivalsService.TimeZone)
            .ToString("HH:mm");

    public string GetRowClass(ArrivalItem item)
    {
        if (item.ReleaseAt is null)
        {
            return string.Empty;
        }

        var local = TimeZoneInfo.ConvertTime(item.ReleaseAt.Value, _arrivalsService.TimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(
                _timeProvider.GetUtcNow(),
                _arrivalsService.TimeZone).DateTime);
        return date < today ? "past" : date == today ? "today" : "future";
    }

    public string FormatMovieDates(ArrivalItem item)
    {
        var parts = new List<string>();
        AddDate(parts, "Cinema", item.CinemaReleaseAt);
        AddDate(parts, "Digital", item.DigitalReleaseAt);
        AddDate(parts, "Physical", item.PhysicalReleaseAt);
        return string.Join(" · ", parts);
    }

    private void AddDate(List<string> parts, string label, DateTimeOffset? value)
    {
        if (value is not null)
        {
            parts.Add($"{label} {FormatDate(value.Value)}");
        }
    }

    private ArrivalQuery BuildQuery() => new(
        Math.Clamp(PastDays ?? _options.PastDays, 0, 3650),
        Math.Clamp(FutureDays ?? _options.FutureDays, 0, 3650),
        IncludeWatched,
        IncludeSpecials,
        MediaType,
        Availability,
        ForceRefresh);
}
