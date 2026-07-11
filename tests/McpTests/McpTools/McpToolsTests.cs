using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpTools;

public class McpToolsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mcptests-tools-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private global::McpToolkit.McpTools BuildTools(FakeHttpMessageHandler handler)
    {
        var client = TestFactories.BuildMcpClient(handler);
        var backups = TestFactories.BuildBackupService(_tempDir);
        return new global::McpToolkit.McpTools(client, backups);
    }

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
    public async Task update_workflow_node_BacksUpFirst_AndReturnsChangeSummaryWithExistingBackupFile()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "Node1", ["parameters"] = new JsonObject() } },
            ["connections"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.update_workflow_node("wf1", "Node1", """{"disabled":true}"""))!.AsObject();

        var backupFile = result["backupFile"]!.GetValue<string>();
        Assert.True(File.Exists(backupFile));
        Assert.Contains("Node1", result["nodes"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task update_workflow_node_Throws_WhenPatchJsonInvalid()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tools.update_workflow_node("wf1", "Node1", "{invalido"));
        Assert.Contains("node_patch_json", ex.Message);
        Assert.Contains("JSON inválido", ex.Message);
    }

    [Fact]
    public async Task update_workflow_node_Throws_WhenPatchJsonIsArrayNotObject()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tools.update_workflow_node("wf1", "Node1", "[]"));
        Assert.Contains("deve ser um objeto JSON", ex.Message);
    }

    [Fact]
    public async Task create_workflow_Throws_WhenNodesJsonIsObjectNotArray()
    {
        var handler = new FakeHttpMessageHandler();
        var tools = BuildTools(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tools.create_workflow("Nome", nodes_json: "{}"));
        Assert.Contains("deve ser um array JSON", ex.Message);
        Assert.Empty(handler.Requests);
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
    public async Task delete_workflow_BacksUpBeforeDeleting_AndReturnsBackupFileInResponse()
    {
        var workflow = new JsonObject { ["name"] = "WF", ["nodes"] = new JsonArray() };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "");
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.delete_workflow("wf1"))!.AsObject();

        Assert.True(result["deleted"]!.GetValue<bool>());
        Assert.True(File.Exists(result["backupFile"]!.GetValue<string>()));
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
    }

    private static JsonObject BuildExecutionWithError() => new()
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
    public async Task list_executions_ExtractsErrorNodeAndMessage_WhenExecutionFailed()
    {
        var page = new JsonObject { ["data"] = new JsonArray { BuildExecutionWithError() }, ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, page.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.list_executions())!.AsObject();

        var item = result["data"]!.AsArray()[0]!.AsObject();
        Assert.Equal("NodeX", item["errorNode"]!.GetValue<string>());
        Assert.Equal("deu ruim", item["errorMessage"]!.GetValue<string>());
        Assert.False(item.ContainsKey("data"));
    }

    [Fact]
    public async Task list_executions_IncludesFullDataOnly_WhenIncludeDataTrue()
    {
        var page = new JsonObject { ["data"] = new JsonArray { BuildExecutionWithError() }, ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, page.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.list_executions(include_data: true))!.AsObject();

        var item = result["data"]!.AsArray()[0]!.AsObject();
        Assert.True(item.ContainsKey("data"));
    }

    [Fact]
    public async Task set_workflow_tags_by_name_ResolvesExistingAndCreatesMissingTags()
    {
        var tagsPage = new JsonObject
        {
            ["data"] = new JsonArray { new JsonObject { ["id"] = "t1", ["name"] = "TagA" } },
            ["nextCursor"] = null
        };
        var createdTag = new JsonObject { ["id"] = "t2", ["name"] = "TagB" };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, tagsPage.ToJsonString())
            .Enqueue(HttpStatusCode.OK, createdTag.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "[]");
        var tools = BuildTools(handler);

        await tools.set_workflow_tags_by_name("wf1", "TagA,TagB");

        var setRequest = handler.Requests[2];
        var sentIds = JsonNode.Parse(setRequest.Body!)!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(new List<string> { "t1", "t2" }, sentIds);
    }

    [Fact]
    public async Task set_workflow_tags_by_name_Throws_WhenMissingAndCreateMissingFalse()
    {
        var tagsPage = new JsonObject { ["data"] = new JsonArray(), ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, tagsPage.ToJsonString());
        var tools = BuildTools(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tools.set_workflow_tags_by_name("wf1", "TagA", create_missing: false));
        Assert.Contains("TagA", ex.Message);
        Assert.Contains("não encontrada", ex.Message);
    }

    [Fact]
    public async Task backup_workflow_list_backups_restore_workflow_backup_RoundTrip_WhenWorkflowStillExists()
    {
        var workflow = new JsonObject
        {
            ["id"] = "wf1",
            ["name"] = "WF",
            ["nodes"] = new JsonArray(),
            ["connections"] = new JsonObject(),
            ["settings"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var tools = BuildTools(handler);

        var backupResult = JsonNode.Parse(await tools.backup_workflow("wf1"))!.AsObject();
        var backupFile = backupResult["backupFile"]!.GetValue<string>();

        var listResult = JsonNode.Parse(tools.list_backups())!.AsArray();
        Assert.Contains(listResult, b => b!["fileName"]!.GetValue<string>() == Path.GetFileName(backupFile));

        handler.Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        handler.Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var restoreResult = JsonNode.Parse(await tools.restore_workflow_backup(Path.GetFileName(backupFile)))!.AsObject();

        Assert.Equal("wf1", restoreResult["id"]!.GetValue<string>());
        Assert.Equal(HttpMethod.Put, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task restore_workflow_backup_RecreatesWorkflow_WhenOriginalWasDeleted()
    {
        var workflow = new JsonObject
        {
            ["id"] = "wf1",
            ["name"] = "WF",
            ["nodes"] = new JsonArray(),
            ["connections"] = new JsonObject(),
            ["settings"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var tools = BuildTools(handler);
        var backupResult = JsonNode.Parse(await tools.backup_workflow("wf1"))!.AsObject();
        var backupFile = backupResult["backupFile"]!.GetValue<string>();

        handler.Enqueue(HttpStatusCode.NotFound, "workflow excluído");
        handler.Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var restoreResult = JsonNode.Parse(await tools.restore_workflow_backup(Path.GetFileName(backupFile)))!.AsObject();

        Assert.Equal(HttpMethod.Post, handler.Requests[^1].Method);
        Assert.Equal("wf1", restoreResult["id"]!.GetValue<string>());
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

        var result = JsonNode.Parse(await tools.find_in_workflows("procurado"))!.AsArray();

        Assert.Single(result);
        Assert.Equal("wf1", result[0]!["workflowId"]!.GetValue<string>());
    }
}
