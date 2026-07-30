using System.Net.Http.Headers;
using System.Text.Json;

namespace Arrivals.Infrastructure;

internal static class HttpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<T> GetAsync<T>(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        return await ReadAsync<T>(response, uri, cancellationToken);
    }

    public static async Task<T> PostAsync<TRequest, T>(
        HttpClient client,
        Uri uri,
        TRequest body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = CreateJsonContent(body)
        };
        using var response = await client.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, uri, cancellationToken);
    }

    public static async Task PostAsync<TRequest>(
        HttpClient client,
        Uri uri,
        TRequest body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = CreateJsonContent(body)
        };
        using var response = await client.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new UpstreamApiException(uri, response.StatusCode, text);
    }

    private static HttpContent CreateJsonContent<TRequest>(TRequest body)
    {
        // Serialize eagerly so Content-Length is sent. This is more reliable with
        // Django's development server than chunked request bodies from JsonContent.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Options);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpstreamApiException(uri, response.StatusCode, text);
        }

        var value = JsonSerializer.Deserialize<T>(text, Options);
        return value ?? throw new InvalidOperationException(
            $"The service returned an empty or invalid JSON response for {uri}.");
    }
}
