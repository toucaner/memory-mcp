#region MODULE_CONTRACT [DOMAIN(Contract): Stats tool input; CONCEPT(MemoryGetStatsInput): Count request; TECH(SPEC §4.3)]
/**
 * [GREP_SUMMARY]: MemoryGetStatsInput, contract, input, DTO, stats, count, project_id, EntryType
 * [STRUCTURE]: MemoryGetStatsInput sealed record → ProjectId required + EntryType? → input to memory_get_stats tool
 *
 * <summary>
 * [PURPOSE]: Input DTO for the memory_get_stats tool — request to count entries (optionally filtered by type).
 * </summary>
 * <remarks>
 * [INVARIANTS]: ProjectId is required. EntryTypeFilter accepts all 6 types including Summary (get_stats=6, per consistency invariant).
 * [RATIONALE]: SPEC §4.3. EntryType accepts all 6 values (unlike capture which excludes Summary). Transport property
 *   keys are snake_case (project_id/entry_type) enforced via [JsonPropertyName] (M7 rung-d fix — SDK 1.4.0 has no
 *   serializer knob on McpServerOptions; see Program.cs M7 scar).
 * [CHANGES]: LAST_CHANGE: M7 — added [JsonPropertyName] snake_case transport overrides (rung-d deterministic fallback);
 *   M3 initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;
using McpMemoryService.Enums;

/// <summary>
/// [PURPOSE]: Input DTO for the memory_get_stats tool.
/// </summary>
/// <remarks>
/// [INVARIANTS]: ProjectId is required. EntryType accepts all 6 types including Summary.
/// </remarks>
public sealed record MemoryGetStatsInput
{
    // BUG_FIX_CONTEXT: [HYPOTHESIS: M7 snake_case transport deliverable was OMITTED — @qa AC-4/AC-8 BLOCK (mem-027).
    //   SDK 1.4.0 exposes NO serializer knob on McpServerOptions (compile-probe CS1061), so the deterministic fallback
    //   is [JsonPropertyName] overrides on this DTO per SPEC §4.3 snake_case contract. See Program.cs M7 scar for the
    //   full ladder walk. ts=2026-07-01T16:00:00Z]
    // BUG_FIX_CONTEXT: [scar — rung-d: snake_case names fixed per-property. Immunized by SnakeCaseTransportTests];

    /// <summary>[PURPOSE]: Project identifier (SHA-256 of workspace root).</summary>
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    /// <summary>[PURPOSE]: Optional filter by entry type (all 6 types including Summary for stats).</summary>
    [JsonPropertyName("entry_type")]
    public EntryType? EntryType { get; init; }
}
