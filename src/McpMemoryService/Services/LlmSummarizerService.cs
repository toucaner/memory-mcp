#region MODULE_CONTRACT [DOMAIN(Services): LLM summarization; CONCEPT(LlmSummarizerService): HttpClient POST to llama.cpp /completion; TECH(SPEC §4.4, M6)]
/**
 * [GREP_SUMMARY]: LlmSummarizerService, HttpClient, llama.cpp, SummarizeAsync, 60s timeout, TaskCanceledException, HttpRequestException, ADR-005
 * [STRUCTURE]: LlmSummarizerService → ctor(IHttpClientFactory+ILogger) → SummarizeAsync(POST /completion) → LlmSummarizeResponse → Content
 *
 * <summary>
 * [PURPOSE]: Sends memory entries to a llama.cpp server for summarization via HTTP.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Throws TaskCanceledException (60s timeout) and HttpRequestException (5xx) — no try/catch swallowing.
 *   Caller (M9 compact) applies ADR-005 silent-fallback; GlobalExceptionMiddleware (M10) catches at pipeline level.
 * [RATIONALE]: Service THROWS per ADR-005 — it is the caller's contract to handle failures, not this service's.
 *   Swallowing exceptions would defeat ADR-003 (M9 must NOT hard-delete sources on LLM failure).
* [CHANGES]: LAST_CHANGE: M6 — initial creation.
     * [CHANGES]: M6 @debug counter=1 — added cached `private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };`;
     *   Serialize(request, JsonOptions) + Deserialize<LlmSummarizeResponse>(responseBody, JsonOptions) (+ `using System.Text.Json;`). Request now emits
     *   snake_case keys (prompt/max_tokens/temperature/stream); response reads snake_case (content/tokens_evaluated) via the same options, replacing the
     *   removed [JsonPropertyName] attributes in LlmSummarizeResponse. See BUG_FIX_CONTEXT scar in #region SummarizeAsync.
     * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Services;

using McpMemoryService.Contracts;
using McpMemoryService.Logging;
using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// [PURPOSE]: Sends memory entries to a llama.cpp server for summarization via HTTP.
/// </summary>
public sealed class LlmSummarizerService : ILlmSummarizerService
{
    #region Constants

    private const string ClientName = "LlamaCpp";
    private const string CompletionEndpoint = "/completion";
    private const string PromptTemplate = "Compress the following technical development logs into a single dense summary. Preserve the essence of problems and solutions:\n{0}";

    // BUG_FIX_CONTEXT: [HYPOTHESIS: LlmSummarizeRequest serializes with default JsonSerializerOptions, producing PascalCase keys
    //   ("Prompt","MaxTokens","Temperature","Stream") which llama.cpp rejects (it expects snake_case "prompt/max_tokens/temperature/stream").
    //   The plan (DevelopmentPlan.md §2 step 4 + Notes #3) and AppGraph node annotation mandated snake_case via a shared cached
    //   JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }; @code emitted it with default options (qa_report
    //   Note 2, non-blocking). The response side used [JsonPropertyName] attributes (qa_report Note 1) — a deviation from the single-source-of-truth plan.]
    // [EVIDENCE: src/McpMemoryService/Services/LlmSummarizerService.cs L80 calls System.Text.Json.JsonSerializer.Serialize(request) with no options;
    //   LlmSummarizeResponse carried [JsonPropertyName("content")]/[JsonPropertyName("tokens_evaluated")]. M6 unit tests pass only because the
    //   mock HttpMessageHandler ignores the request shape and returns canned canned snake_case responses the attribute-mapped deserializer reads correctly.]
    // [RATIONALE: Introduce ONE cached `private static readonly JsonSerializerOptions JsonOptions` with SnakeCaseLower and pass it on BOTH the
    //   serialize (request) and deserialize (response) paths. Remove [JsonPropertyName] attributes from LlmSummarizeResponse so the shared JsonOptions
    //   is the single source of truth for naming policy → makes request/response naming drift impossible (immunization).]
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    #endregion Constants

    #region Fields

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmSummarizerService> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// [PURPOSE]: Creates a new LlmSummarizerService backed by a named HttpClient and logger.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating the "LlamaCpp" named HttpClient.</param>
    /// <param name="logger">Logger for LDD telemetry.</param>
    public LlmSummarizerService(IHttpClientFactory httpClientFactory, ILogger<LlmSummarizerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    #endregion Constructors

    #region SummarizeAsync

    /// <summary>
    /// [PURPOSE]: Summarizes a list of memory entry contents into a single dense summary via llama.cpp.
    /// </summary>
    /// <param name="contents">List of memory entry contents to summarize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summarized text.</returns>
    /// <exception cref="TaskCanceledException">Request timed out (60s per SPEC §4.4).</exception>
    /// <exception cref="HttpRequestException">HTTP 5xx or network error.</exception>
    public async Task<string> SummarizeAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Count == 0)
            throw new ArgumentException("contents must not be empty", nameof(contents));

        var prompt = string.Format(PromptTemplate, string.Join("\n---\n", contents));
        var request = new LlmSummarizeRequest { Prompt = prompt };

        var client = _httpClientFactory.CreateClient(ClientName);

        // BUG_FIX_CONTEXT: [scar] Both request serialize and response deserialize now pass the shared cached `JsonOptions`
        //   { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }. With [JsonPropertyName] removed from LlmSummarizeResponse,
        //   naming policy lives in exactly one place (this static field) → request keys (prompt/max_tokens/temperature/stream) and
        //   response keys (content/tokens_evaluated) cannot drift apart. Old approach: default-options Serialize (PascalCase, rejected
        //   by real llama.cpp) + attribute-mapped Deserialize (per-property override, single-side). Why chosen: serializer-level single
        //   source of truth matches DevelopmentPlan.md §2 step 4 + Notes #3 + M3 MemoryCaptureInput.cs MODULE_CONTRACT precedent
        //   (csharp-conventions §Semantic Exoskeleton). Surfaced by operator post-commit review (qa verdict SUCCESS w/ non-blocking Note 2).
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogInformation("{Marker} Sending LLM summarization request: contents={Count}, promptBytes={Len}", LddMarkers.RetrieveEntry, contents.Count, prompt.Length);

        using var response = await client.PostAsync(CompletionEndpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<LlmSummarizeResponse>(responseBody, JsonOptions) ?? throw new InvalidOperationException("Empty LLM response");

        _logger.LogInformation("{Marker} LLM response received: tokens={Tokens}, contentBytes={Len}", LddMarkers.RetrieveSearchComplete, result.TokensEvaluated, result.Content.Length);

        return result.Content;
    }

    #endregion SummarizeAsync
}
