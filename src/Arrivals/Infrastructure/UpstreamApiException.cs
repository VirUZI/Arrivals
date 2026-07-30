using System.Net;

namespace Arrivals.Infrastructure;

public sealed class UpstreamApiException : Exception
{
    public UpstreamApiException(Uri requestUri, HttpStatusCode statusCode, string responseBody)
        : base(BuildMessage(requestUri, statusCode, responseBody))
    {
        RequestUri = requestUri;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public Uri RequestUri { get; }
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    private static string BuildMessage(
        Uri requestUri,
        HttpStatusCode statusCode,
        string responseBody)
    {
        const int maximumBodyLength = 600;
        var body = responseBody.Length <= maximumBodyLength
            ? responseBody
            : responseBody[..maximumBodyLength] + "…";
        return $"{requestUri} returned {(int)statusCode} {statusCode}: {body}";
    }
}
