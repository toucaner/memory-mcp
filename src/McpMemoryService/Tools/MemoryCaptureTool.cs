#region MODULE_CONTRACT [DOMAIN(Tools): memory_capture MCP tool; CONCEPT(MemoryCaptureTool): Embed → upsert → return success/failure; TECH(MCP, ADR-005/001)]
/**
 * [GREP_SUMMARY]: MemoryCaptureTool, McpServerTool, IEmbeddingService, IQdrantService, CaptureAsync, Guid, Silent-fallback
 * [STRUCTURE]: MemoryCaptureTool sealed → ctor(IEmbeddingService+IQdrantService+ILogger) → [McpServerTool("memory_capture")] CaptureAsync → validate → embed → upsert → return
 *
 * <summary>
 * [PURPOSE]: MCP tool that generates an embedding for content and upserts it into Qdrant with a GUID v4 point ID.
 * </summary>
 * <remarks>
 * [INVARIANTS]: ADR-005 silent-fallback at TOOL level: on Qdrant failure, returns Success=false (does NOT throw).
 *   Validation errors (ArgumentException) propagate (caller misuse, not infrastructure failure).
 *   entry_type=Summary rejected by InputValidator.ValidateCapture (ADR-001).
 * [RATIONALE]: The tool IS the MCP-facing caller of IEmbeddingService+IQdrantService — it applies ADR-005
 *   by catching Exception and returning a failure DTO, so the MCP connection never breaks.
 * [CHANGES]: LAST_CHANGE: M7 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tools;

using System.Diagnostics;
using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Logging;
using McpMemoryService.Mapping;
using McpMemoryService.Models;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Validation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

/// <summary>
/// [PURPOSE]: MCP tool for capturing new memory entries (embed → upsert → return point ID).
/// </summary>
[McpServerToolType]
public sealed class MemoryCaptureTool
{
    #region Fields

    private readonly IEmbeddingService _embedding;
    private readonly IQdrantService _qdrant;
    private readonly QdrantResiliencePolicy _qdrantPolicy;
    private readonly EmbeddingResiliencePolicy _embeddingPolicy;
    private readonly ILogger<MemoryCaptureTool> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// [PURPOSE]: Creates a new MemoryCaptureTool with injected dependencies.
    /// </summary>
    /// <param name="embedding">Service to generate embedding vectors.</param>
    /// <param name="qdrant">Service to persist entries to Qdrant.</param>
    /// <param name="qdrantPolicy">Resilience policy for Qdrant operations.</param>
    /// <param name="embeddingPolicy">Resilience policy for embedding operations.</param>
    /// <param name="logger">Logger for LDD telemetry.</param>
    public MemoryCaptureTool(
        IEmbeddingService embedding,
        IQdrantService qdrant,
        QdrantResiliencePolicy qdrantPolicy,
        EmbeddingResiliencePolicy embeddingPolicy,
        ILogger<MemoryCaptureTool> logger)
    {
        _embedding = embedding;
        _qdrant = qdrant;
        _qdrantPolicy = qdrantPolicy;
        _embeddingPolicy = embeddingPolicy;
        _logger = logger;
    }

    #endregion Constructors

    #region CaptureAsync

    /// <summary>
    /// [PURPOSE]: Captures a memory entry — validates input, generates embedding, upserts to Qdrant.
    /// </summary>
    /// <param name="input">The capture input (content, project_id, agent_role, entry_type, tags, session_id, metadata).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MemoryCaptureOutput with success status and point ID (or failure details on Qdrant error).</returns>
    /// <exception cref="ArgumentException">Thrown when input validation fails (content/project_id empty, entry_type=Summary).</exception>
    /// <remarks>
    /// [INVARIANTS]: On Qdrant failure, returns Success=false (does NOT throw) — ADR-005 silent-fallback at tool level.
    ///   Validation errors propagate (caller misuse, not infrastructure failure).
    /// [RATIONALE]: The tool IS the MCP-facing caller of IQdrantService — it catches Exception and returns
    ///   failure DTO so the MCP Streamable-HTTP connection never breaks (ADR-005).
    /// [CHANGES]: LAST_CHANGE: M7 creation.
    /// </remarks>
    [McpServerTool(Name = "memory_capture")]
    public async Task<MemoryCaptureOutput> CaptureAsync(
        MemoryCaptureInput input,
        CancellationToken cancellationToken = default)
    {
        // Validate input (throws ArgumentException — propagates, NOT swallowed)
        InputValidator.ValidateCapture(input);

        // [IMP:1] capture invoked
        _logger.LogInformation(
            $"{LddMarkers.CaptureEntry} capture invoked projectId={input.ProjectId} entryType={input.EntryType} agentRole={input.AgentRole} contentLen={input.Content.Length}");

        try
        {
            // Generate embedding (wrapped with EmbeddingResiliencePolicy — fatal on failure, rethrown)
            var vector = await _embeddingPolicy.ExecuteAsync(
                () => _embedding.EmbedAsync(input.Content, cancellationToken),
                "CaptureAsync-EmbedAsync");

            // Debug.Assert: embedding dimension must match
            Debug.Assert(vector.Length == _embedding.Dimension, "embedding dimension mismatch");

            // Generate GUID v4 point ID
            var pointId = Guid.NewGuid();

            // Build MemoryPayload (SessionId null-guard: input.SessionId ?? string.Empty — preserves M3 round-trip)
            var payload = new MemoryPayload
            {
                ProjectId = input.ProjectId,
                SessionId = input.SessionId ?? string.Empty,
                AgentRole = input.AgentRole,
                EntryType = input.EntryType,
                Timestamp = DateTimeOffset.UtcNow,
                Content = input.Content,
                Tags = input.Tags,
                Metadata = input.Metadata
            };

            // Upsert to Qdrant (wrapped with QdrantResiliencePolicy — fallback on failure, returns false)
            var upsertOk = await _qdrantPolicy.ExecuteWithFallbackAsync(
                () => _qdrant.UpsertAsync(pointId, vector, payload, cancellationToken),
                "CaptureAsync-UpsertAsync");

            if (!upsertOk)
            {
                // [IMP:4] capture silent-fallback ADR-005
                _logger.LogError(
                    $"{LddMarkers.CaptureFallback} — upsert failed projectId={input.ProjectId}");

                return new MemoryCaptureOutput
                {
                    Success = false,
                    PointId = string.Empty,
                    Error = "Qdrant upsert failed"
                };
            }

            // [IMP:3] capture succeeded
            _logger.LogInformation(
                $"{LddMarkers.CaptureUpserted} pointId={pointId} vectorDim={vector.Length}");

            return new MemoryCaptureOutput
            {
                Success = true,
                PointId = pointId.ToString(),
                Error = null
            };
        }
        catch (Exception ex)
        {
            // [IMP:4] capture silent-fallback ADR-005
            _logger.LogError(ex, $"{LddMarkers.CaptureFallback} projectId={input.ProjectId} error={ex.Message}");

            return new MemoryCaptureOutput
            {
                Success = false,
                PointId = string.Empty,
                Error = ex.Message
            };
        }
    }

    #endregion CaptureAsync
}
