FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY N8nMcpHttp.slnx .
COPY src/Backend/McpToolkit/McpToolkit.csproj src/Backend/McpToolkit/
COPY src/Backend/N8nMcpHttp/N8nMcpHttp.csproj src/Backend/N8nMcpHttp/
RUN dotnet restore src/Backend/N8nMcpHttp/N8nMcpHttp.csproj

COPY src/ src/
RUN dotnet publish src/Backend/N8nMcpHttp/N8nMcpHttp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN useradd -m appuser \
    && mkdir -p /app/backups \
    && chown -R appuser:appuser /app
USER appuser

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "N8nMcpHttp.dll"]

## docker build -t filipefk/n8n-mcp:1.5 -t filipefk/n8n-mcp:latest .
## docker push filipefk/n8n-mcp:1.5
## docker push filipefk/n8n-mcp:latest