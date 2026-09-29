# M16 — Integration follow-ups: 4 documented [KNOWN GAP] items

> **STATUS: SPEC_ONLY — awaiting operator go/no-go per item (see Decision Matrix).**
> This milestone is **NOT scheduled for implementation**. No `@code` dispatch follows this spec.
> Nothing herein modifies `DevelopmentPlan.md`, `AppGraph.xml`, or any `src/` file.

## Dependencies
- M12 (integration test harness — `TestFixture` testcontainer pattern)
- M13 @ commit `0937370` (ONNX memory hygiene — its runtime gate surfaced the 9 environmental failures)
- M13-fix @ commit `f99cddc` (TestFixture gRPC-port + 7-layer onion fix — its verification documented the 4 findings with scars + `[KNOWN GAP]`/FOLLOW-UP tags; see `tests/qa_report.md`, verdict SUCCESS)
- Evidence base: `tests/qa_report.md` (M13-fix verification §"4 out-of-scope findings"), memory entries 006/007/008

## Goal
Close the 4 documented `[KNOWN GAP]`/FOLLOW-UP items from the M13-fix verification as
**non-blocker, independently-schedulable work items**. Each item is a documented divergence
between a written contract (SPEC.md / AGENTS.md ADR) and the actual implementation — or a
test-environment convenience — that was deliberately NOT patched inside the M13-fix scope
(test-infra only, `src/` untouched).

**Explicitly NOT a regression milestone.** The M13/M13-fix gates are SUCCESS (35 pass;
9 environmental failures documented in memory entry 006). Production behavior is correct per
its scars; the divergences are documented, not silent. The operator selects which items (if
any) get scheduled; unscheduled items remain documented-only.

## Problem Statement (evidence)

### Item 1 — FU-1: ADR-005 compact-down status divergence (`skipped` vs `error`) — DECISION NEEDED
**Contract says:** ADR-005 (AGENTS.md §5) and SPEC.md §7 (`SPEC.md:215`) —
"Qdrant down (compact) → `status=error`, no source deletion".

**Implementation does:** `MemoryCompactTool.CompactAsync` wraps the batch fetch in
`QdrantResiliencePolicy.ExecuteWithFallbackAsync` with fallback = **empty array**
(`src/McpMemoryService/Tools/MemoryCompactTool.cs:99-103`). When Qdrant is down, the fallback
fires → `entries.Count < input.BatchSize` (`0 < N`, `MemoryCompactTool.cs:108`) → the ADR-004
insufficient-data branch returns `status=skipped, reason=insufficient_data`
(`MemoryCompactTool.cs:114`). The generic empty-list fallback **cannot distinguish "no
entries" from "Qdrant unreachable"**.

**Test evidence:** `FallbackTests.Compact_QdrantDown_ReturnsErrorNoDelete`
(`tests/.../Integration/FallbackTests.cs:289-306`) — the assert was widened to accept BOTH
`"error"` and `"skipped"`, with an explicit `FOLLOW-UP for @architect/@qa` scar
(`FallbackTests.cs:290-300`). The test's core safety intent (controlled DTO, no throw, no
source deletion) holds for both statuses.

**Decision point (operator):**
- **Option A — amend the contract (doc-only):** reclassify ADR-005 compact-down as
  `status=skipped` (behavior is defensible: non-blocking, no deletion either way). Cheapest;
  but `skipped/insufficient_data` is misleading for the `@memory` compact-threshold loop —
  a caller cannot distinguish "nothing to compact" from "infrastructure down; retry later".
- **Option B — change the behavior (src):** make the compact batch fetch distinguish
  unavailability (e.g., tool-level try/catch around `GetBatchForCompactAsync` instead of the
  empty-array resilience fallback) → return `status=error, reason=qdrant_unavailable` per
  ADR-005. ADR-004 (genuine insufficient data → `skipped`) is untouched either way.
- **@architect recommendation:** Option B — compact is the one mutating transaction in the
  toolset (ADR-003); hiding infra failure inside "nothing to do" erodes the caller's ability
  to make the retry decision ADR-005 was designed to inform.

### Item 2 — FU-2: `MemoryRetrieveResult` lacks `session_id`/`metadata` (M8 contract gap)
**Contract says:** SPEC.md §3 payload schema defines `session_id` (keyword) and `metadata`
(object) as stored fields; the M3 model carries both (`Models/MemoryPayload.cs:36` —
`SessionId` required; `:54` — `Metadata` optional). Capture persists them correctly
(`Mapping/PayloadMappingExtensions.cs:47` maps `session_id` — probe-verified in the E2E scar).

