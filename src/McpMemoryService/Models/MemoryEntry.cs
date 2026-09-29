#region MODULE_CONTRACT [DOMAIN(Model): Search result record; CONCEPT(MemoryEntry): Payload + Vector + Score; TECH(Internal)]
/**
 * [GREP_SUMMARY]: MemoryEntry, record, PointId, Payload, Vector, Score, search result, internal
 * [STRUCTURE]: MemoryEntry sealed record → PointId(Guid) + Payload(MemoryPayload) + Vector(float[]) + Score? → internal search result
 *
 * <summary>
 * [PURPOSE]: Internal record combining payload with its vector and optional similarity score for search results.
 * </summary>
 * <remarks>
 * [INVARIANTS]: PointId, Payload, and Vector are required — a MemoryEntry without these cannot exist.
 * [RATIONALE]: This is the internal representation of a memory entry after vector search. It carries the payload (domain data),
 * the vector used for similarity matching, and the score from the search operation.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Models;

/// <summary>
/// [PURPOSE]: Internal record combining payload with its vector and optional similarity score.
/// </summary>
/// <remarks>
/// [INVARIANTS]: PointId, Payload, and Vector are required. Score is optional (null when no score is available).
/// </remarks>
public sealed record MemoryEntry
{
    /// <summary>[PURPOSE]: Unique identifier for this entry in Qdrant (GUID).</summary>
    public required Guid PointId { get; init; }

    /// <summary>[PURPOSE]: The semantic memory payload associated with this entry.</summary>
    public required MemoryPayload Payload { get; init; }

    /// <summary>[PURPOSE]: The embedding vector used for similarity matching (384 dimensions).</summary>
    public required float[] Vector { get; init; }

    /// <summary>[PURPOSE]: Similarity score from the search operation (null when not computed).</summary>
    public float? Score { get; init; }
}
