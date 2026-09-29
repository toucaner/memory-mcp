# M15 — Container memory governance + runtime observability

## Dependencies
- M13 (native hygiene + ORT knobs — steady-state RSS target assumes it shipped)
- M14 (quantized model ~112.8 MiB — the limit value assumes it shipped)
- M11 (Dockerfile + docker-compose + `/health` healthcheck)

## Goal
Make the container memory limit **actually enforced** (the current `deploy.resources` block is not
applied in the operator's non-swarm runtime — the cgroup limit reads `31.2 GB`), add conservative
.NET GC + ONNX runtime environment defaults, and expose runtime/GC memory metrics so the budget can
be verified from Prometheus. Record the budget as **ADR-012**.

## Problem Statement (evidence)
- `docker-compose.yml:22-28` sets `deploy.resources.limits.memory: 512M` and
  `reservations.memory: 256M`. In the deployed container the observed metric `Limit: 31.2 GB`
  shows these are **not enforced** (the `deploy` block is only honored by Docker Swarm; plain
  `docker compose` / Portainer stacks in the operator's runtime ignore it, or the stack was started
  differently). The process is therefore free to grow to the host limit.
- ASP.NET Core defaults to **Server GC**, which creates per-logical-CPU heaps. On the 16-thread
  E5-2667 v4 host this commits a large baseline and, after the allocation-heavy startup
  (tokenizer reflection builds ~250k protobuf `Piece` objects, `OnnxEmbeddingService.cs:372-383`),
  retains committed memory that is never returned to the OS.
- There is no runtime memory telemetry: the OpenTelemetry wiring
  (`Program.cs:42-56`) adds AspNetCore + HttpClient instrumentation and a Prometheus exporter, but
  **no .NET runtime/GC or process-memory metrics**, so the budget cannot be observed or alerted on.
- The operator reached the service at `<url>`, while
  `docker-compose.yml:8-9` maps `5000:5000`. The port contract is ambiguous and must be resolved.

## Decision (per operator)
- **Memory limit:** hard `1 GiB` with `512 MiB` reservation (primary). Tighter `768 MiB` is
  acceptable only after the measured steady-state RSS (quantized model + M13) is ≤ ~450 MiB.
- **GC:** switch to Workstation GC (`DOTNET_gcServer=0`) because the service is low-concurrency
  (embedding calls are short) and the E5 host has few CPUs allotted (SPEC §6.2 CPU limit 1.0).
- **Hard heap ceiling:** `DOTNET_GCHeapHardLimit=0x30000000` (768 MiB) to leave native/ORT memory
  headroom under the 1 GiB cgroup limit.
- Record as **ADR-012** (Container memory budget + enforcement).

## Deliverables
- `docker-compose.yml` (enforced limits + GC/runtime env + port resolution)
- `Dockerfile` (default ENV for the GC/runtime knobs; override-friendly)
- `src/McpMemoryService/McpMemoryService.csproj` (`OpenTelemetry.Instrumentation.Runtime`,
  optionally `OpenTelemetry.Instrumentation.Process`)
- `src/McpMemoryService/Program.cs` (`.AddRuntimeInstrumentation()` / `.AddProcessInstrumentation()`)
- `docs/docker-deploy.md` (verification procedure + expected RSS + port note)
- `tests/test_guide.md` (measured values)
- ADR-012 registry note

## Contracts

### 1. Enforced limits (non-swarm keys)
Add to the `mcp-memory` service:
```yaml
    mem_limit: 1g          # enforced by plain `docker compose` (cgroup)
    mem_reservation: 512m
    deploy:
      resources:
        limits:
          memory: 1g       # kept for Swarm parity (consistent with mem_limit)
        reservations:
          memory: 512m
```
> Use `1g`/`512m` (primary). If steady-state RSS ≤ ~450 MiB, `768m`/`384m` may be used instead —
> but the value MUST be ≥ (steady-state RSS + ~30% headroom) to avoid OOMKill.

### 2. GC / runtime environment (compose `environment` + Dockerfile `ENV`)
```yaml
      - DOTNET_gcServer=0
      - DOTNET_GCConserveMemory=5
      - DOTNET_GCHeapHardLimit=0x30000000
```
- `DOTNET_gcServer=0` → Workstation GC (single heap; bounded committed memory).
- `DOTNET_GCConserveMemory=5` (0-9) → more aggressive heap trimming.
- `DOTNET_GCHeapHardLimit=0x30000000` = 768 MiB GC-heap ceiling (native/ORT memory excluded).
> Keep `ASPNETCORE_URLS`, `MODEL_DIR`, `Logging__*`, `Qdrant__*`, `LlmSummarizer__*` intact.

### 3. Runtime observability
```csharp
.AddMetrics(m => m
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()      // GC heap / allocation / threadpool metrics
    .AddProcessInstrumentation()      // process.memory.usage (working set)
    .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
    .AddPrometheusExporter()
    .AddOtlpExporter(...))
```
> **[UNVERIFIED_VERSION]** `AddRuntimeInstrumentation()` / `AddProcessInstrumentation()` and their
> package/API names must be validated against OpenTelemetry 1.19.x at compile. If a package is not
> compatible with `net10.0`, drop it and keep at minimum the `System.Runtime`-derived metrics that
> compile cleanly. Record the probe in `BUG_FIX_CONTEXT`.
> Expected Prometheus series (names approximate): `process_runtime_dotnet_gc_heap_size_bytes`,
> `process_memory_usage_bytes` (working set). Verify with
> `curl -s http://<host>:<port>/metrics | grep -iE "gc_heap|process_memory"`.

### 4. Port resolution
Resolve the `:5002` vs `5000:5000` discrepancy with the operator, then:
- If a reverse proxy fronts the service → document it and keep `5000:5000`.
- If the container should publish 5002 → make the host port configurable, e.g.
  `ports: ["${MCP_HOST_PORT:-5000}:5000"]`, and document `MCP_HOST_PORT=5002`.

### 5. Preserved invariants
- `/health` behavior and the healthcheck (`start_period: 60s`) unchanged.
- Wazuh syslog + OTLP exporters unchanged.
- The service still runs non-root (`USER appuser`) and with no CUDA deps.

## Algorithm / Logic

### Step 1: Compose + Dockerfile env
Add `mem_limit`/`mem_reservation`, the three `DOTNET_*` vars, and align `deploy`. Mirror the
`DOTNET_*` vars as Dockerfile `ENV` defaults (compose overrides win).

### Step 2: OTel runtime metrics
Add the two instrumentation packages; wire `.AddRuntimeInstrumentation()` (and
`.AddProcessInstrumentation()` if compatible) into the existing `WithMetrics` chain. Add a
`[IMP:M15]` log at startup confirming which instrumentation registered.

### Step 3: Verify enforcement + budget
```bash
docker compose up -d --force-recreate
docker stats --no-stream mcp-memory        # LIMIT column must read 1GiB (not 31.2GB)
curl -s http://<host>:<port>/health        # 200
curl -s http://<host>:<port>/metrics | grep -iE "gc_heap|process_memory"
```
Warm up (issue several `memory_capture` / `memory_retrieve` calls), then read steady-state RSS
from `docker stats` and from `/metrics`; record in `test_guide.md`. Compare against the M13/M14
target (~300 MB; ≤ ~450 MB).

### Step 4: Docs
Update `docs/docker-deploy.md`: correct limits, add the GC env explanation, the verification
commands, expected RSS, and the port note. Fix the stale "expected 256-400MB / 512MB" table.

### Step 5: Gate
`docker build` + `docker compose up` + healthcheck green under the enforced limit; no OOMKill in
logs. `dotnet build` + unit gate unaffected (Program.cs edit only).

## Tests
| Check | Method | Asserts |
|---|---|---|
| Limit enforced | `docker stats` | LIMIT = configured (1GiB), not host 31.2GB |
| Health | `curl /health` | 200 after warmup |
| Runtime metrics | `curl /metrics` | GC heap + process memory series present |
| Steady-state RSS | `docker stats` after warmup | ≤ target (documented), no OOMKill |
| Regression | `dotnet test` (unit) | unchanged |

## Acceptance Criteria
- [ ] `mem_limit`/`mem_reservation` present (non-swarm enforcement) and consistent with `deploy`
- [ ] `docker stats` LIMIT shows the configured limit (verifies enforcement)
- [ ] Container reaches `healthy` and stays up under the enforced limit (no OOMKill)
- [ ] `DOTNET_gcServer=0`, `DOTNET_GCConserveMemory=5`, `DOTNET_GCHeapHardLimit=0x30000000` applied
- [ ] `/metrics` exposes .NET runtime GC heap + process memory metrics
- [ ] Steady-state RSS measured and ≤ documented target (~300 MB; ceiling ~450 MB)
- [ ] `docs/docker-deploy.md` corrected (limits, GC env, verification, expected RSS)
- [ ] Port `:5002` vs `5000:5000` resolved and documented
- [ ] `dotnet build` + unit gate green; `/health` + Wazuh + OTLP unaffected
- [ ] ADR-012 recorded (memory budget + enforcement)

## Context for @code
- Read: `docker-compose.yml:22-35`, `Dockerfile:39-70`, `Program.cs:41-81`
- Read: `docs/docker-deploy.md` (Resource Limits + High memory usage + Troubleshooting)
- Read: `milestones/M11-dockerfile-docker-compose.md` (SPEC §6.2 intent)
- Read: `milestones/M13-onnx-memory-hygiene.md`, `milestones/M14-quantized-model.md` (targets)
- Skill: `csharp-conventions` (Program.cs edit only)
- `[UNVERIFIED_VERSION]`: OTel runtime/process instrumentation package names + metric names
- Do NOT relax the limit below measured RSS + headroom (OOMKill risk); sequence after M13/M14
