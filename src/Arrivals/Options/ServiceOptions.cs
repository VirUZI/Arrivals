namespace Arrivals.Options;

public abstract class ServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    public string WebBaseUrl =>
        Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _)
            ? PublicBaseUrl
            : BaseUrl;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class YamtrackOptions : ServiceOptions
{
    public const string SectionName = "Yamtrack";
}

public sealed class SonarrOptions : ServiceOptions
{
    public const string SectionName = "Sonarr";
}

public sealed class RadarrOptions : ServiceOptions
{
    public const string SectionName = "Radarr";
}

public sealed class JellyfinOptions : ServiceOptions
{
    public const string SectionName = "Jellyfin";

    public bool Enabled { get; set; }
    public string UserId { get; set; } = string.Empty;

    public bool IsFullyConfigured =>
        Enabled && IsConfigured && !string.IsNullOrWhiteSpace(UserId);
}
