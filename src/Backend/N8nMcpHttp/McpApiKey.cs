using GeraApiKey;
using System.Diagnostics.CodeAnalysis;

namespace N8nMcpHttp;

// Conteúdo do header x-api-key, gerado pelo GeraApiKey.ApiKeyGenerator a partir de
// [mcpApiKey, n8nUrl, n8nApiKey] — nesta ordem. FromValues/ToValues são o único lugar
// do código que conhece essa ordem; a biblioteca GeraApiKey só vê uma lista de strings.
// n8nUrl e n8nApiKey são opcionais: vazias ou ausentes caem no fallback do McpClient (appsettings/variáveis).
public sealed class McpApiKey(string mcpKey, string? n8nUrl, string? n8nApiKey)
{
    public const string HeaderName = "x-api-key";

    public string McpKey { get; } = mcpKey;
    public string? N8nUrl { get; } = n8nUrl;
    public string? N8nApiKey { get; } = n8nApiKey;

    public static McpApiKey FromValues(IReadOnlyList<string> values) =>
        new(values.Count > 0 ? values[0] : string.Empty, ValueAt(values, 1), ValueAt(values, 2));

    // Os valores vazios do fim são descartados: uma key só com a chave MCP não carrega
    // separadores inúteis, e o FromValues já trata posições ausentes como null.
    public IReadOnlyList<string> ToValues()
    {
        var values = new List<string> { McpKey, N8nUrl ?? string.Empty, N8nApiKey ?? string.Empty };
        while (values.Count > 1 && values[^1].Length == 0)
            values.RemoveAt(values.Count - 1);
        return values;
    }

    public static bool TryRead(HttpContext? context, [NotNullWhen(true)] out McpApiKey? apiKey)
    {
        apiKey = null;
        var header = ReadHeader(context);
        if (!ApiKeyGenerator.TryParse(header, out var values)) return false;

        apiKey = FromValues(values);
        return true;
    }

    public static string? ReadHeader(HttpContext? context) =>
        context?.Request.Headers[HeaderName].FirstOrDefault();

    private static string? ValueAt(IReadOnlyList<string> values, int index) =>
        index < values.Count && !string.IsNullOrWhiteSpace(values[index]) ? values[index] : null;
}
