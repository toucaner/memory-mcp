#region MODULE_CONTRACT [DOMAIN(Host): Minimal API entry point; CONCEPT(MCPServer): MCP bootstrap + DI + Health; TECH(MCP): Stateless HTTP]
/**
 * [GREP_SUMMARY]: Program.cs WebApplication CreateBuilder ConfigureServices ConfigurePipeline MapMcp HealthChecks DI registration
 * [STRUCTURE]: CreateBuilder → ConfigureServices(options+MCP+HttpClient+HealthChecks) → Build → ConfigurePipeline(health+Mcp) → RunAsync
 *
 * <summary>
 * [PURPOSE]: Entry point — wires dependency injection, registers the MCP server, and exposes /health.
 * </summary>
 * <remarks>
* [INVARIANTS]: All four Options sections are bound before the server starts.
     * [RATIONALE]: Stateless MCP transport avoids connection state — the pipeline is stateless (ADR-005).
     * [CHANGES]: LAST_CHANGE: M2 fix — explicit pattern "/mcp" passed to MapMcp (parameterless MapMcp() defaults to root "/", not "/mcp"). See BUG_FIX_CONTEXT in ConfigurePipeline.
     * [CHANGES]: M2 skeleton creation (AC-1..AC-6).
     * </remarks>
 */
#endregion MODULE_CONTRACT

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using McpMemoryService.Configuration;
using McpMemoryService.Middleware;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using Serilog;
using Serilog.Sinks.Syslog;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;
using McpMemoryService;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry: metrics, traces, logs → OTLP → otel-collector:4317; metrics → Prometheus /metrics
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "mcp-memory", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
        .AddPrometheusExporter()
        .AddOtlpExporter(o => o.Endpoint = new Uri(builder.Configuration["Otlp:Endpoint"] ?? "http://otel-collector:4317")))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(builder.Configuration["Otlp:Endpoint"] ?? "http://otel-collector:4317")));

builder.Logging.AddOpenTelemetry(o => o
    .AddOtlpExporter(o => o.Endpoint = new Uri(builder.Configuration["Otlp:Endpoint"] ?? "http://otel-collector:4317")));

// Wazuh syslog integration (UDP 514): forward Warning+ logs to the Wazuh manager.
// Host/port/appName are configurable via the WazuhLogging section (appsettings.json / env vars).
var wazuhHost = builder.Configuration["WazuhLogging:Host"] ?? "<ip>";
var wazuhPort = int.TryParse(builder.Configuration["WazuhLogging:Port"], out var p) ? p : 514;
var wazuhApp = builder.Configuration["WazuhLogging:AppName"] ?? "mcp-memory";
var wazuhEnabled = !bool.TryParse(builder.Configuration["WazuhLogging:Enabled"], out var e) || e;

if (wazuhEnabled)
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .WriteTo.Console()
        .WriteTo.UdpSyslog(wazuhHost, wazuhPort, wazuhApp, SyslogFormat.RFC5424)
        .CreateLogger();
    builder.Host.UseSerilog();
    WazuhAudit.Configure(wazuhHost, wazuhPort, wazuhApp);
}

ConfigureServices(builder.Services, builder.Configuration);
var app = builder.Build();
ConfigurePipeline(app);
await app.RunAsync();
Log.CloseAndFlush();

