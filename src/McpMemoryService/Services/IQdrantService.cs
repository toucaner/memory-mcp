#region MODULE_CONTRACT [DOMAIN(Services): Qdrant persistence contract; CONCEPT(IQdrantService): Vector DB CRUD interface; TECH(SPEC §5.2, M5)]
/**
 * [GREP_SUMMARY]: IQdrantService, interface, Qdrant, collection, upsert, search, count, delete, compact, CancellationToken
 * [STRUCTURE]: IQdrantService → EnsureCollectionExistsAsync + UpsertAsync + SearchAsync + CountAsync + DeleteAsync + GetAsync + GetBatchForCompactAsync
 *
 * <summary>
 * [PURPOSE]: Contract for Qdrant vector database operations supporting semantic memory storage and retrieval.
 * </summary>
 * <remarks>
 * [INVARIANTS]: All methods accept CancellationToken. Search requires projectId filter (non-optional).
 *   Upsert uses GUID v4 point IDs. Collection is 384-dim, DotProduct per ADR-011.
 * [RATIONALE]: Interface allows M10 resilience wrapper and test mocking. SPEC §5.2 contract.
 * [CHANGES]: LAST_CHANGE: M5 — initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Services;

using McpMemoryService.Enums;
using McpMemoryService.Models;

/// <summary>
/// [PURPOSE]: Wrapper over Qdrant vector DB for memory storage and retrieval.
/// </summary>
public interface IQdrantService
{
    /// <summary>
    /// [PURPOSE]: Ensures the Qdrant collection exists with correct config and keyword indexes. Idempotent.
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: Creates 384-dim DotProduct collection if not exists. Creates keyword indexes on
    ///   project_id, entry_type, agent_role, tags. Swallows "already exists" errors on index creation.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RpcException">Thrown when Qdrant is unreachable (startup fatal per ADR-005).</exception>
    Task EnsureCollectionExistsAsync(CancellationToken ct = default);

    /// <summary>
    /// [PURPOSE]: Upserts a single point with a GUID v4 ID, embedding vector, and payload.
    /// </summary>
    /// <param name="pointId">Unique identifier for the point (GUID v4).</param>
    /// <param name="vector">384-dim embedding vector (L2-normalized from M4).</param>
    /// <param name="payload">Semantic memory payload to store.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertAsync(Guid pointId, float[] vector, MemoryPayload payload, CancellationToken ct = default);

    /// <summary>
    /// [PURPOSE]: Performs semantic vector search with filters.
    /// </summary>
    /// <param name="queryVector">384-dim query embedding vector.</param>
    /// <param name="projectId">Required project ID filter.</param>
    /// <param name="agentRoleFilter">Optional agent role filter.</param>
    /// <param name="entryTypeFilter">Optional entry type filter.</param>
    /// <param name="limit">Maximum number of results (default 5).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked list of memory entries matching the search criteria.</returns>
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        float[] queryVector,
        string projectId,
        AgentRole? agentRoleFilter = null,
        EntryType? entryTypeFilter = null,
        int limit = 5,
        CancellationToken ct = default);

    /// <summary>
    /// [PURPOSE]: Counts points matching the given filters.
    /// </summary>
    /// <param name="projectId">Required project ID filter.</param>
    /// <param name="entryTypeFilter">Optional entry type filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exact count of matching points.</returns>
    Task<int> CountAsync(string projectId, EntryType? entryTypeFilter = null, CancellationToken ct = default);

    /// <summary>
    /// [PURPOSE]: Deletes a batch of points by IDs (used by compact hard-delete per ADR-003).
    /// </summary>
    /// <param name="pointIds">Enumerable of point GUIDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(IEnumerable<Guid> pointIds, CancellationToken ct = default);

    /// <summary>
    /// [PURPOSE]: Retrieves points by IDs (used by compact to fetch batch content).
    /// </summary>
    /// <param name="pointIds">Enumerable of point GUIDs to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of memory entries for the requested IDs.</returns>
    Task<IReadOnlyList<MemoryEntry>> GetAsync(IEnumerable<Guid> pointIds, CancellationToken ct = default);

    /// <summary>
    /// [PURPOSE]: Retrieves a batch of points (excluding summary) ordered by timestamp ASC, for compact.
    /// </summary>
    /// <param name="projectId">Project ID to filter by.</param>
    /// <param name="batchSize">Maximum number of points to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of non-summary memory entries ordered by timestamp ascending.</returns>
    Task<IReadOnlyList<MemoryEntry>> GetBatchForCompactAsync(
        string projectId,
        int batchSize,
        CancellationToken ct = default);
}
