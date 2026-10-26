using System.Text.RegularExpressions;

namespace N8nMcpHttp.Middleware;

public sealed partial class McpTrafficLoggingMiddleware(RequestDelegate next, ILogger<McpTrafficLoggingMiddleware> logger)
{
    // X-N8N-API-KEY não é mais lido pelo servidor (a credencial vem dentro do x-api-key), mas segue
    // na lista: se algum cliente antigo ainda mandar o header, ele não vaza para o arquivo de log.
    private static readonly string[] SensitiveHeaders = [McpApiKey.HeaderName, "X-N8N-API-KEY", "Authorization"];

    // Redige o parâmetro "password" da tool execute_shell_command para não gravar
    // em texto claro no log o único segredo que protege a execução de comandos.
    [GeneratedRegex("\"password\"\\s*:\\s*\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFieldRegex();

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = string.Join(", ", context.Request.Headers
            .Where(h => !string.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}={(SensitiveHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase) ? "***" : h.Value.ToString())}"));

        // O GET /mcp abre o canal SSE do transporte MCP e fica aberto por toda a sessão — bufferizar
        // a resposta em MemoryStream (como abaixo) faria o canal nunca ser entregue de forma incremental
        // e acumularia indefinidamente em memória. Só corpos de requisição/resposta de tamanho finito
        // (POST, o canal de mensagens JSON-RPC) são bufferizados e logados por inteiro.
        if (HttpMethods.IsGet(context.Request.Method))
        {
            logger.LogInformation(
                "MCP request: {Method} {Path} | Headers: {Headers} | Body: (canal SSE, não bufferizado)",
                context.Request.Method, context.Request.Path, headers);
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        using var requestReader = new StreamReader(context.Request.Body, leaveOpen: true);
        var requestBody = await requestReader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        requestBody = PasswordFieldRegex().Replace(requestBody, "\"password\":\"***\"");

        logger.LogInformation(
            "MCP request: {Method} {Path} | Headers: {Headers} | Body: {Body}",
            context.Request.Method, context.Request.Path, headers, requestBody);

        var originalResponseBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;

        try
        {
            await next(context);
        }
        finally
        {
            responseBuffer.Position = 0;
            var responseBody = await new StreamReader(responseBuffer).ReadToEndAsync();
            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalResponseBody);
            context.Response.Body = originalResponseBody;

            logger.LogInformation(
                "MCP response: {StatusCode} {Path} | Body: {Body}",
                context.Response.StatusCode, context.Request.Path, responseBody);
        }
    }
}
