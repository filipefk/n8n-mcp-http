using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class TagTools(McpClient n8n)
{
    [McpServerTool(Title = "Listar tags", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as tags de workflow da instância n8n. Resposta paginada (verifique nextCursor).")]
    public async Task<string> list_tags(
        [Description("Quantidade máxima de itens (padrão 100, máximo 250)")] int limit = 100,
        [Description("Cursor de paginação retornado por uma chamada anterior (opcional)")] string? cursor = null,
        CancellationToken ct = default) =>
        Json(await n8n.ListTagsAsync(limit, cursor, ct));

    [McpServerTool(Title = "Obter tag", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Obtém uma tag do n8n pelo ID.")]
    public async Task<string> get_tag(
        [Description("ID da tag")] string tag_id,
        CancellationToken ct = default) =>
        Json(await n8n.GetTagAsync(tag_id, ct));

    [McpServerTool(Title = "Criar tag", Destructive = false, OpenWorld = false), Description("Cria uma nova tag no n8n.")]
    public async Task<string> create_tag(
        [Description("Nome da tag")] string name,
        CancellationToken ct = default) =>
        Json(await n8n.CreateTagAsync(name, ct));

    [McpServerTool(Title = "Renomear tag", Destructive = false, Idempotent = true, OpenWorld = false), Description("Renomeia uma tag existente no n8n.")]
    public async Task<string> update_tag(
        [Description("ID da tag")] string tag_id,
        [Description("Novo nome da tag")] string name,
        CancellationToken ct = default) =>
        Json(await n8n.UpdateTagAsync(tag_id, name, ct));

    [McpServerTool(Title = "Excluir tag", Idempotent = true, OpenWorld = false), Description("Exclui uma tag do n8n.")]
    public async Task<string> delete_tag(
        [Description("ID da tag")] string tag_id,
        CancellationToken ct = default) =>
        Json(await n8n.DeleteTagAsync(tag_id, ct));

    [McpServerTool(Title = "Listar tags do workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lista as tags atribuídas a um workflow do n8n.")]
    public async Task<string> get_workflow_tags(
        [Description("ID ou URL do workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        return Json(await n8n.GetWorkflowTagsAsync(id, ct), "[]");
    }

    [McpServerTool(Title = "Definir tags do workflow", Idempotent = true, OpenWorld = false), Description("Define as tags de um workflow a partir dos nomes informados: resolve cada nome para o ID da tag existente ou cria a tag automaticamente se não existir, e substitui as tags do workflow pelas resolvidas.")]
    public async Task<string> set_workflow_tags(
        [Description("ID ou URL do workflow")] string workflow_id,
        [Description("Nomes das tags a atribuir, separados por vírgula")] string tag_names,
        [Description("Se true (padrão), cria automaticamente as tags que ainda não existirem; se false, falha caso alguma não exista")] bool create_missing = true,
        CancellationToken ct = default)
    {
        var id = ResolveWorkflowId(workflow_id);
        var names = tag_names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        string? previousCursor = null;
        for (var page = 0; ; page++)
        {
            GuardPagination(page, cursor, previousCursor);
            var result = await n8n.ListTagsAsync(250, cursor, ct) as JsonObject;
            foreach (var tag in (result?["data"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var tagName = tag["name"]?.GetValue<string>();
                var tagId = tag["id"]?.GetValue<string>();
                if (tagName is not null && tagId is not null)
                    existing[tagName] = tagId;
            }
            previousCursor = cursor;
            cursor = result?["nextCursor"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cursor)) break;
        }

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

        return Json(await n8n.SetWorkflowTagsAsync(id, resolvedIds, ct), "[]");
    }
}
