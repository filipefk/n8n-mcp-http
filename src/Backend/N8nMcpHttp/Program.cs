using McpToolkit;
using McpToolkit.Shell;
using N8nMcpHttp;
using N8nMcpHttp.Middleware;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

var strLogLevel = builder.Configuration.GetValue<string>("Logging:LogLevel:Default");
var enumLogEventLevel = Enum.TryParse<LogEventLevel>(strLogLevel, true, out var parsedLogEventLevel)
    ? parsedLogEventLevel
    : LogEventLevel.Information;
builder.Host.UseSerilog((context, configuration) =>
    configuration
        .MinimumLevel.Is(enumLogEventLevel)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console(enumLogEventLevel)
        .WriteTo.File("logs/log.txt",
            enumLogEventLevel,
            rollingInterval: RollingInterval.Day));

builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("N8n"));
builder.Services.Configure<ShellOptions>(builder.Configuration.GetSection("Shell"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRequestContext, HttpRequestContext>();

builder.Services.AddHttpClient<McpClient>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<ShellCommandRunner>();

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithHttpTransport();

// Cada família de tools pode ser desligada por configuração (padrão: ligada), no mesmo espírito
// de Shell:Enabled — quem só usa workflows não paga pela superfície (schema/descrições) das demais.
if (builder.Configuration.GetValue("Tools:Workflows:Enabled", true))
    mcpBuilder.WithTools<WorkflowTools>();
if (builder.Configuration.GetValue("Tools:DataTables:Enabled", true))
    mcpBuilder.WithTools<DataTableTools>();
if (builder.Configuration.GetValue("Tools:Executions:Enabled", true))
    mcpBuilder.WithTools<ExecutionTools>();
if (builder.Configuration.GetValue("Tools:Tags:Enabled", true))
    mcpBuilder.WithTools<TagTools>();
if (builder.Configuration.GetValue("Tools:Backups:Enabled", true))
    mcpBuilder.WithTools<BackupTools>();

// Desligado por padrão: defina "Shell:Enabled": true para expor as tools de execução de comandos.
if (builder.Configuration.GetValue("Shell:Enabled", false))
    mcpBuilder.WithTools<ShellTools>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseSerilogRequestLogging();

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    appBuilder => appBuilder.UseMiddleware<McpTrafficLoggingMiddleware>());

app.MapMcp("/mcp");

app.MapGet("/backups/{fileName}", (string fileName, BackupService backups) =>
{
    var path = backups.ResolveBackupPath(fileName);
    return path is null
        ? Results.NotFound()
        : Results.File(path, "application/json", Path.GetFileName(path));
});

app.Run();
