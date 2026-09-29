#region MODULE_CONTRACT [DOMAIN(Contract): Compact tool input; CONCEPT(MemoryCompactInput): Batch request; TECH(SPEC §4.4)]
/**
 * [GREP_SUMMARY]: MemoryCompactInput, contract, input, DTO, compact, batch, project_id, batch_size
 * [STRUCTURE]: MemoryCompactInput sealed record → ProjectId required + BatchSize=20 → input to memory_compact tool
 *
 * <summary>
 * [PURPOSE]: Input DTO for the memory_compact tool — request to compact entries into a summary.
 * </summary>
 * <remarks>
 * [INVARIANTS]: ProjectId is required. BatchSize defaults to 20 (per SPEC §4.4).
 * [RATIONALE]: SPEC §4.4 — compact input. No validation logic here (insufficient_data check lives in M9).
 * [CHANGES]: LAST_CHANGE: M3 — initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;

// BUG_FIX_CONTEXT: [scar — rung-d preemptive (M9): snake_case names fixed per-property at M9 creation/edit time per mem-027/mem-028. Immunized by SnakeCaseTransportTests];

/// <summary>
/// [PURPOSE]: Input DTO for the memory_compact tool.
/// </summary>
/// <remarks>
/// [INVARIANTS]: ProjectId is required. BatchSize defaults to 20.
/// [CHANGES]: LAST_CHANGE: M9 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 discipline); M3 initial creation.
/// </remarks>
public sealed record MemoryCompactInput
{
    /// <summary>[PURPOSE]: Project identifier (SHA-256 of workspace root).</summary>
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    /// <summary>[PURPOSE]: Number of entries to include in a single compact batch. Defaults to 20.</summary>
    [JsonPropertyName("batch_size")]
    public int BatchSize { get; init; } = 20;
}
