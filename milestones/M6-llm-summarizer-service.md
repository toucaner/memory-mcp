# M6 — LlmSummarizerService (HttpClient + 60s timeout + prompt)

## Dependencies
- M2 (solution skeleton, LlmSummarizerOptions, IHttpClientFactory registration)

## Goal
Implement `LlmSummarizerService` (Singleton) that sends a summarization prompt to the llama.cpp HTTP endpoint via `IHttpClientFactory` ("LlamaCpp" client). Handles 60s timeout via `TaskCanceledException`. Used by `memory_compact` tool (M9).

## Deliverables
- `src/McpMemoryService/Services/ILlmSummarizerService.cs`
- `src/McpMemoryService/Services/LlmSummarizerService.cs`
- `src/McpMemoryService/Contracts/LlmSummarizeRequest.cs`
- `src/McpMemoryService/Contracts/LlmSummarizeResponse.cs`
- Registration in `Program.cs` (configure "LlamaCpp" HttpClient with timeout + base URL)
- `tests/McpMemoryService.Tests/Services/LlmSummarizerServiceTests.cs`

## Contracts

### ILlmSummarizerService.cs

```csharp
namespace McpMemoryService.Services;

/// <summary>
/// Service for LLM-based summarization via llama.cpp HTTP endpoint.
/// </summary>
public interface ILlmSummarizerService
{
    /// <summary>
    /// Summarize a list of memory entries into a single dense summary.
    /// </summary>
    /// <param name="contents">List of memory entry contents to summarize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summarized text.</returns>
    /// <exception cref="TaskCanceledException">Request timed out (60s per SPEC §4.4).</exception>
    /// <exception cref="HttpRequestException">HTTP 5xx or network error.</exception>
    Task<string> SummarizeAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default);
}
```

### LlmSummarizeRequest.cs

```csharp
namespace McpMemoryService.Contracts;

public sealed record LlmSummarizeRequest
{
    public required string Prompt { get; init; }
    public int MaxTokens { get; init; } = 512;
    public double Temperature { get; init; } = 0.3;
    public bool Stream { get; init; } = false;
}
```

### LlmSummarizeResponse.cs

```csharp
namespace McpMemoryService.Contracts;

public sealed record LlmSummarizeResponse
{
    public required string Content { get; init; }
    public int? TokensEvaluated { get; init; }
}
```

### LlmSummarizerService.cs — implementation notes

```csharp
namespace McpMemoryService.Services;

public sealed class LlmSummarizerService : ILlmSummarizerService
{
    # region Fields
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmSummarizerOptions _options;
    private readonly ILogger<LlmSummarizerService> _logger;
    private const string ClientName = "LlamaCpp";
    # endregion

    # region Constants
    private const string PromptTemplate = "Compress the following technical development logs into a single dense summary. Preserve the essence of problems and solutions:\n{0}";
    # endregion

    # region Constructor
    // Inject IHttpClientFactory, IOptions<LlmSummarizerOptions>, ILogger.
    # endregion

    # region SummarizeAsync
    public async Task<string> SummarizeAsync(IReadOnlyList<string> contents, CancellationToken ct = default)
    {
        // 1. Validate: contents not null/empty
        // 2. Build prompt: string.Format(PromptTemplate, string.Join("\n---\n", contents))
        // 3. Create LlmSummarizeRequest
        // 4. Get HttpClient from factory (named "LlamaCpp")
        // 5. POST to "/completion" (llama.cpp default endpoint)
        //    - Serialize request as JSON
        //    - Content-Type: application/json
        // 6. Ensure success: response.EnsureSuccessStatusCode()
        //    - On 5xx → throw HttpRequestException (caller handles)
        // 7. Deserialize response → LlmSummarizeResponse
        // 8. Log [IMP:1] request sent, [IMP:2] response received (tokens)
        // 9. Return response.Content
        //
        // NOTE: TaskCanceledException propagates on timeout (HttpClient configured with 60s timeout).
        // Caller (M9 compact) must catch and handle — do NOT swallow here.
    }
    # endregion
}
```

### Program.cs — HttpClient registration

