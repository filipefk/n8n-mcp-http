# CLAUDE.md

Este arquivo fornece orientações para o Claude Code (claude.ai/code) ao trabalhar com o código deste repositório.

## Visão Geral

N8nMcpHttp é um servidor MCP (Model Context Protocol) em .NET 10 ASP.NET Core que expõe a API REST do n8n (workflows, data tables, execuções, tags) como ferramentas MCP via transporte HTTP, montado em `/mcp`.

## Build e Execução

```
dotnet build N8nMcpHttp.slnx
dotnet run --project src/Backend/N8nMcpHttp/N8nMcpHttp.csproj
```

```
dotnet test tests/McpTests/McpTests.csproj
```

Projeto de testes em `tests/McpTests` (xUnit), cobrindo o `McpToolkit` (`McpClient`, `McpTools`, `BackupService`). `McpClient` e `BackupService` são `sealed` sem interfaces — os testes instanciam as classes reais e fakeiam só as bordas: um `FakeHttpMessageHandler` escrito à mão (`Fakes/FakeHttpMessageHandler.cs`, sem dependência de mocking framework) no lugar da API REST do n8n, e um diretório temporário real para o `BackupService` (via `Fakes/FakeHostEnvironment.cs` e os helpers em `TestFactories`). Cobertura completa nos pontos com lógica própria (`ResolveTarget`, `FilterWorkflowPayload`, os quatro métodos de edição de node, `ParseObject`/`ParseArray`, `ResolveWorkflowId`, os helpers de sumarização, `BackupService` inteiro, incluindo sanitização de nome de arquivo e `ResolveBackupPath`, e o round-trip de `restore_data_table_backup`); os métodos de CRUD simples (tags, data tables, execuções) têm só 1-2 testes representativos por família. O endpoint HTTP `GET /backups/{fileName}` (`Program.cs`) não tem teste automatizado (não há `WebApplicationFactory` no projeto) — validação é manual.

## Arquitetura

```
src/Backend/McpToolkit/    — biblioteca McpToolkit
src/Backend/N8nMcpHttp/    — host ASP.NET Core
tests/McpTests/            — projeto de testes xUnit
```

Dois projetos de código, mais um de testes:

- **McpToolkit** (`src/Backend/McpToolkit`) — biblioteca de classes sem dependência do ASP.NET além do `FrameworkReference`. Contém:
  - `McpClient.cs` — o único lugar que se comunica com a API REST do n8n. Encapsula o `HttpClient`, monta as requisições e expõe um método assíncrono para cada operação da API do n8n (workflows, data tables, execuções, tags), incluindo métodos compostos de edição granular de node (`UpdateWorkflowNodeAsync`, `AddWorkflowNodeAsync`, `DeleteWorkflowNodeAsync`, `RenameWorkflowNodeAsync` — todos fazem GET → transforma em memória → PUT via `FilterWorkflowPayload`, no mesmo padrão de `UpdateWorkflowAsync`). Todas as respostas são `JsonNode?` brutos.
  - `McpTools.cs` — a superfície voltada ao MCP. Uma única classe `[McpServerToolType]` onde todo método público é anotado com `[McpServerTool]` e se torna uma ferramenta MCP invocável. Cada método de ferramenta é um wrapper simples: faz o parse dos parâmetros em string (os argumentos JSON chegam como strings brutas, ex. `nodes_json`, `filter_json`) via os helpers locais `ParseObject`/`ParseArray`, chama o método correspondente do `McpClient` e retorna `ToJsonString()`. Também concentra a economia de tokens: helpers de projeção (`SummarizeWorkflowListItem`, `BuildWorkflowChangeSummary`, `SummarizeExecution`) reduzem o JSON devolvido à IA, `ResolveWorkflowId` aceita URL do editor n8n em qualquer parâmetro `workflow_id`, e os wrappers de backup (`BackupWorkflowAsync`/`BackupDataTableAsync`) chamam o `BackupService` apenas quando a IA invoca `backup_workflow`/`backup_data_table` explicitamente — não há chamada automática antes de operações destrutivas (ver seção "Economia de tokens").
  - `McpOptions.cs` — vincula a seção de configuração `N8n` (`Url`, `ApiKey`, `BackupDir`, `PublicBaseUrl`).
  - `BackupService.cs` — salva snapshots JSON em disco (`BackupDir`, padrão `backups/` na raiz do host) quando `backup_workflow`/`backup_data_table` são chamados, lista esses arquivos e lê um arquivo específico para restauração. O nome do arquivo inclui o nome sanitizado do item (workflow/tabela) além do ID, ex: `20260711-153000-workflow-MeuFluxo-abc123.json` — a sanitização (`SanitizeForFileName`) mantém só `[A-Za-z0-9-_]` (colapsando o resto em `_`) porque o nome do arquivo também vira parte da URL de download, então precisa ser seguro em disco e em URL sem depender de encode/decode. Os métodos `Save*BackupAsync` retornam apenas o **nome do arquivo** (não mais o caminho absoluto do disco do servidor — inútil quando o cliente MCP roda em outra máquina); `ResolveBackupPath` reaplica a mesma sanitização contra path traversal usada em `ReadBackupAsync` e devolve o caminho físico (ou `null`) para o endpoint de download servir o arquivo.
