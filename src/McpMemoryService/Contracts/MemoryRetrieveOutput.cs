#region MODULE_CONTRACT [DOMAIN(Contract): Retrieve tool output; CONCEPT(MemoryRetrieveOutput + MemoryRetrieveResult): Search results; TECH(SPEC §4.1)]
/**
 * [GREP_SUMMARY]: MemoryRetrieveOutput, MemoryRetrieveResult, contract, output, DTO, retrieve, results, PointId, AgentRole, EntryType
 * [STRUCTURE]: MemoryRetrieveOutput sealed record → Results(IReadOnlyList<MemoryRetrieveResult>) + MemoryRetrieveResult sibling record → output from memory_retrieve tool
 *
 * <summary>
 * [PURPOSE]: Output DTO for the memory_retrieve tool — container for a list of search results.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Results is always populated (may be empty when no matches).
 * [RATIONALE]: MemoryRetrieveResult is a public SIBLING record in the same file (per M3 spec lines 167-184),
 * NOT nested and NOT a separate file.
 * [CHANGES]: LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 mem-027/mem-028 discipline).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;
using McpMemoryService.Enums;

/// <summary>
/// [PURPOSE]: Output DTO for the memory_retrieve tool — container for a list of search results.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Results is always non-null (may be empty).
/// [CHANGES]: LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 mem-027/mem-028 discipline).
/// </remarks>
public sealed record MemoryRetrieveOutput
{
    // BUG_FIX_CONTEXT: [scar — rung-d preemptive (M8): snake_case names fixed per-property at M8 creation time per mem-027/mem-028. Immunized by SnakeCaseTransportTests.]
    /// <summary>[PURPOSE]: List of semantically similar memory entries, ranked by score.</summary>
    [JsonPropertyName("results")]
    public required IReadOnlyList<MemoryRetrieveResult> Results { get; init; }
}

/// <summary>
/// [PURPOSE]: Individual search result returned by memory_retrieve.
/// </summary>
/// <remarks>
/// [INVARIANTS]: All fields except Tags are required. Tags defaults to empty array.
/// [CHANGES]: LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides.
/// </remarks>
public sealed record MemoryRetrieveResult
{
    // BUG_FIX_CONTEXT: [scar — rung-d preemptive (M8): snake_case names fixed per-property at M8 creation time per mem-027/mem-028. Immunized by SnakeCaseTransportTests.]
    /// <summary>[PURPOSE]: Unique identifier for this entry in Qdrant (string).</summary>
    [JsonPropertyName("point_id")]
    public required string PointId { get; init; }

    /// <summary>[PURPOSE]: Agent role that created this entry.</summary>
    [JsonPropertyName("agent_role")]
    public required AgentRole AgentRole { get; init; }

    /// <summary>[PURPOSE]: Semantic type of this entry.</summary>
    [JsonPropertyName("entry_type")]
    public required EntryType EntryType { get; init; }

    /// <summary>[PURPOSE]: The content of the memory entry.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary>[PURPOSE]: Timestamp of when the entry was created.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>[PURPOSE]: Similarity score from the vector search (higher = more relevant).</summary>
    [JsonPropertyName("score")]
    public required float Score { get; init; }

    /// <summary>[PURPOSE]: Optional tags associated with this entry. Defaults to empty.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}
