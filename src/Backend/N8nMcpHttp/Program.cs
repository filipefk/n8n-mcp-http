using McpToolkit;
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
        .WriteTo.Console(enumLogEventLevel)
        .WriteTo.File("logs/log.txt",
            enumLogEventLevel,
            rollingInterval: RollingInterval.Day));

builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("N8n"));
builder.Services.AddHttpContextAccessor();

builder.Services.AddHttpClient<McpClient>();
builder.Services.AddSingleton<BackupService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<McpTools>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

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
