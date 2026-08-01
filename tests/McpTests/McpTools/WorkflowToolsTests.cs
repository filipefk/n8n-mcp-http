using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpTools;

public class WorkflowToolsTests
{
    private static McpToolkit.WorkflowTools BuildTools(FakeHttpMessageHandler handler) =>
        new(TestFactories.BuildMcpClient(handler));

    [Fact]
    public async Task list_workflows_ReturnsSummarizedItems_NotFullNodesArray()
    {
        var page = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "wf1",
                    ["name"] = "Meu WF",
                    ["active"] = true,
                    ["createdAt"] = "2026-01-01",
                    ["updatedAt"] = "2026-01-02",
                    ["tags"] = new JsonArray { new JsonObject { ["name"] = "tag1" } },
                    ["nodes"] = new JsonArray { new JsonObject(), new JsonObject() }
                }
            },
            ["nextCursor"] = null
        };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, page.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.list_workflows())!.AsObject();

        var item = result["data"]!.AsArray()[0]!.AsObject();
        Assert.Equal("wf1", item["id"]!.GetValue<string>());
        Assert.Equal(2, item["nodeCount"]!.GetValue<int>());
        Assert.Equal("tag1", item["tags"]!.AsArray()[0]!.GetValue<string>());
        Assert.False(item.ContainsKey("nodes"));
    }

    private static JsonObject BuildFullWorkflow() => new()
    {
        ["id"] = "wf1",
        ["name"] = "WF",
        ["nodes"] = new JsonArray(),
        ["connections"] = new JsonObject(),
        ["pinData"] = new JsonObject { ["x"] = 1 },
        ["staticData"] = new JsonObject { ["y"] = 2 },
        ["meta"] = new JsonObject { ["z"] = 3 },
        ["shared"] = new JsonArray()
    };

    [Fact]
    public async Task get_workflow_StripsPinDataStaticDataMetaShared_ByDefault()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, BuildFullWorkflow().ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.get_workflow("wf1"))!.AsObject();

        Assert.False(result.ContainsKey("pinData"));
        Assert.False(result.ContainsKey("staticData"));
        Assert.False(result.ContainsKey("meta"));
        Assert.False(result.ContainsKey("shared"));
    }

    [Fact]
    public async Task get_workflow_RetainsExtraFields_WhenFullTrue()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, BuildFullWorkflow().ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.get_workflow("wf1", full: true))!.AsObject();

        Assert.True(result.ContainsKey("pinData"));
        Assert.True(result.ContainsKey("staticData"));
        Assert.True(result.ContainsKey("meta"));
        Assert.True(result.ContainsKey("shared"));
    }

    [Fact]
    public async Task update_workflow_node_ReturnsChangeSummary_WithoutBackupFile()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "Node1", ["parameters"] = new JsonObject() } },
            ["connections"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var tools = BuildTools(handler);

        var patch = new JsonObject { ["disabled"] = true };
        var result = JsonNode.Parse(await tools.update_workflow_node("wf1", "Node1", patch))!.AsObject();

        Assert.Contains("Node1", result["nodes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(result.ContainsKey("backupFile"));
        Assert.False(result.ContainsKey("backupUrl"));
    }

    [Fact]
    public async Task get_workflow_ResolvesWorkflowIdFromEditorUrl()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);

        await tools.get_workflow("https://n8n.example.com/workflow/abc123/executions");

        Assert.Contains("workflows/abc123", handler.LastRequest.Uri.AbsolutePath);
    }

    [Fact]
    public async Task get_workflow_ResolvesWorkflowIdFromEditorUrl_WhenNoTrailingSegment()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);

        await tools.get_workflow("https://n8n.example.com/workflow/abc123");

        Assert.Contains("workflows/abc123", handler.LastRequest.Uri.AbsolutePath);
    }

    [Fact]
    public async Task get_workflow_PassesThroughPlainId_WhenNotAUrl()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);

        await tools.get_workflow("abc123");

        Assert.Contains("workflows/abc123", handler.LastRequest.Uri.AbsolutePath);
    }

    [Fact]
    public async Task get_workflow_nodes_ReturnsSingleNode_WhenNodeNameProvided()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "Node1", ["type"] = "t1" },
                new JsonObject { ["name"] = "Node2", ["type"] = "t2" }
            }
        };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.get_workflow_nodes("wf1", node_name: "Node2"))!.AsObject();

        Assert.Equal("Node2", result["name"]!.GetValue<string>());
        Assert.Equal("t2", result["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task get_workflow_nodes_Throws_WhenNodeNameNotFound()
    {
        var workflow = new JsonObject { ["name"] = "WF", ["nodes"] = new JsonArray() };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var tools = BuildTools(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.get_workflow_nodes("wf1", node_name: "Ausente"));
    }

    [Fact]
    public async Task delete_workflow_DeletesWithoutBackingUp_AndReturnsDeletedConfirmation()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "");
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.delete_workflow("wf1"))!.AsObject();

        Assert.True(result["deleted"]!.GetValue<bool>());
        Assert.Equal("wf1", result["id"]!.GetValue<string>());
        Assert.False(result.ContainsKey("backupFile"));
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
    }

    [Fact]
    public async Task set_workflow_active_Activates_And_Deactivates()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{}")
            .Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);

        await tools.set_workflow_active("wf1", true);
        await tools.set_workflow_active("wf1", false);

        Assert.EndsWith("workflows/wf1/activate", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("workflows/wf1/deactivate", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task find_in_workflows_PaginatesAndReturnsOnlyMatchingWorkflows()
    {
        var page1 = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "wf1",
                    ["name"] = "Sem relação",
                    ["nodes"] = new JsonArray { new JsonObject { ["name"] = "N1", ["type"] = "t", ["parameters"] = new JsonObject { ["url"] = "https://procurado.example.com" } } }
                }
            },
            ["nextCursor"] = "page2"
        };
        var page2 = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject { ["id"] = "wf2", ["name"] = "Outro", ["nodes"] = new JsonArray() }
            },
            ["nextCursor"] = null
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, page1.ToJsonString())
            .Enqueue(HttpStatusCode.OK, page2.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.find_in_workflows("procurado"))!.AsObject();
        var data = result["data"]!.AsArray();

        Assert.Single(data);
        Assert.Equal("wf1", data[0]!["workflowId"]!.GetValue<string>());
        Assert.False(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task find_in_workflows_SetsTruncated_WhenMaxResultsReached()
    {
        var page = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject { ["id"] = "wf1", ["name"] = "procurado-1", ["nodes"] = new JsonArray() },
                new JsonObject { ["id"] = "wf2", ["name"] = "procurado-2", ["nodes"] = new JsonArray() },
                new JsonObject { ["id"] = "wf3", ["name"] = "procurado-3", ["nodes"] = new JsonArray() }
            },
            ["nextCursor"] = null
        };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, page.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.find_in_workflows("procurado", max_results: 2))!.AsObject();

        Assert.Equal(2, result["data"]!.AsArray().Count);
        Assert.True(result["truncated"]!.GetValue<bool>());
    }
}
