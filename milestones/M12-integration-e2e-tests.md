# M12 — Integration / E2E tests

## Dependencies
- M7 (memory_capture, memory_get_stats)
- M8 (memory_retrieve)
- M9 (memory_compact)
- M11 (Docker image for full E2E)

## Goal
Create comprehensive integration and E2E tests verifying the complete memory pipeline: capture → retrieve → stats → compact round-trip with real Qdrant (via testcontainers), fallback scenarios (Qdrant/LLM down), hard-delete verification after compact, and filter correctness. These tests complement unit tests from M4-M9 by exercising the full stack.

## Deliverables
- `tests/McpMemoryService.Tests/Integration/MemoryPipelineE2ETests.cs` (full round-trip)
- `tests/McpMemoryService.Tests/Integration/QdrantFilterTests.cs` (filter correctness)
- `tests/McpMemoryService.Tests/Integration/CompactTransactionTests.cs` (hard delete + LLM failure)
- `tests/McpMemoryService.Tests/Integration/FallbackTests.cs` (Qdrant/LLM down scenarios)
- `tests/McpMemoryService.Tests/Integration/TestFixture.cs` (shared Qdrant testcontainer setup)
- Update `tests/McpMemoryService.Tests/McpMemoryService.Tests.csproj` (add Testcontainers packages)

## Contracts

### TestFixture.cs — shared setup

```csharp
namespace McpMemoryService.Tests.Integration;

public sealed class TestFixture : IAsyncLifetime
{
    # region Fields
    private Testcontainer _qdrantContainer;
    public HttpClient Client { get; private set; }
    public string QdrantUrl { get; private set; }
    public string ProjectId { get; } = "e2e-test-project";
    # endregion

    # region InitializeAsync
    // 1. Start Qdrant testcontainer:
    //    _qdrantContainer = new TestcontainerBuilder<QdrantContainer>()
    //        .WithImage("qdrant/qdrant")
    //        .WithPortBinding(6333, true)  // random host port
    //        .Build();
    //    await _qdrantContainer.StartAsync();
    // 2. QdrantUrl = _qdrantContainer.ConnectionString
    // 3. Build WebApplicationFactory<Program> with QDRANT_URL overridden to QdrantUrl
    // 4. Client = factory.CreateClient()
    // 5. Wait for health: GET /health until 200 (ONNX model loads)
    # endregion

    # region DisposeAsync
    // Stop Qdrant container, dispose factory
    # endregion

    # region Helpers
    public async Task<JsonElement> CallToolAsync(string toolName, object args);
    public async Task ResetAsync();  // delete all points for ProjectId
    # endregion
}
```

### Test categories

| Category | Scope | Real services | Mock |
|---|---|---|---|
| E2E round-trip | capture→retrieve→stats→compact | Qdrant (testcontainer), ONNX (real) | LLM (mock via HttpMessageHandler) |
| Qdrant filters | capture with variants → retrieve with filters | Qdrant (testcontainer), ONNX (real) | LLM (n/a) |
| Compact transaction | compact success (hard delete) + compact LLM-fail (no delete) | Qdrant (testcontainer), ONNX (real) | LLM (mock success + mock timeout) |
| Fallback | Qdrant down, LLM down | Qdrant (stop container), ONNX (real) | LLM (n/a or mock) |

## Algorithm / Logic

### Step 1: Add Testcontainers dependencies
Update `tests/McpMemoryService.Tests/McpMemoryService.Tests.csproj`:
```xml
<PackageReference Include="Testcontainers.Qdrant" Version="latest" />
```

### Step 2: Implement TestFixture
1. Start Qdrant testcontainer with random host port.
2. Override `QDRANT_URL` in WebApplicationFactory configuration.
3. Use real ONNX model (from M4 Models/ directory).
4. Mock LLM via configurable HttpMessageHandler (for compact tests).
5. `CallToolAsync` helper: invoke MCP tool via HTTP (or SDK client).
6. `ResetAsync` helper: delete all points for the test project between tests.

### Step 3: Implement E2E round-trip tests
Full pipeline verification:
1. Capture N entries (mix of entry_types: decision, bug_fix, insight, rejection).
2. Retrieve by semantic query → verify relevant entries returned with scores.
3. Get stats → verify count matches captured entries.
4. Compact → verify summary created, source entries deleted.
5. Get stats again → verify count decreased (sources gone, summary added).
6. Retrieve → verify summary is retrievable.

