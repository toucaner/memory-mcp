# M11 — Dockerfile + docker-compose

## Dependencies
- M10 (service hardened, all tools working, logging in place)

## Goal
Create production Docker artifacts: Dockerfile (runtime-only aspnet:10.0 image, no SDK, no CUDA), docker-compose.yml with strict resource limits (1 CPU, 512MB per SPEC §6), .dockerignore. Container must start and respond to MCP handshake.

## Deliverables
- `Dockerfile`
- `docker-compose.yml`
- `.dockerignore`
- `src/McpMemoryService/Scripts/download-model.sh` (already from M4 — verify Docker-friendly)
- `docs/docker-deploy.md` (brief deployment notes — optional)

## Contracts

### Dockerfile (SPEC §6.1)

```dockerfile
# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj + restore (layer cache)
COPY src/McpMemoryService/McpMemoryService.csproj ./McpMemoryService/
RUN dotnet restore ./McpMemoryService/McpMemoryService.csproj

# Copy source + build
COPY src/McpMemoryService/ ./McpMemoryService/
RUN dotnet publish ./McpMemoryService/McpMemoryService.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install no CUDA/libcuda dependencies (per SPEC §6.1)
# aspnet:10.0 base image has no CUDA — no action needed

# Copy published output
COPY --from=build /app/publish ./

# Copy ONNX model + tokenizer (downloaded by M4 script)
# Models/ is in .gitignore but must be in Docker context
COPY src/McpMemoryService/Models/ ./Models/

# Expose MCP port
EXPOSE 5000

# Environment defaults (overridable via docker-compose)
ENV ASPNETCORE_URLS=http://+:5000
ENV MODEL_DIR=/app/Models

# Non-root user (security best practice)
RUN adduser --disabled-password --gecos "" appuser
USER appuser

ENTRYPOINT ["dotnet", "McpMemoryService.dll"]
```

### docker-compose.yml (SPEC §6.2)

```yaml
services:
  mcp-memory:
    image: mcp-memory:latest
    container_name: mcp-memory
    build:
      context: .
      dockerfile: Dockerfile
    ports:
      - "5000:5000"
    environment:
      - QDRANT_URL=<url>
      - LLM_CPP_URL=<url>
      - MODEL_DIR=/app/Models
      - ASPNETCORE_URLS=http://+:5000
      - Logging__LogLevel__Default=Information
    deploy:
      resources:
        limits:
          cpus: '1.0'       # Strict 1 core per SPEC §6.2
          memory: 512M      # Strict 512MB (model ~200MB + runtime ~150MB + buffers)
        reservations:
          memory: 256M
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5000/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 60s
```

### .dockerignore

```
# Git
.git
.gitignore

# Build artifacts
**/bin/
**/obj/
artifacts/

# IDE
.vs/
.vscode/
.idea/

# Tests (not needed in runtime image)
tests/

# Documentation
milestones/
*.md
!src/McpMemoryService/Scripts/*.sh
!src/McpMemoryService/Scripts/*.ps1

# Docker files themselves
Dockerfile
docker-compose.yml
.dockerignore

# ONNX model source (will be COPY'd explicitly, but exclude from context bloat)
# NOTE: Models/ must be in context for COPY in Dockerfile
# Keep Models/ in context

# OS files
Thumbs.db
.DS_Store
```

## Algorithm / Logic

### Step 1: Create .dockerignore
1. Exclude build artifacts, IDE files, tests, docs, git.
2. **Keep `src/McpMemoryService/Models/`** in context — needed for COPY in Dockerfile.
3. Verify model files exist locally (M4 download script was run).

### Step 2: Create Dockerfile
1. Multi-stage build: SDK stage for publish, aspnet:10.0 runtime stage.
2. Publish with `/p:UseAppHost=false` (framework-dependent).
3. COPY published output + Models/ directory.
4. Set environment defaults (ASPNETCORE_URLS, MODEL_DIR).
5. Create non-root user `appuser` for security.
6. EXPOSE 5000.
7. ENTRYPOINT: `dotnet McpMemoryService.dll`.

