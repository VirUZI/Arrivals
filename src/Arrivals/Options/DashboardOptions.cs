namespace Arrivals.Options;

public enum MovieDatePreference
{
    Earliest,
    DigitalThenCinema,
    CinemaThenDigital
}

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public int PastDays { get; set; } = 30;
    public int FutureDays { get; set; } = 90;
    public int CacheMinutes { get; set; } = 10;
    public int MaxConcurrentRequests { get; set; } = 6;
    public string TimeZoneId { get; set; } = "Europe/Stockholm";
    public MovieDatePreference MovieDatePreference { get; set; } =
        MovieDatePreference.DigitalThenCinema;
    public bool IncludeUnmonitored { get; set; } = true;
    public bool EnableMovieMarkWatched { get; set; } = true;
    public bool EnableEpisodeMarkWatched { get; set; }
}
