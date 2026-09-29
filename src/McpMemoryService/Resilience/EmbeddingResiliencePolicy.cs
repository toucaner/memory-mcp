#region MODULE_CONTRACT [DOMAIN(Resilience): Embedding resilience policy; CONCEPT(EmbeddingResiliencePolicy): wraps ONNX inference with try/catch + fatal logging + rethrow; TECH(AggregateException, ADR-005)]
/**
 * [GREP_SUMMARY]: EmbeddingResiliencePolicy, ExecuteAsync, ONNX, InvalidOperationException, rethrow, fatal
 * [STRUCTURE]: EmbeddingResiliencePolicy → ExecuteAsync(action, opName, ct) → try { return await action() } → catch { log [IMP:FATAL]; throw }
 *
 * <summary>
 * [PURPOSE]: Wraps ONNX embedding calls with fatal logging — on failure, logs [IMP:FATAL] and rethrows (ONNX runtime failure is fatal per ADR-005).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Always rethrows — never returns a fallback. ONNX failures propagate to GlobalExceptionMiddleware which returns 503.
 *   Startup failure (model load in OnnxEmbeddingService.ctor) causes Exit 1 (handled in M4).
 * [RATIONALE]: Per ADR-005, ONNX runtime failures are fatal — the tool catches the propagated exception and returns empty results,
 *   while the middleware ensures no MCP connection break.
 * [CHANGES]: LAST_CHANGE: M10 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Resilience;

using McpMemoryService.Logging;
using Microsoft.Extensions.Logging;

/// <summary>
/// [PURPOSE]: Resilience policy for ONNX embedding operations — logs fatal errors and rethrows.
/// </summary>
/// <remarks>
/// ONNX runtime failures are treated as fatal (ADR-005): they propagate to the tool's catch block,
/// which returns empty results, and to GlobalExceptionMiddleware which returns 503.
/// The policy does NOT swallow exceptions — it only adds structured logging.
/// </remarks>
public sealed class EmbeddingResiliencePolicy
{
    #region Fields

    private readonly ILogger<EmbeddingResiliencePolicy> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="EmbeddingResiliencePolicy"/>.
    /// </summary>
    /// <param name="logger">Logger for embedding diagnostics.</param>
    public EmbeddingResiliencePolicy(ILogger<EmbeddingResiliencePolicy> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructors

    #region ExecuteAsync

    /// <summary>
    /// [PURPOSE]: Executes an embedding operation with fatal logging on failure — rethrows the exception.
    /// </summary>
    /// <param name="action">The embedding operation (e.g., _embeddingService.EmbedAsync).</param>
    /// <param name="operationName">Name of the operation (for logging).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The embedding vector.</returns>
    /// <remarks>
    /// Catches any exception, logs [IMP:FATAL], then rethrows.
    /// The caller (tool) catches the propagated exception and returns empty results per ADR-005.
    /// </remarks>
    public async Task<float[]> ExecuteAsync(
        Func<Task<float[]>> action,
        string operationName,
        CancellationToken ct = default)
    {
        try
        {
            return await action();
        }
        // BUG_FIX_CONTEXT: [HYPOTHESIS: TaskCanceledException derives from OperationCanceledException, so a separate catch (TaskCanceledException) after catch (OperationCanceledException) is unreachable — CS0160.]
        // BUG_FIX_CONTEXT: [Why old approach failed: C# compiler rejects unreachable catch clauses (CS0160). OperationCanceledException is the base type and already covers both OperationCanceled and TaskCanceled semantics, so the redundant clause must be removed.]
        catch (OperationCanceledException)
        {
            // Cancellation propagates unchanged — never logged as fatal.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, $"{LddMarkers.OnnxFatal} {operationName}: {ex.GetType().Name} — {ex.Message}");
            throw;
        }
    }

    #endregion ExecuteAsync
}
