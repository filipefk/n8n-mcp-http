using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class BackupTools(McpClient n8n, BackupService backups, IOptions<McpOptions> options, IRequestContext requestContext)
{
    private string ResolveBaseUrl()
    {
        var baseUrl = options.Value.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = requestContext.GetRequestBaseUrl()
                ?? throw new InvalidOperationException("Não foi possível determinar a URL pública do servidor (nenhuma requisição HTTP ativa e N8n:PublicBaseUrl não configurado).");
        return baseUrl.TrimEnd('/');
    }

    private string BuildBackupsBaseUrl() => $"{ResolveBaseUrl()}/backups/";

    private string BuildBackupUrl(string fileName) => $"{BuildBackupsBaseUrl()}{Uri.EscapeDataString(fileName)}";

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
        string? previousCursor = null;
        for (var page = 0; ; page++)
        {
            GuardPagination(page, cursor, previousCursor);
            var result = await n8n.GetDataTableRowsAsync(tableId, 250, cursor, ct) as JsonObject;
            if (result?["data"] is JsonArray data)
                foreach (var row in data)
                    rows.Add(row?.DeepClone());
            previousCursor = cursor;
            cursor = result?["nextCursor"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return rows;
    }

    private async Task<string> BackupDataTableAsync(string tableId, CancellationToken ct)
    {
        var table = await n8n.GetDataTableAsync(tableId, ct);
        var name = (table as JsonObject)?["name"]?.GetValue<string>();
        var rows = await FetchAllDataTableRowsAsync(tableId, ct);
        return await backups.SaveDataTableBackupAsync(tableId, name, table, rows, ct);
    }

    [McpServerTool(Title = "Fazer backup do workflow", Destructive = false, OpenWorld = false), Description("Faz um backup de um workflow do n8n. Salva no servidor e aparece em list_backups. Retorna backupFile (nome do arquivo, usado para restaurar) e backupUrl (link para baixar o conteúdo).")]
    public async Task<string> backup_workflow(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var backupFile = await BackupWorkflowAsync(id, ct);
        return new JsonObject { ["workflowId"] = id, ["backupFile"] = backupFile, ["backupUrl"] = BuildBackupUrl(backupFile) }.ToJsonString();
    }

    [McpServerTool(Title = "Fazer backup da data table", Destructive = false, OpenWorld = false), Description("Faz um backup de uma data table do n8n (colunas + todas as linhas). Salva no servidor e aparece em list_backups. Retorna backupFile (nome do arquivo, usado para restaurar) e backupUrl (link para baixar o conteúdo).")]
    public async Task<string> backup_data_table(
        [Description("ID da data table")] string table_id,
        CancellationToken ct = default)
    {
        var backupFile = await BackupDataTableAsync(table_id, ct);
        return new JsonObject { ["tableId"] = table_id, ["backupFile"] = backupFile, ["backupUrl"] = BuildBackupUrl(backupFile) }.ToJsonString();
    }

    [McpServerTool(Title = "Listar backups", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista os arquivos de backup (workflows e data tables) salvos pelo servidor, mais recentes primeiro. Retorna baseUrl uma vez (concatene com fileName para baixar) em vez de repetir a URL completa por item. Resposta paginada (verifique nextCursor).")]
    public string list_backups(
        [Description("Filtra pelo nome do arquivo (opcional, substring, sem distinção de maiúsculas/minúsculas)")] string? filter = null,
        [Description("Quantidade máxima de itens (padrão 50, máximo 250)")] int limit = 50,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null)
    {
        var items = backups.ListBackups(filter);
        var startIndex = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            var idx = items.Select((item, i) => (item, i)).FirstOrDefault(x => x.item.FileName == cursor).i;
            startIndex = items.Any(i => i.FileName == cursor) ? idx + 1 : 0;
        }

        var effectiveLimit = Math.Clamp(limit, 1, 250);
        var page = items.Skip(startIndex).Take(effectiveLimit).ToList();
        var nextCursor = startIndex + page.Count < items.Count ? page[^1].FileName : null;

        var array = new JsonArray([.. page.Select(i => (JsonNode)new JsonObject
        {
            ["fileName"] = i.FileName,
            ["modifiedAt"] = i.ModifiedUtc.ToString("O"),
            ["sizeBytes"] = i.SizeBytes
        })]);

        return new JsonObject
        {
            ["baseUrl"] = BuildBackupsBaseUrl(),
            ["data"] = array,
            ["nextCursor"] = nextCursor
        }.ToJsonString();
    }

    [McpServerTool(Title = "Excluir backups", Idempotent = true, OpenWorld = false), Description("Exclui permanentemente um ou mais arquivos de backup salvos no servidor (não afeta o n8n). Use list_backups para descobrir os nomes. Retorna os arquivos excluídos e os que não foram encontrados.")]
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

    [McpServerTool(Title = "Restaurar backup de workflow", OpenWorld = false), Description("Restaura um workflow a partir de um arquivo de backup (nome vindo de backupFile). Se o workflow ainda existir, sobrescreve seu conteúdo; se foi excluído, recria um novo (o ID muda). Retorna um resumo compacto do workflow restaurado.")]
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
            catch (N8nApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Workflow não existe mais (foi excluído) — recria abaixo.
            }
        }

        var created = await n8n.CreateWorkflowAsync(backup, ct) as JsonObject;
        return BuildWorkflowChangeSummary(created).ToJsonString();
    }

    [McpServerTool(Title = "Restaurar backup de data table", OpenWorld = false), Description("Restaura uma data table a partir de um arquivo de backup (colunas + linhas), substituindo totalmente a tabela pelo estado do backup (o ID muda sempre). Nome do arquivo vindo de backupFile.")]
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

        // Cria a tabela nova e reinsere as linhas ANTES de excluir a antiga: se a criação ou a
        // reinserção falhar, a tabela original (se ainda existir) permanece intacta em vez de já
        // ter sido apagada sem chance de reverter.
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

        if (!string.IsNullOrWhiteSpace(tableId))
        {
            try
            {
                await n8n.DeleteDataTableAsync(tableId, ct);
            }
            catch (N8nApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Tabela já não existia — segue normalmente.
            }
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
