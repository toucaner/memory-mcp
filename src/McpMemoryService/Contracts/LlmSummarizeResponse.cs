#region MODULE_CONTRACT [DOMAIN(Contract): LLM summarizer response; CONCEPT(LlmSummarizeResponse): llama.cpp /completion response payload; TECH(SPEC §4.4, M6)]
/**
 * [GREP_SUMMARY]: LlmSummarizeResponse, llama.cpp, completion, content, tokens_evaluated, snake_case
 * [STRUCTURE]: LlmSummarizeResponse → {Content, TokensEvaluated?} ← POST /completion response (snake_case via cached JsonOptions in service)
 *
 * <summary>
 * [PURPOSE]: JSON response body from the llama.cpp /completion endpoint.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Content required; TokensEvaluated optional (nullable — llama.cpp may omit it on some response paths).
 *   Deserialized snake_case via a cached static JsonSerializerOptions in LlmSummarizerService (serializer-level naming, NOT attribute-level).
 * [RATIONALE]: Single source of truth for naming policy (serializer-level), consistent with the request side — no attribute/transport
 *   duplication. Removing [JsonPropertyName] in favor of the shared JsonOptions makes naming drift between request/response impossible.
 * [CHANGES]: LAST_CHANGE: M6 — initial creation (this @debug fix counter=1 adds MODULE_CONTRACT + XML docs AND removes
 *   [JsonPropertyName]/using System.Text.Json.Serialization in favor of the shared cached JsonOptions per DevelopmentPlan.md §2 step 4 + Notes #3).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Contracts;

/// <summary>
/// [PURPOSE]: JSON response body from the llama.cpp /completion endpoint.
/// </summary>
public sealed record LlmSummarizeResponse
{
    /// <summary>[PURPOSE]: The generated summary text.</summary>
    public required string Content { get; init; }

    /// <summary>[PURPOSE]: Number of prompt tokens evaluated by the server, if reported.</summary>
    public int? TokensEvaluated { get; init; }
}