### Step 3: Create docker-compose.yml
1. Image: `mcp-memory:latest` (built from local Dockerfile).
2. Port mapping: 5000:5000.
3. Environment variables per SPEC §6.2:
   - QDRANT_URL=<url>
   - LLM_CPP_URL=<url>
   - MODEL_DIR=/app/Models
4. Resource limits (STRICT per SPEC §6.2):
   - cpus: '1.0'
   - memory: 512M
   - reservations.memory: 256M
5. Healthcheck: curl `/health` endpoint.
6. restart: unless-stopped.

### Step 4: Build and smoke test
1. `docker build -t mcp-memory:latest .`
2. `docker-compose up -d`
3. Wait for healthcheck to pass (start_period: 60s — ONNX model loads slowly).
4. Verify: `curl http://localhost:5000/health` → 200.
5. Verify MCP handshake: `tools/list` returns 4 tools.
6. Verify resource limits: `docker stats mcp-memory` — CPU ≤ 1.0, MEM ≤ 512M.

### Step 5: Document deployment (optional)
Brief notes in `docs/docker-deploy.md`:
- Prerequisites: Qdrant running at QDRANT_URL, llama.cpp at LLM_CPP_URL.
- Build + run commands.
- How to check logs: `docker logs mcp-memory`.
- How to update model: re-run M4 download script, rebuild image.

## Tests (unit, inline)
None (infrastructure milestone — verified via smoke test in Algorithm).

### Smoke test verification (manual or scripted)
```bash
# 1. Build
docker build -t mcp-memory:latest .

# 2. Run
docker-compose up -d

# 3. Wait for healthy
timeout 90 bash -c 'until docker inspect --format="{{.State.Health.Status}}" mcp-memory | grep healthy; do sleep 5; done'

# 4. Health check
curl -f http://localhost:5000/health && echo "OK"

# 5. MCP tools/list (4 tools expected)
# (use MCP client or raw HTTP depending on SDK)

# 6. Resource limits
docker stats --no-stream mcp-memory
# Assert: CPU ≤ 1.0, MEM ≤ 512M

# 7. Logs contain [IMP:N] markers
docker logs mcp-memory | grep "\[IMP:"
```

## Acceptance Criteria
- [ ] `docker build -t mcp-memory:latest .` succeeds
- [ ] `docker-compose up -d` starts container
- [ ] Container healthcheck passes (start_period 60s for ONNX load)
- [ ] `curl http://localhost:5000/health` returns 200
- [ ] MCP `tools/list` returns 4 tools (capture, get_stats, retrieve, compact)
- [ ] Container runs as non-root user `appuser`
- [ ] Resource limits enforced: CPU ≤ 1.0, MEM ≤ 512M (verify via `docker stats`)
- [ ] No CUDA/libcuda dependencies in image
- [ ] Base image: `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime-only, no SDK)
- [ ] Models/ directory included in image (model.onnx + tokenizer.json)
- [ ] Environment variables configurable (QDRANT_URL, LLM_CPP_URL, MODEL_DIR)
- [ ] `restart: unless-stopped` configured
- [ ] Logs visible via `docker logs mcp-memory` with `[IMP:N]` markers

## Context for @code
- Read: `SPEC.md` §6 (deployment requirements — Dockerfile + docker-compose)
- Read: `milestones/M4-onnx-embedding-service.md` (Models/ directory, download script)
- Read: `milestones/M10-error-handling-resilience-logging.md` (logging config, health endpoint)
- Skills: `csharp-conventions` (not directly applicable — Docker files)
- Previous artifacts: M2 (appsettings.json), M4 (Models/), M10 (health endpoint, logging)
- External prerequisite: Docker installed, model downloaded (M4 script run)
- Note: Resource limits (cpus: '1.0', memory: 512M) are STRICT per SPEC §6.2 — do not relax
