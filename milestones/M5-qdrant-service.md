# M5 — QdrantService (collection + indexes + CRUD)

## Dependencies
- M2 (solution skeleton, QdrantOptions)
- M3 (models: MemoryPayload, MemoryEntry, mapping extensions)

## Goal
Implement `QdrantService` (Singleton) wrapping `QdrantClient`: ensure collection exists (384-dim, DotProduct, keyword indexes on project_id/entry_type/agent_role/tags), upsert, search, count, delete. Called at app startup for collection initialization.

## Deliverables
- `src/McpMemoryService/Services/IQdrantService.cs`
- `src/McpMemoryService/Services/QdrantService.cs`
- `src/McpMemoryService/Services/QdrantCollectionInitializer.cs` (hosted service for startup init)
- Registration in `Program.cs`:
  - `services.AddSingleton<IQdrantService, QdrantService>();`
  - `services.AddHostedService<QdrantCollectionInitializer>();`
- Update `src/McpMemoryService/Mapping/PayloadMappingExtensions.cs` (full Qdrant.Client integration)
- `tests/McpMemoryService.Tests/Services/QdrantServiceTests.cs`

## Contracts

### IQdrantService.cs

```csharp
namespace McpMemoryService.Services;

/// <summary>
/// Wrapper over Qdrant vector DB for memory storage.
/// </summary>
public interface IQdrantService
{
    /// <summary>Ensure collection exists with correct config + indexes. Idempotent.</summary>
    Task EnsureCollectionExistsAsync(CancellationToken ct = default);

    /// <summary>Upsert a single point (GUID v4) with payload + vector.</summary>
    Task UpsertAsync(Guid pointId, float[] vector, MemoryPayload payload, CancellationToken ct = default);

    /// <summary>Semantic search with filters.</summary>
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        float[] queryVector,
        string projectId,
        AgentRole? agentRoleFilter = null,
        EntryType? entryTypeFilter = null,
        int limit = 5,
        CancellationToken ct = default);

    /// <summary>Count points matching filters.</summary>
    Task<int> CountAsync(string projectId, EntryType? entryTypeFilter = null, CancellationToken ct = default);

    /// <summary>Delete a batch of points by IDs (used by compact hard-delete).</summary>
    Task DeleteAsync(IEnumerable<Guid> pointIds, CancellationToken ct = default);

    /// <summary>Retrieve points by IDs (used by compact to fetch batch content).</summary>
    Task<IReadOnlyList<MemoryEntry>> GetAsync(IEnumerable<Guid> pointIds, CancellationToken ct = default);

    /// <summary>Retrieve a batch of points (excluding summary) ordered by timestamp ASC, for compact.</summary>
    Task<IReadOnlyList<MemoryEntry>> GetBatchForCompactAsync(
        string projectId,
        int batchSize,
        CancellationToken ct = default);
}
```

### QdrantService.cs — implementation notes

