# AGENTS.md — McpMemoryService

Project-specific context for AI agents (GRACE Protocol). Generic pipeline conventions live in `.opencode/rules/rules.md` and `.opencode/agent/*.md`. This file holds **project-specific** decisions, commands, and structure.

# STRUCTURE: > Read[SPEC.md] -> o M1..M12[milestones/] -> @architect[DevelopmentPlan.md] -> @code[impl] -> @qa[verify] -> = Done

## 1. Project Overview

**McpMemoryService** — stateless MCP-server (.NET 10) providing semantic memory tools for AI agent pipelines. Replaces the file-based `@memory` system with Qdrant-backed vector storage + ONNX embeddings + LLM summarization.

- **Product spec:** `SPEC.md` (architecture, Qdrant schema, 4 MCP tools, deployment)
- **Interaction protocol:** `SPEC-PROTOCOL.md` (how agents use the service — *migrated to MCP in Etap 2*)
- **Implementation plan:** `milestones/M1..M12-*.md` (12 recipe-style specs, DAG-ordered)

## 2. Build / Test / Lint Commands

| Action | Command |
|---|---|
| Restore + build solution | `dotnet build McpMemoryService.sln` |
| Run all tests | `dotnet test McpMemoryService.sln` |
| Run unit tests only | `dotnet test --filter "Category!=Integration"` |
| Run integration tests | `dotnet test --filter "Category=Integration"` (requires Docker) |
| Docker build | `docker build -t mcp-memory:latest .` |
| Docker run | `docker-compose up -d` |
| Health check | `curl http://localhost:5000/health` |
| Download ONNX model | `pwsh src/McpMemoryService/Scripts/Download-Model.ps1` |

**Target framework:** .NET 10 (`net10.0`). Nullable enabled. ImplicitUsings enabled.

## 3. Project Structure

```
memory-mcp/
├── SPEC.md                    # Product specification (source of truth for the service)
├── SPEC-PROTOCOL.md           # Agent interaction protocol (migration pending — Etap 2)
├── AGENTS.md                  # This file — project-specific ADRs + commands
├── milestones/                # 12 milestone specs (M1..M12)
├── src/McpMemoryService/      # Service source (created in M2)
│   ├── Configuration/         # Options classes
│   ├── Enums/                 # EntryType, AgentRole
│   ├── Models/                # MemoryPayload, MemoryEntry, Metadata
│   ├── Contracts/             # DTOs for 4 MCP tools
│   ├── Services/              # OnnxEmbeddingService, QdrantService, LlmSummarizerService
│   ├── Tools/                 # 4 MCP tools
│   ├── Mapping/               # Qdrant payload mapping
│   ├── Resilience/            # Fallback policies
│   ├── Middleware/            # GlobalExceptionMiddleware
│   ├── Logging/               # LddMarkers constants
│   ├── Scripts/               # Model download scripts
│   └── Models/                # ONNX model + tokenizer (gitignored)
├── tests/McpMemoryService.Tests/  # xUnit tests (unit + integration)
└── .opencode/                 # GRACE Protocol config (generic, cross-project)
    ├── rules/rules.md
    ├── agent/                 # architect, code, debug, qa, memory, orchestrator
    └── skills/                # csharp-conventions, devplan-protocol, etc.
```

## 4. State Files (read by @orchestrator every turn)

| File | Owner | Purpose |
|---|---|---|
| `DevelopmentPlan.md` | @architect | Current plan + decomposition (created at M1 design phase) |
| `AppGraph.xml` | @architect | Structural code map |
| `tests/test_guide.md` | @code | Progress checklist + `[IMP:N]` markers |
| `tests/qa_report.md` | @qa | Last verdict + bug report |
| `.opencode/state/phase_log.md` | @orchestrator | Routing journal (gitignored) |
| `.opencode/state/operations.ndjson` | @orchestrator | Structured operational log (gitignored) |
| `.opencode/state/phase_summary.md` | @monitor | Metrics summary (gitignored) |
| `.test_counter.json` | @debug | Anti-loop counter (gitignored) |
| `.opencode/memory/` | @memory | Local profile.md only (gitignored; full migration to MCP = Etap 2) |

