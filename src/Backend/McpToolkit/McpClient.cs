using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json.Nodes;

namespace McpToolkit;

public sealed class McpClient(HttpClient http, IOptions<McpOptions> options, IRequestContext requestContext)
{
    private readonly McpOptions _opts = options.Value;

    private (string ApiBase, string ApiKey) ResolveTarget()
    {
        var url = requestContext.GetHeader("X-N8N-URL");
        if (string.IsNullOrWhiteSpace(url)) url = _opts.Url;

        var apiKey = requestContext.GetHeader("X-N8N-API-KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = _opts.ApiKey;

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("URL do n8n não informada (header X-N8N-URL ou appsettings N8n:Url).");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key do n8n não informada (header X-N8N-API-KEY ou appsettings N8n:ApiKey).");

        return ($"{url.TrimEnd('/')}/api/v1", apiKey);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body = null, CancellationToken ct = default)
    {
        var (apiBase, apiKey) = ResolveTarget();
        using var request = new HttpRequestMessage(method, new Uri($"{apiBase}/{pathAndQuery}"));
        request.Headers.Add("X-N8N-API-KEY", apiKey);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new N8nApiException(response.StatusCode, content);

        return string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);
    }

    private static string BuildQuery(params (string Key, string? Value)[] parameters)
    {
        var parts = parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}");
        var query = string.Join("&", parts);
        return query.Length == 0 ? string.Empty : $"?{query}";
    }

    private static readonly string[] AllowedSettingsKeys =
    [
        "saveExecutionProgress", "saveManualExecutions", "saveDataErrorExecution",
        "saveDataSuccessExecution", "executionTimeout", "errorWorkflow", "timezone",
        "executionOrder", "callerPolicy", "callerIds"
    ];

    private static JsonObject FilterWorkflowPayload(JsonObject source, string? overrideName = null)
    {
        var name = overrideName ?? source["name"]?.GetValue<string>() ?? string.Empty;

        JsonObject settings;
        if (source["settings"] is JsonObject settingsSource)
        {
            settings = new JsonObject();
            foreach (var key in AllowedSettingsKeys)
                if (settingsSource[key] is JsonNode value)
                    settings[key] = value.DeepClone();
        }
        else
        {
            settings = new JsonObject { ["executionOrder"] = "v1" };
        }

        return new JsonObject
        {
            ["name"] = name,
            ["nodes"] = source["nodes"]?.DeepClone() ?? new JsonArray(),
            ["connections"] = source["connections"]?.DeepClone() ?? new JsonObject(),
            ["settings"] = settings
        };
    }

    // A API de update do n8n só aceita name/nodes/connections/settings — staticData e pinData
    // não podem ser reenviados e são perdidos por esta operação. Não há como evitar isso client-side;
    // o melhor que dá pra fazer é avisar quando o workflow tinha esses campos antes da edição.
    private static void AttachDataLossWarningIfNeeded(JsonObject current, JsonNode? result)
    {
        var lost = new List<string>();
        if (current["staticData"] is JsonObject { Count: > 0 }) lost.Add("staticData");
        if (current["pinData"] is JsonObject { Count: > 0 }) lost.Add("pinData");

        if (lost.Count > 0 && result is JsonObject resultObj)
            resultObj["_dataLossWarning"] =
                $"O workflow tinha {string.Join(" e ", lost)} antes desta edição. A API do n8n não aceita esses campos no payload de atualização (apenas name/nodes/connections/settings), então podem ter sido perdidos.";
    }

    // ---------- Workflows ----------

    public Task<JsonNode?> ListWorkflowsAsync(string? name, bool? active, int limit, string? cursor, bool excludePinnedData = true, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("limit", limit.ToString()),
            ("name", name),
            ("active", active?.ToString().ToLowerInvariant()),
            ("excludePinnedData", excludePinnedData.ToString().ToLowerInvariant()),
            ("cursor", cursor));
        return SendAsync(HttpMethod.Get, $"workflows{query}", ct: ct);
    }

    public Task<JsonNode?> GetWorkflowAsync(string workflowId, bool excludePinnedData = false, CancellationToken ct = default)
    {
        var query = BuildQuery(("excludePinnedData", excludePinnedData.ToString().ToLowerInvariant()));
        return SendAsync(HttpMethod.Get, $"workflows/{workflowId}{query}", ct: ct);
    }

    public Task<JsonNode?> CreateWorkflowAsync(JsonObject workflow, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "workflows", FilterWorkflowPayload(workflow), ct);

    public async Task<JsonNode?> UpdateWorkflowAsync(string workflowId, string? newName, JsonArray? nodes, JsonObject? connections, JsonObject? settings, CancellationToken ct = default)
    {
        var current = await SendAsync(HttpMethod.Get, $"workflows/{workflowId}", ct: ct) as JsonObject
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' não encontrado.");

        if (newName is not null) current["name"] = newName;
        if (nodes is not null) current["nodes"] = nodes;
        if (connections is not null) current["connections"] = connections;
        if (settings is not null) current["settings"] = settings;

        var result = await SendAsync(HttpMethod.Put, $"workflows/{workflowId}", FilterWorkflowPayload(current), ct);
        AttachDataLossWarningIfNeeded(current, result);
        return result;
    }

    public async Task<JsonNode?> DuplicateWorkflowAsync(string workflowId, string? newName, CancellationToken ct = default)
    {
        var source = await SendAsync(HttpMethod.Get, $"workflows/{workflowId}", ct: ct) as JsonObject
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' não encontrado.");

        var sourceName = source["name"]?.GetValue<string>() ?? workflowId;
        var payload = FilterWorkflowPayload(source, newName ?? $"{sourceName} (cópia)");
        return await SendAsync(HttpMethod.Post, "workflows", payload, ct);
    }

    public Task<JsonNode?> DeleteWorkflowAsync(string workflowId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"workflows/{workflowId}", ct: ct);

    public Task<JsonNode?> ActivateWorkflowAsync(string workflowId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"workflows/{workflowId}/activate", ct: ct);

    public Task<JsonNode?> DeactivateWorkflowAsync(string workflowId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"workflows/{workflowId}/deactivate", ct: ct);

    public Task<JsonNode?> SetWorkflowActiveAsync(string workflowId, bool active, CancellationToken ct = default) =>
        active ? ActivateWorkflowAsync(workflowId, ct) : DeactivateWorkflowAsync(workflowId, ct);

    // ---------- Edição granular de nodes ----------

    private static void DeepMergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject patchObj && target[key] is JsonObject targetObj)
                DeepMergePatch(targetObj, patchObj);
            else
                target[key] = value?.DeepClone();
        }
    }

    // Escreve na saída "main" de índice fromOutputIndex (índice de entrada toInputIndex no destino).
    // Preservar o índice de saída importa quando fromNode é um IF/Switch: religar sempre em main[0]
    // fundiria os ramos "true"/"false" (ou os cases de um Switch) em um só.
    private static void AddConnection(JsonObject connections, string fromNode, int fromOutputIndex, string toNode, int toInputIndex = 0)
    {
        if (connections[fromNode] is not JsonObject fromEntry)
        {
            fromEntry = new JsonObject();
            connections[fromNode] = fromEntry;
        }

        if (fromEntry["main"] is not JsonArray outputs)
        {
            outputs = new JsonArray();
            fromEntry["main"] = outputs;
        }

        while (outputs.Count <= fromOutputIndex)
            outputs.Add(new JsonArray());
        if (outputs[fromOutputIndex] is not JsonArray branch)
        {
            branch = new JsonArray();
            outputs[fromOutputIndex] = branch;
        }

        branch.Add(new JsonObject { ["node"] = toNode, ["type"] = "main", ["index"] = toInputIndex });
    }

    private static void AddConnection(JsonObject connections, string fromNode, string toNode) =>
        AddConnection(connections, fromNode, 0, toNode, 0);

    // Retorna, para cada conexão que aponta para nodeName, o node de origem e o índice
    // da saída "main" pela qual ele alimentava nodeName (necessário para religar preservando o ramo).
    private static List<(string Node, int OutputIndex)> FindUpstreamConnections(JsonObject connections, string nodeName)
    {
        var result = new List<(string, int)>();
        foreach (var (source, entry) in connections)
        {
            if (entry is not JsonObject entryObj || entryObj["main"] is not JsonArray outputs) continue;
            for (var idx = 0; idx < outputs.Count; idx++)
                if (outputs[idx] is JsonArray targets && targets.Any(t => t?["node"]?.GetValue<string>() == nodeName))
                    result.Add((source, idx));
        }
        return result;
    }

    // Retorna os destinos das saídas "main" do próprio nodeName, com o índice de entrada original de cada um.
    private static List<(string Node, int InputIndex)> FindDownstreamConnections(JsonObject connections, string nodeName)
    {
        var result = new List<(string, int)>();
        if (connections[nodeName] is not JsonObject entry || entry["main"] is not JsonArray outputs)
            return result;

        foreach (var targets in outputs.OfType<JsonArray>())
            foreach (var target in targets.OfType<JsonObject>())
                if (target["node"]?.GetValue<string>() is string name)
                    result.Add((name, target["index"]?.GetValue<int>() ?? 0));

        return result;
    }

    // Remove toda referência a nodeName em qualquer tipo de conexão (main, ai_languageModel, ai_tool, ai_memory, ...),
    // não só "main" — um node ligado só por uma conexão de IA (comum em nodes LangChain) não tem "main" nenhum,
    // e a versão anterior descartava a entrada inteira nesse caso, apagando também as demais conexões de IA do source.
    private static void RemoveConnectionsTo(JsonObject connections, string nodeName)
    {
        foreach (var (_, entry) in connections)
        {
            if (entry is not JsonObject entryObj) continue;
            foreach (var (_, typeValue) in entryObj)
            {
                if (typeValue is not JsonArray outputs) continue;
                foreach (var targets in outputs.OfType<JsonArray>())
                    for (var i = targets.Count - 1; i >= 0; i--)
                        if (targets[i]?["node"]?.GetValue<string>() == nodeName)
                            targets.RemoveAt(i);
            }
        }

        var emptyKeys = connections
            .Where(kv => kv.Value is JsonObject entryObj
                && entryObj.All(p => p.Value is not JsonArray outputs || outputs.OfType<JsonArray>().All(a => a.Count == 0)))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in emptyKeys)
            connections.Remove(key);
    }

    private static void RenameConnectionKey(JsonObject connections, string oldName, string newName)
    {
        if (connections[oldName] is not JsonNode entry) return;
        connections.Remove(oldName);
        connections[newName] = entry;
    }

    private static void ReplaceExpressionReferences(JsonNode? node, string oldRef, string newRef)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var str) && str.Contains(oldRef))
                        obj[key] = str.Replace(oldRef, newRef);
                    else
                        ReplaceExpressionReferences(obj[key], oldRef, newRef);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue value && value.TryGetValue<string>(out var str) && str.Contains(oldRef))
                        arr[i] = str.Replace(oldRef, newRef);
                    else
                        ReplaceExpressionReferences(arr[i], oldRef, newRef);
                }
                break;
        }
    }

    public async Task<JsonNode?> UpdateWorkflowNodeAsync(string workflowId, string nodeName, JsonObject patch, CancellationToken ct = default)
    {
        var current = await SendAsync(HttpMethod.Get, $"workflows/{workflowId}", ct: ct) as JsonObject
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' não encontrado.");

        var nodes = current["nodes"] as JsonArray ?? [];
        var node = nodes.OfType<JsonObject>().FirstOrDefault(n => n["name"]?.GetValue<string>() == nodeName)
            ?? throw new InvalidOperationException($"Node '{nodeName}' não encontrado no workflow '{workflowId}'.");

        DeepMergePatch(node, patch);

        var result = await SendAsync(HttpMethod.Put, $"workflows/{workflowId}", FilterWorkflowPayload(current), ct);
        AttachDataLossWarningIfNeeded(current, result);
        return result;
    }

    public async Task<JsonNode?> AddWorkflowNodeAsync(string workflowId, JsonObject node, string? connectFrom, string? connectTo, CancellationToken ct = default)
    {
        var current = await SendAsync(HttpMethod.Get, $"workflows/{workflowId}", ct: ct) as JsonObject
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' não encontrado.");

        var nodeName = node["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException("O node informado precisa ter um campo 'name'.");

        var nodes = current["nodes"] as JsonArray;
        if (nodes is null)
        {
            nodes = new JsonArray();
            current["nodes"] = nodes;
        }

        if (nodes.OfType<JsonObject>().Any(n => n["name"]?.GetValue<string>() == nodeName))
            throw new InvalidOperationException($"Já existe um node chamado '{nodeName}' neste workflow.");

        if (node["id"] is null)
            node["id"] = Guid.NewGuid().ToString();
        if (node["position"] is not JsonArray)
            node["position"] = new JsonArray(JsonValue.Create(0), JsonValue.Create(0));

        nodes.Add(node);

        var connections = current["connections"] as JsonObject;
        if (connections is null)
        {
            connections = new JsonObject();
            current["connections"] = connections;
        }
        if (!string.IsNullOrWhiteSpace(connectFrom)) AddConnection(connections, connectFrom, nodeName);
        if (!string.IsNullOrWhiteSpace(connectTo)) AddConnection(connections, nodeName, connectTo);

        var result = await SendAsync(HttpMethod.Put, $"workflows/{workflowId}", FilterWorkflowPayload(current), ct);
        AttachDataLossWarningIfNeeded(current, result);
        return result;
    }

    public async Task<JsonNode?> DeleteWorkflowNodeAsync(string workflowId, string nodeName, bool reconnect, CancellationToken ct = default)
    {
        var current = await SendAsync(HttpMethod.Get, $"workflows/{workflowId}", ct: ct) as JsonObject
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' não encontrado.");

        var nodes = current["nodes"] as JsonArray ?? [];
        var node = nodes.OfType<JsonObject>().FirstOrDefault(n => n["name"]?.GetValue<string>() == nodeName)
            ?? throw new InvalidOperationException($"Node '{nodeName}' não encontrado no workflow '{workflowId}'.");

        var connections = current["connections"] as JsonObject;
        if (connections is null)
        {
            connections = new JsonObject();
            current["connections"] = connections;
        }

        var upstream = FindUpstreamConnections(connections, nodeName);
        var downstream = FindDownstreamConnections(connections, nodeName);

        nodes.Remove(node);
        connections.Remove(nodeName);
        RemoveConnectionsTo(connections, nodeName);

        if (reconnect)
            // Cada upstream religa, no seu próprio índice de saída original (preservando ramos de
            // IF/Switch a montante), para todos os destinos a jusante de nodeName, preservando o
            // índice de entrada original de cada destino.
            foreach (var (upNode, upOutputIndex) in upstream)
                foreach (var (downNode, downInputIndex) in downstream)
                    AddConnection(connections, upNode, upOutputIndex, downNode, downInputIndex);

        var result = await SendAsync(HttpMethod.Put, $"workflows/{workflowId}", FilterWorkflowPayload(current), ct);
        AttachDataLossWarningIfNeeded(current, result);
        return result;
    }

    public async Task<JsonNode?> RenameWorkflowNodeAsync(string workflowId, string oldName, string newName, CancellationToken ct = default)
    {
        var current = await SendAsync(HttpMethod.Get, $"workflows/{workflowId}", ct: ct) as JsonObject
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' não encontrado.");

        var nodeList = (current["nodes"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        if (nodeList.Any(n => n["name"]?.GetValue<string>() == newName))
            throw new InvalidOperationException($"Já existe um node chamado '{newName}' neste workflow.");

        var node = nodeList.FirstOrDefault(n => n["name"]?.GetValue<string>() == oldName)
            ?? throw new InvalidOperationException($"Node '{oldName}' não encontrado no workflow '{workflowId}'.");

        node["name"] = newName;

        if (current["connections"] is JsonObject connections)
        {
            RenameConnectionKey(connections, oldName, newName);
            foreach (var (_, entry) in connections)
            {
                if (entry is not JsonObject entryObj || entryObj["main"] is not JsonArray outputs) continue;
                foreach (var target in outputs.OfType<JsonArray>().SelectMany(t => t.OfType<JsonObject>()))
                    if (target["node"]?.GetValue<string>() == oldName)
                        target["node"] = newName;
            }
        }

        var oldRef = $"$('{oldName}')";
        var newRef = $"$('{newName}')";
        foreach (var n in nodeList)
            ReplaceExpressionReferences(n, oldRef, newRef);

        var result = await SendAsync(HttpMethod.Put, $"workflows/{workflowId}", FilterWorkflowPayload(current), ct);
        AttachDataLossWarningIfNeeded(current, result);
        return result;
    }

    // ---------- Data Tables ----------

    public Task<JsonNode?> ListDataTablesAsync(string? cursor, CancellationToken ct = default)
    {
        var query = BuildQuery(("limit", "250"), ("cursor", cursor));
        return SendAsync(HttpMethod.Get, $"data-tables{query}", ct: ct);
    }

    public Task<JsonNode?> GetDataTableAsync(string tableId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"data-tables/{tableId}", ct: ct);

    public Task<JsonNode?> CreateDataTableAsync(string name, JsonArray columns, string? projectId, CancellationToken ct = default)
    {
        var body = new JsonObject { ["name"] = name, ["columns"] = columns };
        if (!string.IsNullOrWhiteSpace(projectId)) body["projectId"] = projectId;
        return SendAsync(HttpMethod.Post, "data-tables", body, ct);
    }

    public Task<JsonNode?> UpdateDataTableAsync(string tableId, string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"data-tables/{tableId}", new JsonObject { ["name"] = name }, ct);

    public Task<JsonNode?> DeleteDataTableAsync(string tableId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"data-tables/{tableId}", ct: ct);

    public Task<JsonNode?> GetDataTableRowsAsync(string tableId, int limit, string? cursor, CancellationToken ct = default)
    {
        var query = BuildQuery(("limit", limit.ToString()), ("cursor", cursor));
        return SendAsync(HttpMethod.Get, $"data-tables/{tableId}/rows{query}", ct: ct);
    }

    public Task<JsonNode?> CreateDataTableColumnAsync(string tableId, string name, string type, CancellationToken ct = default)
    {
        var body = new JsonObject { ["name"] = name, ["type"] = type };
        return SendAsync(HttpMethod.Post, $"data-tables/{tableId}/columns", body, ct);
    }

    public Task<JsonNode?> DeleteDataTableColumnAsync(string tableId, string columnId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"data-tables/{tableId}/columns/{columnId}", ct: ct);

    public Task<JsonNode?> UpdateDataTableRowsAsync(string tableId, JsonObject filter, JsonObject data, bool returnData, CancellationToken ct = default)
    {
        var body = new JsonObject { ["filter"] = filter, ["data"] = data, ["returnData"] = returnData };
        return SendAsync(HttpMethod.Patch, $"data-tables/{tableId}/rows/update", body, ct);
    }

    public Task<JsonNode?> UpsertDataTableRowsAsync(string tableId, JsonObject filter, JsonObject data, bool returnData, CancellationToken ct = default)
    {
        var body = new JsonObject { ["filter"] = filter, ["data"] = data, ["returnData"] = returnData };
        return SendAsync(HttpMethod.Post, $"data-tables/{tableId}/rows/upsert", body, ct);
    }

    public Task<JsonNode?> InsertDataTableRowsAsync(string tableId, JsonArray rows, string returnType, CancellationToken ct = default)
    {
        var body = new JsonObject { ["data"] = rows, ["returnType"] = returnType };
        return SendAsync(HttpMethod.Post, $"data-tables/{tableId}/rows", body, ct);
    }

    public Task<JsonNode?> DeleteDataTableRowsAsync(string tableId, JsonObject filter, bool returnData, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("filter", filter.ToJsonString()),
            ("returnData", returnData.ToString().ToLowerInvariant()));
        return SendAsync(HttpMethod.Delete, $"data-tables/{tableId}/rows/delete{query}", ct: ct);
    }

    // ---------- Executions ----------

    public Task<JsonNode?> ListExecutionsAsync(string? workflowId, string? status, bool includeData, int limit, string? cursor, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("limit", limit.ToString()),
            ("workflowId", workflowId),
            ("status", status),
            ("includeData", includeData.ToString().ToLowerInvariant()),
            ("cursor", cursor));
        return SendAsync(HttpMethod.Get, $"executions{query}", ct: ct);
    }

    public Task<JsonNode?> GetExecutionAsync(string executionId, bool includeData, CancellationToken ct = default)
    {
        var query = BuildQuery(("includeData", includeData.ToString().ToLowerInvariant()));
        return SendAsync(HttpMethod.Get, $"executions/{executionId}{query}", ct: ct);
    }

    public Task<JsonNode?> DeleteExecutionAsync(string executionId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"executions/{executionId}", ct: ct);

    // ---------- Tags ----------

    public Task<JsonNode?> ListTagsAsync(int limit, string? cursor, CancellationToken ct = default)
    {
        var query = BuildQuery(("limit", limit.ToString()), ("cursor", cursor));
        return SendAsync(HttpMethod.Get, $"tags{query}", ct: ct);
    }

    public Task<JsonNode?> GetTagAsync(string tagId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"tags/{tagId}", ct: ct);

    public Task<JsonNode?> CreateTagAsync(string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "tags", new JsonObject { ["name"] = name }, ct);

    public Task<JsonNode?> UpdateTagAsync(string tagId, string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"tags/{tagId}", new JsonObject { ["name"] = name }, ct);

    public Task<JsonNode?> DeleteTagAsync(string tagId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"tags/{tagId}", ct: ct);

    public Task<JsonNode?> GetWorkflowTagsAsync(string workflowId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"workflows/{workflowId}/tags", ct: ct);

    public Task<JsonNode?> SetWorkflowTagsAsync(string workflowId, IReadOnlyList<string> tagIds, CancellationToken ct = default)
    {
        var body = new JsonArray(tagIds.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray());
        return SendAsync(HttpMethod.Put, $"workflows/{workflowId}/tags", body, ct);
    }
}
