using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpTools;

public class BackupToolsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mcptests-tools-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private McpToolkit.BackupTools BuildTools(FakeHttpMessageHandler handler)
    {
        var client = TestFactories.BuildMcpClient(handler);
        var backups = TestFactories.BuildBackupService(_tempDir);
        var requestContext = TestFactories.BuildRequestContext();
        var options = Microsoft.Extensions.Options.Options.Create(new McpToolkit.McpOptions { PublicBaseUrl = "https://mcp.test" });
        return new McpToolkit.BackupTools(client, backups, options, requestContext);
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

        var listResult = JsonNode.Parse(tools.list_backups())!.AsObject();
        Assert.Contains(listResult["data"]!.AsArray(), b => b!["fileName"]!.GetValue<string>() == Path.GetFileName(backupFile));

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
    public async Task backup_workflow_ReturnsBackupUrl_PointingToConfiguredPublicBaseUrl()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, new JsonObject { ["name"] = "WF" }.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.backup_workflow("wf1"))!.AsObject();

        var backupFile = result["backupFile"]!.GetValue<string>();
        Assert.Equal($"https://mcp.test/backups/{backupFile}", result["backupUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task list_backups_ReturnsBaseUrl_ForConstructingDownloadLinks()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, new JsonObject { ["name"] = "WF" }.ToJsonString());
        var tools = BuildTools(handler);
        var backupResult = JsonNode.Parse(await tools.backup_workflow("wf1"))!.AsObject();
        var backupFile = backupResult["backupFile"]!.GetValue<string>();

        var list = JsonNode.Parse(tools.list_backups())!.AsObject();

        Assert.Equal("https://mcp.test/backups/", list["baseUrl"]!.GetValue<string>());
        Assert.Contains(list["data"]!.AsArray(), b => b!["fileName"]!.GetValue<string>() == backupFile);
    }

    [Fact]
    public async Task list_backups_RespectsLimitAndReturnsNextCursor()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, new JsonObject { ["name"] = "WF1" }.ToJsonString())
            .Enqueue(HttpStatusCode.OK, new JsonObject { ["name"] = "WF2" }.ToJsonString());
        var tools = BuildTools(handler);
        await tools.backup_workflow("wf1");
        await tools.backup_workflow("wf2");

        var firstPage = JsonNode.Parse(tools.list_backups(limit: 1))!.AsObject();

        Assert.Single(firstPage["data"]!.AsArray());
        Assert.NotNull(firstPage["nextCursor"]);

        var secondPage = JsonNode.Parse(tools.list_backups(limit: 1, cursor: firstPage["nextCursor"]!.GetValue<string>()))!.AsObject();
        Assert.Single(secondPage["data"]!.AsArray());
        Assert.Null(secondPage["nextCursor"]);
    }

    [Fact]
    public async Task delete_backup_RemovesFile_AndItDisappearsFromListBackups()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, new JsonObject { ["name"] = "WF" }.ToJsonString());
        var tools = BuildTools(handler);
        var backupResult = JsonNode.Parse(await tools.backup_workflow("wf1"))!.AsObject();
        var backupFile = backupResult["backupFile"]!.GetValue<string>();

        var deleteResult = JsonNode.Parse(tools.delete_backup(backupFile))!.AsObject();

        Assert.Equal(1, deleteResult["deletedCount"]!.GetValue<int>());
        Assert.Contains(deleteResult["deleted"]!.AsArray(), n => n!.GetValue<string>() == backupFile);
        Assert.Empty(deleteResult["notFound"]!.AsArray());
        Assert.Empty(JsonNode.Parse(tools.list_backups())!["data"]!.AsArray());
    }

    [Fact]
    public async Task delete_backup_ReportsMultipleFiles_SeparatingDeletedFromNotFound()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, new JsonObject { ["name"] = "WF1" }.ToJsonString())
            .Enqueue(HttpStatusCode.OK, new JsonObject { ["name"] = "WF2" }.ToJsonString());
        var tools = BuildTools(handler);
        var file1 = JsonNode.Parse(await tools.backup_workflow("wf1"))!["backupFile"]!.GetValue<string>();
        var file2 = JsonNode.Parse(await tools.backup_workflow("wf2"))!["backupFile"]!.GetValue<string>();

        var deleteResult = JsonNode.Parse(tools.delete_backup($"{file1}, {file2}, nao-existe.json"))!.AsObject();

        Assert.Equal(2, deleteResult["deletedCount"]!.GetValue<int>());
        Assert.Equal(2, deleteResult["deleted"]!.AsArray().Count);
        var notFound = Assert.Single(deleteResult["notFound"]!.AsArray());
        Assert.Equal("nao-existe.json", notFound!.GetValue<string>());
    }

    private static JsonObject BuildDataTableWithOneColumn() => new()
    {
        ["id"] = "t1",
        ["name"] = "Tabela",
        ["columns"] = new JsonArray { new JsonObject { ["id"] = "c1", ["name"] = "col", ["type"] = "string" } }
    };

    [Fact]
    public async Task backup_data_table_restore_data_table_backup_RoundTrip_CreatesBeforeDeleting_AndReinsertsRows()
    {
        var rowsPage = new JsonObject
        {
            ["data"] = new JsonArray { new JsonObject { ["id"] = 1, ["col"] = "val", ["createdAt"] = "2026-01-01", ["updatedAt"] = "2026-01-01" } },
            ["nextCursor"] = null
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, BuildDataTableWithOneColumn().ToJsonString())
            .Enqueue(HttpStatusCode.OK, rowsPage.ToJsonString());
        var tools = BuildTools(handler);

        var backupResult = JsonNode.Parse(await tools.backup_data_table("t1"))!.AsObject();
        var backupFile = backupResult["backupFile"]!.GetValue<string>();
        Assert.Contains("Tabela", backupFile);

        // Ordem esperada após a correção: cria a tabela nova, reinsere as linhas, só então exclui a antiga.
        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["id"] = "t2", ["name"] = "Tabela" }.ToJsonString());
        handler.Enqueue(HttpStatusCode.OK, """{"count":1}""");
        handler.Enqueue(HttpStatusCode.OK, "");

        var restoreResult = JsonNode.Parse(await tools.restore_data_table_backup(backupFile))!.AsObject();

        Assert.Equal("t2", restoreResult["tableId"]!.GetValue<string>());
        Assert.Equal(1, restoreResult["rowsRestored"]!.GetValue<int>());
        Assert.Equal(1, restoreResult["columnsRestored"]!.GetValue<int>());

        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.EndsWith("data-tables", handler.Requests[2].Uri.AbsolutePath, StringComparison.Ordinal);
        var createdBody = JsonNode.Parse(handler.Requests[2].Body!)!.AsObject();
        Assert.Equal("Tabela", createdBody["name"]!.GetValue<string>());
        var sentColumns = createdBody["columns"]!.AsArray();
        Assert.Single(sentColumns);
        Assert.Equal("col", sentColumns[0]!["name"]!.GetValue<string>());
        Assert.False(sentColumns[0]!.AsObject().ContainsKey("id"));

        Assert.Equal(HttpMethod.Post, handler.Requests[3].Method);
        Assert.EndsWith("data-tables/t2/rows", handler.Requests[3].Uri.AbsolutePath, StringComparison.Ordinal);
        var insertedBody = JsonNode.Parse(handler.Requests[3].Body!)!.AsObject();
        var insertedRows = insertedBody["data"]!.AsArray();
        Assert.Single(insertedRows);
        Assert.False(insertedRows[0]!.AsObject().ContainsKey("id"));
        Assert.False(insertedRows[0]!.AsObject().ContainsKey("createdAt"));
        Assert.False(insertedRows[0]!.AsObject().ContainsKey("updatedAt"));
        Assert.Equal("val", insertedRows[0]!["col"]!.GetValue<string>());

        Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
        Assert.EndsWith("data-tables/t1", handler.Requests[4].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task restore_data_table_backup_RecreatesTable_WhenOriginalWasDeleted()
    {
        var rowsPage = new JsonObject { ["data"] = new JsonArray(), ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, BuildDataTableWithOneColumn().ToJsonString())
            .Enqueue(HttpStatusCode.OK, rowsPage.ToJsonString());
        var tools = BuildTools(handler);
        var backupResult = JsonNode.Parse(await tools.backup_data_table("t1"))!.AsObject();
        var backupFile = backupResult["backupFile"]!.GetValue<string>();

        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["id"] = "t3", ["name"] = "Tabela" }.ToJsonString());
        handler.Enqueue(HttpStatusCode.NotFound, "tabela excluída");

        var restoreResult = JsonNode.Parse(await tools.restore_data_table_backup(backupFile))!.AsObject();

        Assert.Equal("t3", restoreResult["tableId"]!.GetValue<string>());
        Assert.Equal(0, restoreResult["rowsRestored"]!.GetValue<int>());
    }
}