### Step 4: Implement Qdrant filter tests
1. Capture entries with different agent_role and entry_type.
2. Retrieve with agent_role_filter → only matching entries.
3. Retrieve with entry_type_filter → only matching entries.
4. Retrieve with both filters → intersection.
5. Retrieve with project_id mismatch → empty results (isolation).
6. Get stats with entry_type filter → correct count per type.

### Step 5: Implement compact transaction tests
1. **Success path:** Capture 20 entries → compact → verify:
   - 20 source entries deleted (hard delete)
   - 1 summary entry created (entry_type=summary, agent_role=orchestrator)
   - Status == "completed"
2. **LLM timeout path:** Capture 20 entries → mock LLM throws TaskCanceledException → verify:
   - 0 entries deleted (transactional safety)
   - Status == "error", reason == "llm_timeout"
   - All 20 source entries still retrievable
3. **Insufficient data path:** Capture 5 entries → compact with batchSize=20 → verify:
   - Status == "skipped", reason == "insufficient_data"
   - No entries deleted, no summary created
4. **LLM 5xx path:** Capture 20 → mock LLM throws HttpRequestException → verify:
   - 0 entries deleted
   - Status == "error", reason == "llm_5xx"

### Step 6: Implement fallback tests
1. **Qdrant down (retrieve):** Stop Qdrant container → retrieve → verify empty results, no exception.
2. **Qdrant down (capture):** Stop Qdrant → capture → verify `success=false`, no exception.
3. **Qdrant down (get_stats):** Stop Qdrant → get_stats → verify `count=-1`, no exception.
4. **Qdrant down (compact):** Stop Qdrant → compact → verify `status=error`, no delete.
5. **LLM down (compact):** Mock LLM unreachable → compact → verify `status=error`, no delete (covered in Step 5).
6. **ONNX runtime failure:** (harder to simulate — optional) inject faulty embedding → verify middleware catches, returns 500, no connection break.

## Tests (integration)

### tests/.../Integration/MemoryPipelineE2ETests.cs
```csharp
public class MemoryPipelineE2ETests : IClassFixture<TestFixture>
{
    [Fact]
    public async Task FullRoundTrip_Capture_Retrieve_Stats_Compact()
    {
        // 1. Capture 5 entries (decision, bug_fix, insight, rejection, requirement)
        // 2. Retrieve "threading error" → verify bug_fix/insight rank higher
        // 3. Get stats → count == 5
        // 4. Compact (batchSize=5) → completed
        // 5. Get stats → count == 1 (only summary)
        // 6. Retrieve → summary is retrievable
    }

    [Fact]
    public async Task Capture_PreservesAllPayloadFields()
    {
        // Capture entry with tags + metadata
        // Retrieve → verify tags, metadata, timestamp, agent_role, entry_type preserved
    }

    [Fact]
    public async Task Retrieve_SemanticRelevance_RanksCorrectly()
    {
        // Capture: "deadlock in payment service", "borscht recipe", "circuit breaker tripping"
        // Retrieve "concurrency issue in payments" → deadlock ranks highest
    }
}
```

### tests/.../Integration/QdrantFilterTests.cs
```csharp
public class QdrantFilterTests : IClassFixture<TestFixture>
{
    [Fact]
    public async Task Retrieve_FilterByAgentRole_ReturnsOnlyMatching()
    {
        // Capture entries with roles: architect, code, debug, qa
        // Retrieve with agent_role_filter=debug → only debug entries
    }

    [Fact]
    public async Task Retrieve_FilterByEntryType_ReturnsOnlyMatching()
    {
        // Capture entries with types: decision, bug_fix, insight
        // Retrieve with entry_type_filter=bug_fix → only bug_fix
    }

    [Fact]
    public async Task Retrieve_FilterByProjectId_IsolatesProjects()
    {
        // Capture to projectA and projectB
        // Retrieve projectA → no projectB entries
    }

    [Fact]
    public async Task GetStats_FilterByEntryType_ReturnsCorrectCount()
    {
        // Capture 3 bug_fix + 2 decision
        // GetStats entry_type=bug_fix → 3
    }

    [Fact]
    public async Task Retrieve_LimitEnforced()
    {
        // Capture 10 entries
        // Retrieve limit=3 → exactly 3 results
    }
}
```

