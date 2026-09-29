#region MODULE_CONTRACT [DOMAIN(Model): Qdrant payload record; CONCEPT(MemoryPayload): Semantic memory entry shape; TECH(SPEC §3)]
/**
 * [GREP_SUMMARY]: MemoryPayload, record, ProjectId, SessionId, AgentRole, EntryType, Timestamp, Content, Tags, Metadata, Qdrant, required, init
 * [STRUCTURE]: MemoryPayload sealed record → 6 required props + Tags(default Array.Empty) + Metadata? → stored as Qdrant payload
 *
 * <summary>
 * [PURPOSE]: Qdrant payload record representing a semantic memory entry — matches SPEC.md §3 payload schema.
 * </summary>
 * <remarks>
 * [INVARIANTS]: ProjectId, SessionId, AgentRole, EntryType, Timestamp, Content are all required (non-null at creation).
 * [RATIONALE]: This is the canonical shape of a memory entry stored in Qdrant. The Tags collection defaults to an empty array to avoid null-checks downstream.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation per SPEC §3.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Models;

using McpMemoryService.Enums;

/// <summary>
/// [PURPOSE]: Qdrant payload record representing a semantic memory entry.
/// </summary>
/// <remarks>
/// [INVARIANTS]:
///   - ProjectId, SessionId, AgentRole, EntryType, Timestamp, Content are required (cannot be null).
///   - Tags defaults to an empty array (never null).
///   - Metadata is optional.
/// </remarks>
public sealed record MemoryPayload
{
    /// <summary>[PURPOSE]: Identifier for the project (SHA-256 hash of workspace root, first 16 hex chars).</summary>
    public required string ProjectId { get; init; }

    /// <summary>[PURPOSE]: Identifier for the development session.</summary>
    public required string SessionId { get; init; }

    /// <summary>[PURPOSE]: Agent role that created this entry (Orchestrator, Architect, Code, Debug, Qa).</summary>
    public required AgentRole AgentRole { get; init; }

    /// <summary>[PURPOSE]: Semantic type of this memory entry (Decision, BugFix, Requirement, Summary, Rejection, Insight).</summary>
    public required EntryType EntryType { get; init; }

    /// <summary>[PURPOSE]: ISO 8601 timestamp of when the entry was created.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>[PURPOSE]: The semantic content of the memory entry (the actual insight/decision/bug fix text).</summary>
    public required string Content { get; init; }

    /// <summary>[PURPOSE]: Optional tags for classification and filtering. Defaults to an empty array.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>[PURPOSE]: Optional trace context (file path, error code, session).</summary>
    public Metadata? Metadata { get; init; }
}
