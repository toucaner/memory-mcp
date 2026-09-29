#region MODULE_CONTRACT [DOMAIN(Enum): Memory entry taxonomy; CONCEPT(EntryType): 6 semantic categories; TECH(ADR-001)]
/**
 * [GREP_SUMMARY]: EntryType, enum, decision, bug_fix, requirement, summary, rejection, insight, ADR-001, snake_case
 * [STRUCTURE]: EntryType enum (6 members) → [JsonConverter(typeof(SnakeCaseEnumConverter))] → snake_case JSON
 *
 * <summary>
 * [PURPOSE]: Semantic taxonomy of memory entry types, matching SPEC.md §3 payload.entry_type.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Exactly 6 members; compact threshold > 20 applies only to BugFix and Insight (M9).
 * [RATIONALE]: ADR-001 extended the original 4 types with Rejection and Insight for richer capture semantics.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation per ADR-001.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Enums;

using System.Text.Json.Serialization;

/// <summary>
/// Memory entry type. Matches SPEC.md §3 payload.entry_type.
/// </summary>
/// <remarks>
/// [INVENTORY]:
///   - Decision — Architectural choice, pattern, stack (why we chose it).
///   - BugFix — Root cause of a bug and the fix approach.
///   - Requirement — Important business rule or constraint.
///   - Summary — Compressed summary of old entries (created only via compact).
///   - Rejection — Rejected architectural option (why we did NOT choose it).
///   - Insight — Process observation, workaround, non-standard API usage.
/// </remarks>
[JsonConverter(typeof(SnakeCaseEnumConverter))]
public enum EntryType
{
    /// <summary>Architectural choice, pattern, stack (why we chose it).</summary>
    Decision,

    /// <summary>Root cause of a bug and the fix approach.</summary>
    BugFix,

    /// <summary>Important business rule or constraint.</summary>
    Requirement,

    /// <summary>Compressed summary of old entries (created only via compact).</summary>
    Summary,

    /// <summary>Rejected architectural option (why we did NOT choose it).</summary>
    Rejection,

    /// <summary>Process observation, workaround, non-standard API usage.</summary>
    Insight
}
