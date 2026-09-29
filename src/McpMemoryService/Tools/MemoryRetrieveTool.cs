#region MODULE_CONTRACT [DOMAIN(Tools): memory_retrieve MCP tool; CONCEPT(MemoryRetrieveTool): Validate → embed query → Qdrant search → map results; TECH(MCP, ADR-005/001/002, SPEC §4.1)]
/**
 * [GREP_SUMMARY]: MemoryRetrieveTool, memory_retrieve, McpServerToolType, McpServerTool, IEmbeddingService, IQdrantService, SearchAsync, EmbedAsync, MemoryEntry, MemoryRetrieveResult, silent-fallback, ADR-005, ILogger, IMP:1/IMP:2/IMP:3
 * [STRUCTURE]: MemoryRetrieveTool sealed → ctor(IEmbeddingService+IQdrantService+ILogger) → [McpServerTool("memory_retrieve")] RetrieveAsync → validate → embed → search → map → return
 *
 * <summary>
 * [PURPOSE]: MCP tool for semantic search of past memory entries via query embedding + Qdrant vector search.
 * </summary>
 * <remarks>
 * [INVARIANTS]: ADR-005 silent-fallback at TOOL level: on Qdrant/ONNX failure, returns empty Results (does NOT throw).
 *   Validation errors (ArgumentException) propagate (caller misuse, not infrastructure failure).
 *   Filters (agent_role_filter, entry_type_filter) passed through to IQdrantService.SearchAsync.
 * [RATIONALE]: The tool IS the MCP-facing caller of IEmbeddingService+IQdrantService — it applies ADR-005
 *   by catching Exception and returning empty results, so the MCP connection never breaks (SPEC §7).
 * [CHANGES]: LAST_CHANGE: M8 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tools;

using McpMemoryService.Contracts;
using McpMemoryService.Logging;
using McpMemoryService.Models;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Validation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class MemoryRetrieveTool
{
    #region Fields

    private readonly IEmbeddingService _embedding;
    private readonly IQdrantService _qdrant;
    private readonly QdrantResiliencePolicy _qdrantPolicy;
    private readonly EmbeddingResiliencePolicy _embeddingPolicy;
    private readonly ILogger<MemoryRetrieveTool> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// [PURPOSE]: Constructs the retrieve tool with its dependencies.
    /// </summary>
    /// <param name="embedding">Embedding service for query vectorization (384-dim L2-normalized).</param>
    /// <param name="qdrant">Qdrant service for vector search.</param>
    /// <param name="qdrantPolicy">Resilience policy for Qdrant operations.</param>
    /// <param name="embeddingPolicy">Resilience policy for embedding operations.</param>
    /// <param name="logger">Structured logger for LDD markers.</param>
    public MemoryRetrieveTool(
        IEmbeddingService embedding,
        IQdrantService qdrant,
        QdrantResiliencePolicy qdrantPolicy,
        EmbeddingResiliencePolicy embeddingPolicy,
        ILogger<MemoryRetrieveTool> logger)
    {
        _embedding = embedding;
        _qdrant = qdrant;
        _qdrantPolicy = qdrantPolicy;
        _embeddingPolicy = embeddingPolicy;
        _logger = logger;
    }

    #endregion Constructors

    #region RetrieveAsync

    /// <summary>
    /// [PURPOSE]: Performs semantic search for relevant past memory entries.
    /// </summary>
    /// <param name="input">Retrieve input (query, project_id, optional filters, limit).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MemoryRetrieveOutput with ranked results (empty on Qdrant/ONNX failure per ADR-005).</returns>
    /// <exception cref="ArgumentException">Thrown when input validation fails.</exception>
    /// <remarks>
    /// [INVARIANTS]: On Qdrant/ONNX failure, returns empty Results (does NOT throw) — ADR-005 silent-fallback at tool level.
    ///   Validation errors propagate (caller misuse, not infrastructure failure).
    /// [RATIONALE]: The tool IS the MCP-facing caller — catches Exception and returns empty Results per SPEC §7.
    /// [CHANGES]: LAST_CHANGE: M8 creation.
    /// </remarks>
    [McpServerTool(Name = "memory_retrieve")]
    public async Task<MemoryRetrieveOutput> RetrieveAsync(
        MemoryRetrieveInput input,
        CancellationToken cancellationToken = default)
    {
        // Validate input (throws ArgumentException — propagates, NOT swallowed)
        InputValidator.ValidateRetrieve(input);

        try
        {
            // Generate query embedding (384-dim L2-normalized per M4 / ADR-011)
            var vector = await _embeddingPolicy.ExecuteAsync(
                () => _embedding.EmbedAsync(input.Query, cancellationToken),
                "RetrieveAsync-EmbedAsync");

            // [IMP:2] embedding generated
            _logger.LogInformation(
                $"{LddMarkers.RetrieveEmbedding} dim={vector.Length} queryLen={input.Query.Length}");

            // Semantic search in Qdrant with optional filters (wrapped with QdrantResiliencePolicy — fallback: empty list)
            var entries = await _qdrantPolicy.ExecuteWithFallbackAsync(
                () => _qdrant.SearchAsync(
                    vector,
                    input.ProjectId,
                    input.AgentRoleFilter,
                    input.EntryTypeFilter,
                    input.Limit,
                    cancellationToken),
                Array.Empty<MemoryEntry>(),
                "RetrieveAsync-SearchAsync");

            // [IMP:3] search returned N results
            _logger.LogInformation(
                $"{LddMarkers.RetrieveSearchComplete} count={entries.Count} projectId={input.ProjectId}");

            // Map MemoryEntry → MemoryRetrieveResult
            var results = entries.Select(MapToRetrieveResult).ToList();

            return new MemoryRetrieveOutput { Results = results };
        }
        catch (Exception ex)
        {
            // [IMP:4] silent-fallback ADR-005 — return empty Results
            _logger.LogWarning(
                ex,
                $"{LddMarkers.RetrieveFallbackEmpty} projectId={input.ProjectId}");

            return new MemoryRetrieveOutput { Results = Array.Empty<MemoryRetrieveResult>() };
        }
    }

    private static MemoryRetrieveResult MapToRetrieveResult(MemoryEntry entry)
    {
        return new MemoryRetrieveResult
        {
            PointId = entry.PointId.ToString("D"),
            AgentRole = entry.Payload.AgentRole,
            EntryType = entry.Payload.EntryType,
            Content = entry.Payload.Content,
            Timestamp = entry.Payload.Timestamp,
            Score = entry.Score ?? 0f,
            Tags = entry.Payload.Tags,
        };
    }

    #endregion RetrieveAsync
}