**Implementation does:** `MemoryRetrieveResult`
(`src/McpMemoryService/Contracts/MemoryRetrieveOutput.cs:45-75`) exposes only
`point_id, agent_role, entry_type, content, timestamp, score, tags` — **no `session_id`, no
`metadata`**. `MemoryRetrieveTool.MapToRetrieveResult` (`Tools/MemoryRetrieveTool.cs:136-148`)
cannot map what the DTO does not carry. The wire response never contains the keys.

**Test evidence:** `MemoryPipelineE2ETests.PayloadRoundTrip` asserts made conditional with
`[KNOWN GAP]` log lines (`tests/.../Integration/MemoryPipelineE2ETests.cs:330-351`) + scar
(`:320-329`) documenting the src contract omission (M8 retrieve output), explicitly out of
the M13-fix scope.

**Impact:** round-trip fidelity is incomplete for consumers needing session scoping or trace
context (e.g., Etap-2 `@memory` session-filtered retrieve). Additive output fields — no
wire-breaking change for existing consumers.

### Item 3 — FU-3: snake_case enum VALUES unenforceable over the MCP wire (input AND output)
**Contract says:** SPEC.md §4.1–§4.3 input schemas declare snake_case enum VALUES
(`"decision" | "bug_fix" | ...`, `"orchestrator" | "architect" | ...` — ADR-001/002 taxonomy).
The DTOs honor this via `[JsonConverter(typeof(SnakeCaseEnumConverter))]` on both enums
(`Enums/AgentRole.cs:32`, `Enums/EntryType.cs:33`; `Enums/SnakeCaseEnumConverter.cs`).

**Implementation does:** the MCP SDK's in-flight serializer (AIFunctionFactory marshaling for
input binding; SDK output serialization) **ignores the per-enum converter attribute** —
- Input: multi-word snake_case values are REJECTED (`"bug_fix"` → "The JSON value could not
  be converted to EntryType" — probe-verified, `TestFixture.cs:258-269` scar). Only numeric
  values or PascalCase member names bind (`"BugFix"` — used by `QdrantFilterTests.cs:72,139,155,226`).
- Output: enums serialize as PascalCase member names (`"Code"`, `"Insight"` — probe-verified,
  `MemoryPipelineE2ETests.cs:291-299` scar; case-insensitive compare in the test).
- The M7 scar (`Program.cs:130-179`) documents the compile probe: `McpServerOptions` on
  ModelContextProtocol **1.4.0** exposes NO serializer knob (`SerializerOptions` and
  `JsonSerializerOptions` both CS1061 — property absent). The M7 fallback (rung d:
  per-property `[JsonPropertyName]`) fixes property NAMES only, not enum VALUES.

**External check (best-effort, this spec):**
`[WEB_SEARCH_UNAVAILABLE]` SearXNG returned empty result sets (2 queries, 2026-09-25); the
SDK API-docs page is a JS redirect (S4). One usable source:
[SOURCE: fetch_and_extract, url=https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/README.md, ts=2026-09-25]
— the package lineup has evolved past 1.4.0 (`ModelContextProtocol.Core` split;
`Extensions.Apps`/`Extensions.Tasks`), but no serializer-customization documentation is
published. Whether a newer SDK exposes `McpServerOptions` serializer customization is
**[UNVERIFIED_VERSION]** — a compile probe (the M7 pattern) is the only authoritative check.

**Decision point (operator):** staged resolution —
1. **(c) SDK probe first (cheap):** bump `ModelContextProtocol.*` to latest stable +
   compile-probe for a serializer knob. If present → single-source fix at `Program.cs`
   (retire rung-d where redundant). If absent → fall to (a).
2. **(a) Document the divergence:** amend SPEC §4.1–§4.3 to state that enum VALUES travel as
   PascalCase member names (or numbers) over the MCP wire; Qdrant payload strings remain
   snake_case (`PayloadMappingExtensions` — verified by `ToPayload_MapsSnakeCaseEnums`).
   Harden tests to the documented contract.
3. **Rejected alternatives:** (b) tool DTOs as raw strings (loses type safety);
   (d) renaming enum members to snake_case identifiers (C#-legal, binds case-insensitively —
   but violates .NET naming conventions across every src reference).

### Item 4 — FU-4: 9 environmental test failures — external-Qdrant dependency
**Evidence:** the M13 full `Category=Integration` gate recorded 9 environmental failures
(memory entry 006; `qa_report.md`): **7 × `QdrantServiceTests`** integration section
(`tests/.../Services/QdrantServiceTests.cs:212-241` — hardcodes `localhost:6333/6334`, header
instructs `docker run -d --rm -p 6333:6333 -p 6334:6334 qdrant/qdrant`; 7 async tests at
`:265-441`) + **2 × `HostSmokeTests`** (`tests/.../Smoke/HostSmokeTests.cs:12,30-33` — raw
`WebApplicationFactory<Program>` with NO config override; `QdrantCollectionInitializer`
IHostedService, `Program.cs:219-221` blocks host startup without Qdrant at `localhost:6334`).

**Decision point (operator):**
- **Option A — operator-managed external Qdrant:** keep the classes as-is; document the
  prerequisite in AGENTS.md §2 (test commands). Zero code change; `HostSmokeTests` keeps its
  character as a true default-config host smoke.
- **Option B — convert to testcontainers (M12/M13-fix `TestFixture` pattern):** point both
  classes at a `QdrantContainer` (config-override for `HostSmokeTests`). Removes the manual
  prerequisite; erodes the "production defaults" character of `HostSmokeTests` (mitigation:
  keep one variant against defaults, or accept the override as the CI norm).
- **@architect recommendation:** Option B for `QdrantServiceTests` (pure CRUD round-trip
  suite — no semantic loss); hybrid for `HostSmokeTests` (container-backed by default,
  external-Qdrant run documented as the true-default smoke).

## Deliverables (ALL PLANNED — not implemented by this spec)
| Item | Deliverable | Type |
|---|---|---|
| FU-1 | Operator decision + either ADR-005 amendment (AGENTS.md §5) OR `MemoryCompactTool` batch-fetch error path + tightened `FallbackTests` assert | ADR edit / src change |
| FU-2 | `MemoryRetrieveResult` += `session_id`, `metadata` (`[JsonPropertyName]` overrides) + `MapToRetrieveResult` mapping + re-enabled unconditional E2E asserts | src + tests |
| FU-3 | Compile probe vs latest `ModelContextProtocol.*`; then either `Program.cs` serializer knob (if available) OR SPEC §4.1–§4.3 amendment + hardened tests | probe / SPEC edit / src |
| FU-4 | Per-class decision + either AGENTS.md §2 documentation OR testcontainer conversion | docs / tests |

## Contracts (per item, with acceptance criteria)

### FU-1 contract
- **A (amend):** ADR-005 row reads "Qdrant down (compact) → `status=skipped` (empty-batch
  fallback; no deletion)" + scar reference; `FallbackTests.Compact_QdrantDown` assert
  tightened to the decided status with documented reason.
