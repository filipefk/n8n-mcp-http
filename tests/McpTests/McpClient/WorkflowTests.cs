using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpClient;

public class WorkflowTests
{
    private static McpToolkit.McpClient BuildClient(FakeHttpMessageHandler handler) =>
        TestFactories.BuildMcpClient(handler);

    [Fact]
    public async Task SendAsync_ThrowsInvalidOperationException_OnNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.NotFound, "not found body");
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWorkflowAsync("wf1"));
        Assert.Equal("n8n API 404: not found body", ex.Message);
    }

    [Fact]
    public async Task SendAsync_ReturnsNull_OnEmptyResponseBody()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "");
        var client = BuildClient(handler);

        var result = await client.DeleteWorkflowAsync("wf1");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateWorkflowAsync_SendsFilteredPayload_OnlyNameNodesConnectionsSettings()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);
        var workflow = new JsonObject
        {
            ["id"] = "should-be-stripped",
            ["active"] = true,
            ["tags"] = new JsonArray(),
            ["name"] = "Meu workflow",
            ["nodes"] = new JsonArray(),
            ["connections"] = new JsonObject(),
            ["settings"] = new JsonObject { ["executionOrder"] = "v1" }
        };

        await client.CreateWorkflowAsync(workflow);

        var sent = JsonNode.Parse(handler.LastRequest.Body!)!.AsObject();
        Assert.Equal(4, sent.Count);
        Assert.True(sent.ContainsKey("name"));
        Assert.True(sent.ContainsKey("nodes"));
        Assert.True(sent.ContainsKey("connections"));
        Assert.True(sent.ContainsKey("settings"));
        Assert.False(sent.ContainsKey("id"));
        Assert.False(sent.ContainsKey("active"));
        Assert.False(sent.ContainsKey("tags"));
    }

    [Fact]
    public async Task CreateWorkflowAsync_DefaultsSettingsWhenAbsent()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);
        var workflow = new JsonObject { ["name"] = "Sem settings" };

        await client.CreateWorkflowAsync(workflow);

        var sent = JsonNode.Parse(handler.LastRequest.Body!)!.AsObject();
        Assert.Equal("v1", sent["settings"]!["executionOrder"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateWorkflowAsync_MergesOnlyProvidedFields_AndFiltersPayload()
    {
        var current = new JsonObject
        {
            ["name"] = "Original",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "Node1" } },
            ["connections"] = new JsonObject(),
            ["settings"] = new JsonObject { ["executionOrder"] = "v1" },
            ["extraField"] = "deve sumir"
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, current.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.UpdateWorkflowAsync("wf1", "Novo Nome", null, null, null);

        var putRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, putRequest.Method);
        var sent = JsonNode.Parse(putRequest.Body!)!.AsObject();
        Assert.Equal("Novo Nome", sent["name"]!.GetValue<string>());
        Assert.Single(sent["nodes"]!.AsArray());
        Assert.False(sent.ContainsKey("extraField"));
    }

    [Fact]
    public async Task UpdateWorkflowAsync_Throws_WhenWorkflowGetReturnsNonObject()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "null");
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.UpdateWorkflowAsync("wf1", "Novo Nome", null, null, null));
        Assert.Equal("Workflow 'wf1' não encontrado.", ex.Message);
    }

    [Fact]
    public async Task DuplicateWorkflowAsync_UsesDefaultCopyName_WhenNewNameNull()
    {
        var source = new JsonObject { ["name"] = "Foo", ["nodes"] = new JsonArray() };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, source.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.DuplicateWorkflowAsync("wf1", null);

        var sent = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("Foo (cópia)", sent["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task DuplicateWorkflowAsync_UsesProvidedNewName()
    {
        var source = new JsonObject { ["name"] = "Foo", ["nodes"] = new JsonArray() };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, source.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.DuplicateWorkflowAsync("wf1", "Nome Explícito");

        var sent = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("Nome Explícito", sent["name"]!.GetValue<string>());
    }
}
