using McpToolkit;
using McpToolkit.Shell;
using N8nMcpHttp;
using N8nMcpHttp.Endpoints;
using N8nMcpHttp.Middleware;
using Serilog;
using Serilog.Events;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var strLogLevel = configuration.GetValue<string>("Logging:LogLevel:Default");
var enumLogEventLevel = Enum.TryParse<LogEventLevel>(strLogLevel, true, out var parsedLogEventLevel)
    ? parsedLogEventLevel
    : LogEventLevel.Information;
builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration
        .MinimumLevel.Is(enumLogEventLevel)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console(enumLogEventLevel)
        .WriteTo.File("logs/log.txt",
            enumLogEventLevel,
            rollingInterval: RollingInterval.Day));

builder.Services.Configure<McpOptions>(configuration.GetSection("N8n"));
// Sem N8n:Url/N8n:ApiKey configurados, aceita as variáveis de ambiente N8N_URL/N8N_API_KEY —
// permite subir o container só com variáveis, sem montar um appsettings.
builder.Services.PostConfigure<McpOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.Url))
        options.Url = configuration["N8N_URL"] ?? string.Empty;
    if (string.IsNullOrWhiteSpace(options.ApiKey))
        options.ApiKey = configuration["N8N_API_KEY"] ?? string.Empty;
});
// Chaves MCP autorizadas (primeiro valor do header x-api-key) — lista vazia rejeita
// todas as chamadas a /mcp, /api-key e /backups.
builder.Services.Configure<McpAuthOptions>(configuration.GetSection("McpAuth"));
builder.Services.Configure<ShellOptions>(configuration.GetSection("Shell"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRequestContext, HttpRequestContext>();

// Limite por IP para não deixar a chave MCP exposta a força bruta. Tem de ser o GlobalLimiter, e não
// RequireRateLimiting no endpoint: os UseWhen de autenticação rodam antes do roteamento, então uma
// política por endpoint só seria avaliada depois do 401. Atrás de reverse proxy sem forwarded headers
// o RemoteIpAddress é o do proxy, e todas as requisições caem na mesma partição.
var mcpPermits = configuration.GetValue("RateLimit:McpPermitsPerMinute", 300);
var apiKeyPermits = configuration.GetValue("RateLimit:ApiKeyPermitsPerMinute", 10);
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

        if (context.Request.Path.StartsWithSegments("/api-key"))
            return RateLimitPartition.GetFixedWindowLimiter($"api-key:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = apiKeyPermits,
                Window = TimeSpan.FromMinutes(1)
            });

        if (context.Request.Path.StartsWithSegments("/mcp") || context.Request.Path.StartsWithSegments("/backups"))
            return RateLimitPartition.GetFixedWindowLimiter($"mcp:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = mcpPermits,
                Window = TimeSpan.FromMinutes(1)
            });

        return RateLimitPartition.GetNoLimiter("sem-limite");
    });
});

// N8n:IgnoreSslErrors permite ignorar erros de validação do certificado TLS da instância n8n
// (ex: certificado autoassinado ou cadeia inválida) — desligado por padrão.
var ignoreSslErrors = configuration.GetValue("N8n:IgnoreSslErrors", false);
builder.Services.AddHttpClient<McpClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = ignoreSslErrors
            ? (_, _, _, _) => true
            : null
    });
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<ShellCommandRunner>();

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithHttpTransport();

// Cada família de tools pode ser desligada por configuração (padrão: ligada), no mesmo espírito
// de Shell:Enabled — quem só usa workflows não paga pela superfície (schema/descrições) das demais.
if (configuration.GetValue("Tools:Workflows:Enabled", true))
    mcpBuilder.WithTools<WorkflowTools>();
if (configuration.GetValue("Tools:DataTables:Enabled", true))
    mcpBuilder.WithTools<DataTableTools>();
if (configuration.GetValue("Tools:Executions:Enabled", true))
    mcpBuilder.WithTools<ExecutionTools>();
if (configuration.GetValue("Tools:Tags:Enabled", true))
    mcpBuilder.WithTools<TagTools>();
if (configuration.GetValue("Tools:Backups:Enabled", true))
    mcpBuilder.WithTools<BackupTools>();

// Desligado por padrão: defina "Shell:Enabled": true para expor as tools de execução de comandos.
if (configuration.GetValue("Shell:Enabled", false))
    mcpBuilder.WithTools<ShellTools>();

var app = builder.Build();

if (!configuration.GetSection("McpAuth:ApiKeys").GetChildren().Any(k => !string.IsNullOrEmpty(k.Value)))
    app.Logger.LogWarning("Nenhuma chave em McpAuth:ApiKeys — todas as chamadas a /mcp, /api-key e /backups serão rejeitadas (401).");

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseSerilogRequestLogging();

// Página da raiz (wwwroot/index.html) para gerar/decodificar a API Key pelos endpoints /api-key.
// É pública: a chave MCP é digitada nela e vai no header, os endpoints continuam protegidos.
app.UseDefaultFiles();
app.UseStaticFiles();

// Antes dos UseWhen de autenticação, para o 429 vir antes do 401.
app.UseRateLimiter();

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    appBuilder => appBuilder
        .UseMiddleware<McpTrafficLoggingMiddleware>()
        .UseMiddleware<McpApiKeyMiddleware>(McpKeyFormat.Encoded));

// Os endpoints de API Key também exigem o x-api-key, mas com a chave MCP em texto puro: a key
// gerada é o que eles produzem/desfazem. Sem o middleware de tráfego, para a API key do n8n do
// corpo do generate não ir para o log.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api-key"),
    appBuilder => appBuilder.UseMiddleware<McpApiKeyMiddleware>(McpKeyFormat.Raw));

// O link de download do backup carrega a key completa, igual ao /mcp.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/backups"),
    appBuilder => appBuilder.UseMiddleware<McpApiKeyMiddleware>(McpKeyFormat.Encoded));

app.MapApiKeyEndpoints();

app.MapMcp("/mcp");

app.MapGet("/backups/{fileName}", (string fileName, BackupService backups) =>
{
    var path = backups.ResolveBackupPath(fileName);
    return path is null
        ? Results.NotFound()
        : Results.File(path, "application/json", Path.GetFileName(path));
});

// Liveness para container/orquestrador — aberto, fora dos prefixos protegidos.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
