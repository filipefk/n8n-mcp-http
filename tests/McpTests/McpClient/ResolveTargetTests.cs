using McpTests.Fakes;
using System.Net;

namespace McpTests.McpClient;

public class ResolveTargetTests
{
    [Fact]
    public async Task GetWorkflowAsync_UsesHeaderUrlAndApiKey_WhenHeadersPresent()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = TestFactories.BuildMcpClient(handler,
            url: "https://options.test", apiKey: "options-key",
            headerUrl: "https://header.test", headerApiKey: "header-key");

        await client.GetWorkflowAsync("wf1");

        var request = handler.LastRequest;
        Assert.StartsWith("https://header.test/api/v1", request.Uri.ToString());
        Assert.Equal("header-key", request.ApiKeyHeader);
    }

    [Fact]
    public async Task GetWorkflowAsync_FallsBackToOptions_WhenHeadersAbsent()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = TestFactories.BuildMcpClient(handler, url: "https://options.test", apiKey: "options-key");

        await client.GetWorkflowAsync("wf1");

        Assert.StartsWith("https://options.test/api/v1", handler.LastRequest.Uri.ToString());
    }

    [Fact]
    public async Task GetWorkflowAsync_HeaderUrlOnly_ApiKeyFallsBackToOptions()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = TestFactories.BuildMcpClient(handler,
            url: "https://options.test", apiKey: "options-key",
            headerUrl: "https://header.test", headerApiKey: null);

        await client.GetWorkflowAsync("wf1");

        Assert.StartsWith("https://header.test/api/v1", handler.LastRequest.Uri.ToString());
    }

    [Fact]
    public async Task GetWorkflowAsync_ThrowsInvalidOperationException_WhenUrlMissingEverywhere()
    {
        var handler = new FakeHttpMessageHandler();
        var client = TestFactories.BuildMcpClient(handler, url: "", apiKey: "some-key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWorkflowAsync("wf1"));
        Assert.Contains("N8n:Url", ex.Message);
    }

    [Fact]
    public async Task GetWorkflowAsync_ThrowsInvalidOperationException_WhenApiKeyMissingEverywhere()
    {
        var handler = new FakeHttpMessageHandler();
        var client = TestFactories.BuildMcpClient(handler, url: "https://options.test", apiKey: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWorkflowAsync("wf1"));
        Assert.Contains("N8n:ApiKey", ex.Message);
    }

    [Fact]
    public async Task GetWorkflowAsync_TrimsTrailingSlashFromUrl()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = TestFactories.BuildMcpClient(handler, url: "https://options.test/", apiKey: "key");

        await client.GetWorkflowAsync("wf1");

        Assert.StartsWith("https://options.test/api/v1/workflows/wf1", handler.LastRequest.Uri.ToString());
    }
}
