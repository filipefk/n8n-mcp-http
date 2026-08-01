using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class ExecutionTools(McpClient n8n)
{
    [McpServerTool(Title = "Listar execuções", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as execuções de workflows do n8n, com filtros opcionais por workflow e status. Resposta resumida por item (id, status, workflowId, mode, startedAt, stoppedAt e, se houve erro, errorNode/errorMessage); use include_data=true para o payload completo de cada execução. Resposta paginada (verifique nextCursor).")]
    public async Task<string> list_executions(
        [Description("Filtra pelas execuções de um workflow específico (opcional, aceita ID ou URL)")] string? workflow_id = null,
        [Description("Filtra por status: error, success ou waiting (opcional)")] string? status = null,
        [Description("Se true, inclui os dados completos de cada execução na resposta (payload grande)")] bool include_data = false,
        [Description("Quantidade máxima de itens (padrão 100, máximo 250)")] int limit = 100,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null,
        CancellationToken ct = default)
    {
        var id = workflow_id is null ? null : ResolveWorkflowId(workflow_id);
        // O payload completo só é baixado da API quando o próprio chamador pediu (include_data=true)
        // ou, item a item, quando a execução deu erro (para extrair errorNode/errorMessage) — antes
        // isso baixava sempre o payload inteiro de todas as execuções da página, mesmo descartado em seguida.
        var result = await n8n.ListExecutionsAsync(id, status, includeData: include_data, limit, cursor, ct) as JsonObject;
        if (result?["data"] is JsonArray data)
        {
            var summarized = new JsonArray();
            foreach (var item in data.OfType<JsonObject>())
                summarized.Add(await SummarizeWithErrorDetailAsync(item, include_data, ct));
            result["data"] = summarized;
        }
        return Json(result);
    }

    [McpServerTool(Title = "Obter execução", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém os detalhes de uma execução do n8n. Resposta resumida por padrão (id, status, workflowId, mode, startedAt, stoppedAt e, se houve erro, errorNode/errorMessage); use include_data=true para o payload completo.")]
    public async Task<string> get_execution(
        [Description("ID da execução")] string execution_id,
        [Description("Se true, inclui os dados completos de execução na resposta (payload grande)")] bool include_data = false,
        CancellationToken ct = default)
    {
        var result = await n8n.GetExecutionAsync(execution_id, includeData: include_data, ct) as JsonObject;
        return (await SummarizeWithErrorDetailAsync(result, include_data, ct)).ToJsonString();
    }

    private async Task<JsonObject> SummarizeWithErrorDetailAsync(JsonObject? execution, bool includeData, CancellationToken ct)
    {
        if (!includeData && execution?["status"]?.GetValue<string>() == "error" && execution["id"]?.GetValue<string>() is string executionId)
        {
            var detail = await n8n.GetExecutionAsync(executionId, includeData: true, ct) as JsonObject;
            return SummarizeExecution(detail ?? execution, includeFullData: false);
        }
        return SummarizeExecution(execution, includeData);
    }

    [McpServerTool(Title = "Excluir execução", Idempotent = true, OpenWorld = false), Description("Exclui uma execução do histórico do n8n.")]
    public async Task<string> delete_execution(
        [Description("ID da execução")] string execution_id,
        CancellationToken ct = default) =>
        Json(await n8n.DeleteExecutionAsync(execution_id, ct));
}
