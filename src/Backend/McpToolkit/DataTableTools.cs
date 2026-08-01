using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class DataTableTools(McpClient n8n)
{
    [McpServerTool(Title = "Listar data tables", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as data tables do n8n. Se name for informado, pagina automaticamente por todas as tabelas do servidor e retorna só as que casarem (substring, sem distinção de maiúsculas/minúsculas); sem name, retorna uma única página (verifique nextCursor).")]
    public async Task<string> list_data_tables(
        [Description("Filtra pelo nome da tabela (opcional, substring, sem distinção de maiúsculas/minúsculas; busca em todas as páginas)")] string? name = null,
        [Description("Cursor de paginação retornado por uma chamada anterior; ignorado quando name é informado (opcional)")] string? cursor = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var page = await n8n.ListDataTablesAsync(cursor, ct);
            return Json(page);
        }

        var matches = new JsonArray();
        string? pageCursor = null;
        string? previousCursor = null;
        for (var page = 0; ; page++)
        {
            GuardPagination(page, pageCursor, previousCursor);
            var result = await n8n.ListDataTablesAsync(pageCursor, ct) as JsonObject;
            foreach (var item in (result?["data"] as JsonArray ?? []).OfType<JsonObject>())
                if ((item["name"]?.GetValue<string>() ?? "").Contains(name, StringComparison.OrdinalIgnoreCase))
                    matches.Add(item.DeepClone());

            previousCursor = pageCursor;
            pageCursor = result?["nextCursor"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageCursor)) break;
        }

        return new JsonObject { ["data"] = matches, ["nextCursor"] = null }.ToJsonString();
    }

    [McpServerTool(Title = "Obter data table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém uma data table do n8n, incluindo suas colunas (nome, tipo, índice).")]
    public async Task<string> get_data_table(
        [Description("ID da data table")] string table_id,
        CancellationToken ct = default) =>
        Json(await n8n.GetDataTableAsync(table_id, ct));

    [McpServerTool(Title = "Criar data table", Destructive = false, OpenWorld = false), Description("Cria uma nova data table no n8n. É necessário informar ao menos uma coluna — o schema é imutável após a criação (exceto via create/delete_data_table_column).")]
    public async Task<string> create_data_table(
        [Description("Nome da tabela")] string name,
        [Description("Colunas, ex: [{\"name\":\"email\",\"type\":\"string\"}]. Tipos válidos: string, number, boolean, date")] JsonArray columns,
        [Description("ID do projeto onde criar a tabela (opcional; padrão: projeto default)")] string? project_id = null,
        CancellationToken ct = default) =>
        Json(await n8n.CreateDataTableAsync(name, columns, project_id, ct));

    [McpServerTool(Title = "Renomear data table", Destructive = false, Idempotent = true, OpenWorld = false), Description("Renomeia uma data table. Apenas o nome pode ser alterado — o schema (colunas) é imutável após a criação.")]
    public async Task<string> update_data_table(
        [Description("ID da data table")] string table_id,
        [Description("Novo nome da tabela")] string name,
        CancellationToken ct = default) =>
        Json(await n8n.UpdateDataTableAsync(table_id, name, ct));

    [McpServerTool(Title = "Excluir data table", Idempotent = true, OpenWorld = false), Description("Exclui permanentemente uma data table do n8n, incluindo todas as suas colunas e linhas.")]
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
        CancellationToken ct = default) =>
        Json(await n8n.GetDataTableRowsAsync(table_id, limit, cursor, ct));

    [McpServerTool(Title = "Criar coluna da data table", Destructive = false, OpenWorld = false), Description("Cria uma coluna em uma data table do n8n. Tipos válidos: string, number, boolean, date.")]
    public async Task<string> create_data_table_column(
        [Description("ID da data table")] string table_id,
        [Description("Nome da coluna")] string name,
        [Description("Tipo da coluna: string, number, boolean ou date")] string type,
        CancellationToken ct = default) =>
        Json(await n8n.CreateDataTableColumnAsync(table_id, name, type, ct));

    [McpServerTool(Title = "Excluir coluna da data table", Idempotent = true, OpenWorld = false), Description("Exclui uma coluna de uma data table do n8n. A exclusão da coluna e de seus valores é permanente.")]
    public async Task<string> delete_data_table_column(
        [Description("ID da data table")] string table_id,
        [Description("ID da coluna a excluir")] string column_id,
        CancellationToken ct = default) =>
        Json(await n8n.DeleteDataTableColumnAsync(table_id, column_id, ct));

    [McpServerTool(Title = "Inserir linhas na data table", Destructive = false, OpenWorld = false), Description("Insere uma ou mais linhas em uma data table do n8n.")]
    public async Task<string> insert_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Objetos a inserir, cada um com os nomes das colunas como chaves")] JsonArray rows,
        [Description("O que a resposta deve trazer: count (quantidade inserida), id (IDs gerados) ou all (linhas completas)")] string return_type = "all",
        CancellationToken ct = default) =>
        Json(await n8n.InsertDataTableRowsAsync(table_id, rows, return_type, ct));

    [McpServerTool(Title = "Atualizar ou inserir linhas da data table", Idempotent = true, OpenWorld = false), Description("Atualiza as linhas que casarem com um filtro; com upsert=true, insere uma nova linha com os dados informados quando nenhuma casar. Filtro ex: " + FilterJsonExample)]
    public async Task<string> update_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Filtro para selecionar as linhas")] JsonObject filter,
        [Description("Colunas e valores a alterar/inserir")] JsonObject data,
        [Description("Se true, insere uma linha nova quando nenhuma casar com o filtro (upsert); se false, só atualiza as existentes")] bool upsert = false,
        [Description("Se true, retorna as linhas resultantes; se false, retorna apenas confirmação")] bool return_data = true,
        CancellationToken ct = default)
    {
        var result = upsert
            ? await n8n.UpsertDataTableRowsAsync(table_id, filter, data, return_data, ct)
            : await n8n.UpdateDataTableRowsAsync(table_id, filter, data, return_data, ct);
        return Json(result);
    }

    [McpServerTool(Title = "Excluir linhas da data table", Idempotent = true, OpenWorld = false), Description("Exclui as linhas que casarem com um filtro (obrigatório, para evitar exclusão acidental de todos os dados). Filtro ex: " + FilterJsonExample)]
    public async Task<string> delete_data_table_rows(
        [Description("ID da data table")] string table_id,
        [Description("Filtro para selecionar as linhas a excluir")] JsonObject filter,
        [Description("Se true, retorna as linhas excluídas; se false, retorna apenas confirmação")] bool return_data = true,
        CancellationToken ct = default) =>
        Json(await n8n.DeleteDataTableRowsAsync(table_id, filter, return_data, ct));
}