- **N8nMcpHttp** (`src/Backend/N8nMcpHttp`) — o host ASP.NET Core. O `Program.cs` configura o Serilog, registra o `McpClient` via `AddHttpClient`, registra o `BackupService` como singleton, registra o `McpTools` via `AddMcpServer().WithHttpTransport().WithTools<McpTools>()`, mapeia o endpoint MCP em `/mcp` e um endpoint `GET /backups/{fileName}` para download de backups (ver seção de backup remoto abaixo). O `Middleware/McpTrafficLoggingMiddleware.cs` registra os corpos e cabeçalhos completos de requisição/resposta para qualquer chamada sob `/mcp` (redigindo o cabeçalho `X-N8N-API-KEY`), fazendo buffer de ambos os streams para que possam ser lidos e reproduzidos.

## Resolução do destino n8n (override por requisição)

`McpClient.ResolveTarget()` escolhe a URL da instância n8n e a API key com a seguinte precedência, verificada a cada requisição:

1. Cabeçalhos da requisição `X-N8N-URL` / `X-N8N-API-KEY` (via `IHttpContextAccessor`)
2. Caso não informados, usa `N8n:Url` / `N8n:ApiKey` da configuração (`appsettings.json`, vazio por padrão — definido via `appsettings.Development.json`, variáveis de ambiente ou user secrets)

Isso significa que uma única instância do servidor implantada pode apontar para diferentes instâncias do n8n a cada chamada, bastando definir esses cabeçalhos — não há uma configuração fixa de tenant único.

## Tratamento do payload de workflow

A API de criação/atualização de workflow do n8n só aceita `name`, `nodes`, `connections`, `settings` — qualquer outro campo é rejeitado. `McpClient.FilterWorkflowPayload` reduz um objeto JSON de workflow a apenas esses campos antes de toda chamada de criação/atualização/duplicação, e dentro de `settings` mantém só as chaves da whitelist `AllowedSettingsKeys` (`saveExecutionProgress`, `saveManualExecutions`, `saveDataErrorExecution`, `saveDataSuccessExecution`, `executionTimeout`, `errorWorkflow`, `timezone`, `executionOrder`, `callerPolicy`, `callerIds`) — o n8n rejeita `settings` com chaves fora dessa lista. `UpdateWorkflowAsync` e `DuplicateWorkflowAsync` primeiro fazem um `GET` do workflow atual, mesclam apenas os campos alterados e então enviam o payload filtrado via `PUT`/`POST` — as atualizações parciais são implementadas no lado do cliente, não pela API do n8n.

## Convenção de parâmetros de ferramentas

Os parâmetros de ferramentas MCP que representam estruturas JSON (nodes, connections, settings, filters) são passados como strings JSON brutas (com sufixo `_json`) em vez de objetos estruturados, e depois parseados dentro do `McpTools` via `ParseObject`/`ParseArray`, que lançam `InvalidOperationException` com mensagem de erro em português no caso de JSON inválido. Todas as descrições de ferramentas e mensagens de erro estão em português (pt-BR), alinhadas ao público-alvo.

## Economia de tokens

O servidor foi desenhado para minimizar o volume de JSON trocado com a IA, sem reduzir o que ela consegue fazer:

