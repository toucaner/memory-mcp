# M7 — MCP tools: memory_capture + memory_get_stats

## Dependencies
- M3 (models, DTOs)
- M4 (IEmbeddingService — for vector generation in capture)
- M5 (IQdrantService — for upsert/count)
- M6 (ILlmSummarizerService — NOT directly used here, but service must be registered for DI completeness)

## Goal
Implement and register two MCP tools: `memory_capture` (generate embedding → L2 normalize → upsert to Qdrant with GUID v4) and `memory_get_stats` (count points with filters). Tools follow MCP protocol (input schema, output JSON). Includes input validation.

## Deliverables
- `src/McpMemoryService/Tools/IMcpTool.cs` (common interface, if SDK requires)
- `src/McpMemoryService/Tools/MemoryCaptureTool.cs`
- `src/McpMemoryService/Tools/MemoryGetStatsTool.cs`
- `src/McpMemoryService/Tools/ToolRegistry.cs` (or SDK-specific registration in Program.cs)
- `src/McpMemoryService/Validation/InputValidator.cs`
- Registration in `Program.cs` (register tools with MCP server per SDK pattern from M2)
- `tests/McpMemoryService.Tests/Tools/MemoryCaptureToolTests.cs`
- `tests/McpMemoryService.Tests/Tools/MemoryGetStatsToolTests.cs`

## Contracts

### MemoryCaptureTool (SPEC §4.2)

**Tool name:** `memory_capture`
**Description:** "Save a new fact/decision into memory."

**Input schema (MemoryCaptureInput from M3):**
```json
{
  "type": "object",
  "properties": {
    "content": { "type": "string" },
    "project_id": { "type": "string" },
    "agent_role": { "type": "string", "enum": ["orchestrator","architect","code","debug","qa"] },
    "entry_type": { "type": "string", "enum": ["decision","bug_fix","requirement","rejection","insight"] },
    "tags": { "type": "array", "items": { "type": "string" } },
    "session_id": { "type": "string" },
    "metadata": { "type": "object" }
  },
  "required": ["content", "project_id", "agent_role", "entry_type"]
}
```

**Output (MemoryCaptureOutput):**
```json
{ "success": true, "point_id": "<guid-v4-string>" }
```

**Logic (SPEC §4.2):**
1. Validate input (non-empty content, project_id; entry_type != summary).
2. Generate embedding via `IEmbeddingService.EmbedAsync(content)`.
3. Vector is already L2-normalized (OnnxEmbeddingService guarantees this — verify with assertion).
4. Generate `point_id` = Guid.NewGuid().
5. Build `MemoryPayload` (timestamp = DateTimeOffset.UtcNow).
6. Upsert via `IQdrantService.UpsertAsync(pointId, vector, payload)`.
7. Return `MemoryCaptureOutput { Success=true, PointId=pointId.ToString() }`.
8. On Qdrant failure: log + return `Success=false, Error="..."` (silent fail per SPEC §7 — do NOT throw).

### MemoryGetStatsTool (SPEC §4.3)

**Tool name:** `memory_get_stats`
**Description:** "Get memory entry count for compact threshold check."

**Input (MemoryGetStatsInput):**
```json
{
  "type": "object",
  "properties": {
    "project_id": { "type": "string" },
    "entry_type": { "type": "string", "enum": ["decision","bug_fix","requirement","summary","rejection","insight"] }
  },
  "required": ["project_id"]
}
```

**Output (MemoryGetStatsOutput):**
```json
{ "count": 42 }
```

**Logic (SPEC §4.3):**
1. Validate input (non-empty project_id).
2. Call `IQdrantService.CountAsync(projectId, entryTypeFilter)`.
3. Return `MemoryGetStatsOutput { Count = count }`.
4. On Qdrant failure: log + return `Count = -1` (or error response — see M10 for final policy).

### InputValidator.cs

```csharp
namespace McpMemoryService.Validation;

public static class InputValidator
{
    public static void ValidateCapture(MemoryCaptureInput input)
    {
        // Throw ArgumentException with clear message if:
        // - content is null/whitespace
        // - project_id is null/whitespace
        // - entry_type == Summary (not allowed via capture — only via compact)
    }

    public static void ValidateGetStats(MemoryGetStatsInput input)
    {
        // - project_id is null/whitespace
    }

    public static void ValidateRetrieve(MemoryRetrieveInput input)
    {
        // - query is null/whitespace
        // - project_id is null/whitespace
        // - Limit in [1, 10]
    }

    public static void ValidateCompact(MemoryCompactInput input)
    {
        // - project_id is null/whitespace
        // - BatchSize in [1, 100]
    }
}
```

### Tool registration pattern (SDK-specific)

Per the SDK choice from M2, tools are registered with the MCP server. Pattern (approximate — adapt to SDK):
```csharp
// In Program.cs or ToolRegistry.cs
mcpServer.RegisterTool("memory_capture", memoryCaptureTool.ExecuteAsync);
mcpServer.RegisterTool("memory_get_stats", memoryGetStatsTool.ExecuteAsync);
```

Each tool exposes an `ExecuteAsync(JsonElement args, CancellationToken ct)` method (or SDK-specific signature) that:
1. Deserializes args to the typed Input DTO.
2. Validates via InputValidator.
3. Executes business logic.
4. Serializes Output DTO to JSON response.
5. Logs `[IMP:1]` entry, `[IMP:2]` exit.

## Algorithm / Logic