### tests/.../Integration/CompactTransactionTests.cs
```csharp
public class CompactTransactionTests : IClassFixture<TestFixture>
{
    [Fact]
    public async Task Compact_Success_HardDeletesSources_CreatesSummary()
    {
        // Capture 20 entries
        // Compact → completed
        // Verify: 20 source IDs gone, 1 summary exists (entry_type=summary, agent_role=orchestrator)
    }

    [Fact]
    public async Task Compact_LlmTimeout_PreservesSources()
    {
        // Capture 20, mock LLM timeout
        // Compact → error/llm_timeout
        // Verify: all 20 sources still retrievable
    }

    [Fact]
    public async Task Compact_Llm5xx_PreservesSources()
    {
        // Capture 20, mock LLM 500
        // Compact → error/llm_5xx
        // Verify: all 20 sources still retrievable
    }

    [Fact]
    public async Task Compact_InsufficientData_ReturnsSkipped()
    {
        // Capture 5, compact batchSize=20
        // → skipped/insufficient_data, available=5, required=20
        // Verify: 5 sources still present, no summary
    }

    [Fact]
    public async Task Compact_SummaryHasCorrectMetadata()
    {
        // Compact success
        // Verify summary entry: entry_type=summary, agent_role=orchestrator,
        //   tags contains "compact" and "summary"
    }
}
```

### tests/.../Integration/FallbackTests.cs
```csharp
public class FallbackTests : IClassFixture<TestFixture>
{
    [Fact]
    public async Task Retrieve_QdrantDown_ReturnsEmptyNoException()
    {
        // Stop Qdrant container
        // Retrieve → empty results, no exception
    }

    [Fact]
    public async Task Capture_QdrantDown_ReturnsFailureNoException()
    {
        // Stop Qdrant
        // Capture → success=false, no exception
    }

    [Fact]
    public async Task GetStats_QdrantDown_ReturnsNegativeOne()
    {
        // Stop Qdrant
        // GetStats → count=-1
    }

    [Fact]
    public async Task Compact_QdrantDown_ReturnsErrorNoDelete()
    {
        // Capture 20, stop Qdrant
        // Compact → status=error
        // (cannot verify no-delete since Qdrant is down, but no exception thrown)
    }

    [Fact]
    public async Task OnnxRuntimeFailure_MiddlewareCatches_NoConnectionBreak()
    {
        // Inject faulty embedding (optional — may require test hook)
        // Call tool → 500 response, connection stays open for next call
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — all integration tests PASS (requires Docker for testcontainers)
- [ ] E2E round-trip: capture → retrieve → stats → compact → verify summary retrievable
- [ ] Filters work: agent_role_filter, entry_type_filter, project_id isolation
- [ ] Limit enforced (retrieve returns ≤ limit)
- [ ] Compact success: hard-deletes 20 sources, creates 1 summary with correct metadata
- [ ] Compact LLM timeout: 0 sources deleted, all 20 still retrievable
- [ ] Compact LLM 5xx: 0 sources deleted
- [ ] Compact insufficient data: returns skipped (non-blocking), no summary created
- [ ] Qdrant-down fallback: retrieve → empty, capture → false, get_stats → -1, no exceptions
- [ ] No MCP connection breaks on any failure scenario
- [ ] Semantic relevance: similar texts rank higher than dissimilar (verify embedding pipeline works end-to-end)
- [ ] TestFixture properly starts/stops Qdrant testcontainer (no port conflicts)

## Context for @code
- Read: `SPEC.md` §4 (all 4 tool specs), §7 (error handling — fallback policies)
- Read: `milestones/M1-foundation-spec-corrections.md` (S8: non-blocking compact)
- Read: `milestones/M7-mcp-tool-capture-stats.md`, `M8-mcp-tool-retrieve.md`, `M9-mcp-tool-compact.md` (tool logic)
- Read: `milestones/M10-error-handling-resilience-logging.md` (resilience policies under test)
- Skills: `csharp-conventions` (test structure, IAsyncLifetime, IClassFixture)
- Previous artifacts: M4 (ONNX model for real embeddings), M5-M9 (services + tools), M10 (resilience)
- External prerequisite: Docker (for Qdrant testcontainers), ONNX model downloaded (M4)
- Note: Integration tests are slower (Qdrant container startup + ONNX model load). Mark with `[Trait("Category", "Integration")]` for selective runs.
