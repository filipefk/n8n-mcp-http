using McpToolkit;
using McpToolkit.Shell;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McpTests.Fakes;

public sealed class FakeHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "McpTests";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public sealed class FakeRequestContext(string? headerUrl = null, string? headerApiKey = null, string? baseUrl = null) : IRequestContext
{
    public string? GetHeader(string name) => name switch
    {
        "X-N8N-URL" => headerUrl,
        "X-N8N-API-KEY" => headerApiKey,
        _ => null
    };

    public string? GetRequestBaseUrl() => baseUrl;
}

public static class TestFactories
{
    public static IRequestContext BuildRequestContext(string? headerUrl = null, string? headerApiKey = null, string? baseUrl = null) =>
        new FakeRequestContext(headerUrl, headerApiKey, baseUrl);

    public static McpToolkit.McpClient BuildMcpClient(FakeHttpMessageHandler handler, string url = "https://n8n.test", string apiKey = "test-key", string? headerUrl = null, string? headerApiKey = null)
    {
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new McpOptions { Url = url, ApiKey = apiKey });
        var requestContext = new FakeRequestContext(headerUrl, headerApiKey);
        return new McpToolkit.McpClient(httpClient, options, requestContext);
    }

    public static McpToolkit.BackupService BuildBackupService(string backupDir)
    {
        var env = new FakeHostEnvironment { ContentRootPath = backupDir };
        var options = Options.Create(new McpOptions { BackupDir = backupDir });
        return new McpToolkit.BackupService(env, options);
    }

    public static ShellCommandRunner BuildShellRunner(ShellOptions? shellOptions = null)
    {
        var options = Options.Create(shellOptions ?? new ShellOptions());
        return new ShellCommandRunner(options);
    }

    public static ShellTools BuildShellTools(ShellOptions? shellOptions = null, ShellCommandRunner? runner = null)
    {
        var options = Options.Create(shellOptions ?? new ShellOptions());
        return new ShellTools(options, NullLogger<ShellTools>.Instance, runner ?? new ShellCommandRunner(options));
    }
}
