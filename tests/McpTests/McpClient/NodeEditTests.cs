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
    public async Task UpdateWorkflowNodeAsync_AttachesDataLossWarning_WhenWorkflowHadStaticDataOrPinData()
    {
        // A API de update do n8n só aceita name/nodes/connections/settings — staticData e pinData
        // são descartados nesse PUT. Como não há como evitar isso client-side, o mínimo é avisar.
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "Node1", ["parameters"] = new JsonObject() } },
            ["connections"] = new JsonObject(),
            ["staticData"] = new JsonObject { ["lastPoll"] = "2026-01-01" },
            ["pinData"] = new JsonObject { ["Node1"] = new JsonArray() }
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        var result = await client.UpdateWorkflowNodeAsync("wf1", "Node1", new JsonObject { ["disabled"] = true }) as JsonObject;

        Assert.NotNull(result);
        Assert.Contains("staticData", result!["_dataLossWarning"]!.GetValue<string>());
        Assert.Contains("pinData", result["_dataLossWarning"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateWorkflowNodeAsync_NoDataLossWarning_WhenNoStaticDataOrPinData()
    {
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray { new JsonObject { ["name"] = "Node1", ["parameters"] = new JsonObject() } },
            ["connections"] = new JsonObject()
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        var result = await client.UpdateWorkflowNodeAsync("wf1", "Node1", new JsonObject { ["disabled"] = true }) as JsonObject;

        Assert.NotNull(result);
        Assert.False(result!.ContainsKey("_dataLossWarning"));
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
    public async Task DeleteWorkflowNodeAsync_ReconnectPreservesOutputBranch_WhenUpstreamIsIf()
    {
        // If --true(0)--> C ; If --false(1)--> B --> D. B é alimentado pelo ramo "false" (índice 1).
        // Bug antigo: AddConnection sempre gravava em main[0], então religar B->D jogaria D dentro
        // do ramo "true" (misturando com C) e deixaria o ramo "false" vazio, fundindo os dois ramos.
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "If" },
                new JsonObject { ["name"] = "B" },
                new JsonObject { ["name"] = "C" },
                new JsonObject { ["name"] = "D" }
            },
            ["connections"] = new JsonObject
            {
                ["If"] = new JsonObject
                {
                    ["main"] = new JsonArray
                    {
                        new JsonArray { new JsonObject { ["node"] = "C", ["type"] = "main", ["index"] = 0 } },
                        new JsonArray { new JsonObject { ["node"] = "B", ["type"] = "main", ["index"] = 0 } }
                    }
                },
                ["B"] = new JsonObject { ["main"] = new JsonArray { new JsonArray { new JsonObject { ["node"] = "D", ["type"] = "main", ["index"] = 0 } } } }
            }
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.DeleteWorkflowNodeAsync("wf1", "B", reconnect: true);

        var connections = PutPayload(handler)["connections"]!.AsObject();
        var ifOutputs = connections["If"]!["main"]!.AsArray();
        var trueBranch = ifOutputs[0]!.AsArray();
        var falseBranch = ifOutputs[1]!.AsArray();
        Assert.Single(trueBranch);
        Assert.Equal("C", trueBranch[0]!["node"]!.GetValue<string>());
        Assert.Single(falseBranch);
        Assert.Equal("D", falseBranch[0]!["node"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteWorkflowNodeAsync_DoesNotRemoveUnrelatedAiConnections()
    {
        // Model (um node LangChain ligado só via ai_languageModel, sem "main") não deve ser afetado
        // ao excluir um node totalmente não relacionado (bug antigo: qualquer entry sem "main" era
        // descartada por inteiro no cleanup de conexões vazias).
        var workflow = new JsonObject
        {
            ["name"] = "WF",
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "Model" },
                new JsonObject { ["name"] = "Agent" },
                new JsonObject { ["name"] = "Unrelated" }
            },
            ["connections"] = new JsonObject
            {
                ["Model"] = new JsonObject
                {
                    ["ai_languageModel"] = new JsonArray { new JsonArray { new JsonObject { ["node"] = "Agent", ["type"] = "ai_languageModel", ["index"] = 0 } } }
                }
            }
        };
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, workflow.ToJsonString())
            .Enqueue(HttpStatusCode.OK, "{}");
        var client = BuildClient(handler);

        await client.DeleteWorkflowNodeAsync("wf1", "Unrelated", reconnect: true);

        var connections = PutPayload(handler)["connections"]!.AsObject();
        Assert.True(connections.ContainsKey("Model"));
        Assert.Equal("Agent", connections["Model"]!["ai_languageModel"]![0]![0]!["node"]!.GetValue<string>());
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
