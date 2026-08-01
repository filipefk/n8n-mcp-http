using System.Net;

namespace McpToolkit;

public sealed class N8nApiException(HttpStatusCode statusCode, string body)
    : Exception($"n8n API {(int)statusCode}: {Truncate(body)}")
{
    private const int MaxBodyChars = 2000;

    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Body { get; } = body;

    private static string Truncate(string body) =>
        body.Length <= MaxBodyChars ? body : body[..MaxBodyChars] + "... (truncado)";
}
