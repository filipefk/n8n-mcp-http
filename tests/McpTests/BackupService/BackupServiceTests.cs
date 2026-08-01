using McpTests.Fakes;
using System.Text.Json.Nodes;

namespace McpTests.BackupService;

public class BackupServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mcptests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_CreatesBackupDirectory_WhenMissing()
    {
        var backupDir = Path.Combine(_tempDir, "nested", "backups");
        TestFactories.BuildBackupService(backupDir);

        Assert.True(Directory.Exists(backupDir));
    }

    [Fact]
    public void Constructor_FallsBackToContentRootPathBackups_WhenBackupDirEmpty()
    {
        Directory.CreateDirectory(_tempDir);
        var env = new FakeHostEnvironment { ContentRootPath = _tempDir };
        var options = Microsoft.Extensions.Options.Options.Create(new McpToolkit.McpOptions { BackupDir = "" });
        _ = new McpToolkit.BackupService(env, options);

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "backups")));
    }

    [Fact]
    public async Task SaveWorkflowBackupAsync_WritesJsonFile_WithTimestampedNameContainingWorkflowIdAndName()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var workflow = new JsonObject { ["id"] = "wf1", ["name"] = "Meu WF" };

        var fileName = await service.SaveWorkflowBackupAsync("wf1", "Meu WF", workflow);
        var path = Path.Combine(_tempDir, fileName);

        Assert.True(File.Exists(path));
        Assert.Matches(@"\d{8}-\d{9}-workflow-Meu_WF-wf1-[0-9a-f]{6}\.json$", fileName);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal(workflow.ToJsonString(), JsonNode.Parse(content)!.ToJsonString());
    }

    [Fact]
    public async Task SaveWorkflowBackupAsync_OmitsNameSegment_WhenNameIsNullOrWhitespace()
    {
        var service = TestFactories.BuildBackupService(_tempDir);

        var fileName = await service.SaveWorkflowBackupAsync("wf1", null, new JsonObject());

        Assert.Matches(@"\d{8}-\d{9}-workflow-wf1-[0-9a-f]{6}\.json$", fileName);
    }

    [Fact]
    public async Task SaveWorkflowBackupAsync_SanitizesInvalidFileNameCharsInWorkflowId()
    {
        var service = TestFactories.BuildBackupService(_tempDir);

        var fileName = await service.SaveWorkflowBackupAsync("wf/with:invalid*chars", null, new JsonObject());

        Assert.Contains("workflow-wf_with_invalid_chars-", fileName);
    }

    [Fact]
    public async Task SaveWorkflowBackupAsync_SanitizesUrlUnsafeCharsInName_KeepingOnlyAlphanumericDashUnderscore()
    {
        var service = TestFactories.BuildBackupService(_tempDir);

        var fileName = await service.SaveWorkflowBackupAsync("wf1", "Fluxo #1 (teste)? café & açúcar", new JsonObject());

        Assert.DoesNotContain(' ', fileName);
        Assert.DoesNotContain('#', fileName);
        Assert.DoesNotContain('?', fileName);
        Assert.DoesNotContain('&', fileName);
        Assert.DoesNotContain('(', fileName);
        Assert.DoesNotContain(')', fileName);
        // O arquivo vira parte de uma URL de download — precisa ser seguro sem depender de encode/decode.
        Assert.Equal(fileName, Uri.EscapeDataString(fileName));
    }

    [Fact]
    public async Task SaveDataTableBackupAsync_WritesTableAndRowsUnderSeparateKeys()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var table = new JsonObject { ["id"] = "t1" };
        var rows = new JsonArray { new JsonObject { ["col"] = "val" } };

        var fileName = await service.SaveDataTableBackupAsync("t1", "Minha Tabela", table, rows);

        var content = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_tempDir, fileName)))!.AsObject();
        Assert.Equal("t1", content["table"]!["id"]!.GetValue<string>());
        Assert.Single(content["rows"]!.AsArray());
        Assert.Contains("Minha_Tabela", fileName);
    }

    [Fact]
    public async Task ListBackups_OrdersByLastWriteTimeDescending()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var older = await service.SaveWorkflowBackupAsync("a", null, new JsonObject());
        var newer = await service.SaveWorkflowBackupAsync("b", null, new JsonObject());
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, older), DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, newer), DateTime.UtcNow);

        var list = service.ListBackups(null);

        Assert.Equal(newer, list[0].FileName);
        Assert.Equal(older, list[1].FileName);
    }

    [Fact]
    public async Task ListBackups_FiltersBySubstringCaseInsensitive()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        await service.SaveWorkflowBackupAsync("wf1", null, new JsonObject());
        await service.SaveDataTableBackupAsync("dt1", null, new JsonObject(), new JsonArray());

        var list = service.ListBackups("DATATABLE");

        Assert.Single(list);
        Assert.Contains("datatable-dt1", list[0].FileName);
    }

    [Fact]
    public async Task ReadBackupAsync_ReturnsParsedJson_ForExistingFile()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var fileName = await service.SaveWorkflowBackupAsync("wf1", null, new JsonObject { ["name"] = "X" });

        var result = await service.ReadBackupAsync(fileName);

        Assert.Equal("X", result!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadBackupAsync_Throws_WhenFileMissing()
    {
        var service = TestFactories.BuildBackupService(_tempDir);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadBackupAsync("nao-existe.json"));
        Assert.Contains("nao-existe.json", ex.Message);
    }

    [Fact]
    public async Task ReadBackupAsync_StripsDirectoryTraversal_TreatsPathAsFileNameOnly()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var fileName = await service.SaveWorkflowBackupAsync("wf1", null, new JsonObject { ["name"] = "X" });

        var result = await service.ReadBackupAsync($"../../{fileName}");

        Assert.Equal("X", result!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResolveBackupPath_ReturnsPhysicalPath_ForExistingFile()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var fileName = await service.SaveWorkflowBackupAsync("wf1", null, new JsonObject());

        var path = service.ResolveBackupPath(fileName);

        Assert.Equal(Path.Combine(_tempDir, fileName), path);
    }

    [Fact]
    public void ResolveBackupPath_ReturnsNull_WhenFileMissing()
    {
        var service = TestFactories.BuildBackupService(_tempDir);

        Assert.Null(service.ResolveBackupPath("nao-existe.json"));
    }

    [Fact]
    public async Task ResolveBackupPath_StripsDirectoryTraversal()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var fileName = await service.SaveWorkflowBackupAsync("wf1", null, new JsonObject());

        var path = service.ResolveBackupPath($"../../{fileName}");

        Assert.Equal(Path.Combine(_tempDir, fileName), path);
    }

    [Fact]
    public async Task DeleteBackup_RemovesFileAndReturnsTrue_WhenFileExists()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var fileName = await service.SaveWorkflowBackupAsync("wf1", null, new JsonObject());

        var result = service.DeleteBackup(fileName);

        Assert.True(result);
        Assert.False(File.Exists(Path.Combine(_tempDir, fileName)));
    }

    [Fact]
    public void DeleteBackup_ReturnsFalse_WhenFileMissing()
    {
        var service = TestFactories.BuildBackupService(_tempDir);

        Assert.False(service.DeleteBackup("nao-existe.json"));
    }

    [Fact]
    public async Task DeleteBackup_StripsDirectoryTraversal()
    {
        var service = TestFactories.BuildBackupService(_tempDir);
        var outsideDir = Path.Combine(_tempDir, "..", "mcptests-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.json");
        await File.WriteAllTextAsync(outsideFile, "{}");

        try
        {
            var result = service.DeleteBackup($"../{Path.GetFileName(outsideDir)}/secret.json");

            Assert.False(result);
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }
}
