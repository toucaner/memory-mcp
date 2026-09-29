# M10 — Error handling + resilience + LDD logging

## Dependencies
- M7 (MemoryCaptureTool, MemoryGetStatsTool)
- M8 (MemoryRetrieveTool)
- M9 (MemoryCompactTool)

## Goal
Harden the service with global error-handling policies per SPEC §7: Qdrant-down → silent fallback (empty results, silent capture fail); ONNX-fail at startup → Exit 1; LLM timeout/5xx → non-blocking compact status. Add LDD (Logging-Driven Diagnostics) telemetry with `[IMP:N]` markers across all tools. Verify no MCP connection breaks on downstream failures.

## Deliverables
- `src/McpMemoryService/Middleware/GlobalExceptionMiddleware.cs` (ASP.NET Core middleware)
- `src/McpMemoryService/Resilience/QdrantResiliencePolicy.cs` (wrapper for Qdrant calls)
- `src/McpMemoryService/Resilience/EmbeddingResiliencePolicy.cs` (ONNX-fail handling)
- `src/McpMemoryService/Logging/LddMarkers.cs` (constants for `[IMP:N]` markers per tool)
- Update `Program.cs` (register middleware, configure logging)
- Update `MemoryCaptureTool`, `MemoryRetrieveTool`, `MemoryCompactTool`, `MemoryGetStatsTool` (apply resilience policies, ensure LDD markers)
- `tests/McpMemoryService.Tests/Resilience/ResilienceTests.cs`

## Contracts

### SPEC §7 error-handling matrix

| Component | Failure | Policy | Source |
|---|---|---|---|
| Qdrant (retrieve) | Down/timeout | Return empty results, log warning, NO exception | SPEC §7, M8 |
| Qdrant (capture) | Down/timeout | Silent fail: return `success=false`, log error, NO exception | SPEC §7, M7 |
| Qdrant (get_stats) | Down/timeout | Return `count=-1` or error indicator, log warning | SPEC §7 |
| Qdrant (compact) | Down/timeout | Return `status=error`, source NOT deleted | M9 |
| ONNX (startup) | Model load fail | **Exit 1** with clear log (fatal) | SPEC §7 |
| ONNX (runtime) | Inference fail | Let propagate → GlobalExceptionMiddleware catches → MCP error response (not connection break) | SPEC §7 |
| LLM (compact) | Timeout (60s) | Return `status=error, reason=llm_timeout`, source NOT deleted | SPEC §7, M9 |
| LLM (compact) | 5xx | Return `status=error, reason=llm_5xx`, source NOT deleted | SPEC §7, M9 |

### GlobalExceptionMiddleware.cs

```csharp
namespace McpMemoryService.Middleware;

public sealed class GlobalExceptionMiddleware
{
    # region Fields
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    # endregion

    # region InvokeAsync
    // 1. try { await _next(context); }
    // 2. catch (Exception ex):
    //    - Log [IMP:CRITICAL] unhandled exception (type, message, stack)
    //    - Return JSON response:
    //      { "error": { "code": "internal_error", "message": "..." } }
    //      StatusCode = 500
    //    - DO NOT rethrow (prevents MCP connection break)
    // 3. Exception filters (optional):
    //    - InvalidOperationException (ONNX) → 503 Service Unavailable
    //    - ArgumentException (validation) → 400 Bad Request
    # endregion
}
```

### QdrantResiliencePolicy.cs

```csharp
namespace McpMemoryService.Resilience;

public sealed class QdrantResiliencePolicy
{
    # region ExecuteWithFallbackAsync
    // Generic wrapper for Qdrant calls:
    // - On QdrantException/timeout: log [IMP:WARN], return fallback value (empty list, -1, false)
    // - Used by tools to ensure silent degradation
    public async Task<T> ExecuteWithFallbackAsync<T>(
        Func<Task<T>> action,
        T fallbackValue,
        string operationName,
        ILogger logger);
    # endregion
}
```

### EmbeddingResiliencePolicy.cs

