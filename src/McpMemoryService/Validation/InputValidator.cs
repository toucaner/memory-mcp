#region MODULE_CONTRACT [DOMAIN(Validation): Input validation; CONCEPT(InputValidator): Static validation for all 4 tool inputs; TECH(M7)]
/**
 * [GREP_SUMMARY]: InputValidator, static, ValidateCapture, ValidateGetStats, ValidateRetrieve, ValidateCompact, ArgumentException
 * [STRUCTURE]: InputValidator static class → 4 Validate* methods (capture/get_stats/retrieve/compact) → ArgumentException
 *
 * <summary>
 * [PURPOSE]: Static validation methods for all 4 MCP tool inputs. Throws ArgumentException with descriptive messages.
 * </summary>
 * <remarks>
 * [INVARIANTS]: All methods throw ArgumentException for invalid input (NOT swallowed).
 * [RATIONALE]: Validation is a separate concern from business logic (ADR-005 silent-fallback applies ONLY to infrastructure failures).
 * [CHANGES]: LAST_CHANGE: M7 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Validation;

using McpMemoryService.Contracts;
using McpMemoryService.Enums;

/// <summary>
/// [PURPOSE]: Static validation methods for all 4 MCP tool inputs.
/// </summary>
public static class InputValidator
{
    /// <summary>
    /// [PURPOSE]: Validates MemoryCaptureInput — content non-empty, project_id non-empty, entry_type != Summary (ADR-001).
    /// </summary>
    /// <param name="input">The capture input to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
    /// <exception cref="ArgumentException">Thrown when content or project_id is empty/whitespace, or entry_type is Summary.</exception>
    public static void ValidateCapture(MemoryCaptureInput input)
    {
        ArgumentNullException.ThrowIfNull(input, nameof(input));

        if (string.IsNullOrWhiteSpace(input.Content))
            throw new ArgumentException("Content must not be empty", nameof(input.Content));

        if (string.IsNullOrWhiteSpace(input.ProjectId))
            throw new ArgumentException("ProjectId must not be empty", nameof(input.ProjectId));

        if (input.EntryType == EntryType.Summary)
            throw new ArgumentException("entry_type=Summary is not allowed for capture (only via compact)", nameof(input.EntryType));
    }

    /// <summary>
    /// [PURPOSE]: Validates MemoryGetStatsInput — project_id non-empty.
    /// </summary>
    /// <param name="input">The stats input to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
    /// <exception cref="ArgumentException">Thrown when project_id is empty/whitespace.</exception>
    public static void ValidateGetStats(MemoryGetStatsInput input)
    {
        ArgumentNullException.ThrowIfNull(input, nameof(input));

        if (string.IsNullOrWhiteSpace(input.ProjectId))
            throw new ArgumentException("ProjectId must not be empty", nameof(input.ProjectId));
    }

    /// <summary>
    /// [PURPOSE]: Validates MemoryRetrieveInput — query and project_id non-empty, Limit in [1, 10].
    /// </summary>
    /// <param name="input">The retrieve input to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
    /// <exception cref="ArgumentException">Thrown when query/project_id is empty or Limit is out of range.</exception>
    /// <remarks>
    /// [INVARIANTS]: This is a FORWARD stub (M8 caller) — full body declared so M8 does not touch this file.
    /// [RATIONALE]: Keeps validation logic centralized; M8/M9 only add callers.
    /// </remarks>
    public static void ValidateRetrieve(MemoryRetrieveInput input)
    {
        ArgumentNullException.ThrowIfNull(input, nameof(input));

        if (string.IsNullOrWhiteSpace(input.Query))
            throw new ArgumentException("Query must not be empty", nameof(input.Query));

        if (string.IsNullOrWhiteSpace(input.ProjectId))
            throw new ArgumentException("ProjectId must not be empty", nameof(input.ProjectId));

        if (input.Limit < 1 || input.Limit > 10)
            throw new ArgumentException("Limit must be between 1 and 10", nameof(input.Limit));
    }

    /// <summary>
    /// [PURPOSE]: Validates MemoryCompactInput — project_id non-empty, BatchSize in [1, 100].
    /// </summary>
    /// <param name="input">The compact input to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
    /// <exception cref="ArgumentException">Thrown when project_id is empty or BatchSize is out of range.</exception>
    /// <remarks>
    /// [INVARIANTS]: This is a FORWARD stub (M9 caller) — full body declared so M9 does not touch this file.
    /// [RATIONALE]: Keeps validation logic centralized; M8/M9 only add callers.
    /// </remarks>
    public static void ValidateCompact(MemoryCompactInput input)
    {
        ArgumentNullException.ThrowIfNull(input, nameof(input));

        if (string.IsNullOrWhiteSpace(input.ProjectId))
            throw new ArgumentException("ProjectId must not be empty", nameof(input.ProjectId));

        if (input.BatchSize < 1 || input.BatchSize > 100)
            throw new ArgumentException("BatchSize must be between 1 and 100", nameof(input.BatchSize));
    }
}
