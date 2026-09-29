#region MODULE_CONTRACT [DOMAIN(Services): LLM summarization contract; CONCEPT(ILlmSummarizerService): contract-only; TECH(SPEC §4.4, M6)]
/**
 * [GREP_SUMMARY]: ILlmSummarizerService, SummarizeAsync, llama.cpp, summary, TaskCanceledException, HttpRequestException, ADR-005
 * [STRUCTURE]: ILlmSummarizerService → SummarizeAsync(IReadOnlyList<string>, CancellationToken) → Task<string> | ArgumentNullException | ArgumentException | TaskCanceledException | HttpRequestException
 *
 * <summary>
 * [PURPOSE]: Contract for LLM-based summarization of memory entries via the llama.cpp /completion HTTP endpoint.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Implementations MUST NOT swallow TaskCanceledException (60s timeout) or HttpRequestException (5xx/network) —
 *   the M9 compact caller relies on propagation to apply ADR-005 silent-fallback and to gate ADR-003 hard-delete-of-sources.
 * [RATIONALE]: Interface allows M10 resilience wrapping and test mocking. SPEC §4.4 + ADR-005: the service throws; the caller catches.
 * [CHANGES]: LAST_CHANGE: M6 — initial creation (this @debug fix counter=1 adds MODULE_CONTRACT + [PURPOSE] tags to match the
 *   IEmbeddingService/IQdrantService precedent; the existing method docs were already complete and are preserved).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Services;

/// <summary>
/// [PURPOSE]: Service for LLM-based summarization via the llama.cpp HTTP endpoint.
/// </summary>
public interface ILlmSummarizerService
{
    /// <summary>
    /// [PURPOSE]: Summarizes a list of memory entry contents into a single dense summary via llama.cpp.
    /// </summary>
    /// <param name="contents">List of memory entry contents to summarize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summarized text.</returns>
    /// <exception cref="TaskCanceledException">Request timed out (60s per SPEC §4.4).</exception>
    /// <exception cref="HttpRequestException">HTTP 5xx or network error.</exception>
    Task<string> SummarizeAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default);
}