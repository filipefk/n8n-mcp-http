using McpToolkit;

namespace N8nMcpHttp;

public sealed class HttpRequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    public string? GetHeader(string name) =>
        httpContextAccessor.HttpContext?.Request.Headers[name].FirstOrDefault();

    public string? GetRequestBaseUrl()
    {
        var request = httpContextAccessor.HttpContext?.Request;
        return request is null ? null : $"{request.Scheme}://{request.Host}";
    }
}
