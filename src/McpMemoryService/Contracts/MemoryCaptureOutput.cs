#region MODULE_CONTRACT [DOMAIN(Contract): Capture tool output; CONCEPT(MemoryCaptureOutput): Capture result; TECH(SPEC §4.2)]
/**
 * [GREP_SUMMARY]: MemoryCaptureOutput, contract, output, DTO, capture, result, Success, PointId, Error
 * [STRUCTURE]: MemoryCaptureOutput sealed record → Success + PointId + Error → output from memory_capture tool
 *
 * <summary>
 * [PURPOSE]: Output DTO for the memory_capture tool — indicates success/failure and the stored point ID.
 * </summary>
 * <remarks>
 * [INVARIANTS]: On success: Success=true, PointId is a valid GUID string, Error is null.
 *   On failure: Success=false, PointId may be empty, Error contains the reason (ADR-005).
 * [RATIONALE]: ADR-005 — Error field enables silent-fallback reporting without exceptions. Transport property keys
 *   are snake_case (success/point_id/error) enforced via [JsonPropertyName] (M7 rung-d fix — SDK 1.4.0 has no
 *   serializer knob on McpServerOptions; see Program.cs M7 scar).
 * [CHANGES]: LAST_CHANGE: M7 — added [JsonPropertyName] snake_case transport overrides (rung-d deterministic fallback);
 *   M3 initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// [PURPOSE]: Output DTO for the memory_capture tool.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Success and PointId are always set. Error is null on success.
/// </remarks>
public sealed record MemoryCaptureOutput
{
    // BUG_FIX_CONTEXT: [HYPOTHESIS: M7 snake_case transport deliverable was OMITTED — @qa AC-4/AC-8 BLOCK (mem-027).
    //   SDK 1.4.0 exposes NO serializer knob on McpServerOptions (compile-probe CS1061), so the deterministic fallback
    //   is [JsonPropertyName] overrides on this DTO per SPEC §4.2 snake_case contract. See Program.cs M7 scar for the
    //   full ladder walk. ts=2026-07-01T16:00:00Z]
    // BUG_FIX_CONTEXT: [scar — rung-d: snake_case names fixed per-property. Immunized by SnakeCaseTransportTests];

    /// <summary>[PURPOSE]: Whether the capture operation succeeded.</summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>[PURPOSE]: The Qdrant point ID where the entry was stored (on success).</summary>
    [JsonPropertyName("point_id")]
    public required string PointId { get; init; }

    /// <summary>[PURPOSE]: Error description when Success=false (silent-fallback, per ADR-005).</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
