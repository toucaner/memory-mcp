#region MODULE_CONTRACT [DOMAIN(Enum): Agent identity taxonomy; CONCEPT(AgentRole): 5 GRACE roles; TECH(ADR-002)]
/**
 * [GREP_SUMMARY]: AgentRole, enum, orchestrator, architect, code, debug, qa, ADR-002, snake_case
 * [STRUCTURE]: AgentRole enum (5 members) → [JsonConverter(typeof(SnakeCaseEnumConverter))] → snake_case JSON
 *
 * <summary>
 * [PURPOSE]: Identity taxonomy of agents in the GRACE pipeline, matching SPEC.md §3 payload.agent_role.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Exactly 5 members matching the GRACE protocol agent names.
 * [RATIONALE]: ADR-002 — Orchestrator captures entries "on behalf of" returning agents. Payload stores 5 roles.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation per ADR-002.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Enums;

using System.Text.Json.Serialization;

/// <summary>
/// Source agent role. Matches SPEC.md §3 payload.agent_role.
/// </summary>
/// <remarks>
/// [INVENTORY]:
///   - Orchestrator — Dispatches tasks across the GRACE pipeline.
///   - Architect — Produces designs, plans, and ADRs.
///   - Code — Writes and fixes code.
///   - Debug — Diagnoses and fixes bugs.
///   - Qa — Verifies and reports quality.
/// </remarks>
[JsonConverter(typeof(SnakeCaseEnumConverter))]
public enum AgentRole
{
    /// <summary>Orchestrator — dispatches tasks across the GRACE pipeline.</summary>
    Orchestrator,

    /// <summary>Architect — produces designs, plans, and ADRs.</summary>
    Architect,

    /// <summary>Code — writes and fixes code.</summary>
    Code,

    /// <summary>Debug — diagnoses and fixes bugs.</summary>
    Debug,

    /// <summary>Qa — verifies and reports quality.</summary>
    Qa
}
