namespace Arrivals.Infrastructure;

internal static class UriHelpers
{
    public static Uri Combine(string baseUrl, string relativePath)
    {
        if (!Uri.TryCreate(EnsureTrailingSlash(baseUrl), UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException($"Invalid service BaseUrl: '{baseUrl}'.");
        }

        return new Uri(baseUri, relativePath.TrimStart('/'));
    }

    public static string EnsureTrailingSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    public static string WebLink(string baseUrl, string relativePath) =>
        Combine(baseUrl, relativePath).ToString();
}
