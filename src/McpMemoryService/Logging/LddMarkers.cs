#region MODULE_CONTRACT [DOMAIN(Logging): LDD telemetry markers; CONCEPT(LddMarkers): constants for [IMP:N] markers per tool/service; TECH(LDD): structured logging constants]
/**
 * [GREP_SUMMARY]: LddMarkers, IMP, telemetry, markers, constants, Capture, Retrieve, Compact, Embedding, Qdrant, Critical
 * [STRUCTURE]: LddMarkers sealed static → Capture(x4) + Retrieve(x4) + Compact(x7) + Embedding(x5) + Qdrant(x5) + Critical(x3)
 *
 * <summary>
 * [PURPOSE]: Centralized constants for all [IMP:N] LDD (Logging-Driven Diagnostics) markers used across tools and services.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Constants are immutable. Each marker encodes the [IMP:N] tag + a semantic label for grep-based verification.
 * [RATIONALE]: Prevents drift between inline string literals and the LDD contract; single-source-of-truth per marker.
* [CHANGES]: LAST_CHANGE: M10 creation.
   * [CHANGES]: M10 @debug counter=0 — added EmbeddingServiceInitialized + EmbeddingProduced (IMP:9 SUCCESS milestones, ctor + EmbedAsync) to enable OnnxEmbeddingService full inline-string migration.
 *   [CHANGES]: M10 @debug counter=1 (mem-038) — added QdrantWarn ("[IMP:WARN]") const in #region Critical per AC-9; QdrantResiliencePolicy's 8 fallback log sites migrated from UnhandledException → QdrantWarn to fix the CRITICAL-vs-WARN marker polarity mismatch (deliberately-handled ADR-005 fallback must not emit a middleware-FATAL marker).
 *   [CHANGES]: M13 — added EmbeddingOrtKnobs (IMP:M13 effective-knobs ctor marker). Rationale: the [IMP:M13] log in OnnxEmbeddingService.ctor is a RUNTIME ILogger emission, so the M10 single-source invariant requires a constant.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Logging;

/// <summary>
/// [PURPOSE]: Centralized LDD (Logging-Driven Diagnostics) telemetry markers.
/// </summary>
/// <remarks>
/// Each constant encodes the [IMP:N] tag and is consumed by ILogger calls across tools and services.
/// </remarks>
public static class LddMarkers
{
    #region Capture

    /// <summary>Marker for capture tool entry (IMP:1).</summary>
    public const string CaptureEntry = "[IMP:1][CaptureAsync][PROGRESS]";

    /// <summary>Marker for embedding generated during capture (IMP:2).</summary>
    public const string CaptureEmbeddingGenerated = "[IMP:2][CaptureAsync][PROGRESS] embedding generated";

    /// <summary>Marker for upsert completion during capture (IMP:3).</summary>
    public const string CaptureUpserted = "[IMP:3][CaptureAsync][SUCCESS] capture succeeded";

    /// <summary>Marker for capture fallback (IMP:4).</summary>
    public const string CaptureFallback = "[IMP:4][CaptureAsync][FATAL] capture fallback";

    #endregion Capture

    #region Retrieve

    /// <summary>Marker for retrieve tool entry (IMP:1).</summary>
    public const string RetrieveEntry = "[IMP:1][RetrieveAsync][PROGRESS]";

    /// <summary>Marker for embedding generated in retrieve (IMP:2).</summary>
    public const string RetrieveEmbedding = "[IMP:2][RetrieveAsync][PROGRESS] embedding generated";

    /// <summary>Marker for search completion in retrieve (IMP:3).</summary>
    public const string RetrieveSearchComplete = "[IMP:3][RetrieveAsync][SUCCESS] search returned";

    /// <summary>Marker for retrieve fallback (IMP:4).</summary>
    public const string RetrieveFallbackEmpty = "[IMP:4][RetrieveAsync][FATAL] silent-fallback";

    #endregion Retrieve

    #region Compact

    /// <summary>Marker for compact batch fetched (IMP:1).</summary>
    public const string CompactBatchFetched = "[IMP:1][ExecuteAsync][PROGRESS] batch fetched";

    /// <summary>Marker for LLM call in compact (IMP:2).</summary>
    public const string CompactLlmCall = "[IMP:2][ExecuteAsync][SUCCESS] LLM summarization complete";

    /// <summary>Marker for summary entry captured (IMP:3).</summary>
    public const string CompactSummaryCaptured = "[IMP:3][ExecuteAsync][SUCCESS] summary captured";

    /// <summary>Marker for source deletion in compact (IMP:4).</summary>
    public const string CompactSourceDeleted = "[IMP:4][ExecuteAsync][SUCCESS] source entries deleted";

    /// <summary>Marker for LLM timeout in compact (IMP:5).</summary>
    public const string CompactLlmTimeout = "[IMP:5][ExecuteAsync][FATAL] LLM timeout";

    /// <summary>Marker for LLM 5xx in compact (IMP:6).</summary>
    public const string CompactLlm5xx = "[IMP:6][ExecuteAsync][FATAL] LLM HTTP error";

    /// <summary>Marker for skipped compact (IMP:7).</summary>
    public const string CompactSkipped = "[IMP:7][ExecuteAsync][PROGRESS] compact skipped insufficient_data";

    #endregion Compact

    #region Embedding

    /// <summary>Marker for embedding service entry (IMP:1).</summary>
    public const string EmbeddingEntry = "[IMP:1][EmbedAsync][INIT]";

    /// <summary>Marker for tokenization (IMP:2).</summary>
    public const string EmbeddingTokenized = "[IMP:2][EmbedAsync][PROGRESS]";

    /// <summary>Marker for inference (IMP:3).</summary>
    public const string EmbeddingInference = "[IMP:3][EmbedAsync][MILESTONE]";

    /// <summary>Marker for mean pooling (IMP:4).</summary>
    public const string EmbeddingMeanPool = "[IMP:4][EmbedAsync][CHECKPOINT]";

    /// <summary>Marker for L2 normalization (IMP:5).</summary>
    public const string EmbeddingL2Norm = "[IMP:5][EmbedAsync][CHECKPOINT]";

    // BUG_FIX_CONTEXT: [HYPOTHESIS: OnnxEmbeddingService used inline [IMP:9] strings for ctor-success and EmbedAsync-success with no matching LddMarkers constant; the M10 single-source-of-truth invariant (LddRemarks Rationale line 11) requires all [IMP:N] markers to be constants.]
    // BUG_FIX_CONTEXT: [Why this fix: add EmbeddingServiceInitialized + EmbeddingProduced as the IMP:9 SUCCESS milestones documented in OnnxEmbeddingService [LDD] header, so the service can reference LddMarkers.X without inline literals.]
    /// <summary>Marker for ONNX embedding service initialized milestone (IMP:9, ctor).</summary>
    public const string EmbeddingServiceInitialized = "[IMP:9][OnnxEmbeddingService.ctor][SUCCESS]";

    /// <summary>Marker for embedding successfully produced (IMP:9, EmbedAsync).</summary>
    public const string EmbeddingProduced = "[IMP:9][EmbedAsync][SUCCESS]";

    /// <summary>Marker for effective ORT memory knobs after session creation (IMP:M13, ctor).</summary>
    public const string EmbeddingOrtKnobs = "[IMP:M13][OnnxEmbeddingService.ctor][SUCCESS] ORT memory knobs";

    #endregion Embedding

    #region Qdrant

    /// <summary>Marker for collection ready (IMP:1).</summary>
    public const string QdrantCollectionReady = "[IMP:1][EnsureCollectionExistsAsync][SUCCESS]";

    /// <summary>Marker for upsert completion (IMP:2).</summary>
    public const string QdrantUpserted = "[IMP:2][UpsertAsync][SUCCESS]";

    /// <summary>Marker for search completion (IMP:3).</summary>
    public const string QdrantSearched = "[IMP:3][SearchAsync][SUCCESS]";

    /// <summary>Marker for deletion completion (IMP:4).</summary>
    public const string QdrantDeleted = "[IMP:4][DeleteAsync][SUCCESS]";

    /// <summary>Marker for batch fetched for compact (IMP:5).</summary>
    public const string QdrantBatchFetched = "[IMP:5][GetBatchForCompactAsync][SUCCESS]";

    #endregion Qdrant

    #region Critical

    /// <summary>Marker for unhandled exceptions (CRITICAL).</summary>
    public const string UnhandledException = "[IMP:CRITICAL][GlobalExceptionMiddleware][FATAL]";

    /// <summary>Marker for fatal ONNX errors (FATAL).</summary>
    public const string OnnxFatal = "[IMP:FATAL][EmbedAsync]";

    // BUG_FIX_CONTEXT: [HYPOTHESIS: QdrantResiliencePolicy fallback logs (8 call sites, deliberately-handled ADR-005 silent-degradation WARN path) reused LddMarkers.UnhandledException (= "[IMP:CRITICAL][GlobalExceptionMiddleware][FATAL]") — a middleware-FATAL polarity marker inside a non-fatal Qdrant-down WARN context. AC-9 of the M10 plan requires an additive `QdrantWarn = "[IMP:WARN]"` const in #region Critical. Category mismatch + const missing = QA BLOCK (GREEN-TEST-TRAP: AllTools_EmitLddMarkers enumerated 27 consts but never referenced QdrantWarn, so the omission was silent).]
    // BUG_FIX_CONTEXT: [Why this fix: add the dedicated QdrantWarn const so QdrantResiliencePolicy can emit the correct WARN-polarity marker; swap all 8 fallback call sites from UnhandledException → QdrantWarn; extend AllTools_EmitLddMarkers to assert the const exists. No behavioral change — marker text only; all 74 unit tests stay green.]
    /// <summary>Marker for Qdrant-down fallback warnings (WARN — ADR-005 silent degradation).</summary>
    public const string QdrantWarn = "[IMP:WARN]";

    #endregion Critical
}
