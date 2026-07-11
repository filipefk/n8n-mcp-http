using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpClient;

public class CrudSampleTests
{
    private static McpToolkit.McpClient BuildClient(FakeHttpMessageHandler handler) =>
        TestFactories.BuildMcpClient(handler);

    [Fact]
    public async Task ListWorkflowsAsync_BuildsQueryString_OmittingNullOrEmptyParams()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.ListWorkflowsAsync(name: null, active: true, limit: 50, cursor: null);

        var query = handler.LastRequest.Uri.Query;
        Assert.Contains("limit=50", query);
        Assert.Contains("active=true", query);
        Assert.Contains("excludePinnedData=true", query);
        Assert.DoesNotContain("name=", query);
        Assert.DoesNotContain("cursor=", query);
    }

    [Fact]
    public async Task ListWorkflowsAsync_LowercasesBooleanQueryValues()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.ListWorkflowsAsync(name: null, active: true, limit: 10, cursor: null);

        Assert.DoesNotContain("True", handler.LastRequest.Uri.Query);
    }

    [Fact]
    public async Task ListDataTablesAsync_UsesFixedLimit250()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.ListDataTablesAsync(cursor: null);

        Assert.Contains("limit=250", handler.LastRequest.Uri.Query);
    }

    [Fact]
    public async Task CreateDataTableAsync_OmitsProjectId_WhenNull()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.CreateDataTableAsync("Tabela", new JsonArray(), projectId: null);

        var sent = JsonNode.Parse(handler.LastRequest.Body!)!.AsObject();
        Assert.False(sent.ContainsKey("projectId"));
    }

    [Fact]
    public async Task CreateDataTableAsync_IncludesProjectId_WhenProvided()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.CreateDataTableAsync("Tabela", new JsonArray(), projectId: "proj1");

        var sent = JsonNode.Parse(handler.LastRequest.Body!)!.AsObject();
        Assert.Equal("proj1", sent["projectId"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteDataTableRowsAsync_EncodesFilterAsQueryStringJson()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);
        var filter = new JsonObject { ["type"] = "and" };

        await client.DeleteDataTableRowsAsync("t1", filter, returnData: true);

        var query = handler.LastRequest.Uri.Query;
        Assert.Contains(Uri.EscapeDataString(filter.ToJsonString()), query);
        Assert.Contains("returnData=true", query);
    }

    [Fact]
    public async Task SetWorkflowTagsAsync_SendsArrayOfIdObjects()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.SetWorkflowTagsAsync("wf1", ["t1", "t2"]);

        var sent = JsonNode.Parse(handler.LastRequest.Body!)!.AsArray();
        Assert.Equal("t1", sent[0]!["id"]!.GetValue<string>());
        Assert.Equal("t2", sent[1]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ActivateWorkflowAsync_And_DeactivateWorkflowAsync_HitExpectedPathsWithoutBody()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{}")
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.ActivateWorkflowAsync("wf1");
        await client.DeactivateWorkflowAsync("wf1");

        Assert.EndsWith("workflows/wf1/activate", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Null(handler.Requests[0].Body);
        Assert.EndsWith("workflows/wf1/deactivate", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetExecutionAsync_And_ListExecutionsAsync_BuildExpectedQuery()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{}")
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.GetExecutionAsync("exec1", includeData: true);
        await client.ListExecutionsAsync(workflowId: "wf1", status: "error", includeData: false, limit: 20, cursor: null);

        Assert.Contains("includeData=true", handler.Requests[0].Uri.Query);
        var listQuery = handler.Requests[1].Uri.Query;
        Assert.Contains("workflowId=wf1", listQuery);
        Assert.Contains("status=error", listQuery);
        Assert.Contains("includeData=false", listQuery);
        Assert.DoesNotContain("cursor=", listQuery);
    }
}
