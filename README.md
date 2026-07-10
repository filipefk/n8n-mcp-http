# N8nMcpHttp

Servidor MCP (Model Context Protocol) para integração com o n8n, exposto via HTTP usando ASP.NET Core 10 e a biblioteca `ModelContextProtocol.AspNetCore`.

## Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Uma instância do n8n (self-hosted ou cloud) com uma [API Key](https://docs.n8n.io/api/authentication/) gerada

## Documentação da API do n8n

[https://docs.n8n.io/api/api-reference/](https://docs.n8n.io/api/api-reference/)

## Obtendo a API Key do n8n

1. Faça login no n8n
2. Selecione o menu Settings, a esquerda em baixo, e escolha a opção **N8n API**
2. Clique em **"Create an API Key"**
3. Dê um nome, escolha a expiração e selecione as permissões que esta API Key terá (podem ser editadas depois) e clique em Save
4. Copie o valor gerado — será usado como `X-N8N-API-KEY`

## Configuração

### Credenciais por requisição (recomendado)

Passe a URL e a API key da instância n8n como headers HTTP em cada requisição. Isso permite que diferentes clientes apontem para instâncias n8n diferentes sem nenhuma configuração no servidor:

```
X-N8N-URL: <url-da-sua-instancia-n8n>
X-N8N-API-KEY: <sua-api-key>
```

### Credenciais globais (fallback)

Crie o arquivo `N8nMcpHttp/N8nMcpHttp/appsettings.Development.json` (ignorado pelo git) com suas credenciais:

```json
{
  "N8n": {
    "Url": "<url-da-sua-instancia-n8n>",
    "ApiKey": "<sua-api-key>",
    "BackupDir": ""
  }
}
```

`BackupDir` é opcional — define onde os backups automáticos (ver seção "Backups automáticos" abaixo) são salvos. Se vazio (padrão), usa a pasta `backups/` na raiz do host.

Em ambiente de desenvolvimento o endpoint MCP fica disponível em `http://localhost:5462/mcp` ou `https://localhost:7268/mcp`.

> **Aviso — HTTPS:** Em ambiente de desenvolvimento, prefira usar `http://localhost:5462/mcp` para evitar problemas com certificados. A url `https://localhost:7268/mcp` funciona bem no Postman mas é mais complicado no Claude Code.

### Adicionando o MCP no Claude Code

É necessário ter o Claude Code CLI instalado. Diretamente no terminal (PowerShell por exemplo) rode o comando abaixo:

```bash
# Adicionar globalmente (disponível em todos os projetos)
claude mcp add --scope user n8n --transport http http://localhost:5462/mcp --header "X-N8N-URL: url-da-sua-instancia-n8n" --header "X-N8N-API-KEY: sua-api-key"

# Adicionar no projeto atual
claude mcp add n8n --transport http http://localhost:5462/mcp --header "X-N8N-URL: url-da-sua-instancia-n8n" --header "X-N8N-API-KEY: sua-api-key"
```

Outros comandos úteis:

```bash
claude mcp list      # Lista MCPs configurados e testa a conectividade
claude mcp get n8n    # Ver detalhes do MCP
claude mcp remove n8n # Remover o MCP
```

> **Aviso:** O Claude Code só vai identificar o MCP adicionado se o comando `claude mcp list` indicar que o mcp n8n está disponível

## Tools disponíveis

Todo parâmetro `workflow_id` aceita tanto o ID quanto a URL do editor n8n (`https://.../workflow/<id>`).

### Workflows

| Tool | Descrição |
|------|-----------|
| `list_workflows` | Lista os workflows do n8n, com filtros opcionais por nome e estado ativo. Resposta resumida por item (paginado) |
| `find_in_workflows` | Procura um texto em todos os workflows (nome, nodes, parâmetros), paginando automaticamente no servidor |
| `get_workflow` | Obtém o JSON de um workflow (nodes, connections, settings, active, tags). Por padrão omite `pinData`/`staticData`/`meta`/`shared` — use `full=true` para o workflow completo |
| `get_workflow_nodes` | Lista os nodes de um workflow, com nome, tipo e se está desabilitado |
| `get_workflow_node` | Obtém o JSON completo de um único node, pelo nome |
| `update_workflow_node` | Aplica um merge profundo de um patch sobre um node existente, sem reenviar o workflow inteiro |
| `add_workflow_node` | Adiciona um node a um workflow, opcionalmente conectando-o a nodes existentes |
| `delete_workflow_node` | Remove um node e suas referências em `connections` (religando upstream→downstream por padrão) |
| `rename_workflow_node` | Renomeia um node, atualizando `connections` e expressões `$('Nome antigo')` nos demais nodes |
| `create_workflow` | Cria um novo workflow (sempre criado inativo) |
| `update_workflow` | Atualiza um workflow existente — apenas os parâmetros informados são alterados |
| `duplicate_workflow` | Duplica um workflow existente (a cópia é criada inativa) |
| `delete_workflow` | Exclui permanentemente um workflow |
| `activate_workflow` | Ativa um workflow |
| `deactivate_workflow` | Desativa um workflow |

As ferramentas de escrita de workflow (`create_workflow`, `update_workflow`, `duplicate_workflow`, `activate_workflow`, `deactivate_workflow` e as de edição de node) retornam um resumo compacto (`id`, `name`, `active`, `nodeCount`, `nodes`) em vez do workflow completo.

### Data Tables

| Tool | Descrição |
|------|-----------|
| `list_data_tables` | Lista as data tables, com filtro opcional por nome |
| `get_data_table` | Obtém uma data table, incluindo suas colunas (nome, tipo, índice) |
| `get_data_table_rows` | Lista as linhas de uma data table (paginado) |
| `create_data_table_column` | Cria uma coluna em uma data table (tipos: `string`, `number`, `boolean`, `date`) |
| `delete_data_table_column` | Exclui uma coluna de uma data table |
| `insert_data_table_rows` | Insere uma ou mais linhas em uma data table |
| `update_data_table_rows` | Atualiza as linhas que casarem com um filtro |
| `delete_data_table_rows` | Exclui as linhas que casarem com um filtro (filtro obrigatório) |

### Executions

| Tool | Descrição |
|------|-----------|
| `list_executions` | Lista as execuções de workflows, com filtros por workflow e status. Resposta resumida por item (paginado) |
| `get_execution` | Obtém os detalhes de uma execução. Resposta resumida por padrão — use `include_data=true` para o payload completo |
| `delete_execution` | Exclui uma execução do histórico |

### Tags

| Tool | Descrição |
|------|-----------|
| `list_tags` | Lista as tags de workflow da instância n8n (paginado) |
| `get_tag` | Obtém uma tag pelo ID |
| `create_tag` | Cria uma nova tag |
| `update_tag` | Renomeia uma tag existente |
| `delete_tag` | Exclui uma tag |
| `get_workflow_tags` | Lista as tags atribuídas a um workflow |
| `set_workflow_tags` | Define o conjunto de tags de um workflow, substituindo as atuais |
| `set_workflow_tags_by_name` | Define as tags de um workflow a partir dos nomes, resolvendo/criando os IDs automaticamente |

### Backups automáticos

| Tool | Descrição |
|------|-----------|
| `list_backups` | Lista os arquivos de backup automático salvos pelo servidor (nome, data, tamanho) |
| `restore_workflow_backup` | Restaura um workflow a partir de um arquivo de backup (sobrescreve se ainda existir, recria se foi excluído) |

Antes de qualquer operação destrutiva (`update_workflow`, `delete_workflow`, as ferramentas de edição de node, `update_data_table_rows`, `delete_data_table_rows`, `delete_data_table_column`, `delete_data_table`), o servidor salva automaticamente um snapshot do estado atual e devolve o caminho do arquivo no campo `backupFile` da resposta — sem que o conteúdo do backup passe pela IA.

## Testando via Postman

O protocolo MCP sobre HTTP usa o padrão JSON-RPC 2.0. Todas as chamadas são feitas via `POST /mcp`.

### 1. Inicializar a sessão

Antes de chamar qualquer tool, é necessário enviar a mensagem de inicialização do protocolo MCP.

- **Método:** `POST`
- **URL:** `https://localhost:7268/mcp`
- **Headers:**
  ```
  Content-Type: application/json
  X-N8N-URL: <url-da-sua-instancia-n8n>
  X-N8N-API-KEY: <sua-api-key>
  ```
- **Body (raw JSON):**
  ```json
  {
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2024-11-05",
      "capabilities": {},
      "clientInfo": {
        "name": "postman-test",
        "version": "1.0"
      }
    }
  }
  ```

Guarde o header `mcp-session-id` retornado na resposta — ele é necessário nas chamadas seguintes.

### 2. Listar workflows (`list_workflows`)

- **Método:** `POST`
- **URL:** `https://localhost:7268/mcp`
- **Headers:**
  ```
  Content-Type: application/json
  X-N8N-URL: <url-da-sua-instancia-n8n>
  X-N8N-API-KEY: <sua-api-key>
  mcp-session-id: <id-retornado-no-initialize>
  ```
- **Body (raw JSON):**
  ```json
  {
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/call",
    "params": {
      "name": "list_workflows",
      "arguments": {}
    }
  }
  ```

### 3. Criar um workflow (`create_workflow`)

- **Método:** `POST`
- **URL:** `https://localhost:7268/mcp`
- **Headers:** (mesmos do passo 2)
- **Body (raw JSON):**
  ```json
  {
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {
      "name": "create_workflow",
      "arguments": {
        "name": "Meu workflow de teste",
        "nodes_json": "[]",
        "connections_json": "{}"
      }
    }
  }
  ```

### 4. Listar tools disponíveis

Para descobrir todas as tools expostas pelo servidor:

- **Body (raw JSON):**
  ```json
  {
    "jsonrpc": "2.0",
    "id": 4,
    "method": "tools/list",
    "params": {}
  }
  ```

## Arquitetura

```
N8nMcpHttp/N8nMcpHttp/Program.cs                          — bootstrap: registra serviços e monta o servidor MCP
N8nMcpHttp/N8nMcpHttp/Middleware/McpTrafficLoggingMiddleware.cs — loga request/response completos sob /mcp
N8nMcpHttp/McpToolkit/McpOptions.cs                        — POCO com Url, ApiKey e BackupDir mapeados do appsettings.json
N8nMcpHttp/McpToolkit/McpClient.cs                         — wrapper sobre HttpClient para a API REST do n8n
N8nMcpHttp/McpToolkit/McpTools.cs                          — tools MCP expostas via atributos [McpServerTool]
N8nMcpHttp/McpToolkit/BackupService.cs                     — salva/lista/lê snapshots JSON em disco antes de operações destrutivas
```

O `McpClient` resolve o destino priorizando os headers `X-N8N-URL` / `X-N8N-API-KEY` da requisição atual, com fallback para `McpOptions` (`appsettings.json`). Isso permite que uma única instância do servidor atenda múltiplas instâncias n8n, uma por requisição.

A API de criação/atualização de workflow do n8n só aceita os campos `name`, `nodes`, `connections` e `settings` — o `McpClient` filtra o payload para conter apenas esses campos antes de cada chamada, e implementa atualizações parciais no lado do cliente (faz `GET` do workflow atual, mescla os campos alterados e envia o resultado filtrado). As ferramentas de edição granular de node (`update_workflow_node`, `add_workflow_node`, `delete_workflow_node`, `rename_workflow_node`) seguem o mesmo padrão, mas mesclando apenas o node/conexão alterado antes do `PUT`.

O `McpTools` reduz o volume de JSON devolvido à IA: resume listas e execuções, aceita URL do editor n8n como `workflow_id`, e delega ao `BackupService` o snapshot automático antes de operações destrutivas — o conteúdo do backup fica em disco (pasta `backups/` por padrão) e só o caminho do arquivo retorna na resposta da ferramenta.

## Stack

- .NET 10 / ASP.NET Core 10
- `ModelContextProtocol.AspNetCore`
- Serilog (console + arquivo, `logs/log.txt`)
- Stateless — apenas proxy para a API REST do n8n
