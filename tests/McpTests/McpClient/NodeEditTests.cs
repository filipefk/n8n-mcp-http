using McpTests.Fakes;
using System.Net;
using System.Text.Json.Nodes;

namespace McpTests.McpClient;

public class NodeEditTests
{
    private static McpToolkit.McpClient BuildClient(FakeHttpMessageHandler handler) =>
        TestFactories.BuildMcpClient(handler);

    private static JsonObject PutPayload(FakeHttpMessageHandler handler) =>
        JsonNode.Parse(handler.Requests[^1].Body!)!.AsObject();

    [Fact]
    public async Task UpdateWorkflowNodeAsync_DeepMergesPatch_PreservingSiblingFields()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Node1",
                    ["parameters"] = new JsonObject { ["url"] = "https://old", ["method"] = "GET" }
                }
            },
            ["connections"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        var patch = new JsonObject { ["parameters"] = new JsonObject { ["url"] = "https://new" } };
        await client.UpdateWorkflowNodeAsync("wf1", "Node1", patch);

        var node = PutPayload(handler)["nodes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("https://new", node["parameters"]!["url"]!.GetValue<string>());
        Assert.Equal("GET", node["parameters"]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateWorkflowNodeAsync_Throws_WhenNodeNotFound()
    {
        var workflow = new JsonObject { ["name"] = "WF", ["nodes"] = new JsonArray(), ["connections"] = new JsonObject() };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.UpdateWorkflowNodeAsync("wf1", "Ausente", new JsonObject()));
        Assert.Equal("Node 'Ausente' não encontrado no workflow 'wf1'.", ex.Message);
    }

    [Fact]
    public async Task AddWorkflowNodeAsync_GeneratesIdAndPosition_WhenAbsent()
    {
        var workflow = new JsonObject { ["name"] = "WF", ["nodes"] = new JsonArray(), ["connections"] = new JsonObject() };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        var newNode = new JsonObject { ["name"] = "NovoNode", ["type"] = "n8n-nodes-base.set" };
        await client.AddWorkflowNodeAsync("wf1", newNode, null, null);

        var node = PutPayload(handler)["nodes"]!.AsArray()[0]!.AsObject();
        Assert.False(string.IsNullOrEmpty(node["id"]!.GetValue<string>()));
        Assert.Equal(2, node["position"]!.AsArray().Count);
    }

    [Fact]
    public async Task AddWorkflowNodeAsync_WiresConnectFromAndConnectTo()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "A" }, new JsonObject { ["name"] = "C" } },
            ["connections"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        var newNode = new JsonObject { ["name"] = "B", ["type"] = "n8n-nodes-base.set" };
        await client.AddWorkflowNodeAsync("wf1", newNode, "A", "C");

        var connections = PutPayload(handler)["connections"]!.AsObject();
        var fromA = connections["A"]!["main"]![0]!.AsArray();
        Assert.Equal("B", fromA[0]!["node"]!.GetValue<string>());
        var fromB = connections["B"]!["main"]![0]!.AsArray();
        Assert.Equal("C", fromB[0]!["node"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddWorkflowNodeAsync_Throws_WhenNodeNameMissing()
    {
        var workflow = new JsonObject { ["name"] = "WF", ["nodes"] = new JsonArray(), ["connections"] = new JsonObject() };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AddWorkflowNodeAsync("wf1", new JsonObject(), null, null));
        Assert.Equal("O node informado precisa ter um campo 'name'.", ex.Message);
    }

    [Fact]
    public async Task AddWorkflowNodeAsync_Throws_WhenDuplicateNodeName()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "Existente" } },
            ["connections"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AddWorkflowNodeAsync("wf1", new JsonObject { ["name"] = "Existente" }, null, null));
        Assert.Equal("Já existe um node chamado 'Existente' neste workflow.", ex.Message);
    }

    private static JsonObject BuildChainWorkflow() => new()
    {
        ["name"] = "WF",
        ["nodes"] = new JsonArray
        {
            new JsonObject { ["name"] = "A" },
            new JsonObject { ["name"] = "B" },
            new JsonObject { ["name"] = "C" }
        },
        ["connections"] = new JsonObject
        {
            ["A"] = new JsonObject { ["main"] = new JsonArray { new JsonArray { new JsonObject { ["node"] = "B", ["type"] = "main", ["index"] = 0 } } } },
            ["B"] = new JsonObject { ["main"] = new JsonArray { new JsonArray { new JsonObject { ["node"] = "C", ["type"] = "main", ["index"] = 0 } } } }
        }
    };

    [Fact]
    public async Task DeleteWorkflowNodeAsync_ReconnectsUpstreamToDownstream_WhenReconnectTrue()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, BuildChainWorkflow().ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.DeleteWorkflowNodeAsync("wf1", "B", reconnect: true);

        var payload = PutPayload(handler);
        var connections = payload["connections"]!.AsObject();
        Assert.False(connections.ContainsKey("B"));
        var fromA = connections["A"]!["main"]![0]!.AsArray();
        Assert.Equal("C", fromA[0]!["node"]!.GetValue<string>());
        Assert.DoesNotContain(payload["nodes"]!.AsArray(), n => n!["name"]!.GetValue<string>() == "B");
    }

    [Fact]
    public async Task DeleteWorkflowNodeAsync_DoesNotReconnect_WhenReconnectFalse()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, BuildChainWorkflow().ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.DeleteWorkflowNodeAsync("wf1", "B", reconnect: false);

        var connections = PutPayload(handler)["connections"]!.AsObject();
        Assert.False(connections.ContainsKey("A"));
    }

    [Fact]
    public async Task DeleteWorkflowNodeAsync_Throws_WhenNodeNotFound()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, BuildChainWorkflow().ToJsonString());
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DeleteWorkflowNodeAsync("wf1", "Ausente", true));
        Assert.Equal("Node 'Ausente' não encontrado no workflow 'wf1'.", ex.Message);
    }

    [Fact]
    public async Task RenameWorkflowNodeAsync_RenamesConnectionsAndExpressionReferences()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "Old" },
                new JsonObject
                {
                    ["name"] = "Consumer",
                    ["parameters"] = new JsonObject { ["expr"] = "={{ $('Old').item.json.value }}" }
                }
            },
            ["connections"] = new JsonObject
            {
                ["Old"] = new JsonObject { ["main"] = new JsonArray { new JsonArray { new JsonObject { ["node"] = "Consumer", ["type"] = "main", ["index"] = 0 } } } },
                ["Consumer"] = new JsonObject { ["main"] = new JsonArray { new JsonArray { new JsonObject { ["node"] = "Old", ["type"] = "main", ["index"] = 0 } } } }
            }
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.RenameWorkflowNodeAsync("wf1", "Old", "New");

        var payload = PutPayload(handler);
        var connections = payload["connections"]!.AsObject();
        Assert.False(connections.ContainsKey("Old"));
        Assert.True(connections.ContainsKey("New"));
        Assert.Equal("New", connections["Consumer"]!["main"]![0]![0]!["node"]!.GetValue<string>());

        var consumerNode = payload["nodes"]!.AsArray().First(n => n!["name"]!.GetValue<string>() == "Consumer")!.AsObject();
        Assert.Contains("$('New')", consumerNode["parameters"]!["expr"]!.GetValue<string>());
    }

    [Fact]
    public async Task RenameWorkflowNodeAsync_Throws_WhenOldNameNotFound()
    {
        var workflow = new JsonObject { ["name"] = "WF", ["nodes"] = new JsonArray(), ["connections"] = new JsonObject() };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RenameWorkflowNodeAsync("wf1", "Ausente", "Novo"));
        Assert.Equal("Node 'Ausente' não encontrado no workflow 'wf1'.", ex.Message);
    }

    [Fact]
    public async Task RenameWorkflowNodeAsync_Throws_WhenNewNameAlreadyExists()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "A" }, new JsonObject { ["name"] = "B" } },
            ["connections"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, workflow.ToJsonString());
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RenameWorkflowNodeAsync("wf1", "A", "B"));
        Assert.Equal("Já existe um node chamado 'B' neste workflow.", ex.Message);
    }
}