If a state file does not exist, treat its content as empty.

## 5. ADRs (Architectural Decision Records)

These decisions are **fixed**. Do not re-litigate without operator approval.

### ADR-001: entry_type taxonomy = 6 types
**Decision:** `decision | bug_fix | requirement | summary | rejection | insight`
**Rationale:** Extends SPEC's original 4 with `rejection` (rejected architectural options) and `insight` (process observations, workarounds) for richer capture semantics.
**Source:** M1 spec, corrections S1, S6, S7.
**Compactable types:** `bug_fix`, `insight` (compact threshold > 20). `decision`, `rejection`, `requirement`, `summary` are never compacted.

### ADR-002: agent_role = 5 roles (including orchestrator)
**Decision:** `orchestrator | architect | code | debug | qa`
**Rationale:** Orchestrator captures entries "on behalf of" returning agents. Payload stores 5 roles; `agent_role_filter` in retrieve accepts all 5.
**Source:** M1 spec, correction S4.

### ADR-003: Compact semantics = hard delete
**Decision:** After LLM summarization succeeds, source entries are **physically deleted** from Qdrant. Summary entry (`entry_type=summary`) is created.
**Rationale:** Prevents unbounded DB growth. Summary preserves the essence. Transactional guarantee: if LLM fails (timeout/5xx), sources are NOT deleted.
**Source:** SPEC.md §4.4 step 7; M9 spec.

### ADR-004: Non-blocking compact on insufficient data
**Decision:** When `entries.Count < batch_size`, compact returns `{"status":"skipped","reason":"insufficient_data"}` — NOT an error.
**Rationale:** Aligns with non-blocking philosophy (SPEC §7). Prevents MCP connection breaks.
**Source:** M1 spec, correction S8.

### ADR-005: Silent fallback on MCP/Qdrant/LLM unavailability
**Decision:**
- Qdrant down (retrieve) → empty results, no exception
- Qdrant down (capture) → `success=false`, no exception
- Qdrant down (get_stats) → `count=-1`, no exception
- Qdrant down (compact) → `status=error`, no source deletion
- LLM down (compact) → `status=error` with reason, no source deletion
- ONNX fail at startup → Exit 1 (fatal)
- ONNX fail at runtime → caught by GlobalExceptionMiddleware (no connection break)
**Rationale:** Memory is a capability enhancer, not a dependency. Pipeline must not break when memory is unavailable.
**Source:** SPEC.md §7; M10 spec.

### ADR-006: .NET 10 target framework
**Decision:** `net10.0` for McpMemoryService.
**Rationale:** SPEC.md §2 requires .NET 10. `architect.md` and `mode-architect` skill updated in M1 to offer .NET 10.
**Source:** SPEC.md §2; M1 spec.

### ADR-007: project_id = SHA-256(absolute path)[:16]
**Decision:** `project_id` is computed as the first 16 hex characters of `SHA-256(absolute_path_of_workspace_root)`.
**Rationale:** Stable across folder renames within the same path; unique per project location; no manual config. Computed by @orchestrator at session start (Etap 2 migration).
**Source:** SPEC-PROTOCOL.md §2; final plan decision.

### ADR-008: Profile stored locally, not in Qdrant
**Decision:** Operator profile (preferences, retrieve-limits) stays in `.opencode/memory/profile.md`. NOT migrated to Qdrant.
**Rationale:** Profile has no semantic value — vector search is pointless. Local file allows per-machine customization without DB writes.
**Source:** M1 spec, correction S10; final plan decision.