```csharp
namespace McpMemoryService.Resilience;

public sealed class EmbeddingResiliencePolicy
{
    # region ExecuteAsync
    // - On success: return vector
    // - On failure (runtime): log [IMP:ERROR], rethrow (ONNX failure is considered fatal
    //   per SPEC §7 — startup failure = Exit 1, runtime failure = propagate to middleware)
    # endregion
}
```

### LddMarkers.cs — constants

```csharp
namespace McpMemoryService.Logging;

public static class LddMarkers
{
    # region Capture
    public const string CaptureEntry = "[IMP:1]";
    public const string CaptureEmbeddingGenerated = "[IMP:2]";
    public const string CaptureUpserted = "[IMP:3]";
    public const string CaptureFallback = "[IMP:4]";
    # endregion

    # region Retrieve
    public const string RetrieveEntry = "[IMP:1]";
    public const string RetrieveEmbedding = "[IMP:2]";
    public const string RetrieveSearchComplete = "[IMP:3]";
    public const string RetrieveFallbackEmpty = "[IMP:4]";
    # endregion

    # region Compact
    public const string CompactBatchFetched = "[IMP:1]";
    public const string CompactLlmCall = "[IMP:2]";
    public const string CompactSummaryCaptured = "[IMP:3]";
    public const string CompactSourceDeleted = "[IMP:4]";
    public const string CompactLlmTimeout = "[IMP:5]";
    public const string CompactLlm5xx = "[IMP:6]";
    public const string CompactSkipped = "[IMP:7]";
    # endregion

    # region Embedding
    public const string EmbeddingEntry = "[IMP:1]";
    public const string EmbeddingTokenized = "[IMP:2]";
    public const string EmbeddingInference = "[IMP:3]";
    public const string EmbeddingMeanPool = "[IMP:4]";
    public const string EmbeddingL2Norm = "[IMP:5]";
    # endregion

    # region Qdrant
    public const string QdrantCollectionReady = "[IMP:1]";
    public const string QdrantUpserted = "[IMP:2]";
    public const string QdrantSearched = "[IMP:3]";
    public const string QdrantDeleted = "[IMP:4]";
    public const string QdrantBatchFetched = "[IMP:5]";
    # endregion

    # region Critical
    public const string UnhandledException = "[IMP:CRITICAL]";
    public const string OnnxFatal = "[IMP:FATAL]";
    # endregion
}
```

## Algorithm / Logic

### Step 1: Implement GlobalExceptionMiddleware
1. Create ASP.NET Core middleware that catches all unhandled exceptions.
2. Log `[IMP:CRITICAL]` with exception type, message, stack trace.
3. Return JSON error response (500 or 503) — do NOT rethrow (prevents MCP connection break).
4. Register in Program.cs pipeline: `app.UseMiddleware<GlobalExceptionMiddleware>();`

### Step 2: Implement QdrantResiliencePolicy
1. Generic wrapper `ExecuteWithFallbackAsync<T>` that catches Qdrant exceptions.
2. Logs `[IMP:WARN]` and returns fallback value.
3. Apply in tools:
   - `MemoryRetrieveTool`: wrap SearchAsync, fallback = empty list
   - `MemoryCaptureTool`: wrap UpsertAsync, fallback = `Success=false`
   - `MemoryGetStatsTool`: wrap CountAsync, fallback = `Count=-1`
   - `MemoryCompactTool`: wrap GetBatchForCompactAsync (fallback = empty → triggers insufficient_data path)

### Step 3: Implement EmbeddingResiliencePolicy
1. Wrapper for embedding calls.
2. On failure: log `[IMP:FATAL]` and rethrow (ONNX failure is fatal per SPEC §7).
3. Apply in tools where embedding is called (capture, retrieve, compact).
4. Startup ONNX failure (model load) → handled in OnnxEmbeddingService constructor (M4) → propagates → app exits with Exit 1.

### Step 4: Create LddMarkers constants
1. Define all `[IMP:N]` markers as constants (per contract above).
2. Update all tools and services to use these constants instead of inline strings.

