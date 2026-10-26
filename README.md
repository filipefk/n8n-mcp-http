# N8nMcpHttp

Servidor MCP (Model Context Protocol) para integração com o n8n, exposto via HTTP usando ASP.NET Core 10 e a biblioteca `ModelContextProtocol.AspNetCore`.

## Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Uma instância do n8n (self-hosted ou cloud) com uma [API Key](https://docs.n8n.io/api/authentication/) gerada

## Documentação da API do n8n

[https://docs.n8n.io/api/api-reference/](https://docs.n8n.io/api/api-reference/)

## Obtendo a API Key do n8n

1. Faça login no n8n
2. Selecione o menu Settings, a esquerda em baixo, e escolha a opção **n8n API**
2. Clique em **"Create an API Key"**
3. Dê um nome, escolha a expiração e selecione as permissões que esta API Key terá (podem ser editadas depois) e clique em Save
4. Copie o valor gerado — é o 3º valor da key do header `x-api-key` (ver "Autenticação" abaixo)

## Autenticação (header `x-api-key`)

O servidor exige **um único header** em `/mcp`, `/api-key` e `/backups`:

```
x-api-key: <key>
```

O valor é uma *key composta* que junta numa só string Base64Url três valores, nesta ordem:

| Posição | Valor | Obrigatório |
|---|---|---|
| 1 | **Chave MCP** — uma das chaves cadastradas em `McpAuth:ApiKeys`; é o que autoriza o uso deste servidor | sim |
| 2 | **URL da instância n8n** | não |
| 3 | **API Key do n8n** | não |

Com as posições 2 e 3 preenchidas, uma única instância deste servidor atende instâncias n8n diferentes a cada requisição, sem nenhuma configuração — era o papel dos headers `X-N8N-URL`/`X-N8N-API-KEY`, que **não são mais lidos**. Vazias, caem na configuração do servidor (ver "Destino n8n" abaixo).

A validação é *fail-closed*: sem o header, com uma key que não decodifica, com uma chave MCP fora de `McpAuth:ApiKeys` ou com `McpAuth:ApiKeys` vazia, a resposta é `401`. A comparação é em tempo fixo (`CryptographicOperations.FixedTimeEquals`) contra todas as chaves cadastradas, sem sair na primeira coincidência.

> **Aviso:** a key é **ofuscação, não criptografia** — não há segredo no algoritmo, e quem a tiver consegue recuperar a API Key do n8n gravada dentro dela. Trate-a como uma senha. Cada geração produz uma string diferente (semente aleatória) e todas são válidas.

### Cadastrando as chaves MCP

Em `appsettings.json` (ou user secrets, ou variáveis de ambiente):

```json
{
  "McpAuth": {
    "ApiKeys": [ "chave-de-um-usuario", "chave-de-outro" ]
  }
}
```

Por variável de ambiente, uma por índice: `McpAuth__ApiKeys__0`, `McpAuth__ApiKeys__1`, …

Use valores aleatórios longos, não senhas digitáveis — por exemplo 32 bytes em Base64:

```bash
openssl rand -base64 32
```

Subir o servidor sem nenhuma chave cadastrada é permitido, mas registra um aviso no boot e rejeita tudo com `401`.

### Gerando a key pela página web

Acesse a raiz do servidor no navegador (`http://localhost:5462/`). A página é pública e tem duas abas: **Gerar key** (monta a key a partir da chave MCP, da URL e da API Key do n8n, e já mostra o comando `claude mcp add` pronto) e **Decodificar key** (desfaz uma key existente para conferir o que há dentro dela). As duas chamam os endpoints abaixo, que continuam protegidos — a página pede a chave MCP no passo 1 justamente para enviá-la no header.

### Gerando a key pelos endpoints HTTP

Nos endpoints `/api-key`, o `x-api-key` é a **chave MCP em texto puro** (não a key composta): é o que esses endpoints produzem e desfazem.

```bash
curl -X POST http://localhost:5462/api-key/generate \
  -H "x-api-key: <sua-chave-mcp>" \
  -H "Content-Type: application/json" \
  -d '{"mcpApiKey":"<sua-chave-mcp>","n8nUrl":"https://n8n.minha-empresa.com","n8nApiKey":"<api-key-do-n8n>"}'
```

```bash
curl -X POST http://localhost:5462/api-key/decode \
  -H "x-api-key: <sua-chave-mcp>" \
  -H "Content-Type: application/json" \
  -d '{"apiKey":"<key-gerada>"}'
```

`generate` devolve `{ "apiKey": "..." }` e exige `mcpApiKey` (`400` sem ele); `decode` devolve `{ "mcpApiKey", "n8nUrl", "n8nApiKey" }` e responde `400` se a key não for válida. São `POST` de propósito: os segredos vão no corpo, nunca na URL — e `/api-key` fica fora do log de tráfego, para a API Key do n8n do corpo não chegar ao arquivo de log.

### Destino n8n (URL e API Key)

A URL e a API key da instância n8n são resolvidas **a cada requisição**, nesta ordem:

| Prioridade | Origem |
|---|---|
| 1 | 2º e 3º valores do header `x-api-key` |
| 2 | `N8n:Url` / `N8n:ApiKey` (appsettings, user secrets, `N8n__Url` / `N8n__ApiKey`) |
| 3 | variáveis de ambiente `N8N_URL` / `N8N_API_KEY` |

As duas são resolvidas de forma independente (a URL pode vir da key e a credencial da configuração). Faltando qualquer uma nas três origens, a tool falha com uma mensagem dizendo exatamente onde informá-la.

### Limite de requisições

Há um limite por IP, em janela fixa de um minuto, para que a chave MCP não fique exposta a força bruta: `RateLimit:ApiKeyPermitsPerMinute` (padrão `10`) em `/api-key` e `RateLimit:McpPermitsPerMinute` (padrão `300`) em `/mcp` e `/backups`. Ao estourar, a resposta é `429`.

> Atrás de um reverse proxy sem forwarded headers, o IP visto é o do proxy e todas as requisições compartilham a mesma cota.

### Health check

`GET /health` responde `200` com `{ "status": "ok" }`. É aberto, sem API Key — serve de liveness probe para container/orquestrador.

## Configuração do servidor (opcional)

O arquivo `src/Backend/N8nMcpHttp/appsettings.Development.json` contém opções do servidor:

```json
{
  "McpAuth": {
    "ApiKeys": []
  },
  "RateLimit": {
    "McpPermitsPerMinute": 300,
    "ApiKeyPermitsPerMinute": 10
  },
  "N8n": {
    "Url": "",
    "ApiKey": "",
    "BackupDir": "",
    "PublicBaseUrl": "",
    "IgnoreSslErrors": false
  },
  "Shell": {
    "Enabled": false,
    "DefaultShell": "Auto",
    "WorkingDirectory": "",
    "TimeoutSeconds": 60,
    "MaxOutputChars": 30000,
    "Password": "",
    "AllowedCommands": [],
    "BlockedCommands": []
  }
}
```

- `McpAuth:ApiKeys` — chaves MCP autorizadas (ver "Autenticação" acima). Lista vazia rejeita tudo.
- `RateLimit:*` — cota por IP, por minuto, em `/api-key` e em `/mcp`+`/backups`.
- `N8n:Url` / `N8n:ApiKey` — instância n8n padrão, usada quando a key não traz esses valores.
- `BackupDir` — onde os backups (ver seção "Backups" abaixo) são salvos. Se vazio (padrão), usa a pasta `backups/` na raiz do host.
- `PublicBaseUrl` — base (`scheme://host`) usada para montar o link de download dos backups (`backupUrl`/`baseUrl`). Útil atrás de um reverse proxy sem forwarded headers. Se vazio (padrão), é derivado da própria requisição MCP recebida.
- `IgnoreSslErrors` — ignora erros de validação do certificado TLS da instância n8n (certificado autoassinado, cadeia inválida). Desligado por padrão.
- `Shell:*` — configura as tools de execução de comandos do sistema operacional (ver seção "Tool de execução de comandos do sistema operacional" abaixo).

Em ambiente de desenvolvimento o endpoint MCP fica disponível em `http://localhost:5462/mcp` ou `https://localhost:7268/mcp`.

> **Aviso — HTTPS:** Em ambiente de desenvolvimento, prefira usar `http://localhost:5462/mcp` para evitar problemas com certificados. A url `https://localhost:7268/mcp` funciona bem no Postman mas é mais complicado no Claude Code.

### Adicionando o MCP no Claude Code

É necessário ter o Claude Code CLI instalado. Diretamente no terminal (PowerShell por exemplo) rode o comando abaixo:

```bash
# Adicionar globalmente (disponível em todos os projetos)
claude mcp add --scope user n8n --transport http http://localhost:5462/mcp --header "x-api-key: sua-key"

# Adicionar no projeto atual
claude mcp add n8n --transport http http://localhost:5462/mcp --header "x-api-key: sua-key"
```

A `sua-key` é a key composta gerada na página web do servidor ou pelo endpoint `/api-key/generate` (ver "Autenticação" acima) — ela já carrega a URL e a API Key do n8n, então nenhum outro header é necessário.

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

### Backups

| Tool | Descrição |
|------|-----------|
| `backup_workflow` | Faz um backup de um workflow |
| `backup_data_table` | Faz um backup de uma data table (colunas + todas as linhas) |
| `list_backups` | Lista os arquivos de backup salvos pelo servidor (nome, data, tamanho, link de download) |
| `restore_workflow_backup` | Restaura um workflow a partir de um arquivo de backup (sobrescreve se ainda existir, recria se foi excluído) |
| `restore_data_table_backup` | Restaura uma data table a partir de um arquivo de backup — substituição total: exclui a tabela atual (se existir) e recria do zero com colunas e linhas do backup (o ID sempre muda) |
| `delete_backup` | Exclui permanentemente um ou mais arquivos de backup do servidor (informados por nome, separados por vírgula) |

O servidor não faz mais backup automaticamente antes de operações destrutivas (`update_workflow`, `delete_workflow`, as ferramentas de edição de node, `update_data_table_rows`, `delete_data_table_rows`, `delete_data_table_column`, `delete_data_table`) — as descrições dessas ferramentas apenas recomendam chamar `backup_workflow`/`backup_data_table` antes de alterar ou excluir algo existente. Quando chamadas, essas ferramentas salvam um snapshot em disco e devolvem o nome do arquivo (`backupFile`, usado para restaurar) e um link de download (`backupUrl`) — o conteúdo do backup nunca passa pela IA.

#### Baixando um backup

`backupUrl` (e `baseUrl` + `fileName`, em `list_backups`) apontam para `GET /backups/{fileName}` no próprio servidor MCP. O endpoint **exige o header `x-api-key`**, com a mesma key composta usada em `/mcp` — então o link não abre direto no navegador; baixe com um cliente que mande o header:

```bash
curl -O -H "x-api-key: <sua-key>" http://localhost:5462/backups/<nome-do-arquivo>
```

Retorna `401` sem a key e `404` se o arquivo não existir.

### Shell

| Tool | Descrição |
|------|-----------|
| `get_shell_info` | Retorna o SO detectado, o shell padrão, os shells disponíveis, o diretório de trabalho e os limites configurados. Sem senha |
| `execute_shell_command` | Executa um comando no shell do sistema operacional onde o servidor MCP está rodando |

**Desligadas por padrão** (`Shell:Enabled = false`) — com `false`, essas duas tools nem aparecem em `tools/list`. Ver seção "Tool de execução de comandos do sistema operacional" abaixo.

## Tool de execução de comandos do sistema operacional

Desligada por padrão (`Shell:Enabled = false`). Quando ligada, expõe dois tools MCP:

- **`get_shell_info`** — sem senha, sem efeitos colaterais. Retorna o SO detectado, o shell padrão, os shells disponíveis, o diretório de trabalho e os limites configurados. Útil para o modelo decidir como chamar `execute_shell_command`.
- **`execute_shell_command`** — executa um comando no shell do SO onde o servidor está rodando (**não** na máquina do usuário):
  - **Windows** → PowerShell (`pwsh` se disponível no PATH, senão `powershell.exe`) ou `cmd.exe`.
  - **Linux/macOS** → `bash` ou `sh`.
  - Parâmetros: `command` (obrigatório), `shell` (`auto`/`powershell`/`cmd`/`bash`/`sh`), `working_directory`, `timeout_seconds`, `password`.
  - Nunca lança exceção por causa de exit code diferente de zero — isso é um resultado legítimo, com `exitCode` e `stdErr` preenchidos no retorno.

### Guard-rails configurados via `Shell`

| Chave | Default | Efeito |
|---|---|---|
| `Enabled` | `false` | Se `false` (padrão), os dois tools nem são registrados no servidor MCP |
| `DefaultShell` | `Auto` | Shell usado quando o chamador pede `shell = "auto"`; se `Auto`, cai no padrão do sistema operacional |
| `WorkingDirectory` | `""` (diretório do processo) | Diretório padrão de execução |
| `TimeoutSeconds` | `60` | Teto de timeout — o chamador pode pedir menos, nunca mais |
| `MaxOutputChars` | `30000` | Trunca stdout/stderr; resultado marca `truncated: true` |
| `Password` | `""` (sem senha) | Se preenchida, `execute_shell_command` passa a exigir o parâmetro `password` batendo com este valor. **`get_shell_info` nunca exige senha.** |
| `AllowedCommands` | `[]` (sem restrição) | Se preenchida, só libera comandos cujo executável esteja na lista — checado em cada segmento do comando (separado por `;`, `&&`, `\|\|`, `\|`, `&`) |
| `BlockedCommands` | `[]` (sem restrição) | Bloqueia comandos cujo executável esteja na lista, com a mesma checagem por segmento |

**Importante:** mesmo com allowlist/blocklist e senha configuradas, isso é um guard-rail e **não uma sandbox** — quoting, subshells e comandos como `powershell -c "..."` ainda podem contornar a checagem. O `x-api-key` já impede que um desconhecido chame as tools, mas qualquer portador de uma key válida executa comandos na máquina do servidor, então **avalie com cuidado antes de ligar `Shell:Enabled`** em qualquer instância exposta além de `localhost`, e configure pelo menos `Shell:Password`.

## Testando via Postman

O protocolo MCP sobre HTTP usa o padrão JSON-RPC 2.0. Todas as chamadas são feitas via `POST /mcp`.

Na raiz do repositório estão disponíveis os arquivos [`N8nMcpHttp.postman_collection.json`](N8nMcpHttp.postman_collection.json) e [`N8nMcpHttp.postman_environment.json`](N8nMcpHttp.postman_environment.json), prontos para importar no Postman e testar as chamadas descritas abaixo sem precisar montá-las manualmente.

### 1. Inicializar a sessão

Antes de chamar qualquer tool, é necessário enviar a mensagem de inicialização do protocolo MCP.

- **Método:** `POST`
- **URL:** `https://localhost:7268/mcp`
- **Headers:**
  ```
  Content-Type: application/json
  x-api-key: <sua-key>
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
  x-api-key: <sua-key>
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
src/Backend/GeraApiKey/ApiKeyGenerator.cs                  — junta/desfaz uma lista de strings numa única key Base64Url
src/Backend/N8nMcpHttp/Program.cs                          — bootstrap: serviços, pipeline de autenticação, servidor MCP e endpoints HTTP
src/Backend/N8nMcpHttp/McpApiKey.cs                        — contrato do header x-api-key: nome e ordem dos valores
src/Backend/N8nMcpHttp/McpAuthOptions.cs                   — POCO com as chaves MCP autorizadas (seção McpAuth)
src/Backend/N8nMcpHttp/Middleware/McpApiKeyMiddleware.cs   — valida o x-api-key em /mcp, /api-key e /backups (fail-closed)
src/Backend/N8nMcpHttp/Middleware/McpTrafficLoggingMiddleware.cs — loga request/response completos sob /mcp, redigindo segredos
src/Backend/N8nMcpHttp/Endpoints/ApiKeyEndpoints.cs        — POST /api-key/generate e /api-key/decode
src/Backend/N8nMcpHttp/wwwroot/index.html                  — página pública que gera e decodifica a key
src/Backend/N8nMcpHttp/HttpRequestContext.cs               — decodifica o x-api-key da requisição em curso
src/Backend/McpToolkit/McpOptions.cs                       — POCO com Url, ApiKey, BackupDir e PublicBaseUrl mapeados do appsettings.json
src/Backend/McpToolkit/McpClient.cs                        — wrapper sobre HttpClient para a API REST do n8n
src/Backend/McpToolkit/WorkflowTools.cs, DataTableTools.cs, ExecutionTools.cs, TagTools.cs, BackupTools.cs — tools MCP, uma classe por família
src/Backend/McpToolkit/BackupService.cs                    — salva/lista/lê/exclui snapshots JSON em disco
src/Backend/McpToolkit/Shell/                              — tools MCP de execução de comandos do SO (desligadas por padrão)
tests/McpTests/                                            — projeto de testes xUnit para o McpToolkit
```

O `McpApiKeyMiddleware` autentica `/mcp`, `/api-key` e `/backups` antes de qualquer tool rodar; só `/`, os arquivos estáticos e `/health` ficam abertos. O `McpClient` resolve o destino priorizando os valores que vêm dentro do `x-api-key` (via `IRequestContext`, implementado pelo `HttpRequestContext`), com fallback para `McpOptions` (`appsettings.json`) e para as variáveis `N8N_URL`/`N8N_API_KEY`. Isso permite que uma única instância do servidor atenda múltiplas instâncias n8n, uma por requisição. O `McpToolkit` não conhece o formato do header — é o host que decodifica.

A API de criação/atualização de workflow do n8n só aceita os campos `name`, `nodes`, `connections` e `settings` — o `McpClient` filtra o payload para conter apenas esses campos antes de cada chamada, e implementa atualizações parciais no lado do cliente (faz `GET` do workflow atual, mescla os campos alterados e envia o resultado filtrado). As ferramentas de edição granular de node (`update_workflow_node`, `add_workflow_node`, `delete_workflow_node`, `rename_workflow_node`) seguem o mesmo padrão, mas mesclando apenas o node/conexão alterado antes do `PUT`.

As classes de tools reduzem o volume de JSON devolvido à IA: resumem listas e execuções, aceitam URL do editor n8n como `workflow_id`, e delegam ao `BackupService` o snapshot sob demanda quando `backup_workflow`/`backup_data_table` são chamados explicitamente (não há backup automático antes de operações destrutivas). O nome do arquivo de backup inclui o nome sanitizado do workflow/tabela além do ID (ex: `20260711-153000123-workflow-MeuFluxo-abc123-a1b2c3.json`). O conteúdo do backup fica em disco (pasta `backups/` por padrão) e nunca passa pela IA — a ferramenta retorna apenas o nome do arquivo (`backupFile`) e um link de download (`backupUrl`), montado por `BackupTools.BuildBackupUrl` a partir de `N8n:PublicBaseUrl` ou, na ausência dele, do host da própria requisição MCP. O endpoint `GET /backups/{fileName}` (`Program.cs`) exige o header `x-api-key` e serve o arquivo direto do disco se ele existir.

## Testes

```bash
dotnet test tests/McpTests/McpTests.csproj
```

Os testes cobrem o `McpToolkit` (`McpClient`, as cinco classes de tools, `BackupService`, `Shell/`) sem depender de uma instância real do n8n nem de mocking frameworks: um `HttpMessageHandler` fake (`tests/McpTests/Fakes/FakeHttpMessageHandler.cs`) simula as respostas da API REST do n8n, um `FakeRequestContext` entrega os valores que viriam do `x-api-key`, o `BackupService` é exercitado com um diretório temporário real, e os testes de `Shell/` executam comandos reais e triviais no shell do sistema operacional onde os testes rodam. O host (autenticação, endpoints de API Key, página web) não tem teste automatizado — validação é manual.

## Stack

- .NET 10 / ASP.NET Core 10
- `ModelContextProtocol.AspNetCore`
- Serilog (console + arquivo, `logs/log.txt`)
- Autenticação por API Key própria (header `x-api-key`), com rate limit por IP
- Stateless — apenas proxy para a API REST do n8n
