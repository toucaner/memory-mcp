#region MODULE_CONTRACT [DOMAIN(Resilience): Qdrant resilience policy; CONCEPT(QdrantResiliencePolicy): wraps Qdrant calls with try/catch + fallback; TECH(AggregateException, ADR-005)]
/**
 * [GREP_SUMMARY]: QdrantResiliencePolicy, ExecuteWithFallbackAsync, QdrantException, RpcException, AggregateException, fallback
 * [STRUCTURE]: QdrantResiliencePolicy → ExecuteWithFallbackAsync<T>(action, fallback, opName, ct) → try { await action() } → catch { log [IMP:WARN]; return fallback }
 *
 * <summary>
 * [PURPOSE]: Wraps Qdrant operations with fallback — on QdrantException/AggregateException (inner RpcException), logs a warning and returns the fallback value instead of propagating.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Only catches Qdrant-related exceptions (QdrantException, AggregateException with RpcException inner). Other exceptions propagate.
 *   Stateless — no retry logic (per ADR-005: silent degradation, not retry storms).
 * [RATIONALE]: Per ADR-005, Qdrant failures must be silent at the tool level. The tool applies the policy so it never sees an exception.
 * [CHANGES]: LAST_CHANGE: M10 creation.
 *   [CHANGES]: M10 @debug counter=1 (mem-038) — all 8 fallback log sites migrated from LddMarkers.UnhandledException (="[IMP:CRITICAL][GlobalExceptionMiddleware][FATAL]") → LddMarkers.QdrantWarn (="[IMP:WARN]") per AC-9. These are DELIBERATELY-HANDLED ADR-005 silent-degradation paths (8 LogWarning calls across the generic + non-generic ExecuteWithFallbackAsync), NOT middleware-FATAL conditions; the CRITICAL marker was a category error that misrepresented a handled fallback as an unhandled fatal exception.
 * </remarks>
 */
// BUG_FIX_CONTEXT: [HYPOTHESIS: QdrantResiliencePolicy emitted LddMarkers.UnhandledException (= "[IMP:CRITICAL][GlobalExceptionMiddleware][FATAL]") inside 8 LogWarning fallback sites — a CRITICAL/middleware-FATAL marker for a deliberately-handled Qdrant-down WARN path (ADR-005 silent degradation). Marker polarity mismatch: handled-fallback logs masquerading as unhandled-fatal. Observed as AC-9 QA BLOCK; tests stayed GREEN because AllTools_EmitLddMarkers was a static-const enumeration (27 hardcoded names, QdrantWarn never referenced) — GREEN-TEST-TRAP.]
// BUG_FIX_CONTEXT: [Why the old approach failed: reusing UnhandledException across the policy+middleware collapsed two distinct LDD categories (handled WARN vs unhandled FATAL), breaking the single-source-of-truth semantic invariant. The test did not reference QdrantWarn, so the const omission was invisible.]
// BUG_FIX_CONTEXT: [Why this fix: add LddMarkers.QdrantWarn="[IMP:WARN]" (#region Critical); swap all 8 policy fallback sites UnhandledException → QdrantWarn; extend AllTools_EmitLddMarkers to assert QdrantWarn exists. Marker-text-only — zero behavioral change, all 74 unit tests stay green.]
#endregion MODULE_CONTRACT

namespace McpMemoryService.Resilience;

using McpMemoryService.Logging;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Grpc.Core;
using System.Net;

/// <summary>
/// [PURPOSE]: Resilience policy for Qdrant operations — wraps calls with fallback on infrastructure failure.
/// </summary>
public sealed class QdrantResiliencePolicy
{
    #region Fields

    private readonly ILogger<QdrantResiliencePolicy> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="QdrantResiliencePolicy"/>.
    /// </summary>
    /// <param name="logger">Logger for resilience diagnostics.</param>
    public QdrantResiliencePolicy(ILogger<QdrantResiliencePolicy> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructors

    #region ExecuteWithFallbackAsync

    /// <summary>
    /// [PURPOSE]: Executes a Qdrant operation with fallback — catches Qdrant-related exceptions and returns the fallback value.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The Qdrant operation to execute.</param>
    /// <param name="fallbackValue">The fallback value to return on failure.</param>
    /// <param name="operationName">Name of the operation (for logging).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the operation, or the fallback value on failure.</returns>
    /// <remarks>
    /// Catches <see cref="QdrantException"/> and <see cref="AggregateException"/> (with <see cref="RpcException"/> inner).
    /// Does NOT catch <see cref="OperationCanceledException"/> / <see cref="TaskCanceledException"/> — cancellation propagates.
    /// </remarks>
    public async Task<T> ExecuteWithFallbackAsync<T>(
        Func<Task<T>> action,
        T fallbackValue,
        string operationName,
        CancellationToken ct = default)
    {
        try
        {
            return await action();
        }
        // BUG_FIX_CONTEXT: [HYPOTHESIS: TaskCanceledException derives from OperationCanceledException — separate catch (TaskCanceledException) is unreachable, CS0160.]
        // BUG_FIX_CONTEXT: [Why old approach failed: C# compiler rejects unreachable catch clauses (CS0160). OperationCanceledException already covers both cancellation types.]
        catch (OperationCanceledException)
        {
            // Cancellation propagates unchanged — never swallowed by the fallback path.
            throw;
        }
        catch (QdrantException ex)
        {
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} Qdrant exception in {operationName}: {ex.Message}");
            return fallbackValue;
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} gRPC error in {operationName} ({ex.StatusCode}): {ex.Message}");
            return fallbackValue;
        }
        catch (AggregateException ex) when (ex.InnerException is RpcException)
        {
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} gRPC error in {operationName}: {ex.InnerException.Message}");
            return fallbackValue;
        }
        catch (Exception ex)
        {
            // Unexpected exception type — log but still return fallback (defensive)
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} Unexpected error in {operationName}: {ex.GetType().Name} — {ex.Message}");
            return fallbackValue;
        }
    }

    /// <summary>
    /// [PURPOSE]: Executes a void Qdrant operation with fallback (no return value).
    /// </summary>
    /// <param name="action">The Qdrant operation to execute.</param>
    /// <param name="operationName">Name of the operation (for logging).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the operation succeeded; <c>false</c> on failure.</returns>
    public async Task<bool> ExecuteWithFallbackAsync(
        Func<Task> action,
        string operationName,
        CancellationToken ct = default)
    {
        try
        {
            await action();
            return true;
        }
        // BUG_FIX_CONTEXT: [HYPOTHESIS: TaskCanceledException derives from OperationCanceledException — separate catch (TaskCanceledException) is unreachable, CS0160.]
        // BUG_FIX_CONTEXT: [Why old approach failed: C# compiler rejects unreachable catch clauses (CS0160). OperationCanceledException already covers both cancellation types.]
        catch (OperationCanceledException)
        {
            // Cancellation propagates unchanged — never swallowed by the fallback path.
            throw;
        }
        catch (QdrantException ex)
        {
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} Qdrant exception in {operationName}: {ex.Message}");
            return false;
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} gRPC error in {operationName} ({ex.StatusCode}): {ex.Message}");
            return false;
        }
        catch (AggregateException ex) when (ex.InnerException is RpcException)
        {
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} gRPC error in {operationName}: {ex.InnerException.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"{LddMarkers.QdrantWarn} Unexpected error in {operationName}: {ex.GetType().Name} — {ex.Message}");
            return false;
        }
    }

    #endregion ExecuteWithFallbackAsync
}
