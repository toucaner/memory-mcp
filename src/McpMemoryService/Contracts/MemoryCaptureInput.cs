#region MODULE_CONTRACT [DOMAIN(Contract): Capture tool I/O; CONCEPT(MemoryCaptureInput): User-facing capture request; TECH(SPEC §4.2)]
/**
 * [GREP_SUMMARY]: MemoryCaptureInput, contract, input, DTO, capture, tool, required, Content, ProjectId, AgentRole, EntryType, Tags, SessionId, Metadata
 * [STRUCTURE]: MemoryCaptureInput sealed record → Content(ProjectId, AgentRole, EntryType) required + Tags + SessionId + Metadata → input to memory_capture tool
 *
 * <summary>
 * [PURPOSE]: Input DTO for the memory_capture tool — user-facing capture request with content and metadata.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Content, ProjectId, AgentRole, and EntryType are required. Capture tool (M7) enforces entry_type exclusion of Summary.
 * [RATIONALE]: SPEC §4.2 — the capture tool input; transport property keys are snake_case (content/project_id/agent_role/
 *   entry_type/tags/session_id/metadata) enforced via [JsonPropertyName] attributes (M7 rung-d fix, see BUG_FIX_CONTEXT
 *   in Program.cs McpServerOptions post-configure block — SDK 1.4.0 exposes no serializer knob on McpServerOptions).
 *   No validation logic here (Limit≤10, ProjectId non-empty all live in the tool).
 * [CHANGES]: LAST_CHANGE: M7 — added [JsonPropertyName] snake_case transport overrides (rung-d deterministic fallback);
 *   M3 initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;
using McpMemoryService.Enums;
using McpMemoryService.Models;

/// <summary>
/// [PURPOSE]: Input DTO for the memory_capture tool.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Content, ProjectId, AgentRole, EntryType are required (enforced by C# 'required' keyword).
/// Capture tool (M7) additionally enforces entry_type excludes Summary.
/// </remarks>
public sealed record MemoryCaptureInput
{
    // BUG_FIX_CONTEXT: [HYPOTHESIS: M7 snake_case transport deliverable was OMITTED — @qa AC-4/AC-8 BLOCK (mem-027).
    //   SDK 1.4.0 exposes NO serializer knob on McpServerOptions (compile-probe CS1061), so the deterministic fallback
    //   is [JsonPropertyName] overrides on this DTO per SPEC §4.2 snake_case contract. See Program.cs M7 scar for the
    //   full ladder walk (primary + rung-b failed; rungs a/c compile-blind; rung-d chosen). ts=2026-07-01T16:00:00Z]
    // BUG_FIX_CONTEXT: [scar — rung-d: snake_case names fixed per-property. Immunized by SnakeCaseTransportTests];

    /// <summary>[PURPOSE]: The semantic content to capture (decision, bug fix, requirement, etc.).</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary>[PURPOSE]: Project identifier (SHA-256 of workspace root).</summary>
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    /// <summary>[PURPOSE]: Agent role that owns this capture.</summary>
    [JsonPropertyName("agent_role")]
    public required AgentRole AgentRole { get; init; }

    /// <summary>[PURPOSE]: Semantic type of the entry being captured.</summary>
    [JsonPropertyName("entry_type")]
    public required EntryType EntryType { get; init; }

    /// <summary>[PURPOSE]: Optional tags for classification. Defaults to empty.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>[PURPOSE]: Optional session identifier.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>[PURPOSE]: Optional trace metadata (file path, error code, session).</summary>
    [JsonPropertyName("metadata")]
    public Metadata? Metadata { get; init; }
}
