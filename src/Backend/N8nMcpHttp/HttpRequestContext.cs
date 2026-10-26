using McpToolkit;

namespace N8nMcpHttp;

// Decodifica o header x-api-key da requisição em curso (já validado pelo McpApiKeyMiddleware).
public sealed class HttpRequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    public string? N8nUrl => Read()?.N8nUrl;

    public string? N8nApiKey => Read()?.N8nApiKey;

    public string? GetRequestBaseUrl()
    {
        var request = httpContextAccessor.HttpContext?.Request;
        return request is null ? null : $"{request.Scheme}://{request.Host}";
    }

    private McpApiKey? Read() =>
        McpApiKey.TryRead(httpContextAccessor.HttpContext, out var apiKey) ? apiKey : null;
}
