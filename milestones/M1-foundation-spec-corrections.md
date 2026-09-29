# M1 — Foundation: SPEC corrections + gitignore + .NET version

## Dependencies
None (first milestone).

## Goal
Prepare the source of truth (SPEC.md) and opencode configuration for McpMemoryService implementation: resolve internal SPEC contradictions, fix 6 entry_type values, add tags/entry_type_filter to schema, protect private memory files via .gitignore, align .NET version in architect.md to .NET 10.

## Deliverables
- `SPEC.md` — corrections S1-S8, S10 (see Contracts)
- `.gitignore` — add `.opencode/memory/` and `Models/*.onnx`, `Models/tokenizer.json`
- `.opencode/agent/architect.md` — .NET version fix (line 33)
- `.opencode/skills/mode-architect/SKILL.md` — .NET version fix (line 23)

## Contracts

### SPEC.md — exact corrections

**S1. Line 52 — entry_type enum (payload):**
```diff
- "entry_type": "string (keyword: decision|bug_fix|requirement|summary)"
+ "entry_type": "string (keyword: decision|bug_fix|requirement|summary|rejection|insight)"
```

**S2. Lines 55-58 — add tags to payload:**
```json
"tags": ["string"]   // keyword-array, indexed for tag-based filtering
```
Add `"tags"` field after `"metadata"` in the Payload structure.

**S3. Line 62 — extend index list:**
```diff
- Create keyword indexes for `project_id` and `entry_type` fields to ensure filtering speed `< 10ms`.
+ Create keyword indexes for `project_id`, `entry_type`, `agent_role` fields and `tags` array to ensure filtering speed `< 10ms`.
```

**S4. Line 77 — agent_role_filter enum (5 roles):**
```diff
- "agent_role_filter": { "type": "string", "enum": ["architect", "code", "debug", "qa"] },
+ "agent_role_filter": { "type": "string", "enum": ["orchestrator", "architect", "code", "debug", "qa"] },
```

**S5. Line 77 — add entry_type_filter to memory_retrieve input:**
After `agent_role_filter` add:
```json
"entry_type_filter": { "type": "string", "enum": ["decision", "bug_fix", "requirement", "summary", "rejection", "insight"] },
```
Parameter is optional. Required for QA-exception (entry_type_filter=insight).

**S6. Line 99 — memory_capture entry_type enum (5 types, no summary):**
```diff
- "entry_type": { "type": "string", "enum": ["decision", "bug_fix", "requirement"] },
+ "entry_type": { "type": "string", "enum": ["decision", "bug_fix", "requirement", "rejection", "insight"] },
```
`summary` is absent — created only via compact.

**S7. Line 119 — memory_get_stats entry_type enum (6 types):**
```diff
- "entry_type": { "type": "string", "enum": ["decision", "bug_fix", "requirement", "summary"] }
+ "entry_type": { "type": "string", "enum": ["decision", "bug_fix", "requirement", "summary", "rejection", "insight"] }
```

**S8. Line 141 — soften compact step 2 (non-blocking):**
```diff
- 2. If records `< batch_size`, terminate with error/status "insufficient data".
+ 2. If records `< batch_size`, return status `{"status":"skipped","reason":"insufficient_data","available": N, "required": batch_size}`. This is NOT an error — compact is skipped. Does not break MCP connection (complies with non-blocking philosophy §7).
```

**S10. Section 3 (payload) — profile note:**
After the Payload description add:
```markdown
**Note:** Operator settings (profile) and retrieve-limits are stored locally in `.opencode/memory/profile.md` and are NOT migrated to Qdrant. Profile has no semantic value and does not require vector search.
```

### .gitignore — add after line 353

```
# Memory pipeline local state (profile.md is private)
.opencode/memory/

# ONNX models (large binary, downloaded by script)
Models/*.onnx
Models/tokenizer.json
```

### .opencode/agent/architect.md — line 33

```diff
- - Choose between .NET 6/7/8, ASP.NET Core, Console App, Class Library.
+ - Choose between .NET 8 (LTS) / .NET 10 (target for McpMemoryService per SPEC.md), ASP.NET Core, Console App, Class Library.
```

### .opencode/skills/mode-architect/SKILL.md — line 23

```diff
- - Choose between .NET 6/7/8, ASP.NET Core, Console App, Class Library.
+ - Choose between .NET 8 (LTS) / .NET 10 (target for McpMemoryService per SPEC.md), ASP.NET Core, Console App, Class Library.
```

## Algorithm / Logic
1. Read `SPEC.md` in full.
2. Apply corrections S1-S8, S10 at the indicated lines.
3. Read `.gitignore`, add the block after line 353.
4. Read `architect.md:33`, apply the fix.
5. Read `mode-architect/SKILL.md:23`, apply the fix.
6. Verify consistency: enum in payload (:52) = enum in get_stats (:119) = 6 types; enum in capture (:99) = 5 types (no summary).

## Tests (unit, inline)
None (documentation milestone).

## Acceptance Criteria
- [ ] SPEC.md payload entry_type contains 6 values
- [ ] SPEC.md memory_capture entry_type contains 5 values (no summary)
- [ ] SPEC.md memory_retrieve input contains agent_role_filter with 5 roles
- [ ] SPEC.md memory_retrieve input contains entry_type_filter (optional)
- [ ] SPEC.md compact step 2 returns non-blocking status (not error)
- [ ] SPEC.md contains the note about local profile
- [ ] .gitignore contains `.opencode/memory/` and `Models/*.onnx`
- [ ] architect.md and mode-architect SKILL.md mention .NET 10
- [ ] All enum lists inside SPEC.md are consistent (no contradictions)

## Context for @code
- Read: `SPEC.md` (in full), `.gitignore` (tail), `.opencode/agent/architect.md:33`, `.opencode/skills/mode-architect/SKILL.md:23`
- Skills: not required (documentation milestone)
- Previous artifacts: none