- **Respostas resumidas por padrão**: `list_workflows` retorna um resumo por item (`id`, `name`, `active`, `createdAt`, `updatedAt`, `tags`, `nodeCount`) em vez do workflow completo; `get_workflow` omite `pinData`/`staticData`/`meta`/`shared` a menos que `full=true`; `list_executions`/`get_execution` retornam um resumo (`id`, `status`, `workflowId`, `mode`, `startedAt`, `stoppedAt` e, se houver erro, `errorNode`/`errorMessage`) a menos que `include_data=true`. As ferramentas de escrita de workflow (`create_workflow`, `update_workflow`, `duplicate_workflow`, `activate_workflow`, `deactivate_workflow` e as de edição de node) retornam um resumo compacto (`id`, `name`, `active`, `nodeCount`, `nodes`) em vez do workflow inteiro.
- **URL do editor aceita como ID**: todo parâmetro `workflow_id` aceita tanto o ID quanto a URL do editor n8n (`ResolveWorkflowId` em `McpTools.cs` extrai o segmento após `/workflow/`).
- **Edição granular de node** (`update_workflow_node`, `add_workflow_node`, `delete_workflow_node`, `rename_workflow_node`): a IA envia só o nome do node e o delta (ex: `{"parameters":{"url":"..."}}`), e o servidor faz o GET, aplica a mudança em memória (merge profundo, rewire de `connections`, substituição de expressões `$('Nome')`) e o PUT — evitando reenviar o array `nodes` inteiro a cada alteração pequena.
- **Backup sob demanda, não automático**: o servidor não faz mais backup automaticamente antes de operações destrutivas (`update_workflow`, `delete_workflow`, as ferramentas de edição de node, `update_data_table_rows`, `delete_data_table_rows`, `delete_data_table_column`, `delete_data_table`) — isso gerava um backup a cada passo de uma sequência de alterações, mesmo quando desnecessário. Em vez disso, as descrições dessas ferramentas apenas recomendam chamar `backup_workflow`/`backup_data_table` antes de alterar ou excluir algo existente; a política de quando efetivamente oferecer o backup ao usuário (perguntar via `AskUserQuestion` mostrando a data do último backup, e oferecer uma única vez no início de uma sequência de alterações no mesmo fluxo/tabela em vez de a cada passo) é implementada do lado do cliente, na skill `n8n` (fora deste repositório), não no servidor. Quando `backup_workflow`/`backup_data_table` são chamados, o `BackupService` salva um snapshot em `backups/` e devolve o nome do arquivo (`backupFile`) e um link de download (`backupUrl`) — o conteúdo do backup nunca passa pela janela de contexto da IA. `list_backups` traz `downloadUrl` por item. `restore_workflow_backup`/`restore_data_table_backup` permitem reverter só com o nome do arquivo, sem precisar baixar o conteúdo.
- **Buscas compostas**: `find_in_workflows` pagina por todos os workflows no servidor e retorna só os workflows/nodes que casaram com o texto procurado, evitando N chamadas de `get_workflow`. `set_workflow_tags_by_name` resolve nomes de tag para IDs (criando as que faltarem) e chama `set_workflow_tags` em uma única ferramenta.

## Backup remoto: download por URL e restauração de data table

Pensado para o cenário em que o servidor MCP está hospedado remotamente e o cliente MCP roda em outra máquina, onde um caminho de disco local do servidor (o que `backupFile` devolvia antes) não serve para nada:

- **Download do backup**: `backup_workflow`/`backup_data_table` retornam `backupFile` (nome do arquivo, usado para restaurar) e `backupUrl` (URL completa para baixar o conteúdo). `list_backups` traz o mesmo link em `downloadUrl` por item. A URL é montada em `McpTools.BuildBackupUrl`: usa `N8n:PublicBaseUrl` se configurado (útil atrás de reverse proxy sem forwarded headers), senão deriva `scheme://host` da própria requisição MCP recebida (via `IHttpContextAccessor`).
- **Endpoint de download** (`Program.cs`, `GET /backups/{fileName}`): aberto, sem autenticação — serve o arquivo direto do disco se existir (`404` caso contrário). Não exige headers `X-N8N-URL`/`X-N8N-API-KEY`, então qualquer client (inclusive browser simples) consegue baixar o link.
- **Restauração de data table**: `restore_data_table_backup` (paralelo ao já existente `restore_workflow_backup`) faz uma **substituição total** — exclui a tabela atual (se ainda existir) e a recria do zero com as colunas e linhas exatas do backup, sempre gerando um novo ID (mesmo padrão de `restore_workflow_backup` ao recriar um workflow excluído). As linhas são reinseridas em lotes de até 250, removendo antes os campos gerados pelo n8n (`id`, `createdAt`, `updatedAt`) via `StripRowMetadata`.
