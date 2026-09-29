# M9 — MCP tool: memory_compact (LLM summarization + hard delete)

## Dependencies
- M5 (IQdrantService — GetBatchForCompactAsync, DeleteAsync, UpsertAsync)
- M6 (ILlmSummarizerService — SummarizeAsync with 60s timeout)
- M7 (MemoryCaptureTool — for internal summary capture; InputValidator)

## Goal
Implement and register the `memory_compact` MCP tool — the critical transactional operation: select a batch of old entries (excluding summary) → LLM summarize → capture new summary entry → **hard delete** source entries. Must be transactional: if LLM fails (timeout/5xx), source entries are NOT deleted. Non-blocking: insufficient data returns `status=skipped`, not an error.

## Deliverables
- `src/McpMemoryService/Tools/MemoryCompactTool.cs`
- Registration in `Program.cs` (4th tool)
- `tests/McpMemoryService.Tests/Tools/MemoryCompactToolTests.cs`

## Contracts

### MemoryCompactTool (SPEC §4.4 + M1 correction S8)

**Tool name:** `memory_compact`
**Description:** "Compress old memory entries into a summary via LLM."

**Input (MemoryCompactInput from M3):**
```json
{
  "type": "object",
  "properties": {
    "project_id": { "type": "string" },
    "batch_size": { "type": "integer", "default": 20 }
  },
  "required": ["project_id"]
}
```

**Output (MemoryCompactOutput from M3 — non-blocking per S8):**
```json
// Success:
{ "status": "completed", "source_count": 20, "summary_point_id": "<guid>" }

// Insufficient data (non-blocking per S8):
{ "status": "skipped", "reason": "insufficient_data", "available": 5, "required": 20 }

// LLM failure (source data NOT deleted):
{ "status": "error", "reason": "llm_timeout", "error": "..." }
{ "status": "error", "reason": "llm_5xx", "error": "..." }
```

**Logic (SPEC §4.4, with S8 non-blocking step 2):**
1. Validate input (non-empty project_id; batch_size in [1, 100]).
2. Fetch batch: `IQdrantService.GetBatchForCompactAsync(projectId, batchSize)` — excludes entry_type=summary, ordered by timestamp ASC.
3. If `entries.Count < batchSize` → return `status=skipped, reason=insufficient_data, available=entries.Count, required=batchSize`. **Non-blocking — NOT an error.**
4. Extract contents list from entries.
5. Call `ILlmSummarizerService.SummarizeAsync(contents)` with 60s timeout.
   - On `TaskCanceledException` (timeout) → return `status=error, reason=llm_timeout, error=message`. **Source entries NOT deleted.**
   - On `HttpRequestException` (5xx) → return `status=error, reason=llm_5xx, error=message`. **Source entries NOT deleted.**
6. On LLM success: capture summary entry internally — generate embedding for the summary text, upsert as new point with `entry_type=summary`, `agent_role=orchestrator` (compact is orchestrator-driven), timestamp=now.
7. Hard delete source entries: `IQdrantService.DeleteAsync(sourcePointIds)`.
8. Return `status=completed, source_count=entries.Count, summary_point_id=<new guid>`.
9. Logging: `[IMP:1]` batch fetched, `[IMP:2]` LLM call, `[IMP:3]` summary captured, `[IMP:4]` source deleted.

### MemoryCompactTool.cs — implementation notes

