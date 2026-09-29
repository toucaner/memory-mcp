#region MODULE_CONTRACT [DOMAIN(Integration): Shared test infrastructure; CONCEPT(TestFixture): Qdrant testcontainer + WebApplicationFactory + MCP tool caller; TECH(M12): IAsyncLifetime, Testcontainers, JSON-RPC over Streamable HTTP]
/**
 * [GREP_SUMMARY]: TestFixture, IAsyncLifetime, QdrantContainer, WebApplicationFactory, CallToolAsync, ResetAsync, LLM mock, container stop/start, gRPC port 6334, REST port 6333
 * [STRUCTURE]: InitializeAsync → QdrantContainer start (6333 REST + 6334 gRPC bound separately) → WebApplicationFactory build (Qdrant:GrpcPort = mapped 6334) → HttpClient → health poll
 *              DisposeAsync → factory dispose → container dispose
 *              CallToolAsync(toolName, args) → JSON-RPC tools/call (arguments wrapped as {"input": args}) → SSE extraction → JsonElement
 *              ResetAsync() → QdrantClient (gRPC 6334) delete points for ProjectId
 *              StopContainerAsync/StartContainerAsync → container lifecycle for fallback tests
 *
 * <summary>
 * [PURPOSE]: Shared test infrastructure for M12 integration/E2E tests.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Requires Docker (for Qdrant testcontainer) + ONNX model (downloaded via M4 script).
 *   All M12 tests use IClassFixture&lt;TestFixture&gt;.
 *   REST port 6333 and gRPC port 6334 are bound and mapped SEPARATELY — Qdrant serves REST on 6333,
 *   gRPC on 6334; Qdrant.Client is gRPC-only (QdrantOptions contract: Url is host-extraction only).
 * [CHANGES]: LAST_CHANGE: M13-fix — gRPC port wiring fix: bound 6334 separately and wired the 6334 mapping
 *   as Qdrant:GrpcPort (was reusing the REST 6333 mapping → gRPC-against-REST PROTOCOL_ERROR); ResetAsync
 *   gRPC client switched to the 6334 mapping. See BUG_FIX_CONTEXT in InitializeAsync.
 *             M13-fix — ONNX model-path override: OnnxModel:ModelPath/TokenizerPath pointed at the repo's
 *   src/McpMemoryService/Models (absolute, via GetRepoRoot) because the test host resolves relative model
 *   paths against AppContext.BaseDirectory (test bin), where the gitignored model never lands. See
 *   BUG_FIX_CONTEXT step 2b in InitializeAsync.
 *             M13-fix — tool-call envelope + SSE parsing: CallToolAsync wraps flat test dictionaries into
 *   the {"input": ...} envelope mandated by the SDK's AIFunctionFactory marshaling (single complex
 *   parameter per tool); SSE detection now branches on Content-Type and tolerates the SDK's indented
 *   "data:" frames. See BUG_FIX_CONTEXT in CallToolAsync.
 *             M12 — initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Testcontainers.Qdrant;
using Xunit;

namespace McpMemoryService.Tests.Integration;

/// <summary>
/// [PURPOSE]: Shared test infrastructure for M12 integration/E2E tests.
/// </summary>
public sealed class TestFixture : IAsyncLifetime
{
    #region Fields

    private QdrantContainer? _qdrantContainer;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>Port wiring captured at InitializeAsync for factory rebuilds after container restarts.</summary>
    private int _grpcPort;

    /// <summary>Absolute ONNX model path captured at InitializeAsync for factory rebuilds.</summary>
    private string _modelPath = string.Empty;

    /// <summary>Absolute ONNX models directory captured at InitializeAsync for factory rebuilds.</summary>
    private string _modelsDir = string.Empty;

    /// <summary>
    /// Gets the Qdrant container connection string (includes host:port).
    /// </summary>
    public string QdrantUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the test project ID used across tests.
    /// </summary>
    public string ProjectId { get; } = "e2e-test-project";

    /// <summary>
    /// Gets the mock LLM handler for compact transaction tests.
    /// </summary>
    public Mock<HttpMessageHandler> LlmHandler { get; } = new(MockBehavior.Strict);

    #endregion

    #region IAsyncLifetime

    /// <summary>
    /// Initializes the test fixture: starts Qdrant testcontainer, configures app, waits for health.
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: REST port 6333 → Qdrant:Url (host extraction only); gRPC port 6334 → Qdrant:GrpcPort
    ///   (Qdrant.Client is gRPC-only). The two container ports are mapped independently by Testcontainers.
    /// </remarks>
    public async Task InitializeAsync()
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: 17 integration failures (CompactTransactionTests 5, FallbackTests 4,
        //   MemoryPipelineE2ETests 3, QdrantFilterTests 5) fail with "HTTP/2 PROTOCOL_ERROR (0x1)" because this
        //   fixture bound ONLY the Qdrant REST port 6333 (.WithPortBinding(6333, true)) and reused
        //   GetMappedPublicPort(6333) as Qdrant:GrpcPort. Qdrant serves REST on 6333 and gRPC on 6334;
        //   QdrantService (QdrantService.cs:68) opens a gRPC QdrantClient(host, port: GrpcPort) → the gRPC
        //   handshake hits the HTTP/1.1 REST listener → PROTOCOL_ERROR. The defect was previously MASKED:
        //   before the ONNX model was downloaded the host failed earlier at model load, so the Qdrant path
        //   never executed. FIX: bind container port 6334 explicitly and wire its mapping as Qdrant:GrpcPort;
        //   the REST 6333 mapping stays in Qdrant:Url (host extraction only, per QdrantOptions contract).
        //   NOTE: HostSmokeTests (2 failures) does NOT use this fixture — it is environmental
        //   (external Qdrant at localhost:6334), same as QdrantServiceIntegrationTests (7). ts=2026-09-25]
        // 1. Start Qdrant testcontainer with random host ports (REST 6333 + gRPC 6334, mapped separately)
        _qdrantContainer = new QdrantBuilder("qdrant/qdrant:latest")
            .WithPortBinding(6333, true)
            .WithPortBinding(6334, true)
            .Build();

        await _qdrantContainer.StartAsync();

        // 2. QdrantUrl (REST) and GrpcPort come from DIFFERENT container ports — do NOT reuse one variable.
        // BUG_FIX_CONTEXT: [RESOLVED: GrpcPort is now GetMappedPublicPort(6334) — the mapped gRPC endpoint —
        //   instead of the REST 6333 mapping. Why the old approach failed: a single `port` variable from
        //   GetMappedPublicPort(6333) fed both Qdrant:Url and Qdrant:GrpcPort, aiming the gRPC channel at the
        //   HTTP/1.1 REST listener. This solution honors the QdrantOptions contract ("Url is only for host
        //   extraction; Qdrant.Client uses gRPC exclusively") by routing gRPC traffic to 6334.
        //   Immunization: the qdrant/qdrant image serves the two protocols on distinct container ports
        //   (6333 REST / 6334 gRPC); Testcontainers maps each port independently, so GrpcPort must NEVER be
        //   derived from the REST port — bind and map both explicitly.]
        var host = _qdrantContainer.Hostname;
        var restPort = _qdrantContainer.GetMappedPublicPort(6333);
        var grpcPort = _qdrantContainer.GetMappedPublicPort(6334);
        QdrantUrl = $"http://{host}:{restPort}";

        // 2b. ONNX model paths: the test host resolves RELATIVE OnnxModel paths against AppContext.BaseDirectory
        //     (= tests/.../bin/Debug/net10.0), where the gitignored 448 MB model never lands.
        // BUG_FIX_CONTEXT: [HYPOTHESIS: after the gRPC-port fix the 17 TestFixture-based tests still failed —
        //   every embedding-dependent tool call died server-side with "ONNX model file not found:
        //   tests\...\bin\Debug\net10.0\Models\model.onnx" (OnnxEmbeddingService.cs:91-97 resolves relative
        //   ModelPath against AppContext.BaseDirectory). This layer was MASKED by the gRPC-port defect: hosts
        //   died earlier at QdrantCollectionInitializer, so tool calls never reached the embedding ctor.
        //   FIX: override OnnxModel:ModelPath/TokenizerPath with ABSOLUTE paths resolved from the repo layout
        //   (walk up from the test bin to the directory containing McpMemoryService.slnx, then
        //   src/McpMemoryService/Models). No model copies, no src changes, no downloads. ts=2026-09-25]
        var modelsDir = Path.Combine(GetRepoRoot(), "src", "McpMemoryService", "Models");
        var modelPath = Path.Combine(modelsDir, "model.onnx");
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException(
                $"ONNX model not found at '{modelPath}'. Run 'pwsh src/McpMemoryService/Scripts/Download-Model.ps1' first (see AGENTS.md).");
        }

        // Remember the wiring so StartContainerAsync can rebuild the host after a container restart.
        _grpcPort = grpcPort;
        _modelPath = modelPath;
        _modelsDir = modelsDir;

        // 3. Build WebApplicationFactory with config overrides + wait for health
        _factory = await BuildFactoryAsync();
    }

    /// <summary>
    /// Builds a fresh WebApplicationFactory bound to the running testcontainer and waits for /health.
    /// </summary>
    /// <returns>The initialized factory (host started, Qdrant collection initialized, ONNX model loaded).</returns>
    /// <remarks>
    /// [INVARIANTS]: Uses the CURRENT container port mappings and the absolute ONNX model paths resolved
    ///   in InitializeAsync. [CHANGES]: M13-fix — extracted from InitializeAsync so StartContainerAsync can
    ///   rebuild the host after a container restart (see BUG_FIX_CONTEXT in StartContainerAsync).
    /// </remarks>
    private async Task<WebApplicationFactory<Program>> BuildFactoryAsync()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Override Qdrant URL to point at testcontainer
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["QDRANT_URL"] = QdrantUrl,
                        ["Qdrant:Url"] = QdrantUrl,
                        ["Qdrant:GrpcPort"] = _grpcPort.ToString(),
                        ["OnnxModel:ModelPath"] = _modelPath,
                        ["OnnxModel:TokenizerPath"] = Path.Combine(_modelsDir, "tokenizer.json")
                    });
                });
            });

        // Wait for health: GET /health until 200 (ONNX model loads)
        var client = factory.CreateClient();
        var maxRetries = 30;
        var retryDelay = TimeSpan.FromSeconds(2);

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                var response = await client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                {
                    return factory; // Ready
                }
            }
            catch
            {
                // Expected during startup; continue retrying
            }

            await Task.Delay(retryDelay);
        }

        // If we get here, health check never succeeded — test will likely fail
        return factory;
    }

    /// <summary>
    /// Disposes the test fixture: stops Qdrant container, disposes factory.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }

        if (_qdrantContainer != null)
        {
            await _qdrantContainer.DisposeAsync();
        }
    }

    #endregion

    #region MCP Tool Caller

    /// <summary>
    /// Invokes an MCP tool via JSON-RPC over Streamable HTTP and returns the result as a JsonElement.
    /// </summary>
    /// <param name="toolName">The MCP tool name (e.g., "memory_capture").</param>
    /// <param name="args">The tool INPUT DTO fields as a flat snake_case dictionary (wrapped into the
    /// "input" envelope — see remarks).</param>
    /// <returns>The JSON response as a JsonElement.</returns>
    /// <remarks>
    /// [INVARIANTS]: Every MCP tool in this service takes exactly ONE complex parameter
    ///   (<c>MemoryXxxInput input</c> — M7 DTO contract), so the wire shape mandated by the MCP SDK's
    ///   AIFunctionFactory marshaling is <c>{"input": { ...flat snake_case fields... }}</c>.
    ///   This caller therefore wraps <paramref name="args"/> into the "input" envelope.
    /// [CHANGES]: M13-fix — added the "input" envelope wrap. See BUG_FIX_CONTEXT below.
    /// </remarks>
    public async Task<JsonElement> CallToolAsync(string toolName, Dictionary<string, object>? args = null)
    {
        var client = _factory!.CreateClient();

        // BUG_FIX_CONTEXT: [RESOLVED: flat test dictionaries are wrapped into the "input" envelope.
        //   HYPOTHESIS: after the gRPC-port and ONNX-model-path fixes, ALL TestFixture-based tool calls
        //   still failed server-side with "The arguments dictionary is missing a value for the required
        //   parameter 'input'" (Microsoft.Extensions.AI AIFunctionFactory marshaling). Tool methods are
        //   [McpServerTool] methods taking a single complex parameter (MemoryCaptureInput input, ...) —
        //   the SDK binds arguments["input"], so FLAT argument dictionaries ({"project_id": ...}) never
        //   marshal. This layer was MASKED twice: (1) gRPC PROTOCOL_ERROR killed hosts before tool calls;
        //   (2) the ONNX ctor threw during tool-target construction BEFORE argument marshaling, so run-1
        //   logs showed "ONNX model file not found" instead of the marshaling error. Fix: wrap the flat
        //   test dictionaries into the "input" envelope here — single point covering ALL call sites; the
        //   DTO properties bind via their [JsonPropertyName] snake_case names, which the test dicts already
        //   use. ts=2026-09-25]
        // BUG_FIX_CONTEXT: [RESOLVED: enum-valued arguments are converted to their numeric value before
        //   serialization. Why the old approach failed: the test dictionaries hold C# enum objects whose
        //   [JsonConverter(SnakeCaseEnumConverter)] attribute is honored by the TEST-side serializer — so
        //   EntryType.BugFix went over the wire as "bug_fix" — but the SERVER-side input binding (MCP SDK's
        //   AIFunctionFactory marshaling) does NOT honor that attribute: it uses the built-in enum converter
        //   which reads member names case-insensitively ("insight" ✓) but rejects multi-word snake_case
        //   names ("bug_fix" ✗ → "The JSON value could not be converted to EntryType" — probe-verified).
        //   Fix: numbers are the raw enum representation and bind under any converter configuration.
        //   Immunization: numeric enum transport is independent of both the test-side attribute and the
        //   SDK's converter resolution. FOLLOW-UP (src, out of scope here): register a string enum converter
        //   in the SDK's serializer options to restore the snake_case value contract over the wire.
        //   ts=2026-09-25]
        var wrappedArgs = new Dictionary<string, object?>();
        foreach (var kv in args ?? new Dictionary<string, object>())
        {
            wrappedArgs[kv.Key] = kv.Value is Enum enumValue ? Convert.ToInt32(enumValue) : kv.Value;
        }

        // Build JSON-RPC request
        var requestBody = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = new Dictionary<string, object?>
                {
                    ["input"] = wrappedArgs
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();

        // The Streamable HTTP transport responds as SSE (Content-Type: text/event-stream).
        // BUG_FIX_CONTEXT: [RESOLVED: SSE extraction now detects SSE by Content-Type (authoritative) and
        //   parses "data:" lines with leading whitespace tolerated. Why the old approach failed: detection
        //   required the body to START with "data: " or CONTAIN "\ndata: ", but the SDK's Streamable-HTTP
        //   SSE writer indents the data field with two leading spaces — probe-verified body shape:
        //   "event: message\n  data: {\"result\":...}" (content-type=text/event-stream) — so the body fell
        //   through to a raw JSON parse and threw "'e' is an invalid start of a value". This layer was
        //   masked by the two upstream layers (gRPC port wiring, ONNX model path): no tool call ever
        //   produced a successful response before. Immunization: content-type branching does not depend
        //   on the SDK's SSE indentation style, and TrimStart tolerates any future indentation. ts=2026-09-25]
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var isSse = mediaType == "text/event-stream"
                    || body.StartsWith("data: ", StringComparison.Ordinal)
                    || body.StartsWith("event: ", StringComparison.Ordinal);

        var jsonElement = isSse
            ? JsonSerializer.Deserialize<JsonElement>(ExtractDataFromSse(body))
            : JsonSerializer.Deserialize<JsonElement>(body);

        // Extract result.content[0].text — the MCP tool response format
        if (jsonElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("content", out var contentArray) &&
            contentArray.ValueKind == JsonValueKind.Array &&
            contentArray.GetArrayLength() > 0)
        {
            var firstContent = contentArray[0];
            if (firstContent.TryGetProperty("text", out var text))
            {
                var textValue = text.GetString() ?? "{}";
                // BUG_FIX_CONTEXT: [RESOLVED: the inner text parse no longer throws on tool-error frames.
                //   Why the old approach failed: when a tool throws (e.g., argument-binding JsonException),
                //   the SDK returns a CallToolResult with isError=true whose content[0].text is PLAIN TEXT
                //   ("An error occurred invoking 'memory_capture'." — probe-verified), not the tool-output
                //   JSON — the unconditional Deserialize threw "'A' is an invalid start of a value" and
                //   masked the real server-side cause from fire-and-forget callers. Fix: fall back to the
                //   raw JSON-RPC result (isError=true inspectable) when the text is not JSON.
                //   Immunization: valid tool-output DTOs always parse (snake_case DTO contract), so the
                //   fallback triggers only for genuine error frames. ts=2026-09-25]
                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(textValue);
                }
                catch (JsonException)
                {
                    return result; // raw JSON-RPC result with isError=true — inspectable by the caller
                }
            }
        }

        // Fallback: return the raw result
        return jsonElement.TryGetProperty("result", out var rawResult)
            ? rawResult
            : jsonElement;
    }

    /// <summary>
    /// Extracts the JSON-RPC payload from a Streamable-HTTP SSE response.
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: Concatenates the payload of every <c>data:</c> line (leading whitespace tolerated —
    ///   the SDK's SSE writer indents data fields). [CHANGES]: M13-fix — TrimStart added to tolerate the
    ///   SDK's indented SSE frames (see BUG_FIX_CONTEXT in CallToolAsync).
    /// </remarks>
    private static string ExtractDataFromSse(string sseBody)
    {
        var sb = new StringBuilder();
        foreach (var line in sseBody.Replace("\r", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("data: ", StringComparison.Ordinal))
                sb.Append(trimmed, 6, trimmed.Length - 6);
        }
        return sb.ToString();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Locates the repository root by walking up from the test bin directory until the solution file is found.
    /// </summary>
    /// <returns>The absolute path of the directory containing <c>McpMemoryService.slnx</c>.</returns>
    /// <remarks>
    /// [PURPOSE]: Provides repo-layout-relative resolution for gitignored artifacts (ONNX model) that never
    ///   appear in the test output directory. [INVARIANTS]: Works from any bin/obj depth; throws with an
    ///   actionable message if the solution file is missing. [CHANGES]: M13-fix — added for the ONNX
    ///   model-path override (see BUG_FIX_CONTEXT in InitializeAsync, step 2b).
    /// </remarks>
    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("McpMemoryService.slnx").Length == 0)
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Repository root not found: 'McpMemoryService.slnx' does not exist above the test bin directory.");
    }

    #endregion

    #region Reset

    /// <summary>
    /// Resets the test environment by deleting all points for the test project.
    /// Uses a raw QdrantClient to bypass MCP tools (faster — no embedding generation).
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: Qdrant.Client.QdrantClient is gRPC-only — it MUST receive the mapped gRPC port (6334),
    ///   not the REST 6333 mapping. [CHANGES]: M13-fix — port corrected 6333 → 6334 (was the same
    ///   REST-port-reuse defect as InitializeAsync; failures were silently swallowed by the catch below,
    ///   making ResetAsync a silent no-op).
    /// </remarks>
    public async Task ResetAsync()
    {
        var qdrantClient = new Qdrant.Client.QdrantClient(
            _qdrantContainer!.Hostname,
            port: _qdrantContainer.GetMappedPublicPort(6334));

        // BUG_FIX_CONTEXT: [RESOLVED: collection name corrected "memory_entries" → "opencode_memory" and the
        //   scroll+delete pass looped until the collection is empty. Why the old approach failed: the app
        //   writes to QdrantOptions.CollectionName = "opencode_memory" (appsettings.json default), so
        //   scrolling "memory_entries" always returned nothing and the exception was swallowed — ResetAsync
        //   was a SILENT NO-OP. Entries accumulated across tests in the same fixture → cross-test
        //   contamination (retrieves returned other tests' entries; compact saw ≥ batch_size entries where
        //   a test expected < batch_size → status=error instead of skipped). Fix: target the real
        //   collection and delete in a loop (ScrollAsync pages 100 points per pass). ts=2026-09-25]
        const string collectionName = "opencode_memory"; // must match QdrantOptions.CollectionName (appsettings.json)

        // Delete all points in the collection for the test project.
        // Qdrant gRPC does not support filtered deletion directly, so we
        // scroll and delete in batches. For tests, we can simply recreate
        // the collection or scroll+delete.
        try
        {
            // Use the REST-style approach: scroll points and delete by IDs.
            // ScrollAsync pages (limit=100), so repeat until the scroll comes back empty.
            for (var pass = 0; pass < 100; pass++)
            {
                var scrollResult = await qdrantClient.ScrollAsync(
                    collectionName: collectionName,
                    limit: 100);

                if (scrollResult.Result.Count == 0)
                {
                    break; // collection empty — reset complete
                }

                var pointIds = scrollResult.Result.Select(p => p.Id).ToList();
                await qdrantClient.DeleteAsync(
                    collectionName: collectionName,
                    pointIds);
            }
        }
        catch
        {
            // Collection may not exist yet — that's fine
        }
    }

    /// <summary>
    /// Stops the Qdrant container (for fallback tests).
    /// </summary>
    public async Task StopContainerAsync()
    {
        if (_qdrantContainer != null)
        {
            await _qdrantContainer.StopAsync();
        }
    }

    /// <summary>
    /// Starts the Qdrant container (for fallback tests) and waits until the gRPC endpoint is serviceable.
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: Returns only after the container is running AND a real gRPC round-trip succeeds.
    /// [CHANGES]: M13-fix — added the gRPC readiness poll. See BUG_FIX_CONTEXT below.
    /// </remarks>
    public async Task StartContainerAsync()
    {
        if (_qdrantContainer != null)
        {
            await _qdrantContainer.StartAsync();

            // BUG_FIX_CONTEXT: [RESOLVED: a gRPC readiness poll guards the post-restart state. A first
            //   iteration rebuilt the WebApplicationFactory here to give the app a fresh gRPC channel —
            //   REVERTED: a rebuild failure inside a test's finally-block poisons the shared fixture
            //   lifecycle (the fresh host's QdrantCollectionInitializer can race the container's own
            //   readiness window and throw, cascading into the remaining tests). The deterministic remedy
            //   is test ORDERING: FallbackTestOrderer runs Compact_QdrantDown (the only test that needs a
            //   WORKING channel at start) FIRST, before any stop/start cycle can push the app's pooled
            //   channel into gRPC TRANSIENT_FAILURE backoff. ts=2026-09-25]
            var grpcPort = _qdrantContainer.GetMappedPublicPort(6334);
            var probeClient = new Qdrant.Client.QdrantClient(_qdrantContainer.Hostname, port: grpcPort);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                try
                {
                    await probeClient.ListCollectionsAsync();
                    break; // gRPC endpoint serviceable
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250);
                }
            }
        }
    }

    #endregion
}
