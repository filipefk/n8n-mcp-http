using System.Net;
using System.Text;

namespace McpTests.Fakes;

public sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, string? ApiKeyHeader);

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, (HttpStatusCode Status, string Body)>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public CapturedRequest LastRequest => Requests[^1];

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string body)
    {
        _responses.Enqueue(_ => (status, body));
        return this;
    }

    public FakeHttpMessageHandler Enqueue(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var apiKeyHeader = request.Headers.TryGetValues("X-N8N-API-KEY", out var values) ? values.FirstOrDefault() : null;
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body, apiKeyHeader));

        if (_responses.Count == 0)
            throw new InvalidOperationException("Nenhuma resposta enfileirada no FakeHttpMessageHandler para a requisição recebida.");

        var (status, responseBody) = _responses.Dequeue()(request);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}