```csharp
namespace McpMemoryService.Services;

public sealed class QdrantService : IQdrantService
{
    # region Fields
    private readonly QdrantClient _client;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantService> _logger;
    # endregion

    # region Constructor
    // Create QdrantClient from QdrantOptions.Url.
    // DO NOT call EnsureCollectionExistsAsync in ctor — use QdrantCollectionInitializer.
    # endregion

    # region EnsureCollectionExistsAsync
    // 1. Check if collection exists via GetCollectionInfoAsync.
    // 2. If not — CreateCollectionAsync:
    //    - Vector size: 384
    //    - Distance: DotProduct
    //    - ON_DISK payload (optional, for memory efficiency)
    // 3. Create payload indexes (idempotent — skip if exists):
    //    - "project_id"   → keyword
    //    - "entry_type"   → keyword
    //    - "agent_role"   → keyword
    //    - "tags"         → keyword (array)
    // 4. Log [IMP:1] collection ready
    # endregion

    # region UpsertAsync
    // 1. Build PointStruct:
    //    - Id = pointId (GUID)
    //    - Vectors = vector
    //    - Payload = payload.ToQdrantPayload()
    // 2. UpsertAsync(collection, points)
    // 3. Log [IMP:2] upserted pointId
    # endregion

    # region SearchAsync
    // 1. Build Filter:
    //    - Must: project_id == projectId
    //    - Must (if agentRoleFilter): agent_role == agentRoleFilter
    //    - Must (if entryTypeFilter): entry_type == entryTypeFilter
    // 2. SearchAsync(collection, queryVector, limit, filter)
    // 3. Map results → MemoryEntry (with Score)
    // 4. Log [IMP:3] search returned N results
    # endregion

    # region CountAsync
    // 1. Build Filter (same logic as SearchAsync)
    // 2. CountAsync(collection, filter)
    // 3. Return count
    # endregion

    # region DeleteAsync
    // 1. DeleteAsync(collection, pointIds)
    // 2. Log [IMP:4] deleted N points
    # endregion

    # region GetAsync
    // Retrieve points by IDs, map to MemoryEntry
    # endregion

    # region GetBatchForCompactAsync
    // 1. ScrollAsync (or SearchAsync with no vector, filtered):
    //    - Filter: project_id == projectId AND entry_type != summary
    //    - Order by timestamp ASC
    //    - Limit: batchSize
    // 2. Map to MemoryEntry list
    // 3. Log [IMP:5] fetched N points for compact
    # endregion
}
```

### QdrantCollectionInitializer.cs

```csharp
namespace McpMemoryService.Services;

public sealed class QdrantCollectionInitializer : IHostedService
{
    # region Fields
    private readonly IQdrantService _qdrantService;
    private readonly ILogger<QdrantCollectionInitializer> _logger;
    # endregion

    # region StartAsync
    // Call _qdrantService.EnsureCollectionExistsAsync(ct).
    // On failure: log CRITICAL + rethrow (app startup should fail if Qdrant unreachable at init).
    // NOTE: per SPEC §7, runtime Qdrant failures during tools = silent fallback.
    //       But startup init failure = fatal (Exit 1) — different policy.
    # endregion

    # region StopAsync
    // No-op
    # endregion
}
```

### Mapping/PayloadMappingExtensions.cs — full Qdrant integration

Update the stubs from M3 with real Qdrant.Client types:
```csharp
public static class PayloadMappingExtensions
{
    public static MemoryPayload ToPayload(this IDictionary<string, Value> qdrantPayload);
    public static IDictionary<string, Value> ToQdrantPayload(this MemoryPayload payload);
    public static MemoryEntry ToEntry(this RetrievedPoint point);
    public static MemoryEntry ToEntry(this ScoredPoint point);
}
```
- `ToQdrantPayload`: project_id, session_id, agent_role (snake_case string), entry_type (snake_case string), timestamp (ISO 8601 string), content, tags (array of strings), metadata (nested map).
- `ToPayload`: reverse mapping. Handle enum parsing from snake_case strings.

## Algorithm / Logic

### Step 1: Implement IQdrantService and QdrantService
1. Verify `Qdrant.Client` is in csproj (from M2).
2. Implement constructor: create `QdrantClient` from `QdrantOptions.Url`.
3. Implement `EnsureCollectionExistsAsync` with idempotent collection + index creation.
4. Implement `UpsertAsync`, `SearchAsync`, `CountAsync`, `DeleteAsync`, `GetAsync`, `GetBatchForCompactAsync`.
5. Logging with `[IMP:1]`..`[IMP:5]` markers.

### Step 2: Implement QdrantCollectionInitializer
1. `IHostedService` that calls `EnsureCollectionExistsAsync` at startup.
2. On failure: log + rethrow (fatal startup).

### Step 3: Update Mapping extensions
1. Replace M3 stubs with real Qdrant.Client type integration.
2. Handle snake_case enum conversion.
3. Handle `Value` type conversions (string, integer, list).

