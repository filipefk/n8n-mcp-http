using System.Text.Json.Nodes;

namespace McpToolkit;

/// <summary>Helpers compartilhados entre as classes de tools MCP (WorkflowTools, DataTableTools, ExecutionTools, TagTools, BackupTools).</summary>
internal static class McpToolsHelpers
{
    // Exemplo de filtro reutilizado nas descrições de várias tools de data table, para não repetir
    // o mesmo literal JSON escapado em cada uma.
    public const string FilterJsonExample = """{"type":"and","filters":[{"columnName":"x","condition":"eq","value":"y"}]}""";

    // Teto de segurança para loops de paginação: aborta em vez de rodar para sempre se a API
    // devolver um cursor inválido que nunca termina ou que se repete.
    public const int MaxPaginationPages = 1000;

    public static void GuardPagination(int pageIndex, string? cursor, string? previousCursor)
    {
        if (pageIndex >= MaxPaginationPages)
            throw new InvalidOperationException("Paginação excedeu o limite de segurança (possível cursor inválido); abortando.");
        if (cursor is not null && cursor == previousCursor)
            throw new InvalidOperationException("Cursor de paginação repetido detectado; abortando para evitar loop infinito.");
    }

    public static string Json(JsonNode? node, string fallback = "{}") => node?.ToJsonString() ?? fallback;

    public static string ResolveWorkflowId(string workflowIdOrUrl)
    {
        var idx = workflowIdOrUrl.IndexOf("/workflow/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return workflowIdOrUrl;

        var start = idx + "/workflow/".Length;
        var end = workflowIdOrUrl.IndexOfAny(['/', '?', '#'], start);
        return end < 0 ? workflowIdOrUrl[start..] : workflowIdOrUrl[start..end];
    }

    public static JsonArray SummarizeTagNames(JsonArray? tags) =>
        new([.. (tags ?? []).Select(t => (JsonNode)JsonValue.Create(t?["name"]?.GetValue<string>() ?? ""))]);

    public static JsonObject SummarizeWorkflowListItem(JsonObject? workflow) => new()
    {
        ["id"] = workflow?["id"]?.DeepClone(),
        ["name"] = workflow?["name"]?.DeepClone(),
        ["active"] = workflow?["active"]?.DeepClone(),
        ["createdAt"] = workflow?["createdAt"]?.DeepClone(),
        ["updatedAt"] = workflow?["updatedAt"]?.DeepClone(),
        ["tags"] = SummarizeTagNames(workflow?["tags"] as JsonArray),
        ["nodeCount"] = (workflow?["nodes"] as JsonArray)?.Count ?? 0
    };

    public static JsonObject BuildWorkflowChangeSummary(JsonObject? workflow)
    {
        var nodes = workflow?["nodes"] as JsonArray ?? [];
        var summary = new JsonObject
        {
            ["id"] = workflow?["id"]?.DeepClone(),
            ["name"] = workflow?["name"]?.DeepClone(),
            ["active"] = workflow?["active"]?.DeepClone(),
            ["nodeCount"] = nodes.Count,
            ["nodes"] = new JsonArray([.. nodes.Select(n => (JsonNode)JsonValue.Create(n?["name"]?.GetValue<string>() ?? ""))])
        };
        if (workflow?["_dataLossWarning"] is JsonNode warning)
            summary["dataLossWarning"] = warning.DeepClone();
        return summary;
    }

    public static (string? Node, string? Message) ExtractExecutionError(JsonObject? execution)
    {
        var error = execution?["data"]?["resultData"]?["error"] as JsonObject;
        if (error is null) return (null, null);

        var node = error["node"]?["name"]?.GetValue<string>()
            ?? execution?["data"]?["resultData"]?["lastNodeExecuted"]?.GetValue<string>();
        var message = error["message"]?.GetValue<string>();
        return (node, message);
    }

    public static JsonObject SummarizeExecution(JsonObject? execution, bool includeFullData)
    {
        var summary = new JsonObject
        {
            ["id"] = execution?["id"]?.DeepClone(),
            ["status"] = execution?["status"]?.DeepClone(),
            ["workflowId"] = execution?["workflowId"]?.DeepClone(),
            ["mode"] = execution?["mode"]?.DeepClone(),
            ["startedAt"] = execution?["startedAt"]?.DeepClone(),
            ["stoppedAt"] = execution?["stoppedAt"]?.DeepClone()
        };

        var (errorNode, errorMessage) = ExtractExecutionError(execution);
        if (errorNode is not null) summary["errorNode"] = errorNode;
        if (errorMessage is not null) summary["errorMessage"] = errorMessage;

        if (includeFullData && execution?["data"] is JsonNode data)
            summary["data"] = data.DeepClone();

        return summary;
    }

    public static JsonObject StripRowMetadata(JsonObject row)
    {
        var clone = (JsonObject)row.DeepClone();
        clone.Remove("id");
        clone.Remove("createdAt");
        clone.Remove("updatedAt");
        return clone;
    }
}
