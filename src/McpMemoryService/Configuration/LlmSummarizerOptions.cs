#region MODULE_CONTRACT [DOMAIN(Options): LLM summarizer; CONCEPT(POCO): sealed + init setters; TECH(HttpClient): REST endpoint]
/**
 * [GREP_SUMMARY]: LlmSummarizerOptions BaseUrl TimeoutSeconds LLM REST endpoint summarization
 * [STRUCTURE]: LlmSummarizer → HttpClient → POST /api/summarize → Timeout → Response
 *
 * <summary>
 * [PURPOSE]: Binds LLM summarizer endpoint configuration from appsettings.json.
 * </summary>
 * <remarks>
 * [INVARIANTS]: BaseUrl may be empty in local/dev environments (silent-fallback ADR-005).
 * [RATIONALE]: TimeoutSeconds protects against slow LLM responses during compaction.
 * [CHANGES]: LAST_CHANGE: M2 skeleton creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Configuration;

/// <summary>
/// [PURPOSE]: LLM summarizer HTTP endpoint configuration.
/// </summary>
/// <remarks>
/// [INVARIANTS]: TimeoutSeconds > 0 (validated in M5 startup).
/// [RATIONALE]: Separate endpoint from Qdrant to allow independent scaling.
/// </remarks>
public sealed class LlmSummarizerOptions
{
    /// <summary>Gets the base URL of the LLM summarizer service.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Gets the HTTP timeout in seconds. Defaults to 60.</summary>
    public int TimeoutSeconds { get; init; } = 60;
}
