using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;

namespace McpToolkit;

[McpServerToolType]
public sealed class McpTools(McpClient n8n, BackupService backups, IHttpContextAccessor httpContextAccessor, IOptions<McpOptions> options)
{
    private string BuildBackupUrl(string fileName)
    {
        var baseUrl = options.Value.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            var request = httpContextAccessor.HttpContext?.Request
                ?? throw new InvalidOperationException("Não foi possível determinar a URL pública do servidor (nenhuma requisição HTTP ativa e N8n:PublicBaseUrl não configurado).");
            baseUrl = $"{request.Scheme}://{request.Host}";
        }
        return $"{baseUrl.TrimEnd('/')}/backups/{Uri.EscapeDataString(fileName)}";
    }
    private static JsonObject ParseObject(string json, string paramName)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException($"O parâmetro '{paramName}' deve ser um objeto JSON.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"O parâmetro '{paramName}' contém um JSON inválido: {ex.Message}");
        }
    }

    private static JsonArray ParseArray(string json, string paramName)
    {
        try
        {
            return JsonNode.Parse(json) as JsonArray
                ?? throw new InvalidOperationException($"O parâmetro '{paramName}' deve ser um array JSON.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"O parâmetro '{paramName}' contém um JSON inválido: {ex.Message}");
        }
    }

    private static string ResolveWorkflowId(string workflowIdOrUrl)
    {
        var idx = workflowIdOrUrl.IndexOf("/workflow/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return workflowIdOrUrl;

        var start = idx + "/workflow/".Length;
        var end = workflowIdOrUrl.IndexOfAny(['/', '?', '#'], start);
        return end < 0 ? workflowIdOrUrl[start..] : workflowIdOrUrl[start..end];
    }

    private static JsonArray SummarizeTagNames(JsonArray? tags) =>
        new([.. (tags ?? []).Select(t => (JsonNode)JsonValue.Create(t?["name"]?.GetValue<string>() ?? ""))]);

    private static JsonObject SummarizeWorkflowListItem(JsonObject? workflow) => new()
    {
        ["id"] = workflow?["id"]?.DeepClone(),
        ["name"] = workflow?["name"]?.DeepClone(),
        ["active"] = workflow?["active"]?.DeepClone(),
        ["createdAt"] = workflow?["createdAt"]?.DeepClone(),
        ["updatedAt"] = workflow?["updatedAt"]?.DeepClone(),
        ["tags"] = SummarizeTagNames(workflow?["tags"] as JsonArray),
        ["nodeCount"] = (workflow?["nodes"] as JsonArray)?.Count ?? 0
    };

    private static JsonObject BuildWorkflowChangeSummary(JsonObject? workflow)
    {
        var nodes = workflow?["nodes"] as JsonArray ?? [];
        return new JsonObject
        {
            ["id"] = workflow?["id"]?.DeepClone(),
            ["name"] = workflow?["name"]?.DeepClone(),
            ["active"] = workflow?["active"]?.DeepClone(),
            ["nodeCount"] = nodes.Count,
            ["nodes"] = new JsonArray([.. nodes.Select(n => (JsonNode)JsonValue.Create(n?["name"]?.GetValue<string>() ?? ""))])
        };
    }

    private static (string? Node, string? Message) ExtractExecutionError(JsonObject? execution)
    {
        var error = execution?["data"]?["resultData"]?["error"] as JsonObject;
        if (error is null) return (null, null);

        var node = error["node"]?["name"]?.GetValue<string>()
            ?? execution?["data"]?["resultData"]?["lastNodeExecuted"]?.GetValue<string>();
        var message = error["message"]?.GetValue<string>();
        return (node, message);
    }

    private static JsonObject SummarizeExecution(JsonObject? execution, bool includeFullData)
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

    private async Task<string> BackupWorkflowAsync(string workflowId, CancellationToken ct)
    {
        var workflow = await n8n.GetWorkflowAsync(workflowId, excludePinnedData: false, ct: ct);
        var name = (workflow as JsonObject)?["name"]?.GetValue<string>();
        return await backups.SaveWorkflowBackupAsync(workflowId, name, workflow, ct);
    }

    private async Task<JsonArray> FetchAllDataTableRowsAsync(string tableId, CancellationToken ct)
    {
        var rows = new JsonArray();
        string? cursor = null;
        do
        {
            var page = await n8n.GetDataTableRowsAsync(tableId, 250, cursor, ct) as JsonObject;
            if (page?["data"] is JsonArray data)
                foreach (var row in data)
                    rows.Add(row?.DeepClone());
            cursor = page?["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor));
        return rows;
    }

    private async Task<string> BackupDataTableAsync(string tableId, CancellationToken ct)
    {
        var table = await n8n.GetDataTableAsync(tableId, ct);
        var name = (table as JsonObject)?["name"]?.GetValue<string>();
        var rows = await FetchAllDataTableRowsAsync(tableId, ct);
        return await backups.SaveDataTableBackupAsync(tableId, name, table, rows, ct);
    }

    private static JsonObject StripRowMetadata(JsonObject row)
    {
        var clone = (JsonObject)row.DeepClone();
        clone.Remove("id");
        clone.Remove("createdAt");
        clone.Remove("updatedAt");
        return clone;
    }

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

    // ---------- Workflows ----------

    [McpServerTool(Title = "Listar workflows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os workflows do n8n, com filtros opcionais por nome e por estado ativo. Resposta resumida por item (id, name, active, createdAt, updatedAt, tags, nodeCount) — use get_workflow para o conteúdo completo de um workflow específico. Resposta paginada (verifique nextCursor).")]
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
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Buscar texto nos workflows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Procura um texto em todos os workflows do n8n (nome do workflow, nome/tipo dos nodes e parâmetros de cada node), paginando automaticamente. Retorna apenas os workflows e nodes que casaram, evitando N chamadas de get_workflow para localizar onde algo é usado.")]
    public async Task<string> find_in_workflows(
        [Description("Texto a procurar (case-insensitive)")] string search,
        [Description("Filtra por estado ativo: true, false ou null para não filtrar (opcional)")] bool? active = null,
        CancellationToken ct = default)
    {
        var matches = new JsonArray();
        string? cursor = null;
        do
        {
            var page = await n8n.ListWorkflowsAsync(null, active, 250, cursor, excludePinnedData: true, ct: ct) as JsonObject;
            foreach (var item in (page?["data"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var workflowMatches = FindMatchesInWorkflow(item, search);
                if (workflowMatches.Count > 0)
                    matches.Add(new JsonObject
                    {
                        ["workflowId"] = item["id"]?.DeepClone(),
                        ["workflowName"] = item["name"]?.DeepClone(),
                        ["matches"] = workflowMatches
                    });
            }
            cursor = page?["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor));

        return matches.ToJsonString();
    }

    [McpServerTool(Title = "Obter workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém o JSON de um workflow do n8n (nodes, connections, settings, active, tags). Por padrão omite pinData/staticData/meta/shared para reduzir o tamanho da resposta; use full=true para obter o workflow completo.")]
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
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Listar nodes do workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os nodes de um workflow do n8n, com nome, tipo e se está desabilitado.")]
    public async Task<string> get_workflow_nodes(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var workflow = await n8n.GetWorkflowAsync(id, excludePinnedData: true, ct: ct) as JsonObject;
        var nodes = workflow?["nodes"] as JsonArray ?? [];
        var summary = new JsonArray(nodes.Select(node => (JsonNode)new JsonObject
        {
            ["name"] = node?["name"]?.DeepClone(),
            ["type"] = node?["type"]?.DeepClone(),
            ["disabled"] = node?["disabled"]?.DeepClone() ?? false
        }).ToArray());
        return summary.ToJsonString();
    }

    [McpServerTool(Title = "Obter node do workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém o JSON completo de um único node de um workflow do n8n, pelo nome do node.")]
    public async Task<string> get_workflow_node(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nome do node")] string node_name,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var workflow = await n8n.GetWorkflowAsync(id, excludePinnedData: true, ct: ct) as JsonObject;
        var nodes = workflow?["nodes"] as JsonArray ?? [];
        var node = nodes.FirstOrDefault(n => string.Equals(n?["name"]?.GetValue<string>(), node_name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Node '{node_name}' não encontrado no workflow '{id}'.");
        return node.ToJsonString();
    }

    [McpServerTool(Title = "Atualizar node do workflow", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Altera parâmetros de um node existente de um workflow do n8n sem reenviar o workflow inteiro: aplica um merge profundo do patch informado sobre o node (ex: apenas {\"parameters\":{\"url\":\"...\"}}). Recomenda-se chamar backup_workflow antes, pois esta ferramenta sobrescreve o node existente.")]
    public async Task<string> update_workflow_node(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nome do node a alterar")] string node_name,
        [Description("Objeto JSON com apenas os campos/propriedades do node a mesclar (ex: {\"parameters\":{...}}, \"disabled\":true)")] string node_patch_json,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var patch = ParseObject(node_patch_json, nameof(node_patch_json));
        var result = await n8n.UpdateWorkflowNodeAsync(id, node_name, patch, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Adicionar node ao workflow", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Adiciona um novo node a um workflow existente do n8n, opcionalmente conectando-o a nodes já existentes (conecta pela saída/entrada principal, main, índice 0). Gera 'id' e 'position' automaticamente se ausentes no node informado.")]
    public async Task<string> add_workflow_node(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Objeto JSON do node a adicionar (deve ter ao menos 'name' e 'type')")] string node_json,
        [Description("Nome de um node existente para conectar como origem deste novo node (opcional)")] string? connect_from = null,
        [Description("Nome de um node existente para conectar como destino deste novo node (opcional)")] string? connect_to = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var node = ParseObject(node_json, nameof(node_json));
        var result = await n8n.AddWorkflowNodeAsync(id, node, connect_from, connect_to, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Remover node do workflow", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Remove um node de um workflow do n8n, junto com todas as referências a ele em connections. Por padrão religa automaticamente os nodes que apontavam para ele diretamente aos nodes que ele apontava (reconnect=true). Recomenda-se chamar backup_workflow antes, pois esta ferramenta remove o node permanentemente.")]
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

    [McpServerTool(Title = "Renomear node do workflow", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Renomeia um node de um workflow do n8n, atualizando as referências em connections e as expressões $('Nome antigo') nos parâmetros dos demais nodes. Recomenda-se chamar backup_workflow antes, pois esta ferramenta altera o workflow existente.")]
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

    [McpServerTool(Title = "Criar workflow", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Cria um novo workflow no n8n. O workflow é sempre criado inativo. O corpo enviado à API contém apenas name, nodes, connections e settings — demais campos são rejeitados pela API do n8n. Retorna um resumo compacto (id, name, active, nodeCount, nodes) em vez do workflow completo.")]
    public async Task<string> create_workflow(
        [Description("Nome do workflow")] string name,
        [Description("Array JSON de nodes do workflow (padrão: [])")] string nodes_json = "[]",
        [Description("Objeto JSON de conexões entre nodes, indexado pelo nome do node de origem (padrão: {})")] string connections_json = "{}",
        [Description("Objeto JSON de configurações do workflow (padrão: {\"executionOrder\":\"v1\"})")] string settings_json = "{\"executionOrder\":\"v1\"}",
        CancellationToken ct = default)
    {
        var workflow = new JsonObject
        {
            ["name"] = name,
            ["nodes"] = ParseArray(nodes_json, nameof(nodes_json)),
            ["connections"] = ParseObject(connections_json, nameof(connections_json)),
            ["settings"] = ParseObject(settings_json, nameof(settings_json))
        };
        var result = await n8n.CreateWorkflowAsync(workflow, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Atualizar workflow", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Atualiza um workflow existente no n8n. Apenas os parâmetros informados são alterados; os demais mantêm o valor atual. Não altera o estado ativo (use activate_workflow/deactivate_workflow para isso). Recomenda-se chamar backup_workflow antes, pois esta ferramenta sobrescreve o workflow existente. Retorna um resumo compacto (id, name, active, nodeCount, nodes) em vez do workflow completo.")]
    public async Task<string> update_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Novo nome do workflow (null para não alterar)")] string? new_name = null,
        [Description("Array JSON de nodes para substituir os atuais (null para não alterar)")] string? nodes_json = null,
        [Description("Objeto JSON de conexões para substituir as atuais (null para não alterar)")] string? connections_json = null,
        [Description("Objeto JSON de configurações para substituir as atuais (null para não alterar)")] string? settings_json = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var nodes = nodes_json is null ? null : ParseArray(nodes_json, nameof(nodes_json));
        var connections = connections_json is null ? null : ParseObject(connections_json, nameof(connections_json));
        var settings = settings_json is null ? null : ParseObject(settings_json, nameof(settings_json));
        var result = await n8n.UpdateWorkflowAsync(id, new_name, nodes, connections, settings, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Duplicar workflow", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Duplica um workflow existente no n8n. A cópia é criada inativa. Retorna um resumo compacto (id, name, active, nodeCount, nodes) em vez do workflow completo.")]
    public async Task<string> duplicate_workflow(
        [Description("ID ou URL do workflow de origem")] string workflow_id,
        [Description("Nome da cópia (padrão: \"<nome original> (cópia)\")")] string? new_name = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.DuplicateWorkflowAsync(id, new_name, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Excluir workflow", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Exclui permanentemente um workflow do n8n. Recomenda-se chamar backup_workflow antes, pois a exclusão é permanente (o backupFile retornado por backup_workflow pode ser usado com restore_workflow_backup para reverter).")]
    public async Task<string> delete_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        await n8n.DeleteWorkflowAsync(id, ct);
        return new JsonObject { ["deleted"] = true, ["id"] = id }.ToJsonString();
    }

    [McpServerTool(Title = "Ativar workflow", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Ativa um workflow do n8n.")]
    public async Task<string> activate_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.ActivateWorkflowAsync(id, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    [McpServerTool(Title = "Desativar workflow", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Desativa um workflow do n8n.")]
    public async Task<string> deactivate_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.DeactivateWorkflowAsync(id, ct) as JsonObject;
        return BuildWorkflowChangeSummary(result).ToJsonString();
    }

    // ---------- Data Tables ----------

    [McpServerTool(Title = "Listar data tables", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as data tables do n8n, com filtro opcional pelo nome (aplicado localmente, sem distinção de maiúsculas/minúsculas, por substring).")]
    public async Task<string> list_data_tables(
        [Description("Filtra pelo nome da tabela (opcional, substring, sem distinção de maiúsculas/minúsculas)")] string? name = null,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await n8n.ListDataTablesAsync(cursor, ct) as JsonObject;
        var data = result?["data"] as JsonArray;
        if (data is not null && !string.IsNullOrWhiteSpace(name))
        {
            var filtered = new JsonArray(data
                .Where(item => (item?["name"]?.GetValue<string>() ?? "").Contains(name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item!.DeepClone())
                .ToArray());
            result!["data"] = filtered;
        }
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Obter data table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém uma data table do n8n, incluindo suas colunas (nome, tipo, índice).")]
    public async Task<string> get_data_table(
        [Description("ID da data table")] string table_id,
        CancellationToken ct = default)
    {
        var result = await n8n.GetDataTableAsync(table_id, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Criar data table", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Cria uma nova data table no n8n. É necessário informar ao menos uma coluna — o schema é imutável após a criação (não é possível adicionar/remover colunas depois, exceto via create/delete_data_table_column).")]
    public async Task<string> create_data_table(
        [Description("Nome da tabela")] string name,
        [Description("Array JSON de colunas, ex: [{\"name\":\"email\",\"type\":\"string\"}]. Tipos válidos: string, number, boolean, date")] string columns_json,
        [Description("ID do projeto onde criar a tabela (opcional; padrão: projeto default)")] string? project_id = null,
        CancellationToken ct = default)
    {
        var columns = ParseArray(columns_json, nameof(columns_json));
        var result = await n8n.CreateDataTableAsync(name, columns, project_id, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Renomear data table", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Renomeia uma data table do n8n. Apenas o nome pode ser alterado — o schema (colunas) é imutável após a criação.")]
    public async Task<string> update_data_table(
        [Description("ID da data table")] string table_id,
        [Description("Novo nome da tabela")] string name,
        CancellationToken ct = default)
    {
        var result = await n8n.UpdateDataTableAsync(table_id, name, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Excluir data table", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Exclui permanentemente uma data table do n8n, incluindo todas as suas colunas e linhas. Recomenda-se chamar backup_data_table antes, pois a exclusão é permanente (o backupFile retornado por backup_data_table pode ser usado com restore_data_table_backup para reverter).")]
    public async Task<string> delete_data_table(
        [Description("ID da data table")] string table_id,
        CancellationToken ct = default)
    {
        await n8n.DeleteDataTableAsync(table_id, ct);
        return new JsonObject { ["deleted"] = true, ["id"] = table_id }.ToJsonString();
    }

    [McpServerTool(Title = "Listar linhas da data table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as linhas de uma data table do n8n. Resposta paginada (verifique nextCursor).")]
    public async Task<string> get_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Quantidade máxima de linhas (padrão 100, máximo 250)")] int limit = 100,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await n8n.GetDataTableRowsAsync(table_id, limit, cursor, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Criar coluna da data table", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Cria uma coluna em uma data table do n8n. Tipos válidos: string, number, boolean, date.")]
    public async Task<string> create_data_table_column(
        [Description("ID da data table")] string table_id,
        [Description("Nome da coluna")] string name,
        [Description("Tipo da coluna: string, number, boolean ou date")] string type,
        CancellationToken ct = default)
    {
        var result = await n8n.CreateDataTableColumnAsync(table_id, name, type, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Excluir coluna da data table", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Exclui uma coluna de uma data table do n8n. Recomenda-se chamar backup_data_table antes, pois a exclusão da coluna e de seus valores é permanente.")]
    public async Task<string> delete_data_table_column(
        [Description("ID da data table")] string table_id,
        [Description("ID da coluna a excluir")] string column_id,
        CancellationToken ct = default)
    {
        var result = await n8n.DeleteDataTableColumnAsync(table_id, column_id, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Inserir linhas na data table", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Insere uma ou mais linhas em uma data table do n8n.")]
    public async Task<string> insert_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Array JSON de objetos a inserir, cada um com os nomes das colunas como chaves")] string rows_json,
        [Description("O que a resposta deve trazer: count (quantidade inserida), id (IDs gerados) ou all (linhas completas)")] string return_type = "all",
        CancellationToken ct = default)
    {
        var rows = ParseArray(rows_json, nameof(rows_json));
        var result = await n8n.InsertDataTableRowsAsync(table_id, rows, return_type, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Atualizar linhas da data table", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Atualiza as linhas de uma data table do n8n que casarem com um filtro. Recomenda-se chamar backup_data_table antes, pois esta ferramenta sobrescreve as linhas existentes.")]
    public async Task<string> update_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Objeto JSON do filtro, ex: {\"type\":\"and\",\"filters\":[{\"columnName\":\"x\",\"condition\":\"eq\",\"value\":\"y\"}]}")] string filter_json,
        [Description("Objeto JSON apenas com as colunas a alterar e seus novos valores")] string data_json,
        [Description("Se true, retorna as linhas atualizadas; se false, retorna apenas confirmação")] bool return_data = true,
        CancellationToken ct = default)
    {
        var filter = ParseObject(filter_json, nameof(filter_json));
        var data = ParseObject(data_json, nameof(data_json));
        var result = await n8n.UpdateDataTableRowsAsync(table_id, filter, data, return_data, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Inserir ou atualizar linhas da data table", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Insere ou atualiza (upsert) linhas de uma data table do n8n: se uma linha casar com o filtro ela é atualizada, senão uma nova linha é inserida com os dados informados.")]
    public async Task<string> upsert_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Objeto JSON do filtro, ex: {\"type\":\"and\",\"filters\":[{\"columnName\":\"x\",\"condition\":\"eq\",\"value\":\"y\"}]}")] string filter_json,
        [Description("Objeto JSON com as colunas e valores a inserir/atualizar")] string data_json,
        [Description("Se true, retorna a linha resultante; se false, retorna apenas confirmação")] bool return_data = true,
        CancellationToken ct = default)
    {
        var filter = ParseObject(filter_json, nameof(filter_json));
        var data = ParseObject(data_json, nameof(data_json));
        var result = await n8n.UpsertDataTableRowsAsync(table_id, filter, data, return_data, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Excluir linhas da data table", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Exclui as linhas de uma data table do n8n que casarem com um filtro. O filtro é obrigatório para evitar exclusão acidental de todos os dados. Recomenda-se chamar backup_data_table antes, pois a exclusão é permanente.")]
    public async Task<string> delete_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Objeto JSON do filtro, ex: {\"type\":\"and\",\"filters\":[{\"columnName\":\"x\",\"condition\":\"eq\",\"value\":\"y\"}]}")] string filter_json,
        [Description("Se true, retorna as linhas excluídas; se false, retorna apenas confirmação")] bool return_data = true,
        CancellationToken ct = default)
    {
        var filter = ParseObject(filter_json, nameof(filter_json));
        var result = await n8n.DeleteDataTableRowsAsync(table_id, filter, return_data, ct);
        return result?.ToJsonString() ?? "{}";
    }

    // ---------- Executions ----------

    [McpServerTool(Title = "Listar execuções", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as execuções de workflows do n8n, com filtros opcionais por workflow e status. Resposta resumida por item (id, status, workflowId, mode, startedAt, stoppedAt e, se houve erro, errorNode/errorMessage); use include_data=true para trazer o payload completo de cada execução. Resposta paginada (verifique nextCursor).")]
    public async Task<string> list_executions(
        [Description("Filtra pelas execuções de um workflow específico (opcional, aceita ID ou URL)")] string? workflow_id = null,
        [Description("Filtra por status: error, success ou waiting (opcional)")] string? status = null,
        [Description("Se true, inclui os dados completos de cada execução na resposta (payload grande)")] bool include_data = false,
        [Description("Quantidade máxima de itens (padrão 100, máximo 250)")] int limit = 100,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null,
        CancellationToken ct = default)
    {
        var id = workflow_id is null ? null : ResolveWorkflowId(workflow_id);
        var result = await n8n.ListExecutionsAsync(id, status, includeData: true, limit, cursor, ct) as JsonObject;
        if (result?["data"] is JsonArray data)
            result["data"] = new JsonArray([.. data.Select(item => (JsonNode)SummarizeExecution(item as JsonObject, include_data))]);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Obter execução", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém os detalhes de uma execução de workflow do n8n. Resposta resumida por padrão (id, status, workflowId, mode, startedAt, stoppedAt e, se houve erro, errorNode/errorMessage); use include_data=true para trazer o payload completo.")]
    public async Task<string> get_execution(
        [Description("ID da execução")] string execution_id,
        [Description("Se true, inclui os dados completos de execução na resposta (payload grande)")] bool include_data = false,
        CancellationToken ct = default)
    {
        var result = await n8n.GetExecutionAsync(execution_id, includeData: true, ct) as JsonObject;
        return SummarizeExecution(result, include_data).ToJsonString();
    }

    [McpServerTool(Title = "Excluir execução", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Exclui uma execução do histórico do n8n.")]
    public async Task<string> delete_execution(
        [Description("ID da execução")] string execution_id,
        CancellationToken ct = default)
    {
        var result = await n8n.DeleteExecutionAsync(execution_id, ct);
        return result?.ToJsonString() ?? "{}";
    }

    // ---------- Tags ----------

    [McpServerTool(Title = "Listar tags", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as tags de workflow da instância n8n. Resposta paginada (verifique nextCursor).")]
    public async Task<string> list_tags(
        [Description("Quantidade máxima de itens (padrão 100, máximo 250)")] int limit = 100,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await n8n.ListTagsAsync(limit, cursor, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Obter tag", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém uma tag do n8n pelo ID.")]
    public async Task<string> get_tag(
        [Description("ID da tag")] string tag_id,
        CancellationToken ct = default)
    {
        var result = await n8n.GetTagAsync(tag_id, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Criar tag", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Cria uma nova tag no n8n.")]
    public async Task<string> create_tag(
        [Description("Nome da tag")] string name,
        CancellationToken ct = default)
    {
        var result = await n8n.CreateTagAsync(name, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Renomear tag", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Renomeia uma tag existente no n8n.")]
    public async Task<string> update_tag(
        [Description("ID da tag")] string tag_id,
        [Description("Novo nome da tag")] string name,
        CancellationToken ct = default)
    {
        var result = await n8n.UpdateTagAsync(tag_id, name, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Excluir tag", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Exclui uma tag do n8n.")]
    public async Task<string> delete_tag(
        [Description("ID da tag")] string tag_id,
        CancellationToken ct = default)
    {
        var result = await n8n.DeleteTagAsync(tag_id, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Title = "Listar tags do workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as tags atribuídas a um workflow do n8n.")]
    public async Task<string> get_workflow_tags(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var result = await n8n.GetWorkflowTagsAsync(id, ct);
        return result?.ToJsonString() ?? "[]";
    }

    [McpServerTool(Title = "Definir tags do workflow", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Define o conjunto de tags de um workflow do n8n, substituindo as tags atuais pelas informadas.")]
    public async Task<string> set_workflow_tags(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("IDs das tags a atribuir, separados por vírgula")] string tag_ids,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var ids = tag_ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await n8n.SetWorkflowTagsAsync(id, ids, ct);
        return result?.ToJsonString() ?? "[]";
    }

    [McpServerTool(Title = "Definir tags do workflow por nome", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Define as tags de um workflow do n8n a partir dos nomes informados: resolve cada nome para o ID da tag existente (paginando list_tags) ou cria a tag automaticamente se não existir, e então substitui as tags do workflow pelas resolvidas. Evita ter que listar/criar tags manualmente antes de set_workflow_tags.")]
    public async Task<string> set_workflow_tags_by_name(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nomes das tags a atribuir, separados por vírgula")] string tag_names,
        [Description("Se true (padrão), cria automaticamente as tags que ainda não existirem; se false, falha caso alguma não exista")] bool create_missing = true,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var names = tag_names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        do
        {
            var page = await n8n.ListTagsAsync(250, cursor, ct) as JsonObject;
            foreach (var tag in (page?["data"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var tagName = tag["name"]?.GetValue<string>();
                var tagId = tag["id"]?.GetValue<string>();
                if (tagName is not null && tagId is not null)
                    existing[tagName] = tagId;
            }
            cursor = page?["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor));

        var resolvedIds = new List<string>();
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (existing.TryGetValue(name, out var tagId))
                resolvedIds.Add(tagId);
            else
                missing.Add(name);
        }

        if (missing.Count > 0 && !create_missing)
            throw new InvalidOperationException($"Tag(s) não encontrada(s): {string.Join(", ", missing)}. Use create_missing=true para criá-las automaticamente.");

        foreach (var name in missing)
        {
            var created = await n8n.CreateTagAsync(name, ct) as JsonObject;
            var createdId = created?["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"Falha ao criar a tag '{name}'.");
            resolvedIds.Add(createdId);
        }

        var result = await n8n.SetWorkflowTagsAsync(id, resolvedIds, ct);
        return result?.ToJsonString() ?? "[]";
    }

    // ---------- Backups ----------

    [McpServerTool(Title = "Fazer backup do workflow", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Faz um backup de um workflow do n8n. Deve ser chamado antes de qualquer ferramenta que altere ou exclua um workflow existente (update_workflow, delete_workflow, update_workflow_node, add_workflow_node, delete_workflow_node, rename_workflow_node). Salva no servidor e aparece em list_backups. Retorna backupFile (nome do arquivo, usado para restaurar) e backupUrl (link para baixar o conteúdo).")]
    public async Task<string> backup_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var backupFile = await BackupWorkflowAsync(id, ct);
        return new JsonObject { ["workflowId"] = id, ["backupFile"] = backupFile, ["backupUrl"] = BuildBackupUrl(backupFile) }.ToJsonString();
    }

    [McpServerTool(Title = "Fazer backup da data table", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Faz um backup de uma data table do n8n (colunas + todas as linhas). Deve ser chamado antes de qualquer ferramenta que altere ou exclua uma data table existente (delete_data_table, delete_data_table_column, update_data_table_rows, delete_data_table_rows). Salva no servidor e aparece em list_backups. Retorna backupFile (nome do arquivo, usado para restaurar) e backupUrl (link para baixar o conteúdo).")]
    public async Task<string> backup_data_table(
        [Description("ID da data table")] string table_id,
        CancellationToken ct = default)
    {
        var backupFile = await BackupDataTableAsync(table_id, ct);
        return new JsonObject { ["tableId"] = table_id, ["backupFile"] = backupFile, ["backupUrl"] = BuildBackupUrl(backupFile) }.ToJsonString();
    }

    [McpServerTool(Title = "Listar backups", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os arquivos de backup (workflows e data tables) salvos pelo servidor via backup_workflow/backup_data_table, com nome, data, tamanho e link de download (downloadUrl).")]
    public string list_backups(
        [Description("Filtra pelo nome do arquivo (opcional, substring, sem distinção de maiúsculas/minúsculas)")] string? filter = null)
    {
        var items = backups.ListBackups(filter);
        var array = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject
        {
            ["fileName"] = i.FileName,
            ["modifiedAt"] = i.ModifiedUtc.ToString("O"),
            ["sizeBytes"] = i.SizeBytes,
            ["downloadUrl"] = BuildBackupUrl(i.FileName)
        })]);
        return array.ToJsonString();
    }

    [McpServerTool(Title = "Excluir backups", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Exclui permanentemente um ou mais arquivos de backup salvos no servidor. A exclusão é definitiva e não afeta o n8n (apenas os arquivos de backup) — confirme com o usuário antes de chamar. Use list_backups para descobrir os nomes dos arquivos. Retorna os arquivos excluídos e os que não foram encontrados.")]
    public string delete_backup(
        [Description("Nome do arquivo de backup, ou vários separados por vírgula, ex: 20260711-153000-workflow-MeuFluxo-abc123.json")] string backup_files)
    {
        var names = backup_files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("Informe ao menos um nome de arquivo de backup.");

        var deleted = new List<string>();
        var notFound = new List<string>();
        foreach (var name in names)
        {
            if (backups.DeleteBackup(name))
                deleted.Add(name);
            else
                notFound.Add(name);
        }

        return new JsonObject
        {
            ["deletedCount"] = deleted.Count,
            ["deleted"] = new JsonArray([.. deleted.Select(n => (JsonNode)n)]),
            ["notFound"] = new JsonArray([.. notFound.Select(n => (JsonNode)n)])
        }.ToJsonString();
    }

    [McpServerTool(Title = "Restaurar backup de workflow", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Restaura um workflow do n8n a partir de um arquivo de backup (o nome do arquivo vem do campo backupFile retornado por backup_workflow — não é necessário baixar o arquivo). Se o workflow ainda existir, sobrescreve seu conteúdo; se tiver sido excluído, recria um novo workflow com o mesmo conteúdo (nesse caso o ID muda). Retorna um resumo compacto do workflow restaurado.")]
    public async Task<string> restore_workflow_backup(
        [Description("Nome do arquivo de backup, ex: 20260710-153000-workflow-abc123.json")] string backup_file,
        CancellationToken ct = default)
    {
        var backup = await backups.ReadBackupAsync(backup_file, ct) as JsonObject
            ?? throw new InvalidOperationException($"O backup '{backup_file}' não contém um workflow válido.");

        var workflowId = backup["id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            try
            {
                var updated = await n8n.UpdateWorkflowAsync(
                    workflowId,
                    backup["name"]?.GetValue<string>(),
                    backup["nodes"]?.DeepClone() as JsonArray,
                    backup["connections"]?.DeepClone() as JsonObject,
                    backup["settings"]?.DeepClone() as JsonObject,
                    ct) as JsonObject;
                return BuildWorkflowChangeSummary(updated).ToJsonString();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("404"))
            {
                // Workflow não existe mais (foi excluído) — recria abaixo.
            }
        }

        var created = await n8n.CreateWorkflowAsync(backup, ct) as JsonObject;
        return BuildWorkflowChangeSummary(created).ToJsonString();
    }

    [McpServerTool(Title = "Restaurar backup de data table", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Restaura uma data table do n8n a partir de um arquivo de backup (colunas + linhas), substituindo totalmente a tabela pelo estado do backup — inclusive se a tabela ainda existir, ela é excluída e recriada (o ID muda sempre). O nome do arquivo vem do campo backupFile retornado por backup_data_table — não é necessário baixar o arquivo.")]
    public async Task<string> restore_data_table_backup(
        [Description("Nome do arquivo de backup, ex: 20260711-153000-datatable-Clientes-abc123.json")] string backup_file,
        CancellationToken ct = default)
    {
        var backup = await backups.ReadBackupAsync(backup_file, ct) as JsonObject
            ?? throw new InvalidOperationException($"O backup '{backup_file}' não contém uma data table válida.");
        var table = backup["table"] as JsonObject
            ?? throw new InvalidOperationException($"O backup '{backup_file}' não contém dados de tabela.");
        var rows = backup["rows"] as JsonArray ?? [];

        var tableId = table["id"]?.GetValue<string>();
        var name = table["name"]?.GetValue<string>() ?? "Restaurada";
        var columns = new JsonArray([.. (table["columns"] as JsonArray ?? [])
            .Select(c => (JsonNode)new JsonObject { ["name"] = c?["name"]?.DeepClone(), ["type"] = c?["type"]?.DeepClone() })]);

        if (!string.IsNullOrWhiteSpace(tableId))
        {
            try
            {
                await n8n.DeleteDataTableAsync(tableId, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("404"))
            {
                // Tabela já não existe — segue para recriar.
            }
        }

        var created = await n8n.CreateDataTableAsync(name, columns, null, ct) as JsonObject;
        var newTableId = created?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Falha ao recriar a data table a partir do backup.");

        const int batchSize = 250;
        var rowsToInsert = rows.OfType<JsonObject>().Select(StripRowMetadata).ToList();
        var inserted = 0;
        for (var i = 0; i < rowsToInsert.Count; i += batchSize)
        {
            var batch = new JsonArray([.. rowsToInsert.Skip(i).Take(batchSize).Select(r => (JsonNode)r.DeepClone())]);
            await n8n.InsertDataTableRowsAsync(newTableId, batch, "count", ct);
            inserted += batch.Count;
        }

        return new JsonObject
        {
            ["tableId"] = newTableId,
            ["name"] = name,
            ["columnsRestored"] = columns.Count,
            ["rowsRestored"] = inserted
        }.ToJsonString();
    }
}