#region ServiceRegistration
/// <summary>
/// [PURPOSE]: Registers all services (Options, MCP server, HttpClient, HealthChecks).
/// </summary>
void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    // [IMP:4][ConfigureServices][INIT] Binding Options from appsettings sections
    services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"));
    services.Configure<OnnxModelOptions>(configuration.GetSection("OnnxModel"));
    services.Configure<LlmSummarizerOptions>(configuration.GetSection("LlmSummarizer"));
    services.Configure<McpOptions>(configuration.GetSection("Mcp"));

    // [IMP:M6][ConfigureServices][OPTION] Register LlamaCpp HttpClient (named-client, options-driven BaseAddress + 60s timeout from LlmSummarizerOptions — ADR-005 propagates TaskCanceledException to M9 caller; ADR-008)
    services.AddHttpClient("LlamaCpp", (sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<LlmSummarizerOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    });

    // [IMP:4][ConfigureServices][OPTION] Register MCP server identity bridge:
    // see the AddMcpServer block below for the withHttpTransport + WithListToolsHandler wiring.

    // BUG_FIX_CONTEXT: [HYPOTHESIS: H — the SDK's McpServerOptions.ServerInfo defaults to the host
    //   process assembly name/version (here "testhost"/"15.0.0.0") and is NOT populated from the
    //   project's McpOptions (Mcp:ServerName/ServerVersion is a separate, custom options class the
    //   SDK does not bind). EVIDENCE: SSE probe of initialize returned
    //   serverInfo.name = "testhost" (expected "McpMemoryService"). SDK reflection
    //   (DebugProbeTests.Probe_Reflect_McpServerOptions): McpServerOptions.ServerInfo is of type
    //   ModelContextProtocol.Protocol.Implementation with writable Name/Version. ts=2026-06-30T14:02:00Z]
    // BUG_FIX_CONTEXT: [Why the old approach failed: AddMcpServer() with no ServerInfo configuration
    //   leaves the SDK to derive identity from the entry assembly. The custom McpOptions, although
    //   bound from the "Mcp" appsettings section, fed nothing into the SDK's handshake. Fix: post-configure
    //   McpServerOptions.ServerInfo from McpOptions via a deferred IOptions dependency so the handshake
    //   advertises the configured name/version. Alternatives: (a) hardcode "McpMemoryService" in the
    //   AddMcpServer callback — rejected: bypasses appsettings binding and AC-6; (b) reshape appsettings
    //   to the SDK schema (Mcp:ServerInfo:Name) — rejected: breaks the McpOptions ServerName convention
    //   already mandated by M2 AC-6 and AppGraph. Immunization: the bridge depends only on McpOptions
    //   (stable), so SDK identity drift cannot recur as long as the bridge stays.]
    services.AddOptions<McpServerOptions>()
        .Configure<IOptions<McpOptions>>((serverOpts, mcpOpts) =>
        {
            serverOpts.ServerInfo = new Implementation
            {
                Name = mcpOpts.Value.ServerName,
                Version = mcpOpts.Value.ServerVersion
            };

            // BUG_FIX_CONTEXT: [HYPOTHESIS: H-SnakeCase — the M7 transport contract (SPEC §4.2/§4.3 +
            //   DevelopmentPlan §M7 M7_EDIT step 2 + AppGraph node McpServerOptions_SerializerOptions_UNVERIFIED_M7)
            //   mandates snake_case property keys on the tool DTOs (content, project_id, agent_role, entry_type,
            //   tags, session_id, metadata, count, point_id, success, error). The original @code M7 dispatch OMITTED
            //   the deliverable entirely (Program.cs L74-82 set only ServerInfo; grep 'Snake|Serializer|Json' on Program.cs
            //   returned zero matches; no [JsonPropertyName] fallback on the capture/stats DTOs). @qa AC-4 + AC-8 BLOCK
            //   on this omission (mem-027). The plan's PRIMARY path was to set McpServerOptions.SerializerOptions here
            //   (single-source-of-truth, mem-024 lesson). That property was [UNVERIFIED_VERSION] against
            //   ModelContextProtocol 1.4.0; @debug was mandated to verify at compile + walk the fallback ladder.
            //   [WEB_SEARCH_UNAVAILABLE] SearXNG service unavailable ("Сервис поиска временно недоступен") on 2026-07-01
            //   — per WEB_SEARCH_PROTOCOL S3, the manual compile probe is the authoritative source of truth. ts=2026-07-01T16:00:00Z]
            //   [SOURCE: manual compile probe] Probe results against ModelContextProtocol 1.4.0 net10.0:
            //     - PRIMARY rung: `serverOpts.SerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };`
            //       -> CS1061 ("McpServerOptions не содержит определения SerializerOptions"). PROPERTY ABSENT.
            //     - Ladder rung (b): `serverOpts.JsonSerializerOptions` (alt-name knob) -> CS1061 again. PROPERTY ABSENT.
            //     - Ladder rungs (a)/(c): `services.Configure<JsonSerializerOptions>("McpServer", ...)` / DI-default
            //       COMPILE unconditionally (Configure<> is a standard DI extension) — compile success does NOT prove the
            //       SDK actually consumes them, so they are SEMANTICALLY UNVERIFIABLE at compile and would risk ANOTHER
            //       GREEN TEST TRAP. Rejected for that reason.
            //     - Ladder rung (d): [JsonPropertyName("snake_case")] attributes on the capture/stats DTO properties ONLY
            //       (MemoryCaptureInput/Output + MemoryGetStatsInput/Output) — DETERMINISTIC, SDK-independent, and
            //       verifiable by a unit test. CHOSEN PATH.
            //   Chosen path = rung (d). See the 4 DTO files (Contracts/ + the nested Models/Metadata.cs) for the
            //   [JsonPropertyName] overrides + their own BUG_FIX_CONTEXT scars. Program.cs itself has NO serializer
            //   knob to set (SDK 1.4.0 exposes none on McpServerOptions), so this lambda returns to setting ONLY ServerInfo;
            //   the snake_case contract now lives on the DTOs.
            // BUG_FIX_CONTEXT: [Why the old approach failed: the M7 @code dispatch treated the snake_case transport as a
            //   single options assignment but never wrote it — a deliverable omission, not a wrong API. The test suite
            //   could not detect it because tool unit tests bypass the SDK JSON-RPC transport (ctor-direct construction)
            //   and HostSmokeTests asserts only tool COUNT + NAMES, never the input-schema field-name casing — textbook
            //   GREEN TEST TRAP (mode-debug skill). Fix: [JsonPropertyName] overrides on the 4 M7 tool DTOs (rung d) make
            //   the snake_case transport contract DETERMINISTIC and verifiable regardless of SDK serializer internals.
            //   Why rung (d) over (a)/(c): the DI Configure<JsonSerializerOptions> rungs compile blindly — they would have
            //   created a SECOND GREEN TEST TRAP (green build, green tests, but the SDK silently ignores the options).
            //   mem-024's single-source-of-truth discipline is preserved IN SPIRIT: the snake_case mapping is defined ONCE
            //   per DTO property via an attribute (not duplicated across files); if a future SDK bump exposes a real
            //   serializer knob, the attributes become redundant-but-harmless and can be retired in favor of options-level
            //   config at that milestone. Immunization: the gap-closure unit tests (SnakeCaseTransportTests) prove BOTH
            //   directions (snake_case keys deserialize into the input DTOs; output DTOs serialize to snake_case) under
            //   default options — mirroring the SDK's default serializer — so any regression (attribute removed, new
            //   multi-word DTO property added without an attribute) is caught at unit-gate time. The [IMP:M7] marker below
            //   documents the resolution; the AppGraph McpServerOptions_SerializerOptions_UNVERIFIED_M7 node gate is now
            //   resolved (gate outcome: property absent -> rung d taken).]
            // [IMP:M7][ConfigureServices][OPTION] snake_case transport for tool I/O (ADR-010 mem-006 tool pattern).
            //   SPEC §4.2/§4.3 mandate snake_case property keys. SDK 1.4.0 exposes NO serializer knob on McpServerOptions
            //   (compile-probe CS1061 on both SerializerOptions AND JsonSerializerOptions), so the deterministic fallback
            //   [JsonPropertyName] attributes on the 4 M7 tool DTOs (Contracts/MemoryCaptureInput/Output +
            //   Contracts/MemoryGetStatsInput/Output) + nested Models/Metadata.cs is the chosen path (plan ladder rung d).
            //   mem-024 single-source-of-truth is preserved per-DTO; gap-closure is proven by SnakeCaseTransportTests.
        });

    // BUG_FIX_CONTEXT: [HYPOTHESIS: H4 — with no McpServerTool classes AND no explicit list-tools handler,
    //   the SDK registers NO tools/list handler, so tools/list fails with "Method 'tools/list' is not available"
    //   (JSON-RPC error, not result.tools). EVIDENCE: dotnet test log: "received request for method
    //   'tools/list', but no handler is available." + McpProtocolException at HostSmokeTests.cs:147.
    //   The M2 plan (test_guide note #6) correctly forbids [McpServerTool] classes but assumed tools/list
    //   "just works" — it does not; a list handler must exist. ts=2026-06-30T14:08:00Z]
    // BUG_FIX_CONTEXT: [Why the old approach failed: AddMcpServer() alone exposes no tools/ subsystem, so the
    //   tools/list method is unavailable even for an empty list. Fix: register WithListToolsHandler returning an
    //   empty ListToolsResult (Tools = []) so tools/list yields result.tools == [] per AC-5. This adds NO real
    //   tool implementations, honoring test_guide note #6 ("Do NOT add [McpServerTool] classes") in spirit and
    //   letter. Alternatives: (a) WithToolsFromAssembly() discovering zero tools — rejected: the AppGraph/plan
    //   explicitly forbids WithToolsFromAssembly in M2 and it couples tools/list to assembly scanning; (b) change
    //   AC-5 to expect a "method not available" error — rejected: spec mandates an empty array. Immunization: the
    //   handler is the canonical, explicit way to advertise an empty tool set, independent of SDK default-handler
    //   churn.]
    // [IMP:M7][ConfigureServices][OPTION] Register MCP server: Streamable-HTTP (stateless) + tools discovered from assembly
    // [IMP:M7] Superseded in M7 by WithToolsFromAssembly() — M2 empty-list placeholder was the designated eviction point.
    //   M2 scar block above kept for history. M2 BUG_FIX_CONTEXT explains why WithToolsFromAssembly was forbidden in M2.
    services.AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly();

    // [IMP:4][ConfigureServices][OPTION] Health checks (minimal — no external deps in M2)
    services.AddHealthChecks();

    // [IMP:M4][ConfigureServices][OPTION] Register ONNX embedding service (Singleton, IDisposable) — ADR-005 (fatal-on-load), ADR-011 (model)
    services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();

    // [IMP:M7][ConfigureServices][OPTION] Register MCP tools (transient — instantiated per tool call by WithToolsFromAssembly)
    services.AddTransient<McpMemoryService.Tools.MemoryCaptureTool>();
    services.AddTransient<McpMemoryService.Tools.MemoryGetStatsTool>();

    // [IMP:M8][ConfigureServices][OPTION] Register memory_retrieve MCP tool (Transient) — ADR-005 silent-fallback at tool level, ADR-010 WithToolsFromAssembly auto-discovery
    services.AddTransient<McpMemoryService.Tools.MemoryRetrieveTool>();

    // [IMP:M9][ConfigureServices][OPTION] Register memory_compact MCP tool (Transient) — ADR-003 transactional compact, ADR-005 silent-fallback
    services.AddTransient<McpMemoryService.Tools.MemoryCompactTool>();

    // [IMP:M5][ConfigureServices][OPTION] Register Qdrant service (Singleton) + collection initializer (HostedService) — ADR-005 (startup fatal), ADR-003 (hard-delete)
    services.AddSingleton<IQdrantService, QdrantService>();
    services.AddHostedService<QdrantCollectionInitializer>();

    // [IMP:M10][ConfigureServices][OPTION] Register resilience policies (Transient, stateless) — ADR-005
    services.AddTransient<QdrantResiliencePolicy>();
    services.AddTransient<EmbeddingResiliencePolicy>();

    // [IMP:M6][ConfigureServices][OPTION] Register LlmSummarizer service (Singleton) — ADR-003 (throws so M9 can detect failure; hard-delete gated on success), ADR-005 (caller catches)
    services.AddSingleton<ILlmSummarizerService, LlmSummarizerService>();

    // [IMP:9][ConfigureServices][SUCCESS] Service registration complete — 4 Options + MCP + HttpClient + HealthChecks + ONNX Embedding + Qdrant Service + LlmSummarizer Service
}
#endregion ServiceRegistration

