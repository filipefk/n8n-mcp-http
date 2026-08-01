using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpTools;

public class TagToolsTests
{
    private static McpToolkit.TagTools BuildTools(FakeHttpMessageHandler handler) =>
        new(TestFactories.BuildMcpClient(handler));

    [Fact]
    public async Task set_workflow_tags_ResolvesExistingAndCreatesMissingTags()
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

        await tools.set_workflow_tags("wf1", "TagA,TagB");

        var setRequest = handler.Requests[2];
        var sentIds = JsonNode.Parse(setRequest.Body!)!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(new List<string> { "t1", "t2" }, sentIds);
    }

    [Fact]
    public async Task set_workflow_tags_Throws_WhenMissingAndCreateMissingFalse()
    {
        var tagsPage = new JsonObject { ["data"] = new JsonArray(), ["nextCursor"] = null };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, tagsPage.ToJsonString());
        var tools = BuildTools(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tools.set_workflow_tags("wf1", "TagA", create_missing: false));
        Assert.Contains("TagA", ex.Message);
        Assert.Contains("não encontrada", ex.Message);
    }
}
