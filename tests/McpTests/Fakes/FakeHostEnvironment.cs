using McpToolkit;
using McpToolkit.Shell;
using Microsoft.AspNetCore.Http;
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

public static class TestFactories
{
    public static IHttpContextAccessor BuildHttpContextAccessor(string? url = null, string? apiKey = null)
    {
        var accessor = new HttpContextAccessor();
        if (url is null && apiKey is null)
        {
            accessor.HttpContext = null;
            return accessor;
        }

        var context = new DefaultHttpContext();
        if (url is not null) context.Request.Headers["X-N8N-URL"] = url;
        if (apiKey is not null) context.Request.Headers["X-N8N-API-KEY"] = apiKey;
        accessor.HttpContext = context;
        return accessor;
    }

    public static McpToolkit.McpClient BuildMcpClient(FakeHttpMessageHandler handler, string url = "https://n8n.test", string apiKey = "test-key", string? headerUrl = null, string? headerApiKey = null)
    {
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new McpOptions { Url = url, ApiKey = apiKey });
        var accessor = headerUrl is null && headerApiKey is null
            ? BuildHttpContextAccessor()
            : BuildHttpContextAccessor(headerUrl, headerApiKey);
        return new McpToolkit.McpClient(httpClient, options, accessor);
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
