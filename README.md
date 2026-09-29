# McpMemoryService

A stateless MCP server on .NET 10 that gives AI agent pipelines **semantic memory**: Qdrant vector
store + ONNX Runtime embeddings + LLM summarization via llama.cpp. It replaces the file-based
`@memory` system with a centralized, semantically searchable store.

## Architecture

```
MCP client ──POST /mcp──► McpMemoryService (ASP.NET Core, stateless)
                              ├─ Tools/           4 MCP tools
                              ├─ IEmbeddingService ── ONNX (384-dim, int8 MiniLM)
                              ├─ IQdrantService ───── Qdrant (gRPC)
                              └─ ILlmSummarizerService ── llama.cpp HTTP
```

No server state: every request is independent. If Qdrant/LLM is unavailable, the service degrades
gracefully instead of breaking the MCP connection. Endpoints: `POST /mcp`, `GET /health`, `GET /metrics`.

## MCP tools

| Tool | Purpose |
|---|---|
| `memory_capture` | Embed and persist a new entry |
| `memory_retrieve` | Semantic search (`query`, `project_id`, optional filters/`limit`) |
| `memory_get_stats` | Entry count (optional `entry_type` filter) |
| `memory_compact` | LLM-summarize a batch of old entries into one `summary`; hard-deletes sources on success |

`entry_type`: `decision`, `bug_fix`, `requirement`, `summary`, `rejection`, `insight`.

## Stack

- .NET 10 / ASP.NET Core Minimal API; `ModelContextProtocol` 1.4.0 (stateless Streamable-HTTP)
- Qdrant (`Qdrant.Client`), ONNX Runtime + ML.Tokenizers, model `paraphrase-multilingual-MiniLM-L12-v2`
- OpenTelemetry (OTLP + Prometheus), Serilog → UDP syslog (Wazuh)
- xUnit + Moq + FluentAssertions, Testcontainers for integration tests

## Build and run

```bash
dotnet build McpMemoryService.slnx
dotnet run --project src/McpMemoryService
pwsh src/McpMemoryService/Scripts/Download-Model.ps1   # fetch the ONNX model (~113 MiB)
```

Docker: `cp .env.example .env` (fill in values) then `docker compose up -d` (model mounted at `/app/Models`).

Tests: `dotnet test McpMemoryService.slnx` (`--filter "Category!=Integration"` for unit-only;
integration requires Docker).

## Configuration

Settings come from `appsettings.json`, overridden by environment variables (`Qdrant:Url` → `Qdrant__Url`).
Key sections: `Qdrant`, `OnnxModel`, `LlmSummarizer`, `Mcp`, `WazuhLogging`, `Otlp`.
Deployment values are supplied via `.env` (gitignored) or env vars.