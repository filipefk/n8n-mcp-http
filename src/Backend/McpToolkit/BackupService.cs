using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text;
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

    public Task<string> SaveWorkflowBackupAsync(string workflowId, string? name, JsonNode? workflow, CancellationToken ct = default) =>
        SaveAsync(BuildSuffix("workflow", workflowId, name), workflow, ct);

    public Task<string> SaveDataTableBackupAsync(string tableId, string? name, JsonNode? table, JsonArray rows, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["table"] = table?.DeepClone(),
            ["rows"] = rows
        };
        return SaveAsync(BuildSuffix("datatable", tableId, name), payload, ct);
    }

    private async Task<string> SaveAsync(string suffix, JsonNode? content, CancellationToken ct)
    {
        // Precisão de segundo colidia em silêncio quando dois backups do mesmo item eram feitos
        // no mesmo segundo (o segundo sobrescrevia o primeiro). Milissegundos reduzem a janela;
        // o sufixo aleatório garante unicidade mesmo se dois backups caírem no mesmo milissegundo.
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{suffix}-{Guid.NewGuid().ToString("N")[..6]}.json";
        var path = Path.Combine(_backupDir, fileName);
        await File.WriteAllTextAsync(path, content?.ToJsonString() ?? "{}", ct);
        return fileName;
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

    public string? ResolveBackupPath(string fileName)
    {
        var path = Path.Combine(_backupDir, Path.GetFileName(fileName));
        return File.Exists(path) ? path : null;
    }

    public bool DeleteBackup(string fileName)
    {
        var path = Path.Combine(_backupDir, Path.GetFileName(fileName));
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    private static string BuildSuffix(string kind, string id, string? name)
    {
        var idPart = SanitizeForFileName(id);
        var namePart = string.IsNullOrWhiteSpace(name) ? null : SanitizeForFileName(name);
        return string.IsNullOrEmpty(namePart) ? $"{kind}-{idPart}" : $"{kind}-{namePart}-{idPart}";
    }

    private static string SanitizeForFileName(string value)
    {
        const int maxLength = 40;
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasUnderscore = false;
        foreach (var c in value)
        {
            var isSafe = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
            if (isSafe)
            {
                builder.Append(c);
                lastWasUnderscore = c == '_';
            }
            else if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }

            if (builder.Length >= maxLength)
                break;
        }

        return builder.ToString().Trim('_');
    }
}