### Step 1: Implement InputValidator
1. Create static validation methods for all 4 tool inputs (capture, get_stats, retrieve, compact).
2. Throw `ArgumentException` with descriptive messages.

### Step 2: Implement MemoryCaptureTool
1. Inject `IEmbeddingService`, `IQdrantService`, `ILogger<MemoryCaptureTool>`.
2. Implement `ExecuteAsync` per the logic above.
3. L2-normalization assertion: `Debug.Assert(vector norm ≈ 1.0)` — OnnxEmbeddingService guarantees this.
4. Silent Qdrant failure: catch `Exception`, log, return `Success=false`.

### Step 3: Implement MemoryGetStatsTool
1. Inject `IQdrantService`, `ILogger<MemoryGetStatsTool>`.
2. Implement `ExecuteAsync` per the logic above.
3. Qdrant failure handling: log + return error indicator (finalize in M10).

### Step 4: Register tools with MCP server
1. Per SDK pattern from M2, register both tools with their input schemas.
2. Input schemas must match SPEC §4.2, §4.3 (with M1 corrections S4, S6, S7).
3. Verify `tools/list` now returns both tools.

### Step 5: Unit tests
Tests mock `IEmbeddingService` and `IQdrantService` to isolate tool logic.

## Tests (unit, inline)

### tests/.../Tools/MemoryCaptureToolTests.cs
```csharp
public class MemoryCaptureToolTests
{
    [Fact]
    public async Task ExecuteAsync_ValidInput_ReturnsSuccessWithPointId()
    {
        // Arrange: mock IEmbeddingService (return float[384]),
        //          mock IQdrantService (UpsertAsync completes)
        // Act: ExecuteAsync(validInput)
        // Assert: Success==true, PointId is valid GUID string
    }

    [Fact]
    public async Task ExecuteAsync_GeneratesEmbeddingFromContent()
    {
        // Arrange: mock embedding service
        // Act: ExecuteAsync(input with content="test")
        // Assert: EmbedAsync called with "test"
    }

    [Fact]
    public async Task ExecuteAsync_GeneratesGuidV4PointId()
    {
        // Act: ExecuteAsync
        // Assert: PointId parses as Guid, version 4
    }

    [Fact]
    public async Task ExecuteAsync_QdrantFails_ReturnsFailureNotThrow()
    {
        // Arrange: mock IQdrantService.UpsertAsync throws
        // Act: ExecuteAsync
        // Assert: Success==false, Error populated, NO exception thrown
    }

    [Fact]
    public async Task ExecuteAsync_EntryTypeSummary_ThrowsValidation()
    {
        // Arrange: input with entry_type=summary
        // Act + Assert: throws ArgumentException
    }

    [Fact]
    public async Task ExecuteAsync_EmptyContent_ThrowsValidation()
    {
        // Act + Assert: throws ArgumentException
    }
}
```

### tests/.../Tools/MemoryGetStatsToolTests.cs
```csharp
public class MemoryGetStatsToolTests
{
    [Fact]
    public async Task ExecuteAsync_ValidInput_ReturnsCount()
    {
        // Arrange: mock IQdrantService.CountAsync returns 42
        // Act: ExecuteAsync
        // Assert: Count==42
    }

    [Fact]
    public async Task ExecuteAsync_PassesEntryTypeFilter()
    {
        // Arrange: mock
        // Act: ExecuteAsync with entry_type=bug_fix
        // Assert: CountAsync called with EntryType.BugFix
    }

    [Fact]
    public async Task ExecuteAsync_NoEntryTypeFilter_PassesNull()
    {
        // Assert: CountAsync called with null entryTypeFilter
    }

    [Fact]
    public async Task ExecuteAsync_EmptyProjectId_ThrowsValidation()
    {
        // Act + Assert
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — all unit tests PASS
- [ ] MCP `tools/list` returns `memory_capture` and `memory_get_stats`
- [ ] `memory_capture` input schema matches SPEC §4.2 (5 entry_type values, no summary)
- [ ] `memory_capture` generates embedding, upserts with GUID v4, returns success+point_id
- [ ] `memory_capture` on Qdrant failure returns `success=false` (does NOT throw)
- [ ] `memory_capture` rejects entry_type=summary with validation error
- [ ] `memory_get_stats` input schema matches SPEC §4.3 (6 entry_type values)
- [ ] `memory_get_stats` returns correct count from QdrantService
- [ ] `memory_get_stats` passes entry_type_filter when provided
- [ ] InputValidator covers all 4 tool inputs (capture, get_stats, retrieve, compact)
- [ ] Logs contain `[IMP:1]` entry, `[IMP:2]` exit markers

## Context for @code
- Read: `SPEC.md` §4.2 (memory_capture), §4.3 (memory_get_stats), §7 (error handling — silent capture fail)
- Read: `milestones/M1-foundation-spec-corrections.md` (S4: 5 roles, S6: 5 entry_type in capture, S7: 6 entry_type in stats)
- Read: `milestones/M3-data-models-payload.md` (DTOs), `milestones/M4-onnx-embedding-service.md` (IEmbeddingService), `milestones/M5-qdrant-service.md` (IQdrantService)
- Skills: `csharp-conventions` (#region, XML docs, DI, logging)
- Previous artifacts: M2 (MCP SDK registration pattern), M3 (DTOs), M4 (embedding), M5 (Qdrant)
- Web search: verify MCP tool registration API for chosen SDK (from M2)
