#region MODULE_CONTRACT [DOMAIN(Tools): memory_get_stats MCP tool; CONCEPT(MemoryGetStatsTool): Validate → count → return count; TECH(MCP, ADR-005)]
/**
 * [GREP_SUMMARY]: MemoryGetStatsTool, McpServerTool, IQdrantService, GetStatsAsync, CountAsync, Silent-fallback
 * [STRUCTURE]: MemoryGetStatsTool sealed → ctor(IQdrantService+ILogger) → [McpServerTool("memory_get_stats")] GetStatsAsync → validate → count → return
 *
 * <summary>
 * [PURPOSE]: MCP tool that returns the count of memory entries (optionally filtered by entry type).
 * </summary>
 * <remarks>
 * [INVARIANTS]: ADR-005 silent-fallback at TOOL level: on Qdrant failure, returns Count=-1 (does NOT throw).
 *   Validation errors (ArgumentException) propagate (caller misuse, not infrastructure failure).
 * [RATIONALE]: The tool IS the MCP-facing caller of IQdrantService — it catches Exception and returns
 *   Count=-1 so the MCP connection never breaks (ADR-005).
 * [CHANGES]: LAST_CHANGE: M7 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tools;

using McpMemoryService.Contracts;
using McpMemoryService.Logging;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Validation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

/// <summary>
/// [PURPOSE]: MCP tool for getting memory entry counts (with optional entry_type filter).
/// </summary>
[McpServerToolType]
public sealed class MemoryGetStatsTool
{
    #region Fields

    private readonly IQdrantService _qdrant;
    private readonly QdrantResiliencePolicy _qdrantPolicy;
    private readonly ILogger<MemoryGetStatsTool> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// [PURPOSE]: Creates a new MemoryGetStatsTool with injected dependencies.
    /// </summary>
    /// <param name="qdrant">Service to query Qdrant for entry counts.</param>
    /// <param name="qdrantPolicy">Resilience policy for Qdrant operations.</param>
    /// <param name="logger">Logger for LDD telemetry.</param>
    public MemoryGetStatsTool(IQdrantService qdrant, QdrantResiliencePolicy qdrantPolicy, ILogger<MemoryGetStatsTool> logger)
    {
        _qdrant = qdrant;
        _qdrantPolicy = qdrantPolicy;
        _logger = logger;
    }

    #endregion Constructors

    #region GetStatsAsync

    /// <summary>
    /// [PURPOSE]: Returns the count of memory entries matching the filter.
    /// </summary>
    /// <param name="input">The stats input (project_id, optional entry_type filter).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MemoryGetStatsOutput with the count (or Count=-1 on Qdrant error per ADR-005).</returns>
    /// <exception cref="ArgumentException">Thrown when project_id is empty.</exception>
    /// <remarks>
    /// [INVARIANTS]: On Qdrant failure, returns Count=-1 (does NOT throw) — ADR-005 silent-fallback at tool level.
    ///   Validation errors propagate (caller misuse, not infrastructure failure).
    /// [RATIONALE]: The tool IS the MCP-facing caller of IQdrantService — it catches Exception and returns
    ///   Count=-1 so the MCP connection never breaks (ADR-005).
    /// [CHANGES]: LAST_CHANGE: M7 creation.
    /// </remarks>
    [McpServerTool(Name = "memory_get_stats")]
    public async Task<MemoryGetStatsOutput> GetStatsAsync(
        MemoryGetStatsInput input,
        CancellationToken cancellationToken = default)
    {
        // Validate input (throws ArgumentException — propagates, NOT swallowed)
        InputValidator.ValidateGetStats(input);

        // [IMP:1] get_stats invoked
        _logger.LogInformation(
            $"{LddMarkers.RetrieveEntry} get_stats invoked projectId={input.ProjectId} entryTypeFilter={input.EntryType}");

        try
        {
            // Count from Qdrant (wrapped with QdrantResiliencePolicy — fallback: -1)
            var count = await _qdrantPolicy.ExecuteWithFallbackAsync(
                () => _qdrant.CountAsync(input.ProjectId, input.EntryType, cancellationToken),
                -1,
                "GetStatsAsync-CountAsync");

            // [IMP:2] stats succeeded
            _logger.LogInformation($"{LddMarkers.CaptureUpserted} get_stats succeeded count={count}");

            return new MemoryGetStatsOutput { Count = count };
        }
        catch (Exception ex)
        {
            // [IMP:4] get_stats silent-fallback ADR-005
            _logger.LogError(ex, $"{LddMarkers.CaptureFallback} get_stats silent-fallback ADR-005 projectId={input.ProjectId}");

            return new MemoryGetStatsOutput { Count = -1 };
        }
    }

    #endregion GetStatsAsync
}