```csharp
namespace McpMemoryService.Tools;

public sealed class MemoryCompactTool
{
    # region Fields
    private readonly IQdrantService _qdrantService;
    private readonly ILlmSummarizerService _llmSummarizerService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<MemoryCompactTool> _logger;
    # endregion

    # region ExecuteAsync
    public async Task<MemoryCompactOutput> ExecuteAsync(MemoryCompactInput input, CancellationToken ct = default)
    {
        // 1. InputValidator.ValidateCompact(input)
        //    - project_id non-empty
        //    - BatchSize in [1, 100]

        // 2. var entries = await _qdrantService.GetBatchForCompactAsync(
        //        input.ProjectId, input.BatchSize, ct)
        //    Log [IMP:1] batch fetched (count)

        // 3. if (entries.Count < input.BatchSize)
        //    return new MemoryCompactOutput {
        //        Status = "skipped",
        //        Reason = "insufficient_data",
        //        Available = entries.Count,
        //        Required = input.BatchSize
        //    };

        // 4. var contents = entries.Select(e => e.Payload.Content).ToList();

        // 5. string summary;
        //    try {
        //        summary = await _llmSummarizerService.SummarizeAsync(contents, ct);
        //        Log [IMP:2] LLM summarization complete (length)
        //    }
        //    catch (TaskCanceledException ex) {
        //        Log [IMP:2] LLM timeout
        //        return new MemoryCompactOutput {
        //            Status = "error", Reason = "llm_timeout", Error = ex.Message
        //        };
        //        // SOURCE ENTRIES NOT DELETED — transactional safety
        //    }
        //    catch (HttpRequestException ex) {
        //        Log [IMP:2] LLM HTTP error
        //        return new MemoryCompactOutput {
        //            Status = "error", Reason = "llm_5xx", Error = ex.Message
        //        };
        //        // SOURCE ENTRIES NOT DELETED
        //    }

        // 6. Capture summary entry:
        //    var summaryPointId = Guid.NewGuid();
        //    var summaryVector = await _embeddingService.EmbedAsync(summary, ct);
        //    var summaryPayload = new MemoryPayload {
        //        ProjectId = input.ProjectId,
        //        SessionId = "compact",
        //        AgentRole = AgentRole.Orchestrator,
        //        EntryType = EntryType.Summary,
        //        Timestamp = DateTimeOffset.UtcNow,
        //        Content = summary,
        //        Tags = new[] { "compact", "summary" },
        //        Metadata = new Metadata {
        //            Session = $"compact-{DateTimeOffset.UtcNow:yyyyMMdd}"
        //        }
        //    };
        //    await _qdrantService.UpsertAsync(summaryPointId, summaryVector, summaryPayload, ct);
        //    Log [IMP:3] summary captured (pointId)

        // 7. Hard delete source entries:
        //    var sourceIds = entries.Select(e => e.PointId).ToList();
        //    await _qdrantService.DeleteAsync(sourceIds, ct);
        //    Log [IMP:4] source entries deleted (count)

        // 8. return new MemoryCompactOutput {
        //        Status = "completed",
        //        SourceCount = entries.Count,
        //        SummaryPointId = summaryPointId.ToString()
        //    };
    }
    # endregion
}
```

### Registration with MCP server

Add to tool registration in Program.cs (4th and final tool):
```csharp
mcpServer.RegisterTool("memory_compact", memoryCompactTool.ExecuteAsync);
```

## Algorithm / Logic

### Step 1: Implement MemoryCompactTool
1. Inject `IQdrantService`, `ILlmSummarizerService`, `IEmbeddingService`, `ILogger<MemoryCompactTool>`.
2. Implement `ExecuteAsync` per the algorithm above.
3. **CRITICAL transactional guarantee:** steps 6-7 (capture summary + delete source) must only execute if step 5 (LLM) succeeded. On any LLM failure, return error status WITHOUT deleting source entries.
4. Summary entry: `entry_type=summary`, `agent_role=orchestrator`, tags include "compact" and "summary".
5. Logging with `[IMP:1]`..`[IMP:4]` markers.

### Step 2: Add compact validation to InputValidator
Ensure `ValidateCompact` (declared in M7) is fully implemented:
- project_id: non-null, non-whitespace
- BatchSize: in [1, 100] (throw if outside)

### Step 3: Register tool with MCP server
1. Add `memory_compact` to MCP server registration.
2. Verify `tools/list` now returns all 4 tools: capture, get_stats, retrieve, compact.

### Step 4: Unit tests
Tests mock all three services (Qdrant, LLM, Embedding) to isolate tool logic and verify transactional behavior.

## Tests (unit, inline)

