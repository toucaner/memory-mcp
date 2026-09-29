#region MODULE_CONTRACT [DOMAIN(Contract): Stats tool output; CONCEPT(MemoryGetStatsOutput): Count result; TECH(SPEC §4.3)]
/**
 * [GREP_SUMMARY]: MemoryGetStatsOutput, contract, output, DTO, stats, count, result, ADR-005
 * [STRUCTURE]: MemoryGetStatsOutput sealed record → Count → output from memory_get_stats tool
 *
 * <summary>
 * [PURPOSE]: Output DTO for the memory_get_stats tool — count of entries matching the filter.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Count is always set. On Qdrant failure (ADR-005), Count=-1.
 * [RATIONALE]: ADR-005 — Qdrant down → Count=-1 (no exception). Transport property key is snake_case (count)
 *   enforced via [JsonPropertyName] (M7 rung-d fix — SDK 1.4.0 has no serializer knob on McpServerOptions;
 *   see Program.cs M7 scar).
 * [CHANGES]: LAST_CHANGE: M7 — added [JsonPropertyName] snake_case transport overrides (rung-d deterministic fallback);
 *   M3 initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// [PURPOSE]: Output DTO for the memory_get_stats tool.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Count is always set. -1 indicates Qdrant unavailable (ADR-005).
/// </remarks>
public sealed record MemoryGetStatsOutput
{
    // BUG_FIX_CONTEXT: [HYPOTHESIS: M7 snake_case transport deliverable was OMITTED — @qa AC-4/AC-8 BLOCK (mem-027).
    //   SDK 1.4.0 exposes NO serializer knob on McpServerOptions (compile-probe CS1061), so the deterministic fallback
    //   is [JsonPropertyName] overrides on this DTO per SPEC §4.3 snake_case contract. See Program.cs M7 scar for the
    //   full ladder walk. ts=2026-07-01T16:00:00Z]
    // BUG_FIX_CONTEXT: [scar — rung-d: snake_case names fixed per-property. Immunized by SnakeCaseTransportTests];

    /// <summary>[PURPOSE]: Number of entries matching the filter. -1 when Qdrant is unavailable (ADR-005).</summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }
}
