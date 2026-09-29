#region MODULE_CONTRACT [DOMAIN(Contract): Compact tool output; CONCEPT(MemoryCompactOutput): Compact result with non-blocking status; TECH(SPEC §4.4 + ADR-003/004)]
/**
 * [GREP_SUMMARY]: MemoryCompactOutput, contract, output, DTO, compact, result, status, skipped, completed, error, ADR-003, ADR-004
 * [STRUCTURE]: MemoryCompactOutput sealed record → Status(required) + Reason/Available/Required/SourceCount/SummaryPointId/Error → output from memory_compact tool
 *
 * <summary>
 * [PURPOSE]: Output DTO for the memory_compact tool — result of a compact operation with non-blocking semantics.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Status is one of: "completed" (sources deleted + summary created, ADR-003),
 *   "skipped" (insufficient data, ADR-004), or "error" (LLM/Qdrant failure).
 * [RATIONALE]: ADR-003 (compact = hard delete after LLM success) + ADR-004 (non-blocking skip on insufficient data).
 * The Status field encodes the branch; other nullable fields carry branch-specific data.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation per SPEC §4.4 + S8 (non-blocking skip).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;

// BUG_FIX_CONTEXT: [scar — rung-d preemptive (M9): snake_case names fixed per-property at M9 creation/edit time per mem-027/mem-028. Immunized by SnakeCaseTransportTests];

/// <summary>
/// [PURPOSE]: Output DTO for the memory_compact tool.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Status is one of "completed", "skipped", or "error".
///   - "completed": SourceCount + SummaryPointId set; sources deleted (ADR-003).
///   - "skipped": Reason="insufficient_data"; Available + Required set; NO deletion (ADR-004).
///   - "error": Error set; sources NOT deleted (if LLM failed).
/// [CHANGES]: LAST_CHANGE: M9 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 discipline); M3 initial creation.
/// </remarks>
public sealed record MemoryCompactOutput
{
    /// <summary>[PURPOSE]: Compact result status — "completed", "skipped", or "error".</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>[PURPOSE]: Reason for the result (e.g., "insufficient_data", "llm_timeout").</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>[PURPOSE]: Number of entries available for compacting (set when skipped).</summary>
    [JsonPropertyName("available")]
    public int? Available { get; init; }

    /// <summary>[PURPOSE]: Number of entries required for a compact batch (BatchSize).</summary>
    [JsonPropertyName("required")]
    public int? Required { get; init; }

    /// <summary>[PURPOSE]: Number of source entries deleted (set when completed).</summary>
    [JsonPropertyName("source_count")]
    public int? SourceCount { get; init; }

    /// <summary>[PURPOSE]: Qdrant point ID of the created summary entry (set when completed).</summary>
    [JsonPropertyName("summary_point_id")]
    public string? SummaryPointId { get; init; }

    /// <summary>[PURPOSE]: Error description when Status="error".</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
