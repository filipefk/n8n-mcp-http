using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class WorkflowTools(McpClient n8n)
{
    private static JsonArray FindMatchesInWorkflow(JsonObject workflow, string search)
    {
        var result = new JsonArray();

        if ((workflow["name"]?.GetValue<string>() ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))
            result.Add(new JsonObject { ["node"] = null, ["type"] = null, ["campo"] = "workflowName" });

        foreach (var node in (workflow["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = node["name"]?.GetValue<string>() ?? "";
            var type = node["type"]?.GetValue<string>() ?? "";
            var parametersText = node["parameters"]?.ToJsonString() ?? "";

            string? field = name.Contains(search, StringComparison.OrdinalIgnoreCase) ? "name"
                : type.Contains(search, StringComparison.OrdinalIgnoreCase) ? "type"
                : parametersText.Contains(search, StringComparison.OrdinalIgnoreCase) ? "parameters"
                : null;

            if (field is not null)
                result.Add(new JsonObject { ["node"] = name, ["type"] = type, ["campo"] = field });
        }

        return result;
    }

    private async Task<string> WorkflowSummary(Func<string, CancellationToken, Task<JsonNode?>> action, string workflowIdOrUrl, CancellationToken ct)
    {
        var id = ResolveWorkflowId(workflowIdOrUrl);
        var result = await action(id, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Listar workflows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os workflows do n8n, com filtros opcionais por nome e por estado ativo. Resposta resumida por item (id, name, active, createdAt, updatedAt, tags, nodeCount) — use get_workflow para o conteúdo completo. Resposta paginada (verifique nextCursor).")]
    public async Task<string> list_workflows(
        [Description("Filtra pelo nome do workflow (opcional)")] string? name = null,
        [Description("Filtra por estado ativo: true, false ou null para não filtrar")] bool? active = null,
        [Description("Quantidade máxima de itens (padrão 100, máximo 250)")] int limit = 100,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await n8n.ListWorkflowsAsync(name, active, limit, cursor, excludePinnedData: true, ct: ct) as JsonObject;
        if (result?["data"] is JsonArray data)
            result["data"] = new JsonArray([.. data.Select(item => (JsonNode)SummarizeWorkflowListItem(item as JsonObject))]);
        return Json(result);
    }

    [McpServerTool(Title = "Buscar texto nos workflows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Procura um texto em todos os workflows do n8n (nome, nome/tipo dos nodes e parâmetros de cada node), paginando automaticamente. Retorna apenas os workflows e nodes que casaram, evitando N chamadas de get_workflow. Resposta limitada a max_results workflows (verifique truncated).")]
    public async Task<string> find_in_workflows(
        [Description("Texto a procurar (case-insensitive)")] string search,
        [Description("Filtra por estado ativo: true, false ou null para não filtrar (opcional)")] bool? active = null,
        [Description("Quantidade máxima de workflows casados a retornar (padrão 50)")] int max_results = 50,
        CancellationToken ct = default)
    {
        var matches = new JsonArray();
        string? cursor = null;
        string? previousCursor = null;
        var truncated = false;
        for (var page = 0; ; page++)
        {
            GuardPagination(page, cursor, previousCursor);
            var result = await n8n.ListWorkflowsAsync(null, active, 250, cursor, excludePinnedData: true, ct: ct) as JsonObject;
            foreach (var item in (result?["data"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (matches.Count >= max_results) { truncated = true; break; }
                var workflowMatches = FindMatchesInWorkflow(item, search);
                if (workflowMatches.Count > 0)
                    matches.Add(new JsonObject
                    {
                        ["workflowId"] = item["id"]?.DeepClone(),
                        ["workflowName"] = item["name"]?.DeepClone(),
                        ["matches"] = workflowMatches
                    });
            }
            previousCursor = cursor;
            cursor = result?["nextCursor"]?.GetValue<string>();
            if (truncated || string.IsNullOrEmpty(cursor)) break;
        }

        return new JsonObject { ["data"] = matches, ["truncated"] = truncated }.ToJsonString();
    }

    [McpServerTool(Title = "Obter workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém o JSON de um workflow do n8n (nodes, connections, settings, active, tags). Por padrão omite pinData/staticData/meta/shared; use full=true para o workflow completo.")]
    public async Task<string> get_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Se true, retorna o workflow completo incluindo pinData/staticData/meta/shared")] bool full = false,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.GetWorkflowAsync(id, excludePinnedData: !full, ct: ct) as JsonObject;
        if (!full)
        {
            result?.Remove("pinData");
            result?.Remove("staticData");
            result?.Remove("meta");
            result?.Remove("shared");
        }
        return Json(result);
    }

    [McpServerTool(Title = "Listar/obter nodes do workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os nodes de um workflow (nome, tipo, disabled). Se node_name for informado, retorna o JSON completo apenas daquele node.")]
    public async Task<string> get_workflow_nodes(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nome de um node específico para retornar completo, em vez da lista resumida (opcional)")] string? node_name = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var workflow = await n8n.GetWorkflowAsync(id, excludePinnedData: true, ct: ct) as JsonObject;
        var nodes = workflow?["nodes"] as JsonArray ?? [];

        if (node_name is not null)
        {
            var node = nodes.FirstOrDefault(n => string.Equals(n?["name"]?.GetValue<string>(), node_name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Node '{node_name}' não encontrado no workflow '{id}'.");
            return node.ToJsonString();
        }

        var summary = new JsonArray(nodes.Select(node => (JsonNode)new JsonObject
        {
            ["name"] = node?["name"]?.DeepClone(),
            ["type"] = node?["type"]?.DeepClone(),
            ["disabled"] = node?["disabled"]?.DeepClone() ?? false
        }).ToArray());
        return summary.ToJsonString();
    }

    [McpServerTool(Title = "Atualizar node do workflow", Idempotent = true, OpenWorld = false), Description("Altera parâmetros de um node existente sem reenviar o workflow inteiro: aplica um merge profundo do patch informado sobre o node (ex: apenas {\"parameters\":{\"url\":\"...\"}}).")]
    public async Task<string> update_workflow_node(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nome do node a alterar")] string node_name,
        [Description("Objeto com apenas os campos/propriedades do node a mesclar (ex: {\"parameters\":{...}}, \"disabled\":true)")] JsonObject node_patch,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.UpdateWorkflowNodeAsync(id, node_name, node_patch, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Adicionar node ao workflow", Destructive = false, OpenWorld = false), Description("Adiciona um novo node a um workflow existente, opcionalmente conectando-o a nodes já existentes (saída/entrada principal, main, índice 0). Gera 'id' e 'position' automaticamente se ausentes.")]
    public async Task<string> add_workflow_node(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Node a adicionar (deve ter ao menos 'name' e 'type')")] JsonObject node,
        [Description("Nome de um node existente para conectar como origem deste novo node (opcional)")] string? connect_from = null,
        [Description("Nome de um node existente para conectar como destino deste novo node (opcional)")] string? connect_to = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.AddWorkflowNodeAsync(id, node, connect_from, connect_to, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Remover node do workflow", Idempotent = true, OpenWorld = false), Description("Remove um node de um workflow, junto com toda referência a ele em connections (inclusive conexões de IA). Por padrão religa os nodes que apontavam para ele diretamente aos nodes que ele apontava, preservando os ramos de saída de origem (reconnect=true).")]
    public async Task<string> delete_workflow_node(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nome do node a remover")] string node_name,
        [Description("Se true, religa os nodes upstream diretamente aos nodes downstream após remover")] bool reconnect = true,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.DeleteWorkflowNodeAsync(id, node_name, reconnect, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Renomear node do workflow", Idempotent = true, OpenWorld = false), Description("Renomeia um node, atualizando as referências em connections e as expressões $('Nome antigo') nos parâmetros dos demais nodes.")]
    public async Task<string> rename_workflow_node(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nome atual do node")] string old_name,
        [Description("Novo nome do node")] string new_name,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.RenameWorkflowNodeAsync(id, old_name, new_name, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Criar workflow", Destructive = false, OpenWorld = false), Description("Cria um novo workflow no n8n, sempre inativo. Retorna um resumo compacto (id, name, active, nodeCount, nodes) em vez do workflow completo.")]
    public async Task<string> create_workflow(
        [Description("Nome do workflow")] string name,
        [Description("Nodes do workflow (padrão: [])")] JsonArray? nodes = null,
        [Description("Conexões entre nodes, indexadas pelo nome do node de origem (padrão: {})")] JsonObject? connections = null,
        [Description("Configurações do workflow (padrão: {\"executionOrder\":\"v1\"})")] JsonObject? settings = null,
        CancellationToken ct = default)
    {
        var workflow = new JsonObject
        {
            ["name"] = name,
            ["nodes"] = nodes ?? new JsonArray(),
            ["connections"] = connections ?? new JsonObject(),
            ["settings"] = settings ?? new JsonObject { ["executionOrder"] = "v1" }
        };
        var result = await n8n.CreateWorkflowAsync(workflow, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Atualizar workflow", Idempotent = true, OpenWorld = false), Description("Atualiza um workflow existente. Apenas os parâmetros informados são alterados; os demais mantêm o valor atual. Não altera o estado ativo (use set_workflow_active). Retorna um resumo compacto em vez do workflow completo.")]
    public async Task<string> update_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Novo nome do workflow (null para não alterar)")] string? new_name = null,
        [Description("Nodes para substituir os atuais (null para não alterar)")] JsonArray? nodes = null,
        [Description("Conexões para substituir as atuais (null para não alterar)")] JsonObject? connections = null,
        [Description("Configurações para substituir as atuais (null para não alterar)")] JsonObject? settings = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.UpdateWorkflowAsync(id, new_name, nodes, connections, settings, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Duplicar workflow", Destructive = false, OpenWorld = false), Description("Duplica um workflow existente. A cópia é criada inativa. Retorna um resumo compacto em vez do workflow completo.")]
    public async Task<string> duplicate_workflow(
        [Description("ID ou URL do workflow de origem")] string workflow_id,
        [Description("Nome da cópia (padrão: \"<nome original> (cópia)\")")] string? new_name = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.DuplicateWorkflowAsync(id, new_name, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Excluir workflow", Idempotent = true, OpenWorld = false), Description("Exclui permanentemente um workflow do n8n.")]
    public async Task<string> delete_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        await n8n.DeleteWorkflowAsync(id, ct);
        return new JsonObject { ["deleted"] = true, ["id"] = id }.ToJsonString();
    }

    [McpServerTool(Title = "Ativar/desativar workflow", Destructive = false, Idempotent = true, OpenWorld = false), Description("Ativa ou desativa um workflow do n8n.")]
    public Task<string> set_workflow_active(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("true para ativar, false para desativar")] bool active,
        CancellationToken ct = default) =>
        WorkflowSummary((id, token) => n8n.SetWorkflowActiveAsync(id, active, token), workflow_id, ct);
}
