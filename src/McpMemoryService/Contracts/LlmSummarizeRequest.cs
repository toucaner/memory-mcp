#region MODULE_CONTRACT [DOMAIN(Contract): LLM summarizer request; CONCEPT(LlmSummarizeRequest): llama.cpp /completion request payload; TECH(SPEC §4.4, M6)]
/**
 * [GREP_SUMMARY]: LlmSummarizeRequest, llama.cpp, completion, prompt, max_tokens, temperature, stream, snake_case
 * [STRUCTURE]: LlmSummarizeRequest → {Prompt, MaxTokens, Temperature, Stream} → POST /completion (snake_case via cached JsonOptions in service)
 *
 * <summary>
 * [PURPOSE]: JSON request body for the llama.cpp /completion endpoint.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Prompt required; MaxTokens>0 (default 512); Temperature default 0.3; Stream default false.
 *   Serialized snake_case via a cached static JsonSerializerOptions in LlmSummarizerService (serializer-level naming, NOT attribute-level).
 * [RATIONALE]: llama.cpp server expects snake_case JSON keys (prompt/max_tokens/temperature/stream). Transport naming is the
 *   serializer's responsibility (DevelopmentPlan.md §2 step 4 + Notes #3), so the record carries NO [JsonPropertyName] attributes —
 *   it stays the verbatim M6 spec contract. Differs from M3's SnakeCaseEnumConverter (enum-value-level); here it is property-level.
 * [CHANGES]: LAST_CHANGE: M6 — initial creation (this @debug fix counter=1 adds MODULE_CONTRACT + XML docs preserving the no-attribute contract).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

/// <summary>
/// [PURPOSE]: JSON request body for the llama.cpp /completion endpoint.
/// </summary>
public sealed record LlmSummarizeRequest
{
    /// <summary>[PURPOSE]: The summarization prompt assembled from memory contents joined by a separator.</summary>
    public required string Prompt { get; init; }

    /// <summary>[PURPOSE]: Maximum tokens to generate. Defaults to 512 (llama.cpp default).</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>[PURPOSE]: Sampling temperature. Defaults to 0.3 for deterministic summarization.</summary>
    public double Temperature { get; init; } = 0.3;

    /// <summary>[PURPOSE]: Whether to stream the response. Defaults to false (single response object).</summary>
    public bool Stream { get; init; } = false;
}