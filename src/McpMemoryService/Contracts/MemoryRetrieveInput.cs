#region MODULE_CONTRACT [DOMAIN(Contract): Retrieve tool input; CONCEPT(MemoryRetrieveInput): Search request with filters; TECH(SPEC §4.1 + S4, S5)]
/**
 * [GREP_SUMMARY]: MemoryRetrieveInput, contract, input, DTO, retrieve, search, query, project_id, AgentRoleFilter, EntryTypeFilter, Limit
 * [STRUCTURE]: MemoryRetrieveInput sealed record → Query(ProjectId) required + AgentRoleFilter?(EntryTypeFilter? + Limit=5) → input to memory_retrieve tool
 *
 * <summary>
 * [PURPOSE]: Input DTO for the memory_retrieve tool — semantic search request with optional role/type filters.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Query and ProjectId are required. Limit defaults to 5 (max=10 enforced in the M8 tool).
 * [RATIONALE]: M1/S4 + S5 corrections — added AgentRoleFilter and EntryTypeFilter to the retrieve contract.
 * Limit validation (≤10) lives in the M8 tool, not here.
 * [CHANGES]: LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 mem-027/mem-028 discipline); M3 initial creation with EntryTypeFilter added (S5 correction).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;
using McpMemoryService.Enums;

/// <summary>
/// [PURPOSE]: Input DTO for the memory_retrieve tool — semantic search request with optional role/type filters.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Query and ProjectId are required. Limit defaults to 5 (max=10 enforced in the M8 tool).
/// [CHANGES]: LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 mem-027/mem-028 discipline).
/// </remarks>
public sealed record MemoryRetrieveInput
{
    // BUG_FIX_CONTEXT: [scar — rung-d preemptive (M8): snake_case names fixed per-property at M8 creation time per mem-027/mem-028. Immunized by SnakeCaseTransportTests.]
    /// <summary>[PURPOSE]: The search query — text to find semantically similar entries for.</summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>[PURPOSE]: Project identifier (SHA-256 of workspace root).</summary>
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    /// <summary>[PURPOSE]: Optional filter by agent role (Orchestrator, Architect, Code, Debug, Qa).</summary>
    [JsonPropertyName("agent_role_filter")]
    public AgentRole? AgentRoleFilter { get; init; }

    /// <summary>[PURPOSE]: Optional filter by entry type (Decision, BugFix, Requirement, Summary, Rejection, Insight).</summary>
    [JsonPropertyName("entry_type_filter")]
    public EntryType? EntryTypeFilter { get; init; }

    /// <summary>[PURPOSE]: Maximum number of results to return. Defaults to 5 (max=10 enforced in M8 tool).</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 5;
}