```csharp
services.AddHttpClient("LlamaCpp", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<LlmSummarizerOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);  // 60s per SPEC §4.4
});
services.AddSingleton<ILlmSummarizerService, LlmSummarizerService>();
```

## Algorithm / Logic

### Step 1: Implement contracts
1. Create `LlmSummarizeRequest` and `LlmSummarizeResponse` records.
2. Verify llama.cpp `/completion` endpoint format via web_search (see Context).

### Step 2: Implement LlmSummarizerService
1. Implement `SummarizeAsync` per the algorithm above.
2. Use `IHttpClientFactory.CreateClient("LlamaCpp")`.
3. Configure JSON serialization with snake_case (llama.cpp convention).
4. Logging with `[IMP:1]` (request sent, content count) and `[IMP:2]` (response received, tokens).
5. Do NOT catch `TaskCanceledException` — let it propagate to caller (M9).

### Step 3: Configure HttpClient in Program.cs
1. Register "LlamaCpp" named client with base URL and timeout from `LlmSummarizerOptions`.
2. Register `LlmSummarizerService` as Singleton.

### Step 4: Unit tests
Tests use `HttpMessageHandler` mock to simulate llama.cpp responses — no real LLM required.

## Tests (unit, inline)

### tests/.../Services/LlmSummarizerServiceTests.cs
```csharp
public class LlmSummarizerServiceTests
{
    [Fact]
    public async Task SummarizeAsync_ReturnsContent_OnSuccess()
    {
        // Arrange: HttpMessageHandler mock returning 200 with {"content":"summary text"}
        // Act: SummarizeAsync(["log1", "log2"])
        // Assert: result == "summary text"
    }

    [Fact]
    public async Task SummarizeAsync_BuildsPromptWithAllContents()
    {
        // Arrange: handler mock capturing request body
        // Act: SummarizeAsync(["entry A", "entry B"])
        // Assert: request prompt contains "entry A" AND "entry B"
    }

    [Fact]
    public async Task SummarizeAsync_ThrowsOn5xx()
    {
        // Arrange: handler mock returning 500
        // Act + Assert: throws HttpRequestException
    }

    [Fact]
    public async Task SummarizeAsync_ThrowsTaskCanceled_OnTimeout()
    {
        // Arrange: handler mock with delay > timeout
        // Act + Assert: throws TaskCanceledException
    }

    [Fact]
    public async Task SummarizeAsync_EmptyContents_ThrowsArgumentException()
    {
        // Act + Assert: throws on empty list
    }

    [Fact]
    public async Task SummarizeAsync_NullContents_ThrowsArgumentNullException()
    {
        // Act + Assert
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — all unit tests PASS (no real LLM required)
- [ ] `SummarizeAsync` sends POST to llama.cpp `/completion` endpoint
- [ ] Prompt includes all input contents joined by separator
- [ ] 60s timeout configured on HttpClient (from LlmSummarizerOptions.TimeoutSeconds)
- [ ] `TaskCanceledException` propagates to caller (NOT swallowed)
- [ ] 5xx responses throw `HttpRequestException`
- [ ] HttpClient registered as named client "LlamaCpp" via IHttpClientFactory
- [ ] Logs contain `[IMP:1]` and `[IMP:2]` markers
- [ ] Empty/null contents throw appropriate exceptions

## Context for @code
- Read: `SPEC.md` §4.4 (compact logic, step 4: POST to llama.cpp, 60s timeout), §5.3 (LlmSummarizerService spec)
- Read: `milestones/M1-foundation-spec-corrections.md` (S8: non-blocking compact — LlmSummarizerService itself throws, caller handles non-blocking)
- Skills: `csharp-conventions` (#region, XML docs, IHttpClientFactory, Singleton DI)
- Previous artifacts: M2 (LlmSummarizerOptions, HttpClient registration skeleton)
- Web search: verify llama.cpp server `/completion` endpoint request/response format (JSON schema). Query: `"llama.cpp server completion endpoint JSON API"`. If endpoint differs, adapt `LlmSummarizeRequest`/`Response`.
- External prerequisite: none for unit tests (mocked); real llama.cpp at <ip>:<port> for manual E2E (M12)
