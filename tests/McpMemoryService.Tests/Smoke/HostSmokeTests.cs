#region MODULE_CONTRACT [DOMAIN(Smoke): End-to-end host smoke tests; CONCEPT(WebApplicationFactory): In-process test host; TECH(MCP): JSON-RPC handshake]
/**
 * [GREP_SUMMARY]: HostSmokeTests WebApplicationFactory HealthEndpoint McpInitialize tools/list JSON-RPC smoke
 * [STRUCTURE]: WebApplicationFactory<Program> → HttpClient → GET /health → Assert 200
 *                          → POST /mcp (initialize) → Assert serverInfo.name
 *                          → POST /mcp (tools/list) → Assert 2 tools (memory_capture + memory_get_stats)
 *
 * <summary>
 * [PURPOSE]: Smoke tests verifying the host, health endpoint, and MCP handshake.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Tests run in-process. Require Qdrant at localhost:6334 (M5 added QdrantCollectionInitializer as IHostedService).
 * [CHANGES]: LAST_CHANGE: M5 debug — added Category=Integration because QdrantCollectionInitializer (M5) makes host startup depend on Qdrant.
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace McpMemoryService.Tests.Smoke;

/// <summary>
/// [PURPOSE]: Smoke tests verifying the host, health endpoint, and MCP handshake.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Tests run in-process. Require Qdrant at localhost:6334 (M5 added QdrantCollectionInitializer as IHostedService).
/// [CHANGES]: LAST_CHANGE: M5 debug — added [Trait("Category","Integration")] because QdrantCollectionInitializer makes host depend on Qdrant.
/// </remarks>
[Trait("Category", "Integration")]
public class HostSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HostSmokeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// [PURPOSE]: Verifies the /health endpoint returns 200 OK.
    /// </summary>
    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        // [IMP:1][HealthEndpoint_Returns200][INIT] Creating HttpClient from factory
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// [PURPOSE]: Verifies MCP initialize returns serverInfo.name == "McpMemoryService" and tools/list returns 2 tools.
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: POST /mcp carries Accept: application/json, text/event-stream (Streamable HTTP spec)
    /// otherwise the SDK returns 406 NotAcceptable.
    /// [CHANGES]: LAST_CHANGE: M2 fix — added Accept header to both JSON-RPC POSTs (server returned 406
    /// without it). See BUG_FIX_CONTEXT below.
    /// </remarks>
    [Fact]
    public async Task McpInitialize_ReturnsServerInfo()
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: H3 — MCP Streamable HTTP transport requires Accept: application/json,
        //   text/event-stream on every POST; a bare HttpClient POST (no Accept header) is rejected with 406
        //   NotAcceptable after the route fix (app.MapMcp("/mcp")). EVIDENCE: dotnet test log shows
        //   "Expected 200, but found HttpStatusCode.NotAcceptable {value: 406}" at HostSmokeTests.cs:76.
        //   Confirmed: route is now matched (no longer 404) — the 406 is content-negotiation, not routing.
        //   This matches the SDK's StreamableHttpHandler validating the Accept header per the
        //   2025-11-25 Streamable HTTP transport spec. ts=2026-06-30T13:58:00Z]
        // BUG_FIX_CONTEXT: [Why the old approach failed: the test relied on default HttpClient Accept behaviour,
        //   which sends no Accept header. The MCP SDK's Streamable HTTP handler negotiates the response media
        //   type and rejects requests that do not advertise application/json or text/event-stream. Fix: set the
        //   Accept header on both POSTs. This matches test_guide.md note #2's "raw JSON-RPC POST" intent — the
        //   test must still speak the transport protocol correctly, not just the JSON-RPC layer. Alternatives
        //   considered: relaxing server Accept requirements — rejected (the SDK enforces protocol compliance).
        //   Immunization: the Accept header is required independent of SDK version churn, so this stays stable.]
        // [IMP:2][McpInitialize_ReturnsServerInfo][INIT] Creating MCP client
        var client = _factory.CreateClient();

        // --- Step 1: initialize ---
        var initializeBody = """
{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "protocolVersion": "2025-06-18",
        "capabilities": {},
        "clientInfo": { "name": "smoke", "version": "0.0.1" }
    }
}
""";
        using var initializeRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(initializeBody, Encoding.UTF8, "application/json")
        };
        // Streamable HTTP transport requires the client to advertise both response media types.
        initializeRequest.Headers.Accept.ParseAdd("application/json, text/event-stream");

        var initializeResponse = await client.SendAsync(initializeRequest);

        initializeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var initializeContent = await initializeResponse.Content.ReadAsStringAsync();
        // The Streamable HTTP transport responds as an SSE event stream (Content-Type: text/event-stream).
        // The JSON-RPC message lives in the `data:` field(s); extract them before deserialization.
        var initJson = ExtractDataFromSse(initializeContent);
        var initializeResult = System.Text.Json.JsonSerializer.Deserialize<
            System.Text.Json.JsonElement>(initJson);

        // [IMP:2][McpInitialize_ReturnsServerInfo][INIT] Asserting serverInfo.name
        initializeResult.GetProperty("result")
            .GetProperty("serverInfo")
            .GetProperty("name")
            .GetString()
            .Should().Be("McpMemoryService");

        // --- Step 2: tools/list ---
        // [IMP:3][McpInitialize_ReturnsServerInfo][INIT] Sending tools/list request
        var toolsBody = """
{
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/list",
    "params": {}
}
""";

        // Note: per MCP spec, tools/list may require notifications/initialized first.
        // The Stateless SDK accepts tools/list directly in M2 (no tools registered yet).
        using var toolsRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(toolsBody, Encoding.UTF8, "application/json")
        };
        toolsRequest.Headers.Accept.ParseAdd("application/json, text/event-stream");

        var toolsResponse = await client.SendAsync(toolsRequest);

        toolsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var toolsContent = await toolsResponse.Content.ReadAsStringAsync();
        var toolsJson = ExtractDataFromSse(toolsContent);
        var toolsResult = System.Text.Json.JsonSerializer.Deserialize<
            System.Text.Json.JsonElement>(toolsJson);

        // [IMP:M9][McpInitialize_ReturnsServerInfo][SUCCESS] tools/list returns 4 tools (memory_capture, memory_get_stats, memory_retrieve, memory_compact) — was 3 tools (M8, scar preserved)
        var toolsArray = toolsResult.GetProperty("result").GetProperty("tools");
        toolsArray.GetArrayLength().Should().Be(4);

        // Verify tool names
        var toolNames = toolsArray.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        toolNames.Should().Contain("memory_capture");
        toolNames.Should().Contain("memory_get_stats");
        toolNames.Should().Contain("memory_retrieve", "M8 adds memory_retrieve to tools/list");
        toolNames.Should().Contain("memory_compact", "M9 adds memory_compact to tools/list");
    }

    /// <summary>
    /// [PURPOSE]: Extracts the JSON-RPC payload from a Streamable-HTTP SSE response.
    /// The SDK responds with `Content-Type: text/event-stream` and writes the JSON-RPC message
    /// as one or more `data: <json>` lines (terminated by a blank line). This helper
    /// concatenates every `data:` line's payload and returns the assembled JSON text.
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: Returns the raw JSON (never the leading `event:`/`id:`/comment lines).
    /// [RATIONALE]: Required because the Streamable HTTP transport always frames the response
    /// as SSE when the client advertises text/event-stream (which the spec mandates).
    /// [CHANGES]: M2 fix — added to parse the SSE envelope instead of a bare JSON body.
    /// </remarks>
    private static string ExtractDataFromSse(string sseBody)
    {
        var sb = new StringBuilder();
        foreach (var line in sseBody.Replace("\r", "\n").Split('\n'))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                sb.Append(line, 6, line.Length - 6);
        }
        return sb.ToString();
    }
}