### ADR-009: @memory subagent as MCP adapter (Etap 2 — ACTIVE)
**Decision:** After McpMemoryService is implemented (Etap 1 = M1..M12), the `@memory` subagent is refactored from file-based operations to MCP-tool calls. Orchestrator continues to dispatch `@memory` (not calling MCP-tools directly).
**Status:** AMENDED by ADR-013 (DB-primary). `rules.md`, `agent/memory.md`, `skills/memory-protocol/SKILL.md` refactored: MCP capture is **MANDATORY** and Qdrant is the source of truth; local files are an **offline outbox only** (written on DB failure, deleted after sync). Legacy dual-mode file mirror (`entries/`/`summaries/`) retired. `profile.md` remains local (ADR-008).

### ADR-010: MCP SDK = ModelContextProtocol.SDK (pending M2 validation)
**Decision:** Primary choice is `ModelContextProtocol.SDK`. @code validates stability + .NET 10 support via `web_search` in M2. Fallback: `StreamJsonRpc` + manual MCP protocol.
**Rationale:** Official SDK preferred for protocol compliance. Validation required because SDK maturity varies.
**Source:** M2 spec, step 1.

### ADR-011: ONNX model = paraphrase-multilingual-MiniLM-L12-v2
**Decision:** Model `paraphrase-multilingual-MiniLM-L12-v2` (384-dim, multilingual). Downloaded via script in M4.
**Rationale:** SPEC.md §3 specifies 384-dim vectors. Model is multilingual (RU+EN), small (~90-120MB), CPU-friendly.
**Source:** SPEC.md §3; M4 spec.

### ADR-011a: model artifact = dynamically-quantized int8 variant (amendment to ADR-011)
**Decision:** The deployed ONNX artifact is `onnx/model_quantized.onnx` (118,308,126 B = 112.8 MiB, dynamically-quantized int8) from the same Xenova/paraphrase-multilingual-MiniLM-L12-v2 repo. The destination filename stays `Models/model.onnx` (stable-path: OnnxModel:ModelPath, Dockerfile mount, compose volume unchanged); provenance = script size-assertion (118308126 ± 1 MiB) + variant print. Same `tokenizer.json`, same graph IO (`input_ids`/`attention_mask`/`token_type_ids` → `last_hidden_state`), same 384-dim L2-normalized output. Retrieval-quality gate: M4 semantic test + M14 multilingual test. Fallback: `model_fp16.onnx` (235,336,673 B = 224.4 MiB).
**Rationale:** FP32 file (470,268,510 B = 448.5 MiB) accounted for ~2/3 of the deployed RSS 674 MB; int8 dynamic quantization preserves embedding geometry at ~1/4 the footprint.
**Source:** milestones/M14-quantized-model.md; recorded M14.

### ADR-012: Wiki (BookStack) = publish layer, non-blocking read-only mirror
**Decision:** BookStack wiki is a **publish layer** for pipeline documentation, NOT a source of truth. Structure: shelf `Development` → one book per project (book name = human-readable project name, e.g. `McpMemoryService` — distinct from the DB `project_id`, e.g. `dev/memory-mcp`) → 4 chapters: `Overview`, `Milestone Reports`, `Decisions & ADRs`, `Lessons Learned`. Publishers: `@memory` (non-compactable entries decision/rejection/requirement → Decisions & ADRs; compact summaries → Lessons Learned; Overview card) and `@monitor` (milestone report page per COMMIT). Idempotent upsert by deterministic page slug + `wiki_page_id` stored in `index.json` (v3). Non-blocking per ADR-005 style: wiki failure → `[WIKI_PUBLISH_FAILED]` log + `pending_wiki_sync=true`, pipeline never blocked; recovery sync ≤ 5 pending per successful call.
**Rationale:** Pipeline state is gitignored/local and compact hard-deletes details (ADR-003) — the wiki provides a durable, human-readable, cross-project-visible mirror of permanent knowledge (ADRs, decisions, lessons, metrics) without changing memory semantics. Wiki never feeds pipeline state back (retrieve-augmentation from wiki is out of scope for now).
**Source:** Operator decision 2026-09-28; first book `McpMemoryService` created with ADR-001..011 backfill.

