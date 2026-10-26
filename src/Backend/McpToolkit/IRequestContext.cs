namespace McpToolkit;

/// <summary>
/// Abstrai o acesso à requisição HTTP em curso, para que o McpToolkit não dependa
/// diretamente de Microsoft.AspNetCore.Http nem do formato do header x-api-key
/// (implementado no host, N8nMcpHttp).
/// </summary>
public interface IRequestContext
{
    /// <summary>URL da instância n8n informada pelo cliente, ou null se a key não a traz.</summary>
    string? N8nUrl { get; }

    /// <summary>API key do n8n informada pelo cliente, ou null se a key não a traz.</summary>
    string? N8nApiKey { get; }

    /// <summary>Retorna "scheme://host" da requisição atual, ou null se não houver requisição HTTP ativa.</summary>
    string? GetRequestBaseUrl();
}