- **B (fix):** with Qdrant down, compact returns `status=error, reason=qdrant_unavailable`;
  genuine insufficient data still returns `status=skipped, reason=insufficient_data` (ADR-004
  invariant); NO source deletion in either failure path (ADR-003 invariant); no exception
  escapes the tool (ADR-005 non-blocking invariant).

### FU-2 contract
- `MemoryRetrieveResult` gains `[JsonPropertyName("session_id")] required string SessionId`
  and `[JsonPropertyName("metadata")] Metadata? Metadata` (null-omitted,
  `JsonIgnoreCondition.WhenWritingNull`, consistent with capture-input mapping); all existing
  fields unchanged; additive only.
- Acceptance: `PayloadRoundTrip` unconditional asserts on `session_id` + `metadata`
  (`file_path`, `line_number`) PASS; `SnakeCaseTransportTests` extended to cover the two new
  properties round-trip under default options.

### FU-3 contract
- **Probe outcome recorded** (knob present/absent + SDK version) in the implementing
  milestone's notes and, if taken, the SPEC amendment `[SOURCE]`-tagged.
- **If knob path:** snake_case enum values bind on input AND serialize on output
  (`"bug_fix"`, `"code"` asserted verbatim in E2E); `QdrantFilterTests` revert to
  SPEC-literal `"bug_fix"` arguments.
- **If document path:** SPEC §4.1–§4.3 state PascalCase/numeric enum transport over MCP;
  tests assert the documented casing verbatim; Qdrant payload strings remain snake_case.

### FU-4 contract
- **A (document):** AGENTS.md §2 integration-test row notes the external-Qdrant prerequisite
  (`docker run -d --rm -p 6333:6333 -p 6334:6334 qdrant/qdrant`).
- **B (convert):** full `Category=Integration` gate runs green with ONLY Docker + ONNX model
  present (no manual Qdrant); no semantic assertions weakened; `HostSmokeTests` variant
  strategy recorded.

## Algorithm / Logic (design-level)

### FU-1 (if Option B chosen)
Move the batch fetch OUT of the generic empty-array resilience wrapper in
`MemoryCompactTool.CompactAsync`: tool-local `try/catch` around `GetBatchForCompactAsync`
(Qdrant-related exception → log `[IMP:WARN]` + return `error/qdrant_unavailable`; keep the
resilience policy for the later upsert/delete legs unchanged). Design constraint: the change
is confined to the batch-fetch leg; the ADR-003 transactional ordering (summarize → upsert →
hard delete) is untouched.

