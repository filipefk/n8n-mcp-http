namespace McpToolkit;

/// <summary>
/// Abstrai o acesso à requisição HTTP em curso, para que o McpToolkit não dependa
/// diretamente de Microsoft.AspNetCore.Http (implementado no host, N8nMcpHttp).
/// </summary>
public interface IRequestContext
{
    string? GetHeader(string name);

    /// <summary>Retorna "scheme://host" da requisição atual, ou null se não houver requisição HTTP ativa.</summary>
    string? GetRequestBaseUrl();
}
