using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace McpToolkit;

public sealed class BackupService
{
    private readonly string _backupDir;

    public BackupService(IHostEnvironment env, IOptions<McpOptions> options)
    {
        var configured = options.Value.BackupDir;
        _backupDir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "backups")
            : configured;
        Directory.CreateDirectory(_backupDir);
    }

    public Task<string> SaveWorkflowBackupAsync(string workflowId, JsonNode? workflow, CancellationToken ct = default) =>
        SaveAsync($"workflow-{SanitizeIdForFileName(workflowId)}", workflow, ct);

    public Task<string> SaveDataTableBackupAsync(string tableId, JsonNode? table, JsonArray rows, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["table"] = table?.DeepClone(),
            ["rows"] = rows
        };
        return SaveAsync($"datatable-{SanitizeIdForFileName(tableId)}", payload, ct);
    }

    private async Task<string> SaveAsync(string suffix, JsonNode? content, CancellationToken ct)
    {
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{suffix}.json";
        var path = Path.Combine(_backupDir, fileName);
        await File.WriteAllTextAsync(path, content?.ToJsonString() ?? "{}", ct);
        return path;
    }

    public IReadOnlyList<(string FileName, DateTime ModifiedUtc, long SizeBytes)> ListBackups(string? filter)
    {
        IEnumerable<string> files = Directory.EnumerateFiles(_backupDir, "*.json");
        if (!string.IsNullOrWhiteSpace(filter))
            files = files.Where(f => Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase));

        return [.. files
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Select(fi => (fi.Name, fi.LastWriteTimeUtc, fi.Length))];
    }

    public async Task<JsonNode?> ReadBackupAsync(string fileName, CancellationToken ct = default)
    {
        var path = Path.Combine(_backupDir, Path.GetFileName(fileName));
        if (!File.Exists(path))
            throw new InvalidOperationException($"Arquivo de backup '{fileName}' não encontrado.");

        var content = await File.ReadAllTextAsync(path, ct);
        return JsonNode.Parse(content);
    }

    private static string SanitizeIdForFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