### tests/.../Tools/MemoryCompactToolTests.cs
```csharp
public class MemoryCompactToolTests
{
    [Fact]
    public async Task ExecuteAsync_SufficientData_CompletesAndDeletesSources()
    {
        // Arrange: mock Qdrant returns 20 entries, mock LLM returns "summary",
        //          mock Embedding returns float[384]
        // Act: ExecuteAsync(input with batchSize=20)
        // Assert: Status=="completed", SourceCount==20, SummaryPointId is GUID
        //         DeleteAsync called with 20 source IDs
        //         UpsertAsync called once (for summary)
    }

    [Fact]
    public async Task ExecuteAsync_InsufficientData_ReturnsSkippedNonBlocking()
    {
        // Arrange: mock Qdrant returns 5 entries (batchSize=20)
        // Act: ExecuteAsync
        // Assert: Status=="skipped", Reason=="insufficient_data",
        //         Available==5, Required==20
        //         LLM NOT called, Delete NOT called, Upsert NOT called
    }

    [Fact]
    public async Task ExecuteAsync_LlmTimeout_ReturnsErrorSourceNotDeleted()
    {
        // Arrange: mock LLM throws TaskCanceledException
        // Act: ExecuteAsync
        // Assert: Status=="error", Reason=="llm_timeout"
        //         DeleteAsync NOT called (transactional safety)
        //         UpsertAsync NOT called
    }

    [Fact]
    public async Task ExecuteAsync_Llm5xx_ReturnsErrorSourceNotDeleted()
    {
        // Arrange: mock LLM throws HttpRequestException
        // Act: ExecuteAsync
        // Assert: Status=="error", Reason=="llm_5xx"
        //         DeleteAsync NOT called
    }

    [Fact]
    public async Task ExecuteAsync_SummaryEntryHasCorrectPayload()
    {
        // Arrange: mocks
        // Act: ExecuteAsync
        // Assert: UpsertAsync called with payload:
        //   EntryType == Summary
        //   AgentRole == Orchestrator
        //   Tags contains "compact" and "summary"
        //   Content == LLM response
    }

    [Fact]
    public async Task ExecuteAsync_EmptyProjectId_ThrowsValidation()
    {
        // Act + Assert
    }

    [Fact]
    public async Task ExecuteAsync_BatchSizeZero_ThrowsValidation()
    {
        // Act + Assert
    }

    [Fact]
    public async Task ExecuteAsync_BatchSizeOver100_ThrowsValidation()
    {
        // Act + Assert
    }

    [Fact]
    public async Task ExecuteAsync_GeneratesEmbeddingForSummary()
    {
        // Arrange: mock LLM returns "summary text"
        // Act: ExecuteAsync
        // Assert: EmbedAsync called with "summary text"
    }

    [Fact]
    public async Task ExecuteAsync_ExcludesSummaryFromBatch()
    {
        // Assert: GetBatchForCompactAsync called (which excludes summary per M5)
        // — verify the call is made, M5 guarantees exclusion
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — all unit tests PASS
- [ ] MCP `tools/list` returns all 4 tools (capture, get_stats, retrieve, compact)
- [ ] `memory_compact` input schema matches SPEC §4.4
- [ ] Sufficient data → LLM summarize → capture summary → hard delete sources → return completed
- [ ] Insufficient data (`< batchSize`) → return `status=skipped` (non-blocking, no error)
- [ ] LLM timeout → return `status=error, reason=llm_timeout` — **source entries NOT deleted**
- [ ] LLM 5xx → return `status=error, reason=llm_5xx` — **source entries NOT deleted**
- [ ] Summary entry has `entry_type=summary`, `agent_role=orchestrator`, tags include "compact"
- [ ] BatchSize validated to [1, 100]
- [ ] Logs contain `[IMP:1]`..`[IMP:4]` markers
- [ ] Transactional guarantee verified by tests (no delete on LLM failure)

## Context for @code
- Read: `SPEC.md` §4.4 (compact logic, step 1-8), §7 (LLM error handling — return status, don't delete)
- Read: `milestones/M1-foundation-spec-corrections.md` (S8: non-blocking insufficient_data status)
- Read: `milestones/M3-data-models-payload.md` (MemoryCompactInput/Output DTOs)
- Read: `milestones/M5-qdrant-service.md` (GetBatchForCompactAsync, DeleteAsync, UpsertAsync)
- Read: `milestones/M6-llm-summarizer-service.md` (SummarizeAsync throws TaskCanceledException/HttpRequestException)
- Read: `milestones/M7-mcp-tool-capture-stats.md` (InputValidator.ValidateCompact, tool registration pattern)
- Skills: `csharp-conventions` (#region, XML docs, DI, transactional pattern, logging)
- Previous artifacts: M5 (Qdrant CRUD), M6 (LLM service), M7 (tool pattern + validator)
