using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpTools;

public class DataTableToolsTests
{
    private static McpToolkit.DataTableTools BuildTools(FakeHttpMessageHandler handler) =>
        new(TestFactories.BuildMcpClient(handler));

    [Fact]
    public async Task list_data_tables_PaginatesAllPages_WhenNameFilterProvided()
    {
        // Antes da correção, o filtro por nome só era aplicado à primeira página (limit=250 fixo),
        // então uma tabela que só aparecesse na segunda página nunca era encontrada.
        var page1 = new JsonObject
        {
            ["data"] = new JsonArray { new JsonObject { ["id"] = "t1", ["name"] = "Outra" } },
            ["nextCursor"] = "page2"
        };
        var page2 = new JsonObject
        {
            ["data"] = new JsonArray { new JsonObject { ["id"] = "t2", ["name"] = "Clientes" } },
            ["nextCursor"] = null
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, page1.ToJsonString())
            .Enqueue(HttpStatusCode.OK, page2.ToJsonString());
        var tools = BuildTools(handler);

        var result = JsonNode.Parse(await tools.list_data_tables(name: "cliente"))!.AsObject();

        var data = result["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal("t2", data[0]!["id"]!.GetValue<string>());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task list_data_tables_ReturnsSinglePage_WhenNoNameFilter()
    {
        var page = new JsonObject { ["data"] = new JsonArray(), ["nextCursor"] = "page2" };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, page.ToJsonString());
        var tools = BuildTools(handler);

        await tools.list_data_tables();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task update_data_table_rows_CallsUpdateEndpoint_ByDefault()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);
        var filter = new JsonObject { ["type"] = "and" };
        var data = new JsonObject { ["col"] = "x" };

        await tools.update_data_table_rows("t1", filter, data);

        Assert.EndsWith("rows/update", handler.LastRequest.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task update_data_table_rows_CallsUpsertEndpoint_WhenUpsertTrue()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{}");
        var tools = BuildTools(handler);
        var filter = new JsonObject { ["type"] = "and" };
        var data = new JsonObject { ["col"] = "x" };

        await tools.update_data_table_rows("t1", filter, data, upsert: true);

        Assert.EndsWith("rows/upsert", handler.LastRequest.Uri.AbsolutePath, StringComparison.Ordinal);
    }
}
