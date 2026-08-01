using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpTools;

public class ExecutionToolsTests
{
    private static McpToolkit.ExecutionTools BuildTools(FakeHttpMessageHandler handler) =>
        new(TestFactories.BuildMcpClient(handler));

    private static JsonObject BuildExecutionSummary(string status = "error") => new()
    {
        ["id"] = "e1",
        ["status"] = status,
        ["workflowId"] = "wf1",
        ["mode"] = "manual",
        ["startedAt"] = "2026-01-01",
        ["stoppedAt"] = "2026-01-01"
    };

    private static JsonObject BuildExecutionWithErrorDetail() => new()
    {
        ["id"] = "e1",
        ["status"] = "error",
        ["workflowId"] = "wf1",
        ["mode"] = "manual",
        ["startedAt"] = "2026-01-01",
        ["stoppedAt"] = "2026-01-01",
        ["data"] = new JsonObject
        {
            ["resultData"] = new JsonObject
            {
                ["error"] = new JsonObject { ["node"] = new JsonObject { ["name"] = "NodeX" }, ["message"] = "deu ruim" }
            }
        }
    };

    [Fact]
    public async Task list_executions_FetchesDetailOnlyForErrorItems_WhenIncludeDataFalse()
    {
        var page = new JsonObject { ["data"] = new JsonArray { BuildExecutionSummary() }, ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, page.ToJsonString())
            .Enqueue(HttpStatusCode.OK, BuildExecutionWithErrorDetail().ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.list_executions())!.AsObject();

        // A primeira chamada (lista) não deve pedir includeData=true — só a segunda, pontual, para o item com erro.
        Assert.Contains("includeData=false", handler.Requests[0].Uri.Query);
        Assert.Equal(2, handler.Requests.Count);

        var item = result["data"]!.AsArray()[0]!.AsObject();
        Assert.Equal("NodeX", item["errorNode"]!.GetValue<string>());
        Assert.Equal("deu ruim", item["errorMessage"]!.GetValue<string>());
        Assert.False(item.ContainsKey("data"));
    }

    [Fact]
    public async Task list_executions_DoesNotFetchDetail_ForSuccessfulItems()
    {
        var page = new JsonObject { ["data"] = new JsonArray { BuildExecutionSummary("success") }, ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, page.ToJsonString());
        var tools = BuildTools(handler);

        await tools.list_executions();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task list_executions_IncludesFullDataOnly_WhenIncludeDataTrue()
    {
        var page = new JsonObject { ["data"] = new JsonArray { BuildExecutionWithErrorDetail() }, ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, page.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.list_executions(include_data: true))!.AsObject();

        Assert.Single(handler.Requests);
        var item = result["data"]!.AsArray()[0]!.AsObject();
        Assert.True(item.ContainsKey("data"));
    }

    [Fact]
    public async Task get_execution_FetchesDetail_WhenErrorAndIncludeDataFalse()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, BuildExecutionSummary().ToJsonString())
            .Enqueue(HttpStatusCode.OK, BuildExecutionWithErrorDetail().ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.get_execution("e1"))!.AsObject();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("NodeX", result["errorNode"]!.GetValue<string>());
        Assert.False(result.ContainsKey("data"));
    }
}