### Step 4: DI registration
Add to Program.cs:
```csharp
services.AddSingleton<IQdrantService, QdrantService>();
services.AddHostedService<QdrantCollectionInitializer>();
```

### Step 5: Unit tests
Tests use a real Qdrant instance (integration-style) OR mocked `QdrantClient`. Mark integration tests with `[Trait("Category", "Integration")]` — they require Qdrant at `localhost:6333`. Unit tests with mock cover filter-building and mapping logic.

## Tests (unit, inline)

### tests/.../Services/QdrantServiceTests.cs
```csharp
public class QdrantServiceTests
{
    // === UNIT TESTS (mocked QdrantClient) ===

    [Fact]
    public void ToQdrantPayload_MapsAllFields()
    {
        // Arrange: MemoryPayload with all fields
        // Act: ToQdrantPayload()
        // Assert: all keys present, snake_case, correct types
    }

    [Fact]
    public void ToPayload_MapsSnakeCaseEnums()
    {
        // Arrange: qdrant payload with "entry_type":"bug_fix", "agent_role":"debug"
        // Act: ToPayload()
        // Assert: EntryType==BugFix, AgentRole==Debug
    }

    [Fact]
    public void ToPayload_RoundTrip_PreservesAllFields()
    {
        // Round-trip: payload → qdrant → payload
    }

    // === INTEGRATION TESTS (require Qdrant at localhost:6333) ===

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnsureCollectionExistsAsync_CreatesCollectionWithIndexes()
    {
        // Arrange: fresh Qdrant
        // Act: EnsureCollectionExistsAsync
        // Assert: collection exists, 384 dim, DotProduct, indexes present
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpsertAsync_SearchAsync_RoundTrip()
    {
        // Arrange: EnsureCollectionExists
        // Act: Upsert point → Search by vector
        // Assert: point found, payload preserved
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SearchAsync_FiltersByProjectId()
    {
        // Upsert points with different project_id
        // Search with projectId filter → only matching points returned
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SearchAsync_FiltersByEntryType()
    {
        // Upsert points with different entry_type
        // Search with entryTypeFilter → only matching points returned
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        // Upsert N points → Count == N
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteAsync_RemovesPoints()
    {
        // Upsert → Delete → Get returns empty
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetBatchForCompactAsync_ExcludesSummary()
    {
        // Upsert mix of types including summary
        // GetBatchForCompact → no summary in results
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — unit tests PASS (no Qdrant required)
- [ ] Integration tests PASS (require Qdrant at localhost:6333)
- [ ] `EnsureCollectionExistsAsync` creates collection with 384 dim, DotProduct
- [ ] Indexes created for project_id, entry_type, agent_role, tags (keyword)
- [ ] Upsert → Search round-trip preserves all payload fields
- [ ] Search filters by project_id (required), agent_role_filter, entry_type_filter (optional)
- [ ] Count returns correct number
- [ ] Delete removes points
- [ ] GetBatchForCompactAsync excludes summary type, orders by timestamp ASC
- [ ] Logs contain `[IMP:1]`..`[IMP:5]` markers
- [ ] Startup init failure is fatal (rethrow); runtime failures handled in M10

## Context for @code
- Read: `SPEC.md` §3 (Qdrant schema, indexes), §5.2 (QdrantService spec)
- Read: `milestones/M1-foundation-spec-corrections.md` (S2: tags, S3: index list)
- Read: `milestones/M3-data-models-payload.md` (MemoryPayload, MemoryEntry, mapping stubs)
- Skills: `csharp-conventions` (#region, XML docs, Singleton DI, IHostedService)
- Previous artifacts: M2 (QdrantOptions), M3 (models, mapping stubs)
- External prerequisite: Qdrant instance at localhost:6333 for integration tests (use `docker run -p 6333:6333 qdrant/qdrant`)
- Web search: if needed, verify `Qdrant.Client` API for .NET 10 (CreateCollectionAsync, CreatePayloadIndexAsync, ScrollAsync)
