# DevelopmentPlan — McpMemoryService

| Field | Value |
|---|---|
| Project | McpMemoryService — stateless .NET 10 MCP-server (Qdrant + ONNX + LLM summarization) |
| Current Milestone | **M12 — Integration / E2E tests (FINAL milestone of Etap 1; comprehensive integration tests verifying the complete memory pipeline: capture → retrieve → stats → compact round-trip with real Qdrant testcontainer, fallback scenarios, hard-delete verification after compact, and filter correctness). Milestones M1..M11a are DONE and committed; the 74/74 unit test suite (M10 baseline) is preserved. .test_counter.json = `{"counter":0}` (fresh milestone anti-loop reset).** |
| Status | PLAN_READY — **DECOMPOSED into 5 atomic units** (awaiting sequential `@code` dispatch per-unit, ordered by dependency) |
| Previous State | M11a — **SUCCESS** (committed; the 4-file ApiKey pass-through coda + LLM URL alignment; backward-compatible `string? ApiKey = null` contract; `QdrantClient(apiKey:)` named-param pass-through; `Qdrant__ApiKey=` empty-default env-var bridge; `LlmSummarizer.BaseUrl` aligned to operator's new llama.cpp host; `.env` gitignore hygiene; AC-1..AC-10 all passed; all prior scars preserved). M12 is the FINAL Etap-1 milestone — the only remaining work is integration/E2E test coverage. The full implementation (M1..M11a) is DONE: 4 MCP tools (capture/stats/retrieve/compact), 3 services (ONNX embedding + Qdrant + LLM summarization), cross-cutting resilience (GlobalExceptionMiddleware + QdrantResiliencePolicy + EmbeddingResiliencePolicy), Docker deployment (Dockerfile + docker-compose.yml), and the ApiKey pass-through. All acceptance criteria depend on testcontainer-managed Qdrant (NOT a manually started Docker container — unlike M5 QdrantServiceIntegrationTests).
| Target Framework | `net10.0` (.NET 10) — per ADR-006 / SPEC.md §2 |
| Milestone Deps | **M5 (DONE — `QdrantOptions.cs` + `QdrantService.cs` ctor L66 `new QdrantClient(host, port: _options.GrpcPort)` + `QdrantCollectionInitializer` IHostedService + `QdrantServiceTests` integration ctor L241 — the integration test ctor stays `new QdrantClient("localhost", port: 6334)` unchanged: the `apiKey:` named parameter defaults to `null`, so the M5 integration tests against an unauthenticated Docker `qdrant/qdrant` stay green). M11 (DONE — `Dockerfile` + `docker-compose.yml` + `.dockerignore` + `@qa SUCCESS 0ee493d` with the operator-action-required note that motivates this mini-milestone).** M11a is a **CONFIG-PASS-THROUGH coda** — 4-file additive change (1 nullable Options property + 1 single-line Service ctor named-parameter appendage + 1 appsettings.json line + 1 docker-compose env-var line) + optional `.gitignore` `.env` hygiene line + `.test_counter.json` reset-confirmation. NO new unit tests; NO decomposition. Backward-compatible by construction (`ApiKey` defaults to `null`; `QdrantClient` ctor `apiKey:` defaults to `null`; unauth Qdrant deployments continue to work unchanged). |
| Dispatch Recommendation | **5 sequential `@code` dispatches, one per unit (see `## Decomposition` below).** Unit 1 (TestFixture+csproj) MUST complete first — all other units depend on the shared infrastructure. Units 2-5 can be dispatched in any order after Unit 1, but the recommended order is: Unit 2 (E2E round-trip, most critical path), Unit 3 (Qdrant filters), Unit 4 (Compact transaction), Unit 5 (Fallback). Each unit produces a single `.cs` file with 3-5 test methods → well below the >5-methods-per-session threshold. After all 5 units are done, `@qa` runs the full integration gate (`dotnet test --filter "Category=Integration"`) on the complete M12 test suite. |
| Etap | Etap 1 (M1..M12) — **DONE**. Etap 2 (migrate agent pipeline to MCP) — **DONE**: `rules.md`, `agent/memory.md`, `skills/memory-protocol` migrated to dual-mode (files + MCP). `mcp-memory` activated in `opencode.json`. Project in maintenance/support phase. |

## ADRs Touched by M6

| ADR | Decision | M6 Action |
|---|---|---|
| **ADR-003** | Compact semantics = hard delete (source entries physically deleted after LLM summarization) | M6 provides `LlmSummarizerService.SummarizeAsync` — the LLM call that M9 compact invokes BEFORE the hard-delete (`IQdrantService.DeleteAsync`). If `SummarizeAsync` throws (`TaskCanceledException`/`HttpRequestException`), the M9 caller does NOT delete sources. The service itself THROWS (does not swallow) so M9 can detect failure and skip deletion. |
| **ADR-005** | Silent fallback on Qdrant/LLM unavailability; LLM down (compact) → `status=error`, no source deletion | **CRITICAL:** Silent-fallback applies at the **M9 caller level**, NOT inside `LlmSummarizerService`. The service THROWS (`TaskCanceledException` on 60s timeout, `HttpRequestException` on 5xx/network). M9 compact tool catches these and returns `{"status":"error","reason":"llm_timeout"|...}`. M10 `GlobalExceptionMiddleware` is the second safety net. **Do NOT regress to the M5-style "do not swallow": M6 service must NOT catch/swallow `TaskCanceledException` or `HttpRequestException`** — they propagate. Document this contract in the interface XML docs (already in spec §Contracts). |
| **ADR-006** | .NET 10 target | csproj already `net10.0`. `System.Text.Json` `JsonNamingPolicy.SnakeCaseLower` exists since .NET 8 → net10.0-confirmed. No new package refs. |
| **ADR-010** | MCP SDK = ModelContextProtocol 1.4.0 | No SDK interaction in M6 (LlmSummarizerService is MCP-agnostic — it's called by the M9 tool, not by the MCP transport). The M6 DI line `services.AddSingleton<ILlmSummarizerService, LlmSummarizerService>()` + the swapped `AddHttpClient("LlamaCpp", (sp, client) => {...})` are orthogonal to MCP. |
| **ADR-008** | `LlmSummarizerOptions` (BaseUrl, TimeoutSeconds=60) | M2 deliverable ALREADY EXISTS. M6 CONSUMES it via `IOptions<LlmSummarizerOptions>` in the Program.cs `AddHttpClient` lambda (options-driven BaseAddress + Timeout) — replacing the M2 hardcoded `<url>`. No change to the Options class itself. |

> **M6 invariants (must NOT regress):**
> 1. **The service THROWS, it does NOT swallow.** `TaskCanceledException` (timeout) and `HttpRequestException` (5xx via `EnsureSuccessStatusCode()`) propagate uncaught. This mirrors the M5 invariant ("QdrantService is a thin wrapper — throws on failure; M10 catches"). ADR-005 silent-fallback is the **caller's** (M9/M10) responsibility.
> 2. **HttpClient timeout = 60s** is enforced by `HttpClient.Timeout` (set in the `AddHttpClient` lambda from `options.TimeoutSeconds`), NOT by a manual `CancellationTokenSource` in the service. `HttpClient.Timeout` raises `TaskCanceledException` when exceeded — this is the SPEC §4.4 step 4 + §7 contract.
> 3. **Named client "LlamaCpp"** — reuse, do NOT rename. M2 already registered this name; M6 only swaps the lambda body.
> 4. **Program.cs scars are INTACT.** The M2 BUG_FIX_CONTEXT blocks (McpServerOptions, WithListToolsHandler, MapMcp) and the M4/M5 DI lines are NOT touched. The M6 edit is a strict **lambda-body swap** + **one additive DI line**.

---

## PURPOSE

Implement the LLM summarization tier of McpMemoryService: an `ILlmSummarizerService` contract and a `sealed` `LlmSummarizerService` (Singleton) that POSTs a summarization prompt to the llama.cpp HTTP `/completion` endpoint via `IHttpClientFactory` (named client "LlamaCpp", 60s timeout from `LlmSummarizerOptions.TimeoutSeconds`). Produces `LlmSummarizeRequest` and `LlmSummarizeResponse` records (PascalCase C# ↔ snake_case JSON transport). Validates `contents` (null/empty → `ArgumentNullException`/`ArgumentException`). Joins contents with `"\n---\n"` into the prompt template, serializes with `System.Text.Json` (`PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower`), POSTs, `EnsureSuccessStatusCode()`, deserializes, returns `response.Content`. `TaskCanceledException` and `HttpRequestException` propagate uncaught to the M9 caller (ADR-005 silent-fallback is the caller's contract, NOT the service's). Also rewire `Program.cs`: replace the M2 hardcoded `AddHttpClient("LlamaCpp", client => { BaseAddress = "<url>"; ... })` placeholder with an options-driven lambda `(sp, client) => { var options = sp.GetRequiredService<IOptions<LlmSummarizerOptions>>().Value; client.BaseAddress = new Uri(options.BaseUrl); client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds); }` and register `services.AddSingleton<ILlmSummarizerService, LlmSummarizerService>();`. Includes 6 unit tests via an `HttpMessageHandler` mock (no real LLM required) covering success, prompt-build validation, 5xx, timeout, empty, null.

---

## 1. Draft Code Graph

> M6 adds the third real *service* (after OnnxEmbeddingService, QdrantService). `LlmSummarizerService` is a logic class — `csharp-conventions` `#region` structuring applies (Fields / Constants / Constructors / SummarizeAsync). Two small record contracts join the M3 `Contracts/` folder. The Program.cs edit is a placeholder-lambda-swap + 1 additive DI line. `LlmSummarizerOptions` (M2) is consumed, not modified.

```xml
<DraftCodeGraph>
  <!-- ========== INTERFACE ========== -->
  <src_McpMemoryService_Services_ILlmSummarizerService_cs FILE="src/McpMemoryService/Services/ILlmSummarizerService.cs" TYPE="SERVICE_INTERFACE">
    <keywords>llm, summarizer, llama.cpp, HttpClient, 60s timeout, TaskCanceledException, HttpRequestException, CancellationToken, ILlmSummarizerService DI</keywords>
    <annotation>public interface ILlmSummarizerService with a single method SummarizeAsync. Verbatim contract from M6 spec §Contracts lines 19-39. Consumed (DI-injected) by M9 compact tool. Flat interface, no #region. XML docs on the method incl &lt;exception cref="TaskCanceledException"/&gt; (60s timeout, SPEC §4.4) + &lt;exception cref="HttpRequestException"/&gt; (5xx/network). The exception docs encode the "service THROWS, caller (M9) catches per ADR-005" contract.</annotation>
    <src_McpMemoryService_Services_ILlmSummarizerService_SummarizeAsync_METHOD NAME="SummarizeAsync" TYPE="INTERFACE_METHOD" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_LlmSummarizerService_cs" TYPE="IMPLEMENTED_BY" />
      <Link TARGET="M9_Compact_Tool_PLANNED" TYPE="INJECTED_INTO" />
    </CrossLinks>
  </src_McpMemoryService_Services_ILlmSummarizerService_cs>

  <!-- ========== REQUEST CONTRACT ========== -->
  <src_McpMemoryService_Contracts_LlmSummarizeRequest_cs FILE="src/McpMemoryService/Contracts/LlmSummarizeRequest.cs" TYPE="CONTRACT_RECORD">
    <keywords>llama.cpp /completion request, PascalCase C#, snake_case JSON transport, required Prompt, MaxTokens 512, Temperature 0.3, Stream false</keywords>
    <annotation>public sealed record LlmSummarizeRequest. Verbatim from M6 spec §Contracts lines 41-53. Properties (PascalCase, init): required string Prompt; int MaxTokens=512; double Temperature=0.3; bool Stream=false. Serialized to snake_case JSON (prompt, max_tokens, temperature, stream) via a cached JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower } in the service. NO [JsonPropertyName] attributes — transport naming is serializer-level, not attribute-level (differs from M3 SnakeCaseEnumConverter which is enum-value-level).</annotation>
    <src_McpMemoryService_Contracts_LlmSummarizeRequest_Prompt_PROPERTY NAME="Prompt" TYPE="PROPERTY" REQUIRED="true" />
    <src_McpMemoryService_Contracts_LlmSummarizeRequest_MaxTokens_PROPERTY NAME="MaxTokens" TYPE="PROPERTY" DEFAULT="512" />
    <src_McpMemoryService_Contracts_LlmSummarizeRequest_Temperature_PROPERTY NAME="Temperature" TYPE="PROPERTY" DEFAULT="0.3" />
    <src_McpMemoryService_Contracts_LlmSummarizeRequest_Stream_PROPERTY NAME="Stream" TYPE="PROPERTY" DEFAULT="false" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_LlmSummarizerService_cs" TYPE="SERIALIZED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Contracts_LlmSummarizeRequest_cs>

  <!-- ========== RESPONSE CONTRACT ========== -->
  <src_McpMemoryService_Contracts_LlmSummarizeResponse_cs FILE="src/McpMemoryService/Contracts/LlmSummarizeResponse.cs" TYPE="CONTRACT_RECORD">
    <keywords>llama.cpp /completion response, PascalCase C#, snake_case JSON transport, required Content, nullable TokensEvaluated</keywords>
    <annotation>public sealed record LlmSummarizeResponse. Verbatim from M6 spec §Contracts lines 55-65. Properties (PascalCase, init): required string Content; int? TokensEvaluated. Deserialized from snake_case JSON (content, tokens_evaluated) via the same cached JsonSerializerOptions. TokensEvaluated nullable because llama.cpp may omit it on some response paths [UNVERIFIED_VERSION] — exact llama.cpp response JSON fields not web-verified (web_search S3-unavailable); adapted from M6 spec contracts.</annotation>
    <src_McpMemoryService_Contracts_LlmSummarizeResponse_Content_PROPERTY NAME="Content" TYPE="PROPERTY" REQUIRED="true" />
    <src_McpMemoryService_Contracts_LlmSummarizeResponse_TokensEvaluated_PROPERTY NAME="TokensEvaluated" TYPE="PROPERTY" NULLABLE="true" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_LlmSummarizerService_cs" TYPE="DESERIALIZED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Contracts_LlmSummarizeResponse_cs>

  <!-- ========== SERVICE ========== -->
  <src_McpMemoryService_Services_LlmSummarizerService_cs FILE="src/McpMemoryService/Services/LlmSummarizerService.cs" TYPE="SERVICE">
    <keywords>LlmSummarizerService, Singleton, IHttpClientFactory, LlamaCpp named client, LlmSummarizerOptions, llama.cpp /completion POST, 60s timeout, TaskCanceledException, HttpRequestException, EnsureSuccessStatusCode, System.Text.Json snake_case, ADR-005, ILogger, IMP:1/IMP:2, do-not-swallow</keywords>
    <annotation>public sealed class LlmSummarizerService : ILlmSummarizerService. MODULE_CONTRACT header per csharp-conventions (matches IEmbeddingService + QdrantService style). #region Fields: IHttpClientFactory _httpClientFactory (readonly), LlmSummarizerOptions _options (readonly — bound at factory-lambda time via IOptions&lt;LlmSummarizerOptions&gt;), ILogger&lt;LlmSummarizerService&gt; _logger (readonly). #region Constants: private const string ClientName = "LlamaCpp"; private const string PromptTemplate = "Compress the following technical development logs into a single dense summary. Preserve the essence of problems and solutions:\n{0}"; private const string CompletionEndpoint = "/completion"; private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }. #region Constructors: ctor(IHttpClientFactory, IOptions&lt;LlmSummarizerOptions&gt;, ILogger) — assign fields; _options = options.Value. #region SummarizeAsync: the single async method. LDD markers [IMP:1] request sent (content count, prompt length) + [IMP:2] response received (tokens, content length). NO try/catch — TaskCanceledException (timeout) and HttpRequestException (5xx/EnsureSuccessStatusCode) propagate to M9. ADR-005 silent-fallback is the caller's contract.</annotation>
    <src_McpMemoryService_Services_LlmSummarizerService_httpClientFactory_FIELD NAME="_httpClientFactory" TYPE="FIELD" />
    <src_McpMemoryService_Services_LlmSummarizerService_options_FIELD NAME="_options" TYPE="FIELD" />
    <src_McpMemoryService_Services_LlmSummarizerService_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Services_LlmSummarizerService_ClientName_CONST NAME="ClientName" TYPE="PRIVATE_CONST" VALUE="LlamaCpp" />
    <src_McpMemoryService_Services_LlmSummarizerService_PromptTemplate_CONST NAME="PromptTemplate" TYPE="PRIVATE_CONST" />
    <src_McpMemoryService_Services_LlmSummarizerService_CompletionEndpoint_CONST NAME="CompletionEndpoint" TYPE="PRIVATE_CONST" VALUE="/completion" />
    <src_McpMemoryService_Services_LlmSummarizerService_SerializerOptions_FIELD NAME="SerializerOptions" TYPE="STATIC_READONLY_FIELD" />
    <src_McpMemoryService_Services_LlmSummarizerService_SummarizeAsync_METHOD NAME="SummarizeAsync" TYPE="PUBLIC_ASYNC_METHOD" IMP="IMP:1,IMP:2">
      <annotation>1. Validate contents: null → ArgumentNullException; empty → ArgumentException. 2. Build prompt = string.Format(PromptTemplate, string.Join("\n---\n", contents)). 3. Create LlmSummarizeRequest { Prompt = prompt } (MaxTokens/Temperature/Stream defaults). 4. Get HttpClient via _httpClientFactory.CreateClient(ClientName). 5. [IMP:1] log content count + prompt byte length. 6. Serialize request → JSON (snake_case) → StringContent(application/json). 7. POST to CompletionEndpoint ("/completion"). 8. response.EnsureSuccessStatusCode() — 5xx → HttpRequestException (propagates, M9 catches). 9. Deserialize response → LlmSummarizeResponse (snake_case). 10. [IMP:2] log tokens + content length. 11. return response.Content. NOTE: TaskCanceledException on 60s timeout (HttpClient.Timeout) propagates uncaught.</annotation>
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Services_ILlmSummarizerService_cs" TYPE="IMPLEMENTS" />
        <Link TARGET="src_McpMemoryService_Contracts_LlmSummarizeRequest_cs" TYPE="SERIALIZES" />
        <Link TARGET="src_McpMemoryService_Contracts_LlmSummarizeResponse_cs" TYPE="DESERIALIZES" />
      </CrossLinks>
    </src_McpMemoryService_Services_LlmSummarizerService_SummarizeAsync_METHOD>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_ILlmSummarizerService_cs" TYPE="IMPLEMENTS_INTERFACE" />
      <Link TARGET="src_McpMemoryService_Configuration_LlmSummarizerOptions_cs" TYPE="INJECTS_OPTIONS" />
      <Link TARGET="src_McpMemoryService_csproj" TYPE="PACKAGE_REFS" />
      <Link TARGET="LlmSummarizerService_PLANNED_M6" TYPE="RESOLVES" />
    </CrossLinks>
  </src_McpMemoryService_Services_LlmSummarizerService_cs>

  <!-- ========== PROGRAM.CS EDIT (placeholder-lambda swap + additive DI line) ========== -->
  <src_McpMemoryService_Program_cs_ConfigureServices_M6_EDIT FILE="src/McpMemoryService/Program.cs" TYPE="DI_EDIT">
    <annotation>REPLACE the M2 AddHttpClient("LlamaCpp", client =&gt; { client.BaseAddress = new Uri("<url>"); client.Timeout = TimeSpan.FromSeconds(configuration.GetValue&lt;int&gt;("LlmSummarizer:TimeoutSeconds", 60)); }); placeholder (Program.cs L48-52) with options-driven lambda:
  services.AddHttpClient("LlamaCpp", (sp, client) =&gt; { var options = sp.GetRequiredService&lt;IOptions&lt;LlmSummarizerOptions&gt;&gt;().Value; client.BaseAddress = new Uri(options.BaseUrl); client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds); });
  // [IMP:M6][ConfigureServices][OPTION] Register LLM summarizer service (Singleton) — ADR-005 (service throws, caller M9 catches), 60s timeout (SPEC §4.4 step 4)
  services.AddSingleton&lt;ILlmSummarizerService, LlmSummarizerService&gt;();
STRICT ADDITIVE/EXCHANGE-WITHIN-PLACEHOLDER: keep the named-client registration line, swap the lambda body, add the comment + the AddSingleton line right after. Do NOT touch the M2 BUG_FIX_CONTEXT scars (McpServerOptions, WithListToolsHandler, MapMcp), the M4 IEmbeddingService line, or the M5 IQdrantService/QdrantCollectionInitializer lines. Preserve the [IMP:9] SUCCESS marker. `using McpMemoryService.Services;` already present from M4. The IOptions&lt;LlmSummarizerOptions&gt; binding (services.Configure&lt;LlmSummarizerOptions&gt;(...)) lives earlier in ConfigureServices from M2 — DO NOT re-add.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Services_ILlmSummarizerService_cs" TYPE="REGISTERS" />
      <Link TARGET="src_McpMemoryService_Services_LlmSummarizerService_cs" TYPE="REGISTERS" />
      <Link TARGET="src_McpMemoryService_Configuration_LlmSummarizerOptions_cs" TYPE="CONFIGURES_HTTPCLIENT" />
    </CrossLinks>
  </src_McpMemoryService_Program_cs_ConfigureServices_M6_EDIT>

  <!-- ========== TESTS ========== -->
  <tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_cs FILE="tests/McpMemoryService.Tests/Services/LlmSummarizerServiceTests.cs" TYPE="XUNIT_TEST">
    <keywords>LlmSummarizerService, HttpMessageHandler mock, IHttpClientFactory mock, no real LLM, unit, snake_case JSON, prompt-build, 5xx, timeout, empty, null, IMP markers</keywords>
    <annotation>6 unit tests, UNCATEGORISED (no [Trait], no Category=Integration — no real LLM/Docker required, mocked HttpMessageHandler). Build LlmSummarizerService with Mock&lt;IHttpClientFactory&gt; returning new HttpClient(capturingHandler) { BaseAddress = new Uri("http://test/") }. Use IOptions&lt;LlmSummarizerOptions&gt; = Options.Create(new LlmSummarizerOptions { BaseUrl = "http://test/", TimeoutSeconds = 60 }) + NullLogger&lt;LlmSummarizerService&gt;.Instance. A private FakeHttpMessageHandler : HttpMessageHandler subclass captures the request (Uri, method, body) and returns a canned HttpResponseMessage. Tests: 1) SummarizeAsync_ReturnsContent_OnSuccess (handler returns 200 {"content":"summary text","tokens_evaluated":5} → result == "summary text"). 2) SummarizeAsync_BuildsPromptWithAllContents (capture request body → JSON contains all input strings joined, MaxTokens/Temperature/Stream present as snake_case). 3) SummarizeAsync_ThrowsOn5xx (handler returns 500 → Assert.ThrowsAsync&lt;HttpRequestException&gt;). 4) SummarizeAsync_ThrowsTaskCanceled_OnTimeout — set HttpClient.Timeout = TimeSpan.FromMilliseconds(50) on the test client + handler Task.Delay(200) → Assert.ThrowsAsync&lt;TaskCanceledException&gt;(or OperationCanceledException which TaskCanceledException derives from). 5) SummarizeAsync_EmptyContents_ThrowsArgumentException (Assert.ThrowsAsync&lt;ArgumentException&gt;). 6) SummarizeAsync_NullContents_ThrowsArgumentNullException (Assert.ThrowsAsync&lt;ArgumentNullException&gt;). No Moq strict-mock of IHttpClientFactory required if a minimal TestHttpClientFactory stub is cleaner — @code picks the pattern that builds cleanest on net10.0.</annotation>
    <tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_SummarizeAsync_ReturnsContent_OnSuccess_METHOD NAME="SummarizeAsync_ReturnsContent_OnSuccess" TYPE="TEST_METHOD" IMP="IMP:1,IMP:2" />
    <tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_SummarizeAsync_BuildsPromptWithAllContents_METHOD NAME="SummarizeAsync_BuildsPromptWithAllContents" TYPE="TEST_METHOD" IMP="IMP:1" />
    <tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_SummarizeAsync_ThrowsOn5xx_METHOD NAME="SummarizeAsync_ThrowsOn5xx" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_SummarizeAsync_ThrowsTaskCanceled_OnTimeout_METHOD NAME="SummarizeAsync_ThrowsTaskCanceled_OnTimeout" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_SummarizeAsync_EmptyContents_ThrowsArgumentException_METHOD NAME="SummarizeAsync_EmptyContents_ThrowsArgumentException" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_SummarizeAsync_NullContents_ThrowsArgumentNullException_METHOD NAME="SummarizeAsync_NullContents_ThrowsArgumentNullException" TYPE="TEST_METHOD" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_LlmSummarizerService_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Services_ILlmSummarizerService_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Contracts_LlmSummarizeRequest_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Contracts_LlmSummarizeResponse_cs" TYPE="EXERCISES" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Services_LlmSummarizerServiceTests_cs>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow

> `@code` execution algorithm for `scope=impl:M6`. Source: M6 spec §Algorithm (lines 124-143) + §Contracts + SPEC §4.4 step 4 (POST llama.cpp, 60s timeout) + §5.3 (LlmSummarizerService spec) + §7 (LLM error → handle TaskCanceledException, no source deletion) + M1 correction S8 (non-blocking compact — service itself throws, caller handles) + ADR-003/005/006/008. One `@code` dispatch, no decomposition.

1. **Implement `Contracts/LlmSummarizeRequest.cs`** verbatim per M6 spec §Contracts lines 41-53:
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
   No `[JsonPropertyName]` attributes — transport naming is **serializer-level** (`JsonNamingPolicy.SnakeCaseLower` in the service), not attribute-level. This differs from M3's `SnakeCaseEnumConverter` (which is enum-value-level). Brief MODULE_CONTRACT region header per `csharp-conventions` (matches the M3 Contracts style — short header, the records are self-documenting).

2. **Implement `Contracts/LlmSummarizeResponse.cs`** verbatim per M6 spec §Contracts lines 55-65:
   ```csharp
   namespace McpMemoryService.Contracts;

   public sealed record LlmSummarizeResponse
   {
       public required string Content { get; init; }
       public int? TokensEvaluated { get; init; }
   }
   ```
   `TokensEvaluated` is nullable because the llama.cpp `/completion` response schema's optional fields may be omitted on some response paths `[UNVERIFIED_VERSION]` — the exact response JSON fields were not web-verified (web_search returned S3-unavailable; see §Notes #8). The M6 spec §Contracts declares it nullable, so nullable it is. The snake_case serializer maps `content` ↔ `Content` and `tokens_evaluated` ↔ `TokensEvaluated` (default `null` if the JSON field is absent — System.Text.Json honors nullable int? correctly).

3. **Implement `Services/ILlmSummarizerService.cs`** verbatim per M6 spec §Contracts lines 19-39: `namespace McpMemoryService.Services;` `public interface ILlmSummarizerService` with a single method `Task<string> SummarizeAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default);`. XML docs on the method incl `<exception cref="TaskCanceledException">Request timed out (60s per SPEC §4.4).</exception>` + `<exception cref="HttpRequestException">HTTP 5xx or network error.</exception>`. MODULE_CONTRACT region header per `csharp-conventions` (matches `IEmbeddingService`/`IQdrantService` style). Flat interface, no `#region`. No extra usings needed (only `using System.Collections.Generic;` is implicit via `IReadOnlyList<>` — covered by `ImplicitUsings`).

4. **Implement `Services/LlmSummarizerService.cs`** per §1 node + SPEC §5.3 + §4.4 step 4, with `#region` structuring:
   - **#region Fields** — `private readonly IHttpClientFactory _httpClientFactory;` `private readonly LlmSummarizerOptions _options;` `private readonly ILogger<LlmSummarizerService> _logger;`. Usings: `System.Net.Http.Json` (for `JsonContent` or manual `JsonSerializer`), `System.Text.Json`, `System.Net.Http.Headers` (for `MediaTypeHeaderValue` if needed), `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Options`, `McpMemoryService.Configuration`, `McpMemoryService.Contracts`.
   - **#region Constants** — `private const string ClientName = "LlamaCpp";` `private const string PromptTemplate = "Compress the following technical development logs into a single dense summary. Preserve the essence of problems and solutions:\n{0}";` `private const string CompletionEndpoint = "/completion";` `private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };`
   - **#region Constructors** — `public LlmSummarizerService(IHttpClientFactory httpClientFactory, IOptions<LlmSummarizerOptions> options, ILogger<LlmSummarizerService> logger)`. Assign `_httpClientFactory = httpClientFactory; _options = options.Value; _logger = logger;`. No I/O in the ctor — the timer/timeout is enforced by the named `HttpClient` (configured at `AddHttpClient` time in `Program.cs`).
   - **#region SummarizeAsync** — the single async method, per M6 spec §Algorithm lines 90-108:
     ```csharp
     /// <inheritdoc />
     public async Task<string> SummarizeAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default)
     {
         // 1. Validate
         ArgumentNullException.ThrowIfNull(contents);
         if (contents.Count == 0)
         {
             throw new ArgumentException("Contents list must not be empty.", nameof(contents));
         }

         // 2. Build prompt
         var joined = string.Join("\n---\n", contents);
         var prompt = string.Format(PromptTemplate, joined);

         // 3. Build request
         var request = new LlmSummarizeRequest { Prompt = prompt };

         // 4. Get the named HttpClient ("LlamaCpp" — configured with BaseAddress + 60s Timeout in Program.cs)
         var client = _httpClientFactory.CreateClient(ClientName);

         // 5. [IMP:1] request sent — log content count + prompt length
         _logger.LogInformation("[IMP:1][SummarizeAsync][PROGRESS] Sending LLM summarization request: contents={Count}, promptBytes={Len}",
             contents.Count, prompt.Length);

         // 6. Serialize (snake_case) + POST to /completion
         using var httpContent = JsonContent.Create(request, options: SerializerOptions);
         using var response = await client.PostAsync(CompletionEndpoint, httpContent, cancellationToken);

         // 7. Ensure success — 5xx → HttpRequestException (propagates, M9 catches)
         response.EnsureSuccessStatusCode();

         // 8. Deserialize (snake_case) → LlmSummarizeResponse
         var summary = await response.Content.ReadFromJsonAsync<LlmSummarizeResponse>(SerializerOptions, cancellationToken)
             ?? throw new HttpRequestException("LLM returned an empty response body.");

         // 9. [IMP:2] response received — log tokens + content length
         _logger.LogInformation("[IMP:2][SummarizeAsync][SUCCESS] LLM response received: tokens={Tokens}, contentBytes={Len}",
             summary.TokensEvaluated, summary.Content.Length);

         // 10. Return the summarized content
         return summary.Content;

         // NOTE: TaskCanceledException (60s HttpClient.Timeout) and HttpRequestException (5xx/EnsureSuccessStatusCode)
         // propagate UNCAUGHT to the M9 caller. ADR-005 silent-fallback is the caller's contract, NOT this service's.
         // Do NOT add try/catch swallowing these — see M5 invariant (QdrantService pattern).
     }
     ```
   - **#region MODULE_CONTRACT** — header comment matching the `IEmbeddingService`/`QdrantService` style: DOMAIN(Embedding/LLM), CONCEPT(Singleton + IHttpClientFactory), TECH(SPEC §5.3, ADR-005, M6). Add `[CHANGES]: LAST_CHANGE: M6 creation.` to remarks.

5. **Program.cs edit (placeholder-lambda swap + 1 additive DI line).** In `ConfigureServices`, REPLACE the M2 placeholder block (Program.cs L47-52):
   ```csharp
   // [IMP:4][ConfigureServices][OPTION] Register LlamaCpp HttpClient (placeholder — not called in M2)
   services.AddHttpClient("LlamaCpp", client =>
   {
       client.BaseAddress = new Uri("<url>");
       client.Timeout = TimeSpan.FromSeconds(configuration.GetValue<int>("LlmSummarizer:TimeoutSeconds", 60));
   });
   ```
   WITH the options-driven block:
   ```csharp
   // [IMP:M6][ConfigureServices][OPTION] Configure LlamaCpp HttpClient (options-driven) + register LLM summarizer (Singleton) — 60s timeout (SPEC §4.4 step 4), ADR-005 (service throws, caller M9 catches)
   services.AddHttpClient("LlamaCpp", (sp, client) =>
   {
       var options = sp.GetRequiredService<IOptions<LlmSummarizerOptions>>().Value;
       client.BaseAddress = new Uri(options.BaseUrl);
       client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
   });
   services.AddSingleton<ILlmSummarizerService, LlmSummarizerService>();
   ```
   - **KEEP**: the named-client registration ("LlamaCpp" — reuse, do NOT rename), the line ordering relative to the BUG_FIX_CONTEXT scars (M2 McpServerOptions, WithListToolsHandler), the M4 `services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();` line, the M5 `services.AddSingleton<IQdrantService, QdrantService>();` + `services.AddHostedService<QdrantCollectionInitializer>();` lines, and the `[IMP:9][ConfigureServices][SUCCESS]` marker.
   - **DO NOT touch** any BUG_FIX_CONTEXT scar blocks (M2 McpServerOptions, WithListToolsHandler, MapMcp).
   - `using McpMemoryService.Services;` is already present from M4 (covers `ILlmSummarizerService`/`LlmSummarizerService`). `using Microsoft.Extensions.Options;` already present (L23). No new usings.
   - The `services.Configure<LlmSummarizerOptions>(configuration.GetSection("LlmSummarizer"));` binding (L44) is from M2 and STAYS — do not re-add or remove it.

6. **Write `tests/McpMemoryService.Tests/Services/LlmSummarizerServiceTests.cs`** — 6 unit tests, UNCATEGORISED (no `[Trait]`, no `Category=Integration` — no real LLM/Docker required). Pattern: a private `FakeHttpMessageHandler : HttpMessageHandler` subclass captures the `HttpRequestMessage` and returns a canned `HttpResponseMessage`. Build the SUT with `Mock<IHttpClientFactory>` (Moq already in test csproj from M2) returning `new HttpClient(fakeHandler) { BaseAddress = new Uri("http://test/") }`, `Options.Create(new LlmSummarizerOptions { BaseUrl = "http://test/", TimeoutSeconds = 60 })`, and `NullLogger<LlmSummarizerService>.Instance`.
   - `SummarizeAsync_ReturnsContent_OnSuccess` — fakeHandler returns `200 OK` with `{"content":"summary text","tokens_evaluated":5}`. `await SummarizeAsync(["log1","log2"])` → `Assert.Equal("summary text", result)`.
   - `SummarizeAsync_BuildsPromptWithAllContents` — fakeHandler captures the request body. `await SummarizeAsync(["entry A","entry B"])`. Deserialize the captured body as `JsonDocument` → assert the `prompt` field contains both "entry A" and "entry B" (joined with `\n---\n`) and the prompt template prefix; assert `max_tokens` == 512, `temperature` == 0.3, `stream` == false (snake_case keys).
   - `SummarizeAsync_ThrowsOn5xx` — fakeHandler returns `500 Internal Server Error`. `await Assert.ThrowsAsync<HttpRequestException>(() => SummarizeAsync(["x"]))`.
   - `SummarizeAsync_ThrowsTaskCanceled_OnTimeout` — build a separate `HttpClient` with `Timeout = TimeSpan.FromMilliseconds(50)` (override the factory timeout for this test) + fakeHandler `await Task.Delay(200)`. `await Assert.ThrowsAsync<TaskCanceledException>(() => SummarizeAsync(["x"]))`. (TaskCanceledException derives from OperationCanceledException — `ThrowsAsync<TaskCanceledException>` is the precise assertion.)
   - `SummarizeAsync_EmptyContents_ThrowsArgumentException` — `await Assert.ThrowsAsync<ArgumentException>(() => SummarizeAsync(Array.Empty<string>()))`.
   - `SummarizeAsync_NullContents_ThrowsArgumentNullException` — `await Assert.ThrowsAsync<ArgumentNullException>(() => SummarizeAsync(null!))`.
   - `@code` picks the cleanest `IHttpClientFactory`-mock pattern that builds on net10.0 (either Moq `Mock<IHttpClientFactory>` or a minimal `TestHttpClientFactory` stub returning the test `HttpClient`). Note the timeout test must construct its own `HttpClient` with a short timeout (an `IHttpClientFactory`-mock returning a pre-built `HttpClient` with the desired `Timeout` is cleanest).

7. **Build + test gate before return:**
   - `dotnet build McpMemoryService.sln` (the `dotnet` CLI auto-discovers `.slnx` → `McpMemoryService.slnx`) → **0 Warning(s), 0 Error(s)**. AC-1.
   - `dotnet test --filter "Category!=Integration"` → unit gate: M3 (16 = 3 DTO + 13 mapping) + M5 mapping unit (5) + **M6 unit (6)** = **27 unit tests, `Failed: 0`** (M2 host smoke + M4 ONNX + M5 integration are `Category=Integration`, skipped). This verifies the unit subset stays green. AC-2.
   - Do NOT run `dotnet test` (full) — M6 has no integration tests. The full gate is @qa's responsibility (no Docker LLM required for M6 unit tests).

---

## 3. Acceptance Criteria

> Verbatim from `milestones/M6-llm-summarizer-service.md` lines 194-204, labelled for mechanical `@qa` checking.

- [ ] **AC-1:** `dotnet build` — OK.
- [ ] **AC-2:** `dotnet test` — all unit tests PASS (no real LLM required).
- [ ] **AC-3:** `SummarizeAsync` sends POST to llama.cpp `/completion` endpoint.
- [ ] **AC-4:** Prompt includes all input contents joined by separator.
- [ ] **AC-5:** 60s timeout configured on HttpClient (from `LlmSummarizerOptions.TimeoutSeconds`).
- [ ] **AC-6:** `TaskCanceledException` propagates to caller (NOT swallowed).
- [ ] **AC-7:** 5xx responses throw `HttpRequestException`.
- [ ] **AC-8:** HttpClient registered as named client "LlamaCpp" via `IHttpClientFactory`.
- [ ] **AC-9:** Logs contain `[IMP:1]` and `[IMP:2]` markers.
- [ ] **AC-10:** Empty/null contents throw appropriate exceptions.

---

## Notes for @code

1. **The service THROWS — do NOT swallow exceptions (M5-style invariant, ADR-005 caller-level).** `LlmSummarizerService.SummarizeAsync` has NO try/catch around the `PostAsync`/`EnsureSuccessStatusCode`/`ReadFromJsonAsync` chain. `TaskCanceledException` (60s `HttpClient.Timeout`) and `HttpRequestException` (5xx via `EnsureSuccessStatusCode`) propagate uncaught to the M9 compact caller. ADR-005 silent-fallback is the **caller's** (M9 tool + M10 `GlobalExceptionMiddleware`) contract, NOT this service's. This mirrors the M5 `QdrantService` pattern ("thin wrapper — throws on failure; M10 catches"). A regression here (swallowing to return an empty string) would defeat ADR-003 (hard-delete precedence — M9 must NOT delete sources on LLM failure). The interface XML docs encode the contract: `<exception cref="TaskCanceledException">` + `<exception cref="HttpRequestException">`.

2. **HttpClient timeout = 60s is on the client, not the service.** The 60s timeout (SPEC §4.4 step 4, ADR-008) is enforced by `HttpClient.Timeout`, set in the `AddHttpClient("LlamaCpp", (sp, client) => { ... client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds); })` lambda in `Program.cs`. When exceeded, `HttpClient` raises `TaskCanceledException` (which derives from `OperationCanceledException`). Do NOT use a manual `CancellationTokenSource` in the service — `HttpClient.Timeout` is the canonical mechanism and is already wired by the named-client configuration. The injected `cancellationToken` param is a separate cancel path (M9 tool cancellation) and is passed through to `PostAsync`. AC-5 verifies the timeout comes from `LlmSummarizerOptions.TimeoutSeconds` (default 60).

3. **Snake_case JSON transport is serializer-level, NOT attribute-level (differs from M3).** M3 introduced `SnakeCaseEnumConverter` (a sealed subclass of `JsonStringEnumConverter`) for **enum-value** snake_case, applied via `[JsonConverter(typeof(SnakeCaseEnumConverter))]` on `EntryType`/`AgentRole`. For M6 contracts (`LlmSummarizeRequest`/`LlmSummarizeResponse` — records with primitive properties), the snake_case mapping is **record-property-level** (PascalCase C# `Prompt` ↔ snake_case JSON `prompt`), handled by a cached `private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };` instance in the service. NO `[JsonPropertyName]` attributes on the records — they stay as the verbatim M6 spec contracts. `JsonNamingPolicy.SnakeCaseLower` exists since .NET 8 → net10.0-confirmed. This is the **same approach as M3 serialization but at the serializer level** (per-property naming) vs M3's per-enum converter (per-value naming). Mem-009 insight applies.

4. **Named client "LlamaCpp" — reuse, do NOT rename.** M2 already registered `services.AddHttpClient("LlamaCpp", ...)` (Program.cs L48). M6 SWAPS the lambda body (hardcoded `<url>` → options-driven `options.BaseUrl`) and ADDS `services.AddSingleton<ILlmSummarizerService, LlmSummarizerService>();` right after. The named-client string `"LlamaCpp"` is the integration point consumed by `_httpClientFactory.CreateClient(ClientName)` — keep it. The M2 `services.Configure<LlmSummarizerOptions>(configuration.GetSection("LlmSummarizer"));` binding (L44) STAYS — the new lambda's `sp.GetRequiredService<IOptions<LlmSummarizerOptions>>().Value` resolves the bound options. Do NOT re-add the binding.

5. **Program.cs scars — leave intact.** `Program.cs` carries BUG_FIX_CONTEXT scar blocks from M2 (`McpServerOptions` post-configure, `WithListToolsHandler`, `MapMcp("/mcp")`) and the M4/M5 DI lines. The M6 edit is a **targeted placeholder-lambda swap** + **1 additive comment** + **1 additive DI line**. Do NOT modify or remove the BUG_FIX_CONTEXT scars, the M4 `IEmbeddingService` line, the M5 `IQdrantService`/`QdrantCollectionInitializer` lines, or the `[IMP:9]` SUCCESS marker. The edit is strictly LOCAL: replace the L48-52 block (5 lines) with the options-driven block (8 lines incl comment + AddSingleton). `using McpMemoryService.Services;` (L25) already covers `ILlmSummarizerService`/`LlmSummarizerService`. `using Microsoft.Extensions.Options;` (L23) already covers `IOptions<>`/`GetRequiredService`.

6. **`ArgumentNullException.ThrowIfNull` + `ArgumentException` validation.** Per M6 spec §Algorithm line 92 ("Validate: contents not null/empty") + spec test lines 181-191. Use `ArgumentNullException.ThrowIfNull(contents)` (.NET 6+ static helper) for null. For empty, `if (contents.Count == 0) throw new ArgumentException("Contents list must not be empty.", nameof(contents));`. AC-10 covers both. Do NOT use a `RequireArgument`-style lib — keep it stdlib.

7. **`JsonContent.Create` vs manual `StringContent`.** Prefer `JsonContent.Create(request, options: SerializerOptions)` (`System.Net.Http.Json`) — it sets `Content-Type: application/json; charset=utf-8` automatically and serializes via the cached options. `using var httpContent = JsonContent.Create(...)` ensures disposal. Alternatively, `var json = JsonSerializer.Serialize(request, SerializerOptions); var httpContent = new StringContent(json, Encoding.UTF8, "application/json");` — both work; `JsonContent.Create` is cleaner. `@code` picks whichever builds cleanest. The deserialize side: `await response.Content.ReadFromJsonAsync<LlmSummarizeResponse>(SerializerOptions, cancellationToken)` (`System.Net.Http.Json`).

8. **lluama.cpp `/completion` response JSON schema — [UNVERIFIED_VERSION].** The exact response JSON field names (`content`, `tokens_evaluated`) were **not web-verified** — `web_search` returned S3-unavailable (`[WEB_SEARCH_UNAVAILABLE] SearXNG service unavailable — search returned error`). The M6 spec §Contracts (lines 55-65) declares `Content` (required string) + `TokensEvaluated` (nullable int?), which the architect treats as the source of truth (spec is the authority per AGENTS.md). The snake_case serializer maps `content` ↔ `Content` and `tokens_evaluated` ↔ `TokensEvaluated`. If `@code`/`@debug` discover (during M12 manual E2E against a real llama.cpp at `<ip>:<port>`) that the actual response uses a different field name (e.g., llama.cpp `/completion` historically returns `{ "content": "...", "tokens_evaluated": N, ... }` per local knowledge but `[UNVERIFIED]` without a live check), the fix is to adjust the `LlmSummarizeResponse` record properties (or add `[JsonPropertyName]` overrides) — this is a contract adjustment, not a service-logic change. **For M6 unit tests, the mocked handler returns the spec's `{ "content": "...", "tokens_evaluated": 5 }` shape, so the unit gate is self-consistent regardless of the real endpoint.** No M6 AC depends on a live llama.cpp call.

9. **No `#pragma warning disable`.** Build warnings = AC-1 failure. Watch for: CS8602 nullable (the `ReadFromJsonAsync` `??` null-coalescing handles the nullable return), IDE0005 unused usings, CA warnings. The `IHttpClientFactory` field is `readonly` assigned in ctor — no null issues. The `LlmSummarizerOptions` field is `readonly` assigned via `options.Value` — no null. The `SerializerOptions` static field is `readonly` initialized inline — no null.

10. **Test categorisation — UNCATEGORISED (unit).** The 6 M6 tests have NO `[Trait("Category","Integration")]` — they use a mocked `HttpMessageHandler` and require NO real llama.cpp/Docker. They MUST run in the unit gate (`dotnet test --filter "Category!=Integration"`). M2 host smoke, M4 ONNX, M5 Qdrant integration remain `Category=Integration` (unchanged by M6). Expected unit total after M6: M3 (16 = 3 DTO + 13 mapping) + M5 mapping unit (5) + **M6 unit (6)** = **27 unit tests** in the unit gate, `Failed: 0`.

11. **Decomposition decision — single dispatch.** Single `@code scope=impl:M6`. 1 interface (1 method) + 1 service class (1 async method) + 2 record contracts (≤4 properties each) + 1 Program.cs edit (lambda swap + 1 DI line) + 1 test file (6 tests). All deliverables are cohesive (one service + its contracts + its registration + its tests) and sequential. Decomposition would fragment an inherently cohesive service. Below the >5-new-methods threshold. **NO `## Decomposition` section** is appended to this plan.

12. **Do NOT run `dotnet test` (full) during the `@code` dispatch** unless explicitly permitted — the unit gate (`dotnet test --filter "Category!=Integration"`) is sufficient for M6 (no M6 integration tests). @code returns with the build + unit-gate transcript.

13. **AGENTS.md build command note.** Per AGENTS.md §2: `dotnet build McpMemoryService.sln` (the `dotnet` CLI auto-discovers the `.slnx`). `dotnet test --filter "Category!=Integration"` skips M2 host smoke + M4 ONNX + M5 integration. M6 tests are UNIT only → uncategorised → run in this gate.

14. **profile.md consistency.** Plan prose is technical English (matches the M5 plan style). No Russian summary header required (existing plans use English throughout).

15. **Web search record (for @qa audit).**
    - `[SOURCE: web_search attempt, query="llama.cpp server completion endpoint JSON API request response schema", ts=2026-07-01T12:00:00Z]` → **[WEB_SEARCH_UNAVAILABLE] SearXNG service unavailable — search returned error ("Сервис поиска временно недоступен").** Per WEB_SEARCH_PROTOCOL scenario S3, proceeded without search results using local knowledge + the M6 spec §Contracts as the source of truth. The exact llama.cpp `/completion` response JSON fields are `[UNVERIFIED_VERSION]` (see Notes #8).
    - No `fetch_and_extract` call was made (no web_search URLs returned to fetch).
    - Architectural decisions (snake_case transport via `JsonNamingPolicy.SnakeCaseLower`, 60s `HttpClient.Timeout` from `LlmSummarizerOptions`, service-throws/caller-catches per ADR-005, named-client "LlamaCpp" reuse, Program.cs placeholder-lambda swap) are derived from the M6 spec + SPEC §4.4/§5.3/§7 + AGENTS.md ADR-005/008 + the M5 antecedent pattern, NOT from external web documentation.

---

## M7 — MCP tools: memory_capture + memory_get_stats

| Field | Value |
|---|---|
| Current Milestone | **M7 — MCP tools `memory_capture` + `memory_get_stats` (DI-injected tools, [McpServerToolType]+[McpServerTool]+WithToolsFromAssembly, InputValidator, snake_case transport)** |
| Status | PLAN_READY (awaiting `@code scope=impl:M7`) |
| Milestone Deps | **M3 (DONE — DTOs `MemoryCaptureInput/Output`, `MemoryGetStatsInput/Output`, `MemoryRetrieveInput`, `MemoryCompactInput`, `MemoryPayload` + enums), M4 (DONE — `IEmbeddingService.EmbedAsync`/`Dimension`, 384-dim L2-normalized), M5 (DONE — `IQdrantService.UpsertAsync`/`CountAsync`, `PayloadMappingExtensions.ToQdrantPayload`), M6 (DONE — `ILlmSummarizerService` registered in DI; not directly called by M7 but registration must remain intact).** All deps DONE per qa_report.md (M6 SUCCESS 2026-07-01). |
| Dispatch Recommendation | **Single `@code scope=impl:M7` — NO decomposition.** 2 tool classes (1 async method each) + 1 static validator (4 methods, only 2 used by M7 — retrieve/compact are forward stubs) + 1 Program.cs edit (replace `.WithListToolsHandler(...)` empty-list placeholder with `.WithToolsFromAssembly()` + add a snake_case `JsonSerializerOptions` to `McpServerOptions.SerializerOptions`) + 1 HostSmokeTests edit (tools/list now returns 2 tools → update the `toolsArray.GetArrayLength().Should().Be(0)` assertion) + 2 unit test files (6 capture + 4 get_stats = 10 tests). All deliverables are cohesive (two tools + their validator + their registration + their tests + the singular smoke-test assertion they break together). Right at the >5-methods boundary but conceptually a single unit (tool registration is atomic). BELOW decomposition threshold. **NO `## Decomposition` section** is appended. |
| Etap | Etap 1 (implement M1..M12). Etap 2 future. |

## ADRs Touched by M7

| ADR | Decision | M7 Action |
|---|---|---|
| **ADR-001** | entry_type taxonomy = 6 types; compactable = bug_fix+insight | `MemoryCaptureTool` **rejects `entry_type == Summary`** at `InputValidator.ValidateCapture` — `ArgumentException` (only compact-created summaries are allowed entry_type=summary; capture forbids it). Consistency invariant: capture=5 (decision/bug_fix/requirement/rejection/insight), get_stats=6 (incl summary). `MemoryGetStatsTool` accepts all 6 including Summary. M3 `MemoryGetStatsInput.EntryType?` already allows Summary. |
| **ADR-002** | agent_role = 5 roles incl orchestrator | Both tools pass the DTO `AgentRole` enum (PascalCase C# ↔ snake_case JSON via the snake_case serializer config). The M3 `MemoryCaptureInput.AgentRole` accepts all 5. No split. |
| **ADR-005** | Silent fallback on Qdrant unavailability; capture → `Success=false`, get_stats → `Count=-1` | **CRITICAL:** The silent-fallback lives at the **tool level** (caller-facing), NOT inside `IQdrantService`/`QdrantService` (M5 do-not-swallow invariant — the service THROWS, the tool catches). `MemoryCaptureTool.CaptureAsync` wraps the embedding + upsert call in `try { ... } catch (Exception ex) { _logger.LogError(...); return new MemoryCaptureOutput { Success=false, PointId=string.Empty, Error="qdrant_unavailable" }; }`. `MemoryGetStatsTool.GetStatsAsync` wraps `CountAsync` similarly → returns `Count = -1`. NO exception escapes the tool — the MCP connection MUST NOT break. This is the **inverse** of the M6 do-not-swallow invariant (M6 service throws; M9 caller catches). Here the **tool itself catches** because it is the MCP-facing boundary. |
| **ADR-010** | MCP SDK = ModelContextProtocol 1.4.0 | `WithToolsFromAssembly()` discovers `[McpServerToolType]`-annotated classes + `[McpServerTool]`-annotated instance methods. DI resolves constructor parameters (`IEmbeddingService`, `IQdrantService`, `ILogger<>`). DTO parameters are deserialized by the SDK from JSON-RPC `arguments`. mem-006 insight: this is the confirmed pattern. Replaces the M2 `WithListToolsHandler(...)` placeholder (which was M2's deliberate empty-list stand-in — forbidden for M2, now correct for M7). |
| **ADR-006** | .NET 10 target | net10.0 already confirmed across the project. `[McpServerToolType]`/`[McpServerTool]` from ModelContextProtocol 1.4.0 are net10-compatible (already pinned in csproj from M2). |
| **ADR-011** | ONNX 384-dim | `MemoryCaptureTool` calls `_embedding.EmbedAsync(content)` → 384-dim L2-normalized vector, passed verbatim to `_qdrant.UpsertAsync(pointId, vector, payload)`. OnnxEmbeddingService ALREADY L2-normalizes (M4 invariant) — the tool does NOT re-normalize. The M7 spec's optional `Debug.Assert(norm ≈ 1.0)` is a DEBUG-only guard (compiled out in Release); the tool trusts the contract but asserts in Debug builds to surface regressions early. |

> **M7 invariants (must NOT regress):**
> 1. **The tool CATCHES — silent fallback at the MCP boundary (ADR-005).** Unlike M5/M6 services that THROW, M7 tools catch `Exception` from `_qdrant.*` and `_embedding.*` and return failure DTO shapes (`Success=false` / `Count=-1`) without re-throwing. The MCP connection MUST NOT break. This is the **caller level** ADR-005 references — tools ARE the caller (wrt Qdrant/ONNX). M10 `GlobalExceptionMiddleware` is the second safety net.
> 2. **Tools are instance classes with ctor DI, NOT static methods.** `[McpServerToolType]` on the class + `[McpServerTool]` on instance methods + ctor-injected `IEmbeddingService`/`IQdrantService`/`ILogger<>`. The MCP SDK instantiates the tool via DI on each `tools/call`. This preserves testability (tests construct the tool with `Mock<IEmbeddingService>` + `Mock<IQdrantService>` + `NullLogger<T>.Instance` directly — no MCP transport needed).
> 3. **Program.cs scars INTACT.** The M2 BUG_FIX_CONTEXT blocks (McpServerOptions post-configure, WithListToolsHandler, MapMcp) STAY as historical breadcrumbs. M7 replaces the `.WithListToolsHandler((_,_) => ValueTask.FromResult(new ListToolsResult { Tools = new List<Tool>() }))` **line** with `.WithToolsFromAssembly()` (the M2-era explicit empty-list handler is the dedicated eviction point — its BUG_FIX_CONTEXT scar comment block is preserved and extended with an `[M7 supersede]` note). The M2 McpServerOptions post-configure `Configure<IOptions<McpOptions>>` lambda is EXTENDED within the same `AddOptions<McpServerOptions>()` registration to ALSO set `SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }` (snake_case transport for tool DTOs). The McpOptions ServerInfo post-configure is preserved verbatim. The M4/M5/M6 DI lines are NOT touched. `[IMP:9]` SUCCESS marker preserved.
> 4. **M3 DTOs are NOT modified.** No `[JsonPropertyName]` attributes are added to `MemoryCaptureInput`/`Output`, `MemoryGetStatsInput`/`Output`, `MemoryRetrieveInput`, `MemoryCompactInput`, `MemoryPayload`. Transport naming is **serializer-level** (`McpServerOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower`) — SAME discipline as M6's shared `JsonOptions` (mem-024 lesson: unified naming policy, single source of truth, no drift). The M3 `SnakeCaseEnumConverter` (enum-value-level) is unchanged and composes with the property naming policy. AGENTS.md ADR-005 §"M3 JSON enum design" (mem-009 insight) preserved.
> 5. **HostSmokeTests assertion MUST be updated.** The M2 test `McpInitialize_ReturnsServerInfo` asserts `toolsArray.GetArrayLength().Should().Be(0)` — this assertion is the ONE point M7 deliberately breaks. The assertion is updated to `toolsArray.GetArrayLength().Should().Be(2)` + tool-name containment check (`memory_capture` + `memory_get_stats`). The `Accept: application/json, text/event-stream` header setup, the SSE `ExtractDataFromSse` helper, the initialize handshake, and the BUG_FIX_CONTEXT scar blocks are preserved. The test file's existing Module header / category (`[Trait("Category","Integration")]` — M5 reclassification) is preserved. The `[IMP:1]`/`[IMP:2]`/`[IMP:3]` markers preserved; an `[IMP:M7]` marker added to the tools/list assertion.
> 6. **`point_id` = `Guid.NewGuid()` (UUID v4)** per SPEC §4.2 step 3 + IQdrantService.UpsertAsync signature. The tool generates the Guid, passes it to `UpsertAsync(Guid, float[], MemoryPayload)`, and returns `pointId.ToString()` in `MemoryCaptureOutput.PointId`. Lowercase-vpaced Guid string via `Guid.ToString()` (default format `"D"`).

---

## PURPOSE (M7)

Implement the first two MCP-facing tools of McpMemoryService: `memory_capture` (validate → embed → upsert to Qdrant with a fresh GUID v4 → return `success`+`point_id`, silent-fallback on Qdrant/ONNX failure) and `memory_get_stats` (validate → `IQdrantService.CountAsync` → return `count`, silent-fallback returning `count=-1`). Both tools are `[McpServerToolType]`+`[McpServerTool]` instance classes DI-injected with `IEmbeddingService`/`IQdrantService`/`ILogger<>` and discovered via `.WithToolsFromAssembly()`. A static `InputValidator` provides ADR-001 enforcement (capture rejects `entry_type == Summary`; project_id content non-empty). `Program.cs` is edited to register the tools via the SDK pattern, configure the MCP serializer with snake_case naming, and update the M2 placeholder. `HostSmokeTests` tools/list assertion is updated 0 → 2. 10 unit tests (6 capture + 4 get_stats) mock the services — no real Qdrant/ONNX/Docker required.

---

## 1. Draft Code Graph (M7)

> M7 adds the first MCP-facing tools (after M2's empty-list handler). Tool classes follow `csharp-conventions` `#region` structuring (Fields / Constants / Constructors / Tool-method). The `Tools/` folder is created here. Serialization is at SDK-transport level (mem-009 + mem-024 unified policy).

```xml
<DraftCodeGraph> (M7 subset)
  <!-- ========== INPUT VALIDATOR ========== -->
  <src_McpMemoryService_Validation_InputValidator_cs FILE="src/McpMemoryService/Validation/InputValidator.cs" TYPE="STATIC_HELPER">
    <keywords>validation, ArgumentException, whitespace, entry_type summary exclusion, ADR-001, capture=5, get_stats=6, retrieve limit 1..10, compact batch 1..100</keywords>
    <annotation>public static class InputValidator. MODULE_CONTRACT header per csharp-conventions. Four static methods per M7 spec §InputValidator lines 89-121 (all 4 declared now; capture + getStats are exercised by M7; retrieve + compact are FORWARD stubs M8/M9 will call — declared now to avoid touching this file later, but with NO callers in M7 production code so they cannot break the build). Methods: ValidateCapture(MemoryCaptureInput) throws ArgumentException if content null/whitespace OR project_id null/whitespace OR entry_type == Summary (ADR-001 capture-excludes-summary). ValidateGetStats(MemoryGetStatsInput) throws if project_id null/whitespace. ValidateRetrieve(MemoryRetrieveInput) throws if query null/whitespace OR project_id null/whitespace OR Limit not in [1,10] (M8 will call). ValidateCompact(MemoryCompactInput) throws if project_id null/whitespace OR BatchSize not in [1,100] (M9 will call). Use `string.IsNullOrWhiteSpace(...)` for string checks. Use `nameof(input.Content)` in ArgumentException param-name. REGION: Static methods only, no Fields/Constants (or one private const `int MinLimit=1; int MaxLimit=10; int MinBatchSize=1; int MaxBatchSize=100;` — @code picks the cleanest).</annotation>
    <src_McpMemoryService_Validation_InputValidator_ValidateCapture_METHOD NAME="ValidateCapture" TYPE="PUBLIC_STATIC_METHOD" />
    <src_McpMemoryService_Validation_InputValidator_ValidateGetStats_METHOD NAME="ValidateGetStats" TYPE="PUBLIC_STATIC_METHOD" />
    <src_McpMemoryService_Validation_InputValidator_ValidateRetrieve_METHOD NAME="ValidateRetrieve" TYPE="PUBLIC_STATIC_METHOD" />
    <src_McpMemoryService_Validation_InputValidator_ValidateCompact_METHOD NAME="ValidateCompact" TYPE="PUBLIC_STATIC_METHOD" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="CALLED_BY" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="CALLED_BY" />
      <Link TARGET="M8_Retrieve_Tool_PLANNED" TYPE="FORWARD_CALLER" />
      <Link TARGET="M9_Compact_Tool_PLANNED" TYPE="FORWARD_CALLER" />
    </CrossLinks>
  </src_McpMemoryService_Validation_InputValidator_cs>

  <!-- ========== MEMORY CAPTURE TOOL ========== -->
  <src_McpMemoryService_Tools_MemoryCaptureTool_cs FILE="src/McpMemoryService/Tools/MemoryCaptureTool.cs" TYPE="MCP_TOOL">
    <keywords>McpServerToolType, McpServerTool, memory_capture, IEmbeddingService, IQdrantService, ILogger, MemoryCaptureInput, MemoryCaptureOutput, GUID v4, silent fallback, ADR-005, ADR-011, IMP:1/IMP:2, try-catch</keywords>
    <annotation>public sealed class MemoryCaptureTool. MODULE_CONTRACT header per csharp-conventions. [McpServerToolType] attribute on the class (ModelContextProtocol 1.4.0 — mem-006 confirmed pattern). #region Fields: IEmbeddingService _embedding (readonly), IQdrantService _qdrant (readonly), ILogger&lt;MemoryCaptureTool&gt; _logger (readonly). #region Constructors: ctor(IEmbeddingService, IQdrantService, ILogger&lt;MemoryCaptureTool&gt;) — assign fields. #region Tools: the single [McpServerTool(Name="memory_capture", Description="Save a new fact/decision into memory.")] async CaptureAsync(MemoryCaptureInput input, CancellationToken ct) method. Algorithm per M7 spec §MemoryCaptureTool logic + SPEC §4.2: (1) InputValidator.ValidateCapture(input) — throws ArgumentException on bad input (validation is NOT silent — caller supplied bad input); (2) [IMP:1] log entry: projectId, entryType, agentRole, content length; (3) try { var vector = await _embedding.EmbedAsync(input.Content, ct); Debug.Assert(vector.Length == _embedding.Dimension, "embedding dimension mismatch"); var pointId = Guid.NewGuid(); var payload = new MemoryPayload { ProjectId=input.ProjectId, SessionId = input.SessionId ?? string.Empty, AgentRole = input.AgentRole, EntryType = input.EntryType, Timestamp = DateTimeOffset.UtcNow, Content = input.Content, Tags = input.Tags, Metadata = input.Metadata }; await _qdrant.UpsertAsync(pointId, vector, payload, ct); [IMP:2] log success: pointId, vector dimension; return new MemoryCaptureOutput { Success = true, PointId = pointId.ToString(), Error = null }; } catch (Exception ex) { _logger.LogError(ex, "[IMP:2][CaptureAsync][FATAL] Qdrant/ONNX failure during capture — silent-fallback (ADR-005): projectId={ProjectId}"); return new MemoryCaptureOutput { Success = false, PointId = string.Empty, Error = "capture_failed" }; } — NO rethrow. SessionId null-guard: M3 DTO SessionId is nullable; MemoryPayload.SessionId is required string → use `input.SessionId ?? string.Empty` (matches PayloadMappingExtensions.ToPayload reading "session_id" StringValue — a null would break round-trip; default empty string is safe; documented in the TOOL not by changing M3 DTO).</annotation>
    <src_McpMemoryService_Tools_MemoryCaptureTool_embedding_FIELD NAME="_embedding" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryCaptureTool_qdrant_FIELD NAME="_qdrant" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryCaptureTool_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryCaptureTool_CaptureAsync_METHOD NAME="CaptureAsync" TYPE="MCP_TOOL_METHOD" ATTR="McpServerTool" IMP="IMP:1,IMP:2">
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Services_IEmbeddingService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="CALLS" />
        <Link TARGET="src_McpMemoryService_Contracts_MemoryCaptureInput_cs" TYPE="ACCEPTS" />
        <Link TARGET="src_McpMemoryService_Contracts_MemoryCaptureOutput_cs" TYPE="RETURNS" />
        <Link TARGET="src_McpMemoryService_Models_MemoryPayload_cs" TYPE="BUILDS" />
      </CrossLinks>
    </src_McpMemoryService_Tools_MemoryCaptureTool_CaptureAsync_METHOD>
    <CrossLinks>
      <Link TARGET="M7_CaptureStats_Tools_PLANNED" TYPE="RESOLVES" />
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="REGISTERED_IN" />
    </CrossLinks>
  </src_McpMemoryService_Tools_MemoryCaptureTool_cs>

  <!-- ========== MEMORY GET STATS TOOL ========== -->
  <src_McpMemoryService_Tools_MemoryGetStatsTool_cs FILE="src/McpMemoryService/Tools/MemoryGetStatsTool.cs" TYPE="MCP_TOOL">
    <keywords>McpServerToolType, McpServerTool, memory_get_stats, IQdrantService, ILogger, MemoryGetStatsInput, MemoryGetStatsOutput, CountAsync, silent fallback, ADR-005, IMP:1/IMP:2, try-catch</keywords>
    <annotation>public sealed class MemoryGetStatsTool. MODULE_CONTRACT header. [McpServerToolType] on the class. #region Fields: IQdrantService _qdrant (readonly), ILogger&lt;MemoryGetStatsTool&gt; _logger (readonly). #region Constructors: ctor(IQdrantService, ILogger&lt;MemoryGetStatsTool&gt;). #region Tools: the single [McpServerTool(Name="memory_get_stats", Description="Get memory entry count for compact threshold check.")] async GetStatsAsync(MemoryGetStatsInput input, CancellationToken ct) method. Algorithm per M7 spec §MemoryGetStatsTool logic + SPEC §4.3: (1) InputValidator.ValidateGetStats(input) — throws ArgumentException on null/whitespace project_id; (2) [IMP:1] log entry: projectId, entryTypeFilter; (3) try { var count = await _qdrant.CountAsync(input.ProjectId, input.EntryType, ct); [IMP:2] log success: count; return new MemoryGetStatsOutput { Count = count }; } catch (Exception ex) { _logger.LogError(ex, "[IMP:2][GetStatsAsync][FATAL] Qdrant failure during get_stats — silent-fallback (ADR-005): projectId={ProjectId}"); return new MemoryGetStatsOutput { Count = -1 }; } — NO rethrow. Note: input.EntryType is `EntryType?` and IQdrantService.CountAsync accepts `EntryType?` — direct pass-through, no coercion; null means "all entry types".</annotation>
    <src_McpMemoryService_Tools_MemoryGetStatsTool_qdrant_FIELD NAME="_qdrant" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryGetStatsTool_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryGetStatsTool_GetStatsAsync_METHOD NAME="GetStatsAsync" TYPE="MCP_TOOL_METHOD" ATTR="McpServerTool" IMP="IMP:1,IMP:2">
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="CALLS" />
        <Link TARGET="src_McpMemoryService_Contracts_MemoryGetStatsInput_cs" TYPE="ACCEPTS" />
        <Link TARGET="src_McpMemoryService_Contracts_MemoryGetStatsOutput_cs" TYPE="RETURNS" />
      </CrossLinks>
    </src_McpMemoryService_Tools_MemoryGetStatsTool_GetStatsAsync_METHOD>
    <CrossLinks>
      <Link TARGET="M7_CaptureStats_Tools_PLANNED" TYPE="RESOLVES" />
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="REGISTERED_IN" />
    </CrossLinks>
  </src_McpMemoryService_Tools_MemoryGetStatsTool_cs>

  <!-- ========== PROGRAM.CS EDIT (replace WithListToolsHandler placeholder + snake_case serializer) ========== -->
  <src_McpMemoryService_Program_cs_ConfigureServices_M7_EDIT FILE="src/McpMemoryService/Program.cs" TYPE="DI_EDIT">
    <annotation>STRICTLY ADDITIVE + ONE TARGETED LINE SWAP within an existing M2 block (eviction point) + serializer-options extension:
1. SWAP the M2 placeholder `.WithListToolsHandler((_,_) => ValueTask.FromResult(new ListToolsResult { Tools = new List&lt;Tool&gt;() }))` (Program.cs L102-103) with `.WithToolsFromAssembly()` — the M2 BUG_FIX_CONTEXT scar block above it is PRESERVED (it documents why M2 did NOT use WithToolsFromAssembly); append a one-line `// [IMP:M7] Superseded in M7 by WithToolsFromAssembly() — M2 empty-list handler was the placeholder. Scar block above kept for history.` comment immediately after the swap so @qa can trace the evolution.
2. EXTEND the existing `services.AddOptions&lt;McpServerOptions&gt;().Configure&lt;IOptions&lt;McpOptions&gt;&gt;((serverOpts, mcpOpts) =&gt; { serverOpts.ServerInfo = new Implementation { Name = mcpOpts.Value.ServerName, Version = mcpOpts.Value.ServerVersion }; })` (Program.cs L74-82) to ALSO set `serverOpts.SerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };` inside the same Configure lambda — single source of truth for tool I/O snake_case (mem-024 lesson). Do NOT remove the existing ServerInfo post-configure. Verify the `McpServerOptions.SerializerOptions` property exists at compile time [UNVERIFIED_VERSION — web_search S3-unavailable on 2026-07-01]; if the SDK exposes the property under a different name (e.g., `JsonSerializerOptions` directly on options, or requires configuring via `services.Configure&lt;JsonSerializerOptions&gt;("McpServer", ...)` named-options), @code adapts to the actual 1.4.0 API and documents the chosen path in a comment. FALLBACK if no serializer-options knob exists: add `[JsonPropertyName("snake_case")]` attributes to the M3 capture/stats DTO properties only (ProjectId, Content, AgentRole, EntryType, Tags, SessionId, Metadata, PointId, Success, Error, Count) — this is the deterministic fallback, kept as a single-file additive change to M3 DTO files (the M3 plan banned attribute-level naming on LlmSummarize* contracts but those are M6-internal; for SDK-bus M7 tool DTOs a confirmed SDK gap justifies override attributes — documented in the tool file's MODULE_CONTRACT).
3. ADD `using System.Text.Json;` to the Program.cs usings block (No-op if `Microsoft.Extensions.Options` already transitively covers it — verify at compile).
4. `[IMP:M7][ConfigureServices][OPTION] Register MCP tools via WithToolsFromAssembly() + snake_case serializer (ADR-010 mem-006, mem-024 unified naming)` comment.
DO NOT touch: the M2 BUG_FIX_CONTEXT comment blocks (only the WithListToolsHandler CODE LINE is replaced — its associated scar comment above STAYS), the McpServerOptions Configure&lt;IOptions&lt;McpOptions&gt;&gt; ServerInfo block (only EXTENDED with SerializerOptions), the MapMcp("/mcp") line + its scar block, the M4 IEmbeddingService line, the M5 IQdrantService/QdrantCollectionInitializer lines, the M6 ILlmSummarizerService line, the [IMP:9] SUCCESS marker. `using McpMemoryService.Tools;` is NOT added nor required — WithToolsFromAssembly scans by attribute, no namespace using needed. `using McpMemoryService.Services;` + `using Microsoft.Extensions.Options;` already present from M4/M2.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="REGISTERS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="REGISTERS" />
    </CrossLinks>
  </src_McpMemoryService_Program_cs_ConfigureServices_M7_EDIT>

  <!-- ========== HostSmokeTests UPDATE (tools/list assertion) ========== -->
  <tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs_M7_EDIT FILE="tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs" TYPE="TEST_EDIT">
    <annotation>UPDATE ONLY the `McpInitialize_ReturnsServerInfo` method's tools/list assertion (L155-157 in the current file): replace `toolsArray.GetArrayLength().Should().Be(0);` with `toolsArray.GetArrayLength().Should().Be(2);` + add tool-name containment check: deserialize tools JSON → assert exactly two tool entries with `name == "memory_capture"` and `name == "memory_get_stats"` (Build a small LINQ over the JsonElement: `var names = toolsArray.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet(); names.Should().BeEquivalentTo(new[]{"memory_capture","memory_get_stats"});`). Add a new IMP marker `// [IMP:M7][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 2 tools` at the assertion. DO NOT touch: the HealthEndpoint_Returns200 method, the initialize handshake, the Accept-header setup, the ExtractDataFromSse helper, the BUG_FIX_CONTEXT scar blocks, the [Trait("Category","Integration")] class attribute (M5 reclassification), the existing [IMP:1]/[IMP:2]/[IMP:3] markers, the Module header. The M2 assertion was DESIGNED to break at M7 — this is the designated evolution point (matching the M2 BUG_FIX_CONTEXT comment "alternatives: (a) WithToolsFromAssembly() — rejected: AppGraph/plan forbids it in M2" which now becomes "correct in M7").</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="EXERCISES_VIA_TOOLS_LIST" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="EXERCISES_VIA_TOOLS_LIST" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs_M7_EDIT>

  <!-- ========== CAPTURE TOOL TESTS ========== -->
  <tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_cs FILE="tests/McpMemoryService.Tests/Tools/MemoryCaptureToolTests.cs" TYPE="XUNIT_TEST">
    <keywords>MemoryCaptureTool, Mock IEmbeddingService, Mock IQdrantService, no real Qdrant, snake_case serializer not exercised (SDK transport not loaded), ADR-001 summary exclusion, ADR-005 silent fallback, GUID v4, IMP markers</keywords>
    <annotation>6 unit tests UNCATEGORISED (no [Trait], no Category=Integration — mocked services, no Docker/ONNX). Build MemoryCaptureTool directly via ctor with `Mock&lt;IEmbeddingService&gt;` (Setup EmbedAsync returns a fixed 384-float L2-normalized vector + Setup Get Dimension returns 384) + `Mock&lt;IQdrantService&gt;` (Setup UpsertAsync returns Task.CompletedTask) + `NullLogger&lt;MemoryCaptureTool&gt;.Instance`. Tests per M7 spec §Tests lines 170-215:
1) `ExecuteAsync_ValidInput_ReturnsSuccessWithPointId` — valid input → mock EmbedAsync + UpsertAsync → result.Success == true, result.PointId parses as `Guid.Parse` and `Guid.Parse(result.PointId).Version == 4` (UUID v4 — `Guid.Parse(...).ToString("N").[0..1]` "4" check OR `Guid.TryParse` + inspect version nibble at index 14 of the canonical string).
2) `ExecuteAsync_GeneratesEmbeddingFromContent` — mock EmbedAsync with callback capturing the text arg → invoke with content="test text" → verify mock EmbedAsync called with "test text" (Moq `Verify(x => x.EmbedAsync("test text", It.IsAny&lt;CancellationToken&gt;()), Times.Once)`).
3) `ExecuteAsync_GeneratesGuidV4PointId` — already covered in test 1; pull as a separate assertion OR keep folded. @code decides alias — the spec lists it as a separate [Fact]. Keep separate explicitly.
4) `ExecuteAsync_QdrantFails_ReturnsFailureNotThrow` — `mockQdrant.Setup(x => x.UpsertAsync(...)).ThrowsAsync(new InvalidOperationException("qdrant dead"))` → result.Success == false, result.Error != null, NO exception propagates (`await` does not throw).
5) `ExecuteAsync_EntryTypeSummary_ThrowsValidation` — input with `EntryType = EntryType.Summary` → `await Assert.ThrowsAsync&lt;ArgumentException&gt;(() => tool.CaptureAsync(input, default))` (InputValidator.ValidateCapture throws before the try/catch — validation is NOT silenced).
6) `ExecuteAsync_EmptyContent_ThrowsValidation` — `Content = ""` → `Assert.ThrowsAsync&lt;ArgumentException&gt;`.
OPTIONAL test (if @code deems clean): `ExecuteAsync_GeneratesEmbeddingFromContent_WithTagDefaulting` — verify SessionId null → payload passed to UpsertAsync has SessionId == "" (the null-guard invariant). @code decides whether to add as 7th test (does NOT count toward the 6-test minimum; the AC gate is "all unit tests PASS", not "exactly 6"). Use `MemoryPayload` access via capturing the UpsertAsync callback arg. All tests use CancellationToken.None or `default`.</annotation>
    <tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_ExecuteAsync_ValidInput_ReturnsSuccessWithPointId_METHOD NAME="CaptureAsync_ValidInput_ReturnsSuccessWithPointId" TYPE="TEST_METHOD" IMP="IMP:1,IMP:2" />
    <tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_ExecuteAsync_GeneratesEmbeddingFromContent_METHOD NAME="CaptureAsync_GeneratesEmbeddingFromContent" TYPE="TEST_METHOD" IMP="IMP:1" />
    <tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_ExecuteAsync_GeneratesGuidV4PointId_METHOD NAME="CaptureAsync_GeneratesGuidV4PointId" TYPE="TEST_METHOD" IMP="IMP:2" />
    <tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_ExecuteAsync_QdrantFails_ReturnsFailureNotThrow_METHOD NAME="CaptureAsync_QdrantFails_ReturnsFailureNotThrow" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_ExecuteAsync_EntryTypeSummary_ThrowsValidation_METHOD NAME="CaptureAsync_EntryTypeSummary_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_ExecuteAsync_EmptyContent_ThrowsValidation_METHOD NAME="CaptureAsync_EmptyContent_ThrowsValidation" TYPE="TEST_METHOD" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Services_IEmbeddingService_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="EXERCISES" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Tools_MemoryCaptureToolTests_cs>

  <!-- ========== GET STATS TOOL TESTS ========== -->
  <tests_McpMemoryService_Tests_Tools_MemoryGetStatsToolTests_cs FILE="tests/McpMemoryService.Tests/Tools/MemoryGetStatsToolTests.cs" TYPE="XUNIT_TEST">
    <keywords>MemoryGetStatsTool, Mock IQdrantService, no real Qdrant, snake_case serializer not exercised (SDK transport not loaded), ADR-005 silent fallback, IMP markers</keywords>
    <annotation>4 unit tests UNCATEGORISED. Build MemoryGetStatsTool directly via ctor with `Mock&lt;IQdrantService&gt;` (Setup CountAsync(projectId, entryTypeFilter, ct) returns 42) + `NullLogger&lt;MemoryGetStatsTool&gt;.Instance`. Tests per M7 spec §Tests lines 220-249:
1) `ExecuteAsync_ValidInput_ReturnsCount` — valid input, no filter → mock CountAsync returns 42 → result.Count == 42. [IMP:1,IMP:2]
2) `ExecuteAsync_PassesEntryTypeFilter` — input with `EntryType = EntryType.BugFix` → verify mock CountAsync called with `(projectId, EntryType.BugFix, It.IsAny&lt;CancellationToken&gt;())` (Moq `Verify`).
3) `ExecuteAsync_NoEntryTypeFilter_PassesNull` — input `EntryType = null` → verify mock CountAsync called with `(projectId, null, ct)` (the `EntryType?` passes null through to IQdrantService — no coercion to a sentinel).
4) `ExecuteAsync_EmptyProjectId_ThrowsValidation` — input `ProjectId = ""` → `Assert.ThrowsAsync&lt;ArgumentException&gt;`.
OPTIONAL test: `ExecuteAsync_QdrantFails_ReturnsMinusOneNotThrow` — mock `CountAsync` ThrowsAsync → result.Count == -1, NO exception. spec lists this under "M10 finalize" but providing it gives an explicit ADR-005 silent-fallback runtime proof at M7. @code adds it (does not break the 4-test AC minimum; gives ADR-005 runtime evidence for the get_stats path — symmetric with the capture Qdrant-fails test). Rename `ExecuteAsync_*` → `GetStatsAsync_*` to match the actual method name.</annotation>
    <tests_McpMemoryService_Tests_Tools_MemoryGetStatsToolTests_GetStatsAsync_ValidInput_ReturnsCount_METHOD NAME="GetStatsAsync_ValidInput_ReturnsCount" TYPE="TEST_METHOD" IMP="IMP:1,IMP:2" />
    <tests_McpMemoryService_Tests_Tools_MemoryGetStatsToolTests_GetStatsAsync_PassesEntryTypeFilter_METHOD NAME="GetStatsAsync_PassesEntryTypeFilter" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryGetStatsToolTests_GetStatsAsync_NoEntryTypeFilter_PassesNull_METHOD NAME="GetStatsAsync_NoEntryTypeFilter_PassesNull" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryGetStatsToolTests_GetStatsAsync_EmptyProjectId_ThrowsValidation_METHOD NAME="GetStatsAsync_EmptyProjectId_ThrowsValidation" TYPE="TEST_METHOD" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="EXERCISES" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Tools_MemoryGetStatsToolTests_cs>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow (M7 — single `@code scope=impl:M7`)

1. **Implement `Tools/MemoryCaptureTool.cs`** (per §1 node) — sealed class with `[McpServerToolType]` + ctor-injected `IEmbeddingService _embedding`, `IQdrantService _qdrant`, `ILogger<MemoryCaptureTool> _logger`. One method `[McpServerTool(Name="memory_capture", Description="Save a new fact/decision into memory.")] public async Task<MemoryCaptureOutput> CaptureAsync(MemoryCaptureInput input, CancellationToken cancellationToken)`. Validate via `InputValidator.ValidateCapture(input)` (throws — NOT silent). Then `try { var vector = await _embedding.EmbedAsync(input.Content, cancellationToken); Debug.Assert(vector.Length == _embedding.Dimension, "embedding dimension mismatch"); var pointId = Guid.NewGuid(); var payload = new MemoryPayload { ProjectId=input.ProjectId, SessionId = input.SessionId ?? string.Empty, AgentRole = input.AgentRole, EntryType = input.EntryType, Timestamp = DateTimeOffset.UtcNow, Content = input.Content, Tags = input.Tags, Metadata = input.Metadata }; await _qdrant.UpsertAsync(pointId, vector, payload, cancellationToken); return new MemoryCaptureOutput { Success=true, PointId = pointId.ToString(), Error = null }; } catch (Exception ex) { _logger.LogError(ex, "[IMP:2][CaptureAsync][FATAL] capture silent-fallback ADR-005 projectId={ProjectId}"); return new MemoryCaptureOutput { Success=false, PointId = string.Empty, Error = "capture_failed" }; }`. `[IMP:1]` log emitted before EmbedAsync; `[IMP:2]` log emitted on success path AND inside the catch (as `[IMP:2][...][FATAL]`). LDD format per csharp-conventions.

2. **Implement `Tools/MemoryGetStatsTool.cs`** (per §1 node) — sealed class, `[McpServerToolType]`, ctor `(IQdrantService, ILogger<MemoryGetStatsTool>)`. One method `[McpServerTool(Name="memory_get_stats", Description="Get memory entry count for compact threshold check.")] public async Task<MemoryGetStatsOutput> GetStatsAsync(MemoryGetStatsInput input, CancellationToken cancellationToken)`. `InputValidator.ValidateGetStats(input)`. Then `try { var count = await _qdrant.CountAsync(input.ProjectId, input.EntryType, cancellationToken); return new MemoryGetStatsOutput { Count = count }; } catch (Exception ex) { _logger.LogError(ex, "[IMP:2][GetStatsAsync][FATAL] get_stats silent-fallback ADR-005 projectId={ProjectId}"); return new MemoryGetStatsOutput { Count = -1 }; }`. `[IMP:1]` log entry; `[IMP:2]` log success + fatal-fallback. `input.EntryType` is `EntryType?` → directly passed to `IQdrantService.CountAsync(string, EntryType?, CancellationToken)` (null = "all entry types" — no sentinel).

3. **Implement `Validation/InputValidator.cs`** (per §1 node). Static class, 4 methods. `ValidateCapture(MemoryCaptureInput input)`:
   ```csharp
   if (string.IsNullOrWhiteSpace(input.Content))
       throw new ArgumentException("content must not be null or whitespace.", nameof(input.Content));
   if (string.IsNullOrWhiteSpace(input.ProjectId))
       throw new ArgumentException("project_id must not be null or whitespace.", nameof(input.ProjectId));
   if (input.EntryType == EntryType.Summary)
       throw new ArgumentException("entry_type='summary' is not allowed via capture (created only via compact).", nameof(input.EntryType));
   ```
   `ValidateGetStats(MemoryGetStatsInput input)`:
   ```csharp
   if (string.IsNullOrWhiteSpace(input.ProjectId))
       throw new ArgumentException("project_id must not be null or whitespace.", nameof(input.ProjectId));
   ```
   `ValidateRetrieve(MemoryRetrieveInput input)` — null/whitespace query + project_id + Limit in [1,10] (FORWARD stub, no caller in M7; declared now to avoid touching this file at M8). `ValidateCompact(MemoryCompactInput input)` — null/whitespace project_id + BatchSize in [1,100] (FORWARD stub, M9 caller). Use `ArgumentException` consistently. `_ = input;` is NOT acceptable — full method bodies for all four (no `// TODO`).

4. **Edit `Program.cs`** (per §1 M7_EDIT node):
   - Add `using System.Text.Json;` to the usings block (verify not already transitively present — `ImplicitUsings` covers `System.Text.Json` but the explicit using may be needed for `JsonSerializerOptions`. @code verifies at compile — if IDE0005 (unused), omit; if CS0246 (not in scope), add).
   - In `ConfigureServices`, REPLACE the `.WithListToolsHandler((_,_) => ValueTask.FromResult(new ListToolsResult { Tools = new List<Tool>() }))` line with `.WithToolsFromAssembly()`. KEEP the M2 BUG_FIX_CONTEXT scar block above it (in-tact). Append a one-line `// [IMP:M7] Superseded in M7 by WithToolsFromAssembly() — M2 empty-list placeholder was the designated eviction point. M2 scar block above kept for history.` immediately after the swapped line.
   - EXTEND the existing `services.AddOptions<McpServerOptions>().Configure<IOptions<McpOptions>>((serverOpts, mcpOpts) => { serverOpts.ServerInfo = new Implementation { Name = mcpOpts.Value.ServerName, Version = mcpOpts.Value.ServerVersion }; })` lambda to ALSO set `serverOpts.SerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };` inside the SAME Configure lambda (before/after ServerInfo — order-independent, both are simple assignments). This is the single source of truth for tool I/O snake_case transport (ADR-010, mem-024).
   - **[UNVERIFIED_VERSION] SDK 1.4.0 serializer-options knob:** `McpServerOptions.SerializerOptions` is the proposed property name (architect's best-effort from local knowledge — `web_search` returned S3-unavailable on 2026-07-01, property name NOT web-verified). @code VERIFY at compile: if `McpServerOptions.SerializerOptions` does not exist, try the documented alternatives: (a) `services.Configure<JsonSerializerOptions>("McpServer", o => o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)` named-options for the SDK's internal serializer, (b) `McpServerOptions.JsonSerializerOptions` (different casing/name), (c) DI-default `JsonSerializerOptions` configuration. Document the chosen API in a comment block in Program.cs. If NONE of the SDK knobs can be configured for snake_case (should not happen, but as the FINAL fallback), add explicit `[JsonPropertyName("snake_case")]` attributes to the M3 `MemoryCaptureInput`/`MemoryCaptureOutput`/`MemoryGetStatsInput`/`MemoryGetStatsOutput` DTO properties — and document this as the chosen deterministic fallback in the tool classes' MODULE_CONTRACT (since attribute-level naming on SDK-transport DTOs is justified when the SDK offers no serializer knob — NOT a violation of the M3 plan's serializer-level discipline, which was about M6-internal LlmSummarize* contracts that go through the explicit OnnxEmbeddingService/QdrantService serializer paths, NOT through the MCP SDK auto-serializer).

5. **Update `tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs`** (per §1 M7_EDIT test node) — in `McpInitialize_ReturnsServerInfo`, REPLACE the tools/list assertion block (currently `var toolsArray = toolsResult.GetProperty("result").GetProperty("tools"); toolsArray.GetArrayLength().Should().Be(0);`) with: `var toolsArray = toolsResult.GetProperty("result").GetProperty("tools"); toolsArray.GetArrayLength().Should().Be(2); var toolNames = toolsArray.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet(); toolNames.Should().BeEquivalentTo(new[] { "memory_capture", "memory_get_stats" }); // [IMP:M7][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 2 tools (memory_capture, memory_get_stats)`. PRESERVE: the HealthEndpoint_Returns200 method, initialize handshake, Accept header, ExtractDataFromSse helper, BUG_FIX_CONTEXT scars, `[Trait("Category","Integration")]`, existing [IMP:1]/[IMP:2]/[IMP:3] markers, Module header. Test categorisation: STAYS `Category=Integration` (M5 reclassification rationale — host startup runs `QdrantCollectionInitializer` which needs Qdrant). So this test continues to run only under the full `dotnet test` gate, NOT the unit gate.

6. **Write `tests/McpMemoryService.Tests/Tools/MemoryCaptureToolTests.cs`** — 6 (or 7) unit tests, UNCATEGORISED (no `[Trait]`, no `Category=Integration`). Build `MemoryCaptureTool` directly via ctor with `Mock<IEmbeddingService>` + `Mock<IQdrantService>` + `NullLogger<MemoryCaptureTool>.Instance` (Moq already in test csproj from M2). `Mock<IEmbeddingService>.Setup(x => x.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(CreateUnitVector(384)); SetupGet(x => x.Dimension).Returns(384);` `Mock<IQdrantService>.Setup(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);`. Tests per §1 capture-tests node annotation (rename `ExecuteAsync_*` → `CaptureAsync_*` to match the method). For the GUID v4 check: `var guid = Guid.Parse(result.PointId); guid.ToString().Should().MatchRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-4[0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$");` — version nibble `4` at index 14 + variant nibble `8/9/a/b` at index 19. For the Qdrant-fails test: `mockQdrant.Setup(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("qdrant down"));` → `result.Success.Should().BeFalse(); result.Error.Should().NotBeNullOrEmpty();` (await must NOT throw). For summary-exclusion + empty-content: `Assert.ThrowsAsync<ArgumentException>` — these throw BEFORE the silent-fallback try/catch (validation is not silenced).

7. **Write `tests/McpMemoryService.Tests/Tools/MemoryGetStatsToolTests.cs`** — 4 (or 5) unit tests, UNCATEGORISED. Build `MemoryGetStatsTool` via ctor with `Mock<IQdrantService>` + `NullLogger<MemoryGetStatsTool>.Instance`. `mockQdrant.Setup(x => x.CountAsync(It.IsAny<string>(), It.IsAny<EntryType?>(), It.IsAny<CancellationToken>())).ReturnsAsync(42);`. Tests per §1 get_stats-tests node annotation (rename `ExecuteAsync_*` → `GetStatsAsync_*`). For PassesEntryTypeFilter: `var mockQdrant = new Mock<IQdrantService>(); mockQdrant.Setup(x => x.CountAsync(It.IsAny<string>(), It.IsAny<EntryType?>(), It.IsAny<CancellationToken>())).ReturnsAsync(42); await tool.GetStatsAsync(input with { EntryType = EntryType.BugFix }, default); mockQdrant.Verify(x => x.CountAsync(It.IsAny<string>(), EntryType.BugFix, It.IsAny<CancellationToken>()), Times.Once);` For NoEntryTypeFilter_PassesNull: input with `EntryType = null` → `Verify(x => x.CountAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once)` (Moq supports exact-null `Verify` — use `It.IsAny<EntryType?>()` if Moq null-equality becomes finicky; @code picks the cleanest). For QdrantFails_ReturnsMinusOneNotThrow: `mockQdrant.Setup(x => x.CountAsync(...)).ThrowsAsync(new InvalidOperationException())` → `result.Count.Should().Be(-1)`.

8. **Build + unit gate before return:**
   - `dotnet build McpMemoryService.slnx` → **0 Warning(s), 0 Error(s)**. AC-1.
   - `dotnet test --filter "Category!=Integration"` → unit gate: M3 (16 = 3 DTO + 13 mapping) + M5 mapping unit (5) + M6 unit (6) + **M7 unit (10..11)** = **37..38 tests, `Failed: 0`** (M2 host smoke stays `Category=Integration` — skipped under the filter; the M7 HostSmokeTests assertion change therefore does not affect the unit gate; @qa will exercise it under the full `dotnet test` gate with Docker Qdrant).
   - Do NOT run `dotnet test` (full) — M7 unit tests are mocking-only (no Docker); @qa runs the full gate (HostSmokeTests now expects 2 tools + the M5 integration tests) separately.

---

## 3. Acceptance Criteria (M7)

> Verbatim from `milestones/M7-mcp-tool-capture-stats.md` lines 252-264, labelled for mechanical `@qa` checking.

- [ ] **AC-1:** `dotnet build` — OK.
- [ ] **AC-2:** `dotnet test` — all unit tests PASS (no real Qdrant/Docker required).
- [ ] **AC-3:** MCP `tools/list` returns `memory_capture` and `memory_get_stats` (HostSmokeTests updated assertion: 2 tools, names match).
- [ ] **AC-4:** `memory_capture` input schema matches SPEC §4.2 (5 entry_type values, no summary) — ADR-001. Verified by source: `InputValidator.ValidateCapture` rejects `EntryType.Summary`. Note: the M3 DTO `MemoryCaptureInput.EntryType` is `EntryType` (all 6 allowed at the DTO level); the exclusion is enforced AT the tool validator (consistent with M3 contract annotation "validation lives in M7 tool").
- [ ] **AC-5:** `memory_capture` generates embedding, upserts with GUID v4, returns success+point_id.
- [ ] **AC-6:** `memory_capture` on Qdrant failure returns `success=false` (does NOT throw) — ADR-005.
- [ ] **AC-7:** `memory_capture` rejects `entry_type=summary` with validation error — ADR-001.
- [ ] **AC-8:** `memory_get_stats` input schema matches SPEC §4.3 (6 entry_type values incl summary).
- [ ] **AC-9:** `memory_get_stats` returns correct count from QdrantService.
- [ ] **AC-10:** `memory_get_stats` passes `entry_type_filter` when provided (and null when not).
- [ ] **AC-11:** `InputValidator` covers all 4 tool inputs (capture, get_stats, retrieve, compact) — `ValidateCapture`+`ValidateGetStats` exercised by M7 tests; `ValidateRetrieve`+`ValidateCompact` declared (forward stubs for M8/M9).
- [ ] **AC-12:** Logs contain `[IMP:1]` entry + `[IMP:2]` exit markers (CaptureAsync + GetStatsAsync; both SUCCESS and FATAL paths).

---

## Notes for @code (M7)

1. **The tool CATCHES — silent-fallback at the MCP boundary (ADR-005).** This is the **inverse** of the M5/M6 do-not-swallow invariant. M7 tools are the *MCP-facing caller* of `IQdrantService`/`IEmbeddingService`; they MUST swallow non-validation exceptions and return failure DTOs (`Success=false`/`Count=-1`) without re-throwing — so the MCP Streamable-HTTP connection never breaks. `try { ... } catch (Exception ex) { _logger.LogError(ex, ...); return failureDto; }` is the canonical shape. The M5 `QdrantService` still THROWS internally; the M7 tool catches its throw. The M6 `LlmSummarizerService` still THROWS internally; M9 (not M7) catches its throw. No re-throw from any M7 tool's catch handler. Validation failures (`ArgumentException` from `InputValidator`) DO propagate — they signal caller misuse, not infrastructure failure, and the M10 `GlobalExceptionMiddleware` will turn them into JSON-RPC errors.

2. **Tools are INSTANCE classes with `[McpServerToolType]` + ctor DI** (mem-006 SDK pattern). NOT static classes with static methods. The MCP SDK 1.4.0 `WithToolsFromAssembly()` discovers `[McpServerToolType]`-annotated classes, instantiates them via DI on each `tools/call`, resolves ctor parameters from the DI container (`IEmbeddingService`/`IQdrantService`/`ILogger<>` — all already registered by M4/M5), and calls the `[McpServerTool]`-annotated method. DTO method parameters (`MemoryCaptureInput`, `MemoryGetStatsInput`) are deserialized by the SDK from the JSON-RPC `arguments` block. `CancellationToken` is supplied by the SDK transport. Tests bypass the SDK entirely — they construct the tool via the ctor with mocked services and invoke the method directly, exactly like the M6 `LlmSummarizerServiceTests` pattern.

3. **`MemoryPayload.SessionId` null-guard lives in the tool, NOT by changing the M3 DTO.** `MemoryCaptureInput.SessionId` is `string?` (M3); `MemoryPayload.SessionId` is `required string` (M3). The null→empty coercion `input.SessionId ?? string.Empty` happens in `CaptureAsync` — this preserves the M3 contracts verbatim AND the PayloadMappingExtensions round-trip (which reads `qdrantPayload["session_id"].StringValue` — a null would NRE there). Documented in the tool's MODULE_CONTRACT `[INVARIANTS]` block.

4. **`point_id` = `Guid.NewGuid().ToString()` — lowercase-dashed UUID v4.** `Guid.NewGuid()` produces v4 by construction (random + version/variant nibbles set). `pointId.ToString()` default format `"D"` (`xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx`). Lowercase. Returned in `MemoryCaptureOutput.PointId` (M3 DTO is `required string`). The IQdrantService.UpsertAsync signature takes `Guid pointId` (M5) — pass the raw `Guid` to Qdrant, the string to the DTO. Do NOT reformat with `.ToString("N")`/`.ToString("B")`.

5. **Program.cs scar discipline.** The M2 BUG_FIX_CONTEXT scar block above the `WithListToolsHandler` line is PRESERVED (it documents why M2 forbade `WithToolsFromAssembly` — historical truth). M7's swap the LINE not the SCAR. Append a one-line `[IMP:M7] supersede` comment immediately AFTER the swap. The `McpServerOptions` post-configure block's scar is preserved; the `Configure<IOptions<McpOptions>>` lambda is EXTENDED (add `SerializerOptions` assignment) — this is additive within an existing block, not a scar removal. The `MapMcp("/mcp")` scar is Untouched. `[IMP:9]` SUCCESS marker preserved.

6. **M7 deliberately breaks ONE M2 assertion — the `toolsArray.GetArrayLength().Should().Be(0)` in HostSmokeTests.** This assertion was DESIGNED to break at M7 (the M2 BUG_FIX_CONTEXT comment itself acknowledges `(a) WithToolsFromAssembly() discovering zero tools — rejected: AppGraph/plan forbids it in M2` — i.e., M2 used the explicit empty-list handler BECAUSE M7 was the planned real-tools milestone). The update: 0 → 2, plus tool-name containment (`memory_capture`, `memory_get_stats`). The Accept header setup, the initialize handshake, the ExtractDataFromSse helper, the BUG_FIX_CONTEXT scars, and the `[Trait("Category","Integration")]` classification are ALL preserved — only the tools/list assertion is touched.

7. **Snake_case transport — `McpServerOptions.SerializerOptions` is `[UNVERIFIED_VERSION]`.** `web_search` returned S3-unavailable on 2026-07-01 ("Сервис поиска временно недоступен"). The architect's local-knowledge best-effort: `McpServerOptions.SerializerOptions` is a `JsonSerializerOptions` property on the SDK's server options (ModelContextProtocol 1.4.0). @code VERIFY at compile: try `serverOpts.SerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };` inside the existing `Configure<IOptions<McpOptions>>` lambda. If the property name differs or is read-only, fall back to (a) named-options `services.Configure<JsonSerializerOptions>("McpServer", ...)`, (b) DI-default `JsonSerializerOptions` configuration, or (c) the FINAL deterministic fallback — explicit `[JsonPropertyName("snake_case")]` attributes on the M3 capture/stats DTOs only (justified by the SDK-gap — see Program.cs M7_EDIT annotation for the rationale). @code DOCUMENTS the chosen path in a comment block in Program.cs + the tool classes' MODULE_CONTRACT so @qa can verify transport correctness. mem-024 lesson: prefer single-source-of-truth serializer config; `[JsonPropertyName]` is the last resort.

8. **No `#pragma warning disable`.** Build warnings = AC-1 failure. Watch for: CS8625 nullable (the catch handler's `result.Error = "capture_failed"` — non-nullable string, fine; `input.SessionId ?? string.Empty` — null-coalesced, fine), IDE0005 unused usings (add `using System.Text.Json;` ONLY if used — `JsonSerializerOptions` may already be in scope via `ImplicitUsings`; if `JsonSerializerOptions` resolves without the using, do NOT add it and accept IDE0005 won't trigger), CA1062 nullable argument validation (the InputValidator tools guard; the tool methods do not — that's fine, MSTest-style). The `Debug.Assert` is compiled out in Release → no perf impact, no CS warning. Use `System.Diagnostics.Debug` (already covered by `ImplicitUsings`).

9. **Test categorisation — UNCATEGORISED for tool unit tests** (6 capture + 4..5 get_stats = 10..11 tests). They use `Mock<IEmbeddingService>` + `Mock<IQdrantService>` + `NullLogger<T>.Instance` — NO real Qdrant/Docker/ONNX. They MUST run in the unit gate (`dotnet test --filter "Category!=Integration"`). Evolved unit count: M3 (16) + M5 mapping (5) + M6 (6) + M7 (10..11) = **37..38**. The new `Tools/` test folder does NOT need a `.csproj` edit — SDK auto-discovers. HostSmokeTests stays `Category=Integration` (unchanged by M7) — its tools/list assertion change lives in the integration-only path; @qa exercises it under the full gate with Docker Qdrant.

10. **Decomposition decision — SINGLE `@code scope=impl:M7` dispatch.** 2 tool classes (1 async method each) + 1 static validator (4 methods, 2 used now) + 1 Program.cs edit (1 line swap + 1 serializer extension + 1 using) + 1 HostSmokeTests assertion edit + 2 test files (10..11 tests). Cohesive: tool registration is atomic — splitting tool-implementations from Program.cs registration would yield a non-running intermediate state (`tools/list` would not advertise the new tool without the `WithToolsFromAssembly` swap). Below the >5-method threshold if tests are counted separately (but tests are not "implementation methods"). **NO `## Decomposition` section** is appended.

11. **Do NOT run `dotnet test` (full) during the `@code` dispatch.** The M7 unit tests are mocking-only; the HostSmokeTests assertion update requires Docker Qdrant to start the `QdrantCollectionInitializer` IHostedService. The unit gate (`dotnet test --filter "Category!=Integration"`) is the `@code` return gate. @qa runs the full gate (with Docker) — including the updated HostSmokeTests assertion (`toolsArray.GetArrayLength().Should().Be(2)`). AC-3 (`tools/list` returns 2 tools) is verified by @qa under the full gate, NOT by @code's unit-run.

12. **AGENTS.md build commands.** `dotnet build McpMemoryService.slnx` + `dotnet test --filter "Category!=Integration"`. `.slnx` is the .NET 10 default solution format; the legacy `.sln` alias auto-discovers it.

13. **profile.md consistency.** Plan prose is technical English (matches the M5/M6 plan style). No Russian summary header required.

14. **Web search record (for @qa audit).**
    - `[SOURCE: web_search attempt, query="ModelContextProtocol C# SDK 1.4.0 WithToolsFromAssembly McpServerToolType McpServerTool instance method DI inject serializer options JsonNamingPolicy", ts=2026-07-01T15:30:00Z]` → **[WEB_SEARCH_UNAVAILABLE] SearXNG service unavailable — search returned error ("Сервис поиска временно недоступен").** Per WEB_SEARCH_PROTOCOL scenario S3, proceeded without search results using local knowledge + mem-006 (the M2 SDK-validation insight confirming `[McpServerTool]` attribute + `.WithToolsFromAssembly()` pattern) as the source of truth.
    - No `fetch_and_extract` call was made (no web_search URLs returned to fetch).
    - Architectural decisions (instance `[McpServerToolType]`+ctor DI per mem-006, silent-fallback at tool level per ADR-005, entry_type summary exclusion per ADR-001, GUID v4 `point_id`, snake_case transport via `McpServerOptions.SerializerOptions` (or `[JsonPropertyName]` fallback per `[UNVERIFIED_VERSION]` SDK 1.4.0 API), Program.cs `WithListToolsHandler`→`WithToolsFromAssembly` swap, HostSmokeTests 0→2 assertion update) are derived from the M7 spec + SPEC §4.2/§4.3/§7 + AGENTS.md ADR-001/002/005/010/011 + mem-006/mem-009/mem-024 + the M2/M3/M4/M5/M6 realized source, NOT from external web documentation. Specifically `[McpServerType]/[McpServerTool]/WithToolsFromAssembly` API shape is `[UNVERIFIED_VERSION]` against the live 1.4.0 NuGet — but mem-006 already validated this from the M2 architectural ground-truth investigation, so this is treated as confirmed.

---

## M8 — MCP tool: memory_retrieve

| Field | Value |
|---|---|
| Current Milestone | **M8 — MCP tool `memory_retrieve` (DI-injected `[McpServerToolType]`+`[McpServerTool]` tool: validate → embed query → Qdrant semantic search → map `MemoryEntry`→`MemoryRetrieveResult`; silent-fallback to empty Results on Qdrant failure)** |
| Status | PLAN_READY (awaiting `@code scope=impl:M8`) |
| Previous State | M7 — **SUCCESS** (committed as `cdd5bd8` — `feat(m7): memory_capture + memory_get_stats MCP tools with snake_case transport`, per `tests/qa_report.md`). `.test_counter.json` counter=0. M8 is the next DAG node (`M7 → {M8, M9}` parallel branch). |
| Milestone Deps | **M3 (DONE — `MemoryRetrieveInput`/`MemoryRetrieveOutput`/`MemoryRetrieveResult` DTOs exist in `Contracts/`; created without `[JsonPropertyName]` — M8 ADDS them), M4 (DONE — `IEmbeddingService.EmbedAsync(text, ct)` returns `float[384]` L2-normalized; `IEmbeddingService.Dimension => 384`), M5 (DONE — `IQdrantService.SearchAsync(float[] queryVector, string projectId, AgentRole? agentRoleFilter, EntryType? entryTypeFilter, int limit=5, CancellationToken ct)` returning `IReadOnlyList<MemoryEntry>` where `MemoryEntry { PointId: Guid, Payload: MemoryPayload, Vector: float[], Score: float? }`), M7 (DONE — tool registration pattern `[McpServerToolType]`+`[McpServerTool(Name=...)]` + ctor DI; `services.AddTransient<McpMemoryService.Tools.MemoryCaptureTool>();` + `MemoryGetStatsTool` registration lines at Program.cs L163-164; `InputValidator.ValidateRetrieve` ALREADY fully implemented as a forward stub — null/whitespace Query/ProjectId throws `ArgumentException`, Limit outside [1,10] throws `ArgumentException`; HostSmokeTests assertion `toolsArray.GetArrayLength().Should().Be(2)` at L156 + `toolNames.Should().Contain("memory_capture")`/`"memory_get_stats"` at L160-161 + `[IMP:M7]...[SUCCESS]` marker at L154).** All deps DONE. |
| Dispatch Recommendation | **Single `@code scope=impl:M8` — NO decomposition.** 1 tool class (`MemoryRetrieveTool` — 1 async method `RetrieveAsync`) + 3 DTO edits (add `[JsonPropertyName]` snake_case overrides on `MemoryRetrieveInput`/`MemoryRetrieveOutput`/`MemoryRetrieveResult` — the M7 rung-d discipline applied at M8 creation time per mem-027/mem-028) + 1 Program.cs edit (add `services.AddTransient<McpMemoryService.Tools.MemoryRetrieveTool>();` after the M7 tool registrations) + 1 HostSmokeTests edit (2→3 tools: `.Be(2)`→`.Be(3)` + add `toolNames.Should().Contain("memory_retrieve")` + update `[IMP:M7]` marker or add `[IMP:M8]` marker) + 1 test file (`MemoryRetrieveToolTests.cs` — ~10 unit tests). `InputValidator.ValidateRetrieve` is ALREADY fully implemented (M7 forward stub) — M8 just CALLS it, does NOT touch `InputValidator.cs`. Below the >5-new-methods decomposition threshold. **NO `## Decomposition` section** is appended. |
| Etap | Etap 1 (implement M1..M12). Etap 2 future. |

## ADRs Touched by M8

| ADR | Decision | M8 Action |
|---|---|---|
| **ADR-005** | Silent fallback on Qdrant unavailability; retrieve → empty results, no exception | **CRITICAL:** The silent-fallback lives at the **tool level** (MCP-facing boundary), exactly mirroring M7 `MemoryCaptureTool`/`MemoryGetStatsTool`. `MemoryRetrieveTool.RetrieveAsync` wraps `_embedding.EmbedAsync` + `_qdrant.SearchAsync` in `try { ... } catch (Exception ex) { _logger.LogWarning(ex, ...); return new MemoryRetrieveOutput { Results = Array.Empty<MemoryRetrieveResult>() }; }`. NO exception escapes the tool — the MCP Streamable-HTTP connection MUST NOT break. This is the **inverse** of the M5/M6 do-not-swallow invariant at the service level (QdrantService/SearchAsync still THROWS internally; the M8 tool catches at the boundary). Per SPEC §7: "memory_retrieve should return empty, not throw and break MCP connection." |
| **ADR-010** | MCP SDK = ModelContextProtocol 1.4.0 | `WithToolsFromAssembly()` already discovers `[McpServerToolType]`-annotated classes (swapped in by M7 — Program.cs). M8 just ADDS one more `[McpServerToolType]` class (`MemoryRetrieveTool`) + one `services.AddTransient<...MemoryRetrieveTool>();` DI line. The assembly-scan auto-discovers the new tool — NO `Program.cs` `WithToolsFromAssembly()` call edit needed (only the AddTransient DI line so ctor params resolve). mem-006 pattern. |
| **ADR-001** | entry_type taxonomy = 6 types; `entry_type_filter` enum over `decision|bug_fix|requirement|summary|rejection|insight` | `MemoryRetrieveInput.EntryTypeFilter` is `EntryType?` (M3 — all 6 allowed). Retrieve filters ALL 6 types (unlike capture which excludes `Summary` — retrieve reads summaries produced by compact). `InputValidator.ValidateRetrieve` does NOT filter enum values (it only checks Query/ProjectId/Limit) — enum-shaped validation is the SDK's `arguments` deserialization concern. M8 passes the filter straight through to `IQdrantService.SearchAsync`. |
| **ADR-002** | agent_role = 5 roles incl orchestrator; `agent_role_filter` enum over `orchestrator|architect|code|debug|qa` | `MemoryRetrieveInput.AgentRoleFilter` is `AgentRole?` (M3). M8 passes the filter straight through to `IQdrantService.SearchAsync`. Enum-shaped validation lives at SDK JSON-RPC deserialization; `InputValidator.ValidateRetrieve` does not re-validate enum values. |
| **ADR-006** | .NET 10 target | net10.0 already confirmed. `[McpServerToolType]`/`[McpServerTool]` from ModelContextProtocol 1.4.0 (pinned in csproj from M2). No new package refs. |
| **ADR-011** | ONNX 384-dim | `MemoryRetrieveTool` calls `_embedding.EmbedAsync(input.Query)` → 384-dim L2-normalized query vector, passed verbatim to `_qdrant.SearchAsync(queryVector, ...)` (Qdrant Dot distance — QdrantService already assumes L2-normalized vectors). OnnxEmbeddingService ALREADY L2-normalizes (M4 invariant) — the tool does NOT re-normalize. |

> **M8 invariants (must NOT regress):**
> 1. **The tool CATCHES — silent-fallback at the MCP boundary (ADR-005).** `MemoryRetrieveTool.RetrieveAsync` wraps the embed + search call in `try { ... } catch (Exception ex) { _logger.LogWarning(ex, "[IMP:3][RetrieveAsync][FATAL] silent-fallback ADR-005 ..."); return new MemoryRetrieveOutput { Results = Array.Empty<MemoryRetrieveResult>() }; }`. The MCP connection MUST NOT break. This is the **caller level** ADR-005 references — the tool IS the MCP-facing caller of `IQdrantService`/`IEmbeddingService`. Identical to the M7 tool pattern; inverse of M5/M6 service-level do-not-swallow.
> 2. **Tools are instance classes with ctor DI, NOT static methods.** `[McpServerToolType]` on `MemoryRetrieveTool` + `[McpServerTool(Name = "memory_retrieve")]` on `RetrieveAsync` + ctor-injected `IEmbeddingService`/`IQdrantService`/`ILogger<MemoryRetrieveTool>`. The MCP SDK instantiates the tool via DI on each `tools/call`. Tests bypass the SDK and construct the tool via the ctor with mocked services (exactly like the M7 `MemoryCaptureToolTests` pattern). NO `[McpServerTool]` on static methods — instance method only.
> 3. **`[JsonPropertyName]` on retrieve DTOs is added AT M8 CREATION TIME (mem-027/mem-028 discipline).** The M3 `MemoryRetrieveInput`/`MemoryRetrieveOutput`/`MemoryRetrieveResult` records exist WITHOUT `[JsonPropertyName]` (M3 predates the M7 rung-d fix). M8 EDITS these three records to add `[JsonPropertyName("snake_case")]` overrides per SPEC §4.1 transport contract (`query`, `project_id`, `agent_role_filter`, `entry_type_filter`, `limit` for input; `results` for output; `point_id`, `agent_role`, `entry_type`, `content`, `timestamp`, `score`, `tags` for result). Do NOT regress to relying on a `McpServerOptions.SerializerOptions` knob (status: absent on SDK 1.4.0 — the M7 rung-a/b probe already proved this, see Program.cs M7 scar). Add a `BUG_FIX_CONTEXT: [scar — rung-d preemptive]` block in each edited DTO mirroring the M7 capture/stats DTO scar text. Update `[CHANGES]: LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides` in each DTO's MODULE_CONTRACT.
> 4. **`InputValidator.ValidateRetrieve` is ALREADY fully implemented — M8 just CALLS it.** Do NOT touch `src/McpMemoryService/Validation/InputValidator.cs` — the M7 forward stub (`Query`/`ProjectId` non-whitespace, `Limit` in [1, 10], throws `ArgumentException`) is the complete validation. M8 `RetrieveAsync` calls `InputValidator.ValidateRetrieve(input);` as the first line (before any `[IMP:1]` log) — validation errors PROPAGATE (caller misuse, not infrastructure failure), exactly mirroring M7 `MemoryCaptureTool`/`MemoryGetStatsTool`.
> 5. **LDD markers honor the per-tool namespace.** `[IMP:1][RetrieveAsync][PROGRESS]` embedding generated (dim) — log AFTER `EmbedAsync` returns. `[IMP:2][RetrieveAsync][SUCCESS]` search returned N results — log AFTER `SearchAsync` returns. `[IMP:3][RetrieveAsync][FATAL]` silent-fallback to empty on error — log INSIDE the `catch (Exception ex)` handler. The `[IMP:N][Method][Step]` canonical format from M4 applies. Do NOT collide with `IQdrantService.SearchAsync`'s own `[IMP:3][SearchAsync][SUCCESS]` marker — that marker belongs to the service; the tool's `[IMP:3]` is on `RetrieveAsync`, distinct method-name token.

---

## PURPOSE

Implement the retrieval tier of McpMemoryService: a `MemoryRetrieveTool` (instance class, `[McpServerToolType]`+`[McpServerTool(Name = "memory_retrieve")]`) that validates `MemoryRetrieveInput` via the existing `InputValidator.ValidateRetrieve` (Query/ProjectId required, Limit in [1, 10]), generates a 384-dim L2-normalized query embedding via `IEmbeddingService.EmbedAsync`, searches Qdrant via `IQdrantService.SearchAsync(queryVector, projectId, agentRoleFilter, entryTypeFilter, limit, ct)`, maps the returned `MemoryEntry` list (each carrying `PointId: Guid`, `Payload: MemoryPayload`, `Score: float?`) to `MemoryRetrieveResult` DTOs (`PointId` string GUID `"D"` format, `AgentRole`, `EntryType`, `Content`, `Timestamp`, `Score`, `Tags`), and returns `MemoryRetrieveOutput { Results = [...] }`. On `IEmbeddingService`/`IQdrantService` failure (any `Exception`), the tool catches, logs `[IMP:3][RetrieveAsync][FATAL]` warning, and returns an EMPTY `MemoryRetrieveOutput { Results = Array.Empty<MemoryRetrieveResult>() }` — silent-fallback per ADR-005 (memory is a capability enhancer, not a dependency; retrieve-empty MUST NOT break the MCP connection). Also: edit the three M3 retrieve DTOs (`MemoryRetrieveInput`, `MemoryRetrieveOutput`, `MemoryRetrieveResult`) to add `[JsonPropertyName("snake_case")]` transport overrides per the M7 rung-d discipline (mem-027/mem-028 — apply at creation/edit time, never wait for QA to catch PascalCase leakage); add `services.AddTransient<McpMemoryService.Tools.MemoryRetrieveTool>();` to `Program.cs` alongside the M7 tool registrations (L163-164); update `HostSmokeTests` tools/list assertion from 2→3 tools plus `memory_retrieve` name containment; write `tests/McpMemoryService.Tests/Tools/MemoryRetrieveToolTests.cs` with ~10 unit tests (mocked `IEmbeddingService`+`IQdrantService`+`NullLogger<T>`, no Docker/ONNX/Qdrant). Extend `SnakeCaseTransportTests` with retrieve-DTO round-trip cases OR add a sibling test file — `@code` picks whichever builds cleanest on net10.0.

---

## 1. Draft Code Graph

> M8 adds the third **MCP tool** (after M7's `MemoryCaptureTool`/`MemoryGetStatsTool`). `MemoryRetrieveTool` follows the M7 instance-class + ctor-DI pattern verbatim — `[McpServerToolType]`+`[McpServerTool]`, `IEmbeddingService`+`IQdrantService`+`ILogger<>` ctor params, `try`/`catch` silent-fallback. The three M3 retrieve DTOs get an additive `[JsonPropertyName]` edit (no logic change). `Program.cs` gets one additive `AddTransient` line. `HostSmokeTests` gets the tools/list assertion bumped 2→3. `csharp-conventions` `#region` structuring applies (Fields / Constructors / RetrieveAsync).

```xml
<DraftCodeGraph>
  <!-- ========== TOOL ========== -->
  <src_McpMemoryService_Tools_MemoryRetrieveTool_cs FILE="src/McpMemoryService/Tools/MemoryRetrieveTool.cs" TYPE="MCP_TOOL">
    <keywords>MemoryRetrieveTool, McpServerToolType, McpServerTool, memory_retrieve, IEmbeddingService, IQdrantService, SearchAsync, EmbedAsync, MemoryEntry, MemoryRetrieveResult, silent-fallback, ADR-005, ILogger, IMP:1/IMP:2/IMP:3</keywords>
    <annotation>public sealed class MemoryRetrieveTool. MODULE_CONTRACT header per csharp-conventions (matches MemoryCaptureTool + MemoryGetStatsTool style). #region Fields: IEmbeddingService _embedding (readonly), IQdrantService _qdrant (readonly), ILogger&lt;MemoryRetrieveTool&gt; _logger (readonly). #region Constructors: ctor(IEmbeddingService, IQdrantService, ILogger&lt;MemoryRetrieveTool&gt;) — assign fields. #region RetrieveAsync: the single async method, annotated [McpServerTool(Name = "memory_retrieve")]. Signature: Task&lt;MemoryRetrieveOutput&gt; RetrieveAsync(MemoryRetrieveInput input, CancellationToken cancellationToken = default). Body: (1) InputValidator.ValidateRetrieve(input) — throws ArgumentException, PROPAGATES (not swallowed). (2) try { var vector = await _embedding.EmbedAsync(input.Query, cancellationToken); _logger.LogInformation("[IMP:1][RetrieveAsync][PROGRESS] embedding generated dim={Dim}", vector.Length); var entries = await _qdrant.SearchAsync(vector, input.ProjectId, input.AgentRoleFilter, input.EntryTypeFilter, input.Limit, cancellationToken); _logger.LogInformation("[IMP:2][RetrieveAsync][SUCCESS] search returned {Count} results", entries.Count); var results = entries.Select(MapToRetrieveResult).ToList(); return new MemoryRetrieveOutput { Results = results }; } catch (Exception ex) { _logger.LogWarning(ex, "[IMP:3][RetrieveAsync][FATAL] silent-fallback ADR-005 — returning empty Results projectId={ProjectId}", input.ProjectId); return new MemoryRetrieveOutput { Results = Array.Empty&lt;MemoryRetrieveResult&gt; }; }. NO explicit Debug.Assert on vector.Length==384 (M4 already enforces; Optional DEBUG-only assert is acceptable but not required — the tool trusts the IEmbeddingService.Dimension contract).</annotation>
    <src_McpMemoryService_Tools_MemoryRetrieveTool_embedding_FIELD NAME="_embedding" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryRetrieveTool_qdrant_FIELD NAME="_qdrant" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryRetrieveTool_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryRetrieveTool_RetrieveAsync_METHOD NAME="RetrieveAsync" TYPE="PUBLIC_ASYNC_METHOD" MCP_TOOL_NAME="memory_retrieve" IMP="IMP:1,IMP:2,IMP:3">
      <annotation>1. InputValidator.ValidateRetrieve(input) — first line, throws ArgumentException (propagates). 2. try: EmbedAsync → [IMP:1] log dim. 3. SearchAsync(vector, projectId, agentRoleFilter, entryTypeFilter, limit, ct) → [IMP:2] log count. 4. Map entries → MemoryRetrieveResult list. 5. Return MemoryRetrieveOutput { Results }. 6. catch (Exception ex): [IMP:3] log warning, return empty Results. NOTE: Score = entry.Score ?? 0f (QdrantService.SearchAsync always sets Score; null-coalesce is defensive). PointId = entry.PointId.ToString("D") lowercase-dashed GUID.</annotation>
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Contracts_MemoryRetrieveInput_cs" TYPE="CONSUMES_INPUT_DTO" />
        <Link TARGET="src_McpMemoryService_Contracts_MemoryRetrieveOutput_cs" TYPE="PRODUCES_OUTPUT_DTO" />
        <Link TARGET="src_McpMemoryService_Services_IEmbeddingService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="CALLS_VALIDATOR" />
        <Link TARGET="src_McpMemoryService_Models_MemoryEntry_cs" TYPE="MAPS_FROM" />
        <Link TARGET="src_McpMemoryService_Models_MemoryPayload_cs" TYPE="READS_PAYLOAD" />
      </CrossLinks>
    </src_McpMemoryService_Tools_MemoryRetrieveTool_RetrieveAsync_METHOD>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="SIBLING_TOOL_PATTERN" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="SIBLING_TOOL_PATTERN" />
      <Link TARGET="memory_retrieve_MCP_SPEC_PLANNED" TYPE="RESOLVES" />
    </CrossLinks>
  </src_McpMemoryService_Tools_MemoryRetrieveTool_cs>

  <!-- ========== DTO EDITS (additive [JsonPropertyName]) ========== -->
  <src_McpMemoryService_Contracts_MemoryRetrieveInput_cs FILE="src/McpMemoryService/Contracts/MemoryRetrieveInput.cs" TYPE="CONTRACT_RECORD_EDIT">
    <annotation>EDIT the M3 record (exists WITHOUT [JsonPropertyName]) — ADD per-property [JsonPropertyName] snake_case overrides per M7 rung-d discipline (mem-027/mem-028 — apply at edit time, never wait for QA). Properties → JSON keys: Query → "query"; ProjectId → "project_id"; AgentRoleFilter → "agent_role_filter"; EntryTypeFilter → "entry_type_filter"; Limit → "limit". Add `using System.Text.Json.Serialization;`. Add a BUG_FIX_CONTEXT scar block mirroring the M7 capture/stats DTO scar text: `// BUG_FIX_CONTEXT: [scar — rung-d preemptive (M8): snake_case names fixed per-property at M8 creation time per mem-027/mem-028. Immunized by SnakeCaseTransportTests]`. Update MODULE_CONTRACT [CHANGES]: `LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 discipline); M3 initial creation; M1/S4+S5 corrections (AgentRoleFilter + EntryTypeFilter added).`. The record shape, property types, and `required` modifiers are UNCHANGED — only attributes added.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="CONSUMED_BY" />
      <Link TARGET="tests_McpMemoryService_Tests_Models_SnakeCaseTransportTests_cs" TYPE="IMMUNIZED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Contracts_MemoryRetrieveInput_cs>

  <src_McpMemoryService_Contracts_MemoryRetrieveOutput_cs FILE="src/McpMemoryService/Contracts/MemoryRetrieveOutput.cs" TYPE="CONTRACT_RECORD_EDIT">
    <annotation>EDIT the M3 record (output + sibling result) — ADD per-property [JsonPropertyName] snake_case overrides. MemoryRetrieveOutput: Results → "results". MemoryRetrieveResult: PointId → "point_id"; AgentRole → "agent_role"; EntryType → "entry_type"; Content → "content"; Timestamp → "timestamp"; Score → "score"; Tags → "tags". Add `using System.Text.Json.Serialization;` (already added by the Input edit if shared — check). Same BUG_FIX_CONTEXT scar block text. Same MODULE_CONTRACT [CHANGES] update. Record shape/types/required UNCHANGED.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="PRODUCED_BY" />
      <Link TARGET="tests_McpMemoryService_Tests_Models_SnakeCaseTransportTests_cs" TYPE="IMMUNIZED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Contracts_MemoryRetrieveOutput_cs>

  <!-- ========== PROGRAM.CS EDIT (additive DI line) ========== -->
  <src_McpMemoryService_Program_cs_ConfigureServices_M8_EDIT FILE="src/McpMemoryService/Program.cs" TYPE="DI_EDIT">
    <annotation>ADDITIVE — append one DI line IMMEDIATELY AFTER the M7 tool registrations (Program.cs L163-164: `services.AddTransient&lt;McpMemoryService.Tools.MemoryCaptureTool&gt;();` + `services.AddTransient&lt;McpMemoryService.Tools.MemoryGetStatsTool&gt;();`):
  // [IMP:M8][ConfigureServices][OPTION] Register memory_retrieve MCP tool (Transient) — ADR-005 silent-fallback at tool level, ADR-010 WithToolsFromAssembly auto-discovery
  services.AddTransient&lt;McpMemoryService.Tools.MemoryRetrieveTool&gt;();
STRICT ADDITIVE: do NOT touch the L163-164 M7 lines, the M2 BUG_FIX_CONTEXT scars (McpServerOptions post-configure, the `.WithToolsFromAssembly()` swap marker, `MapMcp("/mcp")`), the M4 `IEmbeddingService` line, the M5 `IQdrantService`/`QdrantCollectionInitializer` lines, the M6 `LlamaCpp` HttpClient + `ILlmSummarizerService` lines, or the `[IMP:9][ConfigureServices][SUCCESS]` marker. `WithToolsFromAssembly()` already scans the assembly for `[McpServerToolType]` classes — adding the `[McpServerToolType]` annotation on `MemoryRetrieveTool` + the AddTransient DI line is SUFFICIENT for the SDK to discover + instantiate the tool on `tools/call`. No `using McpMemoryService.Tools;` edit (already present from M7; the AddTransient uses the fully-qualified type name).</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="REGISTERS" />
    </CrossLinks>
  </src_McpMemoryService_Program_cs_ConfigureServices_M8_EDIT>

  <!-- ========== HOSTSMOKETESTS EDIT (2→3 tools assertion) ========== -->
  <tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs_M8_EDIT FILE="tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs" TYPE="TEST_ASSERTION_EDIT">
    <annotation>EDIT the M7 assertion block (HostSmokeTests L154-161): `toolsArray.GetArrayLength().Should().Be(2)` (L156) → `.Should().Be(3)`; the `toolNames.Should().Contain("memory_capture")` (L160) + `.Contain("memory_get_stats")` (L161) lines STAY; ADD `toolNames.Should().Contain("memory_retrieve", "M8 adds memory_retrieve to tools/list");`. UPDATE the `[IMP:M7][McpInitialize_ReturnsServerInfo][SUCCESS]` marker comment at L154 to read `[IMP:M8][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 3 tools (memory_capture, memory_get_stats, memory_retrieve)` (or add a new `[IMP:M8]` comment line below the `[IMP:M7]` line — `@code` picks whichever preserves the M7 scar text best; the M7 marker is a SUCCESS scar, do NOT delete it). PRESERVE: the Accept header setup, the initialize handshake, the SSE `ExtractDataFromSse` helper, the BUG_FIX_CONTEXT scars, the `[Trait("Category","Integration")]` classification, the existing `[IMP:1]/[IMP:2]/[IMP:3]` markers, the Module header. HostSmokeTests stays `Category=Integration` — runtime proof requires Docker Qdrant (`QdrantCollectionInitializer` IHostedService) + ONNX model (M4).</annotation>
    <CrossLinks>
      <Link TARGET="tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="VERIFIES_DISCOVERY" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs_M8_EDIT>

  <!-- ========== TESTS ========== -->
  <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_cs FILE="tests/McpMemoryService.Tests/Tools/MemoryRetrieveToolTests.cs" TYPE="XUNIT_TEST">
    <keywords>MemoryRetrieveTool, RetrieveAsync, Mock IEmbeddingService, Mock IQdrantService, NullLogger, no real Qdrant/ONNX, unit, snake_case transport, filters, limit, silent-fallback, score, IMP markers</keywords>
    <annotation>~10 unit tests, UNCATEGORISED (no [Trait], no Category=Integration — no real Qdrant/Docker/ONNX required). Construct MemoryRetrieveTool via ctor with Mock&lt;IEmbeddingService&gt; + Mock&lt;IQdrantService&gt; + NullLogger&lt;MemoryRetrieveTool&gt;.Instance — NO MCP transport (ctor-direct, exactly like the M7 MemoryCaptureToolTests pattern). Mock IEmbeddingService.EmbedAsync returns a canned float[384] (e.g. new float[384]; or Enumerable.Repeat(0.1f, 384).ToArray()). Mock IQdrantService.SearchAsync returns a canned IReadOnlyList&lt;MemoryEntry&gt; — build MemoryEntry records with PointId=Guid.NewGuid(), Payload=a MemoryPayload (with ProjectId, AgentRole, EntryType, Content, Timestamp, Tags), Vector=float[384] (unused by the tool map), Score=0.85f. Tests: 1) RetrieveAsync_ValidInput_ReturnsResults — mock returns 2 entries → Assert Results.Count == 2, fields mapped (PointId == entries[0].PointId.ToString("D"), AgentRole/EntryType/Content/Timestamp/Score/Tags match Payload/Score). 2) RetrieveAsync_GeneratesEmbeddingFromQuery — verify EmbedAsync called with input.Query (Moq Verify). 3) RetrieveAsync_PassesFiltersToQdrant — input with AgentRoleFilter=Debug + EntryTypeFilter=BugFix → verify SearchAsync called with those enum values + correct projectId + limit. 4) RetrieveAsync_RespectsLimit — input.Limit=3 → verify SearchAsync limit arg == 3. 5) RetrieveAsync_QdrantFails_ReturnsEmptyResults — mock SearchAsync throws → Assert Results.Count == 0, NO exception thrown (silent fallback). 6) RetrieveAsync_EmbeddingFails_ReturnsEmptyResults — mock EmbedAsync throws → Assert Results.Count == 0, NO exception (catch covers BOTH embedding + search). 7) RetrieveAsync_EmptyQuery_ThrowsValidation — Assert.ThrowsAsync&lt;ArgumentException&gt;(() => RetrieveAsync(input with Query="" or whitespace)). 8) RetrieveAsync_EmptyProjectId_ThrowsValidation — Assert.ThrowsAsync&lt;ArgumentException&gt;. 9) RetrieveAsync_LimitExceeds10_ThrowsValidation — input.Limit=50 → Assert.ThrowsAsync&lt;ArgumentException&gt;. 10) RetrieveAsync_LimitZero_ThrowsValidation — input.Limit=0 → Assert.ThrowsAsync&lt;ArgumentException&gt;. 11) RetrieveAsync_ResultsContainScoreFromQdrant — mock returns entry with Score=0.92f → Assert result.Score == 0.92f. 12) RetrieveAsync_ResultsContainScoreZeroWhenQdrantReturnsNullScore — entry.Score=null → Assert result.Score == 0f (defensive null-coalesce). @code picks the cleanest subset (~10..12 tests). Optional: if SnakeCaseTransportTests extension is preferred over a separate retrieve snake_case test, add 2 round-trip cases for MemoryRetrieveInput + MemoryRetrieveOutput there instead of here — but the tool tests must still verify SDK-agnostic field mapping.</annotation>
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_ValidInput_ReturnsResults_METHOD NAME="RetrieveAsync_ValidInput_ReturnsResults" TYPE="TEST_METHOD" IMP="IMP:1,IMP:2" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_GeneratesEmbeddingFromQuery_METHOD NAME="RetrieveAsync_GeneratesEmbeddingFromQuery" TYPE="TEST_METHOD" IMP="IMP:1" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_PassesFiltersToQdrant_METHOD NAME="RetrieveAsync_PassesFiltersToQdrant" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_RespectsLimit_METHOD NAME="RetrieveAsync_RespectsLimit" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_QdrantFails_ReturnsEmptyResults_METHOD NAME="RetrieveAsync_QdrantFails_ReturnsEmptyResults" TYPE="TEST_METHOD" IMP="IMP:3" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_EmbeddingFails_ReturnsEmptyResults_METHOD NAME="RetrieveAsync_EmbeddingFails_ReturnsEmptyResults" TYPE="TEST_METHOD" IMP="IMP:3" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_EmptyQuery_ThrowsValidation_METHOD NAME="RetrieveAsync_EmptyQuery_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_EmptyProjectId_ThrowsValidation_METHOD NAME="RetrieveAsync_EmptyProjectId_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_LimitExceeds10_ThrowsValidation_METHOD NAME="RetrieveAsync_LimitExceeds10_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_LimitZero_ThrowsValidation_METHOD NAME="RetrieveAsync_LimitZero_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_RetrieveAsync_ResultsContainScoreFromQdrant_METHOD NAME="RetrieveAsync_ResultsContainScoreFromQdrant" TYPE="TEST_METHOD" IMP="IMP:2" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Contracts_MemoryRetrieveInput_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Contracts_MemoryRetrieveOutput_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="EXERCISES_VALIDATERetrieve" />
      <Link TARGET="src_McpMemoryService_Services_IEmbeddingService_cs" TYPE="MOCKS" />
      <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="MOCKS" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Tools_MemoryRetrieveToolTests_cs>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow

> `@code` execution algorithm for `scope=impl:M8`. Source: M8 spec §Algorithm (lines 121-141) + §Contracts + SPEC §4.1 (memory_retrieve spec) + §7 (Qdrant error handling — silent empty) + M1/S4 (5 roles in agent_role_filter) + M1/S5 (entry_type_filter added — 6 types) + ADR-001/002/005/006/010/011 + mem-006 (SDK tool pattern) + mem-027/mem-028 (snake_case transport discipline — apply `[JsonPropertyName]` at creation time, never wait for QA). One `@code` dispatch, no decomposition.

1. **Edit `Contracts/MemoryRetrieveInput.cs`** — ADD `[JsonPropertyName("snake_case")]` attributes to the 5 properties per the M7 rung-d discipline. The record already exists from M3 (44 lines, no `[JsonPropertyName]`); M8 EDITS it additively:
   ```csharp
   namespace McpMemoryService.Contracts;

   using System.Text.Json.Serialization;
   using McpMemoryService.Enums;

   /// <summary>
   /// [PURPOSE]: Input DTO for the memory_retrieve tool.
   /// </summary>
   /// <remarks>
   /// [INVARIANTS]: Query and ProjectId are required. Limit defaults to 5 (max=10 enforced in InputValidator.ValidateRetrieve).
   ///   Transport property keys are snake_case (query/project_id/agent_role_filter/entry_type_filter/limit) enforced
   ///   via [JsonPropertyName] (M8 rung-d preemptive — post-M7 discipline, mem-027/mem-028).
   /// </remarks>
   public sealed record MemoryRetrieveInput
   {
       // BUG_FIX_CONTEXT: [scar — rung-d preemptive (M8): snake_case names fixed per-property at M8 creation time
       //   per mem-027/mem-028. Immunized by SnakeCaseTransportTests — see Program.cs M7 scar for the SDK 1.4.0
       //   no-serializer-knob ladder walk. ts=2026-07-01T<now>Z]

       /// <summary>[PURPOSE]: The search query — text to find semantically similar entries for.</summary>
       [JsonPropertyName("query")]
       public required string Query { get; init; }

       /// <summary>[PURPOSE]: Project identifier (SHA-256 of workspace root).</summary>
       [JsonPropertyName("project_id")]
       public required string ProjectId { get; init; }

       /// <summary>[PURPOSE]: Optional filter by agent role (orchestrator/architect/code/debug/qa — 5 roles per ADR-002).</summary>
       [JsonPropertyName("agent_role_filter")]
       public AgentRole? AgentRoleFilter { get; init; }

       /// <summary>[PURPOSE]: Optional filter by entry type (decision/bug_fix/requirement/summary/rejection/insight — 6 types per ADR-001).</summary>
       [JsonPropertyName("entry_type_filter")]
       public EntryType? EntryTypeFilter { get; init; }

       /// <summary>[PURPOSE]: Maximum number of results to return. Defaults to 5 (max=10 enforced in InputValidator.ValidateRetrieve).</summary>
       [JsonPropertyName("limit")]
       public int Limit { get; init; } = 5;
   }
   ```
   Update the `#region MODULE_CONTRACT` `[CHANGES]` line: `LAST_CHANGE: M8 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 mem-027/mem-028 discipline); M3 initial creation; M1/S4+S5 corrections.`. Record shape/types/required UNCHANGED — only attributes + the scar block + the `[CHANGES]` line added.

2. **Edit `Contracts/MemoryRetrieveOutput.cs`** — ADD `[JsonPropertyName]` to BOTH records (`MemoryRetrieveOutput` + the sibling `MemoryRetrieveResult`). The file already exists from M3 (62 lines, two records, no `[JsonPropertyName]`). Add `using System.Text.Json.Serialization;` (already added by the Input edit if shared — verify not duplicated; the file's existing `using McpMemoryService.Enums;` stays).
   - `MemoryRetrieveOutput.Results` → `[JsonPropertyName("results")]`.
   - `MemoryRetrieveResult.PointId` → `[JsonPropertyName("point_id")]`.
   - `MemoryRetrieveResult.AgentRole` → `[JsonPropertyName("agent_role")]`.
   - `MemoryRetrieveResult.EntryType` → `[JsonPropertyName("entry_type")]`.
   - `MemoryRetrieveResult.Content` → `[JsonPropertyName("content")]`.
   - `MemoryRetrieveResult.Timestamp` → `[JsonPropertyName("timestamp")]`.
   - `MemoryRetrieveResult.Score` → `[JsonPropertyName("score")]`.
   - `MemoryRetrieveResult.Tags` → `[JsonPropertyName("tags")]`.
   Add the same `BUG_FIX_CONTEXT: [scar — rung-d preemptive (M8)]` block in BOTH records (or one block at the top of the file covering both — `@code` picks whichever reads cleanest). Update both `MODULE_CONTRACT [CHANGES]` lines to `LAST_CHANGE: M8 — added [JsonPropertyName] ...`. Record shape/types/required UNCHANGED — only attributes added.

3. **Implement `Tools/MemoryRetrieveTool.cs`** per §1 node + M8 spec §Contracts + SPEC §4.1 + §7, with `#region` structuring (matches `MemoryCaptureTool.cs`):
   ```csharp
   #region MODULE_CONTRACT [DOMAIN(Tools): memory_retrieve MCP tool; CONCEPT(MemoryRetrieveTool): Validate → embed query → Qdrant search → map results; TECH(MCP, ADR-005/001/002)]
   // ... header per csharp-conventions, matching MemoryCaptureTool MODULE_CONTRACT style ...
   #endregion MODULE_CONTRACT

   namespace McpMemoryService.Tools;

   using System.Diagnostics;
   using McpMemoryService.Contracts;
   using McpMemoryService.Enums;
   using McpMemoryService.Models;
   using McpMemoryService.Services;
   using McpMemoryService.Validation;
   using Microsoft.Extensions.Logging;
   using ModelContextProtocol;
   using ModelContextProtocol.Server;

   /// <summary>
   /// [PURPOSE]: MCP tool for semantic search of past memory entries.
   /// </summary>
   [McpServerToolType]
   public sealed class MemoryRetrieveTool
   {
       #region Fields
       private readonly IEmbeddingService _embedding;
       private readonly IQdrantService _qdrant;
       private readonly ILogger<MemoryRetrieveTool> _logger;
       #endregion Fields

       #region Constructors
       public MemoryRetrieveTool(IEmbeddingService embedding, IQdrantService qdrant, ILogger<MemoryRetrieveTool> logger)
       {
           _embedding = embedding;
           _qdrant = qdrant;
           _logger = logger;
       }
       #endregion Constructors

       #region RetrieveAsync
       /// <summary>
       /// [PURPOSE]: Semantic search for relevant past memory entries.
       /// </summary>
       /// <param name="input">Retrieve input (query, project_id, optional agent_role_filter/entry_type_filter, limit).</param>
       /// <param name="cancellationToken">Cancellation token.</param>
       /// <returns>MemoryRetrieveOutput with ranked results (empty on Qdrant failure per ADR-005).</returns>
       /// <exception cref="ArgumentException">Thrown when input validation fails (query/project_id empty, limit outside [1,10]).</exception>
       /// <remarks>
       /// [INVARIANTS]: On Qdrant/ONNX failure, returns empty Results (does NOT throw) — ADR-005 silent-fallback at tool level.
       ///   Validation errors propagate (caller misuse, not infrastructure failure).
       /// [RATIONALE]: The tool IS the MCP-facing caller of IEmbeddingService+IQdrantService — it catches Exception and returns
       ///   empty Results so the MCP Streamable-HTTP connection never breaks (ADR-005, SPEC §7).
       /// [CHANGES]: LAST_CHANGE: M8 creation.
       /// </remarks>
       [McpServerTool(Name = "memory_retrieve")]
       public async Task<MemoryRetrieveOutput> RetrieveAsync(
           MemoryRetrieveInput input,
           CancellationToken cancellationToken = default)
       {
           // Validate input (throws ArgumentException — propagates, NOT swallowed)
           InputValidator.ValidateRetrieve(input);

           try
           {
               // Generate query embedding (384-dim L2-normalized per M4 / ADR-011)
               var vector = await _embedding.EmbedAsync(input.Query, cancellationToken);

               // [IMP:1][RetrieveAsync][PROGRESS] embedding generated
               _logger.LogInformation(
                   "[IMP:1][RetrieveAsync][PROGRESS] embedding generated dim={Dim} queryLen={Len}",
                   vector.Length, input.Query.Length);

               // Semantic search in Qdrant
               var entries = await _qdrant.SearchAsync(
                   vector,
                   input.ProjectId,
                   input.AgentRoleFilter,
                   input.EntryTypeFilter,
                   input.Limit,
                   cancellationToken);

               // [IMP:2][RetrieveAsync][SUCCESS] search returned N results
               _logger.LogInformation(
                   "[IMP:2][RetrieveAsync][SUCCESS] search returned {Count} results projectId={ProjectId}",
                   entries.Count, input.ProjectId);

               // Map MemoryEntry → MemoryRetrieveResult
               var results = entries.Select(MapToRetrieveResult).ToList();

               return new MemoryRetrieveOutput { Results = results };
           }
           catch (Exception ex)
           {
               // [IMP:3][RetrieveAsync][FATAL] silent-fallback ADR-005 — return empty Results
               _logger.LogWarning(
                   ex,
                   "[IMP:3][RetrieveAsync][FATAL] silent-fallback ADR-005 — returning empty Results projectId={ProjectId}",
                   input.ProjectId);

               return new MemoryRetrieveOutput { Results = Array.Empty<MemoryRetrieveResult>() };
           }
       }

       private static MemoryRetrieveResult MapToRetrieveResult(MemoryEntry entry)
       {
           return new MemoryRetrieveResult
           {
               PointId = entry.PointId.ToString("D"),    // lowercase-dashed GUID v4
               AgentRole = entry.Payload.AgentRole,
               EntryType = entry.Payload.EntryType,
               Content = entry.Payload.Content,
               Timestamp = entry.Payload.Timestamp,
               Score = entry.Score ?? 0f,                // defensive null-coalesce (QdrantService.SearchAsync always sets Score)
               Tags = entry.Payload.Tags,
           };
       }
       #endregion RetrieveAsync
   }
   ```
   - **Verified property names** against the realized source: `MemoryEntry.PointId` (`Guid`), `MemoryEntry.Payload` (`MemoryPayload`), `MemoryEntry.Score` (`float?`). `MemoryPayload` carries `ProjectId`, `AgentRole`, `EntryType`, `Content`, `Timestamp`, `Tags` (per M3 — `@code` VERIFY the exact property names against `src/McpMemoryService/Models/MemoryPayload.cs` before compiling; the M3 spec lines 167-184 + the M5 PayloadMappingExtensions round-trip are the ground truth). If any name differs, adjust `MapToRetrieveResult` accordingly — the mapping is mechanical.
   - **`PointId.ToString("D")`** — the `"D"` format produces the lowercase-dashed `xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx` form (matches the M7 `MemoryCaptureTool` GUID v4 invariant). Do NOT use `.ToString("N")` (no dashes) or `.ToString("B")` (braces).
   - **`Score ?? 0f`** — `MemoryEntry.Score` is `float?`; `MemoryRetrieveResult.Score` is `float` (required). The null-coalesce is defensive — `QdrantService.SearchAsync` always sets Score on results (verified in `QdrantService.cs` L145 area). The M8 spec test "ResultsContainScoreFromQdrant" exercises a non-null Score; the optional "ResultsContainScoreZeroWhenQdrantReturnsNullScore" covers the null path.
   - **`Debug.Assert` on vector dimension** — OPTIONAL. `IEmbeddingService.Dimension => 384` (M4 invariant). The tool trusts the contract. If `@code` adds a `Debug.Assert(vector.Length == _embedding.Dimension, "embedding dimension mismatch")` (debug-only, compiled out in Release), that's fine — matches the M7 `MemoryCaptureTool` L102 pattern. NOT required for AC.

4. **Program.cs edit (additive DI line).** In `ConfigureServices`, append IMMEDIATELY AFTER the M7 tool registrations (Program.cs L163-164):
   ```csharp
   services.AddTransient<McpMemoryService.Tools.MemoryCaptureTool>();
   services.AddTransient<McpMemoryService.Tools.MemoryGetStatsTool>();
   // [IMP:M8][ConfigureServices][OPTION] Register memory_retrieve MCP tool (Transient) — ADR-005 silent-fallback at tool level, ADR-010 WithToolsFromAssembly auto-discovery (mem-006)
   services.AddTransient<McpMemoryService.Tools.MemoryRetrieveTool>();
   ```
   - **STRICT ADDITIVE** — do NOT touch L163-164 (M7 lines), the M2 BUG_FIX_CONTEXT scars (McpServerOptions post-configure block, the `.WithToolsFromAssembly()` swap + `[IMP:M7] supersede` marker, `MapMcp("/mcp")`), the M4 `IEmbeddingService` line, the M5 `IQdrantService`/`QdrantCollectionInitializer` lines, the M6 `LlamaCpp` HttpClient + `ILlmSummarizerService` lines, or the `[IMP:9][ConfigureServices][SUCCESS]` marker.
   - `WithToolsFromAssembly()` (swapped in by M7) auto-discovers `[McpServerToolType]` classes in the assembly — adding the annotation on `MemoryRetrieveTool` + the `AddTransient` DI line is SUFFICIENT for SDK discovery + ctor-param resolution on `tools/call`. NO `WithToolsFromAssembly()` call edit.
   - No `using McpMemoryService.Tools;` edit (the AddTransient uses the fully-qualified `McpMemoryService.Tools.MemoryRetrieveTool` type name; `using` already present from M7 covers the shorter form if `@code` prefers it — but the M7 lines use the fully-qualified form, MATCH that style for consistency).

5. **HostSmokeTests edit (2→3 tools assertion).** In `tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs` L154-161, update the tools/list assertion block:
   ```csharp
   // [IMP:M8][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 3 tools (memory_capture, memory_get_stats, memory_retrieve)
   // (was [IMP:M7] ... 2 tools ... — M7 scar preserved above; M8 extends the count to 3)
   var toolsArray = toolsResult.GetProperty("result").GetProperty("tools");
   toolsArray.GetArrayLength().Should().Be(3);
   // ...
   var toolNames = toolsArray.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
   toolNames.Should().Contain("memory_capture");
   toolNames.Should().Contain("memory_get_stats");
   toolNames.Should().Contain("memory_retrieve", "M8 adds memory_retrieve to tools/list");
   ```
   - PRESERVE the M7 `[IMP:M7][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 2 tools (memory_capture, memory_get_stats)` scar comment — do NOT delete it. Either: (a) insert the `[IMP:M8]` comment line ABOVE/BELOW the `[IMP:M7]` line with an `(extended by M8)` note, or (b) REPLACE the `[IMP:M7]` line text with the `[IMP:M8]` text + keep a `[IMP:M7] scar — was 2 tools` one-liner. `@code` picks whichever preserves the historical truth (scar discipline — same approach as M7's treatment of the M2 `WithListToolsHandler` scar).
   - PRESERVE: the Accept header setup, the initialize handshake, the SSE `ExtractDataFromSse` helper, the BUG_FIX_CONTEXT scars, `[Trait("Category","Integration")]`, the existing `[IMP:1]/[IMP:2]/[IMP:3]` markers, the Module header. Only the tools/list assertion block (L154-161) is touched (count 2→3 + add `memory_retrieve` containment + update/add the `[IMP:M8]` marker).

6. **Write `tests/McpMemoryService.Tests/Tools/MemoryRetrieveToolTests.cs`** — ~10 unit tests, UNCATEGORISED (no `[Trait]`, no `Category=Integration` — no real Qdrant/Docker/ONNX). Pattern: construct `MemoryRetrieveTool` via ctor with `Mock<IEmbeddingService>` + `Mock<IQdrantService>` + `NullLogger<MemoryRetrieveTool>.Instance` (Moq already in test csproj from M2). Mock `IEmbeddingService.EmbedAsync` returns `new float[384]` (or `Enumerable.Repeat(0.1f, 384).ToArray()`). Mock `IQdrantService.SearchAsync` returns a canned `IReadOnlyList<MemoryEntry>` — build `MemoryEntry` records with `PointId = Guid.NewGuid()`, `Payload = new MemoryPayload { ProjectId = ..., AgentRole = AgentRole.Debug, EntryType = EntryType.BugFix, Content = "...", Timestamp = DateTimeOffset.UtcNow, Tags = new[] { "tag1", "tag2" } }`, `Vector = new float[384]` (unused by the tool map), `Score = 0.85f`. Use `Moq` `Setup(x => x.SearchAsync(...)).ReturnsAsync(...)` + `Verify(x => x.SearchAsync(It.IsAny<float[]>(), "proj", AgentRole.Debug, EntryType.BugFix, 5, It.IsAny<CancellationToken>()), Times.Once)` for the filter-passing test.
   - `RetrieveAsync_ValidInput_ReturnsResults` — mock returns 2 entries → `Assert.Equal(2, output.Results.Count)`; verify `output.Results[0].PointId == entries[0].PointId.ToString("D")`, `AgentRole`/`EntryType`/`Content`/`Timestamp`/`Score`/`Tags` mapped correctly.
   - `RetrieveAsync_GeneratesEmbeddingFromQuery` — `mockEmbedding.Verify(x => x.EmbedAsync("memory leak", It.IsAny<CancellationToken>()), Times.Once)` after `await RetrieveAsync(input with Query = "memory leak")`.
   - `RetrieveAsync_PassesFiltersToQdrant` — `await RetrieveAsync(input with AgentRoleFilter = AgentRole.Debug, EntryTypeFilter = EntryType.BugFix)` → `mockQdrant.Verify(x => x.SearchAsync(It.IsAny<float[]>(), input.ProjectId, AgentRole.Debug, EntryType.BugFix, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once)`.
   - `RetrieveAsync_RespectsLimit` — `await RetrieveAsync(input with Limit = 3)` → `mockQdrant.Verify(x => x.SearchAsync(..., 3, ...), Times.Once)`.
   - `RetrieveAsync_QdrantFails_ReturnsEmptyResults` — `mockQdrant.Setup(x => x.SearchAsync(...)).ThrowsAsync(new InvalidOperationException("qdrant down"))` → `var output = await RetrieveAsync(validInput); Assert.Empty(output.Results);` (NO exception thrown — silent fallback). Verify the IMP:3 warning was logged (optional — `NullLogger` discards; a `TestLogger` would capture, but `NullLogger` is the documented pattern from M7; skip log-content verification unless a `TestLogger` is already in the test infra).
   - `RetrieveAsync_EmbeddingFails_ReturnsEmptyResults` — `mockEmbedding.Setup(x => x.EmbedAsync(...)).ThrowsAsync(new InvalidOperationException("onnx died"))` → `Assert.Empty(output.Results);` (the catch covers BOTH embedding and search — embedding failure is also silent-fallback at the retrieve tool level; ADR-005 doesn't distinguish which subsystem failed).
   - `RetrieveAsync_EmptyQuery_ThrowsValidation` — `await Assert.ThrowsAsync<ArgumentException>(() => RetrieveAsync(input with Query = "" or whitespace))`.
   - `RetrieveAsync_EmptyProjectId_ThrowsValidation` — `await Assert.ThrowsAsync<ArgumentException>(() => RetrieveAsync(input with ProjectId = ""))`.
   - `RetrieveAsync_LimitExceeds10_ThrowsValidation` — `await Assert.ThrowsAsync<ArgumentException>(() => RetrieveAsync(input with Limit = 50))`.
   - `RetrieveAsync_LimitZero_ThrowsValidation` — `await Assert.ThrowsAsync<ArgumentException>(() => RetrieveAsync(input with Limit = 0))`.
   - `RetrieveAsync_ResultsContainScoreFromQdrant` — mock returns entry with `Score = 0.92f` → `Assert.Equal(0.92f, output.Results[0].Score)`.
   - (Optional) `RetrieveAsync_ResultsContainScoreZeroWhenQdrantReturnsNullScore` — mock returns entry with `Score = null` → `Assert.Equal(0f, output.Results[0].Score)` (defensive null-coalesce path).
   - The new `Tools/` test folder is auto-discovered by the SDK-style test csproj — NO `.csproj` edit needed (matches the M7 `Tools/MemoryCaptureToolTests.cs` precedent).
   - If `@code` chooses to extend `SnakeCaseTransportTests` (M7 file) with retrieve-DTO round-trip cases instead of (or in addition to) putting a snake_case assertion in the tool tests, add 2 Fact methods: one deserializes `{"query":"...","project_id":"...","agent_role_filter":"debug","entry_type_filter":"bug_fix","limit":5}` with DEFAULT `JsonSerializerOptions` (no PropertyNamingPolicy) into `MemoryRetrieveInput` and asserts all fields map; one serializes a `MemoryRetrieveOutput` with default options and asserts the JSON keys are `results`/`point_id`/`agent_role`/`entry_type`/`content`/`timestamp`/`score`/`tags`. This immunizes the M8 `[JsonPropertyName]` scar (mirrors the M7 SnakeCaseTransportTests proof).

7. **Build + test gate before return:**
   - `dotnet build McpMemoryService.sln` (the `dotnet` CLI auto-discovers `.slnx`) → **0 Warning(s), 0 Error(s)**. AC-1.
   - `dotnet test --filter "Category!=Integration"` → unit gate: M3 (16) + M5 mapping (5) + M6 (6) + M7 (≈5 capture + ≈5 get_stats + SnakeCaseTransportTests) + **M8 unit (~10..12)** = all green, `Failed: 0`. AC-2. The 3 retrieve-DTO `[JsonPropertyName]` edits must NOT break the existing M3 `DtoValidationTests` / `MemoryRetrieveInput_DefaultLimit_Is5` — verify that test still passes (the `Limit = 5` default is unchanged).
   - Do NOT run `dotnet test` (full) — M8 has no integration tests. The HostSmokeTests 2→3 assertion update requires Docker Qdrant (`QdrantCollectionInitializer` IHostedService) + ONNX model (M4) — that's @qa's responsibility under the full gate (M2 HostSmokeTests is `Category=Integration`, runtime proof deferred — same as M7). AC-3 is source-verified by @code (assertion `.Be(3)` + `memory_retrieve` containment present in the diff), runtime-verified by @qa.

---

## 3. Acceptance Criteria

> Verbatim from `milestones/M8-mcp-tool-retrieve.md` lines 222-232, labelled for mechanical `@qa` checking.

- [ ] **AC-1:** `dotnet build` — OK.
- [ ] **AC-2:** `dotnet test` — all unit tests PASS (no real Qdrant/Docker required for M8 unit tests).
- [ ] **AC-3:** MCP `tools/list` returns `memory_retrieve` alongside capture and get_stats (HostSmokeTests assertion updated 2→3 tools + `memory_retrieve` name containment). Runtime proof requires Docker Qdrant (integration gate) — @qa exercises it; @code source-verifies the assertion edit.
- [ ] **AC-4:** `memory_retrieve` input schema matches SPEC §4.1 (with M1 corrections: 5 roles, 6 entry_type values incl summary, limit max 10) — verified by `[JsonPropertyName]` on `MemoryRetrieveInput` (`query`, `project_id`, `agent_role_filter`, `entry_type_filter`, `limit`) + `SnakeCaseTransportTests` (or sibling) round-trip.
- [ ] **AC-5:** Tool generates embedding from query, searches Qdrant, returns mapped results.
- [ ] **AC-6:** Filters (`agent_role_filter`, `entry_type_filter`) passed through to `IQdrantService.SearchAsync`.
- [ ] **AC-7:** Limit enforced in [1, 10] — `InputValidator.ValidateRetrieve` throws `ArgumentException` on violation (ALREADY implemented in M7 forward stub — M8 just calls it).
- [ ] **AC-8:** Qdrant failure → silent empty results (no exception thrown, `[IMP:3]` warning logged) — ADR-005.
- [ ] **AC-9:** Results contain `Score` from Qdrant semantic search (mapped from `MemoryEntry.Score ?? 0f`).
- [ ] **AC-10:** Logs contain `[IMP:1]` embedding generated, `[IMP:2]` search complete (count), `[IMP:3]` fallback to empty on error markers.
- [ ] **AC-11:** Retrieve DTOs (`MemoryRetrieveInput`/`MemoryRetrieveOutput`/`MemoryRetrieveResult`) carry `[JsonPropertyName("snake_case")]` overrides — the M7 rung-d discipline applied at M8 creation time (mem-027/mem-028). Immunized by `SnakeCaseTransportTests` (extended or sibling).
- [ ] **AC-12:** `Program.cs` has `services.AddTransient<McpMemoryService.Tools.MemoryRetrieveTool>();` after the M7 tool registrations + `[IMP:M8]` marker comment. The M2/M4/M5/M6/M7 scars + the `[IMP:9]` SUCCESS marker are preserved.

---

## Notes for @code (M8)

1. **The tool CATCHES — silent-fallback at the MCP boundary (ADR-005).** Identical to the M7 `MemoryCaptureTool`/`MemoryGetStatsTool` pattern, inverse of the M5/M6 service-level do-not-swallow invariant. `MemoryRetrieveTool.RetrieveAsync` wraps `_embedding.EmbedAsync` + `_qdrant.SearchAsync` in a single `try` and catches `Exception` → returns `new MemoryRetrieveOutput { Results = Array.Empty<MemoryRetrieveResult>() }`. The catch covers BOTH the embedding call and the Qdrant call — a failure in either is silent-fallback (ADR-005 doesn't distinguish which subsystem failed; memory is a capability enhancer, not a dependency). NO re-throw. Validation failures (`ArgumentException` from `InputValidator.ValidateRetrieve`) DO propagate — they signal caller misuse, not infrastructure failure; M10 `GlobalExceptionMiddleware` will turn them into JSON-RPC errors. The catch handler logs `[IMP:3][RetrieveAsync][FATAL]` at **Warning** level (not Error — silent-fallback is a designed-for degraded state, not a crash; the mcp server stays up). Mirror the M7 `MemoryGetStatsTool` catch-block log severity (`LogError` is acceptable too — `@code` picks; the M7 GetStatsTool uses `LogError` for its FATAL marker, so MATCH that for consistency).

2. **`[JsonPropertyName]` on the 3 retrieve DTOs is added AT M8 — preemptive rung-d (mem-027/mem-028).** M3 created these records WITHOUT `[JsonPropertyName]` (M3 predates the M7 rung-d fix). M8 EDITS them additively — add the attributes + a `BUG_FIX_CONTEXT: [scar — rung-d preemptive (M8)]` block + update the `[CHANGES]: LAST_CHANGE: M8` line. This applies the lesson from M7: never wait for QA to catch PascalCase leakage at the MCP transport boundary (mem-027 — the M7 snake_case deliverable was OMITTED, QA caught it at AC-4/AC-8 BLOCK; mem-028 — apply `[JsonPropertyName]` at creation time as the deterministic fallback, since SDK 1.4.0 has no serializer knob on `McpServerOptions`). DO NOT regress to attempting the `McpServerOptions.SerializerOptions` knob — the M7 rung-a/b probe already proved it's absent on SDK 1.4.0 (see the Program.cs M7 scar). The `using System.Text.Json.Serialization;` import is added once per file (the Output file has two records — one file-level using covers both).

3. **`InputValidator.ValidateRetrieve` is ALREADY fully implemented — DO NOT touch `InputValidator.cs`.** The M7 forward stub (InputValidator.cs L71-83) covers all M8 validation needs: `ArgumentNullException.ThrowIfNull(input)`, `string.IsNullOrWhiteSpace(input.Query)` → `ArgumentException`, `string.IsNullOrWhiteSpace(input.ProjectId)` → `ArgumentException`, `input.Limit < 1 || input.Limit > 10` → `ArgumentException`. The M8 spec §Algorithm step 2 ("Add retrieve validation to InputValidator — Ensure it is fully implemented") is ALREADY SATISFIED by the M7 forward stub. M8 `RetrieveAsync` calls `InputValidator.ValidateRetrieve(input);` as the FIRST line (before `[IMP:1]`), exactly mirroring M7 `MemoryCaptureTool.CaptureAsync` L86. No edit to `InputValidator.cs`. If `@code` discovers the stub is INCOMPLETE (e.g., a missing check), fix it as a MINOR edit with a `[CHANGES]` note — but per the verified source (InputValidator.cs L71-83), it is complete.

4. **Tools are INSTANCE classes with `[McpServerToolType]` + ctor DI (mem-006 SDK pattern).** NOT static classes. `[McpServerToolType]` on `MemoryRetrieveTool` + `[McpServerTool(Name = "memory_retrieve")]` on the instance method `RetrieveAsync` + ctor-injected `IEmbeddingService`/`IQdrantService`/`ILogger<MemoryRetrieveTool>`. The MCP SDK 1.4.0 `WithToolsFromAssembly()` (already swapped in by M7) discovers `[McpServerToolType]`-annotated classes, instantiates via DI on each `tools/call`, resolves ctor params from the DI container (`IEmbeddingService`+`IQdrantService`+`ILogger<>` all registered by M4/M5/M7), deserializes the `MemoryRetrieveInput` DTO param from the JSON-RPC `arguments` block via the `[JsonPropertyName]` overrides. `CancellationToken` is supplied by the SDK transport. Tests bypass the SDK entirely — ctor-direct construction with mocked services + direct method invocation (matches M7 `MemoryCaptureToolTests`).

5. **`MemoryPayload` property names — VERIFY against the realized source before compiling.** The `MapToRetrieveResult` helper reads `entry.Payload.AgentRole`, `.EntryType`, `.Content`, `.Timestamp`, `.Tags` (per M3 spec lines 167-184 + the M5 `PayloadMappingExtensions.ToQdrantPayload`/`FromQdrantPayload` round-trip). `@code` MUST read `src/McpMemoryService/Models/MemoryPayload.cs` and confirm the exact property names — if any differ (e.g., `CreatedAt` instead of `Timestamp`, or `TagList` instead of `Tags`), adjust the mapping. The mapping is mechanical — no business logic. The `MemoryRetrieveResult.PointId` is a `string` (the DTO contract), the `MemoryEntry.PointId` is a `Guid` → `entry.PointId.ToString("D")` for the lowercase-dashed v4 form.

6. **Program.cs scar discipline.** The M2 `WithListToolsHandler→WithToolsFromAssembly` swap scar (with the `[IMP:M7] supersede` one-liner), the `McpServerOptions` post-configure block scar (with the rung-d `[JsonPropertyName]` fallback note), the `MapMcp("/mcp")` scar, and the M7 `[IMP:M7][ConfigureServices][OPTION] Register MCP tools via WithToolsFromAssembly + snake_case serializer` comment are ALL preserved. M8's edit is STRICTLY ADDITIVE: one comment line + one `services.AddTransient<McpMemoryService.Tools.MemoryRetrieveTool>();` line appended after the M7 tool registrations (L163-164). Do NOT touch the M7 tool-registration lines, the M7 snake_case serializer options config, the existing DI lines (M4/M5/M6/M7), or the `[IMP:9][ConfigureServices][SUCCESS]` marker. `using McpMemoryService.Tools;` (already present from M7) covers the shorter form if `@code` prefers it — but MATCH the M7 style (fully-qualified `McpMemoryService.Tools.MemoryRetrieveTool`).

7. **HostSmokeTests scar discipline — preserve the M7 marker.** The M7 `[IMP:M7][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 2 tools (memory_capture, memory_get_stats)` comment at L154 is a SUCCESS scar (documents the M7 evolution point). M8 EXTENDS the count 2→3 and ADDS `memory_retrieve` containment — do NOT delete the M7 marker. Either: (a) insert `[IMP:M8]...3 tools (memory_capture, memory_get_stats, memory_retrieve)` ABOVE the `[IMP:M7]` line + add a `(extended by M8 from 2→3)` note on the M7 line, or (b) leave the `[IMP:M7]` line intact and add the `[IMP:M8]` line immediately after it with a one-liner explaining the extension. `@code` picks whichever reads cleanest — the historical truth (M7 established 2 tools; M8 extended to 3) MUST be preserved for future @debug/@qa archaeology. The Accept header setup, initialize handshake, SSE `ExtractDataFromSse` helper, `[Trait("Category","Integration")]`, existing `[IMP:1]/[IMP:2]/[IMP:3]` markers, Module header are ALL preserved — only the tools/list assertion block (L154-161) is touched.

8. **`Debug.Assert` on vector dimension — OPTIONAL.** `IEmbeddingService.Dimension => 384` (M4 invariant; OnnxEmbeddingService always produces 384-dim L2-normalized vectors). The tool trusts the contract — `IQdrantService.SearchAsync` expects a 384-dim vector per Qdrant collection config (384/Dot, ADR-011). A `Debug.Assert(vector.Length == _embedding.Dimension, "embedding dimension mismatch")` after `EmbedAsync` (debug-only, compiled out in Release) is acceptable and matches the M7 `MemoryCaptureTool` L102 pattern, but NOT required for any AC. `@code` includes it if the M7 precedent is being matched; otherwise skip.

9. **Catch-handler log severity — Warning vs Error.** ADR-005 silent-fallback is a DESIGNED-FOR degraded state, not a crash — the MCP server stays up and returns empty results. The M8 spec §Algorithm line 128 says "log [IMP:3] warning" — so `LogWarning` is the spec-faithful choice. HOWEVER, the M7 `MemoryGetStatsTool` catch handler uses `LogError(ex, "[IMP:2]...[FATAL]...")` (verified in `MemoryGetStatsTool.cs` L99) — for cross-tool consistency, `LogError` is also acceptable. `@code` picks whichever matches the existing tool-catch-handler convention (`LogError` is the M7 precedent; the M8 spec says "warning" — these are reconcilable: the `[FATAL]` step token in the log message marks the silent-fallback event regardless of severity). Recommended: use `LogWarning(ex, "[IMP:3][RetrieveAsync][FATAL] silent-fallback ADR-005 — returning empty Results projectId={ProjectId}", input.ProjectId)` — the `[FATAL]` token in the message preserves the LDD step-naming convention; `LogWarning` is the spec-faithful severity. If `@code` prefers `LogError` for cross-tool consistency with M7, that's also acceptable — the AC checks the `[IMP:3]` marker presence, not the severity.

10. **No `#pragma warning disable`.** Build warnings = AC-1 failure. Watch for: CS8625 nullable (the catch handler's `Array.Empty<MemoryRetrieveResult>()` — non-nullable `Results`, fine; `entry.Score ?? 0f` — null-coalesced, fine; `MemoryRetrieveResult.Tags` default `Array.Empty<string>()` from M3 — fine), IDE0005 unused usings (add `using System.Text.Json.Serialization;` ONLY in the DTO files that gain `[JsonPropertyName]`; add `using System.Linq;` in the tool file IF `.Select(...).ToList()` needs it — `ImplicitUsings` covers `System.Linq` since .NET 6, verify; if `System.Linq` resolves without the using, do NOT add it), CA1062 nullable argument validation (the `InputValidator.ValidateRetrieve` guards; the tool method does not — that's fine). The `using System.Diagnostics;` in the tool file is only needed if the optional `Debug.Assert` is included.

11. **Test categorisation — UNCATEGORISED for tool unit tests (~10..12 tests).** They use `Mock<IEmbeddingService>` + `Mock<IQdrantService>` + `NullLogger<T>.Instance` — NO real Qdrant/Docker/ONNX. They MUST run in the unit gate (`dotnet test --filter "Category!=Integration"`). Evolved unit count: M3 (16) + M5 mapping (5) + M6 (6) + M7 (≈10 capture/stats + SnakeCaseTransportTests) + **M8 (~10..12 retrieve)** = total green in the unit gate. HostSmokeTests stays `Category=Integration` (unchanged by M8) — its 2→3 assertion update lives in the integration-only path; @qa exercises it under the full gate with Docker Qdrant + ONNX model. The new `Tools/MemoryRetrieveToolTests.cs` does NOT need a `.csproj` edit — SDK auto-discovers (matches M7 `Tools/MemoryCaptureToolTests.cs` precedent).

12. **Decomposition decision — SINGLE `@code scope=impl:M8` dispatch.** 1 tool class (1 async method + 1 private mapper) + 3 DTO additive edits (`[JsonPropertyName]` only) + 1 Program.cs additive edit (1 DI line + 1 comment) + 1 HostSmokeTests assertion edit (count 2→3 + name containment + marker) + 1 test file (~10..12 tests). Cohesive — tool registration + DTO transport discipline + tool smoke assertion are atomic (splitting would yield a non-running intermediate state where `tools/list` advertises `memory_retrieve` but the DTO transport is PascalCase, or vice versa). Below the >5-new-methods decomposition threshold. **NO `## Decomposition` section** is appended to this plan.

13. **Do NOT run `dotnet test` (full) during the `@code` dispatch.** The M8 unit tests are mocking-only; the HostSmokeTests 2→3 assertion update requires Docker Qdrant (`QdrantCollectionInitializer` IHostedService) + the ONNX model (M4) to start successfully — the unit gate skips `Category=Integration`. The unit gate (`dotnet test --filter "Category!=Integration"`) is the `@code` return gate. @qa runs the full gate (with Docker) — including the updated HostSmokeTests assertion (`.Be(3)` + `memory_retrieve` containment). AC-3 (`tools/list` returns 3 tools) is runtime-verified by @qa under the full gate; @code source-verifies the assertion edit in the diff.

14. **AGENTS.md build commands.** `dotnet build McpMemoryService.sln` + `dotnet test --filter "Category!=Integration"` (`.slnx` is the .NET 10 default solution format; the legacy `.sln` alias auto-discovers it — AGENTS.md §2 documents `McpMemoryService.sln`). Existing M3 `DtoValidationTests.MemoryRetrieveInput_DefaultLimit_Is5` (tests/McpMemoryService.Tests/Models/DtoValidationTests.cs L35) MUST still pass after the `[JsonPropertyName]` edit — the test constructs `new MemoryRetrieveInput { Query = "q", ProjectId = "p" }` and asserts `Limit == 5` (the default is unchanged by the attribute addition; verify in the unit gate).

15. **profile.md consistency.** Plan prose is technical English (matches the M5/M6/M7 plan style). No Russian summary header required.

16. **Web search (optional) — none required for M8.** All M8 architectural decisions (instance `[McpServerToolType]`+`[McpServerTool]`+ctor DI per mem-006, silent-fallback at tool level per ADR-005, `[JsonPropertyName]` rung-d preemptive per mem-027/mem-028, `IQdrantService.SearchAsync` signature, `MemoryEntry`/`MemoryPayload` shape, `IEmbeddingService.EmbedAsync`+`Dimension`, HostSmokeTests 2→3 assertion update, Program.cs AddTransient additive) are derived from the M8 spec + SPEC §4.1/§7 + AGENTS.md ADR-001/002/005/006/010/011 + mem-006/027/028 + the realized M2..M7 source. The `IQdrantService.SearchAsync` signature (IQdrantService.cs L58-64) and the `MemoryEntry`/`MemoryPayload` record shapes are VERIFIED in the codebase (read before drafting this plan) — no `[UNVERIFIED_VERSION]` tags needed for M8. If `@code`/`@debug` discover a divergence (e.g., `MemoryPayload.Timestamp` named differently), the fix is a mechanical property-name adjustment in `MapToRetrieveResult` — documented in the bug-fix context, not web-searched.

---

## M9 — MCP tool: memory_compact

| Field | Value |
|---|---|
| Current Milestone | **M9 — MCP tool `memory_compact` (DI-injected `[McpServerToolType]`+`[McpServerTool]` tool: validate → `GetBatchForCompactAsync` → insufficient_data skip → `ILlmSummarizerService.SummarizeAsync` (catch `TaskCanceledException`/`HttpRequestException` as transactional guards, return `error` WITHOUT deleting sources) → generate embedding → `UpsertAsync` summary entry (`entry_type=summary`, `agent_role=orchestrator`, tags=["compact","summary"]) → `DeleteAsync` source entries → return `completed`)** |
| Status | PLAN_READY (awaiting `@code scope=impl:M9`) |
| Previous State | M7 — **SUCCESS** (committed as `cdd5bd8`). M8 — PLAN_READY (awaiting `@code`). `.test_counter.json` counter=0. M9 is the closure of the `M7 → {M8, M9}` parallel branch — the 4th and final MCP tool, completing the `tools/list` contract (capture, get_stats, retrieve, compact). DAG-next after M9: M10 (resilience/middleware). |
| Milestone Deps | **M3 (DONE — `MemoryCompactInput`/`MemoryCompactOutput` DTOs exist in `Contracts/`; created WITHOUT `[JsonPropertyName]` — M9 ADDS them per the M7 rung-d discipline; `MemoryPayload` record for the summary-entry payload), M4 (DONE — `IEmbeddingService.EmbedAsync(text, ct)` returns `float[384]` L2-normalized; `IEmbeddingService.Dimension => 384`; called for the LLM-produced summary text), M5 (DONE — `IQdrantService.GetBatchForCompactAsync(string projectId, int batchSize, CancellationToken ct)` returning `IReadOnlyList<MemoryEntry>` excluding `entry_type=summary` ordered by timestamp ASC, IQdrantService.cs L97-100; `IQdrantService.UpsertAsync(Guid pointId, float[] vector, MemoryPayload payload, CancellationToken ct)` L46; `IQdrantService.DeleteAsync(IEnumerable<Guid> pointIds, CancellationToken ct)` L80 — all three CRUD primitives realized in QdrantService M5), M6 (DONE — `ILlmSummarizerService.SummarizeAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken)` returning `Task<string>`; THROWS `TaskCanceledException` on 60s timeout + `HttpRequestException` on 5xx — the M9 tool MUST catch BOTH as transactional guards, NOT silent-fallback), M7 (DONE — tool registration pattern `[McpServerToolType]`+`[McpServerTool(Name=...)]` + ctor DI; `InputValidator.ValidateCompact` ALREADY fully implemented as a forward stub — `ProjectId` non-whitespace throws `ArgumentException`, `BatchSize < 1 || > 100` throws `ArgumentException`, InputValidator.cs L95-104; HostSmokeTools assertion `.Be(3)` (post-M8) + `toolNames.Should().Contain("memory_capture")`/`"memory_get_stats"`/`"memory_retrieve"`), M8 (DONE per plan scope — back-to-back parallel branch with M9; M9 extends `tools/list` 3→4 tools).** All deps DONE. |
| Dispatch Recommendation | **Single `@code scope=impl:M9` — NO decomposition.** 1 tool class (`MemoryCompactTool` — 1 async method `CompactAsync`) + 2 DTO edits (add `[JsonPropertyName]` snake_case overrides on `MemoryCompactInput` + `MemoryCompactOutput` per the M7 rung-d discipline applied at M9 edit time) + 1 Program.cs edit (add `services.AddTransient<McpMemoryService.Tools.MemoryCompactTool>();` after the M8 tool registration) + 1 HostSmokeTests edit (3→4 tools: `.Be(3)`→`.Be(4)` + add `toolNames.Should().Contain("memory_compact")` + update/add `[IMP:M9]` marker) + 1 test file (`MemoryCompactToolTests.cs` — ~10 unit tests). `InputValidator.ValidateCompact` is ALREADY fully implemented (M7 forward stub) — M9 just CALLS it, does NOT touch `InputValidator.cs`. Below the >5-new-methods decomposition threshold. Cohesive — tool + transport discipline + smoke assertion are atomic. **NO `## Decomposition` section** is appended. |
| Etap | Etap 1 (implement M1..M12). Etap 2 future. |

## ADRs Touched by M9

| ADR | Decision | M9 Action |
|---|---|---|
| **ADR-003** | Compact semantics = hard delete | **CRITICAL:** After `ILlmSummarizerService.SummarizeAsync` succeeds AND the summary entry is upserted, M9 calls `IQdrantService.DeleteAsync(sourcePointIds)` — PHYSICAL deletion of source entries from Qdrant. Source entries are NOT retained; the upserted `entry_type=summary` point (with `agent_role=orchestrator`, tags `["compact","summary"]`) preserves the essence. This is irreversible — the transactional guarantee (ADR-005 below) is the guard: delete ONLY runs after summarize+embed+upsert all succeed. |
| **ADR-004** | Non-blocking compact on insufficient data | When `entries.Count < input.BatchSize`, `CompactAsync` returns `new MemoryCompactOutput { Status = "skipped", Reason = "insufficient_data", Available = entries.Count, Required = input.BatchSize }` — this is **NOT an error**, it is a designed-for skipped state. `ILlmSummarizerService` is NOT called. `IQdrantService.UpsertAsync`/`DeleteAsync` are NOT called. Per SPEC §7 + SPEC §4.4 step 3 + M1 correction S8: aligns with non-blocking philosophy; prevents MCP connection breaks. |
| **ADR-005** | Silent fallback on Qdrant/ONNX/LLM unavailability | **M9 inverts the M6 do-not-swallow rule at the LAYER boundary ONLY.** Inside `QdrantService`/`LlmSummarizerService` exceptions PROPAGATE (M5/M6 do-not-swallow invariant). The M9 tool catches specifically-named exceptions as TRANSACTIONAL GUARDS — NOT silent fallback: `TaskCanceledException` (LLM 60s timeout) → return `Status="error", Reason="llm_timeout"`, source entries NOT deleted; `HttpRequestException` (LLM 5xx) → return `Status="error", Reason="llm_5xx"`, source entries NOT deleted. These ARE returned to the MCP caller (status=error is a structured response, NOT a connection-breaking exception — the tool does NOT re-throw). For Qdrant/Embedding failures DURING the post-LLM upsert/embed phase, the tool catches `Exception` and returns `Status="error"` WITHOUT deleting sources (transactional — partial write to summary without deleting sources is acceptable; sources remain, summary may exist; idempotent on retry). NO exception escapes `CompactAsync` — the MCP Streamable-HTTP connection MUST NOT break. This is the inverse of the M6 service-level do-not-swallow (LlmSummarizerService still THROWS internally; the M9 tool catches at the boundary). |
| **ADR-010** | MCP SDK = ModelContextProtocol 1.4.0 | `WithToolsFromAssembly()` (swapped in by M7) already discovers `[McpServerToolType]`-annotated classes. M9 ADDS one more `[McpServerToolType]` class (`MemoryCompactTool`) + one `services.AddTransient<...MemoryCompactTool>();` DI line. The assembly-scan auto-discovers the 4th tool — NO `Program.cs` `WithToolsFromAssembly()` call edit needed (only the AddTransient DI line so ctor params resolve: `IQdrantService`+`ILlmSummarizerService`+`IEmbeddingService`+`ILogger<>`). mem-006 pattern. |
| **ADR-001** | entry_type taxonomy = 6 types; `summary` is one of them | The summary entry M9 upserts carries `EntryType = EntryType.Summary`. This is the ONLY pathway that produces `entry_type=summary` entries — capture FORBIDS `EntryType.Summary` (InputValidator.ValidateCapture L43-44 rejects it). `GetBatchForCompactAsync` EXCLUDES `entry_type=summary` (M5 invariant — prevents compacting its own summaries, infinite recursion guard). |
| **ADR-002** | agent_role = 5 roles incl orchestrator | The summary entry M9 upserts carries `AgentRole = AgentRole.Orchestrator` — compact is orchestrator-driven. This populates Qdrant with orchestrator-attributed summary points; subsequent `memory_retrieve` calls with `agent_role_filter=orchestrator` will surface them. |
| **ADR-006** | .NET 10 target | net10.0 already confirmed. `[McpServerToolType]`/`[McpServerTool]` from ModelContextProtocol 1.4.0 (pinned in csproj from M2). No new package refs. |
| **ADR-011** | ONNX 384-dim | `MemoryCompactTool` calls `_embedding.EmbedAsync(summary)` on the LLM-produced summary text → 384-dim L2-normalized vector, passed to `IQdrantService.UpsertAsync(summaryPointId, summaryVector, summaryPayload, ct)`. OnnxEmbeddingService ALREADY L2-normalizes (M4 invariant) — the tool does NOT re-normalize. The summary vector co-exists with captured-entry vectors in the same Qdrant collection (384/Dot). |

> **M9 invariants (must NOT regress):**
> 1. **Transactional guarantee — LLM MUST succeed before any delete (ADR-003).** `CompactAsync` calls `ILlmSummarizerService.SummarizeAsync` inside a `try` that catches `TaskCanceledException` (→ `Status="error", Reason="llm_timeout"`) and `HttpRequestException` (→ `Status="error", Reason="llm_5xx"`). On either catch: RETURN the error response IMMEDIATELY — `IQdrantService.DeleteAsync` is NEVER reached. Source entries survive. The `catch` blocks do NOT fall through to the upsert/delete code. This is the PRIMARY safety invariant of M9; tests verify `DeleteAsync.Verify(Times.Never)`.
> 2. **Non-blocking — insufficient_data is NOT an error (ADR-004).** When `entries.Count < input.BatchSize`, `CompactAsync` returns `Status="skipped"` with `Reason="insufficient_data"` + `Available` + `Required`. This is a structured non-blocking response, NOT a throw, NOT `Status="error"`. `SummarizeAsync`/`UpsertAsync`/`DeleteAsync` are all NOT called (verified by `Mock.Verify(Times.Never)`).
> 3. **Silent-fallback catches `Exception` from Qdrant/Embedding in the post-LLM phase — BUT specifically catches `TaskCanceledException`/`HttpRequestException` from LLM as transactional guards.** The LLM phase (`SummarizeAsync`) is guarded by NAMED catches → structured `error` status WITHOUT source deletion. The post-LLM phase (embed + upsert + delete) is wrapped in a `try { ... } catch (Exception ex) { return Status="error", Reason="qdrant_or_embedding_failure", Error=ex.Message }` — sources are NOT deleted on a post-LLM-phase failure (delete is the LAST step; if embed/upsert fails, delete is never reached). NO exception escapes `CompactAsync` — MCP connection integrity preserved (ADR-005 boundary inverse of M5/M6 do-not-swallow).
> 4. **Tools are INSTANCE classes with `[McpServerToolType]`+ctor DI (mem-006).** `[McpServerToolType]` on `MemoryCompactTool` + `[McpServerTool(Name = "memory_compact")]` on the instance method `CompactAsync` + ctor-injected `IQdrantService`/`ILlmSummarizerService`/`IEmbeddingService`/`ILogger<MemoryCompactTool>`. SDK 1.4.0 `WithToolsFromAssembly()` instantiates via DI on `tools/call`, resolves ctor params. Tests bypass the SDK — ctor-direct with mocked services (matches M7 `MemoryCaptureToolTests`/M8 `MemoryRetrieveToolTests`).
> 5. **`[JsonPropertyName]` on compact DTOs is added AT M9 (rung-d preemptive, mem-027/mem-028).** The M3 `MemoryCompactInput`/`MemoryCompactOutput` records exist WITHOUT `[JsonPropertyName]` (M3 predates the M7 rung-d fix). M9 EDITS them additively: `MemoryCompactInput` → `project_id`, `batch_size`; `MemoryCompactOutput` → `status`, `reason`, `available`, `required`, `source_count`, `summary_point_id`, `error`. Add `using System.Text.Json.Serialization;` + a `BUG_FIX_CONTEXT: [scar — rung-d preemptive (M9)]` block + update the `[CHANGES]: LAST_CHANGE: M9` line. Do NOT regress to the absent `McpServerOptions.SerializerOptions` knob (absent on SDK 1.4.0 — M7 probe).
> 6. **`InputValidator.ValidateCompact` is ALREADY fully implemented — M9 just CALLS it.** Do NOT touch `src/McpMemoryService/Validation/InputValidator.cs` — the M7 forward stub (L95-104: `ProjectId` non-whitespace, `BatchSize in [1,100]`, throws `ArgumentException`) is COMPLETE. `CompactAsync` calls `InputValidator.ValidateCompact(input);` as the FIRST line (before any `[IMP:1]` log) — validation errors PROPAGATE (caller misuse), never swallowed.
> 7. **LDD markers `[IMP:1]`..`[IMP:4]` per the M9 spec.** `[IMP:1][CompactAsync][PROGRESS] batch fetched count=N` — after `GetBatchForCompactAsync`. `[IMP:2][CompactAsync][SUCCESS] LLM summarization complete len=L` PLUS `[IMP:2][CompactAsync][FATAL] LLM timeout/5xx` in the catch blocks (same IMP index, different step token — the M9 spec assigns both the success + failure LLM logs to IMP:2). `[IMP:3][CompactAsync][SUCCESS] summary captured pointId=...` — after `UpsertAsync`. `[IMP:4][CompactAsync][SUCCESS] source entries deleted count=N` — after `DeleteAsync`. The catch for the post-LLM phase logs `[IMP:3][CompactAsync][FATAL]` (embed/upsert failure) or `[IMP:4][CompactAsync][FATAL]` (delete failure — uncommon; if delete fails after upsert, sources remain + summary exists; idempotent). Distinct from `IQdrantService`'s own IMP markers (different method-name token `CompactAsync`).

---

## PURPOSE

Implement the compaction tier of McpMemoryService — the critical transactional operation that bounds Qdrant storage growth: a `MemoryCompactTool` (instance class, `[McpServerToolType]`+`[McpServerTool(Name = "memory_compact")]`) that validates `MemoryCompactInput` via the existing `InputValidator.ValidateCompact` (ProjectId required, BatchSize in [1, 100]), fetches a batch of old non-summary entries via `IQdrantService.GetBatchForCompactAsync(projectId, batchSize, ct)` (excludes `entry_type=summary`, ordered by timestamp ASC), and if `entries.Count < batchSize` returns `MemoryCompactOutput { Status = "skipped", Reason = "insufficient_data", Available = entries.Count, Required = batchSize }` (non-blocking per ADR-004 — LLM/Qdrant NOT touched). On sufficient data: extracts the contents list, calls `ILlmSummarizerService.SummarizeAsync(contents, ct)` inside a `try` that catches `TaskCanceledException` (60s timeout → `Status="error"`, `Reason="llm_timeout"`, sources NOT deleted) and `HttpRequestException` (5xx → `Status="error"`, `Reason="llm_5xx"`, sources NOT deleted) — the TRANSACTIONAL GUARANTEES that prevent data loss when the LLM is unavailable (ADR-005 boundary inverse of M6 do-not-swallow). On LLM success: generates a 384-dim L2-normalized embedding for the summary text via `IEmbeddingService.EmbedAsync(summary, ct)`, upserts a new Qdrant point (`Guid.NewGuid()`, payload with `entry_type=summary`, `agent_role=orchestrator`, `session_id="compact"`, `timestamp=now`, `tags=["compact","summary"]`, `metadata.Session="compact-YYYYMMDD"`) via `IQdrantService.UpsertAsync`, hard-deletes the source entries via `IQdrantService.DeleteAsync(sourcePointIds)` (ADR-003 physical deletion — irreversible, but the summary preserves the essence), and returns `MemoryCompactOutput { Status = "completed", SourceCount = entries.Count, SummaryPointId = summaryPointId.ToString() }`. The post-LLM embed+upsert+delete phase is wrapped in a `try { ... } catch (Exception ex) { return Status="error", Reason="qdrant_or_embedding_failure" }` — sources are NOT deleted on a post-LLM failure (delete is the LAST step; idempotent on retry). NO exception escapes `CompactAsync` — the MCP Streamable-HTTP connection MUST NOT break (ADR-005). Also: edit the two M3 compact DTOs (`MemoryCompactInput`, `MemoryCompactOutput`) to add `[JsonPropertyName("snake_case")]` transport overrides per the M7 rung-d discipline (mem-027/mem-028 — apply at edit time, never wait for QA); add `services.AddTransient<McpMemoryService.Tools.MemoryCompactTool>();` to `Program.cs` after the M8 tool registration; update `HostSmokeTests` tools/list assertion from 3→4 tools plus `memory_compact` name containment; write `tests/McpMemoryService.Tests/Tools/MemoryCompactToolTests.cs` with ~10 unit tests (mocked `IQdrantService`+`ILlmSummarizerService`+`IEmbeddingService`+`NullLogger<T>`, no Docker/ONNX/Qdrant/LLM). Extend `SnakeCaseTransportTests` with compact-DTO round-trip cases. This is the 4th and FINAL MCP tool — M9 completes the `tools/list` contract and closes the `M7 → {M8, M9}` parallel branch.

---

## 1. Draft Code Graph

> M9 adds the fourth and final **MCP tool** (after M7's `MemoryCaptureTool`/`MemoryGetStatsTool` + M8's `MemoryRetrieveTool`). `MemoryCompactTool` follows the M7/M8 instance-class + ctor-DI pattern verbatim — `[McpServerToolType]`+`[McpServerTool]`, ctor params `IQdrantService`+`ILlmSummarizerService`+`IEmbeddingService`+`ILogger<>`. The two M3 compact DTOs get an additive `[JsonPropertyName]` edit (no logic change). `Program.cs` gets one additive `AddTransient` line. `HostSmokeTests` gets the tools/list assertion bumped 3→4. `csharp-conventions` `#region` structuring applies (Fields / Constructors / CompactAsync).

```xml
<DraftCodeGraph>
  <!-- ========== TOOL ========== -->
  <src_McpMemoryService_Tools_MemoryCompactTool_cs FILE="src/McpMemoryService/Tools/MemoryCompactTool.cs" TYPE="MCP_TOOL">
    <keywords>MemoryCompactTool, McpServerToolType, McpServerTool, memory_compact, IQdrantService, ILlmSummarizerService, IEmbeddingService, GetBatchForCompactAsync, SummarizeAsync, EmbedAsync, UpsertAsync, DeleteAsync, transactional, ADR-003/004/005, ILogger, IMP:1/IMP:2/IMP:3/IMP:4</keywords>
    <annotation>public sealed class MemoryCompactTool. MODULE_CONTRACT header per csharp-conventions (matches MemoryCaptureTool + MemoryGetStatsTool + MemoryRetrieveTool style). #region Fields: IQdrantService _qdrantService (readonly), ILlmSummarizerService _llmSummarizerService (readonly), IEmbeddingService _embeddingService (readonly), ILogger&lt;MemoryCompactTool&gt; _logger (readonly). #region Constructors: ctor(IQdrantService, ILlmSummarizerService, IEmbeddingService, ILogger&lt;MemoryCompactTool&gt;) — assign fields. #region CompactAsync: the single async method, annotated [McpServerTool(Name = "memory_compact")]. Signature: Task&lt;MemoryCompactOutput&gt; CompactAsync(MemoryCompactInput input, CancellationToken cancellationToken = default). Body: (1) InputValidator.ValidateCompact(input) — throws ArgumentException, PROPAGATES (not swallowed). (2) var entries = await _qdrantService.GetBatchForCompactAsync(input.ProjectId, input.BatchSize, cancellationToken); _logger.LogInformation("[IMP:1][CompactAsync][PROGRESS] batch fetched count={Count}", entries.Count). (3) if (entries.Count &lt; input.BatchSize) return new MemoryCompactOutput { Status="skipped", Reason="insufficient_data", Available=entries.Count, Required=input.BatchSize }. (4) var contents = entries.Select(e =&gt; e.Payload.Content).ToList(). (5) string summary; try { summary = await _llmSummarizerService.SummarizeAsync(contents, cancellationToken); _logger.LogInformation("[IMP:2][CompactAsync][SUCCESS] LLM summarization complete len={Len}", summary.Length); } catch (TaskCanceledException ex) { _logger.LogWarning(ex, "[IMP:2][CompactAsync][FATAL] LLM timeout"); return new MemoryCompactOutput { Status="error", Reason="llm_timeout", Error=ex.Message }; } catch (HttpRequestException ex) { _logger.LogWarning(ex, "[IMP:2][CompactAsync][FATAL] LLM 5xx"); return new MemoryCompactOutput { Status="error", Reason="llm_5xx", Error=ex.Message }; }. (6) try { var summaryPointId = Guid.NewGuid(); var summaryVector = await _embeddingService.EmbedAsync(summary, cancellationToken); var summaryPayload = new MemoryPayload { ProjectId=input.ProjectId, SessionId="compact", AgentRole=AgentRole.Orchestrator, EntryType=EntryType.Summary, Timestamp=DateTimeOffset.UtcNow, Content=summary, Tags=new[]{"compact","summary"}, Metadata=new Metadata { Session=$"compact-{DateTimeOffset.UtcNow:yyyyMMdd}" } }; await _qdrantService.UpsertAsync(summaryPointId, summaryVector, summaryPayload, cancellationToken); _logger.LogInformation("[IMP:3][CompactAsync][SUCCESS] summary captured pointId={PointId}", summaryPointId); var sourceIds = entries.Select(e =&gt; e.PointId).ToList(); await _qdrantService.DeleteAsync(sourceIds, cancellationToken); _logger.LogInformation("[IMP:4][CompactAsync][SUCCESS] source entries deleted count={Count}", sourceIds.Count); return new MemoryCompactOutput { Status="completed", SourceCount=entries.Count, SummaryPointId=summaryPointId.ToString() }; } catch (Exception ex) { _logger.LogError(ex, "[IMP:3][CompactAsync][FATAL] post-LLM phase (embed/upsert/delete) failed — sources NOT deleted (transactional ADR-003)"); return new MemoryCompactOutput { Status="error", Reason="qdrant_or_embedding_failure", Error=ex.Message }; }. NO exception escapes CompactAsync.</annotation>
    <src_McpMemoryService_Tools_MemoryCompactTool_qdrantService_FIELD NAME="_qdrantService" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryCompactTool_llmSummarizerService_FIELD NAME="_llmSummarizerService" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryCompactTool_embeddingService_FIELD NAME="_embeddingService" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryCompactTool_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Tools_MemoryCompactTool_CompactAsync_METHOD NAME="CompactAsync" TYPE="PUBLIC_ASYNC_METHOD" MCP_TOOL_NAME="memory_compact" IMP="IMP:1,IMP:2,IMP:3,IMP:4">
      <annotation>1. InputValidator.ValidateCompact(input) — first line, throws ArgumentException (propagates). 2. GetBatchForCompactAsync → [IMP:1] log count. 3. if count &lt; batchSize → skipped (non-blocking ADR-004). 4. Extract contents. 5. SummarizeAsync try: [IMP:2] success; catch TaskCanceledException → error/llm_timeout (sources NOT deleted); catch HttpRequestException → error/llm_5xx (sources NOT deleted). 6. post-LLM try: NewGuid → EmbedAsync(summary) → MemoryPayload (summary/orchestrator/compact+summary tags) → UpsertAsync → [IMP:3] log summary PointId → select sourceIds → DeleteAsync → [IMP:4] log deleted count → return completed (SourceCount, SummaryPointId). 7. post-LLM catch (Exception): log [IMP:3][FATAL], return error/qdrant_or_embedding_failure (sources NOT deleted — delete never reached on embed/upsert failure). NOTE: summaryPointId.ToString() default format is "D" (lowercase-dashed) — matches M7/M8 GUID convention; use explicit .ToString("D") for clarity.</annotation>
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Contracts_MemoryCompactInput_cs" TYPE="CONSUMES_INPUT_DTO" />
        <Link TARGET="src_McpMemoryService_Contracts_MemoryCompactOutput_cs" TYPE="PRODUCES_OUTPUT_DTO" />
        <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Services_ILlmSummarizerService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Services_IEmbeddingService_cs" TYPE="INJECTS" />
        <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="CALLS_VALIDATOR" />
        <Link TARGET="src_McpMemoryService_Models_MemoryPayload_cs" TYPE="COMPOSES_SUMMARY_PAYLOAD" />
        <Link TARGET="src_McpMemoryService_Models_Metadata_cs" TYPE="COMPOSES_SUMMARY_METADATA" />
        <Link TARGET="src_McpMemoryService_Models_MemoryEntry_cs" TYPE="READS_FOR_CONTENT_AND_POINTID" />
      </CrossLinks>
    </src_McpMemoryService_Tools_MemoryCompactTool_CompactAsync_METHOD>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="SIBLING_TOOL_PATTERN" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="SIBLING_TOOL_PATTERN" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="SIBLING_TOOL_PATTERN" />
      <Link TARGET="memory_compact_MCP_SPEC_PLANNED" TYPE="RESOLVES" />
    </CrossLinks>
  </src_McpMemoryService_Tools_MemoryCompactTool_cs>

  <!-- ========== DTO EDITS (additive [JsonPropertyName]) ========== -->
  <src_McpMemoryService_Contracts_MemoryCompactInput_cs FILE="src/McpMemoryService/Contracts/MemoryCompactInput.cs" TYPE="CONTRACT_RECORD_EDIT">
    <annotation>EDIT the M3 record (exists WITHOUT [JsonPropertyName], 32 lines) — ADD per-property [JsonPropertyName] snake_case overrides per M7 rung-d discipline (mem-027/mem-028 — apply at edit time, never wait for QA). Properties → JSON keys: ProjectId → "project_id"; BatchSize → "batch_size". Add `using System.Text.Json.Serialization;`. Add a BUG_FIX_CONTEXT scar block mirroring the M7/M8 capture/retrieve DTO scar text: `// BUG_FIX_CONTEXT: [scar — rung-d preemptive (M9): snake_case names fixed per-property at M9 creation/edit time per mem-027/mem-028. Immunized by SnakeCaseTransportTests]`. Update MODULE_CONTRACT [CHANGES]: `LAST_CHANGE: M9 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 discipline); M3 initial creation.`. The record shape, property types, `required` modifiers, and `BatchSize = 20` default are UNCHANGED — only attributes added.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="CONSUMED_BY" />
      <Link TARGET="tests_McpMemoryService_Tests_Models_SnakeCaseTransportTests_cs" TYPE="IMMUNIZED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Contracts_MemoryCompactInput_cs>

  <src_McpMemoryService_Contracts_MemoryCompactOutput_cs FILE="src/McpMemoryService/Contracts/MemoryCompactOutput.cs" TYPE="CONTRACT_RECORD_EDIT">
    <annotation>EDIT the M3 record (exists WITHOUT [JsonPropertyName], 52 lines) — ADD per-property [JsonPropertyName] snake_case overrides: Status → "status"; Reason → "reason"; Available → "available"; Required → "required"; SourceCount → "source_count"; SummaryPointId → "summary_point_id"; Error → "error". Add `using System.Text.Json.Serialization;`. Same BUG_FIX_CONTEXT scar block text. Update MODULE_CONTRACT [CHANGES]: `LAST_CHANGE: M9 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 discipline); M3 initial creation per SPEC §4.4 + S8.`. Record shape/types/required UNCHANGED — only attributes added. NOTE: the nullable fields (Reason/Available/Required/SourceCount/SummaryPointId/Error are `int?`/`string?`) serialize to `null` when unset — the SDK's System.Text.Json default skips nulls only if `DefaultIgnoreCondition = WhenNull` is set; the M7 Program.cs McpServerOptions post-configure scar does NOT set this, so nulls ARE serialized as `"reason":null` etc. This is the M3-contract-faithful behavior (the DTO comment documents the branched shape) — do NOT add a `[JsonIgnore]` or `WhenNull` condition; the caller reads `Status` first and only the relevant branch fields are populated.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="PRODUCED_BY" />
      <Link TARGET="tests_McpMemoryService_Tests_Models_SnakeCaseTransportTests_cs" TYPE="IMMUNIZED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Contracts_MemoryCompactOutput_cs>

  <!-- ========== PROGRAM.CS EDIT (additive DI line) ========== -->
  <src_McpMemoryService_Program_cs_ConfigureServices_M9_EDIT FILE="src/McpMemoryService/Program.cs" TYPE="DI_EDIT">
    <annotation>ADDITIVE — append one DI line IMMEDIATELY AFTER the M8 tool registration (the M8 line is `services.AddTransient&lt;McpMemoryService.Tools.MemoryRetrieveTool&gt;();`):
  // [IMP:M9][ConfigureServices][OPTION] Register memory_compact MCP tool (Transient) — ADR-003 transactional, ADR-004 non-blocking skip, ADR-005 LLM named-catches, ADR-010 WithToolsFromAssembly auto-discovery
  services.AddTransient&lt;McpMemoryService.Tools.MemoryCompactTool&gt;();
STRICT ADDITIVE: do NOT touch the M7/M8 tool-registration lines, the M2 BUG_FIX_CONTEXT scars (McpServerOptions post-configure, `.WithToolsFromAssembly()` swap marker, `MapMcp("/mcp")`), the M4 `IEmbeddingService` line, the M5 `IQdrantService`/`QdrantCollectionInitializer` lines, the M6 `LlamaCpp` HttpClient + `ILlmSummarizerService` lines, or the `[IMP:9][ConfigureServices][SUCCESS]` marker. `WithToolsFromAssembly()` already scans the assembly — adding `[McpServerToolType]` on `MemoryCompactTool` + the AddTransient DI line is SUFFICIENT (4th tool auto-discovered). No `using McpMemoryService.Tools;` edit (already present from M7).</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="REGISTERS" />
    </CrossLinks>
  </src_McpMemoryService_Program_cs_ConfigureServices_M9_EDIT>

  <!-- ========== HOSTSMOKETESTS EDIT (3→4 tools assertion) ========== -->
  <tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs_M9_EDIT FILE="tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs" TYPE="TEST_ASSERTION_EDIT">
    <annotation>EDIT the M8 assertion block (post-M8: `toolsArray.GetArrayLength().Should().Be(3)` + `toolNames.Should().Contain("memory_capture")`/`"memory_get_stats"`/`"memory_retrieve"`): change `.Be(3)` → `.Be(4)`; the 3 existing `toolNames.Should().Contain(...)` lines STAY; ADD `toolNames.Should().Contain("memory_compact", "M9 adds memory_compact to tools/list (4th and final tool)");`. UPDATE/ADD an `[IMP:M9][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 4 tools (memory_capture, memory_get_stats, memory_retrieve, memory_compact)` comment (preserve the M7/M8 marker scars — same approach as M8's treatment of the M7 scar: do NOT delete, add the M9 line alongside with an extension note). PRESERVE: the Accept header setup, the initialize handshake, the SSE ExtractDataFromSse helper, the BUG_FIX_CONTEXT scars, the [Trait("Category","Integration")] classification, the existing [IMP:1]/[IMP:2]/[IMP:3] markers, the Module header. HostSmokeTests stays Category=Integration — runtime proof requires Docker Qdrant + ONNX model + llama.cpp (M6 LlmSummarizerService for compact's LLM phase, though the smoke only verifies tools/list discovery, not a compact invocation).</annotation>
    <CrossLinks>
      <Link TARGET="tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="VERIFIES_DISCOVERY" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Smoke_HostSmokeTests_cs_M9_EDIT>

  <!-- ========== TESTS ========== -->
  <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_cs FILE="tests/McpMemoryService.Tests/Tools/MemoryCompactToolTests.cs" TYPE="XUNIT_TEST">
    <keywords>MemoryCompactTool, CompactAsync, Mock IQdrantService, Mock ILlmSummarizerService, Mock IEmbeddingService, NullLogger, no real Qdrant/ONNX/LLM, unit, snake_case transport, transactional, insufficient_data, llm_timeout, llm_5xx, hard delete, IMP markers</keywords>
    <annotation>~10 unit tests, UNCATEGORISED (no [Trait], no Category=Integration — no real Qdrant/Docker/ONNX/LLM required). Construct MemoryCompactTool via ctor with Mock&lt;IQdrantService&gt; + Mock&lt;ILlmSummarizerService&gt; + Mock&lt;IEmbeddingService&gt; + NullLogger&lt;MemoryCompactTool&gt;.Instance — NO MCP transport (ctor-direct, matches M7/M8 pattern). Mock IQdrantService.GetBatchForCompactAsync returns a canned IReadOnlyList&lt;MemoryEntry&gt; — build MemoryEntry records with PointId=Guid.NewGuid(), Payload=new MemoryPayload { ProjectId, AgentRole, EntryType, Content, Timestamp, Tags, Metadata }, Vector=new float[384] (unused), Score=null (GetBatchForCompactAsync does not score). Mock ILlmSummarizerService.SummarizeAsync returns a canned string ("summary text"). Mock IEmbeddingService.EmbedAsync returns a canned float[384]. Tests (per M9 spec lines 184-267): 1) ExecuteAsync_SufficientData_CompletesAndDeletesSources — mock Qdrant returns 20 entries, mock LLM returns "summary", mock Embedding returns float[384] → Assert Status=="completed", SourceCount==20, SummaryPointId is a GUID; DeleteAsync called with the 20 source PointIds (Verify Times.Once with the exact Guid list); UpsertAsync called once (for the summary). 2) ExecuteAsync_InsufficientData_ReturnsSkippedNonBlocking — mock Qdrant returns 5 entries (batchSize=20) → Assert Status=="skipped", Reason=="insufficient_data", Available==5, Required==20; LLM NOT called (Times.Never), Delete NOT called (Times.Never), Upsert NOT called (Times.Never). 3) ExecuteAsync_LlmTimeout_ReturnsErrorSourceNotDeleted — mock LLM throws TaskCanceledException → Assert Status=="error", Reason=="llm_timeout"; DeleteAsync NOT called (Times.Never), UpsertAsync NOT called (Times.Never) — TRANSACTIONAL SAFETY. 4) ExecuteAsync_Llm5xx_ReturnsErrorSourceNotDeleted — mock LLM throws HttpRequestException → Assert Status=="error", Reason=="llm_5xx"; DeleteAsync NOT called (Times.Never). 5) ExecuteAsync_SummaryEntryHasCorrectPayload — mock returns 20 entries, LLM "summary" → Verify UpsertAsync called with payload where EntryType==Summary, AgentRole==Orchestrator, Tags contains "compact" and "summary", Content==LLM response. 6) ExecuteAsync_EmptyProjectId_ThrowsValidation — Assert.ThrowsAsync&lt;ArgumentException&gt;. 7) ExecuteAsync_BatchSizeZero_ThrowsValidation — Assert.ThrowsAsync&lt;ArgumentException&gt;. 8) ExecuteAsync_BatchSizeOver100_ThrowsValidation — Assert.ThrowsAsync&lt;ArgumentException&gt;. 9) ExecuteAsync_GeneratesEmbeddingForSummary — mock LLM "summary text" → Verify EmbedAsync called with "summary text". 10) ExecuteAsync_ExcludesSummaryFromBatch — Verify GetBatchForCompactAsync called with input.ProjectId + input.BatchSize (M5 guarantees exclusion of entry_type=summary internally; M9 just verifies the call propagates the args). Optional 11) ExecuteAsync_PostLlmEmbeddingFails_ReturnsErrorSourcesNotDeleted — mock Embedding throws InvalidOperationException → Assert Status=="error", Reason=='qdrant_or_embedding_failure' OR null; DeleteAsync NOT called — transactional (upsert may have happened, sources remain; idempotent on retry). Optional 12) ExecuteAsync_PostLlmUpsertFails_ReturnsErrorSourcesNotDeleted — mock UpsertAsync throws → sources NOT deleted. Optional 13) ExecuteAsync_PostLlmDeleteFails_ReturnsError — uncommon; summary upserted + delete throws → Status=="error", Error=ex.Message; this is the leakiest path (summary exists + sources exist — dedup on next compact via SessionId="compact" + timestamp); ASSERT the tool does NOT re-throw (connection preserved).</annotation>
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_SufficientData_CompletesAndDeletesSources_METHOD NAME="ExecuteAsync_SufficientData_CompletesAndDeletesSources" TYPE="TEST_METHOD" IMP="IMP:1,IMP:2,IMP:3,IMP:4" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_InsufficientData_ReturnsSkippedNonBlocking_METHOD NAME="ExecuteAsync_InsufficientData_ReturnsSkippedNonBlocking" TYPE="TEST_METHOD" IMP="IMP:1" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_LlmTimeout_ReturnsErrorSourceNotDeleted_METHOD NAME="ExecuteAsync_LlmTimeout_ReturnsErrorSourceNotDeleted" TYPE="TEST_METHOD" IMP="IMP:2" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_Llm5xx_ReturnsErrorSourceNotDeleted_METHOD NAME="ExecuteAsync_Llm5xx_ReturnsErrorSourceNotDeleted" TYPE="TEST_METHOD" IMP="IMP:2" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_SummaryEntryHasCorrectPayload_METHOD NAME="ExecuteAsync_SummaryEntryHasCorrectPayload" TYPE="TEST_METHOD" IMP="IMP:3" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_EmptyProjectId_ThrowsValidation_METHOD NAME="ExecuteAsync_EmptyProjectId_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_BatchSizeZero_ThrowsValidation_METHOD NAME="ExecuteAsync_BatchSizeZero_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_BatchSizeOver100_ThrowsValidation_METHOD NAME="ExecuteAsync_BatchSizeOver100_ThrowsValidation" TYPE="TEST_METHOD" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_GeneratesEmbeddingForSummary_METHOD NAME="ExecuteAsync_GeneratesEmbeddingForSummary" TYPE="TEST_METHOD" IMP="IMP:3" />
    <tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_ExecuteAsync_ExcludesSummaryFromBatch_METHOD NAME="ExecuteAsync_ExcludesSummaryFromBatch" TYPE="TEST_METHOD" IMP="IMP:1" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Contracts_MemoryCompactInput_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Contracts_MemoryCompactOutput_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Validation_InputValidator_cs" TYPE="EXERCISES_VALIDATECompact" />
      <Link TARGET="src_McpMemoryService_Services_IQdrantService_cs" TYPE="MOCKS" />
      <Link TARGET="src_McpMemoryService_Services_ILlmSummarizerService_cs" TYPE="MOCKS" />
      <Link TARGET="src_McpMemoryService_Services_IEmbeddingService_cs" TYPE="MOCKS" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Tools_MemoryCompactToolTests_cs>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow

> `@code` execution algorithm for `scope=impl:M9`. Source: M9 spec §Contracts (lines 16-147) + §Algorithm (lines 156-175) + SPEC §4.4 (compact logic steps 1-8) + §7 (LLM error handling — return status, don't delete) + M1/S8 (non-blocking insufficient_data status) + AGENTS.md ADR-003/004/005/010/001/002 + mem-006 (SDK tool pattern) + mem-027/mem-028 (snake_case transport discipline). One `@code` dispatch, no decomposition.

1. **Edit `Contracts/MemoryCompactInput.cs`** — ADD `[JsonPropertyName("snake_case")]` attributes to the 2 properties per the M7 rung-d discipline. The record already exists from M3 (32 lines, no `[JsonPropertyName]`); M9 EDITS it additively:
   ```csharp
   namespace McpMemoryService.Contracts;

   using System.Text.Json.Serialization;

   /// <summary>
   /// [PURPOSE]: Input DTO for the memory_compact tool — request to compact entries into a summary.
   /// </summary>
   /// <remarks>
   /// [INVARIANTS]: ProjectId is required. BatchSize defaults to 20 (per SPEC §4.4). Validation in InputValidator.ValidateCompact (M7 forward stub): BatchSize in [1, 100].
   ///   Transport property keys are snake_case (project_id/batch_size) enforced via [JsonPropertyName] (M9 rung-d preemptive — post-M7 discipline, mem-027/mem-028).
   /// </remarks>
   public sealed record MemoryCompactInput
   {
       // BUG_FIX_CONTEXT: [scar — rung-d preemptive (M9): snake_case names fixed per-property at M9 edit time
       //   per mem-027/mem-028. Immunized by SnakeCaseTransportTests — see Program.cs M7 scar for the SDK 1.4.0
       //   no-serializer-knob ladder walk.]

       /// <summary>[PURPOSE]: Project identifier (SHA-256 of workspace root).</summary>
       [JsonPropertyName("project_id")]
       public required string ProjectId { get; init; }

       /// <summary>[PURPOSE]: Number of entries to include in a single compact batch. Defaults to 20.</summary>
       [JsonPropertyName("batch_size")]
       public int BatchSize { get; init; } = 20;
   }
   ```
   Update the `#region MODULE_CONTRACT` `[CHANGES]` line: `LAST_CHANGE: M9 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 mem-027/mem-028 discipline); M3 initial creation.`. Record shape/types/required/default UNCHANGED — only attributes + the scar block + the `[CHANGES]` line added.

2. **Edit `Contracts/MemoryCompactOutput.cs`** — ADD `[JsonPropertyName]` to the 7 properties of the record (exists from M3, 52 lines, no `[JsonPropertyName]`). Add `using System.Text.Json.Serialization;`:
   - `Status` → `[JsonPropertyName("status")]`.
   - `Reason` → `[JsonPropertyName("reason")]`.
   - `Available` → `[JsonPropertyName("available")]`.
   - `Required` → `[JsonPropertyName("required")]`.
   - `SourceCount` → `[JsonPropertyName("source_count")]`.
   - `SummaryPointId` → `[JsonPropertyName("summary_point_id")]`.
   - `Error` → `[JsonPropertyName("error")]`.
   Add the `BUG_FIX_CONTEXT: [scar — rung-d preemptive (M9)]` block. Update `MODULE_CONTRACT [CHANGES]`: `LAST_CHANGE: M9 — added [JsonPropertyName] snake_case transport overrides (rung-d preemptive — post-M7 discipline); M3 initial creation per SPEC §4.4 + S8.`. Record shape/types/required UNCHANGED — only attributes added.

3. **Implement `Tools/MemoryCompactTool.cs`** per §1 node + M9 spec §Contracts + SPEC §4.4 + §7, with `#region` structuring (matches `MemoryCaptureTool`/`MemoryRetrieveTool`):
   ```csharp
   #region MODULE_CONTRACT [DOMAIN(Tools): memory_compact MCP tool; CONCEPT(MemoryCompactTool): Validate → GetBatch → insufficient_data skip OR Summarize → Embed → Upsert summary → hard-delete sources; TECH(MCP, ADR-003/004/005)]
   // ... header per csharp-conventions, matching MemoryRetrieveTool MODULE_CONTRACT style ...
   #endregion MODULE_CONTRACT

   namespace McpMemoryService.Tools;

   using McpMemoryService.Contracts;
   using McpMemoryService.Enums;
   using McpMemoryService.Models;
   using McpMemoryService.Services;
   using McpMemoryService.Validation;
   using Microsoft.Extensions.Logging;
   using ModelContextProtocol.Server;

   /// <summary>
   /// [PURPOSE]: MCP tool for compacting old memory entries into a summary via LLM + hard-deleting sources.
   /// </summary>
   [McpServerToolType]
   public sealed class MemoryCompactTool
   {
       #region Fields
       private readonly IQdrantService _qdrantService;
       private readonly ILlmSummarizerService _llmSummarizerService;
       private readonly IEmbeddingService _embeddingService;
       private readonly ILogger<MemoryCompactTool> _logger;
       #endregion Fields

       #region Constructors
       public MemoryCompactTool(
           IQdrantService qdrantService,
           ILlmSummarizerService llmSummarizerService,
           IEmbeddingService embeddingService,
           ILogger<MemoryCompactTool> logger)
       {
           _qdrantService = qdrantService;
           _llmSummarizerService = llmSummarizerService;
           _embeddingService = embeddingService;
           _logger = logger;
       }
       #endregion Constructors

       #region CompactAsync
       /// <summary>
       /// [PURPOSE]: Compact a batch of old memory entries into a single summary entry via LLM, then hard-delete sources.
       /// </summary>
       /// <param name="input">Compact input (project_id, batch_size default 20).</param>
       /// <param name="cancellationToken">Cancellation token.</param>
       /// <returns>MemoryCompactOutput — Status is "completed" (sources deleted, summary created),
       ///   "skipped" (insufficient data, non-blocking per ADR-004), or "error" (LLM timeout/5xx, OR post-LLM
       ///   embed/upsert/delete failure — sources NOT deleted in any error path per ADR-003/005).</returns>
       /// <exception cref="ArgumentException">Thrown when input validation fails (project_id empty, BatchSize outside [1,100]).</exception>
       /// <remarks>
       /// [INVARIANTS]:
       ///   1. Transactional (ADR-003): source entries deleted ONLY after LLM + embed + upsert all succeed.
       ///      On LLM failure (TaskCanceledException/HttpRequestException) → return error status, sources NOT deleted.
       ///      On post-LLM failure (embed/upsert/delete throw) → return error status, sources NOT deleted (delete-last ordering).
       ///   2. Non-blocking (ADR-004): insufficient_data (entries.Count &lt; batchSize) → return "skipped", NOT an error.
       ///   3. Connection-safe (ADR-005): NO exception escapes this method — the MCP Streamable-HTTP connection MUST NOT break.
       /// [RATIONALE]: The tool IS the MCP-facing caller of IQdrantService+ILlmSummarizerService+IEmbeddingService.
       ///   LLM failures are NAMED catches (transactional guards); Qdrant/Embedding failures in the post-LLM phase
       ///   are caught as `Exception` (silent-fallback to structured error status). Inverse of the M5/M6 do-not-swallow
       ///   service-level invariant — the tool catches at the boundary, services still throw internally.
       /// [CHANGES]: LAST_CHANGE: M9 creation.
       /// </remarks>
       [McpServerTool(Name = "memory_compact")]
       public async Task<MemoryCompactOutput> CompactAsync(
           MemoryCompactInput input,
           CancellationToken cancellationToken = default)
       {
           // Validate input (throws ArgumentException — propagates, NOT swallowed)
           InputValidator.ValidateCompact(input);

           // 1. Fetch batch (excludes entry_type=summary, ordered by timestamp ASC per M5)
           var entries = await _qdrantService.GetBatchForCompactAsync(
               input.ProjectId, input.BatchSize, cancellationToken);

           // [IMP:1][CompactAsync][PROGRESS] batch fetched
           _logger.LogInformation(
               "[IMP:1][CompactAsync][PROGRESS] batch fetched count={Count} projectId={ProjectId}",
               entries.Count, input.ProjectId);

           // 2. Non-blocking insufficient_data check (ADR-004)
           if (entries.Count < input.BatchSize)
           {
               return new MemoryCompactOutput
               {
                   Status = "skipped",
                   Reason = "insufficient_data",
                   Available = entries.Count,
                   Required = input.BatchSize
               };
           }

           // 3. Extract contents for LLM summarization
           var contents = entries.Select(e => e.Payload.Content).ToList();

           // 4. LLM summarization with TRANSACTIONAL named catches (ADR-005 inverse — named, NOT silent)
           string summary;
           try
           {
               summary = await _llmSummarizerService.SummarizeAsync(contents, cancellationToken);

               // [IMP:2][CompactAsync][SUCCESS] LLM summarization complete
               _logger.LogInformation(
                   "[IMP:2][CompactAsync][SUCCESS] LLM summarization complete len={Len}",
                   summary.Length);
           }
           catch (TaskCanceledException ex)
           {
               // [IMP:2][CompactAsync][FATAL] LLM timeout — sources NOT deleted (transactional ADR-003)
               _logger.LogWarning(
                   ex,
                   "[IMP:2][CompactAsync][FATAL] LLM timeout — returning llm_timeout, sources NOT deleted projectId={ProjectId}",
                   input.ProjectId);

               return new MemoryCompactOutput
               {
                   Status = "error",
                   Reason = "llm_timeout",
                   Error = ex.Message
               };
           }
           catch (HttpRequestException ex)
           {
               // [IMP:2][CompactAsync][FATAL] LLM 5xx — sources NOT deleted (transactional ADR-003)
               _logger.LogWarning(
                   ex,
                   "[IMP:2][CompactAsync][FATAL] LLM 5xx — returning llm_5xx, sources NOT deleted projectId={ProjectId}",
                   input.ProjectId);

               return new MemoryCompactOutput
               {
                   Status = "error",
                   Reason = "llm_5xx",
                   Error = ex.Message
               };
           }

           // 5. Post-LLM phase: embed summary → upsert summary entry → hard-delete sources.
           //    Wrapped in try/catch(Exception) — sources NOT deleted on any failure here (delete is LAST).
           try
           {
               var summaryPointId = Guid.NewGuid();

               // Generate embedding for the LLM-produced summary text (384-dim L2-normalized per M4/ADR-011)
               var summaryVector = await _embeddingService.EmbedAsync(summary, cancellationToken);

               // Compose summary payload — orchestrator-attributed, summary entry_type (ADR-001/002)
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

               // Upsert summary entry into Qdrant
               await _qdrantService.UpsertAsync(summaryPointId, summaryVector, summaryPayload, cancellationToken);

               // [IMP:3][CompactAsync][SUCCESS] summary captured
               _logger.LogInformation(
                   "[IMP:3][CompactAsync][SUCCESS] summary captured pointId={PointId}",
                   summaryPointId);

               // 6. Hard-delete source entries (ADR-003 — irreversible, after summary persisted)
               var sourceIds = entries.Select(e => e.PointId).ToList();
               await _qdrantService.DeleteAsync(sourceIds, cancellationToken);

               // [IMP:4][CompactAsync][SUCCESS] source entries deleted
               _logger.LogInformation(
                   "[IMP:4][CompactAsync][SUCCESS] source entries deleted count={Count}",
                   sourceIds.Count);

               return new MemoryCompactOutput
               {
                   Status = "completed",
                   SourceCount = entries.Count,
                   SummaryPointId = summaryPointId.ToString()
               };
           }
           catch (Exception ex)
           {
               // [IMP:3][CompactAsync][FATAL] post-LLM phase failed — sources NOT deleted (transactional ADR-003)
               //   (delete never reached on embed/upsert failure; if delete itself threw, sources remain + summary exists — idempotent on next compact)
               _logger.LogError(
                   ex,
                   "[IMP:3][CompactAsync][FATAL] post-LLM phase (embed/upsert/delete) failed — sources NOT deleted (transactional ADR-003) projectId={ProjectId}",
                   input.ProjectId);

               return new MemoryCompactOutput
               {
                   Status = "error",
                   Reason = "qdrant_or_embedding_failure",
                   Error = ex.Message
               };
           }
       }
       #endregion CompactAsync
   }
   ```
   - **Verified property names** against the realized source: `MemoryEntry.PointId` (`Guid`), `MemoryEntry.Payload` (`MemoryPayload`), `MemoryPayload.ProjectId/SessionId/AgentRole/EntryType/Timestamp/Content/Tags/Metadata` (per M3 — `@code` VERIFY the exact property names against `src/McpMemoryService/Models/MemoryPayload.cs` + `Metadata.cs` before compiling; the M3 spec + M5 PayloadMappingExtensions round-trip are the ground truth). If `SessionId` or `Metadata.Session` naming differs, adjust the summary-payload composition — the construction is mechanical.
   - **`summaryPointId.ToString()`** — default format is `"D"` (lowercase-dashed GUID). Matches the M7/M8 GUID convention. Use explicit `.ToString()` or `.ToString("D")` for clarity (the DTO field is `string?`).
   - **`GetBatchForCompactAsync` exclusion** — the `IQdrantService` implementation (M5 QdrantService) EXCLUDES `entry_type=summary` entries and ORDERS by timestamp ASC. M9 does NOT re-filter — it trusts the M5 contract. The `ExecuteAsync_ExcludesSummaryFromBatch` test verifies the CALL is made (with `input.ProjectId` + `input.BatchSize`); M5 internally guarantees the exclusion.
   - **`catch (TaskCanceledException)` ordering** — `TaskCanceledException` is a subclass of `OperationCanceledException`, NOT of `HttpRequestException`. Place the `TaskCanceledException` catch BEFORE the `HttpRequestException` catch (they are sibling exception types, neither derives from the other — order between them does not matter, but BOTH must come before any generic `catch (Exception)`). The post-LLM `catch (Exception)` is in a SEPARATE try block (the LLM try has only the two named catches; the post-LLM try has the generic catch) — so there is no overlap/ordering concern between the two try blocks.

4. **Program.cs edit (additive DI line).** In `ConfigureServices`, append IMMEDIATELY AFTER the M8 tool registration:
   ```csharp
   services.AddTransient<McpMemoryService.Tools.MemoryCaptureTool>();
   services.AddTransient<McpMemoryService.Tools.MemoryGetStatsTool>();
   services.AddTransient<McpMemoryService.Tools.MemoryRetrieveTool>();
   // [IMP:M9][ConfigureServices][OPTION] Register memory_compact MCP tool (Transient) — ADR-003 transactional, ADR-004 non-blocking skip, ADR-005 LLM named-catches, ADR-010 WithToolsFromAssembly auto-discovery (mem-006)
   services.AddTransient<McpMemoryService.Tools.MemoryCompactTool>();
   ```
   - **STRICT ADDITIVE** — do NOT touch the M7/M8 tool-registration lines, the M2 BUG_FIX_CONTEXT scars (McpServerOptions post-configure block, `.WithToolsFromAssembly()` swap + `[IMP:M7] supersede` marker, `MapMcp("/mcp")`), the M4 `IEmbeddingService` line, the M5 `IQdrantService`/`QdrantCollectionInitializer` lines, the M6 `LlamaCpp` HttpClient + `ILlmSummarizerService` lines, or the `[IMP:9][ConfigureServices][SUCCESS]` marker.
   - `WithToolsFromAssembly()` (swapped in by M7) auto-discovers `[McpServerToolType]` classes — adding the annotation on `MemoryCompactTool` + the `AddTransient` DI line is SUFFICIENT for SDK discovery + ctor-param resolution on `tools/call` (`IQdrantService`+`ILlmSummarizerService`+`IEmbeddingService`+`ILogger<>` all registered by M5/M6/M4/M7). NO `WithToolsFromAssembly()` call edit.
   - MATCH the M7/M8 style — fully-qualified `McpMemoryService.Tools.MemoryCompactTool` type name in the `AddTransient` call (the `using McpMemoryService.Tools;` from M7 covers the shorter form if `@code` prefers it — but the fully-qualified form is the established precedent).

5. **HostSmokeTests edit (3→4 tools assertion).** In `tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs`, update the tools/list assertion block (post-M8):
   ```csharp
   // [IMP:M9][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 4 tools (memory_capture, memory_get_stats, memory_retrieve, memory_compact)
   // (was [IMP:M8] ... 3 tools ... — M8 scar preserved above; M9 extends the count to 4 — 4th and final tool)
   var toolsArray = toolsResult.GetProperty("result").GetProperty("tools");
   toolsArray.GetArrayLength().Should().Be(4);
   // ...
   var toolNames = toolsArray.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
   toolNames.Should().Contain("memory_capture");
   toolNames.Should().Contain("memory_get_stats");
   toolNames.Should().Contain("memory_retrieve");
   toolNames.Should().Contain("memory_compact", "M9 adds memory_compact to tools/list (4th and final tool)");
   ```
   - PRESERVE the M7 + M8 `[IMP:M7]`/`[IMP:M8]` scar comments — do NOT delete them. Insert the `[IMP:M9]` line alongside with an extension note (same approach as M8's treatment of the M7 scar). The historical truth (M7 established 2 tools; M8 extended to 3; M9 extends to 4 — final) MUST be preserved for future @debug/@qa archaeology.
   - PRESERVE: the Accept header setup, the initialize handshake, the SSE `ExtractDataFromSse` helper, the BUG_FIX_CONTEXT scars, `[Trait("Category","Integration")]`, the existing `[IMP:1]/[IMP:2]/[IMP:3]` markers, the Module header. Only the tools/list assertion block is touched (count 3→4 + add `memory_compact` containment + update/add the `[IMP:M9]` marker).

6. **Write `tests/McpMemoryService.Tests/Tools/MemoryCompactToolTests.cs`** — ~10 unit tests, UNCATEGORISED (no `[Trait]`, no `Category=Integration` — no real Qdrant/Docker/ONNX/LLM). Pattern: construct `MemoryCompactTool` via ctor with `Mock<IQdrantService>` + `Mock<ILlmSummarizerService>` + `Mock<IEmbeddingService>` + `NullLogger<MemoryCompactTool>.Instance` (Moq already in test csproj from M2). Mock `IQdrantService.GetBatchForCompactAsync` returns a canned `IReadOnlyList<MemoryEntry>` — build `MemoryEntry` records with `PointId = Guid.NewGuid()`, `Payload = new MemoryPayload { ProjectId = ..., AgentRole = AgentRole.Code, EntryType = EntryType.Decision, Content = "...", Timestamp = DateTimeOffset.UtcNow, Tags = new[] { "tag" }, Metadata = new Metadata { Session = "..." } }`, `Vector = new float[384]` (unused by the tool), `Score = null` (GetBatchForCompactAsync does not score). Mock `ILlmSummarizerService.SummarizeAsync` returns a canned string `"summary text"`. Mock `IEmbeddingService.EmbedAsync` returns `new float[384]` (or `Enumerable.Repeat(0.1f, 384).ToArray()`).
   - `ExecuteAsync_SufficientData_CompletesAndDeletesSources` — mock Qdrant returns 20 entries, LLM "summary", Embedding float[384] → `Assert.Equal("completed", output.Status)`; `Assert.Equal(20, output.SourceCount)`; `Assert.True(Guid.TryParse(output.SummaryPointId, out _))`; `mockQdrant.Verify(x => x.DeleteAsync(It.Is<IEnumerable<Guid>>(ids => ids.Count() == 20), It.IsAny<CancellationToken>()), Times.Once)`; `mockQdrant.Verify(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()), Times.Once)`.
   - `ExecuteAsync_InsufficientData_ReturnsSkippedNonBlocking` — mock Qdrant returns 5 entries (BatchSize=20) → `Assert.Equal("skipped", output.Status)`; `Assert.Equal("insufficient_data", output.Reason)`; `Assert.Equal(5, output.Available)`; `Assert.Equal(20, output.Required)`; `mockLlm.Verify(x => x.SummarizeAsync(...), Times.Never)`; `mockQdrant.Verify(x => x.DeleteAsync(...), Times.Never)`; `mockQdrant.Verify(x => x.UpsertAsync(...), Times.Never)`.
   - `ExecuteAsync_LlmTimeout_ReturnsErrorSourceNotDeleted` — `mockLlm.Setup(x => x.SummarizeAsync(...)).ThrowsAsync(new TaskCanceledException("timeout"))` → `Assert.Equal("error", output.Status)`; `Assert.Equal("llm_timeout", output.Reason)`; `mockQdrant.Verify(x => x.DeleteAsync(...), Times.Never)`; `mockQdrant.Verify(x => x.UpsertAsync(...), Times.Never)` — **TRANSACTIONAL SAFETY**.
   - `ExecuteAsync_Llm5xx_ReturnsErrorSourceNotDeleted` — `mockLlm.Setup(...).ThrowsAsync(new HttpRequestException("503"))` → `Assert.Equal("error", output.Status)`; `Assert.Equal("llm_5xx", output.Reason)`; `mockQdrant.Verify(x => x.DeleteAsync(...), Times.Never)`.
   - `ExecuteAsync_SummaryEntryHasCorrectPayload` — 20 entries, LLM "summary text" → capture the `MemoryPayload` passed to `UpsertAsync` via `Callback` or `It.Is<MemoryPayload>(p => p.EntryType == EntryType.Summary && p.AgentRole == AgentRole.Orchestrator && p.Tags.Contains("compact") && p.Tags.Contains("summary") && p.Content == "summary text")`; `mockQdrant.Verify(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>>(), It.Is<MemoryPayload>(p => p.EntryType == EntryType.Summary && p.AgentRole == AgentRole.Orchestrator), It.IsAny<CancellationToken>()), Times.Once)`.
   - `ExecuteAsync_EmptyProjectId_ThrowsValidation` — `await Assert.ThrowsAsync<ArgumentException>(() => tool.CompactAsync(input with { ProjectId = "" }))`.
   - `ExecuteAsync_BatchSizeZero_ThrowsValidation` — `await Assert.ThrowsAsync<ArgumentException>(() => tool.CompactAsync(input with { BatchSize = 0 }))`.
   - `ExecuteAsync_BatchSizeOver100_ThrowsValidation` — `await Assert.ThrowsAsync<ArgumentException>(() => tool.CompactAsync(input with { BatchSize = 101 }))`.
   - `ExecuteAsync_GeneratesEmbeddingForSummary` — LLM returns "summary text" → `mockEmbedding.Verify(x => x.EmbedAsync("summary text", It.IsAny<CancellationToken>()), Times.Once)`.
   - `ExecuteAsync_ExcludesSummaryFromBatch` — `mockQdrant.Verify(x => x.GetBatchForCompactAsync(input.ProjectId, input.BatchSize, It.IsAny<CancellationToken>()), Times.Once)` (M5 guarantees exclusion internally; M9 verifies the call propagates ProjectId + BatchSize).
   - (Optional) `ExecuteAsync_PostLlmEmbeddingFails_ReturnsErrorSourcesNotDeleted` — `mockEmbedding.Setup(x => x.EmbedAsync(...)).ThrowsAsync(new InvalidOperationException("onnx died"))` → `Assert.Equal("error", output.Status)`; `Assert.Equal("qdrant_or_embedding_failure", output.Reason)`; `mockQdrant.Verify(x => x.DeleteAsync(...), Times.Never)` — transactional (delete is last step; never reached on embed failure).
   - (Optional) `ExecuteAsync_PostLlmUpsertFails_ReturnsErrorSourcesNotDeleted` — `mockQdrant.Setup(x => x.UpsertAsync(...)).ThrowsAsync(new InvalidOperationException("qdrant down"))` → `Assert.Equal("error", output.Status)`; `mockQdrant.Verify(x => x.DeleteAsync(...), Times.Never)`.
   - The new `Tools/MemoryCompactToolTests.cs` does NOT need a `.csproj` edit — SDK auto-discovers (matches M7/M8 `Tools/` precedent).
   - If `@code` extends `SnakeCaseTransportTests` (M7 file) with compact-DTO round-trip cases: add 2 Fact methods — one deserializes `{"project_id":"...","batch_size":20}` with DEFAULT `JsonSerializerOptions` into `MemoryCompactInput` and asserts `ProjectId` + `BatchSize == 20`; one serializes a `MemoryCompactOutput { Status = "completed", SourceCount = 5, SummaryPointId = "..." }` with default options and asserts the JSON keys are `status`/`source_count`/`summary_point_id` (and absent nullable fields serialize to `null` or are omitted per System.Text.Json default — verify behavior). This immunizes the M9 `[JsonPropertyName]` scar (mirrors the M7/M8 proof).

7. **Build + test gate before return:**
   - `dotnet build McpMemoryService.sln` → **0 Warning(s), 0 Error(s)**. AC-1.
   - `dotnet test --filter "Category!=Integration"` → unit gate: M3 (16) + M5 mapping (5) + M6 (6) + M7 (≈10 capture/stats + SnakeCaseTransportTests) + M8 (~10..12 retrieve) + **M9 unit (~10..12 compact)** = all green, `Failed: 0`. AC-2. The 2 compact-DTO `[JsonPropertyName]` edits must NOT break the existing M3 `DtoValidationTests` / `MemoryCompactInput_DefaultBatchSize_Is20` — verify that test still passes (the `BatchSize = 20` default is unchanged).
   - Do NOT run `dotnet test` (full) — M9 has no integration tests. The HostSmokeTests 3→4 assertion update requires Docker Qdrant + ONNX model + llama.cpp — that's @qa's responsibility under the full gate (M2 HostSmokeTests is `Category=Integration`). AC-3 is source-verified by @code, runtime-verified by @qa.

---

## 3. Acceptance Criteria

> Verbatim from `milestones/M9-mcp-tool-compact.md` lines 270-282, labelled for mechanical `@qa` checking.

- [ ] **AC-1:** `dotnet build` — OK.
- [ ] **AC-2:** `dotnet test` — all unit tests PASS (no real Qdrant/Docker/ONNX/LLM required for M9 unit tests).
- [ ] **AC-3:** MCP `tools/list` returns all 4 tools (capture, get_stats, retrieve, compact) — HostSmokeTests assertion updated 3→4 tools + `memory_compact` name containment. Runtime proof requires Docker Qdrant + ONNX + llama.cpp (integration gate) — @qa exercises it; @code source-verifies the assertion edit.
- [ ] **AC-4:** `memory_compact` input schema matches SPEC §4.4 — verified by `[JsonPropertyName]` on `MemoryCompactInput` (`project_id`, `batch_size`) + `SnakeCaseTransportTests` (extended or sibling) round-trip.
- [ ] **AC-5:** Sufficient data → LLM summarize → capture summary → hard delete sources → return `completed` (SourceCount + SummaryPointId set).
- [ ] **AC-6:** Insufficient data (`entries.Count < batchSize`) → return `status=skipped`, `reason=insufficient_data` (non-blocking, no error; LLM/Qdrant NOT called).
- [ ] **AC-7:** LLM timeout (`TaskCanceledException`) → return `status=error`, `reason=llm_timeout` — **source entries NOT deleted** (transactional ADR-003, verified by `DeleteAsync Times.Never`).
- [ ] **AC-8:** LLM 5xx (`HttpRequestException`) → return `status=error`, `reason=llm_5xx` — **source entries NOT deleted** (transactional ADR-003).
- [ ] **AC-9:** Summary entry has `entry_type=summary`, `agent_role=orchestrator`, tags include `"compact"` (and `"summary"`) — verified by `UpsertAsync` payload assertion.
- [ ] **AC-10:** `BatchSize` validated to [1, 100] — `InputValidator.ValidateCompact` throws `ArgumentException` on violation (ALREADY implemented in M7 forward stub — M9 just calls it).
- [ ] **AC-11:** Logs contain `[IMP:1]` (batch fetched), `[IMP:2]` (LLM complete OR LLM timeout/5xx FATAL), `[IMP:3]` (summary captured OR post-LLM FATAL), `[IMP:4]` (source deleted) markers.
- [ ] **AC-12:** Transactional guarantee verified by tests — `DeleteAsync` is `Times.Never` on every LLM-failure path AND every post-LLM-phase-failure path (no delete on embed/upsert failure).
- [ ] **AC-13:** Compact DTOs (`MemoryCompactInput`/`MemoryCompactOutput`) carry `[JsonPropertyName("snake_case")]` overrides — the M7 rung-d discipline applied at M9 edit time (mem-027/mem-028). Immunized by `SnakeCaseTransportTests` (extended or sibling).
- [ ] **AC-14:** `Program.cs` has `services.AddTransient<McpMemoryService.Tools.MemoryCompactTool>();` after the M8 tool registration + `[IMP:M9]` marker comment. The M2/M4/M5/M6/M7/M8 scars + the `[IMP:9]` SUCCESS marker are preserved.

---

## Notes for @code (M9)

1. **TRANSACTIONAL GUARANTEE — LLM MUST succeed before any `DeleteAsync` call (ADR-003 — the PRIMARY M9 invariant).** `CompactAsync` has TWO try blocks: (a) the LLM try with NAMED catches (`TaskCanceledException` → `llm_timeout`; `HttpRequestException` → `llm_5xx`) — both RETURN IMMEDIATELY, `DeleteAsync` is never reached; (b) the post-LLM try (embed+upsert+delete) with a generic `catch (Exception)` — `DeleteAsync` is the LAST statement inside the post-LLM try, so if embed or upsert throws, control jumps to the catch BEFORE delete runs; sources survive. ONLY the happy path (all of LLM→embed→upsert→delete succeed) reaches `return completed`. Tests MUST verify `mockQdrant.Verify(x => x.DeleteAsync(...), Times.Never)` on EVERY ERROR PATH (llm_timeout, llm_5xx, post-LLM embed failure, post-LLM upsert failure). This is the single most important invariant of M9 — @qa will check it explicitly.

2. **Non-blocking — insufficient_data is NOT an error (ADR-004).** When `entries.Count < input.BatchSize`, `CompactAsync` returns `MemoryCompactOutput { Status = "skipped", Reason = "insufficient_data", Available = entries.Count, Required = input.BatchSize }`. This is a structured response, NOT a throw, NOT `Status = "error"`. `ILlmSummarizerService.SummarizeAsync` is NOT called (the early return precedes the LLM try). `IQdrantService.UpsertAsync`/`DeleteAsync` are NOT called. `IEmbeddingService.EmbedAsync` is NOT called. Tests verify `mockLlm.Verify(Times.Never)` + `mockQdrant.Verify(x => x.DeleteAsync, Times.Never)` + `mockQdrant.Verify(x => x.UpsertAsync, Times.Never)` + `mockEmbedding.Verify(Times.Never)`. This aligns with the non-blocking philosophy (SPEC §7 — compact must not break the MCP connection; insufficient data is a normal skipped state, not a failure).

3. **Silent-fallback catches `Exception` from Qdrant/Embedding in the POST-LLM phase — BUT specifically catches `TaskCanceledException`/`HttpRequestException` from the LLM as TRANSACTIONAL GUARDS (ADR-005 boundary inverse of M6 do-not-swallow).** The LLM phase catches are NAMED (not `Exception`) — they produce STRUCTURED error responses (`Status="error"`, `Reason="llm_timeout"`/`"llm_5xx"`) which the MCP caller reads; source entries are NOT deleted. The post-LLM phase (embed+upsert+delete) is wrapped in `try { ... } catch (Exception ex) { return Status="error", Reason="qdrant_or_embedding_failure" }` — sources NOT deleted (delete is last). Inside `LlmSummarizerService.SummarizeAsync` exceptions STILL PROPAGATE (M6 do-not-swallow invariant) — the M9 tool catches at the boundary, NOT inside the service. Inside `QdrantService.UpsertAsync`/`DeleteAsync`/`IEmbeddingService.EmbedAsync` exceptions STILL PROPAGATE (M5/M4 do-not-swallow) — the M9 post-LLM catch handles them at the boundary. NO exception escapes `CompactAsync` — MCP connection integrity preserved (ADR-005).

4. **`[JsonPropertyName]` on the 2 compact DTOs is added AT M9 — preemptive rung-d (mem-027/mem-028).** M3 created `MemoryCompactInput` (32 lines) + `MemoryCompactOutput` (52 lines) WITHOUT `[JsonPropertyName]` (M3 predates the M7 rung-d fix). M9 EDITS them additively — add the attributes + a `BUG_FIX_CONTEXT: [scar — rung-d preemptive (M9)]` block + update the `[CHANGES]: LAST_CHANGE: M9` line. DO NOT regress to the absent `McpServerOptions.SerializerOptions` knob (absent on SDK 1.4.0 — M7 rung-a/b probe proved it; the Program.cs M7 scar documents the ladder walk). The `using System.Text.Json.Serialization;` import is added once per file. The record shapes/types/required/defaults are UNCHANGED — only attributes added. Immunize with `SnakeCaseTransportTests` (extended or sibling) — mirrors the M7/M8 proof.

5. **`InputValidator.ValidateCompact` is ALREADY fully implemented — DO NOT touch `InputValidator.cs`.** The M7 forward stub (InputValidator.cs L95-104) covers all M9 validation needs: `ArgumentNullException.ThrowIfNull(input)`, `string.IsNullOrWhiteSpace(input.ProjectId)` → `ArgumentException`, `input.BatchSize < 1 || > 100` → `ArgumentException`. The M9 spec §Algorithm step 2 ("Ensure `ValidateCompact` is fully implemented") is ALREADY SATISFIED by the M7 forward stub. M9 `CompactAsync` calls `InputValidator.ValidateCompact(input);` as the FIRST line (before `[IMP:1]`), exactly mirroring M7 `MemoryCaptureTool.CaptureAsync` + M8 `MemoryRetrieveTool.RetrieveAsync`. No edit to `InputValidator.cs`. If `@code` discovers the stub is INCOMPLETE (verified: it is complete, InputValidator.cs L95-104), fix as a MINOR edit with a `[CHANGES]` note.

6. **The tool is an INSTANCE class with `[McpServerToolType]` + ctor DI (mem-006 SDK pattern).** NOT a static class. `[McpServerToolType]` on `MemoryCompactTool` + `[McpServerTool(Name = "memory_compact")]` on the instance method `CompactAsync` + ctor-injected `IQdrantService`/`ILlmSummarizerService`/`IEmbeddingService`/`ILogger<MemoryCompactTool>`. The MCP SDK 1.4.0 `WithToolsFromAssembly()` discovers `[McpServerToolType]`-annotated classes, instantiates via DI on each `tools/call`, resolves ctor params from the DI container (all registered by M4/M5/M6/M7), deserializes the `MemoryCompactInput` DTO param from the JSON-RPC `arguments` block via the `[JsonPropertyName]` overrides. `CancellationToken` is supplied by the SDK transport. Tests bypass the SDK entirely — ctor-direct construction with mocked services + direct method invocation (matches M7 `MemoryCaptureToolTests` / M8 `MemoryRetrieveToolTests`).

7. **LDD markers `[IMP:1]`..`[IMP:4]` (per M9 spec line 59).** `[IMP:1][CompactAsync][PROGRESS] batch fetched count=N` — after `GetBatchForCompactAsync` (before the insufficient_data check). `[IMP:2][CompactAsync][SUCCESS] LLM summarization complete len=L` — after `SummarizeAsync` returns; `[IMP:2][CompactAsync][FATAL] LLM timeout` / `LLM 5xx` — in the two named catch blocks (same IMP index, different step token — the M9 spec assigns both the success + failure LLM logs to `[IMP:2]`). `[IMP:3][CompactAsync][SUCCESS] summary captured pointId=...` — after `UpsertAsync` (before `DeleteAsync`); `[IMP:3][CompactAsync][FATAL] post-LLM phase failed` — in the post-LLM `catch (Exception)` (sources NOT deleted, transactional ADR-003). `[IMP:4][CompactAsync][SUCCESS] source entries deleted count=N` — after `DeleteAsync` (the last success marker). The `[IMP:N][Method][Step]` canonical format from M4 applies. Do NOT collide with `IQdrantService`'s own IMP markers (different method-name token `CompactAsync`).

8. **Program.cs scar discipline.** The M2 `WithListToolsHandler→WithToolsFromAssembly` swap scar (with the `[IMP:M7] supersede` one-liner), the `McpServerOptions` post-configure block scar (rung-d `[JsonPropertyName]` fallback note), the `MapMcp("/mcp")` scar, the M7 `[IMP:M7][ConfigureServices][OPTION]` comment, the M7/M8 tool-registration lines, and the M8 `[IMP:M8]` marker are ALL preserved. M9's edit is STRICTLY ADDITIVE: one comment line + one `services.AddTransient<McpMemoryService.Tools.MemoryCompactTool>();` line appended after the M8 tool registration. Do NOT touch the M7/M8 tool-registration lines, the snake_case serializer options config, the existing DI lines (M4/M5/M6/M7/M8), or the `[IMP:9][ConfigureServices][SUCCESS]` marker. `using McpMemoryService.Tools;` (already present from M7) covers the shorter form — but MATCH the M7/M8 style (fully-qualified `McpMemoryService.Tools.MemoryCompactTool`).

9. **HostSmokeTests scar discipline — preserve the M7 + M8 markers.** The M7 `[IMP:M7]...2 tools...` and M8 `[IMP:M8]...3 tools...` scar comments document the evolution history. M9 EXTENDS the count 3→4 and ADDS `memory_compact` containment — do NOT delete the M7/M8 markers. Insert the `[IMP:M9]...4 tools (memory_capture, memory_get_stats, memory_retrieve, memory_compact)` line alongside with an extension note (the historical truth — M7 established 2 tools; M8 extended to 3; M9 extends to 4 — final tool — MUST be preserved for future @debug/@qa archaeology). The Accept header setup, initialize handshake, SSE `ExtractDataFromSse` helper, `[Trait("Category","Integration")]`, existing IMP markers, Module header are ALL preserved — only the tools/list assertion block is touched (count 3→4 + add `memory_compact` containment + add/update the `[IMP:M9]` marker).

10. **Summary entry has `entry_type=summary`, `agent_role=orchestrator` (ADR-001/002).** The M9 `MemoryPayload` for the upserted summary point sets `EntryType = EntryType.Summary` (the ONLY pathway producing summary entries — capture FORBIDS it via `InputValidator.ValidateCapture` L43-44), `AgentRole = AgentRole.Orchestrator` (compact is orchestrator-driven per SPEC §4.4), `Tags = new[] { "compact", "summary" }`, `SessionId = "compact"`, `Metadata.Session = $"compact-{DateTimeOffset.UtcNow:yyyyMMdd}"`, `Timestamp = DateTimeOffset.UtcNow` (the compaction moment, not the source entries' timestamps). `ProjectId = input.ProjectId` (same project as sources). `Content = summary` (the LLM-produced text). Subsequent `memory_retrieve` calls with `agent_role_filter=orchestrator` OR `entry_type_filter=summary` will surface these summary points.

11. **Decomposition decision — SINGLE `@code scope=impl:M9` dispatch.** 1 tool class (1 async method, ~80 lines incl. comments) + 2 DTO additive edits (`[JsonPropertyName]` only) + 1 Program.cs additive edit (1 DI line + 1 comment) + 1 HostSmokeTests assertion edit (count 3→4 + name containment + marker) + 1 test file (~10..12 tests). Cohesive — tool + transactional logic + transport discipline + tool smoke assertion are atomic (splitting would yield a non-running intermediate state where `tools/list` advertises `memory_compact` but the DTO transport is PascalCase, or where the transactional guard is mis-ordered relative to delete). Below the >5-new-methods decomposition threshold. **NO `## Decomposition` section** is appended to this plan.

12. **Do NOT run `dotnet test` (full) during the `@code` dispatch.** The M9 unit tests are mocking-only; the HostSmokeTests 3→4 assertion update requires Docker Qdrant (`QdrantCollectionInitializer` IHostedService) + the ONNX model (M4) + llama.cpp (M6 `LlmSummarizerService` — though the smoke only verifies `tools/list` discovery, not a compact invocation). The unit gate (`dotnet test --filter "Category!=Integration"`) is the `@code` return gate. @qa runs the full gate (with Docker) — including the updated HostSmokeTests assertion (`.Be(4)` + `memory_compact` containment). AC-3 (`tools/list` returns 4 tools) is runtime-verified by @qa under the full gate; @code source-verifies the assertion edit in the diff.

13. **AGENTS.md build commands.** `dotnet build McpMemoryService.sln` + `dotnet test --filter "Category!=Integration"` (`.slnx` is the .NET 10 default solution format; the legacy `.sln` alias auto-discovers it — AGENTS.md §2 documents `McpMemoryService.sln`). Existing M3 `DtoValidationTests.MemoryCompactInput_DefaultBatchSize_Is20` (tests/McpMemoryService.Tests/Models/DtoValidationTests.cs — if present) MUST still pass after the `[JsonPropertyName]` edit — the test constructs `new MemoryCompactInput { ProjectId = "p" }` and asserts `BatchSize == 20` (the default is unchanged by the attribute addition; verify in the unit gate).

14. **`MemoryPayload` + `Metadata` property names — VERIFY against the realized source before compiling.** The summary-payload composition reads/writes `MemoryPayload.ProjectId/SessionId/AgentRole/EntryType/Timestamp/Content/Tags/Metadata` + `Metadata.Session`. `@code` MUST read `src/McpMemoryService/Models/MemoryPayload.cs` + `src/McpMemoryService/Models/Metadata.cs` and confirm the exact property names — if any differ (e.g., `SessionId` named `Session`, or `Metadata.Tags` vs `MemoryPayload.Tags`), adjust the construction. The construction is mechanical — no business logic. The `MemoryEntry.PointId` (`Guid`) used for `sourceIds = entries.Select(e => e.PointId).ToList()` is verified (M5 round-trip).

15. **Catch-handler log severity — Warning vs Error (reconciling with M7/M8 convention).** The named LLM catches (TaskCanceledException/HttpRequestException) are TRANSACTIONAL GUARDS — `LogWarning` is the spec-faithful choice (M9 spec §Contracts comment says "log [IMP:2] LLM timeout" / "LLM HTTP error" — warning severity). HOWEVER, the M7 `MemoryGetStatsTool` catch handler uses `LogError` (verified in `MemoryGetStatsTool.cs`). For the post-LLM `catch (Exception)` (qdrant_or_embedding_failure), `LogError` is appropriate (a post-LLM failure means the LLM succeeded but the summary-upsert or delete failed — this is a more severe state than the LLM being unavailable, which is the designed-for degraded path). Recommended: LLM named catches → `LogWarning`; post-LLM generic catch → `LogError`. The `[FATAL]` token in the log message preserves the LDD step-naming convention regardless of severity. The AC checks the `[IMP:2]`/`[IMP:3]` marker presence, not the severity — `@code` picks whichever matches the existing tool-catch-handler convention; the recommended split (Warning for LLM, Error for post-LLM) reads cleanest.

16. **No `#pragma warning disable`.** Build warnings = AC-1 failure. Watch for: CS8625 nullable (the catch handler's `MemoryCompactOutput` construction with nullable `Reason`/`Available`/`Required`/`SourceCount`/`SummaryPointId`/`Error` — fine, they are `int?`/`string?`); IDE0005 unused usings (add `using System.Text.Json.Serialization;` ONLY in the DTO files that gain `[JsonPropertyName]`; `using System.Linq;` in the tool file — `ImplicitUsings` covers `System.Linq` since .NET 6, verify; if `System.Linq` resolves without the using, do NOT add it); CA1062 nullable argument validation (the `InputValidator.ValidateCompact` guards; the tool method does not — that's fine). The `using ModelContextProtocol.Server;` in the tool file is needed for `[McpServerToolType]`/`[McpServerTool]`.

17. **Test categorisation — UNCATEGORISED for tool unit tests (~10..12 tests).** They use `Mock<IQdrantService>` + `Mock<ILlmSummarizerService>` + `Mock<IEmbeddingService>` + `NullLogger<T>.Instance` — NO real Qdrant/Docker/ONNX/LLM. They MUST run in the unit gate (`dotnet test --filter "Category!=Integration"`). Evolved unit count: M3 (16) + M5 mapping (5) + M6 (6) + M7 (≈10 capture/stats + SnakeCaseTransportTests) + M8 (~10..12 retrieve) + **M9 (~10..12 compact)** = total green in the unit gate. HostSmokeTests stays `Category=Integration` (only the assertion block touched by M9) — its 3→4 assertion update lives in the integration-only path; @qa exercises it under the full gate. The new `Tools/MemoryCompactToolTests.cs` does NOT need a `.csproj` edit — SDK auto-discovers (matches M7/M8 `Tools/` precedent).

18. **profile.md consistency.** Plan prose is technical English (matches the M5/M6/M7/M8 plan style). No Russian summary header required.

19. **Web search (optional) — none required for M9.** All M9 architectural decisions (instance `[McpServerToolType]`+`[McpServerTool]`+ctor DI per mem-006; transactional LLM-named catches + post-LLM generic catch per ADR-003/005; non-blocking insufficient_data per ADR-004; `[JsonPropertyName]` rung-d preemptive per mem-027/mem-028; `IQdrantService.GetBatchForCompactAsync`/`UpsertAsync`/`DeleteAsync` signatures; `ILlmSummarizerService.SummarizeAsync` throws TaskCanceledException/HttpRequestException per M6; `IEmbeddingService.EmbedAsync`+`Dimension` per M4; HostSmokeTests 3→4 assertion update; Program.cs AddTransient additive) are derived from the M9 spec + SPEC §4.4/§7 + AGENTS.md ADR-001/002/003/004/005/006/010/011 + mem-006/027/028 + the realized M2..M8 source. The `IQdrantService.GetBatchForCompactAsync` signature (IQdrantService.cs L97-100), `UpsertAsync` (L46), `DeleteAsync` (L80); the `ILlmSummarizerService.SummarizeAsync` signature (ILlmSummarizerService.cs L34); the `InputValidator.ValidateCompact` body (InputValidator.cs L95-104); and the `MemoryCompactInput`/`MemoryCompactOutput` record shapes are VERIFIED in the codebase (read before drafting this plan) — no `[UNVERIFIED_VERSION]` tags needed for M9. If `@code`/`@debug` discover a divergence (e.g., `MemoryPayload.SessionId` named `Session`, or `Metadata.Session` named differently), the fix is a mechanical property-name adjustment in the summary-payload construction — documented in the bug-fix context, not web-searched.

---

## M10 — Error handling + resilience + LDD logging

| Field | Value |
|---|---|
| Current Milestone | **M10 — GlobalExceptionMiddleware + QdrantResiliencePolicy + EmbeddingResiliencePolicy + LddMarkers constants; ADR-005 resilience hardening across the 4 MCP tools (capture/get_stats/retrieve/compact)** |
| Status | PLAN_READY (awaiting `@code scope=impl:M10`) |
| Previous State | M9 — **SUCCESS** (per `tests/qa_report.md`: 67/67 unit tests, all 14 ACs, 4-tool MCP quartet complete). `.test_counter.json` counter=0. M10 is the next DAG node (`M9 → M10`). |
| Milestone Deps | **M4 (DONE — `OnnxEmbeddingService.ctor`-throws-on-load = Exit 1; `EmbedAsync` rethrows runtime `OnnxRuntimeException`/`InvalidOperationException`), M5 (DONE — `IQdrantService` methods throw `RpcException`/`QdrantException` on failure; do-not-swallow service), M6 (DONE — `LlmSummarizerService` throws `TaskCanceledException`/`HttpRequestException`), M7 (DONE — `MemoryCaptureTool`+`MemoryGetStatsTool` tool-level `catch(Exception)` silent-fallback), M8 (DONE — `MemoryRetrieveTool` tool-level catch → empty Results), M9 (DONE — `MemoryCompactTool` named LLM catches + post-LLM generic catch).** All deps DONE. |
| Dispatch Recommendation | **Single `@code scope=impl:M10` — NO decomposition.** 1 middleware class (1 `InvokeAsync` method) + 2 resilience-policy classes (1 generic method each) + 1 static constants class + 1 Program.cs edit (1 `app.UseMiddleware` line + 2 DI registrations — policies are stateless but registered for DI-injection into tools; alternative: static helpers — see Notes #3) + 4 tool edits (route the `IQdrantService`/`IEmbeddingService` calls through the policies; DO NOT rewrite the tool try/catch blocks) + 1 resilience test file (~7 tests). All deliverables are cohesive (one resilience + middleware layer + its wiring + its tests). Below the >5-new-methods threshold. **NO `## Decomposition` section** is appended. |
| Etap | Etap 1 (implement M1..M12). Etap 2 future. |

## ADRs Touched by M10

| ADR | Decision | M10 Action |
|---|---|---|
| **ADR-005** | Silent fallback on Qdrant/ONNX/LLM unavailability; ONNX fail at startup → Exit 1; ONNX fail at runtime → caught by middleware (no connection break) | **The governing policy of M10.** M10 implements the OUTER safety net (`GlobalExceptionMiddleware`) and the EXTERNAL parameterization of the inline-fallback behavior (`QdrantResiliencePolicy`/`EmbeddingResiliencePolicy`). The tool-level `catch(Exception)` silent-fallback DTOs (M7/M8/M9) are PRESERVED as the inner "second line" — NOT rewritten (anti-pattern: do NOT rewrite working tool try/catch). The middleware catches: (a) validation `ArgumentException` (deliberately propagated by tools — caller misuse, NOT silent) → JSON 400; (b) `InvalidOperationException`/`OnnxRuntimeException` from ONNX runtime that ESCAPES a tool (bug in a catch handler, or ONNX failing outside a tool's try) → JSON 503; (c) any unhandled `Exception` (bug) → JSON 500 with `{ "error": { "code": "internal_error", "message": "..." } }`. NO rethrow from the middleware → MCP Streamable-HTTP connection preserved. **See Notes #1 for the architect's reconciliation of the anti-pattern vs SPEC §7 "ONNX runtime → middleware" doctrine.** |
| **ADR-003** | Compact transactional guarantee (sources deleted only after LLM+embed+upsert succeed) | M10 ADDS the `LddMarkers` constants (`[IMP:4]` source-deleted, `[IMP:3]` summary-captured) that document the transactional checkpoints. The M9 `MemoryCompactTool` ALREADY implements the transactional logic (LLM named catches + post-LLM generic catch with delete-last ordering) — M10 does NOT touch it. `QdrantResiliencePolicy` is NOT applied to the `DeleteAsync` call inside compact (that would risk swallowing a delete-failure into a fallback — violating the transactional contract). See Notes #4. |
| **ADR-006** | .NET 10 target | Middleware is standard `Microsoft.AspNetCore.Http.RequestDelegate` pattern — net10.0-confirmed (exists since ASP.NET Core 1.0). No new NuGet refs. |
| **ADR-010** | MCP SDK = ModelContextProtocol 1.4.0 | The middleware sits OUTSIDE the MCP transport — it wraps the entire HTTP pipeline. `app.UseMiddleware<GlobalExceptionMiddleware>()` is registered BEFORE `app.MapMcp("/mcp")` and `app.MapHealthChecks("/health")` (middleware order: outermost-first). The MCP SDK's Streamable-HTTP endpoint is invoked INSIDE the middleware's `await _next(context)` call — any exception escaping the SDK is caught by the middleware and returned as a JSON error (no connection break). |

> **M10 invariants (must NOT regress):**
> 1. **The tool `try { ... } catch (Exception ex) { return failureDto; }` blocks from M7/M8/M9 are PRESERVED VERBATIM.** M10 does NOT narrow, expand, merge, or rewrite them. The `QdrantResiliencePolicy` is an EXTERNAL wrapper AROUND the `_qdrant.*` CALL SITE — it returns the fallback value (empty list / -1 / false / empty) so the tool's outer catch becomes redundant for the wrapped Qdrant calls but REMAINS as the defensive second line. The `EmbeddingResiliencePolicy` wraps `_embedding.EmbedAsync(...)` — it logs `[IMP:FATAL]` and **rethrows** (ONNX runtime is fatal per SPEC §7); the tool's outer `catch(Exception)` catches the rethrow and returns the DTO (silent-fallback — connection preserved).
> 2. **GlobalExceptionMiddleware is the OUTERMOST pipeline component — registered FIRST in `ConfigurePipeline`, before `MapHealthChecks`/`MapMcp`.** Middleware order in ASP.NET Core: earlier-registered = outermost. `app.UseMiddleware<GlobalExceptionMiddleware>()` MUST be the first line of `ConfigurePipeline` so it wraps the health + MCP endpoints. It must NOT rethrow (or the SDK transport closes the connection). It returns a structured JSON body + appropriate status code (400/500/503).
> 3. **LddMarkers constants are the SINGLE SOURCE OF TRUTH for `[IMP:N]` strings.** M10 introduces `src/McpMemoryService/Logging/LddMarkers.cs` with `public const string` fields per the M10 spec §LddMarkers contract. The existing inline string literals (`"[IMP:1]"`, `"[IMP:2]"`, etc.) in the 4 tools + 3 services are REPLACED by `LddMarkers.X` references — a MECHANICAL find-replace, NO logic change. The markers' semantic meaning is unchanged (e.g., `[IMP:1][CaptureAsync][PROGRESS]` → `LddMarkers.CaptureEntry + "[CaptureAsync][PROGRESS]..."` — or the policy concatenates). @code picks the cleanest interpolation. See Notes #5.
> 4. **Program.cs scars INTACT.** The M2 BUG_FIX_CONTEXT blocks (`McpServerOptions` post-configure, the M7 `WithListToolsHandler`→`WithToolsFromAssembly` swap scar + `[IMP:M7] supersede` marker, `MapMcp("/mcp")`), the M4/M5/M6 DI lines, the M7/M8/M9 tool-registration `AddTransient` lines, and the `[IMP:9][ConfigureServices][SUCCESS]` marker are ALL preserved. M10's edit is STRICTLY ADDITIVE: (a) 2 `services.AddSingleton<QdrantResiliencePolicy>()` + `services.AddSingleton<EmbeddingResiliencePolicy>()` lines (or static — see Notes #3) added after the M9 tool-registration block (or after the resilience policies are needed); (b) 1 `app.UseMiddleware<GlobalExceptionMiddleware>()` line added as the FIRST line of `ConfigurePipeline` (before `app.MapHealthChecks("/health")`).
> 5. **NO new NuGet package refs.** `Microsoft.Extensions.Http` + `Microsoft.Extensions.Hosting` + `Microsoft.AspNetCore.Http.Abstractions` (for `RequestDelegate`/`IMiddleware`) are ALREADY in the csproj from M2. Middleware via the convention-based `InvokeAsync(HttpContext, RequestDelegate)` pattern requires NO additional package (the `Microsoft.AspNetCore.Http.Features` + `Microsoft.Extensions.Logging` are transitively present from `ModelContextProtocol.AspNetCore`). `dotnet build` MUST yield 0W 0E.

---

## PURPOSE (M10)

Implement the cross-cutting error-handling + resilience tier of McpMemoryService per SPEC §7: (1) a `GlobalExceptionMiddleware` ASP.NET Core middleware (outermost pipeline component) that catches all unhandled exceptions escaping the 4 MCP tools / MCP transport, logs `[IMP:CRITICAL]`, and returns a structured JSON error response (400 for validation `ArgumentException`, 503 for `InvalidOperationException`/`OnnxRuntimeException` from ONNX runtime, 500 for any other unhandled exception) WITHOUT rethrowing — preserving the MCP Streamable-HTTP connection integrity (ADR-005); (2) a `QdrantResiliencePolicy` (generic `ExecuteWithFallbackAsync<T>` wrapper) that wraps the `IQdrantService` calls inside the 4 tools and returns the documented fallback value (empty list for retrieve/compact-batch, `Count=-1` for get_stats, `Success=false`-equivalent handled by the existing tool catch — see Notes #3) on Qdrant-down, logging `[IMP:WARN]` — the tool's existing `catch(Exception)` second-line defense is PRESERVED; (3) an `EmbeddingResiliencePolicy` that wraps `IEmbeddingService.EmbedAsync` calls, logs `[IMP:FATAL]` on ONNX runtime failure, and RETHROWS (ONNX runtime is fatal per SPEC §7 — the tool catch converts it to a DTO so the connection is preserved ADR-005; if a bug escapes the catch, the middleware catches → 503); (4) a `LddMarkers` static-constants class that centralizes every `[IMP:N]` marker const per the M10 spec §LddMarkers contract, replacing the scattered inline string literals across the 4 tools + `OnnxEmbeddingService` + `QdrantService` + `LlmSummarizerService`; (5) a `Program.cs` edit that registers the middleware in `ConfigurePipeline` (FIRST line) + the 2 resilience policies in `ConfigureServices`; (6) a `ResilienceTests.cs` test file (~7 unit tests) that verifies the fallback behavior + middleware JSON responses + LDD-marker emission, with NO real Qdrant/ONNX/Docker required (mocked services + `TestHost`/`HttpContext` for the middleware).

---

## 1. Draft Code Graph (M10)

> M10 introduces the FIRST cross-cutting infrastructure tier (middleware + resilience). The middleware follows `csharp-conventions` `#region` structuring (Fields / Constructors / InvokeAsync). The two policies are simple sealed classes with one generic async method each. `LddMarkers` is a static constants class. The 4 tool edits are MECHANICAL call-site wrapping — the tool try/catch logic is NOT restructured.

```xml
<DraftCodeGraph> (M10 subset)
  <!-- ========== GLOBAL EXCEPTION MIDDLEWARE ========== -->
  <src_McpMemoryService_Middleware_GlobalExceptionMiddleware_cs FILE="src/McpMemoryService/Middleware/GlobalExceptionMiddleware.cs" TYPE="ASPNET_MIDDLEWARE">
    <keywords>RequestDelegate, InvokeAsync, GlobalExceptionMiddleware, unhandled exception, JSON error response, 400 500 503, ADR-005, no-rethrow, MCP connection integrity, IMP:CRITICAL/IMP:FATAL, ILogger, System.Text.Json</keywords>
    <annotation>public sealed class GlobalExceptionMiddleware. MODULE_CONTRACT header per csharp-conventions (DOMAIN(Middleware): global unhandled-exception → JSON error; CONCEPT(Middleware): outermost RequestDelegate wrapper; TECH(ASP.NET Core, ADR-005, SPEC §7)). #region Fields: RequestDelegate _next (readonly), ILogger&lt;GlobalExceptionMiddleware&gt; _logger (readonly). #region Constructors: ctor(RequestDelegate, ILogger&lt;GlobalExceptionMiddleware&gt;) — assign fields. #region InvokeAsync: public async Task InvokeAsync(HttpContext context). Algorithm: `try { await _next(context); } catch (Exception ex) { ... }`. The catch handler: (1) classify — `if (ex is ArgumentException) statusCode=400; else if (ex is InvalidOperationException || ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException) statusCode=503; else statusCode=500`; (2) log — `_logger.LogError(ex, "{Marker}[GlobalExceptionMiddleware][FATAL] unhandled exception type={Type} message={Message}", LddMarkers.UnhandledException, ex.GetType().Name, ex.Message)` (use `LogCritical` for the 500 default, `LogError` for 400/503); (3) write the response — `context.Response.StatusCode = statusCode; context.Response.ContentType = "application/json"; var body = JsonSerializer.Serialize(new { error = new { code = statusCode==400 ? "validation_error" : (statusCode==503 ? "service_unavailable" : "internal_error"), message = ex.Message } }, JsonOptions); await context.Response.WriteAsync(body);` — DO NOT rethrow. Reusing the `JsonSerializerOptions` cached in `LlmSummarizerService` is OPTIONAL — @code can build a local `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }` for the JSON err body (snake_case keys: `error.code`, `error.message`) matching the SDK transport contract. The middleware MUST handle the case where the response has already started (`context.Response.HasStarted`) — if so, log CRITICAL + return (cannot write headers after start) — this is a defensive guard for streaming edge cases. NOTE: the middleware CANNOT be an IMiddleware-factory class (would require `UseMiddleware&lt;T&gt;()` parameterless registration which works for both convention + IMiddleware) — @code picks the convention-based `InvokeAsync(HttpContext)` pattern (simpler, no DI scope concerns; the middleware only depends on the logger which is injected via ctor by `UseMiddleware&lt;GlobalExceptionMiddleware&gt;`).</annotation>
    <src_McpMemoryService_Middleware_GlobalExceptionMiddleware_next_FIELD NAME="_next" TYPE="FIELD" />
    <src_McpMemoryService_Middleware_GlobalExceptionMiddleware_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Middleware_GlobalExceptionMiddleware_InvokeAsync_METHOD NAME="InvokeAsync" TYPE="PUBLIC_ASYNC_METHOD" IMP="IMP:CRITICAL,IMP:FATAL">
      <annotation>try { await _next(context); } catch (Exception ex) { classify (ArgumentException→400 / InvalidOperationException|OnnxRuntimeException→503 / else→500); log (LddMarkers.UnhandledException for 500 + LddMarkers.OnnxFatal for 503, or both); write JSON body; NO rethrow. HasStarted guard for streaming edge cases. }</annotation>
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Logging_LddMarkers_cs" TYPE="USES_CONSTANTS" />
      </CrossLinks>
    </src_McpMemoryService_Middleware_GlobalExceptionMiddleware_InvokeAsync_METHOD>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigurePipeline_METHOD" TYPE="REGISTERED_IN" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="WRAPS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="WRAPS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="WRAPS" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="WRAPS" />
      <Link TARGET="M10_Resilience_PlANNED" TYPE="RESOLVES" />
    </CrossLinks>
  </src_McpMemoryService_Middleware_GlobalExceptionMiddleware_cs>

  <!-- ========== QDRANT RESILIENCE POLICY ========== -->
  <src_McpMemoryService_Resilience_QdrantResiliencePolicy_cs FILE="src/McpMemoryService/Resilience/QdrantResiliencePolicy.cs" TYPE="RESILIENCE_POLICY">
    <keywords>QdrantResiliencePolicy, ExecuteWithFallbackAsync, generic, RpcException, QdrantException, fallback value, ADR-005, IMP:WARN, ILogger, silent-degradation</keywords>
    <annotation>public sealed class QdrantResiliencePolicy. MODULE_CONTRACT header per csharp-conventions (DOMAIN(Resilience): Qdrant-down silent fallback; CONCEPT(Policy): generic async wrapper returning fallback on Qdrant exception; TECH(ADR-005, SPEC §7)). #region Fields: ILogger&lt;QdrantResiliencePolicy&gt; _logger (readonly). #region Constructors: ctor(ILogger&lt;QdrantResiliencePolicy&gt;) — assign. #region ExecuteWithFallbackAsync: `public async Task&lt;T&gt; ExecuteWithFallbackAsync&lt;T&gt;(Func&lt;Task&lt;T&gt;&gt; action, T fallbackValue, string operationName, CancellationToken cancellationToken = default)`. Algorithm: `try { return await action(); } catch (Exception ex) { _logger.LogWarning(ex, "[IMP:WARN][QdrantResiliencePolicy][FATAL] Qdrant operation {Op} failed — ADR-005 silent-fallback to default", operationName); return fallbackValue; }`. The catch is BROAD (`Exception`) — this mirrors the tool-level `catch(Exception)` discipline but LOCALIZES it to the Qdrant call (so embedding failures, which happen in the SAME tool method, are NOT swallowed by the Qdrant policy). The `operationName` string ("SearchAsync"/"CountAsync"/"UpsertAsync"/"GetBatchForCompactAsync") is used in the log message for traceability. The `fallbackValue` is supplied by the CALLER (the tool) — e.g., `Array.Empty&lt;MemoryEntry&gt;()` for retrieve, `-1` for get_stats, `Array.Empty&lt;MemoryEntry&gt;()` for compact-batch. For capture's UpsertAsync (which returns `Task`, not `Task&lt;T&gt;`), use the non-generic sibling `ExecuteWithFallbackAsync(Func&lt;Task&gt; action, string operationName, CancellationToken)` → returns `Task` (the tool's existing catch handles the upsert-fail → Success=false; OR @code wraps UpsertAsync as `Task&lt;bool&gt;` returning true, with fallback `false` — see Notes #3). NAMESPACE: `McpMemoryService.Resilience`.</annotation>
    <src_McpMemoryService_Resilience_QdrantResiliencePolicy_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Resilience_QdrantResiliencePolicy_ExecuteWithFallbackAsync_OfT_METHOD METHOD NAME="ExecuteWithFallbackAsync&lt;T&gt;" TYPE="PUBLIC_ASYNC_GENERIC_METHOD" IMP="IMP:WARN">
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="CALLED_BY" />
        <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="CALLED_BY" />
        <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="CALLED_BY" />
        <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="CALLED_BY" />
      </CrossLinks>
    </src_McpMemoryService_Resilience_QdrantResiliencePolicy_ExecuteWithFallbackAsync_OfT_Method>
    <CrossLinks>
      <Link TARGET="M10_Resilience_PlANNED" TYPE="RESOLVES" />
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="REGISTERED_IN" />
    </CrossLinks>
  </src_McpMemoryService_Resilience_QdrantResiliencePolicy_cs>

  <!-- ========== EMBEDDING RESILIENCE POLICY ========== -->
  <src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_cs FILE="src/McpMemoryService/Resilience/EmbeddingResiliencePolicy.cs" TYPE="RESILIENCE_POLICY">
    <keywords>EmbeddingResiliencePolicy, ExecuteAsync, OnnxRuntimeException, InvalidOperationException, rethrow, ADR-005, IMP:FATAL, ILogger, ONNX-runtime-fatal, do-not-swallow</keywords>
    <annotation>public sealed class EmbeddingResiliencePolicy. MODULE_CONTRACT header per csharp-conventions (DOMAIN(Resilience): ONNX-runtime fallback wrapper; CONCEPT(Policy): log + rethrow — ONNX runtime is fatal per SPEC §7; TECH(ADR-005, SPEC §7)). #region Fields: ILogger&lt;EmbeddingResiliencePolicy&gt; _logger (readonly). #region Constructors: ctor(ILogger&lt;EmbeddingResiliencePolicy&gt;). #region ExecuteAsync: `public async Task&lt;float[]&gt; ExecuteAsync(Func&lt;Task&lt;float[]&gt;&gt; action, string operationName, CancellationToken cancellationToken = default)`. Algorithm: `try { return await action(); } catch (Exception ex) { _logger.LogCritical(ex, "{Marker}[EmbeddingResiliencePolicy][FATAL] ONNX embedding operation {Op} failed — rethrowing (ONNX runtime is fatal per SPEC §7)", LddMarkers.OnnxFatal, operationName); throw; }` — **RETHROW** (do NOT swallow). The rethrow propagates to the tool's outer `catch(Exception)` → DTO (silent-fallback at tool level — ADR-005). If a bug escapes the tool catch, the middleware catches it → 503. This mirrors the M4 `OnnxEmbeddingService.EmbedAsync` invariant (do-not-swallow; middleware catches later). NAMESPACE: `McpMemoryService.Resilience`. The `operationName` ("EmbedAsync(content)"/"EmbedAsync(summary)") is for traceability. The `LddMarkers.OnnxFatal` const = `"[IMP:FATAL]"` per the M10 spec.</annotation>
    <src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_logger_FIELD NAME="_logger" TYPE="FIELD" />
    <src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_ExecuteAsync_METHOD NAME="ExecuteAsync" TYPE="PUBLIC_ASYNC_METHOD" IMP="IMP:FATAL">
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="CALLED_BY" />
        <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="CALLED_BY" />
        <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="CALLED_BY" />
        <Link TARGET="src_McpMemoryService_Logging_LddMarkers_cs" TYPE="USES_CONSTANTS" />
      </CrossLinks>
    </src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_ExecuteAsync_METHOD>
    <CrossLinks>
      <Link TARGET="M10_Resilience_PlANNED" TYPE="RESOLVES" />
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="REGISTERED_IN" />
    </CrossLinks>
  </src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_cs>

  <!-- ========== LDD MARKERS CONSTANTS ========== -->
  <src_McpMemoryService_Logging_LddMarkers_cs FILE="src/McpMemoryService/Logging/LddMarkers.cs" TYPE="STATIC_CONSTANTS">
    <keywords>LddMarkers, constants, IMP markers, single source of truth, replace inline strings</keywords>
    <annotation>public static class LddMarkers. MODULE_CONTRACT header per csharp-conventions (DOMAIN(Logging): LDD marker constants; CONCEPT(Static): public const string per marker; TECH(LDD 2.0 — [IMP:N] format)). #region Capture: `public const string CaptureEntry = "[IMP:1]"; public const string CaptureEmbeddingGenerated = "[IMP:2]"; public const string CaptureUpserted = "[IMP:3]"; public const string CaptureFallback = "[IMP:4]";`. #region Retrieve: `public const string RetrieveEntry = "[IMP:1]"; public const string RetrieveEmbedding = "[IMP:2]"; public const string RetrieveSearchComplete = "[IMP:3]"; public const string RetrieveFallbackEmpty = "[IMP:4]";`. #region Compact: `public const string CompactBatchFetched = "[IMP:1]"; public const string CompactLlmCall = "[IMP:2]"; public const string CompactSummaryCaptured = "[IMP:3]"; public const string CompactSourceDeleted = "[IMP:4]"; public const string CompactLlmTimeout = "[IMP:5]"; public const string CompactLlm5xx = "[IMP:6]"; public const string CompactSkipped = "[IMP:7]";`. #region Embedding: `public const string EmbeddingEntry = "[IMP:1]"; public const string EmbeddingTokenized = "[IMP:2]"; public const string EmbeddingInference = "[IMP:3]"; public const string EmbeddingMeanPool = "[IMP:4]"; public const string EmbeddingL2Norm = "[IMP:5]";`. #region Qdrant: `public const string QdrantCollectionReady = "[IMP:1]"; public const string QdrantUpserted = "[IMP:2]"; public const string QdrantSearched = "[IMP:3]"; public const string QdrantDeleted = "[IMP:4]"; public const string QdrantBatchFetched = "[IMP:5]";`. #region Critical: `public const string UnhandledException = "[IMP:CRITICAL]"; public const string OnnxFatal = "[IMP:FATAL]"; public const string QdrantWarn = "[IMP:WARN]";`. NAMESPACE: `McpMemoryService.Logging`. NOTE on marker REUSE — the spec's contract (M10 spec lines 103-148) reuses `[IMP:1]`/`[IMP:2]`/etc. across different tools (e.g., `CaptureEntry = "[IMP:1]"` AND `RetrieveEntry = "[IMP:1]"` AND `CompactBatchFetched = "[IMP:1]"`). This is INTENTIONAL — the IMP N is a SEQUENCE-NUMBER PER METHOD, not a globally-unique ID. The disambiguation lives in the method-name token in the log message (`[IMP:1][CaptureAsync]...` vs `[IMP:1][RetrieveAsync]...` vs `[IMP:1][CompactAsync]...`). The existing inline strings in the 4 tools + 3 services already follow this convention. M10's find-replace is mechanical: `"[IMP:1]"` → `LddMarkers.&lt;Region-specific-name&gt;` where the region is determined by WHICH file/method the literal appears in. @code does NOT attempt to globally-unique-ify the numbers (would break the LDD 2.0 spec — see mode-code skill).</annotation>
    <src_McpMemoryService_Logging_LddMarkers_CaptureEntry_CONST NAME="CaptureEntry" TYPE="PUBLIC_CONST" VALUE="[IMP:1]" />
    <src_McpMemoryService_Logging_LddMarkers_UnhandledException_CONST NAME="UnhandledException" TYPE="PUBLIC_CONST" VALUE="[IMP:CRITICAL]" />
    <src_McpMemoryService_Logging_LddMarkers_OnnxFatal_CONST NAME="OnnxFatal" TYPE="PUBLIC_CONST" VALUE="[IMP:FATAL]" />
    <src_McpMemoryService_Logging_LddMarkers_QdrantWarn_CONST NAME="QdrantWarn" TYPE="PUBLIC_CONST" VALUE="[IMP:WARN]" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Middleware_GlobalExceptionMiddleware_cs" TYPE="USED_BY" />
      <Link TARGET="src_McpMemoryService_Resilience_QdrantResiliencePolicy_cs" TYPE="USED_BY" />
      <Link TARGET="src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_cs" TYPE="USED_BY" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="USED_BY" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="USED_BY" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="USED_BY" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="USED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Logging_LddMarkers_cs>

  <!-- ========== PROGRAM.CS EDIT (ConfigurePipeline middleware-first + ConfigureServices policy registration) ========== -->
  <src_McpMemoryService_Program_cs_ConfigurePipeline_M10_EDIT FILE="src/McpMemoryService/Program.cs" TYPE="PIPELINE_EDIT">
    <annotation>STRICTLY ADDITIVE — TWO changes:
1. In `ConfigurePipeline(WebApplication app)`, ADD as the FIRST line (BEFORE `// [IMP:1][ConfigurePipeline][INIT] Mapping health check endpoint` + `app.MapHealthChecks("/health")`):
   ```csharp
   // [IMP:M10][ConfigurePipeline][INIT] Global exception middleware — outermost pipeline component (catches
   //   unhandled exceptions escaping tools + MCP transport; returns JSON 400/500/503, NO rethrow — preserves
   //   MCP Streamable-HTTP connection integrity per ADR-005/SPEC §7). Registered FIRST so it wraps health + MCP endpoints.
   app.UseMiddleware&lt;McpMemoryService.Middleware.GlobalExceptionMiddleware&gt;();
   ```
   ORDER IS CRITICAL — `UseMiddleware` = outermost → MUST come before `MapHealthChecks` + `MapMcp`. ASP.NET Core middleware ordering: earlier-registered middleware wraps later-registered. The MCP endpoint (`app.MapMcp("/mcp")`) is INSIDE the middleware's `await _next(context)` — exceptions escaping the SDK transport → middleware catches → JSON error (no connection break).
2. In `ConfigureServices(IServiceCollection services, IConfiguration configuration)`, ADD after the M9 tool-registration block (the `services.AddTransient&lt;McpMemoryService.Tools.MemoryCompactTool&gt;();` line at L170) + BEFORE the M5 IQdrantService block (or anywhere consistent — policies are stateless, resolution-order-independent):
   ```csharp
   // [IMP:M10][ConfigureServices][OPTION] Register resilience policies (Singleton — stateless wrappers) — ADR-005 silent-fallback parameterization
   services.AddSingleton&lt;McpMemoryService.Resilience.QdrantResiliencePolicy&gt;();
   services.AddSingleton&lt;McpMemoryService.Resilience.EmbeddingResiliencePolicy&gt;();
   ```
   DO NOT touch: the M2 BUG_FIX_CONTEXT scars (McpServerOptions post-configure + snake-case scar, the WithListToolsHandler→WithToolsFromAssembly swap + [IMP:M7] supersede marker, MapMcp("/mcp") scar), the M4 IEmbeddingService line, the M5 IQdrantService/QdrantCollectionInitializer lines, the M6 LlamaCpp HttpClient + ILlmSummarizerService lines, the M7/M8/M9 tool-registration AddTransient lines, or the [IMP:9][ConfigureServices][SUCCESS] marker. `using McpMemoryService.Middleware;` + `using McpMemoryService.Resilience;` MAY be added to the usings block (verify at compile — `ImplicitUsings` does NOT auto-include project namespaces; the fully-qualified form `McpMemoryService.Middleware.GlobalExceptionMiddleware` AVOIDS the using entirely — @code picks whichever builds cleanest with 0W). The middleware/policy DI registration MUST come before the [IMP:9] SUCCESS marker to keep the marker semantics ("registration complete").</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigurePipeline_METHOD" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_METHOD" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Middleware_GlobalExceptionMiddleware_cs" TYPE="REGISTERS" />
      <Link TARGET="src_McpMemoryService_Resilience_QdrantResiliencePolicy_cs" TYPE="REGISTERS" />
      <Link TARGET="src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_cs" TYPE="REGISTERS" />
    </CrossLinks>
  </src_McpMemoryService_Program_cs_ConfigurePipeline_M10_EDIT>

  <!-- ========== TOOL EDITS (MECHANICAL call-site wrapping — NOT try/catch rewrite) ========== -->
  <src_McpMemoryService_Tools_M10_EDITS FILE="src/McpMemoryService/Tools/*.cs" TYPE="MECHANICAL_WRAP">
    <annotation>The 4 tool classes are EDITED ADDITIVELY — their `[McpServerToolType]`/`[McpServerTool]`/ctor-DI/`try{...}catch(Exception){...}` structure is PRESERVED. The edits inject a `_qdrantPolicy` (QdrantResiliencePolicy) and/or `_embeddingPolicy` (EmbeddingResiliencePolicy) into the ctor and WRAP the `_qdrant.*`/`_embedding.*` call sites with the policy's `Execute*` method:

1. MemoryCaptureTool.cs — ctor ADDS `QdrantResiliencePolicy qdrantPolicy, EmbeddingResiliencePolicy embeddingPolicy` params (after qdrant + logger); assigns `_qdrantPolicy`/`_embeddingPolicy`. Inside `CaptureAsync`'s try-block: replace `_embedding.EmbedAsync(input.Content, ct)` with `_embeddingPolicy.ExecuteAsync(() => _embedding.EmbedAsync(input.Content, ct), "CaptureAsync.EmbedAsync", ct)` (rethrows — tool catch handles DTO); replace `_qdrant.UpsertAsync(pointId, vector, payload, ct)` with `_qdrantPolicy.ExecuteWithFallbackAsync(() => _qdrant.UpsertAsync(pointId, vector, payload, ct), fallbackValue: false, "CaptureAsync.UpsertAsync", ct)` — the bool fallback is then `if (!upserted) return new MemoryCaptureOutput { Success=false, ... }` (DELIBERATE early-return INSIDE the try — the existing catch is a defense-in-depth). The `Debug.Assert(vector.Length == _embedding.Dimension)` STAYS. The existing `catch (Exception ex) { ... return failure DTO }` STAYS verbatim. Replace inline `[IMP:1]`/`[IMP:2]` string literals with `LddMarkers.CaptureEntry`/`LddMarkers.CaptureUpserted`/`LddMarkers.CaptureFallback`.

2. MemoryGetStatsTool.cs — ctor ADDS `QdrantResiliencePolicy qdrantPolicy`; assigns `_qdrantPolicy`. Inside `GetStatsAsync`'s try-block: replace `_qdrant.CountAsync(input.ProjectId, input.EntryType, ct)` with `_qdrantPolicy.ExecuteWithFallbackAsync(() => _qdrant.CountAsync(input.ProjectId, input.EntryType, ct), fallbackValue: -1, "GetStatsAsync.CountAsync", ct)`. The existing `catch (Exception ex) { return new { Count = -1 }; }` STAYS verbatim. Replace inline `[IMP:1]`/`[IMP:2]` string literals with `LddMarkers.RetrieveEntry`/`LddMarkers.QdrantSearched` (REUSE the Retrieve region names is WRONG — for get_stats use the Qdrant region: `LddMarkers.QdrantSearched` for success — ACTUALLY `CountAsync` has no dedicated marker; @code uses `LddMarkers.QdrantWarn` for the fallback path OR keeps the inline `[IMP:2]` for the get_stats-specific success — see Notes #5 for the disambiguation). The fallback path uses `LddMarkers.QdrantWarn`.

3. MemoryRetrieveTool.cs — ctor ADDS `QdrantResiliencePolicy qdrantPolicy, EmbeddingResiliencePolicy embeddingPolicy`; assigns. Inside `RetrieveAsync`'s try-block: replace `_embedding.EmbedAsync(input.Query, ct)` with `_embeddingPolicy.ExecuteAsync(() => _embedding.EmbedAsync(input.Query, ct), "RetrieveAsync.EmbedAsync", ct)` (rethrows — tool catch handles empty Results); replace `_qdrant.SearchAsync(vector, projectId, agentRoleFilter, entryTypeFilter, limit, ct)` with `_qdrantPolicy.ExecuteWithFallbackAsync(() => _qdrant.SearchAsync(vector, input.ProjectId, input.AgentRoleFilter, input.EntryTypeFilter, input.Limit, ct), fallbackValue: Array.Empty&lt;MemoryEntry&gt;(), "RetrieveAsync.SearchAsync", ct)` → empty list on Qdrant-down → `entries.Count == 0` → `results` empty. The existing `catch (Exception ex) { return new { Results = Array.Empty&lt;MemoryRetrieveResult&gt;() }; }` STAYS verbatim. Replace inline `[IMP:1]`/`[IMP:2]`/`[IMP:3]` literals with `LddMarkers.RetrieveEmbedding`/`LddMarkers.RetrieveSearchComplete`/`LddMarkers.RetrieveFallbackEmpty`.

4. MemoryCompactTool.cs — ctor ADDS `QdrantResiliencePolicy qdrantPolicy, EmbeddingResiliencePolicy embeddingPolicy`; assigns. Wrap the `GetBatchForCompactAsync` call (L89-90 — currently `var entries = await _qdrantService.GetBatchForCompactAsync(...)`) with `_qdrantPolicy.ExecuteWithFallbackAsync(() => _qdrantService.GetBatchForCompactAsync(input.ProjectId, input.BatchSize, ct), fallbackValue: Array.Empty&lt;MemoryEntry&gt;(), "CompactAsync.GetBatchForCompactAsync", ct)` — empty fallback → `entries.Count==0` &lt; BatchSize → triggers the insufficient_data "skipped" path (ADR-004 — elegant: Qdrant-down on batch-fetch = "skipped", not "error"). Wrap the post-LLM `EmbedAsync(summary, ct)` call (L150) with `_embeddingPolicy.ExecuteAsync(...)` (rethrows — post-LLM catch handles qdrant_or_embedding_failure). Wrap the post-LLM `UpsertAsync(summaryPointId, summaryVector, summaryPayload, ct)` (L165) with `_qdrantPolicy.ExecuteWithFallbackAsync&lt;bool&gt;(() => { _qdrantService.UpsertAsync(...); return true; }, fallbackValue: false, "CompactAsync.UpsertAsync", ct)` — if fallback `false`, the tool's post-LLM catch OR a `if (!upserted) return error` handles it. DO NOT wrap `DeleteAsync` (L173) with the policy — the transactional guarantee ADR-003 requires a genuine throw on delete-failure to be reported as `reason="post_llm_failure"`/orphaned-summary state, NOT swallowed to `false`. The existing named LLM catches + post-LLM generic catch STAY verbatim. Replace inline `[IMP:1]`..`[IMP:7]` literals with `LddMarkers.Compact*` constants.

The DI container resolves the policies (registered Singleton in M10 ConfigureServices) into the tool ctors. The M7/M8/M9 UNIT TESTS construct the tools via ctor DIRECTLY — they MUST be UPDATED to pass `new QdrantResiliencePolicy(NullLogger&lt;QdrantResiliencePolicy&gt;.Instance)` + `new EmbeddingResiliencePolicy(NullLogger&lt;EmbeddingResiliencePolicy&gt;.Instance)` (or a `Mock&lt;QdrantResiliencePolicy&gt;` if the policy is made an interface — see Notes #3 for the @code decision). This is an ADDITIVE test-edit (new ctor params) — the existing test ASSERTIONS (Success==false / Count==-1 / empty Results / Status==error) STAY unchanged because the fallback semantics are IDENTICAL.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Resilience_QdrantResiliencePolicy_cs" TYPE="USES" />
      <Link TARGET="src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_cs" TYPE="USES" />
      <Link TARGET="src_McpMemoryService_Logging_LddMarkers_cs" TYPE="USES_CONSTANTS" />
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigureServices_M10_EDIT" TYPE="DI_PROVIDES" />
    </CrossLinks>
  </src_McpMemoryService_Tools_M10_EDITS>

  <!-- ========== RESILIENCE TESTS ========== -->
  <tests_McpMemoryService_Tests_Resilience_ResilienceTests_cs FILE="tests/McpMemoryService.Tests/Resilience/ResilienceTests.cs" TYPE="XUNIT_TEST">
    <keywords>ResilienceTests, QdrantResiliencePolicy, EmbeddingResiliencePolicy, GlobalExceptionMiddleware, mocked services, no Docker/ONNX available, ADR-005, fallback, IMP markers, UNCATEGORISED</keywords>
    <annotation>~7 unit tests, UNCATEGORISED (no [Trait], no Category=Integration — no docker/ONNX required; middleware tested via a lightweight `HttpContext` from `DefaultHttpContext` + a `RequestDelegate` that throws). Tests per M10 spec §Tests lines 194-258:

1) `Retrieve_QdrantDown_ReturnsEmptyResults_NoException` — build MemoryRetrieveTool with mock IQdrantService (Setup SearchAsync ThrowsAsync(new InvalidOperationException("qdrant dead"))) + mock IEmbeddingService + REAL QdrantResiliencePolicy + EmbeddingResiliencePolicy + NullLoggers; invoke RetrieveAsync(input) → result.Results empty, NO exception thrown (the policy swallows → fallback empty list; the tool catch never even fires).

2) `Capture_QdrantDown_ReturnsFailure_NoException` — mock IQdrantService (Setup UpsertAsync ThrowsAsync) → result.Success == false, result.Error != null, NO throw. Mirror of the M7 CaptureAsync_QdrantFails_ReturnsFailureNotThrow test (which is PRESERVED — the M7 test passes the policy ctor args too; the assertion is unchanged).

3) `GetStats_QdrantDown_ReturnsNegativeOne_NoException` — mock IQdrantService (Setup CountAsync ThrowsAsync) → result.Count == -1, NO throw. Mirror of the M7 GetStatsAsync_QdrantFails_ReturnsMinusOneNotThrow test.

4) `Compact_QdrantDownOnBatchFetch_ReturnsSkipped_NoDelete` — mock IQdrantService (Setup GetBatchForCompactAsync ThrowsAsync) → result.Status == "skipped", result.Reason == "insufficient_data" (because the fallback empty list triggers &lt; BatchSize — ADR-004 non-blocking). `mockQdrant.Verify(x => x.DeleteAsync(...), Times.Never)` + `mockLlm.Verify(Times.Never)` + `mockEmbedding.Verify(Times.Never)`. CRITICAL: this REINTERPRETS the M10 spec test `Compact_QdrantDownOnBatchFetch_ReturnsError_NoDelete` — the spec's "error/no-source-deletion" expectation is REINTERPRETED as "skipped/insufficient_data/no-source-deletion" because the `QdrantResiliencePolicy` returns an EMPTY LIST fallback for the batch-fetch, and `entries.Count &lt; BatchSize` → ADR-004 skipped path (NOT error). This is the ADR-004-aligned non-blocking behavior. See Notes #4.

5) `Retrieve_OnnxRuntime_PolicyRethrows_ToolCatches_ReturnsEmptyResults` — mock IEmbeddingService (Setup EmbedAsync ThrowsAsync(new InvalidOperationException("onnx died"))) + REAL EmbeddingResiliencePolicy → invoke RetrieveAsync → result.Results empty (the policy logged [IMP:FATAL] + rethrew; the tool's catch(Exception) caught → empty Results DTO — ADR-005 silent-fallback at tool level). NO exception escapes. The middleware is NOT exercised in this test path (the tool catch handles it). This test acknowledges the architect's reconciliation: ONNX runtime INSIDE a tool → silent DTO (NOT middleware-503) — see Notes #1 for the SPEC §7 tension. The middleware catch of ONNX runtime is PROVEN separately in test #6 via a probe endpoint.

6) `GlobalMiddleware_UnhandledException_ReturnsJsonError` — build `GlobalExceptionMiddleware` with a `RequestDelegate _next = _ => throw new InvalidOperationException("probe throw")` + NullLogger; invoke `await middleware.InvokeAsync(new DefaultHttpContext())`; assert `context.Response.StatusCode == 503` (InvalidOperationException → 503) + `context.Response.ContentType == "application/json"` + read the body → `JsonDocument.Parse(body)` → assert `root.GetProperty("error").GetProperty("code").GetString() == "service_unavailable"` + `root.GetProperty("error").GetProperty("message").GetString().Contains("probe throw")`. Variant sub-tests: (a) `ArgumentException` thrown → 400 / `code="validation_error"`; (b) generic `Exception` → 500 / `code="internal_error"`; (c) `OnnxRuntimeException` (if constructible without a real model — try `Microsoft.ML.OnnxRuntime.OnnxRuntimeException` ctor; if it requires native infra, use `InvalidOperationException` as the ONNX-runtime stand-in per the service's own invariant) → 503. The middleware DOES NOT rethrow (`await InvokeAsync` returns normally — the response is already written). NO `TestHost`/`WebApplicationFactory` required — `DefaultHttpContext` + a canned `RequestDelegate` is sufficient (lighter than `WebApplicationFactory&lt;Program&gt;` and keeps the test in the unit gate — `Category!=Integration`).

7) `AllTools_EmitLddMarkers` — for each of the 4 tools, invoke the tool with valid input + mocked services NO throws; capture the logger output via a `TestLogger` (a custom `ILogger&lt;T&gt;` capturing `LogInformation`/`LogWarning`/`LogError` calls into a `List&lt;string&gt;`) — assert the captured log entries contain the expected `LddMarkers.&lt;Tool&gt;*` constants (e.g., for capture: log entries contain `LddMarkers.CaptureEntry` + `LddMarkers.CaptureUpserted`). This proves the find-replace from inline strings to `LddMarkers.*` constants is COMPLETE + CORRECT (the marker constants are used, not stale inline literals). @code MAY use `Microsoft.Extensions.Logging.Testing` `XunitLogger`/`FakeLogger` (if available in net10.0 BCL; else a minimal 10-line `TestLogger&lt;T&gt; : ILogger&lt;T&gt;` capturing messages).

The middleware tests (#5/#6/#7-middleware) DO NOT require Docker or ONNX — they use `DefaultHttpContext` + a canned `RequestDelegate`. The policy tests (#1..#4) use Moq mocks. ALL ~7 tests are UNCATEGORISED → run in the unit gate (`dotnet test --filter "Category!=Integration"`). The new `Resilience/` test folder does NOT need a `.csproj` edit — SDK auto-discovers (matches the `Tools/`/`Models/`/`Services/` precedent).</annotation>
    <tests_McpMemoryService_Tests_Resilience_ResilienceTests_Retrieve_QdrantDown_ReturnsEmptyResults_NoException_METHOD NAME="Retrieve_QdrantDown_ReturnsEmptyResults_NoException" TYPE="TEST_METHOD" IMP="IMP:WARN" />
    <tests_McpMemoryService_Tests_Resilience_ResilienceTests_Capture_QdrantDown_ReturnsFailure_NoException_METHOD NAME="Capture_QdrantDown_ReturnsFailure_NoException" TYPE="TEST_METHOD" IMP="IMP:WARN" />
    <tests_McpMemoryService_Tests_Resilience_ResilienceTests_GetStats_QdrantDown_ReturnsNegativeOne_NoException_METHOD NAME="GetStats_QdrantDown_ReturnsNegativeOne_NoException" TYPE="TEST_METHOD" IMP="IMP:WARN" />
    <tests_McpMemoryService_Tests_Resilience_ResilienceTests_Compact_QdrantDownOnBatchFetch_ReturnsSkipped_NoDelete_METHOD NAME="Compact_QdrantDownOnBatchFetch_ReturnsSkipped_NoDelete" TYPE="TEST_METHOD" IMP="IMP:WARN" />
    <tests_McpMemoryService_Tests_Resilience_ResilienceTests_Retrieve_OnnxRuntime_PolicyRethrows_ToolCatches_ReturnsEmptyResults_METHOD NAME="Retrieve_OnnxRuntime_PolicyRethrows_ToolCatches_ReturnsEmptyResults" TYPE="TEST_METHOD" IMP="IMP:FATAL" />
    <tests_McpMemoryService_Tests_Resilience_ResilienceTests_GlobalMiddleware_UnhandledException_ReturnsJsonError_METHOD NAME="GlobalMiddleware_UnhandledException_ReturnsJsonError" TYPE="TEST_METHOD" IMP="IMP:CRITICAL,IMP:FATAL" />
    <tests_McpMemoryService_Tests_Resilience_ResilienceTests_AllTools_EmitLddMarkers_METHOD NAME="AllTools_EmitLddMarkers" TYPE="TEST_METHOD" IMP="IMP:1,IMP:2,IMP:3,IMP:4" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Middleware_GlobalExceptionMiddleware_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Resilience_QdrantResiliencePolicy_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Resilience_EmbeddingResiliencePolicy_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Logging_LddMarkers_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCaptureTool_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryRetrieveTool_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryGetStatsTool_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Tools_MemoryCompactTool_cs" TYPE="EXERCISES" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Resilience_ResilienceTests_cs>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow (M10 — single `@code scope=impl:M10`)

1. **Implement `Logging/LddMarkers.cs`** (per §1 node) — `public static class LddMarkers` in `namespace McpMemoryService.Logging`. Define `public const string` fields per the M10 spec §LddMarkers contract (lines 103-148):
   - #region Capture: `CaptureEntry="[IMP:1]"`, `CaptureEmbeddingGenerated="[IMP:2]"`, `CaptureUpserted="[IMP:3]"`, `CaptureFallback="[IMP:4]"`.
   - #region Retrieve: `RetrieveEntry="[IMP:1]"`, `RetrieveEmbedding="[IMP:2]"`, `RetrieveSearchComplete="[IMP:3]"`, `RetrieveFallbackEmpty="[IMP:4]"`.
   - #region Compact: `CompactBatchFetched="[IMP:1]"`, `CompactLlmCall="[IMP:2]"`, `CompactSummaryCaptured="[IMP:3]"`, `CompactSourceDeleted="[IMP:4]"`, `CompactLlmTimeout="[IMP:5]"`, `CompactLlm5xx="[IMP:6]"`, `CompactSkipped="[IMP:7]"`.
   - #region Embedding: `EmbeddingEntry="[IMP:1]"`, `EmbeddingTokenized="[IMP:2]"`, `EmbeddingInference="[IMP:3]"`, `EmbeddingMeanPool="[IMP:4]"`, `EmbeddingL2Norm="[IMP:5]"`.
   - #region Qdrant: `QdrantCollectionReady="[IMP:1]"`, `QdrantUpserted="[IMP:2]"`, `QdrantSearched="[IMP:3]"`, `QdrantDeleted="[IMP:4]"`, `QdrantBatchFetched="[IMP:5]"`.
   - #region Critical: `UnhandledException="[IMP:CRITICAL]"`, `OnnxFatal="[IMP:FATAL]"`, `QdrantWarn="[IMP:WARN]"`.
   - MODULE_CONTRACT header per `csharp-conventions` (short — static-constants class; DOMAIN(Logging), CONCEPT(LDD 2.0 markers), TECH([IMP:N] format)). No ctors, no methods beyond the constants. No `#region` for methods — only the `#region Capture/Retrieve/Compact/Embedding/Qdrant/Critical` blocks. NOTE the marker numbers REUSE `[IMP:1]`/`[IMP:2]`/etc. across tool regions (intentional per LDD 2.0 — sequence-per-method; disambiguation in the method-name token). `QdrantWarn` is ADDED beyond the M10 spec (spec lists only `UnhandledException` + `OnnxFatal` under #region Critical) — it's needed for the `QdrantResiliencePolicy` fallback log marker; document this as an additive const in the plan's `## Notes for @code` and add a `[CHANGES]` note in the MODULE_CONTRACT.

2. **Implement `Resilience/QdrantResiliencePolicy.cs`** (per §1 node) — `public sealed class QdrantResiliencePolicy` in `namespace McpMemoryService.Resilience`. `#region Fields`: `private readonly ILogger<QdrantResiliencePolicy> _logger;`. `#region Constructors`: `public QdrantResiliencePolicy(ILogger<QdrantResiliencePolicy> logger) => _logger = logger;`. `#region Public`: the generic `ExecuteWithFallbackAsync<T>`:
   ```csharp
   public async Task<T> ExecuteWithFallbackAsync<T>(
       Func<Task<T>> action,
       T fallbackValue,
       string operationName,
       CancellationToken cancellationToken = default)
   {
       try
       {
           return await action().ConfigureAwait(false);
       }
       catch (Exception ex)
       {
           _logger.LogWarning(
               ex,
               "{Marker}[QdrantResiliencePolicy][FATAL] Qdrant operation {Op} failed — ADR-005 silent-fallback",
               LddMarkers.QdrantWarn,
               operationName);
           return fallbackValue;
       }
   }
   ```
   ADD a non-generic sibling for `Task`-returning Qdrant calls (capture/compact UpsertAsync) OR have `@code` adapt the capture/compact UpsertAsync wrap to `ExecuteWithFallbackAsync<bool>(() => { _qdrant.UpsertAsync(...); return Task.FromResult(true); }, fallbackValue: false, ...)` — the latter is cleaner (single generic method, no overload). @code picks. Use `LddMarkers.QdrantWarn` constant (not the inline `"[IMP:WARN]"`). `ConfigureAwait(false)` is conventional for library code; @code may omit if the project convention is bare `await` (matches the existing tool code which uses bare `await` — match the existing style).

3. **Implement `Resilience/EmbeddingResiliencePolicy.cs`** (per §1 node) — `public sealed class EmbeddingResiliencePolicy` in `namespace McpMemoryService.Resilience`. `#region Fields`: `private readonly ILogger<EmbeddingResiliencePolicy> _logger;`. `#region Constructors`: `public EmbeddingResiliencePolicy(ILogger<EmbeddingResiliencePolicy> logger) => _logger = logger;`. `#region Public`: `ExecuteAsync`:
   ```csharp
   public async Task<float[]> ExecuteAsync(
       Func<Task<float[]>> action,
       string operationName,
       CancellationToken cancellationToken = default)
   {
       try
       {
           return await action().ConfigureAwait(false);
       }
       catch (Exception ex)
       {
           _logger.LogCritical(
               ex,
               "{Marker}[EmbeddingResiliencePolicy][FATAL] ONNX embedding operation {Op} failed — rethrowing (ONNX runtime fatal per SPEC §7)",
               LddMarkers.OnnxFatal,
               operationName);
           throw;  // RETHROW — do NOT swallow. Tool catch(Exception) handles → DTO; bug → middleware → 503.
       }
   }
   ```
   The `catch (Exception ex) { ... throw; }` preserves the stack (use `throw;`, NOT `throw ex;`). Mirror of the M4 `OnnxEmbeddingService.EmbedAsync` catch-rethrow invariant (OnnxEmbeddingService.cs L207-215).

4. **Implement `Middleware/GlobalExceptionMiddleware.cs`** (per §1 node) — `public sealed class GlobalExceptionMiddleware` in `namespace McpMemoryService.Middleware`. Convention-based middleware (NOT `IMiddleware` — simpler, no `UseMiddleware&lt;T&gt;()` factory wiring concerns). `#region Fields`: `private readonly RequestDelegate _next; private readonly ILogger<GlobalExceptionMiddleware> _logger;`. `#region Constructors`: `public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger) { _next = next; _logger = logger; }`. `#region InvokeAsync`:
   ```csharp
   public async Task InvokeAsync(HttpContext context)
   {
       try
       {
           await _next(context).ConfigureAwait(false);
       }
       catch (Exception ex)
       {
           // Defensive guard: if the response has already started (streaming edge case), 
           // we cannot write headers — log critically + return (connection stays alive best-effort)
           if (context.Response.HasStarted)
           {
               _logger.LogCritical(
                   ex,
                   "{Marker}[GlobalExceptionMiddleware][FATAL] unhandled exception AFTER response started — cannot write error body type={Type}",
                   LddMarkers.UnhandledException,
                   ex.GetType().Name);
               return;  // NO rethrow — connection must not break
           }

           // Classify
           var (statusCode, code, marker) = ex switch
           {
               ArgumentException => (StatusCodes.Status400BadRequest, "validation_error", null),
               InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
                   => (StatusCodes.Status503ServiceUnavailable, "service_unavailable", LddMarkers.OnnxFatal),
               _ => (StatusCodes.Status500InternalServerError, "internal_error", LddMarkers.UnhandledException),
           };

           // Log
           if (statusCode == StatusCodes.Status500InternalServerError)
           {
               _logger.LogCritical(ex, "{Marker}[GlobalExceptionMiddleware][FATAL] unhandled exception type={Type} message={Message}", marker, ex.GetType().Name, ex.Message);
           }
           else
           {
               _logger.LogError(ex, "{Marker}[GlobalExceptionMiddleware][FATAL] handled exception type={Type} code={Code} statusCode={Status}", marker ?? LddMarkers.UnhandledException, ex.GetType().Name, code, statusCode);
           }

           // Write JSON error response — NO rethrow
           context.Response.StatusCode = statusCode;
           context.Response.ContentType = "application/json; charset=utf-8";
           var body = JsonSerializer.Serialize(
               new { error = new { code, message = ex.Message } },
               _errorJsonOptions);  // cached static JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
           await context.Response.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
       }
   }
   ```
   `#region Private`: `private static readonly JsonSerializerOptions _errorJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };`. The `Microsoft.ML.OnnxRuntime.OnnxRuntimeException` type is from the M4 ONNX package (already in csproj). If the pattern `InvalidOperationException or OnnxRuntimeException` does not compile (type-name resolution), use `Type.IsAssignableFrom` checks OR two sequential `catch`-style `is` checks — `if (ex is InvalidOperationException) ...; else if (ex.GetType().Name == "OnnxRuntimeException" || ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException) ...` — @code picks the cleanest. `StatusCodes` is in `Microsoft.AspNetCore.Http` (covered by `ImplicitUsings` for web projects — verify). `using System.Text.Json;` + `using Microsoft.AspNetCore.Http;` + `using Microsoft.Extensions.Logging;` + `using McpMemoryService.Logging;` + `using Microsoft.ML.OnnxRuntime;` (for `OnnxRuntimeException`).

5. **Edit `Program.cs`** (per §1 `ConfigurePipeline_M10_EDIT` + `ConfigureServices` DI):
   - **`ConfigurePipeline`** — ADD as the FIRST line (before the `// [IMP:1][ConfigurePipeline][INIT] Mapping health check endpoint` + `app.MapHealthChecks("/health")`):
     ```csharp
     // [IMP:M10][ConfigurePipeline][INIT] Global exception middleware — outermost pipeline component (catches
     //   unhandled exceptions escaping tools + MCP transport; returns JSON 400/500/503, NO rethrow — preserves
     //   MCP Streamable-HTTP connection integrity per ADR-005/SPEC §7). Registered FIRST so it wraps health + MCP endpoints.
     app.UseMiddleware<McpMemoryService.Middleware.GlobalExceptionMiddleware>();
     ```
   - **`ConfigureServices`** — ADD after the M9 tool-registration block (after L170 `services.AddTransient<McpMemoryService.Tools.MemoryCompactTool>();`) + before the M5 IQdrantService block:
     ```csharp
     // [IMP:M10][ConfigureServices][OPTION] Register resilience policies (Singleton — stateless wrappers) — ADR-005 silent-fallback parameterization
     services.AddSingleton<McpMemoryService.Resilience.QdrantResiliencePolicy>();
     services.AddSingleton<McpMemoryService.Resilience.EmbeddingResiliencePolicy>();
     ```
   - Use the FULLY-QUALIFIED type names (no `using` additions — cleanest, 0 IDE0005 risk). DO NOT touch any BUG_FIX_CONTEXT scar, the M4/M5/M6/M7/M8/M9 DI/tool lines, or the `[IMP:9]` SUCCESS marker.

6. **Edit the 4 tool files** (per §1 `Tools_M10_EDITS`) — ADDITIVE ctor params (`QdrantResiliencePolicy _qdrantPolicy`, `EmbeddingResiliencePolicy _embeddingPolicy`) + call-site wrapping; PRESERVE the existing `try{...}catch(Exception){...}` block structure. Replace inline `[IMP:N]` string literals with `LddMarkers.*` constants (mechanical find-replace per file). See §1 `Tools_M10_EDITS` annotation for the exact wrap targets per tool. CRITICAL: DO NOT wrap `DeleteAsync` (compact) in the policy — transactional ADR-003 requires the genuine throw to be reported. The tool's `catch(Exception)` block STAYS VERBATIM (it's the defense-in-depth second line; if the policy's fallback path itself has a bug, the catch handles it).

7. **Update existing tool unit tests** (M7 `MemoryCaptureToolTests` + `MemoryGetStatsToolTests`, M8 `MemoryRetrieveToolTests`, M9 `MemoryCompactToolTests`) — ADD the 2 new policy ctor args: `new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance)` + `new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance)` to each tool's ctor invocation. The existing assertions (Success==false / Count==-1 / empty Results / Status==error / DeleteAsync Times.Never) STAY UNCHANGED — the fallback semantics are IDENTICAL (the policy returns the same fallback values the tool's own catch was producing). This is an ADDITIVE test-edit (additional ctor args); the test count stays ~M7 (10..11) + M8 (~10..12) + M9 (~10..12) = ~30..35 existing tool tests preserved green.

8. **Write `tests/McpMemoryService.Tests/Resilience/ResilienceTests.cs`** (per §1 resilience-tests node) — ~7 unit tests, UNCATEGORISED. For the middleware test: `DefaultHttpContext` + canned `RequestDelegate _next = ctx => throw new InvalidOperationException("probe")` + `NullLogger<GlobalExceptionMiddleware>.Instance` + `await middleware.InvokeAsync(ctx)` + assert `ctx.Response.StatusCode` + read `ctx.Response.Body` (a `MemoryStream` — `DefaultHttpContext` defaults `Response.Body` to `MemoryStream`) → `JsonDocument.Parse`. For policy tests: REAL policy instances (no mock) + Moq `Mock<IQdrantService>`/`Mock<IEmbeddingService>` ThrowsAsync. For the `AllTools_EmitLddMarkers` test: a minimal `TestLogger<T> : ILogger<T>` capturing formatted log messages into `List<string>` (10-line stub) OR `Microsoft.Extensions.Logging.Testing.NullLogger` does NOT capture — use XUnit's `ITestOutputHelper`-backed logger OR a custom capturer. @code picks the cleanest capturing pattern on net10.0.

9. **Build + test gate before return:**
   - `dotnet build McpMemoryService.sln` → **0 Warning(s), 0 Error(s)**. AC-1.
   - `dotnet test --filter "Category!=Integration"` → unit gate: M3 (16) + M5 mapping (5) + M6 (6) + M7 (10..11 + SnakeCaseTransportTests) + M8 (~10..12) + M9 (~10..12) + **M10 (~7 resilience)** = cumulative green, `Failed: 0`. AC-2. The existing tool tests (updated with the 2 new policy ctor args) MUST stay green — if any break, the fallback semantics changed (regression — investigate before the `@code` return).
   - Do NOT run `dotnet test` (full) — M10 has NO integration tests (middleware tested via `DefaultHttpContext`; policies via Moq mocks; no Docker/ONNX/llama.cpp). @qa runs the full gate separately.

---

## 3. Acceptance Criteria (M10)

> Verbatim from `milestones/M10-error-handling-resilience-logging.md` lines 262-272, labelled for mechanical `@qa` checking. Reinterpreted AC-7 (ONNX-runtime-middleware) per the architect's reconciliation in Notes #1.

- [ ] **AC-1:** `dotnet build McpMemoryService.sln` — OK (0W 0E).
- [ ] **AC-2:** `dotnet test` — all resilience tests PASS (no real Qdrant/ONNX/Docker required — `DefaultHttpContext` + Moq mocks).
- [ ] **AC-3:** `GlobalExceptionMiddleware` catches unhandled exceptions, returns JSON 500 (no MCP connection break) — verified by `GlobalMiddleware_UnhandledException_ReturnsJsonError` + variant sub-tests (400/500/503).
- [ ] **AC-4:** `QdrantResiliencePolicy` applied to all Qdrant calls in the 4 tools (capture/compact UpsertAsync, retrieve SearchAsync, get_stats CountAsync, compact GetBatchForCompactAsync) — verified by source inspection (`grep "ExecuteWithFallbackAsync" src/McpMemoryService/Tools/*.cs`) + the policy-fallback tests.
- [ ] **AC-5:** Qdrant-down: retrieve → empty results; capture → `success=false`; get_stats → `count=-1`; compact batch-fetch → `status=skipped,reason=insufficient_data` (reinterpreted from spec's "status=error" — see Notes #4 for the ADR-004 alignment). Verified by the 4 policy-fallback tests.
- [ ] **AC-6:** ONNX runtime failure → middleware catches → 503 (no connection break) — PROVEN via the `GlobalMiddleware_UnhandledException_ReturnsJsonError` probe (InvalidOperationException → 503). The tool-level ONNX-path returns a silent DTO (ADR-005 tool-level) — verified by `Retrieve_OnnxRuntime_PolicyRethrows_ToolCatches_ReturnsEmptyResults`. See Notes #1 for the reconciliation.
- [ ] **AC-7:** ONNX startup failure → app exits with Exit 1 + clear log — ALREADY implemented in M4 (`OnnxEmbeddingService.ctor` throws → `QdrantCollectionInitializer`-style host-fail). M10 does NOT touch this; verify by source (`grep "Exit 1\|throw.*OnnxModelOptions\|LogCritical.*FATAL" src/McpMemoryService/Services/OnnxEmbeddingService.cs`).
- [ ] **AC-8:** LDD markers (`[IMP:N]`) used consistently across all tools/services — no INLINE marker string literals remain; all reference `LddMarkers.*` constants. Verified by `grep "\[IMP:" src/McpMemoryService/Tools/*.cs src/McpMemoryService/Services/*.cs src/McpMemoryService/Middleware/*.cs src/McpMemoryService/Resilience/*.cs` returning only `LddMarkers.*` interpolation results (NOT bare string literals) — @code does the find-replace COMPLETELY. (Exception: the `LddMarkers.cs` constants FILE itself contains the bare string literals — that's the single source of truth.)
- [ ] **AC-9:** `LddMarkers` constants defined and used — no inline marker strings in non-`LddMarkers` files.
- [ ] **AC-10:** Logging levels: Information (tool calls), Warning (Qdrant timeouts/down — policy fallback `LogWarning`), Error/Critical (Qdrant failures in tool catches already `LogError`; middleware `LogCritical` for 500, `LogError` for 400/503) per SPEC §7. Verified by source inspection.
- [ ] **AC-11:** No MCP connection breaks on any downstream failure (Qdrant/ONNX/LLM) — the middleware DOES NOT rethrow; the tools DO NOT rethrow from their catch handlers; the policies either return fallback (Qdrant) or rethrow INTO the tool catch (Embedding). Verified by the ~7 resilience tests (no `await` throws in the fallback paths).
- [ ] **AC-12:** Program.cs `app.UseMiddleware<GlobalExceptionMiddleware>()` is the FIRST line of `ConfigurePipeline` (wraps health + MCP endpoints); the 2 policy DI registrations are present in `ConfigureServices`; the M2..M9 scars + the `[IMP:9]` SUCCESS marker are preserved. Verified by source.

---

## Notes for @code (M10)

1. **Architect's reconciliation: the anti-pattern vs SPEC §7 "ONNX runtime → middleware" doctrine.** SPEC §7 row "ONNX (runtime)" says "Let propagate → GlobalExceptionMiddleware catches → MCP error response (not connection break)". The strict reading implies ONNX runtime failures INSIDE a tool should ESCAPE the tool catch and reach the middleware → 503. HOWEVER:
   - (a) The dispatch instruction explicitly warns: "ANTI-PATTERN: @architect SHOULD NOT rewrite already-working tool try/catch logic." The M7/M8/M9 tools use `catch (Exception ex) { return failureDto; }` which catches ALL exceptions (including ONNX runtime) → silent DTO. Narrowing these catches to exclude ONNX would REWRITE working try/catch logic + BREAK the existing M7/M8/M9 tool-unit-tests (which use `ThrowsAsync(new InvalidOperationException(...))` as a Qdrant-down stand-in — narrowing the catch to exclude `InvalidOperationException` would make those tests' `Success==false`/`Count==-1`/empty Results assertions FAIL).
   - (b) The strict SPEC §7 reading would produce a 503 (HTTP error) for ONNX runtime failure — but the ADR-005 tool-level silent-fallback (already implemented M7/M8/M9) produces a DTO (HTTP 200 with failure shape). The DTO path is BETTER for the MCP consumer (structured failure shape the agent can read; HTTP-200 keeps the connection idempotent) and ALIGNED with the established `@memory` MCP-tool contract. The 503 path is a DEGRADATION (less informative).
   - **Architect's DECISION (documented for @orchestrator + @qa approval):** preserve the tool catch(Exception) silent-fallback (DTO) for ONNX runtime failures INSIDE tools. `EmbeddingResiliencePolicy` rethrows → the tool catch handles → DTO. The middleware catches ONNX runtime failures that ESCAPE the tool catch (bugs in the catch handler, ONNX failing BEFORE the tool's try — e.g., `InputValidator.ValidateCapture` throws ArgumentException which is NOT caught by the tool — but that's a validation error → 400, not ONNX). The middleware's 503-on-`InvalidOperationException`/`OnnxRuntimeException` is exercisable via the probe test (`GlobalMiddleware_UnhandledException_ReturnsJsonError`).
   - **NET EFFECT:** SPEC §7 "ONNX runtime → middleware catches" is HONOURED as the OUTER fallback (middleware catches ALL escapes incl. ONNX-runtime-shaped exceptions) — but the PRIMARY path for ONNX runtime failure inside a tool is the ADR-005 silent DTO (which is the stronger, more-agent-friendly behavior). The M10 spec test `Retrieve_OnnxRuntimeFail_MiddlewareCatches_Returns500` is REINTERPRETED as `Retrieve_OnnxRuntime_PolicyRethrows_ToolCatches_ReturnsEmptyResults` — @qa is alerted to this reinterpretation in `tests/test_guide.md`.

2. **Compact batch-fetch Qdrant-down → `skipped` (NOT `error`).** The M10 spec test `Compact_QdrantDownOnBatchFetch_ReturnsError_NoDelete` expects `status=error`. With the `QdrantResiliencePolicy` wrapping `GetBatchForCompactAsync` with `fallbackValue: Array.Empty<MemoryEntry>()`, the fallback is an EMPTY list → `entries.Count (0) < input.BatchSize` → triggers the ADR-004 `status=skipped,reason=insufficient_data` early-return path. This is the NON-BLOCKING, ADR-004-aligned behavior (Qdrant-down on batch-fetch gracefully degrades to "not enough data to compact" — which is TRUE — rather than surfacing an error to the orchestrator that might break a compact-cycle loop). **Architect's DECISION (documented for @qa):** reinterpret the test as `Compact_QdrantDownOnBatchFetch_ReturnsSkipped_NoDelete` asserting `Status==skipped, Reason==insufficient_data, DeleteAsync Times.Never, SummarizeAsync Times.Never, EmbedAsync Times.Never`. This is CONSISTENT with ADR-004. If the operator prefers the literal spec's `error` behavior, the alternative is `fallbackValue: null` (not `Array.Empty`) → the compact tool would need a null-guard + `return error` — but that complicates the `IReadOnlyList<MemoryEntry>` contract. The architect recommends the `skipped` reinterpretation (cleaner, ADR-004-aligned). FLAG to @orchestrator.

3. **Policies: DI-injected sealed classes (chosen) vs stateless static helpers (rejected for testability).** The M10 spec §Algorithm/Logic shows the policies as INSTANCE classes (ctor DI of an ILogger). This is the chosen path because (a) it matches the M7 `csharp-conventions` #region structuring for services; (b) it permits @qa to inject a capturing logger into the policies for the `AllTools_EmitLddMarkers` test + permits `Mock<QdrantResiliencePolicy>` if testability demands it (not needed — the REAL policy + mocked IQdrantService is the cleanest pattern). The alternative — static helpers `QdrantResiliencePolicy.ExecuteWithFallbackAsync<T>(ILogger, ...)` — would AVOID the DI registration (simpler `Program.cs`) but diverges from the M10 spec's class-based contract. Use the INSTANCE approach (per spec); register as Singleton in `ConfigureServices`. The 4 tool unit tests are UPDATED to pass real policy instances (with `NullLogger<T>.Instance`) — additive ctor args, assertions unchanged.

4. **`DeleteAsync` (compact) is NOT wrapped in `QdrantResiliencePolicy`.** The transactional ADR-003 invariant requires the `DeleteAsync` failure to be reported as `reason="post_llm_failure"` (sources not deleted — but the summary was upserted; an idempotent state for next-compact). If the `QdrantResiliencePolicy` wrapped `DeleteAsync` with a fallback (e.g., `fallbackValue: false`), the tool's post-LLM catch would NOT fire (no throw) → the tool would `return completed` with sources still present — a LIE to the orchestrator. Therefore `DeleteAsync` is called DIRECTLY (`await _qdrantService.DeleteAsync(sourceIds, ct)` — UNCHANGED from M9), and any throw propagates to the post-LLM `catch(Exception)` → `status="error",reason="post_llm_failure"`. This is the M9 transactional design, PRESERVED by M10. SAME for `UpsertAsync` of the summary entry — if the policy wrapped it with `fallback: false`, a `!upserted` check early-returns `error` (cleaner than relying on the catch — but EITHER is acceptable; @code picks: (a) wrap with `fallback:false` + explicit `if (!upserted) return error`, OR (b) leave unwrapped + rely on the post-LLM `catch(Exception)`). The architect RECOMMENDS (b) — keep UpsertAsync unwrapped + rely on the post-LLM catch (minimal edit to the M9 tool — only the `GetBatchForCompactAsync` + `EmbedAsync` calls are wrapped). This keeps the M9 transactional structure INTACT + reduces the M10 tool-edit surface.

5. **`LddMarkers` constants reuse `[IMP:1]` across regions — intentional (LDD 2.0).** The M10 spec lines 103-148 define `CaptureEntry="[IMP:1]"` AND `RetrieveEntry="[IMP:1]"` AND `CompactBatchFetched="[IMP:1]"`. This is the LDD 2.0 convention: `[IMP:N]` is a SEQUENCE-NUMBER PER METHOD, not a globally-unique ID. The disambiguation is the method-name token (`[IMP:1][CaptureAsync]...` vs `[IMP:1][RetrieveAsync]...`). The find-replace from inline `"[IMP:1]"` literals to `LddMarkers.*` MUST preserve the original IMP-number assignment per file/method — do NOT renumber. `@code` reads each tool file, identifies which `[IMP:N]` belongs to which region (by file name: `MemoryCaptureTool.cs` → Capture region; `MemoryRetrieveTool.cs` → Retrieve region; etc.), and replaces the inline literal with the corresponding `LddMarkers.<Region-specific-name>` const. For the `OnnxEmbeddingService.cs` + `QdrantService.cs` + `LlmSummarizerService.cs` service files: use the Embedding/Qdrant region constants — `grep "[IMP:"` per service file to map. For services: `OnnxEmbeddingService` → `EmbeddingEntry/Tokenized/Inference/MeanPool/L2Norm`; `QdrantService` → `QdrantCollectionReady/Upserted/Searched/Deleted/BatchFetched`; `LlmSummarizerService` → the M6-spec `[IMP:1]`/`[IMP:2]` markers (no dedicated region in LddMarkers — REUSE `EmbeddingEmbedding*` is WRONG — @code ADDS two constants to `LddMarkers` if the M6 service uses markers: `LlmSummarizeRequestSent = "[IMP:1]"`, `LlmSummarizeResponseReceived = "[IMP:2]"` in a new `#region Llm` — OR leaves the M6 service's inline markers UNCHANGED since M6 is DONE + tested; the plan's find-replace is FOCUSED on the 4 tools + middleware + policies per the M10 spec §Step 4 "Update all tools and services" — @code's judgement: replace in the 4 tools + middleware + policies (the M10-new code) + OPTINOALLY in the 3 services; if the 3 services are DONE + green + untouched-by-M10-logic, leaving their inline `[IMP:N]` literals is ACCEPTABLE — document the scope in the MODULE_CONTRACT `[CHANGES]` of `LddMarkers.cs`). The AC-8 grep check applies to the 4 TOOLS + middleware + policies (the M10-touched files) — the 3 services may keep inline literals without failing AC-8 if `@code` opts for minimal-scope find-replace. `@qa` is alerted to the scope decision in `tests/test_guide.md`.

6. **`GlobalExceptionMiddleware` classification detail.** `ArgumentException` is the base for `ArgumentNullException` + `ArgumentException` — both subclass `System.ArgumentException`. The `is ArgumentException` pattern catches all three (validation errors)。`InvalidOperationException` is thrown by `OnnxEmbeddingService` (model-not-loaded) + rethrown as `OnnxRuntimeException` from `_session.Run`. `Microsoft.ML.OnnxRuntime.OnnxRuntimeException` is in the ONNX package — the `or` pattern in a switch expression may not compile if the type is not in the using scope — add `using Microsoft.ML.OnnxRuntime;` to `GlobalExceptionMiddleware.cs` OR use a runtime `IsAssignableFrom` check. `@code` verifies at compile.

7. **No `#pragma warning disable`.** Build warnings = AC-1 failure. Watch for: CA1031 (middleware catches a broad Exception — this is the POINT of global-error-handling; suppress with a `[SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification="Global error handler per ADR-005")]` if CA-analyzers are enabled — or configure the analyzer as مشروع-already-disabled; verify the build is 0W with the existing csproj analyzer settings), IDE0005 unused usings (only add usings actually used — `Microsoft.ML.OnnxRuntime` only if `OnnxRuntimeException` is referenced), CS0168 (unused `ex` in the catch — `@code` uses the `ex` in the log call, so no warning). The `throw;` (bare rethrow) preserves the stack — do NOT use `throw ex;` (would reset the stack + emit a CA2200 warning).

8. **Middleware `HasStarted` guard.** `context.Response.HasStarted` is true once the first byte is written (headers flush). In the MCP Streamable-HTTP transport (SSE-style streaming), the response may have started before a late exception. The guard logs critically + returns (cannot write a 500 body over an in-flight SSE stream — would corrupt the protocol). The MCP client receives an unterminated SSE stream (the MCP transport handles this as a connection-level error — but the host process does NOT crash and the next request works). This is the best-effort safety net. The probe tests use `DefaultHttpContext` which does NOT start the response before the throw → the guard is not exercised in the unit tests (the 200-path is the common path; the `HasStarted` guard is for defensive completeness — @qa does NOT need to test it explicitly).

9. **Decomposition decision — SINGLE `@code scope=impl:M10` dispatch.** 1 middleware class (1 async method) + 2 resilience-policy classes (1 generic method each) + 1 static-constants class + 1 Program.cs edit (1 `UseMiddleware` line + 2 DI lines) + 4 mechanical tool edits (ctor params + call-site wrapping + `LddMarkers.*` find-replace — the try/catch block structure is NOT touched) + 1 resilience test file (~7 tests). All deliverables are cohesive (one resilience + middleware tier + its wiring + its tests). Below the >5-new-methods threshold if tests are counted separately. **NO `## Decomposition` section** is appended.

10. **Do NOT run `dotnet test` (full) during the `@code` dispatch.** M10 has NO integration tests (middleware via `DefaultHttpContext`; policies via Moq). The unit gate (`dotnet test --filter "Category!=Integration"`) is the `@code` return gate. @qa runs the full gate (with Docker Qdrant + ONNX + llama.cpp) separately — the middleware/policy wiring is exercised E2E in M11 (docker) + M12 (e2e).

11. **AGENTS.md build commands.** `dotnet build McpMemoryService.sln` + `dotnet test --filter "Category!=Integration"`. `.slnx` is the .NET 10 default; the legacy `.sln` alias auto-discovers it.

12. **profile.md consistency.** Plan prose is technical English (matches the M5..M9 plan style). No Russian summary header required.

13. **Web search record (for @qa audit).**
    - `[SOURCE: web_search attempt, query="ASP.NET Core middleware exception handler UseMiddleware GlobalExceptionMiddleware JSON error response .NET 10", ts=2026-07-02T<ISO8601>]` → **[WEB_SEARCH_UNAVAILABLE] SearXNG service unavailable — search returned error ("Сервис поиска временно недоступен").** Per WEB_SEARCH_PROTOCOL scenario S3, proceeded without search results using local knowledge of the ASP.NET Core middleware convention-based `InvokeAsync(HttpContext)` pattern + the `RequestDelegate` + `ILogger<>` ctor injection via `UseMiddleware<T>()`. The `DefaultHttpContext` test pattern + the `JsonSerializer` snake-case body via `JsonNamingPolicy.SnakeCaseLower` are .NET 8+ stable APIs (net10.0-confirmed — no `[UNVERIFIED_VERSION]` tags needed).
    - No `fetch_and_extract` call was made (no web_search URLs returned to fetch).
    - Architectural decisions (middleware OUTERMOST-first ordering per ASP.NET Core convention; convention-based NOT IMiddleware; classify ArgumentException→400 / InvalidOperationException+OnnxRuntimeException→503 / else→500; `HasStarted` guard for SSE streaming; policies DI-Singleton per M10 spec; `QdrantResiliencePolicy` broad catch with fallback per tool-level M7/M8/M9 precedent; `EmbeddingResiliencePolicy` rethrow mirroring M4 OnnxEmbeddingService do-not-swallow; `LddMarkers` constants reuse per LDD 2.0 sequence-per-method; `DeleteAsync` NOT wrapped per ADR-003 transactional invariant; compact batch-fetch Qdrant-down → `skipped` per ADR-004) are derived from the M10 spec + SPEC §7 + AGENTS.md ADR-003/004/005/006/010 + the realized M4..M9 source (QdrantService.cs catch patterns + OnnxEmbeddingService.cs L207-215 rethrow invariant + the 4 tool files' catch(Exception) structure — all VERIFIED by read before drafting this plan).

---

## M11 — Dockerfile + docker-compose + .dockerignore

| Field | Value |
|---|---|
| Current Milestone | **M11 — Docker artifacts: `Dockerfile` (multi-stage build → aspnet:10.0 runtime, non-root `appuser`, EXPOSE 5000, Models/ COPY), `docker-compose.yml` (strict 1 CPU / 512 MB limits per SPEC §6.2, healthcheck, restart unless-stopped), `.dockerignore` (exclude build/IDE/tests/docs, KEEP Models/ in context), optional `docs/docker-deploy.md`** |
| Status | PLAN_READY (awaiting `@code scope=impl:M11`) |
| Milestone Deps | **M4 (DONE — `Models/model.onnx` + `Models/tokenizer.json` gitignored; `Scripts/download-model.sh` verified Docker-friendly — bash/curl/`set -euo pipefail`), M10 (DONE — `/health` endpoint + `[IMP:N]` LDD markers + `GlobalExceptionMiddleware` no-rethrow).** |
| Dispatch Recommendation | **Single `@code scope=impl:M11` — NO decomposition.** Infrastructure-only: 3 cohesive Docker files (Dockerfile + docker-compose.yml + .dockerignore) + 1 optional doc. No `.cs`, no unit tests; runtime smoke verification is @qa's Docker gate. Below decomposition threshold. **NO `## Decomposition` section** is appended. |
| Etap | Etap 1 (implement M1..M12). Etap 2 future. |

## ADRs Touched by M11

| ADR | Decision | M11 Action |
|---|---|---|
| **ADR-006** | .NET 10 target | `Dockerfile` Stage 2 base image = `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime-only, no SDK — SPEC §6.1). Stage 1 uses `mcr.microsoft.com/dotnet/sdk:10.0` for `dotnet publish -c Release -o /app/publish /p:UseAppHost=false` (framework-dependent deployment). |
| **ADR-011** | ONNX 384-dim, model = paraphrase-multilingual-MiniLM-L12-v2 | `Dockerfile` Stage 2 `COPY src/McpMemoryService/Models/ ./Models/` — the gitignored `model.onnx` + `tokenizer.json` (~90-120 MB) are baked into the image so the `OnnxEmbeddingService` ctor resolve `Models/model.onnx` relative to `AppContext.BaseDirectory` (= `/app`, since `WORKDIR /app`). The model does NOT enter git; the host operator runs `Scripts/download-model.sh` BEFORE `docker build` so `Models/` exists in the build context. `.dockerignore` MUST keep `src/McpMemoryService/Models/` in context (explicit comment). |
| **ADR-005** | Silent fallback / no MCP connection breaks | `docker-compose.yml` `restart: unless-stopped` ensures the container auto-recovers from crashes. The M10 healthcheck-driven restart (per SPEC §6.2) uses `start_period: 60s` because the ONNX model + `QdrantCollectionInitializer` IHostedService make startup slow. The container reports `unhealthy` → Docker restarts it (ADR-005 resilience at the orchestration level). |

> **SPEC §6 reconciliation note (CRITICAL for @code):** SPEC §6.2 lists the environment variables as `QDRANT_URL`, `LLM_CPP_URL`, `MODEL_DIR` (single-underscore names). The realized service binds hierarchical config via `services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"))` + `LlmSummarizerOptions` ⟶ `LlmSummarizer:BaseUrl`. .NET's environment-variable configuration provider treats `__` (DOUBLE underscore) as the hierarchy separator — a single `_` does NOT split. Therefore `QDRANT_URL` would bind to a flat key `QDRANT_URL`, NOT to `Qdrant:Url`. To honor BOTH the SPEC §6.2 operator-facing names AND the actual .NET binding mechanism WITHOUT any `.cs` change (M11 is infrastructure-only), `docker-compose.yml` sets BOTH:
> - `QDRANT_URL=...` + `LLM_CPP_URL=...` + `MODEL_DIR=/app/Models` + `ASPNETCORE_URLS=http://+:5000` + `Logging__LogLevel__Default=Information` — operator-facing aliases (SPEC §6.2 compliance, documented in comments).
> - `Qdrant__Url=${QDRANT_URL}` + `LlmSummarizer__BaseUrl=${LLM_CPP_URL}` — the .NET-binding env vars that ACTUALLY configure `QdrantOptions.Url` + `LlmSummarizerOptions.BaseUrl` (the `__` collapses to `:` in the .NET config layer). The `${QDRANT_URL}` reference reuses the operator-facing value (single source of truth — change the SPEC-named var, the binding var follows).
>
> `MODEL_DIR` is informational only — the app uses the RELATIVE `OnnxModel:ModelPath="Models/model.onnx"` (resolved relative to `/app` via `AppContext.BaseDirectory`), and the Dockerfile `COPY .../Models/ ./Models/` lands the files at `/app/Models/`, so the relative path already resolves correctly. No `.cs` binding is needed for MODEL_DIR (it is an operator reference; the AC checks it is set/configurable, which it is). `ASPNETCORE_URLS` is a native ASP.NET Core env var (understood natively, no Options class). `Logging__LogLevel__Default` uses the `__` convention correctly already.

---

## PURPOSE (M11)

Produce the production Docker artifacts for McpMemoryService: a multi-stage `Dockerfile` (SDK stage `dotnet publish -c Release -o /app/publish /p:UseAppHost=false` → runtime stage on `mcr.microsoft.com/dotnet/aspnet:10.0`, COPY published output + gitignored `Models/` directory, create a non-root `appuser`, EXPOSE 5000, set `ASPNETCORE_URLS=http://+:5000` + `MODEL_DIR=/app/Models` env defaults, ENTRYPOINT `dotnet McpMemoryService.dll`); a `docker-compose.yml` with strict resource limits (`cpus: '1.0'`, `memory: 512M`, `reservations.memory: 256M` per SPEC §6.2 — NOT to be relaxed), port mapping `5000:5000`, both SPEC §6.2 operator-facing env vars AND the .NET-binding `__` env vars (see ADR reconciliation note above), `restart: unless-stopped`, and a healthcheck hitting `/health` with `start_period: 60s` (ONNX load is slow); a `.dockerignore` excluding build artifacts (`bin/`, `obj/`), IDE files (`.vs/`, `.vscode/`, `.idea/`), `tests/`, `milestones/`, `*.md`, git, and the Docker files themselves — while KEEPING `src/McpMemoryService/Models/` in the build context (needed by the Dockerfile COPY). The `Scripts/download-model.sh` (M4) is verified Docker-friendly (already uses portable bash + curl) — it is NOT executed inside Docker; the host operator runs it before `docker build`. An optional `docs/docker-deploy.md` provides brief deployment notes (prerequisites, build/run commands, log inspection, model update). This milestone produces NO `.cs` files and NO unit tests — acceptance is a Docker smoke gate exercised by @qa (`docker build` → `docker-compose up -d` → healthcheck passes → `curl /health` 200 → MCP `tools/list` returns 4 tools → resource limits verified via `docker stats` → logs contain `[IMP:N]` markers).

---

## 1. Draft Code Graph (M11)

> M11 is an infrastructure milestone — NO `.cs` nodes. The graph nodes are Docker/YAML/text artifacts. The `Dockerfile` is the build/packaging contract; `docker-compose.yml` is the orchestration contract; `.dockerignore` is the context-hygiene contract. Each references existing M2..M10 artifacts (appsettings, Program.cs entrypoint, Models/, /health endpoint, LddMarkers logging).

```xml
<DraftCodeGraph>
  <!-- ========== DOCKERFILE ========== -->
  <Dockerfile_FILE FILE="Dockerfile" TYPE="DOCKERFILE">
    <keywords>multi-stage build, sdk:10.0, aspnet:10.0, runtime-only, no CUDA, dotnet publish, UseAppHost=false, non-root appuser, EXPOSE 5000, ASPNETCORE_URLS, MODEL_DIR, Models COPY</keywords>
    <annotation>Multi-stage Dockerfile per SPEC §6.1 + M11 spec §Contracts. Stage 1 (build): FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build; WORKDIR /src; COPY src/McpMemoryService/McpMemoryService.csproj ./McpMemoryService/ (layer-cache for restore); RUN dotnet restore ./McpMemoryService/McpMemoryService.csproj; COPY src/McpMemoryService/ ./McpMemoryService/; RUN dotnet publish ./McpMemoryService/McpMemoryService.csproj -c Release -o /app/publish /p:UseAppHost=false (framework-dependent — runtime image supplies the shared runtime). Stage 2 (runtime): FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime; WORKDIR /app; COPY --from=build /app/publish ./; COPY src/McpMemoryService/Models/ ./Models/ (the gitignored ONNX model + tokenizer — host operator must run Scripts/download-model.sh BEFORE docker build); EXPOSE 5000; ENV ASPNETCORE_URLS=http://+:5000; ENV MODEL_DIR=/app/Models; RUN adduser --disabled-password --gecos "" appuser (Debian adduser available on the non-chiseled aspnet:10.0 base; @code VERIFIES curl availability for the compose healthcheck — if curl absent, add apt-get install step BEFORE the USER line); USER appuser; ENTRYPOINT ["dotnet", "McpMemoryService.dll"]. Comment header noting SPEC §6.1 (no CUDA/libcuda deps — the aspnet:10.0 base has none).</annotation>
    <Dockerfile_Stage1_build_STAGE NAME="build" TYPE="DOCKER_BUILD_STAGE" BASE="mcr.microsoft.com/dotnet/sdk:10.0" />
    <Dockerfile_Stage2_runtime_STAGE NAME="runtime" TYPE="DOCKER_RUNTIME_STAGE" BASE="mcr.microsoft.com/dotnet/aspnet:10.0" />
    <Dockerfile_appuser_USER NAME="appuser" TYPE="NON_ROOT_USER" />
    <Dockerfile_ENTRYPOINT_CMD NAME="ENTRYPOINT" TYPE="DOTNET_ENTRYPOINT" VALUE="dotnet McpMemoryService.dll" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_csproj" TYPE="BUILDS_PROJECT" />
      <Link TARGET="src_McpMemoryService_Program_cs" TYPE="ENTRY_POINT_OF" />
      <Link TARGET="src_McpMemoryService_Models_model_onnx_FILE" TYPE="COPIES_ARTIFACT" />
      <Link TARGET="src_McpMemoryService_Models_tokenizer_json_FILE" TYPE="COPIES_ARTIFACT" />
      <Link TARGET="docker_compose_yml_FILE" TYPE="IMAGED_BY" />
      <Link TARGET="dockerignore_FILE" TYPE="FILTERS_CONTEXT" />
    </CrossLinks>
  </Dockerfile_FILE>

  <!-- ========== DOCKER-COMPOSE ========== -->
  <docker_compose_yml_FILE FILE="docker-compose.yml" TYPE="DOCKER_COMPOSE">
    <keywords>compose, resource limits, 1.0 CPU, 512M memory, 256M reservation, restart unless-stopped, healthcheck, start_period 60s, env vars SPEC §6.2 + .NET "__" binding</keywords>
    <annotation>docker-compose.yml per SPEC §6.2 + M11 spec §Contracts. Single service `mcp-memory` (image mcp-memory:latest, container_name mcp-memory, build context=. dockerfile=Dockerfile, ports 5000:5000). Environment: SPEC §6.2 operator-facing vars (QDRANT_URL=<url>, LLM_CPP_URL=<url>, MODEL_DIR=/app/Models, ASPNETCORE_URLS=http://+:5000, Logging__LogLevel__Default=Information) PLUS the .NET-binding bridge vars (Qdrant__Url=${QDRANT_URL}, LlmSummarizer__BaseUrl=${LLM_CPP_URL}) — see ADR reconciliation note for the single-vs-double-underscore rationale. deploy.resources.limits: cpus '1.0' + memory 512M (STRICT per SPEC §6.2 — do NOT relax); reservations.memory 256M. restart: unless-stopped. healthcheck: test ["CMD","curl","-f","http://localhost:5000/health"], interval 30s, timeout 10s, retries 3, start_period 60s (ONNX + QdrantCollectionInitializer IHostedService startup is slow). The healthcheck requires `curl` in the runtime image — @code VERIFIES the aspnet:10.0 base bundles curl; if not, the Dockerfile adds an apt-get install step (before USER appuser). The QDRANT_URL/LLM_CPP_URL IPs are operator-configured (the SPEC §6.2 example uses <ip> placeholders; @code may parameterize via .env or keep the example IPs with a comment to override).</annotation>
    <docker_compose_mcp_memory_SERVICE NAME="mcp-memory" TYPE="COMPOSE_SERVICE" />
    <docker_compose_limits_RESOURCE NAME="resource-limits" TYPE="DEPLOY_LIMITS" CPUS="1.0" MEMORY="512M" />
    <docker_compose_healthcheck_RESOURCE NAME="healthcheck" TYPE="HEALTHCHECK" START_PERIOD="60s" />
    <CrossLinks>
      <Link TARGET="Dockerfile_FILE" TYPE="BUILDS_FROM" />
      <Link TARGET="src_McpMemoryService_Program_cs_ConfigurePipeline_METHOD" TYPE="HEALTHCHECK_HITS" />
      <Link TARGET="src_McpMemoryService_Configuration_QdrantOptions_cs" TYPE="CONFIGURES_VIA_ENV" />
      <Link TARGET="src_McpMemoryService_Configuration_LlmSummarizerOptions_cs" TYPE="CONFIGURES_VIA_ENV" />
    </CrossLinks>
  </docker_compose_yml_FILE>

  <!-- ========== .DOCKERIGNORE ========== -->
  <dockerignore_FILE FILE=".dockerignore" TYPE="DOCKER_CONTEXT_FILTER">
    <keywords>dockerignore, context hygiene, exclude bin/obj, exclude IDE, exclude tests, exclude *.md, KEEP Models/</keywords>
    <annotation>.dockerignore per M11 spec §Contracts. Excludes: .git, .gitignore, **/bin/, **/obj/, artifacts/, .vs/, .vscode/, .idea/, tests/, milestones/, *.md (with negation exceptions !src/McpMemoryService/Scripts/*.sh + *.ps1 — harmless no-ops since the *.md rule does not match .sh/.ps1, kept for spec-faithfulness), Dockerfile, docker-compose.yml, .dockerignore, Thumbs.db, .DS_Store. CRITICAL INVARIANT: `src/McpMemoryService/Models/` is NOT excluded — the Dockerfile Stage-2 `COPY src/McpMemoryService/Models/ ./Models/` requires the model + tokenizer to be present in the build context (the host operator runs Scripts/download-model.sh first). A prominent comment in the .dockerignore documents this (matching the spec's NOTE). Models/*.onnx + tokenizer.json are gitignored at the VCS layer (M1 root .gitignore + M4 Models/.gitignore) but MUST be in the Docker context — the two ignore systems are independent.</annotation>
    <CrossLinks>
      <Link TARGET="Dockerfile_FILE" TYPE="SCOPES_BUILD_CONTEXT" />
      <Link TARGET="src_McpMemoryService_Models_model_onnx_FILE" TYPE="KEEPS_IN_CONTEXT" />
    </CrossLinks>
  </dockerignore_FILE>

  <!-- ========== OPTIONAL DOC ========== -->
  <docs_docker_deploy_md_FILE FILE="docs/docker-deploy.md" TYPE="DEPLOY_DOC">
    <keywords>deployment notes, prerequisites, build, run, logs, model update</keywords>
    <annotation>Optional brief deployment notes per M11 spec §Algorithm step 5. Contents: (1) Prerequisites — Qdrant running at QDRANT_URL, llama.cpp at LLM_CPP_URL, ONNX model downloaded via Scripts/download-model.sh (place model.onnx + tokenizer.json in src/McpMemoryService/Models/). (2) Build + run — `docker build -t mcp-memory:latest .` then `docker-compose up -d`. (3) Health verification — `curl http://localhost:5000/health` → 200; wait up to 60s (start_period) for ONNX load. (4) Logs — `docker logs mcp-memory` (look for [IMP:N] LDD markers, M10 LddMarkers). (5) Resource overview — CPU ≤ 1.0, MEM ≤ 512M (verify via `docker stats mcp-memory`). (6) Model update — re-run download-model.sh (or Download-Model.ps1 on Windows), then `docker build --no-cache` to bake the new model into the image. (7) Env var override — edit docker-compose.yml environment block (QDRANT_URL / LLM_CPP_URL + the __ bridge vars). Single file, no code.</annotation>
    <CrossLinks>
      <Link TARGET="Dockerfile_FILE" TYPE="DOCUMENTS" />
      <Link TARGET="docker_compose_yml_FILE" TYPE="DOCUMENTS" />
      <Link TARGET="src_McpMemoryService_Scripts_download_model_sh" TYPE="REFERENCES_SCRIPT" />
    </CrossLinks>
  </docs_docker_deploy_md_FILE>

  <!-- ========== EXISTING ARTIFACT REFERENCED (no edit — verified Docker-friendly) ========== -->
  <src_McpMemoryService_Scripts_download_model_sh_M11_VERIFIED FILE="src/McpMemoryService/Scripts/download-model.sh" TYPE="BASH_SCRIPT_VERIFIED">
    <annotation>ALREADY EXISTS from M4 (27 lines). M11 VERIFIES it is Docker-friendly: shebang #!/usr/bin/env bash + set -euo pipefail + portable curl -fsSL + path resolution via BASH_SOURCE relative to script (MODEL_DIR = SCRIPT_DIR/../Models). NOT executed inside Docker (the host runs it before `docker build` so Models/ is in context). No edit required for M11 — @code confirms the file exists and is portable (it already is). The `chmod +x` note in the M11 spec is informational — the script is not RUN in the image, only the host.</annotation>
    <CrossLinks>
      <Link TARGET="docs_docker_deploy_md_FILE" TYPE="REFERENCED_BY_DOC" />
    </CrossLinks>
  </src_McpMemoryService_Scripts_download_model_sh_M11_VERIFIED>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow (M11 — single `@code scope=impl:M11`)

> `@code` execution algorithm for `scope=impl:M11`. Source: M11 spec §Algorithm (lines 138-181) + §Contracts (Dockerfile lines 20-62, docker-compose lines 66-96, .dockerignore lines 100-136) + SPEC §6.1/§6.2 + M4 (Models/ + download script) + M10 (/health + LddMarkers). Infrastructure-only — NO `.cs`, NO unit tests. One `@code` dispatch, no decomposition.

1. **Create `.dockerignore`** at the repo root per §1 node + M11 spec §Contracts lines 100-136. The exact contents per the spec template (Git, build artifacts `**/bin/` `**/obj/` `artifacts/`, IDE `.vs/` `.vscode/` `.idea/`, `tests/`, `milestones/`, `*.md` with `!src/McpMemoryService/Scripts/*.sh` + `*.ps1` negation exceptions, the Docker files themselves, OS files `Thumbs.db` `.DS_Store`). **CRITICAL INVARIANT:** add a prominent comment block noting `src/McpMemoryService/Models/` is KEPT in context (the Dockerfile Stage-2 COPY needs it) — do NOT add any line that excludes `Models/`. The `*.md` rule does NOT match `model.onnx`/`tokenizer.json` (those are not .md), so they stay in context by default. The gitignore (VCS layer) excludes them from git, but .dockerignore (Docker layer) does NOT — the two systems are independent. Verify by reading the file back.

2. **Create `Dockerfile`** at the repo root per §1 node + M11 spec §Contracts lines 20-62. Multi-stage:
   - **Stage 1 (build):** `FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build` + `WORKDIR /src` + `COPY src/McpMemoryService/McpMemoryService.csproj ./McpMemoryService/` + `RUN dotnet restore ./McpMemoryService/McpMemoryService.csproj` (restore is its own layer for cache efficiency — csproj changes rarely) + `COPY src/McpMemoryService/ ./McpMemoryService/` + `RUN dotnet publish ./McpMemoryService/McpMemoryService.csproj -c Release -o /app/publish /p:UseAppHost=false` (framework-dependent — no self-contained apphost; the runtime image provides `dotnet`).
   - **Stage 2 (runtime):** `FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime` + `WORKDIR /app` + comment noting SPEC §6.1 (aspnet:10.0 base has no CUDA/libcuda deps — no action needed) + `COPY --from=build /app/publish ./` + `COPY src/McpMemoryService/Models/ ./Models/` (the gitignored model + tokenizer — host operator must run `Scripts/download-model.sh` BEFORE `docker build`; if `Models/` is empty/absent the build SUCCEEDS but the container exits at runtime per ADR-005 ONNX-load-fatal — clear log) + `EXPOSE 5000` + `ENV ASPNETCORE_URLS=http://+:5000` + `ENV MODEL_DIR=/app/Models` + `RUN adduser --disabled-password --gecos "" appuser` (Debian `adduser` is available on the non-chiseled `mcr.microsoft.com/dotnet/aspnet:10.0` base; @code VERIFIES — if `adduser` is unavailable on a chiseled base, switch to the non-chiseled tag `mcr.microsoft.com/dotnet/aspnet:10.0` explicitly, NOT a `-chiseled`/`-noble-chiseled` variant) + `USER appuser` + `ENTRYPOINT ["dotnet", "McpMemoryService.dll"]`.
   - **curl availability for healthcheck:** the docker-compose healthcheck uses `curl`. @code VERIFIES whether the `mcr.microsoft.com/dotnet/aspnet:10.0` base bundles `curl`. If curl is absent, insert BEFORE the `USER appuser` line: `RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*` (run as root, before dropping to appuser). If curl IS present (the standard non-chiseled Debian-based aspnet images historically bundle curl), skip this step and note it in a `Dockerfile` comment. This is a Dockerfile-only decision (no `.cs`), consistent with the infrastructure milestone.
   - Add a header comment block: `# McpMemoryService Dockerfile — SPEC §6.1 (runtime-only aspnet:10.0, no CUDA). M11. Prereq: run Scripts/download-model.sh so Models/ is in the build context.`

3. **Create `docker-compose.yml`** at the repo root per §1 node + M11 spec §Contracts lines 66-96 + the ADR reconciliation note. Single service `mcp-memory`:
   - `image: mcp-memory:latest`, `container_name: mcp-memory`, `build: { context: ., dockerfile: Dockerfile }`, `ports: ["5000:5000"]`.
   - `environment:` block — set BOTH the SPEC §6.2 operator-facing vars AND the .NET-binding bridge vars (per the ADR reconciliation note):
     ```yaml
     environment:
       # SPEC §6.2 operator-facing env vars (documentation + operator familiarity)
       - QDRANT_URL=<url>
       - LLM_CPP_URL=<url>
       - MODEL_DIR=/app/Models
       - ASPNETCORE_URLS=http://+:5000
       - Logging__LogLevel__Default=Information
       # .NET hierarchical-binding bridge vars (the "__" collapses to ":" → actually configures QdrantOptions.Url / LlmSummarizerOptions.BaseUrl)
       # QDRANT_URL/LLM_CPP_URL use single "_" (SPEC §6.2) which .NET does NOT split — these "__" vars are the real configuration mechanism.
       - Qdrant__Url=<url>
       - LlmSummarizer__BaseUrl=<url>
     ```
     (The IPs `<ip>` are the SPEC §6.2 example placeholders — @code keeps them as-is with a `# Override to your Qdrant/llama.cpp host` comment, OR parameterizes via `.env` file; @code picks whichever is cleanest. The `${QDRANT_URL}` reference pattern is OPTIONAL — the M11 spec template uses literal values; @code may use literals matching the spec for simplicity, or `${QDRANT_URL}` indirection to avoid duplication. Both satisfy the AC "environment variables configurable".)
   - `deploy.resources.limits: { cpus: '1.0', memory: 512M }` + `reservations: { memory: 256M }` — **STRICT per SPEC §6.2, do NOT relax**.
   - `restart: unless-stopped`.
   - `healthcheck:` `{ test: ["CMD","curl","-f","http://localhost:5000/health"], interval: 30s, timeout: 10s, retries: 3, start_period: 60s }` — `start_period: 60s` is REQUIRED (ONNX model load + `QdrantCollectionInitializer` IHostedService startup is slow; a too-short start_period causes a false-unhealthy → restart loop).
   - Add a header comment: `# McpMemoryService docker-compose — SPEC §6.2 (1 CPU / 512MB strict). M11.`

4. **(Optional) Create `docs/docker-deploy.md`** per §1 node — brief deployment notes (prerequisites, build/run, health, logs, resource check, model update, env override). Single file, no code. Create the `docs/` directory if it does not exist. If `@code` judges the doc redundant with the Dockerfile/compose header comments, it is ACCEPTABLE to SKIP — the milestone spec marks it optional. Document the choice in the `@code` return message.

5. **Verify `src/McpMemoryService/Scripts/download-model.sh`** — READ the existing file (M4, 27 lines) and confirm: (a) it uses `#!/usr/bin/env bash` + `set -euo pipefail` (portable), (b) it resolves `MODEL_DIR` relative to the script via `BASH_SOURCE` (portable, no absolute paths), (c) it uses `curl -fsSL` (portable). It is ALREADY Docker-friendly. **NO edit required** for M11 — `@code` confirms in the return message that the script is portable and the host-run-before-build workflow is sound. The `chmod +x` note in the M11 spec is informational (the host operator runs it; Git tracks the executable bit on Linux checkouts). Do NOT modify this file.

6. **Build + smoke gate (deferred to @qa, but @code SHOULD validate `docker build` parses locally if Docker is available):**
   - `@code` does NOT have to run `docker build` (Docker may be unavailable in the code-execution environment). `@code` SHOULD, however, run `docker build -t mcp-memory:latest .` IF Docker is available — this validates the Dockerfile syntax + layer ordering + that the .dockerignore does not accidentally exclude `src/McpMemoryService/McpMemoryService.csproj`. If Docker is unavailable, `@code` returns and flags that the smoke gate is @qa's responsibility (AC-1 through AC-13 are runtime Docker checks, NOT unit tests).
   - Do NOT run `dotnet build` or `dotnet test` — M11 adds NO `.cs` files, so the .NET build is unaffected (a regression here would imply an accidental edit to a `.cs` file, which `@code` MUST NOT make). `@code` MAY run `dotnet build McpMemoryService.sln` as a sanity check that no `.cs` was accidentally touched — expected `0 Warning(s), 0 Error(s)`; if it differs, `@code` has made an unintended edit and must revert.

7. **@code return:**
   - List the created files (`Dockerfile`, `docker-compose.yml`, `.dockerignore`, optionally `docs/docker-deploy.md`).
   - Confirm `Scripts/download-model.sh` is unchanged + verified portable.
   - Note the curl-availability decision (present in base vs apt-get install step added).
   - Note the env-var-bridge decision (SPEC names + .NET `__` bridge vars both present).
   - Optionally paste the `docker build` output if Docker was available; otherwise flag the smoke gate as @qa's runtime responsibility.

---

## 3. Acceptance Criteria (M11)

> From `milestones/M11-dockerfile-docker-compose.md` lines 212-224, labelled for mechanical `@qa` checking. These are RUNTIME Docker checks — NOT unit tests. `@code` source-verifies the file contents; @qa executes the Docker smoke gate (AC-1 through AC-13) with Docker Qdrant + the ONNX model present.

- [ ] **AC-1:** `docker build -t mcp-memory:latest .` succeeds (Dockerfile syntax + layer ordering valid; .dockerignore keeps `src/McpMemoryService/McpMemoryService.csproj` + `src/McpMemoryService/Models/` in context).
- [ ] **AC-2:** `docker-compose up -d` starts the `mcp-memory` container (compose syntax valid; image resolves).
- [ ] **AC-3:** Container healthcheck passes after `start_period: 60s` (ONNX + QdrantCollectionInitializer startup completes; `docker inspect --format="{{.State.Health.Status}}" mcp-memory` → `healthy`).
- [ ] **AC-4:** `curl http://localhost:5000/health` returns 200 (M10 `/health` endpoint reachable through the port mapping).
- [ ] **AC-5:** MCP `tools/list` returns 4 tools (`memory_capture`, `memory_get_stats`, `memory_retrieve`, `memory_compact` — the M9-era HostSmokeTests assertion `Be(4)` proven at runtime through the MCP Streamable-HTTP transport).
- [ ] **AC-6:** Container runs as non-root user `appuser` (`docker exec mcp-memory id` → `uid=NNNN(appuser)`; or `docker inspect --format="{{.Config.User}}" mcp-memory` → `appuser`). NOT root.
- [ ] **AC-7:** Resource limits enforced: `docker stats --no-stream mcp-memory` shows CPU ≤ 1.0, MEM ≤ 512M (compose `deploy.resources.limits` honored — SPEC §6.2 STRICT).
- [ ] **AC-8:** No CUDA/libcuda dependencies in the image (base `mcr.microsoft.com/dotnet/aspnet:10.0` has none by construction; `docker history mcp-memory:latest` shows no apt-get install of cuda/libcuda packages — SPEC §6.1).
- [ ] **AC-9:** Base image is `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime-only, no SDK) — verify via `FROM` line in Dockerfile Stage 2 + `docker history` (the SDK stage is discarded — final image has no `dotnet-sdk`).
- [ ] **AC-10:** `Models/` directory included in the image (`docker exec mcp-memory ls /app/Models` → `model.onnx` + `tokenizer.json` present; OnnxEmbeddingService ctor loads them successfully — proven by AC-3/AC-4 startup success).
- [ ] **AC-11:** Environment variables configurable (`QDRANT_URL`, `LLM_CPP_URL`, `MODEL_DIR` present in compose `environment:` block; the .NET-binding bridge vars `Qdrant__Url` + `LlmSummarizer__BaseUrl` ALSO present per the ADR reconciliation note — overriding `QDRANT_URL` would currently require also updating `Qdrant__Url` unless `@code` uses `${QDRANT_URL}` indirection; `@code` documents which pattern it chose).
- [ ] **AC-12:** `restart: unless-stopped` configured in docker-compose.yml (verify via `grep "restart:" docker-compose.yml` → `unless-stopped`).
- [ ] **AC-13:** Logs visible via `docker logs mcp-memory` AND contain `[IMP:N]` LDD markers (M10 `LddMarkers.*` constants emitted by `ILogger<>` — `docker logs mcp-memory | grep "\[IMP:"` returns matches; startup log includes `[IMP:9][ConfigureServices][SUCCESS]` or equivalent + the QdrantCollectionInitializer `[IMP:1]`/`[IMP:9]`/`[IMP:10]` markers from M5/M10).

---

## Notes for @code (M11)

1. **INFRASTRUCTURE milestone — NO `.cs` files, NO unit tests.** M11 produces Docker configuration files only. Do NOT touch any `.cs` file (Program.cs, appsettings.json, Options classes, services, tools, middleware, tests). A `.cs` edit would be a regression — the .NET build (`dotnet build McpMemoryService.sln`) must remain at `0 Warning(s), 0 Error(s)` with the EXISTING 74/74 unit-test count unchanged. If `@code` runs `dotnet build` as a sanity check and the count differs, an unintended `.cs` edit has occurred — revert immediately and re-verify. The env-var binding gap (SPEC §6.2 single-underscore names vs .NET `__` hierarchy separator) is closed via docker-compose env vars ONLY (see ADR reconciliation note) — do NOT add `.cs` env-var bridging code.

2. **Models/ prerequisite — the host operator MUST run `Scripts/download-model.sh` (or `Download-Model.ps1` on Windows) BEFORE `docker build`.** The `Models/model.onnx` + `Models/tokenizer.json` are gitignored (M1 root .gitignore + M4 Models/.gitignore) but MUST be in the Docker build context. If `Models/` is empty/absent at build time, `docker build` SUCCEEDS (the COPY may create an empty dir or fail depending on Docker version — modern Docker errors on COPY of a missing path; @code verifies the behavior), but the container exits at runtime per ADR-005 ONNX-load-fatal (`OnnxEmbeddingService` ctor throws → host Exit 1). Document this prerequisite in the Dockerfile header comment + `docs/docker-deploy.md` if created. The `@qa` smoke gate (AC-10) catches an empty-Models/ build as a startup failure.

3. **curl availability in `mcr.microsoft.com/dotnet/aspnet:10.0` — VERIFY.** The docker-compose healthcheck uses `curl -f http://localhost:5000/health`. The standard non-chiseled `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian bookworm-based) historically bundles `curl`. @code VERIFIES by checking the base image's installed packages OR by attempting `docker build` + `docker run --rm mcp-memory:latest curl --version`. If curl is ABSENT, insert BEFORE the `USER appuser` line in the Dockerfile runtime stage: `RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*` (run as root, before dropping to appuser — the `rm -rf /var/lib/apt/lists/*` keeps the layer small). If curl IS present, skip + add a `# curl bundled in aspnet:10.0 base — healthcheck-compatible` comment. This is a Dockerfile-only decision (no `.cs`). Do NOT use `wget --spider` as a substitute unless curl is definitively absent AND adding apt-get is undesirable (curl is the SPEC-faithful healthcheck tool per the M11 spec template).

4. **`adduser` availability — the non-chiseled `aspnet:10.0` is Debian-based and bundles `adduser`.** The spec template uses `RUN adduser --disabled-password --gecos "" appuser` (Debian/Ubuntu `adduser` syntax — NOT busybox `adduser`). @code VERIFIES the base image is the non-chiseled variant (`mcr.microsoft.com/dotnet/aspnet:10.0`, NOT `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` or similar). The chiseled/noble-chiseled variants do NOT include a package manager (`apt-get`) or `adduser` — they are minimal and would require a different non-root strategy (`useradd` is also absent). The M11 spec template explicitly targets the non-chiseled `aspnet:10.0`. If @code discovers the non-chiseled tag is unavailable (e.g., only chiseled ships for .NET 10 at @code's Docker registry), @code FLAGS this to @qa/@architect — do NOT silently switch to a chiseled base (the non-root user + curl-healthcheck + apt-get-curl strategy all assume the non-chiseled Debian base).

5. **Env-var bridge — SPEC §6.2 vs .NET `__` separator.** The realized service binds `QdrantOptions.Url` via `configuration.GetSection("Qdrant")` + `LlmSummarizerOptions.BaseUrl` via `configuration.GetSection("LlmSummarizer")`. .NET's environment-variable configuration provider splits on `__` (double underscore), NOT `_` (single). So `QDRANT_URL` binds to a flat key `QDRANT_URL` (does NOT reach `Qdrant:Url`), and `LLM_CPP_URL` binds to `LLM_CPP_URL` (does NOT reach `LlmSummarizer:BaseUrl`). The reconciled docker-compose sets BOTH the SPEC §6.2 names (operator-facing, AC-11 compliance) AND the `.NET-binding bridge vars` `Qdrant__Url` / `LlmSummarizer__BaseUrl` (the ACTUAL configuration mechanism). `@code` MAY use `${QDRANT_URL}` / `${LLM_CPP_URL}` indirection in the bridge vars (single source of truth — change the SPEC-named var, the bridge follows) OR literal duplicates (simpler, matches the M11 spec template). Document the chosen pattern in a docker-compose comment. `MODEL_DIR=/app/Models` is informational (the app uses relative `OnnxModel:ModelPath="Models/model.onnx"` resolved via `AppContext.BaseDirectory=/app` → `/app/Models/model.onnx` — already correct because the Dockerfile COPYs `Models/` to `/app/Models/`); no `.cs` binding needed. `ASPNETCORE_URLS=http://+:5000` is a native ASP.NET Core env var (understood natively). `Logging__LogLevel__Default=Information` already uses `__` correctly (note: the spec template already uses this correct form — it is the ONE env var in the template that binds correctly; the QDRANT_URL/LLM_CPP_URL gap is the architect's flagged discovery).

6. **Resource limits are STRICT per SPEC §6.2 — do NOT relax.** `cpus: '1.0'` + `memory: 512M` + `reservations.memory: 256M`. The SPEC rationale: model ~200 MB + runtime ~150 MB + buffers within 512 MB; 1 core on the TrueNAS E5-2667 v4 host. If the @qa smoke gate reports OOM-kills (MEM exceeded), the fix is NOT to raise the limit — it is to investigate the runtime footprint (the OnnxEmbeddingService `IntraOpNumThreads=4` from OnnxModelOptions may inflate memory; the OPERATOR may lower it via the `OnnxModel__IntraOpNumThreads` env var if needed). Flag any OOM observation to @architect — do NOT silently relax. AC-7 enforces this.

7. **`start_period: 60s` is intentional — do NOT shorten.** The `QdrantCollectionInitializer` IHostedService (M5) calls `EnsureCollectionExistsAsync` at startup (creates the 384-dim collection + 5 payload indexes if absent — slow on first run), AND the `OnnxEmbeddingService` ctor (M4) loads the ~90-120 MB ONNX model + builds the `InferenceSession` (CPU-thread setup). A `start_period` < 60s risks a false-unhealthy → restart loop (the healthcheck fails before startup completes, Docker restarts, the slow startup restarts, infinite loop). 60s is the M11 spec value; keep it. `interval: 30s` + `timeout: 10s` + `retries: 3` give the container a wide healthy-window after the start period.

8. **`.dockerignore` — KEEP `Models/` in context.** The single most-critical .dockerignore invariant for M11. The Dockerfile Stage-2 `COPY src/McpMemoryService/Models/ ./Models/` FAILS the build if `Models/` is excluded from the context (Docker errors: "no such file or directory"). The spec template's .dockerignore does NOT exclude `Models/` (it excludes `**/bin/`, `**/obj/`, IDE, `tests/`, `milestones/`, `*.md`, etc. — none match `Models/model.onnx` or `Models/tokenizer.json`). Add a prominent comment in the .dockerignore documenting this (the spec template already includes a NOTE comment — preserve + extend it). The gitignore (M1 root + M4 Models/.gitignore) excludes these files from GIT — the two ignore systems are independent; the Docker context is what matters for `docker build`.

9. **`docs/docker-deploy.md` is OPTIONAL.** The milestone spec marks it optional. If `@code` judges the deployment notes are adequately covered by the Dockerfile + docker-compose header comments, it is acceptable to SKIP creating `docs/docker-deploy.md`. Document the skip decision in the return message. If created, keep it brief (≤100 lines) — prerequisites, build/run, health, logs, resource check, model update, env override. Create the `docs/` directory if it does not exist. Do NOT create a README.md or anywhere else — only docker-deploy.md if chosen.

10. **`docker build` validation — BEST-EFFORT.** If Docker is available in `@code`'s execution environment, run `docker build -t mcp-memory:latest .` to validate the Dockerfile syntax + .dockerignore + layer ordering. This is a strong signal (catches typos, missing COPY sources, context-exclusion bugs). If Docker is unavailable, `@code` returns and the smoke gate is @qa's runtime responsibility (AC-1..AC-13 are Docker checks). Do NOT simulate a build by reading files — the Dockerfile alone is the source-of-truth; @qa's `docker build` is the verification.

11. **Decomposition decision — SINGLE `@code scope=impl:M11` dispatch.** 3 cohesive Docker files (Dockerfile + docker-compose.yml + .dockerignore) + 1 optional doc + 1 verified-not-edited script. The trio is atomic: a Dockerfile without its compose/ignore is unusable; a compose without its Dockerfile has no image; an ignore without its Dockerfile is meaningless. Splitting would yield non-running intermediate states. Below the >5-new-methods threshold (there are zero methods — no `.cs`). **NO `## Decomposition` section** is appended.

12. **AGENTS.md build commands — N/A for M11.** M11 does not run `dotnet build` / `dotnet test` as its primary gate (it MAY run `dotnet build` as a no-regression sanity check). The M11 gate is the Docker smoke gate (`docker build` + `docker-compose up -d` + `curl /health` + `tools/list`), exercised by @qa. `@code` returns the Dockerfile + compose + .dockerignore (and optionally docker-deploy.md) for @qa to verify.

13. **profile.md consistency.** Plan prose is technical English (matches the M5..M10 plan style). No Russian summary header required.

14. **Web search — NOT REQUIRED for M11.** All M11 decisions (multi-stage build pattern, `aspnet:10.0` runtime base, `adduser` non-root strategy, resource limits from SPEC §6.2, healthcheck `curl /health` + start_period 60s, .dockerignore context hygiene, the `__` env-var separator discovery from .NET configuration-provider docs) are derived from the M11 spec + SPEC §6.1/§6.2 + M10 (`/health` endpoint) + M4 (Models/ + download script) + .NET configuration-provider local knowledge (the `__` separator is documented .NET behavior since .NET Core — net10.0-stable; no `[UNVERIFIED_VERSION]` tag needed). The curl-in-aspnet-base question is a @code runtime verification, not a web-search item. No `web_search` / `fetch_and_extract` calls are made for M11.

---

## M11a — Qdrant ApiKey + LLM URL update (mini-milestone)

| Field | Value |
|---|---|
| Current Milestone | **M11a — Qdrant ApiKey support + LLM URL update (mini-milestone between M11 and M12; adds nullable `QdrantOptions.ApiKey` + `QdrantClient(apiKey:)` named-parameter pass-through + `Qdrant__ApiKey` env-var bridge in `docker-compose.yml`; aligns `appsettings.json` `LlmSummarizer.BaseUrl` to the operator's new llama.cpp host)** |
| Status | PLAN_READY (awaiting `@code scope=impl:M11a`) |
| Milestone Deps | **M5 (DONE — `QdrantOptions.cs` [4 properties: `Url`, `CollectionName`, `GrpcPort=6334`, the M11a-added `ApiKey?=null`], `QdrantService.cs` ctor L66 `new QdrantClient(host, port: _options.GrpcPort)` → M11a becomes `new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey)`, `QdrantCollectionInitializer` IHostedService [fatal-exits on Qdrant auth failure — the M11a ApiKey pass-through unblocks it], `QdrantServiceTests` integration ctor L241 [UNCHANGED — `apiKey:` defaults to `null` → unauth Docker `qdrant/qdrant` stays green]). M11 (DONE — `Dockerfile` + `docker-compose.yml` + `.dockerignore`, `@qa SUCCESS 0ee493d` with the operator-action-required note that the Qdrant-now-requires-auth external prerequisite gates AC-3/AC-4/AC-5/AC-11 runtime — exactly the gap M11a fills).** M11a does NOT depend on M12 and does NOT block M12 (M12 e2e will exercise the ApiKey path naturally once the operator fills `Qdrant__ApiKey`; M12 can also defer to operator-side env-var fill). The LLM URL (`LLM_CPP_URL` / `LlmSummarizer__BaseUrl` in `docker-compose.yml`) was ALREADY updated by the operator to `<url>` BEFORE this milestone — M11a's only LLM-related action is the `appsettings.json` default alignment + header-comment reconciliation (documentation; no Docker behavior change — the env-var bridge override already wins at container-time). |
| Dispatch Recommendation | **Single `@code scope=impl:M11a` — NO decomposition.** 4-file additive cohesive change: (1) `QdrantOptions.cs` — add nullable `string? ApiKey { get; init; }` (default `null`); (2) `QdrantService.cs` ctor L66 — add `apiKey: _options.ApiKey` named-parameter appendage + an `[IMP:M11a]` comment; (3) `appsettings.json` — add `"ApiKey": null` after `GrpcPort` + align `LlmSummarizer.BaseUrl` to the operator's new host; (4) `docker-compose.yml` — add `- Qdrant__ApiKey=` (empty default) after `Qdrant__Url` + update the binding-names header comment. PLUS optional `.gitignore` `.env` hygiene line + `.test_counter.json` reset-confirmation (`{"counter":0}`). 4-file cohesive atomic set (the ApiKey must flow Options → Service → config → env TOGETHER or the contract is incomplete — splitting Options-from-Service would yield a non-compiling intermediate state; splitting config-from-env would yield a service that has the property but no deploy-time fill mechanism). **NO new unit tests** — the change is a nullable named-parameter pass-through; the existing 74/74 M10 unit baseline MUST stay green (no-regression gate). **NO `## Decomposition` section** is appended. |
| Etap | Etap 1 (M1..M12, with the M11a mini-coda between M11 and M12). Etap 2 future. |

## ADRs Touched by M11a

| ADR | Decision | M11a Action |
|---|---|---|
| **ADR-005** | Silent fallback on Qdrant unavailability; Qdrant-down (retrieve) → empty results, (capture) → `success=false`, (get_stats) → `count=-1`, (compact) → `status=error`, ONNX fail = Exit 1 fatal | **NO regression.** The M11a `ApiKey` change is a nullable contract-additive — `ApiKey=null` preserves the M5 unauthenticated behavior exactly. ADR-005 silent-fallback is the **caller's** (M10 tool + middleware) contract and is NOT touched; `QdrantService` continues to THROW on Qdrant failure (the M5 do-not-swallow invariant); the M10 `QdrantResiliencePolicy` continues to catch + fallback. The `ApiKey` only changes WHAT the `QdrantClient` sends in its auth header (none vs bearer); it does NOT change the throw/fallback control flow. Auth failure (`Unauthenticated` gRPC status) raises `RpcException` → `QdrantResiliencePolicy.ExecuteWithFallbackAsync` catches → returns fallback (ADR-005) → tool DTO failure shape → no MCP connection break. So an authenticated-but-wrong-key Qdrant deployment STILL gets the ADR-005 silent-fallback treatment (empty results / `success=false` / `count=-1`), NOT a hard fatal-exit — the `QdrantCollectionInitializer` IHostedService path IS the one place where a Qdrant auth failure hard-exits (host fails to start, `restart: unless-stopped` loops) — but that's the initializer's contract (M5), NOT the per-request path. M11a does NOT change `QdrantCollectionInitializer`. |
| **ADR-006** | .NET 10 target | csproj already `net10.0`. The nullable `string? ApiKey` property is net10.0-native (nullable reference types enabled in M2 csproj). The `Qdrant.Client` 1.18.1 ctor `apiKey:` named-parameter feature requires no new package. No csproj changes. |
| **ADR-008** | `LlmSummarizerOptions` (BaseUrl, TimeoutSeconds=60) | **NO change to the Options class.** M11a only aligns the `appsettings.json` `LlmSummarizer.BaseUrl` VALUE to the operator's new llama.cpp host (a config-value edit, not a contract change). The `docker-compose.yml` `LlmSummarizer__BaseUrl` env-var bridge override was already updated by the operator before M11a → no compose line re-edit for the LLM URL; only the binding-names header comment is updated to document the reconciliation. |

> **M11a invariants (must NOT regress):**
> 1. **Backward-compatibility by construction.** `ApiKey` defaults to `null`; `QdrantClient` ctor `apiKey:` defaults to `null`. Unauthenticated Qdrant deployments (the M5 integration-test path against an unauth Docker `qdrant/qdrant`) continue to work UNCHANGED. The `QdrantServiceTests.cs` ctor L241 stays `new QdrantClient("localhost", port: 6334)` (no `apiKey:` arg) — AC-8 explicitly asserts this is unchanged.
> 2. **Named-parameter passing skips `bool https`.** The `QdrantClient` ctor (Qdrant.Client 1.18.1, operator web-verified) signature is `QdrantClient(string host, int port = 6334, bool https = false, string? apiKey = null, TimeSpan grpcTimeout = default, ILoggerFactory? loggerFactory = null)`. The M11a invocation uses `apiKey:` NAMED (not positional) to skip the `bool https = false` positional default → no behavior change to the http/https scheme (stays http). DO NOT add a positional `false` between `port:` and `apiKey:` — the named-parameter is the cleanest form.
> 3. **No `.cs` business logic added.** M11a is a single nullable property + a single named-parameter pass-through. NO new branch, NO new try/catch, NO new validation, NO new logging marker (the `[IMP:M11a]` comment is an inline source comment, NOT a runtime `ILogger` emission — there is no `[IMP:N]` LDD marker introduced by M11a; the `LddMarkers.cs` constants FILE is NOT touched). The `QdrantOptions.cs` MODULE_CONTRACT header `[GREP_SUMMARY]` + `[CHANGES]` are appended (additive — the M5 entries preserved).
> 4. **No test forward-motion.** M11a adds NO unit tests; the 74/74 M10 unit baseline MUST stay green (no-regression gate, AC-2). The integration tests (`QdrantServiceTests` `Category=Integration`) are NOT exercised by the @code dispatch (no Docker); @qa runs the full + Docker smoke gate. The ApiKey-pass-through is covered INDIRECTLY — unauth integration tests prove the `apiKey:=null` default works; the authenticated path is the @qa runtime Docker smoke gate once the operator fills `Qdrant__ApiKey`.
> 5. **No new package refs.** The `Qdrant.Client` package (already pinned from M2/M5) exposes the `apiKey:` ctor parameter on 1.18.1 — no upgrade, no new `PackageReference`. No csproj edit.

---

## PURPOSE (M11a)

Add a nullable `ApiKey` property to `QdrantOptions` (M5) and pass it through to the `QdrantClient` constructor as the `apiKey:` named parameter, so the service can connect to a Qdrant instance that requires API-key authentication (the operator's production `<ip>:<port>`). The change is strictly backward-compatible: `ApiKey` defaults to `null`, the `QdrantClient` `apiKey` parameter defaults to `null`, and unauthenticated Qdrant deployments — including the M5 integration tests against an unauth Docker `qdrant/qdrant` — continue to work unchanged (the `QdrantServiceTests` ctor L241 is NOT modified: `apiKey:` defaults to `null`). Add the `Qdrant__ApiKey=` env-var bridge (empty default — operator supplies the real key at deploy time via `.env` / shell env / compose edit) to `docker-compose.yml` after the existing `Qdrant__Url` line. Reconcile the LLM URL contract: the operator already changed `LLM_CPP_URL` + `LlmSummarizer__BaseUrl` in `docker-compose.yml` to the new llama.cpp host BEFORE M11a; M11a aligns the `appsettings.json` `LlmSummarizer.BaseUrl` DEFAULT (replacing the M2/M6 placeholder `<url>`) for local `dotnet run` consistency + updates the compose header comment to document the binding (no Docker behavior change — the env-var bridge override already wins at container-time). Reset `.test_counter.json` to `{"counter":0}` (anti-loop reset on a fresh `@code scope=impl:M11a` dispatch). Verify/build + 74/74 no-regression unit gate. NO new unit tests (the change is a nullable named-parameter pass-through — there is no new branch/behavior to assert at the unit level; mocking the sealed `QdrantClient` ctor to assert the `apiKey` arg would require disproportionate scaffolding). The acceptance gate = no-regression unit run + @qa's runtime Docker smoke gate (AC-3/AC-4/AC-5/AC-11 unblock once the operator fills the real ApiKey into `Qdrant__ApiKey`).

---

## 1. Draft Code Graph (M11a)

> M11a is a config-pass-through coda — it ADDS one nullable property to an existing Options POCO + ONE named-parameter to an existing `QdrantClient` ctor call + ONE `appsettings.json` line + ONE `docker-compose.yml` env-var line. No new files, no new interfaces, no new services, no new test files. The `QdrantOptions` node gains one `<..._ApiKey_PROPERTY>` child; the `QdrantService` ctor node gains an `[IMP:M11a]` comment + a named-parameter in the existing `new QdrantClient(...)` call; the `docker_compose_yml_FILE` node gains an `<..._Qdrant_ApiKey_ENV>` child; the `appsettings.json` node gains an `ApiKey=null` field. The AppGraph.xml cross-links are EXTENDED (not replaced) — see §AppGraph.xml edit below (this is a graph annotation update only; the realized code changes are the 4 file edits).

```xml
<DraftCodeGraph> (M11a subset — additive deltas on existing nodes)
  <!-- ========== QdrantOptions.ApiKey (NEW nullable property) ========== -->
  <src_McpMemoryService_Configuration_QdrantOptions_ApiKey_PROPERTY NAME="ApiKey" TYPE="PROPERTY" NULLABLE="true" DEFAULT="null" INIT="true" MILESTONE="M11a">
    <annotation>NEW nullable property added in M11a. public string? ApiKey { get; init; }. Default null. Bound to the "Qdrant:ApiKey" config key (appsettings.json "ApiKey": null OR env var Qdrant__ApiKey). Consumed by QdrantService ctor (M11a: passes as apiKey: named parameter to QdrantClient). When null → QdrantClient connects without auth (backward-compatible with M5 + the unauthenticated integration tests). When set → QdrantClient sends "Authorization: Bearer &lt;key&gt;" on every gRPC call (Qdrant.Client 1.18.1 behavior — operator web-verified ctor signature). NO new validation (nullable+init-only; the SDK guards string.IsNullOrEmpty). XML doc + remarks per csharp-conventions; [CHANGES] appendage to the existing M11a [GREP_SUMMARY]/[CHANGES] header (additive — M5 entries preserved).</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Configuration_QdrantOptions_cs" TYPE="ADDED_TO_POCO" />
      <Link TARGET="src_McpMemoryService_Services_QdrantService_cs" TYPE="CONSUMED_BY_CTOR" />
      <Link TARGET="src_McpMemoryService_appsettings_json" TYPE="BOUND_BY" />
      <Link TARGET="docker_compose_yml_FILE" TYPE="BOUND_BY_ENV" />
    </CrossLinks>
  </src_McpMemoryService_Configuration_QdrantOptions_ApiKey_PROPERTY>

  <!-- ========== QdrantService ctor — apiKey: named-parameter appendage (Edit on existing ctor) ========== -->
  <src_McpMemoryService_Services_QdrantService_ctor_M11a_EDIT FILE="src/McpMemoryService/Services/QdrantService.cs" TYPE="CTOR_EDIT">
    <annotation>EDIT the existing ctor body (currently L64-66):
  var uri = new Uri(_options.Url);
  var host = uri.Host;
  // [IMP:M11a][QdrantService][PROGRESS] ApiKey pass-through to QdrantClient — backward-compatible (apiKey defaults null)
  _client = new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey);
The named-parameter apiKey: skips the bool https = false positional default. STRICTLY LOCAL: do NOT touch the EnsureCollectionExistsAsync method, the BuildFilter/BuildCompactFilter/CreatePayloadIndexIdempotentAsync helpers, the Upsert/Search/Count/Delete/Get/GetBatchForCompactAsync methods, the IQdrantService interface, or the _client/_options/_logger fields. DO NOT touch the M5 BUG_FIX_CONTEXT scars (Distance.Dot/Datetime-range-index/PointStruct.Payload/BuildFilter-entryType/Direction.Asc — preserved verbatim). DO NOT touch any other ctor line (the Uri parsing + host extraction stays — only line 66 gains the apiKey: appendage + the [IMP:M11a] comment immediately above it). DO NOT touch the MODULE_CONTRACT header except the [GREP_SUMMARY] ApiKey appendage + the [CHANGES] M11a appendage (additive — M5 entries preserved).</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Configuration_QdrantOptions_ApiKey_PROPERTY" TYPE="PASSES_THROUGH" />
      <Link TARGET="src_McpMemoryService_Services_QdrantService_cs" TYPE="EDITS_FIELD_INIT" />
    </CrossLinks>
  </src_McpMemoryService_Services_QdrantService_ctor_M11a_EDIT>

  <!-- ========== appsettings.json — ApiKey:null additive + LlmSummarizer.BaseUrl value alignment ========== -->
  <src_McpMemoryService_appsettings_json_M11a_EDIT FILE="src/McpMemoryService/appsettings.json" TYPE="JSON_EDIT">
    <annotation>EDIT the Qdrant section: ADD "ApiKey": null, on a NEW line after "GrpcPort": 6334, (preserve the existing trailing-comma style — the file already uses trailing commas; System.Text.Json's Microsoft.Extensions.Configuration.Json reader accepts them). EDIT the LlmSummarizer section: REPLACE "BaseUrl": "<url>" (the M2/M6 placeholder) with the operator's new llama.cpp host — the same host the operator already set in docker-compose.yml LlmSummarizer__BaseUrl (verify the value matches before writing; if the operator has already edited appsettings.json BaseUrl, leave it). JSON validity: null is a valid JSON literal; the file remains parseable. STRICTLY LOCAL: do NOT touch the Logging/AllowedHosts/OnnxModel/Mcp sections.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Configuration_QdrantOptions_ApiKey_PROPERTY" TYPE="BINDS_FIELD" />
      <Link TARGET="src_McpMemoryService_Configuration_LlmSummarizerOptions_cs" TYPE="BINDS_VALUE" />
    </CrossLinks>
  </src_McpMemoryService_appsettings_json_M11a_EDIT>

  <!-- ========== docker-compose.yml — Qdrant__ApiKey env-var additive (LLM URL unchanged; already updated by operator) ========== -->
  <docker_compose_yml_M11a_EDIT FILE="docker-compose.yml" TYPE="COMPOSE_EDIT">
    <annotation>EDIT the environment: block: ADD "- Qdrant__ApiKey=" (empty value — empty-string literal interpreted by docker-compose) on a NEW line IMMEDIATELY AFTER the existing "- Qdrant__Url=<url>" line. ADD a short inline comment below the new line noting the deploy-time-fill + the .env/shell/compose-edit conventions (terse — preserve YAML whitespace). UPDATE the .NET config binding names header comment: replace "# These map to appsettings.json sections: Qdrant:Url, LlmSummarizer:BaseUrl" with "# These map to appsettings.json sections: Qdrant:Url, Qdrant:ApiKey, LlmSummarizer:BaseUrl". DO NOT re-edit the LLM_CPP_URL / LlmSummarizer__BaseUrl lines — they are ALREADY at the new host (operator's pre-M11a manual edit); the only LLM-related change is the header-comment binding-names list update (documentation). STRICTLY LOCAL: do NOT touch the Dockerfile build/ports/MODEL_DIR/ASPNETCORE_URLS/Logging lines, the deploy.resources.limits, the restart policy, the healthcheck block, or the networks block.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Configuration_QdrantOptions_ApiKey_PROPERTY" TYPE="BINDS_ENV" />
      <Link TARGET="docker_compose_yml_FILE" TYPE="EXTENDS_ENV_BLOCK" />
    </CrossLinks>
  </docker_compose_yml_M11a_EDIT>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow (M11a — single `@code scope=impl:M11a`)

1. **Edit `src/McpMemoryService/Configuration/QdrantOptions.cs`** (per §1 node + spec §Contracts):
   - ADD after the `GrpcPort` property:
     ```csharp
     /// <summary>
     /// Gets the optional API key for Qdrant authentication. Defaults to <c>null</c> (no authentication).
     /// </summary>
     /// <remarks>
     /// [INVARIANTS]: When <c>null</c>, the QdrantClient connects without authentication (backward-compatible
     ///   with M5 behavior and the existing integration tests against an unauthenticated Docker Qdrant).
     ///   When set, the key is passed to the QdrantClient constructor which includes it as an
     ///   <c>Authorization: Bearer &lt;key&gt;</c> header on every gRPC call.
     /// [RATIONALE]: Added in M11a to support Qdrant deployments that require API-key authentication
     ///   (the operator's production Qdrant at <ip>:<port> enforces auth). Nullable + default null
     ///   keeps the change strictly backward-compatible — no test, appsettings, or env-var change is REQUIRED
     ///   for existing unauthenticated deployments.
     /// [CHANGES]: LAST_CHANGE: M11a — added ApiKey property (nullable, init-only) per the M11 @qa
     ///   operator-action-required note.
     /// </remarks>
     public string? ApiKey { get; init; }
     ```
   - UPDATE the `#region MODULE_CONTRACT` `[GREP_SUMMARY]` line: append `, ApiKey` to the existing token list (the M5 token list ends with `connection stateless`; the new token list ends with `connection stateless, ApiKey`). 
   - UPDATE the `[STRUCTURE]` line: replace `QdrantClient(host, GrpcPort)` with `QdrantClient(host, GrpcPort, ApiKey)` (reflects the M11a ctor call). Preserve the rest of the `[STRUCTURE]` line (`AppSettings → IOptions<T> → ... → ConnectionPool → Collection`).
   - UPDATE the `[CHANGES]` line at the end of the `remarks` block: append `M11a — added ApiKey property (nullable, init-only) per the M11 @qa operator-action-required note.` to the existing `LAST_CHANGE: M5 — added GrpcPort property...` text — additive, do NOT remove the M5 entry. Result shape: `[CHANGES]: LAST_CHANGE: M11a — added ApiKey property (nullable, init-only) per the M11 @qa operator-action-required note. Previous: M5 — added GrpcPort property (default 6334) per Qdrant.Client gRPC requirement.`

2. **Edit `src/McpMemoryService/Services/QdrantService.cs`** (per §1 node):
   - Locate the ctor body, currently L64-66: `var uri = new Uri(_options.Url); var host = uri.Host; _client = new QdrantClient(host, port: _options.GrpcPort);`
   - REPLACE line 66 with two lines (comment + the M11a ctor):
     ```csharp
     // [IMP:M11a][QdrantService][PROGRESS] ApiKey pass-through to QdrantClient — backward-compatible (apiKey defaults null)
     _client = new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey);
     ```
   - UPDATE the `#region MODULE_CONTRACT` `[GREP_SUMMARY]` line: append `, ApiKey` to the existing token list (the M5 token list mentions `QdrantClient`; the new token list ends with `..., ApiKey`). 
   - UPDATE the `[STRUCTURE]` line: replace `ctor(QdrantClient) → EnsureCollection...` with `ctor(QdrantClient with apiKey:) → EnsureCollection...` (reflects the M11a ctor construction). Preserve the rest.
   - UPDATE the `[CHANGES]` line at the end of the `remarks` block: APPEND to the existing M5 `[CHANGES]` block (which currently mentions `LAST_CHANGE: M5 debug counter=1 — added PayloadSchemaType.Datetime range index...`) a new sentence: `M11a — added apiKey: _options.ApiKey pass-through to the QdrantClient ctor (M11 @qa operator-action-required note; Qdrant.Client 1.18.1 ctor signature operator web-verified: apiKey is the 4th param, defaults null). Backward-compatible — null ApiKey = no auth, M5 integration tests unchanged.`. Additive — do NOT remove the M5 entry.
   - DO NOT touch any other line (the EnsureCollectionExistsAsync, the BuildFilter/BuildCompactFilter/CreatePayloadIndexIdempotentAsync, the public CRUD methods, the M5 BUG_FIX_CONTEXT scars, the IQdrantService interface, the fields, the rest of the ctor body).

3. **Edit `src/McpMemoryService/appsettings.json`** (per §1 node):
   - In the `Qdrant` section, ADD `"ApiKey": null,` on a new line after `"GrpcPort": 6334,` AND before `"CollectionName": "opencode_memory"`:
     ```json
     "Qdrant": {
       "Url": "http://localhost:6333",
       "GrpcPort": 6334,
       "ApiKey": null,
       "CollectionName": "opencode_memory"
     },
     ```
   - In the `LlmSummarizer` section, REPLACE the existing `"BaseUrl": "<url>"` (the M2/M6 placeholder) with the operator's new llama.cpp host — the same value the operator already set in `docker-compose.yml` `LlmSummarizer__BaseUrl`. **VERIFY the value matches before writing** — read `docker-compose.yml` line 13 (the `LLM_CPP_URL` setting) and line 18 (the `LlmSummarizer__BaseUrl` setting) to confirm the host. If the operator has already edited `appsettings.json`'s `BaseURL`, leave it as-is (no-op). The spec placeholder `<url>` is documentation-redacted; @code writes the FULL host (the operator's full redacted-or-not IP is what the committed `docker-compose.yml` already has — match it verbatim).
   - STRICTLY LOCAL: do NOT touch the Logging/AllowedHosts/OnnxModel/Mcp sections. JSON validity: `null` is a valid JSON literal; trailing commas already used by the file; `System.Text.Json` default config reader accepts them.

4. **Edit `docker-compose.yml`** (per §1 node):
   - In the `environment:` block, immediately AFTER the line `- Qdrant__Url=<url>` (currently line 17), ADD:
     ```yaml
           - Qdrant__ApiKey=              # M11a — empty default; operator supplies the real Qdrant API key via .env / shell env / compose edit (docker-compose.yml), do NOT commit the real key
     ```
   - UPDATE the header comment block (currently line 15-16): replace `# These map to appsettings.json sections: Qdrant:Url, LlmSummarizer:BaseUrl` with `# These map to appsettings.json sections: Qdrant:Url, Qdrant:ApiKey, LlmSummarizer:BaseUrl`. 
   - DO NOT re-edit `LLM_CPP_URL` (line 13) or `LlmSummarizer__BaseUrl` (line 18) — they are ALREADY at the operator's new host; M11a does NOT re-edit those (only the header-comment binding-names list is updated to include `Qdrant:ApiKey`).
   - STRICTLY LOCAL: do NOT touch the QDRANT_URL/LLM_CPP_URL/MODEL_DIR lines, the LlmSummarizer__BaseUrl line, the ASPNETCORE_URLS line, the Logging lines, the deploy.resources block, the restart/healthcheck/networks blocks.
   - Compose validity: `- KEY=` (empty value) is valid YAML — interpreted as the empty string. The mime of the line duplicates the existing `__ bridge` convention exactly; no new pattern.

5. **Verify/edit `.gitignore`** (operator secret hygiene, per §Algorithm step 5):
   - Open `.gitignore`. Confirm `.env` (or `.env*`) is ignored. If absent, ADD `.env` and `.env.*` lines near the existing appendage pattern (after the `.opencode/memory/` / `Models/*.onnx` additions — the M1 gitignore-append pattern). This prevents accidental commit of the operator's real ApiKey if they use the compose `.env` override pattern. If `.env` is already ignored, this is a no-op — note it in the return message.
   - OPTIONAL: ADD a `.env.example` file with a single `Qdrant__ApiKey=` line (empty placeholder) for documentation. Skip if the `docker-compose.yml` inline comment is sufficient; document the skip in the return message if skipped.

6. **Verify/overwrite `.test_counter.json`**:
   - Open `.test_counter.json`. Confirm content is exactly `{"counter":0}`. If it drifted (shouldn't — last read was 0), OVERWRITE to `{"counter":0}`. This is the anti-loop reset on a fresh `@code scope=impl:M11a` dispatch (the protocol auto-resets anyway; this is the explicit confirmation). Write the file with NO trailing newline / BOM — the existing content is a single line.

7. **Build + no-regression unit gate before return:**
   - `dotnet build McpMemoryService.sln` → **0 Warning(s), 0 Error(s)**. The nullable property + the named-parameter appendage are non-breaking compile changes (no new using, no new package, no overload-resolution ambiguity — there's only one `QdrantClient(string host, ...)` ctor on 1.18.1, operator-verified).
   - `dotnet test --filter "Category!=Integration"` → **74 passed, 0 failed, 0 skipped** (the M11a unit-test count is the SAME as M10's 74/74; M11a adds NO unit tests; the gate is no-regression only). The `ApiKey` property is NOT exercised by any unit test (it's a nullable named-parameter pass-through; the integration tests cover the unauth path; adding a mocked-sealed-QdrantClient-ctor-asserts test would be disproportionate scaffolding).
   - Do NOT run `dotnet test` (full) — the `QdrantServiceTests` integration tests require Docker Qdrant at `localhost:6334` (unauthenticated `qdrant/qdrant` image); @qa runs the full + Docker smoke gate separately. The integration test ctor at L241 stays `new QdrantClient("localhost", port: 6334)` — `apiKey:` defaults to `null` → the integration tests pass against unauth Qdrant unchanged.

8. **Source-verify the ApiKey wired (no Docker required)** — `@code` validates the 4-file change is consistent:
   ```bash
   grep -n "ApiKey" src/McpMemoryService/Configuration/QdrantOptions.cs src/McpMemoryService/Services/QdrantService.cs src/McpMemoryService/appsettings.json docker-compose.yml
   # Expect: >=4 matches across the 4 files (1 property declaration + 1 ctor param + 1 JSON field + 1 env-var line).
   grep -n "new QdrantClient" src/McpMemoryService/Services/QdrantService.cs tests/McpMemoryService.Tests/Services/QdrantServiceTests.cs
   # Expect: src/McpMemoryService/Services/QdrantService.cs:66 with the apiKey: named param; tests/...QdrantServiceTests.cs:241 unchanged (no apiKey:).
   ```

---

## 3. Acceptance Criteria (M11a)

> Verbatim from `milestones/M11a-api-key-llm-url.md` §Acceptance Criteria. The first 2 are no-regression unit/build gates; the rest are source-verification + contract-presence checks (NO runtime Docker exercised by @code — that's @qa's gate once the operator fills `Qdrant__ApiKey`).

- [ ] **AC-1:** `dotnet build McpMemoryService.sln` — `0 Warning(s), 0 Error(s)`. No `#pragma warning disable` in `QdrantOptions.cs` / `QdrantService.cs`.
- [ ] **AC-2:** `dotnet test --filter "Category!=Integration"` — **74 passed, 0 failed, 0 skipped** (M11a adds NO unit tests; no-regression against the M10 74/74 baseline).
- [ ] **AC-3:** `QdrantOptions.ApiKey` is a nullable `string?`, `init`-only, default `null`. Method: `grep -n "ApiKey" src/McpMemoryService/Configuration/QdrantOptions.cs` → the property declaration line `public string? ApiKey { get; init; }`.
- [ ] **AC-4:** `QdrantService` ctor constructs `QdrantClient` with `apiKey: _options.ApiKey` named param. Method: `grep -n "new QdrantClient" src/McpMemoryService/Services/QdrantService.cs` → L66 `new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey)` + the `[IMP:M11a]` comment immediately above.
- [ ] **AC-5:** `appsettings.json` Qdrant section contains `"ApiKey": null`. Method: `grep -n "ApiKey" src/McpMemoryService/appsettings.json` → match in the Qdrant section. (Optional: `appsettings.Development.json` MAY receive the same line — @code decides based on whether the dev file overrides the Qdrant section; additive if present.)
- [ ] **AC-6:** `docker-compose.yml` environment block contains `- Qdrant__ApiKey=` (empty default). Method: `grep -n "Qdrant__ApiKey" docker-compose.yml` → match on the new env-var line.
- [ ] **AC-7:** `.test_counter.json` is exactly `{"counter":0}`. Method: `Get-Content .test_counter.json` → `{"counter":0}` (exact-content, no trailing newline/BOM drift).
- [ ] **AC-8 (backward-compat contract preservation):** `QdrantServiceTests.cs` ctor L241 is UNCHANGED — `new QdrantClient("localhost", port: 6334)` (no `apiKey:` named param; defaults to `null` → unauthenticated integration tests stay green). Method: `grep -n "new QdrantClient" tests/McpMemoryService.Tests/Services/QdrantServiceTests.cs` → same line as M5 (no M11a edit).
- [ ] **AC-9 (LLM URL reconciliation, documentation):** `appsettings.json` `LlmSummarizer.BaseUrl` is aligned to the operator's new llama.cpp host (the `docker-compose.yml` `LlmSummarizer__BaseUrl` env-var bridge override is ALREADY at the new host, set before M11a; M11a aligns the local-`dotnet run` default to match). Method: read `LlmSummarizer.BaseUrl` in `appsettings.json` — its value matches the host in `docker-compose.yml` `LlmSummarizer__BaseUrl` (no longer the M2/M6 `<url>` placeholder). If the operator already edited `appsettings.json`, no-op (just verify + note in return).
- [ ] **AC-10 (gitignore secret hygiene):** `.gitignore` excludes `.env` (the compose secret-override pattern for `Qdrant__ApiKey`). Method: `grep -n "\.env" .gitignore` → match (existing OR `@code`-added in §Algorithm step 5). Prevents accidental ApiKey commit.

---

## Notes for @code (M11a)

1. **Single dispatch, no decomposition.** 4-file atomic cohesive set (Options + Service 1-line + appsettings 1-line + docker-compose 1-line) + optional `.gitignore` line + counter reset. The ApiKey must flow Options → Service → config → env TOGETHER — splitting it would yield a non-compiling (Service ctor references a property that doesn't exist) OR non-runnable (Options has the property but env-var bridge missing) intermediate state. Below the >5-methods threshold. **NO `## Decomposition` section.**

2. **Backward-compatibility by construction — CRITICAL invariant.** `ApiKey` defaults to `null`; `QdrantClient` ctor `apiKey:` defaults to `null`. Unauthenticated Qdrant deployments — including the M5 `QdrantServiceIntegrationTests` ctor L241 `new QdrantClient("localhost", port: 6334)` (no `apiKey:` arg) — continue to work UNCHANGED. AC-8 explicitly asserts the test ctor line is unchanged. DO NOT add an `apiKey:` named argument to the test ctor — it's unnecessary (the default `null` is the contract), and adding it would be a no-op edit that muddies the M5 scars. The integration tests run against `docker run -d --rm -p 6333:6333 -p 6334:6334 qdrant/qdrant` — unauthenticated by default. M11a does NOT change that.

3. **Named-parameter `apiKey:` skips `bool https`.** The `QdrantClient` ctor (Qdrant.Client 1.18.1, operator web-verified) signature is `QdrantClient(string host, int port = 6334, bool https = false, string? apiKey = null, TimeSpan grpcTimeout = default, ILoggerFactory? loggerFactory = null)`. The M11a invocation uses `new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey)` — the named `apiKey:` SKIPS the `bool https = false` positional default. DO NOT add a positional `false` between `port:` and `apiKey:` (e.g., `new QdrantClient(host, _options.GrpcPort, false, _options.ApiKey)` would compile but muddies the http/https scheme — the named-parameter is the cleanest form and preserves the existing http scheme exactly). The `apiKey:` named position works because C# supports named arguments AFTER positional arguments, and `apiKey` is the 4th parameter (skipping `https`'s positional slot via the name). NO web_search required — the signature is operator-provided verbatim in the task context.

4. **No new `[IMP:N]` runtime LDD marker.** The `[IMP:M11a]` in the `QdrantService` ctor is an INLINE SOURCE COMMENT (for `@debug`/`@qa` grep traceability), NOT a runtime `ILogger` emission. M11a does NOT touch `LddMarkers.cs` (the M10 single-source-of-truth file) — no new constant is added. There is no new logging branch (the `QdrantClient` ctor with `apiKey:` is a one-line field-init in the `QdrantService` ctor — no `ILogger` call). The `[GREP_SUMMARY]` + `[CHANGES]` MODULE_CONTRACT appendages are the ONLY `QdrantOptions.cs`/`QdrantService.cs` header touches (additive — the M5 entries preserved).

5. **LLM URL value — VERIFY against `docker-compose.yml` before writing `appsettings.json`.** The operator already set `LLM_CPP_URL` (line 13) and `LlmSummarizer__BaseUrl` (line 18) in `docker-compose.yml` to the new llama.cpp host BEFORE M11a. M11a's `appsettings.json` `LlmSummarizer.BaseUrl` edit MUST match that host verbatim (the redacted `[IP]********` in this spec is documentation-only — @code reads the committed `docker-compose.yml` line 18 and writes the SAME host into `appsettings.json`). If the operator has ALREADY edited `appsettings.json` `LlmSummarizer.BaseUrl` (out-of-spec manual edit), leave it as-is and note the verification in the return message. The env-var bridge override wins at container-time, so this `appsettings.json` edit is local-`dotnet run`-consistency (no Docker behavior change). DO NOT touch `LlmSummarizer.TimeoutSeconds` (still 60 — ADR-008 / SPEC §4.4 step 4) — M11a does NOT change the timeout.

6. **Empty-value env var semantics.** `- Qdrant__ApiKey=` in YAML is the empty string `""`, NOT `null`. `.NET Configuration` binds `""` to `string?` as `string.Empty`, NOT `null` (a cosmetic difference). The `QdrantClient` ctor (`apiKey:`) treats both `null` and `""` as "no auth" (internally guards `string.IsNullOrEmpty(apiKey)` — operator-verified). So the empty-default env var is COLLECTIVELY backward-compatible with unauth Qdrant: the operator deploys with `Qdrant__ApiKey=` (empty) against an unauth Qdrant → service connects unauth; the operator fills the real key against an auth-protected Qdrant → service connects with Bearer auth. NO special handling needed in `QdrantService` for the empty-vs-null distinction. **If @code wants exact-null semantics**, replace the env-var empty value with a literal YAML `~` (null) OR omit the line entirely (unset); but the spec REQUIRES the env-var line be present (AC-6) so the operator can SEE the knob — leave it as the empty-string default.

7. **`.test_counter.json` reset — exact format.** The file is a single line `{"counter":0}` with NO trailing newline and NO BOM (matches the existing M5..M11 format). @code OVERWRITES the file with `{"counter":0}` — use the `Write` tool. If the file already contains `{"counter":0}` (last read confirmed it does), this is a no-op; @code notes the verification in the return message. The anti-loop protocol auto-resets on a fresh `@code scope=impl:M11a` dispatch anyway, so this is the explicit confirmation (the orchestrator reads `.test_counter.json` at the next dispatch; the `.slnx` build does NOT touch it).

8. **No `#pragma warning disable`.** Watch for: CS8625 (nullable — the `string?` declaration is nullable-friendly, no assignment to non-nullable); CS8603 (possible null reference return — N/A, `ApiKey` is not returned anywhere as non-nullable); IDE0005 (unused using — none added); CA1056 (URI-like property naming — N/A, `ApiKey` is not a URI). The `init`-only `string?` declaration is the cleanest nullable pattern; no analyzer warning expected. If a CS warning surfaces, investigate the actual cause before any `#pragma` — AC-1 is the zero-warning gate.

9. **No `@debug` round expected.** M11a is a 4-file additive nullable+named-parameter pass-through — the simplest class of code change. The build+unit gate (AC-1+AC-2) should pass on the FIRST `@code` dispatch. If a build warning appears (e.g., CS8625 on the `apiKey:` — unlikely since `string?` is nullable-friendly), it's a one-line fix, NOT a `@debug` round. The anti-loop counter starts at 0 (per AC-7 reset confirmation).

10. **Decomposition decision — SINGLE `@code scope=impl:M11a` dispatch.** The 4-file atomic cohesive set + optional gitignore + counter reset is below the >5-methods decomposition threshold. The change is config-pass-through, NOT business logic. **NO `## Decomposition` section** is appended.

11. **Web search — NOT REQUIRED for M11a.** The `Qdrant.Client` 1.18.1 ctor signature is OPERATOR-PROVIDED verbatim in the task context (`QdrantClient(string host, int port = 6334, bool https = false, string? apiKey = null, TimeSpan grpcTimeout = default, ILoggerFactory? loggerFactory = null)` — operator web-verified before the dispatch). All M11a architectural decisions (nullable `string? ApiKey` with `init`-only default `null`, the `apiKey:` named-parameter skip of `bool https`, the `Qdrant__ApiKey` env-var bridge following the existing `__` convention from M11, the `LlmSummarizer.BaseUrl` appsettings-vs-env-bridge reconciliation, the `.test_counter.json` reset) derive from the M11 `@qa` operator-action-required note + the M5 antecedent (`QdrantOptions.cs` + `QdrantService` ctor) + the M11 `docker-compose.yml` `__` env-var-bridge pattern + .NET configuration-provider local knowledge (JSON `null` binds to `string?`; env-var `""` binds to `string.Empty`; both treated as "no auth" by the SDK). No `web_search` / `fetch_and_extract` calls are made for M11a.

12. **AGENTS.md build commands.** `dotnet build McpMemoryService.sln` (the `dotnet` CLI auto-discovers the `.slnx`) + `dotnet test --filter "Category!=Integration"` (skips the `QdrantServiceIntegrationTests` `Category=Integration` — needs Docker Qdrant). The `QdrantServiceTests` ctor L241 STAYS `new QdrantClient("localhost", port: 6334)` (AC-8) — `apiKey:` defaults to `null`, the integration tests run against unauth `qdrant/qdrant`, unchanged.

13. **profile.md consistency.** Plan prose is technical English (matches the M5..M10 plan style). No Russian summary header required.

---

## M12 — Integration / E2E tests (FINAL milestone, Etap 1)

| Field | Value |
|---|---|
| Current Milestone | **M12 — Integration / E2E tests (FINAL — 5 test files + 1 csproj edit)** |
| Status | PLAN_READY — **DECOMPOSED into 5 atomic units** (see `## Decomposition` below) |
| Previous State | M11a — DONE and committed (ApiKey pass-through coda + LLM URL alignment; backward-compatible; 74/74 unit baseline preserved). |
| Milestone Deps | **M4..M9 (all services + tools realized), M10 (resilience + middleware + LddMarkers), M11 (Dockerfile), M11a (ApiKey).** All DONE. |
| Dispatch Recommendation | **5 sequential dispatches (see decomposition).** Unit 1 MUST complete first. Units 2-5 are independent post-Unit-1. |
| Etap | Etap 1 (final milestone). After M12: @qa runs full integration gate + unit gate. Etap 2 future. |

### ADRs Touched by M12

| ADR | Decision | M12 Action |
|---|---|---|
| **ADR-003** | Compact = hard delete | Tests verify: success → 20 sources gone, 1 summary exists; LLM failure → 0 deleted. |
| **ADR-004** | Non-blocking compact on insufficient data | Test verifies: compact with < batchSize → `status=skipped, reason=insufficient_data`. |
| **ADR-005** | Silent fallback on Qdrant/LLM unavailability | Fallback tests: Qdrant-down → retrieve empty, capture false, stats -1, compact error — no exceptions. |
| **ADR-001** | 6 entry_type types | E2E round-trip mixes all 6 types. Filter tests exercise all types. |
| **ADR-002** | 5 agent_role roles | Filter tests exercise agent_role_filter across all roles. |
| **ADR-006** | .NET 10 target | Testcontainers.Qdrant + xUnit + WebApplicationFactory all net10.0-compatible. |
| **ADR-010** | MCP SDK = ModelContextProtocol 1.4.0 | `CallToolAsync` helper speaks JSON-RPC tools/call pattern from M2 HostSmokeTests. |
| **ADR-011** | ONNX model = 384-dim | Real ONNX model needed; downloaded pre-test via M4 script. |

### PURPOSE (M12)

Create integration/E2E tests verifying the complete memory pipeline — capture → retrieve → stats → compact round-trip with **real** Qdrant (testcontainer), **real** ONNX embeddings, and **mockable** LLM (configurable HttpMessageHandler). Tests cover: full pipeline round-trip, payload preservation, semantic relevance ranking, Qdrant filter correctness, compact transactional safety, and fallback scenarios. COMPLEMENTS the existing 74/74 unit tests.

### Draft Code Graph (M12)

Abbreviated — see the `AppGraph.xml` M12 nodes for full XML graph. Key files:
- `tests/.../Integration/TestFixture.cs` — shared `IAsyncLifetime` fixture (Qdrant testcontainer + `WebApplicationFactory<Program>` + `CallToolAsync`/`ResetAsync` helpers + LLM mock mechanism)
- `tests/.../Integration/MemoryPipelineE2ETests.cs` — 3 tests (full round-trip, payload preservation, semantic relevance)
- `tests/.../Integration/QdrantFilterTests.cs` — 5 tests (agent_role/entry_type/project_id filters, get_stats filter, limit enforcement)
- `tests/.../Integration/CompactTransactionTests.cs` — 5 tests (success hard-delete, LLM timeout/5xx, insufficient_data, summary metadata)
- `tests/.../Integration/FallbackTests.cs` — 5 tests (Qdrant-down retrieve/capture/stats/compact, optional ONNX failure)
- `tests/.../McpMemoryService.Tests.csproj` — add `Testcontainers.Qdrant` package

### Acceptance Criteria (from milestones/M12)

- [ ] **AC-1:** `dotnet build` — OK.
- [ ] **AC-2:** `dotnet test` — all integration tests PASS (requires Docker + ONNX model).
- [ ] **AC-3:** E2E round-trip: capture → retrieve → stats → compact → summary retrievable.
- [ ] **AC-4:** Filters work: agent_role_filter, entry_type_filter, project_id isolation.
- [ ] **AC-5:** Limit enforced (retrieve returns ≤ limit).
- [ ] **AC-6:** Compact success: hard-deletes sources, creates summary with correct metadata.
- [ ] **AC-7:** Compact LLM timeout: 0 sources deleted, all still retrievable.
- [ ] **AC-8:** Compact LLM 5xx: 0 sources deleted.
- [ ] **AC-9:** Compact insufficient data: returns skipped (non-blocking).
- [ ] **AC-10:** Qdrant-down fallback: retrieve→empty, capture→false, stats→-1, compact→error, no exceptions.
- [ ] **AC-11:** No MCP connection breaks on any failure scenario.
- [ ] **AC-12:** Semantic relevance: similar texts rank higher than dissimilar.
- [ ] **AC-13:** TestFixture properly starts/stops Qdrant testcontainer (no port conflicts).
- [ ] **AC-14:** All M12 tests carry `[Trait("Category","Integration")]`.
- [ ] **AC-15:** `dotnet test --filter "Category!=Integration"` → 74/74 unit tests still PASS (no regression).

### Key Design Decisions

1. **TestFixture manages full lifecycle:** The `TestFixture` is the SINGLE shared resource across all M12 tests. It owns the Qdrant testcontainer (started in `InitializeAsync`, stopped in `DisposeAsync`), the `WebApplicationFactory<Program>` (built once, reused), and the `HttpClient`. All test classes use `IClassFixture<TestFixture>`.

2. **LLM is mockable via HttpMessageHandler:** The TestFixture exposes a configurable `HttpMessageHandler` that CompactTransactionTests replace to control `LlmSummarizerService` behavior (canned summary, timeout, 5xx). The WebApplicationFactory overrides the "LlamaCpp" named-client to route through this mock handler.

3. **MCP tools are called via raw JSON-RPC HTTP (not SDK client):** `CallToolAsync` speaks JSON-RPC 2.0 over Streamable-HTTP (POST /mcp, Accept header, SSE extraction). This mirrors the proven HostSmokeTests pattern and avoids coupling tests to the MCP SDK client library.

4. **ResetAsync uses raw QdrantClient (not MCP tool):** Between tests, `ResetAsync` directly uses the Qdrant gRPC client to delete all points for the test `ProjectId`. This is faster than going through the MCP tool (which would require embedding generation).

5. **Fallback tests manipulate the Qdrant container:** The TestFixture exposes the container reference so FallbackTests can stop/start it mid-test to simulate Qdrant unavailability.

### Notes for @code (all M12 units)

1. **`Testcontainers.Qdrant` package version — resolve at dispatch.** The csproj must add the NuGet reference. If the exact `Testcontainers.Qdrant` module doesn't exist, use `Testcontainers` base + `ContainerBuilder` with `qdrant/qdrant` image. The `Testcontainers` lib API: `IAsyncLifetime`-compatible async Start/Stop; connection-string retrieval; port mapping.

2. **`CallToolAsync` helper — mirror HostSmokeTests pattern.** POST `/mcp`, `Accept: application/json, text/event-stream`, JSON-RPC body, SSE extraction via `ExtractDataFromSse`. Copy the SSE helper from HostSmokeTests (duplicate; don't share — test isolation).

3. **`TestFixture.IAsyncLifetime` — async constructor.** The Qdrant container + factory are async resources. Use `InitializeAsync()`, NOT the constructor. xUnit runs `IAsyncLifetime` before the test.

4. **LLM mock mechanism — `Mock<HttpMessageHandler>` + factory override.** The TestFixture builds a `Mock<HttpMessageHandler>` (Moq) in `InitializeAsync`. The WebApplicationFactory's `WithWebHostBuilder` overrides `AddHttpClient("LlamaCpp")` to use the mock. Tests set up `mock.Setup(...).ReturnsAsync(new HttpResponseMessage(...))` before calling compact.

5. **Fallback tests — container StopAsync/StartAsync.** The TestFixture exposes `public Task StopContainerAsync()` / `public Task StartContainerAsync()` (wrappers around the container's lifecycle). The service survives container restarts (QdrantClient gRPC reconnects).

6. **ONNX model prereq.** Integration tests require the real model (`Models/model.onnx` + `tokenizer.json`). Run `Scripts/Download-Model.ps1` before tests. The `WebApplicationFactory<Program>` boots the real host which instantiates `OnnxEmbeddingService`.

7. **All M12 tests carry `[Trait("Category","Integration")]`.** Run with: `dotnet test --filter "Category=Integration"`. Excluded by: `dotnet test --filter "Category!=Integration"` (the 74/74 unit baseline).

8. **74/74 unit baseline MUST stay green.** `dotnet test --filter "Category!=Integration"` → 74 passed, 0 failed. Verify before every `@code` return. M12 adds ZERO new unit tests.

9. **Web search — NOT REQUIRED for M12.** All decisions derive from the M12 spec + existing codebase patterns + AGENTS.md ADRs. Package names are `[UNVERIFIED_VERSION]` — resolved at compile by `@code`.

---

## Decomposition

> M12 is decomposed into **5 atomic units**. Unit 1 MUST complete before Units 2-5. Units 2-5 are independent of each other (they only depend on Unit 1). Each unit produces a single `.cs` file with 3-5 test methods. All carry `[Trait("Category","Integration")]`.

### Unit 1: TestFixture + csproj (shared infrastructure)

**Signature:**
- Edit `tests/McpMemoryService.Tests/McpMemoryService.Tests.csproj` — add `<PackageReference Include="Testcontainers.Qdrant" Version="..."/>`.
- Create `tests/McpMemoryService.Tests/Integration/TestFixture.cs` — `public sealed class TestFixture : IAsyncLifetime`.

**What it builds:**
- `InitializeAsync`: starts Qdrant testcontainer on random port → builds `WebApplicationFactory<Program>` with `Qdrant:Url` overridden → creates `HttpClient` → polls `/health` until 200 → verifies `tools/list` returns 4 tools.
- `DisposeAsync`: stops container → disposes factory.
- `CallToolAsync(string toolName, object arguments)`: JSON-RPC `tools/call` POST → SSE extraction → `JsonElement` result.
- `ResetAsync()`: raw QdrantClient delete of all points for `TestFixture.ProjectId = "e2e-test-project"`.
- Exposes `Mock<HttpMessageHandler> LlmHandler` property (for Unit 4).
- Exposes `StopContainerAsync()`/`StartContainerAsync()` methods (for Unit 5).

**Acceptance Criteria:**
- [ ] `dotnet build` → 0W 0E (Testcontainers resolves; TestFixture compiles).
- [ ] `dotnet test --filter "Category!=Integration"` → 74/74 PASS (no regression, no new unit tests).
- [ ] TestFixture.cs has `[GREP_SUMMARY]` + `[STRUCTURE]` MODULE_CONTRACT header per `csharp-conventions`.
- [ ] No `#pragma warning disable`.

**Dependencies:** None (this is the foundation).

**Task scope:** `@code scope=M12-unit-1:TestFixture+csproj`

---

### Unit 2: MemoryPipelineE2ETests (full round-trip)

**Signature:**
- Create `tests/McpMemoryService.Tests/Integration/MemoryPipelineE2ETests.cs` — `public class MemoryPipelineE2ETests : IClassFixture<TestFixture>`.

**Test methods (3):**
1. `FullRoundTrip_Capture_Retrieve_Stats_Compact()` — 5 entries → retrieve → stats(count=5) → compact(batchSize=5) → stats(count=1) → retrieve(summary).
2. `Capture_PreservesAllPayloadFields()` — capture with tags+metadata+session_id → retrieve → assert all fields round-trip.
3. `Retrieve_SemanticRelevance_RanksCorrectly()` — 3 entries (different topics) → retrieve matching query → assert most relevant ranks highest.

**Acceptance Criteria:**
- [ ] `dotnet test --filter "FullyQualifiedName~MemoryPipelineE2ETests"` → all 3 PASS.
- [ ] FullRoundTrip: compact returns `status=completed`, `source_count=5`, valid `summary_point_id`. Post-compact stats=1. Post-compact retrieve returns summary.
- [ ] Payload: all fields (Content, AgentRole, EntryType, Tags, Metadata sub-fields) preserved.
- [ ] Semantic relevance: best-match entry has highest Score.
- [ ] `[Trait("Category","Integration")]` + MODULE_CONTRACT header.
- [ ] 74/74 unit baseline preserved.

**Dependencies:** Unit 1 (TestFixture + csproj).

**Task scope:** `@code scope=M12-unit-2:MemoryPipelineE2ETests`

---

### Unit 3: QdrantFilterTests (filter correctness)

**Signature:**
- Create `tests/McpMemoryService.Tests/Integration/QdrantFilterTests.cs` — `public class QdrantFilterTests : IClassFixture<TestFixture>`.

**Test methods (5):**
1. `Retrieve_FilterByAgentRole_ReturnsOnlyMatching()` — capture entries with architect/code/debug/qa roles → retrieve with `agent_role_filter=debug` → only debug entries.
2. `Retrieve_FilterByEntryType_ReturnsOnlyMatching()` — capture decision/bug_fix/insight → retrieve with `entry_type_filter=bug_fix` → only bug_fix.
3. `Retrieve_FilterByProjectId_IsolatesProjects()` — capture to projectA and projectB → retrieve projectA → no projectB entries.
4. `GetStats_FilterByEntryType_ReturnsCorrectCount()` — capture 3 bug_fix + 2 decision → get_stats(bug_fix) → count=3.
5. `Retrieve_LimitEnforced()` — capture 10 entries → retrieve(limit=3) → exactly 3 results.

**Acceptance Criteria:**
- [ ] `dotnet test --filter "FullyQualifiedName~QdrantFilterTests"` → all 5 PASS.
- [ ] Agent role filter: only matching roles returned.
- [ ] Entry type filter: only matching types returned.
- [ ] Project isolation: cross-project leakage = zero.
- [ ] Stats filter count: correct per-type count.
- [ ] Limit: result count ≤ limit.
- [ ] `[Trait("Category","Integration")]` + MODULE_CONTRACT header.
- [ ] 74/74 unit baseline preserved.

**Dependencies:** Unit 1.

**Task scope:** `@code scope=M12-unit-3:QdrantFilterTests`

---

### Unit 4: CompactTransactionTests (compact transaction)

**Signature:**
- Create `tests/McpMemoryService.Tests/Integration/CompactTransactionTests.cs` — `public class CompactTransactionTests : IClassFixture<TestFixture>`.

**Test methods (5):**
1. `Compact_Success_HardDeletesSources_CreatesSummary()` — capture 20 entries, mock LLM → "summary text", compact → `status=completed`, `source_count=20`, `summary_point_id` valid. Verify: 20 sources GONE, 1 summary EXISTS (entry_type=summary, agent_role=orchestrator).
2. `Compact_LlmTimeout_PreservesSources()` — mock LLM throws `TaskCanceledException` → `status=error`, `reason=llm_timeout`. Verify: 20 sources STILL retrievable, 0 deleted.
3. `Compact_Llm5xx_PreservesSources()` — mock LLM throws `HttpRequestException` → `status=error`, `reason=llm_5xx`. Verify: 20 sources STILL retrievable, 0 deleted.
4. `Compact_InsufficientData_ReturnsSkipped()` — capture 5 entries, compact(batchSize=20) → `status=skipped`, `reason=insufficient_data`, `available=5`, `required=20`. Verify: 5 sources still present, no summary.
5. `Compact_SummaryHasCorrectMetadata()` — compact success → retrieve summary → assert `entry_type=summary`, `agent_role=orchestrator`, tags=["compact","summary"].

**Acceptance Criteria:**
- [ ] `dotnet test --filter "FullyQualifiedName~CompactTransactionTests"` → all 5 PASS.
- [ ] Success: 20 sources hard-deleted (ADR-003), 1 summary with correct metadata.
- [ ] LLM timeout: 0 deleted, sources retrievable.
- [ ] LLM 5xx: 0 deleted, sources retrievable.
- [ ] Insufficient data: non-blocking skipped, no LLM call, no delete, no summary.
- [ ] `[Trait("Category","Integration")]` + MODULE_CONTRACT header + `// BUG_FIX_CONTEXT` scar for mock LLM strategy.
- [ ] 74/74 unit baseline preserved.

**Dependencies:** Unit 1 (TestFixture LLM mock mechanism required).

**Task scope:** `@code scope=M12-unit-4:CompactTransactionTests`

---

### Unit 5: FallbackTests (fallback scenarios)

**Signature:**
- Create `tests/McpMemoryService.Tests/Integration/FallbackTests.cs` — `public class FallbackTests : IClassFixture<TestFixture>`.

**Test methods (4 required + 1 optional):**
1. `Retrieve_QdrantDown_ReturnsEmptyNoException()` — stop container → retrieve → empty results, no exception.
2. `Capture_QdrantDown_ReturnsFailureNoException()` — stop container → capture → `success=false`, no exception.
3. `GetStats_QdrantDown_ReturnsNegativeOne()` — stop container → get_stats → `count=-1`, no exception.
4. `Compact_QdrantDown_ReturnsErrorNoDelete()` — capture 20, stop container → compact → `status=error`, no exception.
5. `OnnxRuntimeFailure_MiddlewareCatches_NoConnectionBreak()` **(OPTIONAL)** — inject faulty embedding → 500 JSON → connection stays open. If not feasible, document skip with `BUG_FIX_CONTEXT` scar.

**Acceptance Criteria:**
- [ ] `dotnet test --filter "FullyQualifiedName~FallbackTests"` → all tests PASS (test 5 optional).
- [ ] Qdrant-down retrieve: empty results, no exception (ADR-005).
- [ ] Qdrant-down capture: success=false, no exception.
- [ ] Qdrant-down stats: count=-1, no exception.
- [ ] Qdrant-down compact: status=error, no exception.
- [ ] All fallback: HTTP 200 (structured failure DTO, NOT 500/connection-reset).
- [ ] `[Trait("Category","Integration")]` + MODULE_CONTRACT header.
- [ ] 74/74 unit baseline preserved.

**Dependencies:** Unit 1 (TestFixture container stop/start mechanism required).

**Task scope:** `@code scope=M12-unit-5:FallbackTests`

 > **After all 5 units:** `@qa scope=verify:M12` runs `dotnet test --filter "Category=Integration"` (all M12 tests PASS) + `dotnet test --filter "Category!=Integration"` (74/74 PASS). The @qa verdict is the Etap 1 final gate.

---

## M13 — OnnxEmbeddingService: native memory hygiene + ORT tuning

| Field | Value |
|---|---|
| Current Milestone | **M13 — OnnxEmbeddingService: native memory hygiene + ORT tuning (deterministically dispose the `session.Run` output collection + input `NamedOnnxValue` wrappers + `SessionOptions`; expose 3 ORT memory knobs on `OnnxModelOptions` and wire them in the ctor; `IntraOpNumThreads` default 4→2; new `[IMP:M13]` effective-knobs LDD marker; 2 integration stability tests). Addresses the *native-retention* component of the deployed `RSS 674 MB` observation — NOT the model file (M14) and NOT deployment limits (M15).** |
| Status | PLAN_READY (awaiting single `@code scope=impl:M13`) |
| Previous State | M12 — **SUCCESS** (@qa verdict SUCCESS after one @debug round, counter=1: QdrantFilterTests JSON-navigation fix; 74/74 unit gate green; 5 integration test files structurally verified; runtime Docker execution deferred to operator). Etap 1 core (M1..M12) complete. M13 opens the post-Etap-1 memory-remediation arc (M13 code hygiene → M14 quantized model → M15 memory governance). |
| Milestone Deps | **M4 (DONE — `OnnxEmbeddingService` + `IEmbeddingService` + `Download-Model` scripts; the service file under edit was created here), M10 (DONE — `LddMarkers` single-source invariant: every runtime `[IMP:*]` marker MUST be a `LddMarkers` constant; `EmbeddingResiliencePolicy` rethrow semantics — M13 adds NO swallow), M12 (DONE — integration test harness: class-level `[Trait("Category","Integration")]` pattern + the `OnnxEmbeddingServiceTests` repo-root path resolution reused for the stability gate).** |
| Dispatch Recommendation | **Single `@code scope=impl:M13` — NO decomposition.** 1 service file (2 localized edits + 1 tiny private helper + 1 log line) + 1 options file (3 properties + 1 default change) + 1 `LddMarkers` constant + 2 appsettings sections + 2 integration tests + 1 test_guide append. New methods: 1 (`RunInference` private helper — a semantic-preserving extraction of the existing try/catch). Well below the >5-new-methods threshold, and the change is cohesive (the knobs must flow Options → ctor SessionOptions → InferenceSession TOGETHER with their config binding and tests — splitting would yield non-compiling or non-verifiable intermediate states). **NO `## Decomposition` section is appended for M13** — the file's existing `## Decomposition` section belongs to M12 and stays untouched. |
| Etap | Post-Etap-1 maintenance arc: M13 (code hygiene — this milestone) → M14 (quantized model) → M15 (memory governance / RSS gate). |

## ADRs Touched by M13

| ADR | Decision | M13 Action |
|---|---|---|
| **ADR-005** | ONNX fail at startup → Exit 1 (fatal); ONNX fail at runtime → caught downstream (no swallow in `EmbedAsync`) | **Preserved verbatim.** Model/tokenizer load stays in the ctor; failure → `[FATAL]` log + rethrow. `EmbedAsync` still rethrows on inference failure. The dispose edits (`using` / `finally`) must NOT alter any throw path — disposal runs on BOTH success and exception unwind. The `LddMarkers.OnnxFatal` catch keeps its exact scope (the `Run` call ONLY — pooling/L2/logging failures must NOT be re-labeled "Inference failed"; see Step 3). |
| **ADR-011** | 384-dim vectors from paraphrase-multilingual-MiniLM-L12-v2, L2-normalized | **Preserved verbatim.** The knobs (arena / memory-pattern / model-bytes) affect native memory retention, NOT vector math. Tokenization, `MeanPooling`, `L2Normalize` are NOT touched. The 500-call test asserts dim=384 + norm≈1.0 on EVERY call. |
| **ADR-006** | .NET 10 target | No new packages — the knobs are existing `Microsoft.ML.OnnxRuntime` **1.27.0** API surface (pinned in csproj + `packages.lock.json`-confirmed: `Microsoft.ML.OnnxRuntime.Managed 1.27.0`). No csproj change. |

> **M13 invariants (must NOT regress):**
> 1. `Dimension == 384` (ADR-011); output remains L2-normalized (`L2Normalize` untouched).
> 2. Model/tokenizer load stays in the constructor; failure → `[FATAL]` + rethrow (ADR-005 Exit-1 path).
> 3. `EmbedAsync` rethrows on inference failure; the `OnnxFatal` catch stays scoped to the `Run` call ONLY.
> 4. `Dispose()` still disposes `_session` (the `Tokenizer` from `SentencePieceTokenizer.Create` is not `IDisposable` — M4-established; unchanged, no new dispose target).
> 5. All existing `BUG_FIX_CONTEXT` scars preserved VERBATIM (ctor tokenizer scars at `OnnxEmbeddingService.cs` L116-117, M10 marker scars at L143/L196/L245, the `LoadUnigramTokenizerFromHfJson` doc-scar); `LoadUnigramTokenizerFromHfJson` is NOT touched (pinned to Microsoft.ML.Tokenizers 2.0.0 internals).
> 6. Every new runtime `[IMP:*]` emission comes from a `LddMarkers` constant (M10 single-source rule) — `[IMP:M13]` gets a NEW constant (`EmbeddingOrtKnobs`).
> 7. No Dockerfile / docker-compose / model-file change (M14 owns the model file; M15 owns deployment limits).

---

## PURPOSE (M13)

Eliminate native-memory retention in the ONNX embedding hot path and expose ONNX Runtime memory knobs, **without** changing the 384-dim / L2-normalized contract or the ADR-005 fatal-on-load semantics. Evidence (deployed service): (1) `EmbedAsync` assigns `results = _session.Run(inputs)` (`OnnxEmbeddingService.cs:213`) but never disposes it — `IDisposableReadOnlyCollection<DisposableNamedOnnxValue>` holds native `OrtValue` buffers released only at GC finalization → RSS grows under sustained MCP traffic; (2) the `inputs` `NamedOnnxValue` wrappers (`:200-205`) are never disposed after `Run`; (3) the local `SessionOptions` (`:111`) wraps native state and is never disposed after `InferenceSession` construction (`:120`); (4) no ORT memory knobs are exposed — the CPU arena allocator and memory-pattern optimization retain activation memory that ORT does not return to the OS, and `IntraOpNumThreads=4` (default, `OnnxModelOptions.cs:35` + `appsettings.json:18`) multiplies per-thread arena allocations. Deliverables: dispose fixes + session-config entry in `OnnxEmbeddingService.cs`; 3 new `init` knobs on `OnnxModelOptions` (+ `IntraOpNumThreads` default 4→2); knob binding in `appsettings.json` + `appsettings.Development.json` (which DOES override `OnnxModel`); 1 new `LddMarkers` constant (single-source rule — the spec's Algorithm Step 2 mandates it); 2 integration stability tests; `tests/test_guide.md` extension with `[IMP:M13]` markers.

---

## 1. Draft Code Graph (M13)

> M13 is a surgical-edit milestone on M4 artifacts + config. No new files except nothing — every deliverable is an edit of an existing file (the 2 tests are appended to the existing M4 test class). Nodes follow graph-protocol; `_EDIT` nodes carry exact deltas; `[UNVERIFIED_VERSION]` flags the ORT 1.27.0 API names pending the compile probe (see Notes #1).

```xml
<DraftCodeGraph>
  <!-- ========== OnnxModelOptions.cs — 3 new knobs + default change ========== -->
  <src_McpMemoryService_Configuration_OnnxModelOptions_M13_EDIT FILE="src/McpMemoryService/Configuration/OnnxModelOptions.cs" TYPE="OPTIONS_EDIT">
    <keywords>OnnxModelOptions, EnableCpuMemArena, EnableMemoryPattern, UseOrtModelBytesForInitializers, IntraOpNumThreads default 2, bounded RSS, init-only, M13</keywords>
    <annotation>ADD three init-only bool properties (verbatim from M13 spec §Contracts 4) after IntraOpNumThreads, each with the spec's XML summary:
  /// &lt;summary&gt;Enables the ONNX Runtime CPU arena allocator (retains activation memory; default false for bounded RSS).&lt;/summary&gt;
  public bool EnableCpuMemArena { get; init; } = false;
  /// &lt;summary&gt;Enables ONNX Runtime memory-pattern optimization (faster, higher retained memory; default true).&lt;/summary&gt;
  public bool EnableMemoryPattern { get; init; } = true;
  /// &lt;summary&gt;References initializers directly from the model file bytes to avoid duplicating weights (default true).&lt;/summary&gt;
  public bool UseOrtModelBytesForInitializers { get; init; } = true;
CHANGE IntraOpNumThreads default from 4 to 2 AND its XML summary "Defaults to 4." → "Defaults to 2 (M13: halved from 4 to bound per-thread arena allocations; SPEC §5.1 headroom intent preserved)." APPEND the MODULE_CONTRACT header [GREP_SUMMARY] (add EnableCpuMemArena, EnableMemoryPattern, UseOrtModelBytesForInitializers) + a new [CHANGES] line "M13 — added 3 ORT memory knobs + IntraOpNumThreads default 4→2" (additive — M2/M4 entries preserved; the file header currently only carries the M2 [CHANGES]).</annotation>
    <src_McpMemoryService_Configuration_OnnxModelOptions_EnableCpuMemArena_PROPERTY NAME="EnableCpuMemArena" TYPE="PROPERTY" DEFAULT="false" INIT="true" MILESTONE="M13" UNVERIFIED_VERSION="true" />
    <src_McpMemoryService_Configuration_OnnxModelOptions_EnableMemoryPattern_PROPERTY NAME="EnableMemoryPattern" TYPE="PROPERTY" DEFAULT="true" INIT="true" MILESTONE="M13" UNVERIFIED_VERSION="true" />
    <src_McpMemoryService_Configuration_OnnxModelOptions_UseOrtModelBytesForInitializers_PROPERTY NAME="UseOrtModelBytesForInitializers" TYPE="PROPERTY" DEFAULT="true" INIT="true" MILESTONE="M13" UNVERIFIED_VERSION="true" />
    <src_McpMemoryService_Configuration_OnnxModelOptions_IntraOpNumThreads_DEFAULT_CHANGE NAME="IntraOpNumThreads" TYPE="PROPERTY_DEFAULT_CHANGE" FROM="4" TO="2" MILESTONE="M13" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_ctor_M13_EDIT" TYPE="CONSUMED_BY_CTOR" />
      <Link TARGET="src_McpMemoryService_appsettings_json_M13_EDIT" TYPE="BOUND_BY" />
      <Link TARGET="src_McpMemoryService_appsettings_Development_json_M13_EDIT" TYPE="BOUND_BY" />
    </CrossLinks>
  </src_McpMemoryService_Configuration_OnnxModelOptions_M13_EDIT>

  <!-- ========== OnnxEmbeddingService ctor — SessionOptions using + knobs + [IMP:M13] ========== -->
  <src_McpMemoryService_Services_OnnxEmbeddingService_ctor_M13_EDIT FILE="src/McpMemoryService/Services/OnnxEmbeddingService.cs" TYPE="CTOR_EDIT">
    <keywords>ctor, SessionOptions, using, EnableCpuMemArena, EnableMemoryPattern, AddSessionConfigEntry, session.use_ort_model_bytes_for_initializers, InferenceSession copies options, IMP:M13, ADR-005</keywords>
    <annotation>REPLACE the 5-line block at :110-114:
  // Build SessionOptions per SPEC §5.1: CPU provider, InterOp=1, IntraOp=4 (default).
  var sessionOptions = new SessionOptions();
  sessionOptions.AppendExecutionProvider_CPU(0);
  sessionOptions.InterOpNumThreads = 1;
  sessionOptions.IntraOpNumThreads = opts.IntraOpNumThreads;
WITH (M13 spec §Contracts 3):
  // Build SessionOptions per SPEC §5.1 + M13 memory knobs: CPU provider, InterOp=1, IntraOp=2 (default).
  // M13: SessionOptions wraps native state — dispose after InferenceSession copies what it needs (documented-safe pattern).
  using var sessionOptions = new SessionOptions();
  sessionOptions.AppendExecutionProvider_CPU(0);
  sessionOptions.InterOpNumThreads = 1;
  sessionOptions.IntraOpNumThreads = opts.IntraOpNumThreads;
  sessionOptions.EnableCpuMemArena = opts.EnableCpuMemArena;          // [UNVERIFIED_VERSION] — probe, see plan Notes #1
  sessionOptions.EnableMemoryPattern = opts.EnableMemoryPattern;      // [UNVERIFIED_VERSION] — probe, see plan Notes #1
  sessionOptions.AddSessionConfigEntry("session.use_ort_model_bytes_for_initializers",
      opts.UseOrtModelBytesForInitializers ? "1" : "0");              // [UNVERIFIED_VERSION] — probe, see plan Notes #1
AFTER the existing InferenceSession try/catch (:118-126) and BEFORE the tokenizer try/catch, ADD the effective-knobs log:
  _logger.LogInformation("{Marker} arena={Arena}, memoryPattern={Pattern}, intraOpThreads={Threads}, useOrtModelBytes={ModelBytes}",
      LddMarkers.EmbeddingOrtKnobs, opts.EnableCpuMemArena, opts.EnableMemoryPattern, opts.IntraOpNumThreads, opts.UseOrtModelBytesForInitializers);
UPDATE the file MODULE_CONTRACT: [GREP_SUMMARY] append dispose/knob tokens; [LDD] line append "[IMP:M13][OnnxEmbeddingService.ctor][SUCCESS] ORT memory knobs"; [RATIONALE] update the IntraOp default mention; ADD [CHANGES] "M13 — dispose hygiene (SessionOptions/inputs/results) + ORT memory knobs + [IMP:M13] marker" (additive). DO NOT touch: the M4 tokenizer BUG_FIX_CONTEXT scars (:116-117), the two load try/catch blocks' logic (fatal-log + rethrow stays), the M10 marker scars (:143), the path resolution, the [IMP:9] init log (:144). `using var` disposes at ctor-scope exit (incl. exception unwind) — safe because InferenceSession copies the options during construction.</annotation>
    <src_McpMemoryService_Services_OnnxEmbeddingService_ctor_M13_sessionOptions_USING NAME="sessionOptions" TYPE="USING_LOCAL" DISPOSES="native SessionOptions handle" />
    <src_McpMemoryService_Services_OnnxEmbeddingService_ctor_M13_AddSessionConfigEntry_CALL NAME="AddSessionConfigEntry" TYPE="METHOD_CALL" KEY="session.use_ort_model_bytes_for_initializers" VALUE="opts.UseOrtModelBytesForInitializers ? &quot;1&quot; : &quot;0&quot;" />
    <src_McpMemoryService_Services_OnnxEmbeddingService_ctor_M13_IMP_M13_LOG NAME="EmbeddingOrtKnobs log" TYPE="LDD_LOG" IMP="IMP:M13" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Configuration_OnnxModelOptions_M13_EDIT" TYPE="READS_OPTIONS" />
      <Link TARGET="src_McpMemoryService_Logging_LddMarkers_M13_EDIT" TYPE="USES_CONSTANT" />
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_cs" TYPE="EDITS" />
    </CrossLinks>
  </src_McpMemoryService_Services_OnnxEmbeddingService_ctor_M13_EDIT>

  <!-- ========== EmbedAsync — results using + inputs disposal (via RunInference helper) ========== -->
  <src_McpMemoryService_Services_OnnxEmbeddingService_EmbedAsync_M13_EDIT FILE="src/McpMemoryService/Services/OnnxEmbeddingService.cs" TYPE="METHOD_EDIT">
    <keywords>EmbedAsync, using var results, IDisposableReadOnlyCollection, NamedOnnxValue.Dispose, RunInference helper, finally inputs disposal, managed float[] pooled before dispose, IMP:3 preserved, no-swallow preserved</keywords>
    <annotation>REPLACE the block at :207-219:
  var sw = Stopwatch.StartNew();
  IDisposableReadOnlyCollection&lt;DisposableNamedOnnxValue&gt; results;
  try { results = _session.Run(inputs); }
  catch (Exception ex) { _logger.LogCritical(...); throw; }
WITH:
  var sw = Stopwatch.StartNew();
  // M13: outputs disposed deterministically (using); input wrappers disposed in RunInference finally (success AND failure paths).
  using var results = RunInference(inputs);
ADD private helper in #region Private (exact original catch semantics — scoped to Run ONLY):
  /// &lt;summary&gt;Runs inference, disposing the input wrappers on all paths (M13 native-memory hygiene).&lt;/summary&gt;
  private IDisposableReadOnlyCollection&lt;DisposableNamedOnnxValue&gt; RunInference(IReadOnlyList&lt;NamedOnnxValue&gt; inputs)
  {
      try { return _session!.Run(inputs); }
      catch (Exception ex)
      {
          _logger.LogCritical("{Marker} Inference failed: {Error}", LddMarkers.OnnxFatal + "[FATAL]", ex.Message);
          throw;
      }
      finally
      {
          // M13: NamedOnnxValue wrappers hold native backing for some value kinds; disposing a CreateFromTensor
          // wrapper does NOT dispose the managed DenseTensor&lt;long&gt; buffers (arrays remain valid).
          foreach (var v in inputs) { v.Dispose(); }
      }
  }
ALL downstream code (sw.Stop + [IMP:3] log, OutputMetadata extraction, MeanPooling, [IMP:4], L2Normalize, [IMP:5], [IMP:9] produced, return pooled) stays EXACTLY as-is. `pooled` is a managed float[] copied inside MeanPooling BEFORE results disposal at method exit — the returned vector is never backed by native memory. DO NOT: move the catch to wrap pooling/L2 (would re-label non-Run failures as "Inference failed"); change tokenization/pooling/L2; touch the [IMP:1]/[IMP:2] markers or the M10 scars (:196, :245).</annotation>
    <src_McpMemoryService_Services_OnnxEmbeddingService_RunInference_METHOD NAME="RunInference" TYPE="PRIVATE_METHOD" MILESTONE="M13">
      <annotation>ONLY new method of M13. try: return _session.Run(inputs). catch: original OnnxFatal log + rethrow (scope preserved). finally: dispose every input NamedOnnxValue.</annotation>
      <CrossLinks>
        <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_EmbedAsync_M13_EDIT" TYPE="CALLED_BY" />
      </CrossLinks>
    </src_McpMemoryService_Services_OnnxEmbeddingService_RunInference_METHOD>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_cs" TYPE="EDITS" />
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_EmbedAsync_METHOD" TYPE="MODIFIES" />
    </CrossLinks>
  </src_McpMemoryService_Services_OnnxEmbeddingService_EmbedAsync_M13_EDIT>

  <!-- ========== LddMarkers.cs — 1 new constant (single-source rule) ========== -->
  <src_McpMemoryService_Logging_LddMarkers_M13_EDIT FILE="src/McpMemoryService/Logging/LddMarkers.cs" TYPE="CONSTANTS_EDIT">
    <keywords>LddMarkers, EmbeddingOrtKnobs, IMP:M13, single-source rule, M10 invariant, Embedding region</keywords>
    <annotation>ADD to #region Embedding (after EmbeddingProduced):
  /// &lt;summary&gt;Marker for effective ORT memory knobs after session creation (IMP:M13, ctor).&lt;/summary&gt;
  public const string EmbeddingOrtKnobs = "[IMP:M13][OnnxEmbeddingService.ctor][SUCCESS] ORT memory knobs";
APPEND header [CHANGES] line "M13 — added EmbeddingOrtKnobs (IMP:M13 effective-knobs ctor marker)" (additive — M10 entries preserved). RATIONALE: the [IMP:M13] log is a RUNTIME ILogger emission, so the M10 single-source invariant REQUIRES a constant (unlike M11a's inline-only comment, which deliberately added none). This file is a 6th deliverable beyond the spec's file list — mandated by spec Algorithm Step 2 ("use a new LddMarkers constant if the single-source rule requires it" — it does).</annotation>
    <src_McpMemoryService_Logging_LddMarkers_EmbeddingOrtKnobs_CONST NAME="EmbeddingOrtKnobs" TYPE="PUBLIC_CONST" VALUE="[IMP:M13][OnnxEmbeddingService.ctor][SUCCESS] ORT memory knobs" MILESTONE="M13" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_ctor_M13_EDIT" TYPE="CONSUMED_BY" />
    </CrossLinks>
  </src_McpMemoryService_Logging_LddMarkers_M13_EDIT>

  <!-- ========== appsettings.json + appsettings.Development.json — OnnxModel section ========== -->
  <src_McpMemoryService_appsettings_json_M13_EDIT FILE="src/McpMemoryService/appsettings.json" TYPE="JSON_EDIT">
    <annotation>EDIT the OnnxModel section (currently :15-19) to:
  "OnnxModel": {
    "ModelPath": "Models/model.onnx",
    "TokenizerPath": "Models/tokenizer.json",
    "IntraOpNumThreads": 2,
    "EnableCpuMemArena": false,
    "EnableMemoryPattern": true,
    "UseOrtModelBytesForInitializers": true
  },
(IntraOpNumThreads 4→2 + the 3 booleans matching the Options defaults.) STRICTLY LOCAL: do NOT touch Logging/AllowedHosts/Qdrant/LlmSummarizer/Mcp/WazuhLogging/Otlp sections.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Configuration_OnnxModelOptions_M13_EDIT" TYPE="BINDS_SECTION" />
    </CrossLinks>
  </src_McpMemoryService_appsettings_json_M13_EDIT>

  <src_McpMemoryService_appsettings_Development_json_M13_EDIT FILE="src/McpMemoryService/appsettings.Development.json" TYPE="JSON_EDIT">
    <annotation>appsettings.Development.json DOES override OnnxModel (verified: it carries ModelPath/TokenizerPath/IntraOpNumThreads: 4 at :12-16) → MIRROR the same edit: IntraOpNumThreads 4→2 + the 3 booleans. STRICTLY LOCAL: do NOT touch Logging/Qdrant/LlmSummarizer/Mcp sections.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Configuration_OnnxModelOptions_M13_EDIT" TYPE="BINDS_SECTION" />
    </CrossLinks>
  </src_McpMemoryService_appsettings_Development_json_M13_EDIT>

  <!-- ========== OnnxEmbeddingServiceTests.cs — 2 stability tests (Integration) ========== -->
  <tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_M13_EDIT FILE="tests/McpMemoryService.Tests/Services/OnnxEmbeddingServiceTests.cs" TYPE="XUNIT_TEST_EDIT">
    <keywords>stability test, 500 calls, no degradation, WorkingSet64, GC.GetTotalMemory, calibrated threshold, UNVERIFIED_VERSION threshold, Category=Integration inherited, M13</keywords>
    <annotation>APPEND 2 [Fact] tests to the existing M4 class (class-level [Trait("Category","Integration")] is inherited — no new trait needed; the class ctor already resolves the real model + sets IntraOpNumThreads=1 for test speed — keep):
1) EmbedAsync_RepeatedCalls_DoNotDegrade — warm-up 1 call; then N=500 awaits over a small set of non-empty texts (e.g. 5 rotated strings); assert EVERY result: Length==384 AND ComputeL2Norm(result) within 1.0 ± 1e-3 (reuse the existing ComputeL2Norm helper + the M4 precision convention Assert.Equal(1.0, norm, 4)); no exception.
2) EmbedAsync_WarmupMemoryGrowth_IsBounded — after a warm-up loop (e.g. 10 calls to let ORT settle arenas/JIT), sample: managed = GC.GetTotalMemory(false), wsBefore = Process.GetCurrentProcess().WorkingSet64; run the 500-call loop; sample again; ASSERT (wsAfter - wsBefore) &lt; CalibratedWorkingSetGrowthLimitBytes (initial suggested bound: 100 MB = 100 * 1024 * 1024 — [UNVERIFIED_VERSION] threshold, tune after the first instrumented run); LOG both deltas via ITestOutputHelper (managed delta is telemetry — do NOT hard-assert it; GC noise makes it flaky). Do NOT assert any absolute RSS value (that is M15's deployment gate). ADD: private const int CallCount = 500 (+ WarmupCount = 10); `using System.Diagnostics;` for Process (NOT in the default ImplicitUsings set). Update the class MODULE_CONTRACT header ([GREP_SUMMARY] + [CHANGES] additive) + the header remark noting the model prerequisite still applies.
Existing 6 M4 tests UNCHANGED (no regression).</annotation>
    <tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_EmbedAsync_RepeatedCalls_DoNotDegrade_METHOD NAME="EmbedAsync_RepeatedCalls_DoNotDegrade" TYPE="TEST_METHOD" MILESTONE="M13" IMP="IMP:9,IMP:M13" />
    <tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_EmbedAsync_WarmupMemoryGrowth_IsBounded_METHOD NAME="EmbedAsync_WarmupMemoryGrowth_IsBounded" TYPE="TEST_METHOD" MILESTONE="M13" IMP="IMP:9,IMP:M13" UNVERIFIED_VERSION="threshold" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_cs" TYPE="EXERCISES" />
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_EmbedAsync_M13_EDIT" TYPE="VERIFIES" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_M13_EDIT>

  <!-- ========== tests/test_guide.md — M13 section append ========== -->
  <tests_test_guide_md_M13_EDIT FILE="tests/test_guide.md" TYPE="TEST_GUIDE_APPEND">
    <annotation>APPEND a "## M13 — ONNX memory hygiene + ORT tuning" section: purpose (native-retention remediation), the 2 new tests with Key Steps / Acceptance Criteria / LDD Log Markers blocks (house format), integration-run prerequisites (model downloaded via Download-Model.ps1; `dotnet test --filter "Category=Integration"`), AND extend the "## Progress" checklist with:
- [ ] M13: OnnxModelOptions knobs (EnableCpuMemArena=false, EnableMemoryPattern=true, UseOrtModelBytesForInitializers=true) + IntraOpNumThreads default 2
- [ ] M13: OnnxEmbeddingService dispose hygiene (results `using` + inputs `finally` dispose + SessionOptions `using`) + [IMP:M13] effective-knobs marker
- [ ] M13: EmbedAsync_RepeatedCalls_DoNotDegrade (500 calls, dim 384, norm≈1.0)
- [ ] M13: EmbedAsync_WarmupMemoryGrowth_IsBounded (working-set growth &lt; calibrated threshold)
(Check them off when implemented.) DO NOT rewrite the M12 content.</annotation>
    <CrossLinks>
      <Link TARGET="tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_M13_EDIT" TYPE="DOCUMENTS" />
    </CrossLinks>
  </tests_test_guide_md_M13_EDIT>
</DraftCodeGraph>
```

---

## 2. Step-by-Step Data Flow (M13 — single `@code scope=impl:M13`)

> `@code` execution algorithm. Source: M13 spec §Algorithm (Steps 1-6) + §Contracts 1-5 + AGENTS.md ADR-005/011. One `@code` dispatch, no decomposition. Current line anchors: ctor SessionOptions `:110-114`, InferenceSession `:118-126`, inputs `:200-205`, Run `:207-219`, extraction→return `:221-248`, Dispose `:510-513`.

1. **Extend `OnnxModelOptions`** (per §1 node): add the 3 init-only bool knobs verbatim from spec §Contracts 4 (with the spec's XML summaries), change `IntraOpNumThreads` default `4` → `2` (+ summary update), append the MODULE_CONTRACT `[GREP_SUMMARY]` tokens + a new `[CHANGES]` entry (additive — M2 entries preserved).

2. **Wire the knobs in the `OnnxEmbeddingService` ctor**: wrap `SessionOptions` in `using var`; keep `AppendExecutionProvider_CPU(0)` / `InterOpNumThreads = 1` / `IntraOpNumThreads = opts.IntraOpNumThreads`; ADD `EnableCpuMemArena` / `EnableMemoryPattern` property assignments + the `AddSessionConfigEntry("session.use_ort_model_bytes_for_initializers", opts.UseOrtModelBytesForInitializers ? "1" : "0")` call — all BEFORE `new InferenceSession(modelPath, sessionOptions)` (which stays inside its existing try/catch). After the session try/catch, add the `[IMP:M13]` effective-knobs `LogInformation` using `LddMarkers.EmbeddingOrtKnobs`. Add the `EmbeddingOrtKnobs` constant to `LddMarkers.cs` (#region Embedding) FIRST if you build incrementally. **[UNVERIFIED_VERSION] probe** — see Notes #1 for the drop-one-line protocol if any knob fails to compile.

3. **Fix `EmbedAsync` disposal**: extract the `Run` try/catch into the private `RunInference(inputs)` helper (catch: original `LddMarkers.OnnxFatal + "[FATAL]"` log + rethrow, scope Run-ONLY; `finally`: `foreach (var v in inputs) { v.Dispose(); }`); at the original call site use `using var results = RunInference(inputs);`. Everything downstream (sw.Stop + `[IMP:3]`, output extraction, `MeanPooling`, `[IMP:4]`, `L2Normalize`, `[IMP:5]`, `[IMP:9]` produced, `return pooled`) is untouched. `Dispose()` (`:510-513`) needs NO change — `_session?.Dispose()` remains the only dispose target (see invariant #4).

4. **Update appsettings**: apply the §1 JSON edits to `appsettings.json` AND `appsettings.Development.json` (the dev file DOES override `OnnxModel` — verified at `:12-16`). No docker-compose / Dockerfile change.

5. **Tests**: append the 2 stability `[Fact]`s to `OnnxEmbeddingServiceTests` per §1 node (500-call degradation + warm-up working-set growth; threshold 100 MB initial `[UNVERIFIED_VERSION]`, calibrate after first run; NO absolute-RSS assertion). Then append the M13 section + progress entries to `tests/test_guide.md`. Also confirm `.test_counter.json` == `{"counter":0}` (fresh-milestone anti-loop reset).

6. **Return gate** (spec Step 6): `dotnet build McpMemoryService.sln` → **0 Warning(s), 0 Error(s)**; `dotnet test --filter "Category!=Integration"` → **74/74 PASS** (M13 adds ZERO unit tests — both new tests are `Category=Integration` via the class-level trait); the integration gate (`dotnet test --filter "Category=Integration"` — Docker + downloaded model required) is @qa/operator's runtime responsibility — @code runs it best-effort ONLY if the model files exist locally, and reports either way.

---

## 3. Acceptance Criteria (M13)

> Verbatim from `milestones/M13-onnx-memory-hygiene.md` §Acceptance Criteria (11 items), labelled for mechanical `@qa` checking.

- [ ] **AC-1:** `dotnet build McpMemoryService.sln` — OK (0 warnings, 0 errors)
- [ ] **AC-2:** Unit gate (`Category!=Integration`) — all PASS, no regressions
- [ ] **AC-3:** `results` collection is disposed (`using`) in `EmbedAsync`
- [ ] **AC-4:** `inputs` `NamedOnnxValue` wrappers are disposed after `Run`
- [ ] **AC-5:** `SessionOptions` is disposed after `InferenceSession` construction
- [ ] **AC-6:** `OnnxModelOptions` exposes `EnableCpuMemArena` / `EnableMemoryPattern` / `UseOrtModelBytesForInitializers`
- [ ] **AC-7:** `IntraOpNumThreads` default = 2 in `OnnxModelOptions` + `appsettings.json`
- [ ] **AC-8:** 500× `EmbedAsync` produce valid 384-dim L2-normalized vectors
- [ ] **AC-9:** Integration memory-stability test PASS (growth below calibrated threshold)
- [ ] **AC-10:** 384-dim / L2 / fatal-on-load / no-swallow invariants preserved
- [ ] **AC-11:** Logs contain the new `[IMP:M13]` effective-knobs marker

---

## Notes for @code (M13)

1. **[UNVERIFIED_VERSION] probe protocol for the ORT 1.27.0 knob names (CRITICAL).** The property names `SessionOptions.EnableCpuMemArena` / `SessionOptions.EnableMemoryPattern` and the session-config key `session.use_ort_model_bytes_for_initializers` (via `AddSessionConfigEntry`) could NOT be verified this session: `[WEB_SEARCH_UNAVAILABLE]` — two `web_search` calls returned irrelevant results (SearXNG quality failure, scenario S3-equivalent; `[SOURCE: web_search, query="Microsoft.ML.OnnxRuntime C# SessionOptions EnableCpuMemArena EnableMemoryPattern property", ts=2026-09-25T10:30:00Z]` and `[SOURCE: web_search, query="onnxruntime \"session.use_ort_model_bytes_for_initializers\" AddSessionConfigEntry", ts=2026-09-25T10:32:00Z]`), the direct GitHub-source fetch 404'd (`[FETCH_FAILED] URL unreachable`), and the local NuGet cache does not contain the package (`PKG_MISSING: ~/.nuget/packages/microsoft.ml.onnxruntime/1.27.0`) — so no local assembly probe was possible either. They are architect-local-knowledge names (long-standing ORT C# API surface), the pinned version IS confirmed (`Microsoft.ML.OnnxRuntime` 1.27.0 in csproj + `packages.lock.json` → `Microsoft.ML.OnnxRuntime.Managed 1.27.0`). **Protocol:** after `dotnet restore` the package lands in the local cache — the compile step IS the probe. If any single knob fails to compile (CS1061/CS0117-style): **DROP that single line** (property assignment OR the `AddSessionConfigEntry` call — keep the corresponding `OnnxModelOptions` property, it stays bindable/harmless), **do NOT substitute a guessed key/name**, and record the probe result in a `// BUG_FIX_CONTEXT: [PROBE: ...]` comment at the drop site. If ALL three knobs fail, drop all three lines, keep everything else (the dispose fixes alone are the primary RSS win) and flag loudly in the return message for @qa/@architect.

2. **Dispose semantics — why each fix is safe.** (a) `InferenceSession` copies the options it needs during construction; disposing `SessionOptions` afterwards is the documented pattern (spec §Contracts 3). `using var sessionOptions` disposes at ctor-scope exit — including exception unwind through the two load try/catch blocks (the catch blocks run first; `sessionOptions` disposal order relative to `_session?.Dispose()` in the tokenizer-catch is irrelevant — independent native objects). (b) Disposing a `NamedOnnxValue.CreateFromTensor` wrapper does NOT dispose the backing managed `DenseTensor<long>` — the `idsArray`/`maskArray`/`typeIdsArray` buffers stay valid (and `maskArray` is read AFTER disposal by the `[IMP:4]` log — safe, it's managed). (c) `pooled` is a managed `float[]` copied by `MeanPooling` before `results` disposal at method exit — the returned vector is never native-backed.

3. **`RunInference` helper — the catch scope is the contract.** The original try/catch wrapped ONLY the `_session.Run(inputs)` call. The helper preserves that exactly: pooling/L2/logging failures must NEVER be logged as "Inference failed" `[FATAL]`. Do NOT "simplify" by wrapping the whole downstream block in one try/catch — that changes observable LDD semantics (a GREEN-TEST-TRAP: tests would still pass, logs would lie). The `finally` (inputs disposal) runs on both success and throw paths — that is the point.

4. **Preserve the BUG_FIX_CONTEXT scars verbatim.** `OnnxEmbeddingService.cs` carries: the M4 tokenizer scars at `:116-117` (HYPOTHESIS + RESOLVED — the reflection-based `LoadUnigramTokenizerFromHfJson` rebuild), the M10 marker scars at `:143` (ctor-success constant), `:196` (IMP:2 constant), `:245` (IMP:9 produced constant), and the method-level doc-scar on `LoadUnigramTokenizerFromHfJson`. **Do NOT touch `LoadUnigramTokenizerFromHfJson` or its SetInt/SetString/SetBool/SetEnumOrInt helpers** — pinned to Microsoft.ML.Tokenizers 2.0.0 internals. The `[CHANGES]`/`[GREP_SUMMARY]`/`[LDD]` header updates are APPENDS only.

5. **`LddMarkers` single-source rule.** Unlike M11a (inline comment only, no constant), M13's `[IMP:M13]` is a runtime `ILogger` emission → the M10 invariant REQUIRES the `EmbeddingOrtKnobs` constant. Value: `"[IMP:M13][OnnxEmbeddingService.ctor][SUCCESS] ORT memory knobs"` (follows the `[IMP:N][Method][STEP] label` convention). Log the four effective values as structured params (`arena=`, `memoryPattern=`, `intraOpThreads=`, `useOrtModelBytes=`) so AC-11 is grep-able: `docker logs`/test output must contain `[IMP:M13]`.

6. **`IntraOpNumThreads` 4→2 — knobs stay overridable.** The default change halves per-thread arena allocations (SPEC §5.1 headroom intent preserved — 2 intra-op threads + 1 inter-op on the 1-CPU compose limit is comfortable). The value remains overridable via `appsettings*.json` (`OnnxModel:IntraOpNumThreads`) and, if an operator wants it at deploy time, via the `OnnxModel__IntraOpNumThreads` env var (.NET `__` binding — same mechanism as `Qdrant__Url`; do NOT add it to docker-compose.yml in M13, M15 owns deployment knobs). The existing integration test ctor sets `IntraOpNumThreads = 1` explicitly — unaffected by the default change.

7. **Knob defaults rationale (for the [IMP:M13] log reader).** `EnableCpuMemArena=false`: the ORT CPU arena caches allocation memory and does not return it to the OS — the primary retained-RSS suspect for a long-lived singleton; disabling trades a small per-inference allocation cost for bounded RSS. `EnableMemoryPattern=true`: memory-pattern optimization is a significant CPU win with modest retention — kept ON (spec default). `UseOrtModelBytesForInitializers=true`: references initializers from the model bytes/file instead of duplicating weights into the arena — directly reduces retained weight copies.

8. **Test details — stability tests are Integration, threshold is calibrated, NOT absolute.** Both new tests inherit `Category=Integration` from the class-level trait (unit gate count stays 74/74 — AC-2). `EmbedAsync_RepeatedCalls_DoNotDegrade`: rotate a few fixed non-empty texts; assert EVERY vector (Length==384, norm≈1.0 — reuse `ComputeL2Norm`; precision convention from the existing `EmbedAsync_VectorIsL2Normalized`: `Assert.Equal(1.0, norm, 4)`). `EmbedAsync_WarmupMemoryGrowth_IsBounded`: warm up FIRST (10 calls — arenas/JIT/first-inference allocations settle), THEN sample, run 500, sample again; assert only the WorkingSet64 delta < `100 * 1024 * 1024` initially — `[UNVERIFIED_VERSION]` threshold: after the first real run, tune the constant to `observed_growth * ~2` (headroom) and record the calibration in a `BUG_FIX_CONTEXT`/`[CHANGES]` note or test comment; emit both deltas via `ITestOutputHelper` (add ctor `ITestOutputHelper` param if not present). `GC.GetTotalMemory(false)` is telemetry only — managed growth is GC-noisy, do NOT assert it. **NEVER assert absolute RSS here — that is M15's deployment gate.**

9. **Integration gate execution.** The M13 stability tests need the real model (`src/McpMemoryService/Models/model.onnx` + `tokenizer.json`, gitignored). @code: check `Test-Path src/McpMemoryService/Models/model.onnx` — if present, run `dotnet test --filter "FullyQualifiedName~OnnxEmbeddingServiceTests"` (build + this class only, faster than the full integration suite) and report; if absent, report "integration gate deferred to @qa/operator (model not downloaded)" and source-verify instead. The full `Category=Integration` gate (M2 smoke + M4 ONNX + M5 Qdrant + M12 suite + M13) is @qa's runtime responsibility (Docker + model).

10. **No `#pragma warning disable`.** AC-1 is the zero-warning gate. Watch: CS0168/CS0219 (none expected), IDE0005 (`using System.Diagnostics;` in the test file IS used by `Process` — keep), CA-conventions on the new constants (follow the existing `LddMarkers` style), nullable (the helper's `_session!` — `_session` is checked null at `EmbedAsync` entry; the helper is only called from there; use `_session!` or re-check + throw `InvalidOperationException` mirroring the existing null-guard — @code picks, document with a one-line comment).

11. **Do NOT touch:** `LoadUnigramTokenizerFromHfJson` + reflection helpers, `MeanPooling`/`L2Normalize`/`ComputeL2Norm` logic, `IEmbeddingService`, Program.cs DI lines (the OnnxEmbeddingService registration is unchanged), the Dockerfile/docker-compose (M15), `Download-Model` scripts, the M4 test class's existing 6 tests. The model file itself is M14 — M13 does NOT re-download or swap it.

12. **Decomposition decision — SINGLE `@code scope=impl:M13` dispatch, NO `## Decomposition` section.** 1 service file + 1 options file + 1 constants file + 2 config files + 1 test-file append + 1 test_guide append; 1 new method (`RunInference`); 2 new tests. Cohesive: Options-without-ctor-wiring compiles but does nothing; wiring-without-appsettings binds defaults (fine) but hides the knobs from operators; tests-without-the-fix assert nothing meaningful. Below the >5-new-methods threshold. The plan file's existing `## Decomposition` heading belongs to M12 — M13 adds none.

13. **`.test_counter.json`** — confirm/reset to `{"counter":0}` (single line, no trailing newline/BOM) on this fresh `@code scope=impl:M13` dispatch (anti-loop reset, M11a precedent).

14. **AGENTS.md build commands.** `dotnet build McpMemoryService.sln` (CLI auto-discovers the `.slnx`) + `dotnet test --filter "Category!=Integration"` (74/74). Integration: `dotnet test --filter "Category=Integration"` (Docker + model required).

15. **Web search record (for @qa audit).** Two `web_search` calls (queries in Notes #1, ts=2026-09-25T10:30:00Z/10:32:00Z) → irrelevant results (SearXNG quality failure — treated as `[WEB_SEARCH_UNAVAILABLE]` per WEB_SEARCH_PROTOCOL S3); one `fetch_and_extract` (raw.githubusercontent.com SessionOptions.cs) → HTTP 404 (`[FETCH_FAILED]`, S4); local NuGet-cache probe → package absent (`PKG_MISSING`). All design facts derive from the M13 spec (source of truth) + the current source files + ORT C# local knowledge tagged `[UNVERIFIED_VERSION]` where applicable (Notes #1, threshold in Notes #8). Context budget respected: 2 searches + 1 fetch (≤3 each), no verbatim external quotes.

---

## M14 — Model footprint reduction: quantized ONNX variant + factual memory docs

| Field | Value |
|---|---|
| Current Milestone | **M14 — Model footprint reduction: quantized ONNX variant + factual memory docs (swap the FP32 download `onnx/model.onnx` (470,268,510 B = 448.5 MiB, ~2/3 of observed RSS 674 MB) for the dynamically-quantized int8 Xenova variant `onnx/model_quantized.onnx` (118,308,126 B = 112.8 MiB) of the SAME model family — destination filename stays `Models/model.onnx` (stable-path contract); both download scripts gain expected-size assertions + `variant=quantized` print + a Windows PowerShell 5.1 parse fix (pure-ASCII + UTF-8 BOM); 1 new multilingual integration test joins the M4 semantic test as the retrieval-quality gate; docs/compose/ADR registry aligned with measured reality: variant table FP32 448.5 / FP16 224.4 / int8 112.8 MiB + corrected budget ~300 MB RSS). `OnnxEmbeddingService.cs` is NOT modified (spec Contract 4).** |
| Status | PLAN_READY (awaiting single `@code scope=impl:M14`) |
| Previous State | M13 — **SUCCESS** (@qa verdict SUCCESS 11/11 ACs; commit `0937370` + M13-fix `f99cddc`; unit gate 74/74; integration 25/35 with 9 environmental skips; the ONNX stability tests were later RUNTIME-verified with the real model — 500 calls, 384-dim, norm ≈ 1.0; `[IMP:M13]` single-source invariant honored). **Environmental fact:** the solution file is `McpMemoryService.slnx` (NOT `.sln`) — `dotnet build`/`dotnet test` auto-discover it. The memory-remediation arc stands: M13 (code hygiene — DONE) → M14 (this milestone — model file) → M15 (deployment limits / RSS gate). |
| Milestone Deps | **M4 (DONE — `Scripts/Download-Model.ps1` + `Scripts/download-model.sh` origin + the `OnnxEmbeddingServiceTests` semantic-discrimination test reused as the quality gate), M11 (DONE — model-on-TrueNAS mount decision: the model is NOT baked into the image; `/mnt/MainPool/mcp-memory-models` is mounted `:ro` at `/app/Models` per `Dockerfile:29-34` + `docker-compose.yml:21`), M12 (DONE — integration harness conventions), M13 (DONE — disposal/knob fixes; the ~300 MB RSS budget below assumes the M13 knobs `EnableCpuMemArena=false` + `IntraOpNumThreads=2` are in effect; M14 is otherwise independent).** |
| Dispatch Recommendation | **Single `@code scope=impl:M14` — NO decomposition.** 2 script edits + 1 test-file append (1 new test) + 2 docs edits + 1 compose comment + 1 ADR append + 1 test_guide append; **0 new production code, 1 new test method**. The change is cohesive (every file expresses the same variant swap and shares one verification gate — splitting would leave scripts/docs/tests asserting different variants). The model re-download itself is a script/operator action, not code. **NO `## Decomposition` section is appended for M14** (the file's existing `## Decomposition` belongs to M12). |
| Etap | Post-Etap-1 maintenance arc: M13 (code hygiene — DONE) → **M14 (model file — this milestone)** → M15 (memory governance / RSS gate). |

## ADRs Touched by M14

| ADR | Decision | M14 Action |
|---|---|---|
| **ADR-011** | 384-dim vectors from paraphrase-multilingual-MiniLM-L12-v2, L2-normalized | **Amended by ADR-011a (below) — the family, dims, tokenizer, and graph IO are UNCHANGED**; only the exported weight precision variant (FP32 → int8 dynamic quantization) of the same Xenova repo changes. `OnnxEmbeddingService.cs` logic is untouched; the quantized file loads through the same ctor (`InferenceSession` handles int8 initializers transparently — no code path change). |
| **ADR-011a (NEW — recorded by M14)** | Deployed model artifact = `onnx/model_quantized.onnx` (118,308,126 B ≈ 112.8 MiB, dynamically-quantized int8) of Xenova/paraphrase-multilingual-MiniLM-L12-v2; **destination filename stays `Models/model.onnx`** (stable-path: `OnnxModel:ModelPath`, Dockerfile mount, compose volume all unchanged); same `tokenizer.json`, same graph IO (`input_ids`/`attention_mask`/`token_type_ids` → `last_hidden_state`), same 384-dim output; retrieval-quality gate = M4 `EmbedAsync_SimilarTexts_ProduceSimilarVectors` + new M14 `EmbedAsync_MultilingualSimilarity_IsPreserved`; **fallback = `model_fp16.onnx` (235,336,673 B ≈ 224.4 MiB)** if the int8 gate regresses. | **Recorded** — appended to `AGENTS.md` §5 (the project ADR registry) by this milestone (Step 7). |
| **ADR-005** | ONNX fail at startup → Exit 1 (fatal); runtime → caught, no connection break | **Preserved verbatim** — zero service-code changes. The quantized file loads through the identical ctor fatal path (load failure still `[FATAL]` log + rethrow → Exit 1). The scripts' new size assertion fails loudly BEFORE the file ever reaches the service. |
| **ADR-006** | .NET 10 target | No packages, no csproj, no code — only scripts/tests/docs/ADR text. Build gate `dotnet build` auto-discovers `McpMemoryService.slnx`. |

> **M14 invariants (must NOT regress):**
> 1. **`OnnxEmbeddingService.cs` is NOT modified** (spec Contract 4) — zero production `.cs` changes in M14.
> 2. **Destination filename stays `Models/model.onnx`** — `OnnxModel:ModelPath` default, `appsettings.json`/`appsettings.Development.json`, the Dockerfile `/app/Models` mount, the compose volume, and the test ctor path resolution are ALL unchanged. The alternative (rename to `model_quantized.onnx` + `OnnxModel:ModelPath`/`OnnxModel__ModelPath` change) is **explicitly RULED OUT** — it would touch appsettings×2 + compose env + the TrueNAS dataset layout + operator re-mount coordination for zero functional gain; provenance is preserved by the printed variant line + size assertion instead. **Do NOT do both.**
> 3. 384-dim / L2-normalized output, same `tokenizer.json` (URL unchanged), same graph IO names (ADR-011 family contract).
> 4. `docker-compose.yml` memory limit `512M` + reservation `256M` **UNCHANGED** — M14 corrects the explanatory COMMENT only; enforcement/limit change is M15.
> 5. The model is NOT baked into the Docker image (M11 decision preserved — `Dockerfile:29-34` mounts at runtime; the operator's re-download to the TrueNAS dataset is the production path).
> 6. Unit-gate count stays **74/74** — the new test is `Category=Integration` via the class-level trait; M14 adds ZERO unit tests.
> 7. Every factual size claim in the repo (docs/SPEC/compose comment) is replaced with measured HF-metadata values (FP32 470,268,510 B / FP16 235,336,673 B / int8 118,308,126 B) — no rounded folklore.

---

## PURPOSE (M14)

Replace the FP32 ONNX model downloaded by the scripts (470,268,510 B = 448.5 MiB — **verified on disk this session: `src/McpMemoryService/Models/model.onnx` is exactly 470,268,510 B**) with the **dynamically-quantized int8** Xenova variant `onnx/model_quantized.onnx` (118,308,126 B = 112.8 MiB) of the *same* model family, keeping the 384-dim / tokenizer / graph-IO contract intact — the single biggest lever on the deployed `RSS 674 MB` (the FP32 file accounts for ~2/3). Align scripts, config comments, and the memory claims in `docs/docker-deploy.md` + `SPEC.md` §6.2 + `docker-compose.yml` with measured reality (variant table + corrected budget: quantized ~113 MB + runtime/native ~150-200 MB ≈ **~300 MB RSS** steady-state within the unchanged 512M limit). Secondary hardening: both download scripts get expected-size assertions (`118308126 ± 1 MiB`) + a `variant=quantized` print (catches LFS-pointer/wrong-variant/truncated downloads loudly), AND the Windows-script gets a **PowerShell 5.1 parse fix** — the current `Download-Model.ps1` does NOT parse under `powershell.exe` 5.1 (verified this session: `Parser::ParseFile` → 1 error "string is missing the terminator"; cause = UTF-8 no-BOM + the em-dash U+2014 on the summary line misread as CP1251; `pwsh` is NOT installed on this machine). The retrieval-quality gate proves embedding geometry survives int8 quantization: the existing M4 semantic test + one new multilingual RU/EN test; fallback ladder = `model_fp16.onnx` (224.4 MiB) before any escalation.

---

## 1. Draft Code Graph (M14)

> M14 is a scripts + tests + docs milestone — **zero production `.cs` edits**. Nodes follow graph-protocol; `_M14_EDIT` nodes carry exact deltas. All sizes are HF-metadata values embedded in the milestone spec (authoritative per spec) — FP32 corroborated locally to the byte (on-disk file = 470,268,510 B). See Notes #11 for the failed web-verification attempts.

```xml
<DraftCodeGraph>
  <!-- ========== Download-Model.ps1 — quantized URL + size assert + PS 5.1 fix ========== -->
  <src_McpMemoryService_Scripts_Download_Model_ps1_M14_EDIT FILE="src/McpMemoryService/Scripts/Download-Model.ps1" TYPE="SCRIPT_EDIT">
    <keywords>model_quantized.onnx, quantized int8, 118308126, size assertion, 1 MiB tolerance, variant=quantized, UTF-8 BOM, ASCII-only, PowerShell 5.1, UseBasicParsing, stable destination path, ADR-011a</keywords>
    <annotation>FOUR deltas (full-file rewrite preserving structure):
(1) URL SWAP — $onnxUrl = 'https://huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/onnx/model_quantized.onnx' (tokenizerUrl UNCHANGED). $onnxPath STAYS '..\Models\model.onnx' (stable-path contract, invariant #2). Header comments updated: "M14: quantized int8 variant (ADR-011a)".
(2) SIZE ASSERT — add after the existing $onnxSize/$tokenizerSize assignments:
  $ExpectedOnnxSize = 118308126   # onnx/model_quantized.onnx (int8), HF metadata
  $SizeToleranceBytes = 1048576   # 1 MiB
  if ([math]::Abs($onnxSize - $ExpectedOnnxSize) -gt $SizeToleranceBytes) {
      throw ("ONNX model size mismatch: expected {0} +/- {1} bytes (variant=quantized), got {2} bytes - wrong variant, LFS pointer, or truncated download. File: {3}" -f $ExpectedOnnxSize, $SizeToleranceBytes, $onnxSize, $onnxPath)
  }
(3) VARIANT PRINT — final Write-Host becomes: "Download complete - model: $([math]::Round($onnxSize / 1MB, 1)) MB (variant=quantized), tokenizer: $([math]::Round($tokenizerSize / 1KB, 1)) KB" (em-dash REPLACED by ASCII hyphen).
(4) PS 5.1 COMPAT — (a) ALL non-ASCII characters removed (the ONLY offender today is the em-dash U+2014 on the summary line - verified byte-scan: single run 0xE2 0x80 0x0x94); (b) file saved UTF-8 WITH BOM (defensive; currently no-BOM); (c) add -UseBasicParsing to BOTH Invoke-WebRequest calls (PS 5.1 without IE first-run setup fails basic parsing; harmless/ignored on modern PS). File must PARSE under powershell.exe 5.1 (verification command in plan Step 3).</annotation>
    <src_McpMemoryService_Scripts_Download_Model_ps1_M14_URL_CONST NAME="onnxUrl" TYPE="URL_CONST" VALUE=".../resolve/main/onnx/model_quantized.onnx" />
    <src_McpMemoryService_Scripts_Download_Model_ps1_M14_EXPECTED_SIZE_CONST NAME="ExpectedOnnxSize" TYPE="INT_CONST" VALUE="118308126" TOLERANCE="1048576" />
    <src_McpMemoryService_Scripts_Download_Model_ps1_M14_SIZE_ASSERT NAME="size assertion" TYPE="THROW_GUARD" FAILS="loud" />
    <src_McpMemoryService_Scripts_Download_Model_ps1_M14_VARIANT_PRINT NAME="variant print" TYPE="OUTPUT_LINE" VALUE="model: &lt;MiB&gt; MB (variant=quantized)" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Models_model_onnx_FILE" TYPE="DOWNLOADS_TO" VARIANT="int8_quantized" />
      <Link TARGET="src_McpMemoryService_Scripts_download_model_sh_M14_EDIT" TYPE="MIRRORED_BY" />
      <Link TARGET="AGENTS_md_ADR_011a_M14_APPEND" TYPE="IMPLEMENTS_ADR" />
    </CrossLinks>
  </src_McpMemoryService_Scripts_Download_Model_ps1_M14_EDIT>

  <!-- ========== download-model.sh — mirrored quantized URL + size assert ========== -->
  <src_McpMemoryService_Scripts_download_model_sh_M14_EDIT FILE="src/McpMemoryService/Scripts/download-model.sh" TYPE="SCRIPT_EDIT">
    <keywords>model_quantized.onnx, wc -c, EXPECTED_ONNX_SIZE, exit 1, variant=quantized, ASCII-clean, curl -fsSL, ADR-011a</keywords>
    <annotation>MIRROR deltas in bash:
(1) ONNX_URL="https://huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/onnx/model_quantized.onnx" (TOKENIZER_URL unchanged; ONNX_PATH stays model.onnx). Header comment: M14 quantized variant.
(2) EXPECTED_ONNX_SIZE=118308136->NO: 118308126; SIZE_TOLERANCE_BYTES=1048576; after downloads:
  ACTUAL_ONNX_SIZE=$(wc -c &lt; "$ONNX_PATH")
  if [ "$ACTUAL_ONNX_SIZE" -lt $((EXPECTED_ONNX_SIZE - SIZE_TOLERANCE_BYTES)) ] || [ "$ACTUAL_ONNX_SIZE" -gt $((EXPECTED_ONNX_SIZE + SIZE_TOLERANCE_BYTES)) ]; then
      echo "ERROR: ONNX model size mismatch: expected $EXPECTED_ONNX_SIZE +/- $SIZE_TOLERANCE_BYTES bytes (variant=quantized), got $ACTUAL_ONNX_SIZE bytes" >&gt;&amp;2
      exit 1
  fi
(3) Final echo: "Download complete - model: $(du -h "$ONNX_PATH" | cut -f1) (variant=quantized), tokenizer: $(du -h "$TOKENIZER_PATH" | cut -f1)" (em-dash -> ASCII hyphen).
(4) ASCII-CLEAN: replace the em-dash (sole non-ASCII byte run, verified) with '-'. NO BOM (would break the #!/usr/bin/env bash shebang) - .sh MUST stay BOM-less. curl -fsSL already fail-loud (kept). `set -euo pipefail` + `exit 1` = loud failure. LF line endings preferred while editing (cosmetic hardening; not an AC).</annotation>
    <src_McpMemoryService_Scripts_download_model_sh_M14_URL_CONST NAME="ONNX_URL" TYPE="URL_CONST" VALUE=".../resolve/main/onnx/model_quantized.onnx" />
    <src_McpMemoryService_Scripts_download_model_sh_M14_SIZE_ASSERT NAME="size assertion" TYPE="EXIT_GUARD" FAILS="exit 1" />
    <src_McpMemoryService_Scripts_download_model_sh_M14_VARIANT_PRINT NAME="variant print" TYPE="OUTPUT_LINE" VALUE="(variant=quantized)" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Models_model_onnx_FILE" TYPE="DOWNLOADS_TO" VARIANT="int8_quantized" />
      <Link TARGET="src_McpMemoryService_Scripts_Download_Model_ps1_M14_EDIT" TYPE="MIRRORS" />
    </CrossLinks>
  </src_McpMemoryService_Scripts_download_model_sh_M14_EDIT>

  <!-- ========== OnnxEmbeddingServiceTests.cs — M14 multilingual quality gate (append) ========== -->
  <tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_M14_EDIT FILE="tests/McpMemoryService.Tests/Services/OnnxEmbeddingServiceTests.cs" TYPE="XUNIT_TEST_EDIT">
    <keywords>multilingual similarity, RU pair, EN unrelated, ordering assertion, quantized model geometry, variant gate, IMP:M14, Category=Integration inherited, ADR-011a</keywords>
    <annotation>APPEND 1 [Fact] to the existing class (class-level [Trait("Category","Integration")] inherited; ctor/_output/CosineSimilarity reused as-is):
  [Fact] public async Task EmbedAsync_MultilingualSimilarity_IsPreserved()
    v1 = EmbedAsync("ошибка при работе с потоками")   // RU mirror of the M4 anchor "error when working with threads"
    v2 = EmbedAsync("утечка памяти в потоках")        // RU mirror of "memory leak in threads"
    v3 = EmbedAsync("borscht recipe")                 // the EXISTING M4 unrelated EN anchor (consistency)
    simRu = CosineSimilarity(v1, v2); simUnrelated = CosineSimilarity(v1, v3);
    _output.WriteLine("[IMP:M14][EmbedAsync_MultilingualSimilarity_IsPreserved][CHECKPOINT] simRuPair={0:F4}, simRuVsEnUnrelated={1:F4}", simRu, simUnrelated);
    Assert.True(simRu > simUnrelated, $"RU similar pair must outrank RU-vs-EN unrelated: {simRu:F4} vs {simUnrelated:F4}");
ORDERING-ONLY assertion (no absolute floor - the EN floor is already covered by the M4 test's sim12 > 0.5 guard, which stays the primary quantitative gate); telemetry via _output for @qa calibration. The EXISTING EmbedAsync_SimilarTexts_ProduceSimilarVectors is UNTOUCHED and doubles as the second half of the M14 gate (it must stay green with the quantized file - incl. its sim12 > 0.5 floor). Test-side [IMP:M14] markers are literal ITestOutputHelper strings (M13 precedent - the LddMarkers single-source rule covers runtime ILogger emissions, not test output). Header updates (additive): [GREP_SUMMARY] append MultilingualSimilarity tokens; [CHANGES] append "M14 - added EmbedAsync_MultilingualSimilarity_IsPreserved (int8-variant multilingual quality gate, ADR-011a)"; class remark model-size mention refreshed to "~113 MB int8 quantized (M14)". NO changes to the 8 existing tests.</annotation>
    <tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_EmbedAsync_MultilingualSimilarity_IsPreserved_METHOD NAME="EmbedAsync_MultilingualSimilarity_IsPreserved" TYPE="TEST_METHOD" MILESTONE="M14" IMP="IMP:M14" />
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Services_OnnxEmbeddingService_cs" TYPE="EXERCISES" NOTE="service NOT modified by M14" />
      <Link TARGET="src_McpMemoryService_Models_model_onnx_FILE" TYPE="VERIFIES_VARIANT" />
      <Link TARGET="tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_EmbedAsync_SimilarTexts_ProduceSimilarVectors_METHOD" TYPE="GATE_COMPANION" />
    </CrossLinks>
  </tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_M14_EDIT>

  <!-- ========== docs/docker-deploy.md — factual memory budget + variant table + procedure ========== -->
  <docs_docker_deploy_md_M14_EDIT FILE="docs/docker-deploy.md" TYPE="DOC_EDIT">
    <keywords>High memory usage, variant table, FP32 448.5, FP16 224.4, int8 112.8 MiB, ~300MB RSS, Update ONNX Model procedure, TrueNAS dataset, variant=quantized, size check</keywords>
    <annotation>THREE targeted edits:
(1) "High memory usage" section (lines 173-182): replace the 3 stale bullets (~90-120MB model / ~150MB runtime / ~50-100MB buffers) with: intro line "The container uses ~300MB steady-state (within the 512MB limit; measured baseline was RSS 674 MB with the FP32 model - see M13/M14)" + a variant table (| variant | file | size | note |: FP32 model.onnx 448.5 MiB - pre-M14 default, ~2/3 of the observed RSS; FP16 model_fp16.onnx 224.4 MiB - documented fallback; int8 model_quantized.onnx 112.8 MiB - CURRENT, downloaded by the M14 scripts) + corrected budget line "int8 quantized model ~113MB + runtime/native ~150-200MB = ~300MB RSS (M13 knobs + M14 variant)". KEEP the existing remediation bullets (IntraOpNumThreads reduction etc.).
(2) "Update ONNX Model" procedure (lines 115-131): add a variant note ("The script downloads the dynamically-quantized int8 variant (onnx/model_quantized.onnx, ~112.8 MiB, ADR-011a) and saves it as model.onnx; it self-verifies the expected size 118,308,126 B +/- 1 MiB and prints variant=quantized") + fix the stale rebuild step: the model is NOT baked into the image (M11 decision - Dockerfile mounts /app/Models at runtime) -> step 2 becomes "Sync the re-downloaded Models/ to the TrueNAS dataset (/mnt/MainPool/mcp-memory-models)" and the image rebuild step is REMOVED (restart alone picks up the new file: docker-compose down && up -d).
(3) Prerequisites section (~line 6) if it repeats a size claim - align wording with the variant table (no explicit size there today; only touch if needed).</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Scripts_download_model_sh_M14_EDIT" TYPE="DOCUMENTS" />
      <Link TARGET="docker_compose_yml_M14_COMMENT_EDIT" TYPE="CONSISTENT_WITH" />
    </CrossLinks>
  </docs_docker_deploy_md_M14_EDIT>

  <!-- ========== SPEC.md §6.2 — budget arithmetic comment ========== -->
  <SPEC_md_Section_6_2_M14_EDIT FILE="SPEC.md" TYPE="SPEC_COMMENT_EDIT">
    <keywords>6.2, memory: 512M, модель, budget arithmetic, quantized ~113МБ, comment-only</keywords>
    <annotation>COMMENT-ONLY edit at line 206 (inside the §6.2 YAML example): replace "# Строго 512 МБ (модель ~200МБ + runtime ~150МБ + буферы)" with corrected arithmetic reflecting the M14 variant: "# Строго 512 МБ (int8-квантованная модель ~113МБ [ADR-011a] + runtime/native ~150-200МБ + буферы ≈ ~300МБ)". The VALUE stays memory: 512M (limit change is M15 - invariant #4). Russian text (SPEC is Russian). Do NOT touch the rest of §6.2 or §6.1.</annotation>
    <CrossLinks>
      <Link TARGET="docker_compose_yml_M14_COMMENT_EDIT" TYPE="CONSISTENT_WITH" />
      <Link TARGET="AGENTS_md_ADR_011a_M14_APPEND" TYPE="REFERENCES_ADR" />
    </CrossLinks>
  </SPEC_md_Section_6_2_M14_EDIT>

  <!-- ========== docker-compose.yml — comment-only size correction ========== -->
  <docker_compose_yml_M14_COMMENT_EDIT FILE="docker-compose.yml" TYPE="COMPOSE_COMMENT_EDIT">
    <keywords>memory 512M comment, model-size comment, limit change deferred M15, no behavioral change</keywords>
    <annotation>COMMENT-ONLY edit at line 26: replace "# STRICT 512MB (model ~200MB + runtime ~150MB + buffers)" with "# STRICT 512MB (int8 quantized model ~113MB [M14/ADR-011a] + runtime/native ~150-200MB + buffers = ~300MB; limit change deferred to M15)". The VALUE `memory: 512M` and reservation `256M` are UNCHANGED (invariant #4 - M15 owns enforcement). NOTHING else in the file changes.</annotation>
    <CrossLinks>
      <Link TARGET="SPEC_md_Section_6_2_M14_EDIT" TYPE="CONSISTENT_WITH" />
    </CrossLinks>
  </docker_compose_yml_M14_COMMENT_EDIT>

  <!-- ========== AGENTS.md §5 — ADR-011a registry record ========== -->
  <AGENTS_md_ADR_011a_M14_APPEND FILE="AGENTS.md" TYPE="ADR_REGISTRY_APPEND">
    <keywords>ADR-011a, amendment, quantized variant, model_quantized.onnx, 118308126, stable destination filename, fp16 fallback, ADR registry</keywords>
    <annotation>APPEND after ADR-011 (AGENTS.md is the project ADR registry per §5; the M14 spec deliverable "SPEC.md / ADR registry note" resolves to the REGISTRY - AGENTS.md §5 - because every ADR lives there):
### ADR-011a: model artifact = dynamically-quantized int8 variant (amendment to ADR-011)
**Decision:** The deployed ONNX artifact is `onnx/model_quantized.onnx` (118,308,126 B = 112.8 MiB, dynamically-quantized int8) from the same Xenova/paraphrase-multilingual-MiniLM-L12-v2 repo. The destination filename stays `Models/model.onnx` (stable-path: OnnxModel:ModelPath, Dockerfile mount, compose volume unchanged); provenance = script size-assertion (118308126 +/- 1 MiB) + variant print. Same tokenizer.json, same graph IO (input_ids/attention_mask/token_type_ids -> last_hidden_state), same 384-dim L2-normalized output. Retrieval-quality gate: M4 semantic test + M14 multilingual test. Fallback: `model_fp16.onnx` (235,336,673 B = 224.4 MiB).
**Rationale:** FP32 file (470,268,510 B = 448.5 MiB) accounted for ~2/3 of the deployed RSS 674 MB; int8 dynamic quantization preserves embedding geometry at ~1/4 the footprint.
**Source:** milestones/M14-quantized-model.md; recorded M14.
Add "ADR-011a" to the read-order note in §7 @architect entry ONLY if the ADR list is enumerated there (it is not - skip). NO other AGENTS.md change.</annotation>
    <CrossLinks>
      <Link TARGET="src_McpMemoryService_Scripts_Download_Model_ps1_M14_EDIT" TYPE="IMPLEMENTED_BY" />
      <Link TARGET="src_McpMemoryService_Scripts_download_model_sh_M14_EDIT" TYPE="IMPLEMENTED_BY" />
    </CrossLinks>
  </AGENTS_md_ADR_011a_M14_APPEND>

  <!-- ========== tests/test_guide.md — M14 section append ========== -->
  <tests_test_guide_md_M14_EDIT FILE="tests/test_guide.md" TYPE="TEST_GUIDE_APPEND">
    <annotation>APPEND "## M14 - Quantized model variant (int8) + retrieval-quality gate" section: purpose (FP32 448.5 MiB -> int8 112.8 MiB, same family/dims/tokenizer), the quality gate (2 tests: existing M4 EmbedAsync_SimilarTexts_ProduceSimilarVectors re-run + new EmbedAsync_MultilingualSimilarity_IsPreserved) with Key Steps / Acceptance Criteria / LDD Log Markers ([IMP:M14] checkpoint), integration-run prerequisites (re-run the FIXED script under powershell.exe 5.1 - it now parses; expect ~113 MB download, on-disk <= 130 MiB, variant=quantized printed; run class-only filter FullyQualifiedName~OnnxEmbeddingServiceTests), the fp16 fallback ladder, AND extend "## Progress" with 4 unchecked M14 entries (scripts+assertions / multilingual test / docs+compose+ADR-011a / on-disk size <= 130 MiB measured). M13 content untouched.</annotation>
    <CrossLinks>
      <Link TARGET="tests_McpMemoryService_Tests_Services_OnnxEmbeddingServiceTests_M14_EDIT" TYPE="DOCUMENTS" />
    </CrossLinks>
  </tests_test_guide_md_M14_EDIT>
</DraftCodeGraph>
```

---

## 2. Step-by-Step Data Flow (M14 — single `@code scope=impl:M14`)

> `@code` execution algorithm. Source: M14 spec §Algorithm (Steps 1-6) + §Contracts 1-4 + AGENTS.md ADR-011/011a. One dispatch, no decomposition. Environment: Windows, `powershell.exe` 5.1 only (**pwsh NOT installed**), solution = `McpMemoryService.slnx`, on-disk model currently = FP32 470,268,510 B (to be replaced by this milestone).

1. **Edit `Download-Model.ps1`** per §1 node: URL → `model_quantized.onnx`; add `$ExpectedOnnxSize = 118308126` + `$SizeToleranceBytes = 1048576` + post-download `throw` guard; final print gains `(variant=quantized)`; replace the em-dash with `-`; add `-UseBasicParsing`; save **UTF-8 with BOM**; destination `$onnxPath` UNCHANGED (`model.onnx`).

2. **Edit `download-model.sh`** (mirror): `ONNX_URL` → quantized; `EXPECTED_ONNX_SIZE=118308126` / `SIZE_TOLERANCE_BYTES=1048576` + `wc -c` guard → `exit 1`; final echo gains `(variant=quantized)`; em-dash → `-`; **NO BOM** (shebang), keep `set -euo pipefail` + `curl -fsSL`.

3. **Verify script hygiene (must-pass before proceeding):** (a) PS 5.1 parse — `powershell.exe -NoProfile -Command "$t=$null;$e=$null;[System.Management.Automation.Language.Parser]::ParseFile('<AbsolutePathTo>\Download-Model.ps1',[ref]$t,[ref]$e)|Out-Null; if($e.Count){$e|ForEach-Object{$_.Message}; exit 1}else{'PARSE_OK'}"` → expect `PARSE_OK` (the CURRENT file fails this probe with 1 error — verified at design time); (b) byte-scan both scripts → **0 non-ASCII bytes**; (c) .ps1 first 3 bytes = `EF BB BF` (BOM present), .sh first 2 bytes = `#!/` (BOM absent); (d) `bash -n download-model.sh` if bash is available (Git Bash) — best-effort.

4. **Re-download the model locally (replaces the on-disk FP32 file):** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\McpMemoryService\Scripts\Download-Model.ps1` → downloads ~113 MB to `src/McpMemoryService/Models/model.onnx` (overwrites the 448.5 MiB FP32 file; tokenizer re-downloaded unchanged). Then verify: `Get-Item src\McpMemoryService\Models\model.onnx` → **≤ 130 MiB (expect ≈ 112.8 MiB = 118,308,126 B)**; the script itself asserts the exact size (a stale FP32 or LFS-pointer file fails the guard). If the network is unavailable, report and defer the runtime gate to @qa/operator (structural verification only) — the script edit is still complete.

5. **Append the multilingual quality-gate test** to `OnnxEmbeddingServiceTests.cs` per §1 node (1 new `[Fact]`, ordering-only assertion, `[IMP:M14]` checkpoint telemetry, header `[GREP_SUMMARY]`/`[CHANGES]`/remark updates additive). Do NOT touch the 8 existing tests or the service.

6. **Correct documentation:** `docs/docker-deploy.md` (High-memory-usage variant table + ~300 MB budget; Update-ONNX-Model procedure — variant note + size check + TrueNAS-dataset sync replacing the stale image-rebuild step), `SPEC.md:206` §6.2 comment arithmetic, `docker-compose.yml:26` comment (value `512M` unchanged).

7. **Record ADR-011a** in `AGENTS.md` §5 (append after ADR-011, per §1 node text).

8. **test_guide.md + counter:** append the M14 section + 4 Progress entries; confirm `.test_counter.json` == `{"counter":0}` (already verified at design time — fresh-milestone anti-loop reset).

9. **Return gate:** `dotnet build` (auto-discovers `McpMemoryService.slnx`) → 0 warnings / 0 errors; `dotnet test --filter "Category!=Integration"` → **74/74 PASS** (M14 adds zero unit tests); if the quantized model is on disk (Step 4 done) run `dotnet test --filter "FullyQualifiedName~OnnxEmbeddingServiceTests"` → all 9 tests of the class PASS (8 existing + 1 new) — this is the **retrieval-quality gate**. **Fallback ladder ONLY if the gate fails on similarity (not on infra):** switch BOTH script URLs to `model_fp16.onnx`, update both expected-size constants to `235336673`, change the variant prints to `variant=fp16`, re-download (~224 MiB — still ≤ 130 MiB AC is then waived, target becomes ≤ 235 MiB), re-run the gate, and DOCUMENT the switch in the return message + test_guide. If fp16 also fails → stop, report (escalation to @debug, not more guessing).

---

## 3. Acceptance Criteria (M14)

> Verbatim from `milestones/M14-quantized-model.md` §Acceptance Criteria (10 items), labelled for mechanical `@qa` checking.

- [ ] **AC-1:** Download scripts fetch `onnx/model_quantized.onnx` and verify the expected size (`118308126 ± 1 MiB`, fail-loud, `variant=quantized` printed)
- [ ] **AC-2:** On-disk model file ≤ 130 MiB (target ~112.8 MiB) — measured value recorded in `test_guide.md`
- [ ] **AC-3:** `dotnet build` — OK (solution = `McpMemoryService.slnx`; 0 warnings, 0 errors)
- [ ] **AC-4:** Unit gate (`Category!=Integration`) — all PASS (74/74, no regressions)
- [ ] **AC-5:** Integration gate — all PASS, including `EmbedAsync_MultilingualSimilarity_IsPreserved` (and the re-run M4 `EmbedAsync_SimilarTexts_ProduceSimilarVectors` with the quantized file)
- [ ] **AC-6:** `OnnxEmbeddingService` behavior unchanged: 384-dim, L2, fatal-on-load (ZERO edits to `OnnxEmbeddingService.cs`)
- [ ] **AC-7:** `docs/docker-deploy.md` and `SPEC.md` §6.2 contain accurate model sizes / budget (variant table + ~300 MB RSS arithmetic)
- [ ] **AC-8:** `docker-compose.yml` model-size comment corrected (values `512M`/`256M` unchanged — limit change deferred to M15)
- [ ] **AC-9:** ADR-011a recorded in `AGENTS.md` §5 (quantized variant, same dims, fp16 fallback)
- [ ] **AC-10:** Grader note: if int8 quality fails, `model_fp16.onnx` fallback executed and documented (fallback ladder in Step 9)

---

## Notes for @code (M14)

1. **PS 5.1 parse failure — VERIFIED FACT, not hypothesis.** At design time this session: `[System.Management.Automation.Language.Parser]::ParseFile('src/McpMemoryService/Scripts/Download-Model.ps1', ...)` → **1 error** ("the string is missing the terminator"), cause: the file is UTF-8 **without BOM** and contains exactly ONE non-ASCII run — the em-dash U+2014 (bytes `E2 80 94`) on the final summary line — which PS 5.1 misreads as CP1251 (3 garbage chars that break the string literal). `pwsh` is NOT installed on this machine, so the ONLY way the operator can run the script is `powershell.exe` 5.1 — currently impossible. The fix is BOTH: (a) pure-ASCII content (replace `—` with `-`) and (b) save UTF-8 **with BOM** (defense-in-depth: a future non-ASCII char would still be decoded correctly). After editing, re-run the Step 3 parse probe — `PARSE_OK` is a hard prerequisite for claiming AC-1.

2. **Why the destination filename stays `model.onnx` (do NOT rename).** Renaming to `model_quantized.onnx` would cascade: `appsettings.json` + `appsettings.Development.json` `OnnxModel:ModelPath`, a new `OnnxModel__ModelPath` compose env var, the TrueNAS dataset layout + operator re-mount, and the test ctor path — a wide blast radius for zero functional gain. Provenance (which variant the file IS) is preserved by the script's size assertion + `variant=quantized` print + ADR-011a. The spec's Contract 1 makes this the **primary** approach and explicitly forbids doing both.

3. **Size-assertion design rationale.** Expected = `118308126` B exactly (HF metadata, embedded in the spec — authoritative; the FP32 sibling value 470,268,510 was corroborated **to the byte** by the on-disk file this session, so the metadata is trustworthy). Tolerance ±1 MiB absorbs irrelevant drift (none expected — HF files are immutable per revision) while catching every real failure mode: LFS pointer (~1 KB), wrong variant (FP32 = 448.5 MiB, int8 sibling `model_int8.onnx` = 118,054,609 — inside tolerance, acceptable: same quantization family; FP16 = 224.4 MiB), truncated download. Fail LOUDLY (`throw` / `exit 1`) — never leave a broken file in place silently. If HF ever re-exports with a genuinely new size, the guard fails loudly → operator updates one constant per script (documented in the scripts' comments).

4. **`-UseBasicParsing` (PS 5.1 bonus fix).** On PS 5.1 without IE's first-run completed, `Invoke-WebRequest` without `-UseBasicParsing` can throw. Adding it to both calls is harmless on modern PowerShell (parameter is a no-op/deprecated-accepted) and makes the script deterministic on stock Windows. Include it.

5. **Quality gate semantics — what "fail" means.** The gate = the 2 similarity tests with the QUANTIZED file on disk. `EmbedAsync_SimilarTexts_ProduceSimilarVectors` already asserts `sim12 > sim13` AND **`sim12 > 0.5`** — the 0.5 floor is the tightest constraint int8 must survive; do NOT relax or "calibrate" it (that would be a GREEN-TEST-TRAP). The new multilingual test asserts ordering only (`sim(ru1,ru2) > sim(ru1,en-unrelated)`) — absolute cross-lingual floors are model-dependent and would be an uncalibrated risk; the `[IMP:M14]` checkpoint line logs both values so @qa can audit the margin. Infra failures (HTTP, file-missing) are NOT quality failures — fix the environment, do NOT switch variants.

6. **fp16 fallback ladder (AC-10) — exact mechanics.** Only on a genuine similarity-regression failure: swap the URL in BOTH scripts to `.../onnx/model_fp16.onnx`, set both expected-size constants to `235336673`, change prints to `variant=fp16`, re-run Step 4 + Step 9. fp16 budget check: ~224 MiB model + ~150-200 MB runtime ≈ 375-425 MB RSS — still within the unchanged 512M limit, and still a 2× footprint cut vs FP32. Document the switch in the return message, `test_guide.md`, and (if taken) append one line to ADR-011a's Status. The AC-2 `≤ 130 MiB` target then becomes `≤ 235 MiB` (waived-with-documentation).

7. **On-disk FP32 replacement is a REQUIRED local step (Step 4).** `src/McpMemoryService/Models/model.onnx` currently holds the FP32 470,268,510 B file (gitignored — the swap never touches git). The re-download overwrites it in place; the script's own size guard then proves the swap happened (AC-2). **Operator production step (flag in the return message):** re-run the fixed script (or `download-model.sh`) with output synced to the TrueNAS dataset `/mnt/MainPool/mcp-memory-models` (mounted `:ro` at `/app/Models` per `Dockerfile:29-34` + compose) and restart the container — **NO image rebuild** (the model is not baked in). The docker-deploy.md procedure edit (§1 node) documents exactly this.

8. **Files explicitly NOT touched by M14:** `OnnxEmbeddingService.cs` (invariant #1), `IEmbeddingService.cs`, `OnnxModelOptions.cs`, `appsettings.json` + `appsettings.Development.json` (ModelPath unchanged → no edit), `Program.cs`, `Dockerfile`, `docker-compose.yml` values, `LddMarkers.cs` (no new runtime ILogger emission — the [IMP:M14] markers are test-side `ITestOutputHelper` literals per M13 precedent), the 8 existing tests in `OnnxEmbeddingServiceTests.cs`, M13 stability tests, all Tools/Services/Middleware. Also NOT touched: `tokenizer.json` URL in both scripts.

9. **Encoding hygiene for the two scripts (checklist):** .ps1 = ASCII-only + UTF-8 BOM + CRLF (Windows-native, fine for PS); .sh = ASCII-only + **no BOM** (shebang!) + LF preferred. Verify with the Step 3 byte probes, not by eyeball. The `write`/edit tooling writes UTF-8 — confirm the BOM explicitly for the .ps1 (a BOM-less pure-ASCII .ps1 would still parse — the BOM is the second line of defense; AC-1's parse probe is the gate).

10. **Unit-gate count + counter.** M14 adds ZERO unit tests → `Category!=Integration` stays **74/74** (AC-4). `.test_counter.json` = `{"counter":0}` verified at design time; confirm untouched on return.

11. **Web-verification record (for @qa audit).** Best-effort verification of the HF sizes FAILED this session: (a) `fetch_and_extract("https://huggingface.co/api/models/Xenova/paraphrase-multilingual-MiniLM-L12-v2/tree/main/onnx")` → rejected content-type `application/json` (`[FETCH_FAILED]` — tool accepts HTML/text only); (b) `web_search("Xenova paraphrase-multilingual-MiniLM-L12-v2 onnx model_quantized.onnx file size")` → empty result set `[]` (`[WEB_SEARCH_UNAVAILABLE]`, S3-equivalent). Resolution per the dispatch contract: **the spec's embedded sizes are authoritative** (milestone spec Problem Statement table), with strong local corroboration — the on-disk FP32 file measures exactly the spec's 470,268,510 B. The quantized size (118,308,126 B) carries no `[UNVERIFIED]` tag: it is spec-embedded and will be empirically proven by the script's own size guard at Step 4 (a wrong constant = loud failure, not silent drift). Context budget: 1 fetch + 1 search.

12. **Decomposition decision — SINGLE `@code scope=impl:M14` dispatch, NO `## Decomposition` section.** 8 file-touches but 0 production code, 1 new test method, ~15 edited lines per script, comment/doc-level edits elsewhere. Cohesive: scripts, assertion constants, docs numbers, ADR text, and the gate test must all name the SAME variant/size — any split would risk half-the-repo claiming int8 while the other half claims FP32. Below every decomposition threshold (the M13 precedent: similar file-count, single dispatch).

13. **Stray-file observation (NOT M14 scope):** `src/McpMemoryService/Models/` contains three misplaced tracked `.cs` files (`MemoryEntry.cs`, `MemoryPayload.cs`, `Metadata.cs` — duplicates of `Models/`? sizes 1.9/2.8/2.6 KB). Harmless to the build (they may be the actual M3 model files — do NOT move/delete without checking `git ls-files`), but the orchestrator should be aware the Models dir mixes sources with the gitignored binaries. No M14 action.

---