### ADR-013: Memory storage = DB-primary (Qdrant), files = offline outbox only (amendment to ADR-009)
**Decision:** Qdrant (via `mcp-memory` tools) is the single source of truth for memory. Every `@memory capture` MUST call `memory_capture`; on success **NO file is written**. Only when the DB is unreachable does `@memory` write a temporary outbox file (`.opencode/memory/outbox/{id}.md`, `pending_sync: true`). Outbox entries are synced opportunistically (≤5 per call) and, mandatorily, at session end via `@memory mode=flush` — after a successful sync the outbox file is **DELETED** (no dangling files). Compact uses DB counts (`memory_get_stats`) + `memory_compact`; there is no file-based compact. `index.json` (v4) becomes a local ledger of the outbox + per-entry `wiki_page_id`.
**Rationale:** The DB is authoritative and centrally queryable; the file mirror duplicated state, drifted from the DB, and was gitignored — its durability was an illusion (e.g. `searxng-mcp`'s `.opencode/memory/` is absent from the repo; only DB entries survived). Mandatory capture + mandatory session-end flush guarantees every cycle's memory lands in Qdrant; deleting synced files prevents stale dangling stubs.
**Consequence:** `@orchestrator` MUST dispatch `flush` before closing a session and MUST surface `status=pending` (`[MCP_SYNC_INCOMPLETE]`) instead of closing silently. Legacy `entries/`/`summaries/` are migrated to the DB once, verified, then deleted (skill §13).
**Canonical `project_id`:** `dev/memory-mcp` (matches the GitLab group path `dev/memory-mcp`). Earlier captures were split across the legacy ids `mcp-memory` and `memory-mcp`; the entries were re-captured under `dev/memory-mcp`. The legacy-id copies (both `Decision`, non-compactable) remain in Qdrant and can only be removed directly in Qdrant (the MCP API has no delete).
**Source:** Operator decision 2026-09-30.

## 6. Milestone DAG

```
M1 (foundation) -> M2 (skeleton) -> M3 (models) -> {M4 (onnx), M5 (qdrant), M6 (llm)}
                                                              |
                                                              v
                                          M7 (capture+stats) -> {M8 (retrieve), M9 (compact)}
                                                                        |
                                                                        v
                                              M10 (resilience) -> M11 (docker) -> M12 (e2e)
```

Parallel branches (M4/M5/M6 after M3; M8/M9 after M7) can be implemented independently.

## 7. Entry Points for Agents

- **@architect:** Read `SPEC.md` + `milestones/M1..M12` + this file's ADRs. Produce `DevelopmentPlan.md`.
- **@code:** Read the specific milestone spec + `DevelopmentPlan.md` + `AppGraph.xml`. Follow `csharp-conventions` skill.
- **@qa:** Read milestone acceptance criteria + run `dotnet test`. Cross-check LDD logs (`[IMP:N]` markers) against contracts.
- **@debug:** Read `tests/qa_report.md` + `.test_counter.json`. Analyze LDD logs `[IMP:7-10]` per `mode-debug` skill.
- **@monitor:** Read `operations.ndjson` at COMMIT. Compute metrics, write `phase_summary.md` + memory entry.

## 8. Notes

- **Etap 1 (current):** Implement McpMemoryService (M1..M12). File-based `@memory` pipeline works in parallel — do not touch agent prompts.
- **Etap 2 (current):** Migrated agent pipeline (rules.md, agent/memory.md, skills/memory-protocol) to use MCP-tools. `mcp-memory` activated in `opencode.json`. File-based memory logic replaced by Qdrant-backed MCP tools. Profile.md remains local.
- Models/*.onnx and Models/tokenizer.json are gitignored — download via `Scripts/Download-Model.ps1`.
- `.opencode/memory/` is gitignored (profile.md is private).
- **Wiki publish layer (ADR-012):** BookStack shelf `Development` → book per project. `@memory`/`@monitor` publish non-blocking mirrors (Overview, milestone reports, ADRs, lessons); wiki is never a source of truth. Protocol details: `skills/memory-protocol/SKILL.md` §12.
