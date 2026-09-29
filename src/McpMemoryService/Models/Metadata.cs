#region MODULE_CONTRACT [DOMAIN(Model): Metadata carrier; CONCEPT(Metadata): Optional trace context; TECH(SPEC §3)]
/**
 * [GREP_SUMMARY]: Metadata, record, FilePath, ErrorCode, Session, nullable, optional, init
 * [STRUCTURE]: Metadata sealed record → FilePath? | ErrorCode? | Session? → nested in MemoryPayload
 *
 * <summary>
 * [PURPOSE]: Optional trace context attached to a memory entry (file path, error code, session ID).
 * </summary>
 * <remarks>
 * [INVARIANTS]: All fields are nullable — this record is always optional in MemoryPayload.
 * [RATIONALE]: SPEC §3 metadata object. Matches Qdrant payload "metadata" field shape. Transport property keys
 *   are snake_case (file_path/error_code/session) enforced via [JsonPropertyName] (M7 rung-d fix — SDK 1.4.0 has
 *   no serializer knob on McpServerOptions; see Program.cs M7 scar). This nested object is reachable from the
 *   memory_capture input schema, so its sub-fields must also be snake_case for the full SPEC §4.2 contract.
 * [CHANGES]: LAST_CHANGE: M7 — added [JsonPropertyName] snake_case transport overrides (rung-d deterministic fallback,
 *   covers the nested metadata object reachable from MemoryCaptureInput); M3 initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Models;

using System.Text.Json.Serialization;

/// <summary>
/// [PURPOSE]: Optional trace context attached to a memory entry.
/// </summary>
public sealed record Metadata
{
    // BUG_FIX_CONTEXT: [HYPOTHESIS: M7 snake_case transport deliverable OMITTED — @qa AC-4 BLOCK (mem-027). This nested
    //   object is reachable from MemoryCaptureInput.Metadata, so the SPEC §4.2 snake_case contract extends to its
    //   sub-fields. SDK 1.4.0 has no serializer knob (compile-probe CS1061) — deterministic fallback [JsonPropertyName].
    //   See Program.cs M7 scar for the full ladder walk. ts=2026-07-01T16:00:00Z]
    // BUG_FIX_CONTEXT: [scar — rung-d: nested metadata sub-fields snake_case. Immunized by SnakeCaseTransportTests];

    /// <summary>[PURPOSE]: File path associated with the entry (e.g., source file where a bug was found).</summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    /// <summary>[PURPOSE]: Error code if the entry is related to a specific error.</summary>
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    /// <summary>[PURPOSE]: Session identifier for traceability.</summary>
    [JsonPropertyName("session")]
    public string? Session { get; init; }
}