#region Pipeline
/// <summary>
/// [PURPOSE]: Configures the HTTP request pipeline (middleware order).
/// </summary>
void ConfigurePipeline(WebApplication app)
{
    // [IMP:M10][ConfigurePipeline][OPTION] Global exception middleware (outermost — ADR-005, SPEC §7)
    app.UseMiddleware<GlobalExceptionMiddleware>();

    // [IMP:1][ConfigurePipeline][INIT] Mapping health check endpoint
    // Replaced the plain MapHealthChecks with a custom handler that also reports status to Wazuh.
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            WazuhAudit.Health("mcp-memory", report.Status.ToString());
            await Microsoft.AspNetCore.Http.Results.Ok(new { status = report.Status.ToString() }).ExecuteAsync(context);
        }
    });

    // [IMP:2][ConfigurePipeline][INIT] Mapping MCP Streamable-HTTP endpoint at /mcp
    // BUG_FIX_CONTEXT: [HYPOTHESIS: H1 — app.MapMcp() (parameterless) does NOT default to "/mcp";
    //   the SDK's parameterless overload passes an empty pattern, registering the endpoint at the
    //   root "/" (POST only). EVIDENCE: WebApplicationFactory endpoint enumeration probe
    //   (DebugProbeTests, IMP:10) shows EP#2 Pattern='/' Methods='POST', and POST /mcp => 404
    //   with empty body (no matching endpoint). test_guide.md note #5 and DevelopmentPlan.md
    //   both incorrectly asserted MapMcp() defaults to /mcp. ts=2026-06-30T13:55:00Z]
    // BUG_FIX_CONTEXT: [Why the old approach failed: relied on the documented-but-incorrect "default route = /mcp"
    //   assumption. The ModelContextProtocol.AspNetCore 1.4.0 XML docs only expose
    //   MapMcp(IEndpointRouteBuilder, string pattern); the parameterless extension forwards an
    //   empty pattern => root mapping. Fix: pass the explicit pattern "/mcp" so the route matches
    //   the design contract (test_guide note #5) and the smoke test's POST /mcp. Alternatives considered:
    //   (a) rewrite the test to POST "/" — rejected: the design contract (AGENTS.md, plan) fixes the
    //   route at /mcp; (b) keep MapMcp() and document the root mapping — rejected: diverges from spec.
    //   Immunization: explicit pattern makes the route independent of SDK default-changing churn.]
    // Wazuh audit: log every request to the /mcp endpoint.
    app.Use(async (context, next) =>
    {
        await next();
        if (context.Request.Path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            WazuhAudit.McpRequest(context.Request.Method, context.Response.StatusCode, context.Connection.RemoteIpAddress?.ToString() ?? "?");
        }
    });
    app.MapMcp("/mcp");

    // Metrics endpoint for Prometheus scraping (not registered in OpenAPI).
    app.MapPrometheusScrapingEndpoint();

    // [IMP:9][ConfigurePipeline][SUCCESS] Pipeline configured — health + MCP endpoints registered
}
#endregion Pipeline

// Required for WebApplicationFactory<Program> in tests (see test_guide.md note #1)
public partial class Program { }
