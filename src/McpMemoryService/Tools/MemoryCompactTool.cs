#region MODULE_CONTRACT [DOMAIN(Tools): memory_compact MCP tool; CONCEPT(MemoryCompactTool): Validate → batch fetch → insufficient_data skip → LLM summarize (transactional guard) → embed summary → upsert summary entry → hard delete sources; TECH(MCP, ADR-003/004/005, SPEC §4.4)]
/**
 * [GREP_SUMMARY]: MemoryCompactTool, memory_compact, McpServerToolType, McpServerTool, IQdrantService, ILlmSummarizerService, IEmbeddingService, GetBatchForCompactAsync, SummarizeAsync, EmbedAsync, UpsertAsync, DeleteAsync, transactional, ADR-003, ADR-004, ADR-005, ILogger, IMP:1/IMP:2/IMP:3/IMP:4
 * [STRUCTURE]: MemoryCompactTool sealed → ctor(IQdrantService+ILlmSummarizerService+IEmbeddingService+ILogger) → [McpServerTool("memory_compact")] ExecuteAsync → validate → fetch batch → skip if insufficient → LLM summarize (catch TaskCanceledException/HttpRequestException as transactional guards) → embed summary → upsert summary entry → hard delete sources → return completed
 *
 * <summary>
 * [PURPOSE]: MCP tool for compacting old memory entries into a single LLM-generated summary — the critical transactional operation that bounds Qdrant storage growth.
 * </summary>
 * <remarks>
 * [INVARIANTS]: ADR-003 transactional guarantee: source entries are ONLY deleted after LLM summarizes AND summary entry is upserted.
 *   ADR-004 non-blocking: insufficient_data returns status=skipped (NOT error) without touching LLM/Qdrant.
 *   ADR-005 silent-fallback: LLM exceptions caught at tool boundary (returns error status, NOT throw); post-LLM Qdrant/Embedding failures also caught (returns error, sources NOT deleted).
 * [RATIONALE]: The tool IS the transactional boundary — it applies ADR-003/004/005 at the call site, not inside the services.
 * [CHANGES]: LAST_CHANGE: M9 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tools;

using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Logging;
using McpMemoryService.Models;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Validation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class MemoryCompactTool
{
    #region Fields

    private readonly IQdrantService _qdrantService;
    private readonly ILlmSummarizerService _llmSummarizerService;
    private readonly IEmbeddingService _embeddingService;
    private readonly QdrantResiliencePolicy _qdrantPolicy;
    private readonly EmbeddingResiliencePolicy _embeddingPolicy;
    private readonly ILogger<MemoryCompactTool> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// [PURPOSE]: Constructs the compact tool with its dependencies.
    /// </summary>
    /// <param name="qdrantService">Qdrant service for batch fetch, summary upsert, and source deletion.</param>
    /// <param name="llmSummarizerService">LLM summarization service (throws on timeout/5xx — caught at tool level for ADR-003/005).</param>
    /// <param name="embeddingService">Embedding service for summary vectorization (384-dim L2-normalized).</param>
    /// <param name="qdrantPolicy">Resilience policy for Qdrant operations.</param>
    /// <param name="embeddingPolicy">Resilience policy for embedding operations.</param>
    /// <param name="logger">Structured logger for LDD markers.</param>
    public MemoryCompactTool(
        IQdrantService qdrantService,
        ILlmSummarizerService llmSummarizerService,
        IEmbeddingService embeddingService,
        QdrantResiliencePolicy qdrantPolicy,
        EmbeddingResiliencePolicy embeddingPolicy,
        ILogger<MemoryCompactTool> logger)
    {
        _qdrantService = qdrantService;
        _llmSummarizerService = llmSummarizerService;
        _embeddingService = embeddingService;
        _qdrantPolicy = qdrantPolicy;
        _embeddingPolicy = embeddingPolicy;
        _logger = logger;
    }

    #endregion Constructors

    #region ExecuteAsync

    /// <summary>
    /// [PURPOSE]: Performs a compact operation: validates input, fetches batch, optionally summarizes via LLM, upserts summary entry, hard-deletes sources.
    /// </summary>
    /// <param name="input">Compact input (project_id, batch_size).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MemoryCompactOutput with status (completed/skipped/error) and branch-specific data.</returns>
    /// <exception cref="ArgumentException">Thrown when input validation fails.</exception>
    /// <remarks>
    /// [INVARIANTS]: ADR-003 — source entries NEVER deleted on LLM failure (transactional guarantee).
    ///   ADR-004 — insufficient_data returns "skipped" (NOT error) without touching LLM/Qdrant.
    ///   ADR-005 — LLM exceptions caught as structured error status (no connection break); post-LLM exceptions also caught.
    /// [RATIONALE]: The tool IS the transactional boundary — it catches LLM exceptions and returns error status without deleting sources.
    /// [CHANGES]: LAST_CHANGE: M9 creation.
    /// </remarks>
    [McpServerTool(Name = "memory_compact")]
    public async Task<MemoryCompactOutput> ExecuteAsync(
        MemoryCompactInput input,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate input (throws ArgumentException — propagates, NOT swallowed)
        InputValidator.ValidateCompact(input);

        // 2. Fetch batch (excludes entry_type=summary, ordered by timestamp ASC — M5 invariant)
        // Wrapped with QdrantResiliencePolicy — fallback: empty array (triggers insufficient_data)
        var entries = await _qdrantPolicy.ExecuteWithFallbackAsync(
            () => _qdrantService.GetBatchForCompactAsync(input.ProjectId, input.BatchSize, cancellationToken),
            Array.Empty<MemoryEntry>(),
            "CompactAsync-GetBatchForCompactAsync");

        _logger.LogInformation($"{LddMarkers.CompactBatchFetched} count={entries.Count}");

        // 3. Non-blocking: insufficient data (ADR-004)
        if (entries.Count < input.BatchSize)
        {
            _logger.LogInformation($"{LddMarkers.CompactSkipped} available={entries.Count} required={input.BatchSize}");
            return new MemoryCompactOutput
            {
                Status = "skipped",
                Reason = "insufficient_data",
                Available = entries.Count,
                Required = input.BatchSize
            };
        }

        // 4. Extract contents
        var contents = entries.Select(e => e.Payload.Content).ToList();

        // 5. LLM summarization — transactional guard (M6 service THROWS, M9 tool catches)
        string summary;
        try
        {
            summary = await _llmSummarizerService.SummarizeAsync(contents, cancellationToken);

            _logger.LogInformation($"{LddMarkers.CompactLlmCall} length={summary.Length}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, $"{LddMarkers.CompactLlmTimeout} — source entries NOT deleted");
            return new MemoryCompactOutput
            {
                Status = "error",
                Reason = "llm_timeout",
                Error = ex.Message
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, $"{LddMarkers.CompactLlm5xx} — source entries NOT deleted");
            return new MemoryCompactOutput
            {
                Status = "error",
                Reason = "llm_5xx",
                Error = ex.Message
            };
        }

        // 6. LLM succeeded — capture summary entry (transactional commit point reached)
        try
        {
            var summaryPointId = Guid.NewGuid();
            // Embedding wrapped with EmbeddingResiliencePolicy
            var summaryVector = await _embeddingPolicy.ExecuteAsync(
                () => _embeddingService.EmbedAsync(summary, cancellationToken),
                "CompactAsync-EmbedSummary");
            var summaryPayload = new MemoryPayload
            {
                ProjectId = input.ProjectId,
                SessionId = "compact",
                AgentRole = AgentRole.Orchestrator,
                EntryType = EntryType.Summary,
                Timestamp = DateTimeOffset.UtcNow,
                Content = summary,
                Tags = new[] { "compact", "summary" },
                Metadata = new Metadata
                {
                    Session = $"compact-{DateTimeOffset.UtcNow:yyyyMMdd}"
                }
            };
            // Upsert NOT wrapped — must not fallback (post-LLM failure → error status, no delete)
            await _qdrantService.UpsertAsync(summaryPointId, summaryVector, summaryPayload, cancellationToken);

            _logger.LogInformation($"{LddMarkers.CompactSummaryCaptured} pointId={summaryPointId}");

            // 7. Hard delete source entries — NOT wrapped (ADR-003: never delete on failure)
            var sourceIds = entries.Select(e => e.PointId).ToList();
            await _qdrantService.DeleteAsync(sourceIds, cancellationToken);

            _logger.LogInformation($"{LddMarkers.CompactSourceDeleted} count={sourceIds.Count}");

            // 8. Return completed
            return new MemoryCompactOutput
            {
                Status = "completed",
                SourceCount = entries.Count,
                SummaryPointId = summaryPointId.ToString()
            };
        }
        catch (Exception ex)
        {
            // Post-LLM failure (Qdrant/Embedding) — silent-fallback per ADR-005
            _logger.LogError(ex, $"{LddMarkers.CompactSummaryCaptured.Replace("][SUCCESS]", "][FATAL")} post-LLM failure — Qdrant/Embedding error");
            return new MemoryCompactOutput
            {
                Status = "error",
                Reason = "post_llm_failure",
                Error = ex.Message
            };
        }
    }

    #endregion ExecuteAsync
}