### FU-2
Add the two properties with attributes to the sibling record in `MemoryRetrieveOutput.cs`;
map from `entry.Payload.SessionId` / `entry.Payload.Metadata` in
`MapToRetrieveResult`; flip the two conditional E2E asserts to unconditional; extend the
transport test. No service-layer or Qdrant-layer changes — the data already round-trips
through `MemoryPayload`.

### FU-3
1. `dotnet add package ModelContextProtocol` (+ `.AspNetCore`) to latest stable in a scratch
   probe; compile-probe `McpServerOptions` for a serializer property (the M7 CS1061 pattern).
   Record outcome + version. 2a. If present: set the knob in the existing
   `AddOptions<McpServerOptions>` lambda (`Program.cs:121-179`), retire redundant per-DTO
   attributes only where provably covered, assert wire values verbatim. 2b. If absent: SPEC
   amendment + test hardening per the document path; keep rung-d attributes (harmless).

### FU-4 (if Option B chosen)
Reuse the M13-fix `TestFixture` wiring for `QdrantServiceTests` (container → mapped 6334 as
`GrpcPort`, 6333 as `Url` — the onion scars at `TestFixture.cs:93-124` are the checklist).
For `HostSmokeTests`: `WithWebHostBuilder` config override pointing at the container, plus
the recorded variant strategy for the true-default smoke run.

## Tests
| Item | Verification needed (when scheduled) |
|---|---|
| FU-1 | `FallbackTests.Compact_QdrantDown` single-status assert; new/updated unit test in `MemoryCompactToolTests` for the unavailable-batch path (mock `IQdrantService` throwing `RpcException`); `CompactTransactionTests` unchanged-green |
| FU-2 | `PayloadRoundTrip` unconditional asserts; `SnakeCaseTransportTests` extension; existing unit tests green (additive DTO) |
| FU-3 | E2E verbatim enum assertions both directions; `QdrantFilterTests` literals per decided path; `HostSmokeTests` unchanged-green |
| FU-4 | Full `Category=Integration` gate green under Docker-only prerequisites; per-class conversion diff review (no assertion weakening) |

## Acceptance Criteria (milestone-level — UNCHECKED, not scheduled)
- [ ] Operator go/no-go recorded for FU-1 (A: ADR amendment | B: src fix)
- [ ] FU-1 decided path implemented + verified per its contract
- [ ] Operator go/no-go recorded for FU-2; DTO + mapping + tests green
- [ ] FU-3 SDK probe outcome recorded (version + knob present/absent); decided path implemented
- [ ] FU-4 per-class decision recorded; decided path implemented; integration gate green under documented prerequisites
- [ ] No ADR-003/004/005 invariant regressed by any item (no deletion on failure; skipped for genuine insufficient data; no MCP connection break)
- [ ] All scars/FOLLOW-UP tags referenced by the implemented items updated (resolved), not deleted silently

## Decision Matrix (operator go/no-go)
| Item | Question | Options | Architect recommendation |
|---|---|---|---|
| FU-1 | Amend ADR-005 or fix behavior? | A doc-only / B src fix | **B** (compact is the mutating transaction; infra failure must not read as "nothing to do") |
| FU-2 | Add `session_id`/`metadata` to retrieve output? | add / keep documented-gap | **add** (cheap, additive, unblocks session-scoped retrieve) |
| FU-3 | Probe SDK then fix or document? | c probe → a document / skip | **probe then document-if-absent** (never string-DTOs) |
| FU-4 | External Qdrant or testcontainers? | A document / B convert | **B for QdrantServiceTests, hybrid for HostSmokeTests** |

## Context for @code (only when the operator schedules an item)
- Read this spec + the scars it cites (`FallbackTests.cs:290-300`, `MemoryPipelineE2ETests.cs:291-351`, `TestFixture.cs:93-124/258-269`, `Program.cs:130-179`).
- Commit refs: M13 = `0937370`, M13-fix = `f99cddc`.
- Skills: `csharp-conventions` (LDD markers, `#region`, scars preserved verbatim).
- FU-3 requires a package-version probe BEFORE any code change; record `[SOURCE]` / `[UNVERIFIED_VERSION]` per WEB_SEARCH_PROTOCOL.
- This milestone's items are INDEPENDENT — each may be scheduled alone (FU-1 ⊥ FU-2 ⊥ FU-3 ⊥ FU-4; no cross-dependencies).
