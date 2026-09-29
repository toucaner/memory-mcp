# M8 — MCP tool: memory_retrieve

## Dependencies
- M5 (IQdrantService — for vector search)
- M7 (MemoryCaptureTool — for tool registration pattern, InputValidator)

## Goal
Implement and register the `memory_retrieve` MCP tool: generate query embedding → semantic search in Qdrant with filters (project_id required, agent_role_filter + entry_type_filter optional) → format results as a numbered list. This is the core retrieval tool used by `@memory` subagent before dispatching agents.

## Deliverables
- `src/McpMemoryService/Tools/MemoryRetrieveTool.cs`
- Registration in `Program.cs` (add to MCP server alongside M7 tools)
- `tests/McpMemoryService.Tests/Tools/MemoryRetrieveToolTests.cs`

## Contracts

### MemoryRetrieveTool (SPEC §4.1 + M1 corrections S4, S5)

**Tool name:** `memory_retrieve`
**Description:** "Semantic search for relevant past experience."

**Input schema (MemoryRetrieveInput from M3):**
```json
{
  "type": "object",
  "properties": {
    "query": { "type": "string", "description": "Essence of the question/problem" },
    "project_id": { "type": "string" },
    "agent_role_filter": { "type": "string", "enum": ["orchestrator","architect","code","debug","qa"] },
    "entry_type_filter": { "type": "string", "enum": ["decision","bug_fix","requirement","summary","rejection","insight"] },
    "limit": { "type": "integer", "default": 5, "maximum": 10 }
  },
  "required": ["query", "project_id"]
}
```

**Output (MemoryRetrieveOutput):**
```json
{
  "results": [
    {
      "point_id": "<guid>",
      "agent_role": "debug",
      "entry_type": "bug_fix",
      "content": "...",
      "timestamp": "2026-06-30T12:00:00Z",
      "score": 0.85,
      "tags": ["circuit-breaker", "timeout"]
    }
  ]
}
```

**Logic (SPEC §4.1):**
1. Validate input (non-empty query, project_id; limit in [1, 10]).
2. Generate query embedding via `IEmbeddingService.EmbedAsync(query)`.
3. Search Qdrant via `IQdrantService.SearchAsync(vector, projectId, agentRoleFilter, entryTypeFilter, limit)`.
4. Map results to `MemoryRetrieveResult` list.
5. Return `MemoryRetrieveOutput { Results = [...] }`.
6. On Qdrant failure: return empty results (silent fallback per SPEC §7 — "memory unavailable" → empty, not exception).

### MemoryRetrieveTool.cs — implementation notes

```csharp
namespace McpMemoryService.Tools;

public sealed class MemoryRetrieveTool
{
    # region Fields
    private readonly IEmbeddingService _embeddingService;
    private readonly IQdrantService _qdrantService;
    private readonly ILogger<MemoryRetrieveTool> _logger;
    # endregion

    # region ExecuteAsync
    public async Task<MemoryRetrieveOutput> ExecuteAsync(MemoryRetrieveInput input, CancellationToken ct = default)
    {
        // 1. InputValidator.ValidateRetrieve(input)
        //    - query non-empty
        //    - project_id non-empty
        //    - Limit in [1, 10]
        //
        // 2. var vector = await _embeddingService.EmbedAsync(input.Query, ct)
        //    Log [IMP:1] embedding generated (dim)
        //
        // 3. var entries = await _qdrantService.SearchAsync(
        //        vector, input.ProjectId, input.AgentRoleFilter,
        //        input.EntryTypeFilter, input.Limit, ct)
        //    Log [IMP:2] search returned N results
        //
        // 4. Map entries → MemoryRetrieveResult list:
        //    - PointId, AgentRole, EntryType, Content, Timestamp, Score, Tags
        //
        // 5. Return MemoryRetrieveOutput { Results = results }
        //
        // EXCEPTION HANDLING:
        // - Qdrant failure → catch, log [IMP:3] warning, return empty Results
        //   (silent fallback per SPEC §7: "memory_retrieve should return empty,
        //    not throw and break MCP connection")
        // - Embedding failure → let propagate (ONNX failure is fatal per SPEC §7,
        //   but M10 will add wrapper policy — for now, throw)
    }
    # endregion
}
```

### Registration with MCP server

Add to the tool registration in Program.cs (alongside M7 tools):
```csharp
mcpServer.RegisterTool("memory_retrieve", memoryRetrieveTool.ExecuteAsync);
```

Input schema must be exposed via `tools/list` with:
- `query` (required, string)
- `project_id` (required, string)
- `agent_role_filter` (optional, enum: 5 roles per M1/S4)
- `entry_type_filter` (optional, enum: 6 types per M1/S5)
- `limit` (optional, integer, default 5, max 10)

## Algorithm / Logic