### Step 5: Update Program.cs
1. Register `GlobalExceptionMiddleware` in pipeline.
2. Configure logging levels per SPEC §7 (Information for tool calls, Warning for timeouts, Error for Qdrant failures).
3. Ensure `appsettings.json` logging config matches SPEC §8.

### Step 6: Verify and update tools
1. Ensure each tool uses the resilience policies.
2. Ensure each tool emits LDD markers per LddMarkers constants.
3. Verify no tool throws exceptions that break MCP connection (all caught by middleware or resilience policies).

### Step 7: Resilience tests
Tests verify fallback behavior under various failure scenarios.

## Tests (unit, inline)

### tests/.../Resilience/ResilienceTests.cs
```csharp
public class ResilienceTests
{
    [Fact]
    public async Task Retrieve_QdrantDown_ReturnsEmptyResults_NoException()
    {
        // Arrange: mock Qdrant throws, tool wrapped with QdrantResiliencePolicy
        // Act: retrieve
        // Assert: Results empty, no exception thrown
    }

    [Fact]
    public async Task Capture_QdrantDown_ReturnsFailure_NoException()
    {
        // Arrange: mock Qdrant throws
        // Act: capture
        // Assert: Success==false, no exception
    }

    [Fact]
    public async Task GetStats_QdrantDown_ReturnsNegativeOne_NoException()
    {
        // Arrange: mock Qdrant throws
        // Act: get_stats
        // Assert: Count==-1, no exception
    }

    [Fact]
    public async Task Compact_QdrantDownOnBatchFetch_ReturnsError_NoDelete()
    {
        // Arrange: mock Qdrant GetBatchForCompactAsync throws
        // Act: compact
        // Assert: Status=="error", DeleteAsync NOT called
    }

    [Fact]
    public async Task Retrieve_OnnxRuntimeFail_MiddlewareCatches_Returns500()
    {
        // Arrange: mock Embedding throws InvalidOperationException
        // Act: retrieve via HTTP
        // Assert: 500 response, no connection break, [IMP:CRITICAL] logged
    }

    [Fact]
    public async Task GlobalMiddleware_UnhandledException_ReturnsJsonError()
    {
        // Arrange: endpoint throws
        // Act: request
        // Assert: 500, JSON body with error.code=="internal_error"
    }

    [Fact]
    public async Task AllTools_EmitLddMarkers()
    {
        // Arrange: capture log output
        // Act: call each tool
        // Assert: logs contain expected [IMP:N] markers per tool
    }

    [Fact]
    public async Task Compact_LlmTimeout_PreservesSourceEntries()
    {
        // Covered in M9, but re-verify with resilience policy in place
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — all resilience tests PASS
- [ ] GlobalExceptionMiddleware catches unhandled exceptions, returns JSON 500 (no MCP connection break)
- [ ] QdrantResiliencePolicy applied to all Qdrant calls in tools
- [ ] Qdrant-down: retrieve → empty results; capture → `success=false`; get_stats → `count=-1`
- [ ] ONNX runtime failure → middleware catches → 503 (no connection break)
- [ ] ONNX startup failure → app exits with Exit 1 + clear log (verified in M4)
- [ ] LDD markers (`[IMP:N]`) used consistently across all tools/services
- [ ] LddMarkers constants defined and used (no inline marker strings)
- [ ] Logging levels: Information (tool calls), Warning (timeouts), Error (Qdrant failures) per SPEC §7
- [ ] No MCP connection breaks on any downstream failure (Qdrant/ONNX/LLM)

## Context for @code
- Read: `SPEC.md` §7 (error handling and logging — full section)
- Read: `milestones/M4-onnx-embedding-service.md` (ONNX startup failure = Exit 1)
- Read: `milestones/M7-mcp-tool-capture-stats.md` (silent capture fail)
- Read: `milestones/M8-mcp-tool-retrieve.md` (silent empty results)
- Read: `milestones/M9-mcp-tool-compact.md` (LLM timeout/5xx handling)
- Skills: `csharp-conventions` (#region, XML docs, middleware, logging), `mode-debug` (LDD marker format reference)
- Previous artifacts: M4-M9 (all tools and services)