### Step 1: Implement MemoryRetrieveTool
1. Inject `IEmbeddingService`, `IQdrantService`, `ILogger<MemoryRetrieveTool>`.
2. Implement `ExecuteAsync` per the algorithm above.
3. Map `MemoryEntry` results from QdrantService to `MemoryRetrieveResult` DTOs.
4. Qdrant failure → silent empty results (catch + log warning, do NOT throw).
5. Logging: `[IMP:1]` embedding generated, `[IMP:2]` search complete (count), `[IMP:3]` fallback to empty on error.

### Step 2: Add retrieve validation to InputValidator
The `ValidateRetrieve` method was declared in M7. Ensure it is fully implemented:
- query: non-null, non-whitespace
- project_id: non-null, non-whitespace
- Limit: clamped/validated to [1, 10] (throw if outside)

### Step 3: Register tool with MCP server
1. Add `memory_retrieve` to MCP server registration alongside M7 tools.
2. Verify `tools/list` now returns 3 tools: capture, get_stats, retrieve.

### Step 4: Unit tests
Tests mock `IEmbeddingService` and `IQdrantService` to isolate tool logic.

## Tests (unit, inline)

### tests/.../Tools/MemoryRetrieveToolTests.cs
```csharp
public class MemoryRetrieveToolTests
{
    [Fact]
    public async Task ExecuteAsync_ValidInput_ReturnsResults()
    {
        // Arrange: mock embedding (float[384]), mock Qdrant SearchAsync returns 2 entries
        // Act: ExecuteAsync(validInput)
        // Assert: Results.Count == 2, fields mapped correctly
    }

    [Fact]
    public async Task ExecuteAsync_GeneratesEmbeddingFromQuery()
    {
        // Arrange: mock embedding service
        // Act: ExecuteAsync(input with query="memory leak")
        // Assert: EmbedAsync called with "memory leak"
    }

    [Fact]
    public async Task ExecuteAsync_PassesFiltersToQdrant()
    {
        // Arrange: mock Qdrant
        // Act: ExecuteAsync(input with agentRoleFilter=Debug, entryTypeFilter=BugFix)
        // Assert: SearchAsync called with AgentRole.Debug, EntryType.BugFix
    }

    [Fact]
    public async Task ExecuteAsync_RespectsLimit()
    {
        // Act: ExecuteAsync(input with limit=3)
        // Assert: SearchAsync called with limit=3
    }

    [Fact]
    public async Task ExecuteAsync_QdrantFails_ReturnsEmptyResults()
    {
        // Arrange: mock Qdrant SearchAsync throws
        // Act: ExecuteAsync
        // Assert: Results.Count == 0, NO exception thrown (silent fallback)
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQuery_ThrowsValidation()
    {
        // Act + Assert: throws ArgumentException
    }

    [Fact]
    public async Task ExecuteAsync_EmptyProjectId_ThrowsValidation()
    {
        // Act + Assert
    }

    [Fact]
    public async Task ExecuteAsync_LimitExceeds10_ThrowsValidation()
    {
        // Act: ExecuteAsync(input with limit=50)
        // Assert: throws ArgumentException
    }

    [Fact]
    public async Task ExecuteAsync_LimitZero_ThrowsValidation()
    {
        // Act + Assert
    }

    [Fact]
    public async Task ExecuteAsync_ResultsContainScoreFromQdrant()
    {
        // Arrange: mock returns entry with Score=0.92
        // Assert: result.Score == 0.92
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — all unit tests PASS
- [ ] MCP `tools/list` returns `memory_retrieve` alongside capture and get_stats
- [ ] `memory_retrieve` input schema matches SPEC §4.1 (with M1 corrections: 5 roles, entry_type_filter, limit max 10)
- [ ] Tool generates embedding from query, searches Qdrant, returns mapped results
- [ ] Filters (agent_role_filter, entry_type_filter) passed through to QdrantService
- [ ] Limit enforced in [1, 10] — validation throws on violation
- [ ] Qdrant failure → silent empty results (no exception, log warning)
- [ ] Results contain score from Qdrant semantic search
- [ ] Logs contain `[IMP:1]` embedding, `[IMP:2]` search, `[IMP:3]` fallback markers

## Context for @code
- Read: `SPEC.md` §4.1 (memory_retrieve spec), §7 (Qdrant error handling — silent empty)
- Read: `milestones/M1-foundation-spec-corrections.md` (S4: 5 roles in filter, S5: entry_type_filter added)
- Read: `milestones/M3-data-models-payload.md` (MemoryRetrieveInput/Output DTOs)
- Read: `milestones/M4-onnx-embedding-service.md` (IEmbeddingService.EmbedAsync)
- Read: `milestones/M5-qdrant-service.md` (IQdrantService.SearchAsync signature with filters)
- Read: `milestones/M7-mcp-tool-capture-stats.md` (tool registration pattern, InputValidator.ValidateRetrieve)
- Skills: `csharp-conventions` (#region, XML docs, DI, logging, silent fallback pattern)
- Previous artifacts: M5 (QdrantService with SearchAsync), M7 (tool registration pattern, InputValidator)
