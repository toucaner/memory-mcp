# M2 — Solution skeleton + MCP server bootstrap + config

## Dependencies
- M1 (SPEC corrections fixed, .NET 10 available)

## Goal
Create the .NET 10 solution skeleton: .sln, two csproj (src + tests), Program.cs with Minimal API host + DI + MCP server bootstrap, appsettings.json, Options classes, health endpoint. MCP handshake must respond to `initialize`/`tools/list` (empty tools list for now).

## Deliverables
- `McpMemoryService.sln` (root)
- `src/McpMemoryService/McpMemoryService.csproj` (.NET 10, Nullable enable, ImplicitUsings enable)
- `src/McpMemoryService/Program.cs`
- `src/McpMemoryService/appsettings.json`
- `src/McpMemoryService/appsettings.Development.json`
- `src/McpMemoryService/Configuration/QdrantOptions.cs`
- `src/McpMemoryService/Configuration/OnnxModelOptions.cs`
- `src/McpMemoryService/Configuration/LlmSummarizerOptions.cs`
- `src/McpMemoryService/Configuration/McpOptions.cs`
- `tests/McpMemoryService.Tests/McpMemoryService.Tests.csproj`
- `tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs`

## Contracts

### McpMemoryService.csproj — target framework and packages

```xml
<TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
```

Packages (verify versions via web_search at implementation time — see Algorithm):
- `ModelContextProtocol.SDK` (primary candidate) — OR fallback `StreamJsonRpc` + `Microsoft.AspNetCore.Mvc` (if SDK is unstable)
- `Qdrant.Client` (official NuGet)
- `Microsoft.ML.OnnxRuntime`
- `Microsoft.ML.Tokenizers`
- `Microsoft.Extensions.Http` (IHttpClientFactory)
- `Microsoft.Extensions.Hosting` (Generic Host)
- Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Moq`

### Program.cs — structure

```csharp
# region Usings
using McpMemoryService.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
# endregion

# region Entry
var builder = WebApplication.CreateBuilder(args);
ConfigureServices(builder.Services, builder.Configuration);
var app = builder.Build();
ConfigurePipeline(app);
await app.RunAsync();
# endregion

# region ServiceRegistration
// Options
services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"));
services.Configure<OnnxModelOptions>(configuration.GetSection("OnnxModel"));
services.Configure<LlmSummarizerOptions>(configuration.GetSection("LlmSummarizer"));
services.Configure<McpOptions>(configuration.GetSection("Mcp"));
// MCP server bootstrap (SDK-specific)
// HttpClient "LlamaCpp"
services.AddHttpClient("LlamaCpp", ...);
// Health checks
services.AddHealthChecks();
# endregion

# region Pipeline
app.MapHealthChecks("/health");
// MCP endpoint (SDK-specific: SSE or Streamable HTTP)
# endregion
```

### appsettings.json (from SPEC §8 + additions)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Qdrant": {
    "Url": "http://localhost:6333",
    "CollectionName": "opencode_memory"
  },
  "OnnxModel": {
    "ModelPath": "Models/model.onnx",
    "TokenizerPath": "Models/tokenizer.json",
    "IntraOpNumThreads": 4
  },
  "LlmSummarizer": {
    "BaseUrl": "<url>",
    "TimeoutSeconds": 60
  },
  "Mcp": {
    "ServerName": "McpMemoryService",
    "ServerVersion": "1.0.0"
  }
}
```

### Options classes (POCO with validation)

```csharp
public sealed class QdrantOptions
{
    public string Url { get; init; } = string.Empty;
    public string CollectionName { get; init; } = "opencode_memory";
}

public sealed class OnnxModelOptions
{
    public string ModelPath { get; init; } = "Models/model.onnx";
    public string TokenizerPath { get; init; } = "Models/tokenizer.json";
    public int IntraOpNumThreads { get; init; } = 4;
}

public sealed class LlmSummarizerOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed class McpOptions
{
    public string ServerName { get; init; } = "McpMemoryService";
    public string ServerVersion { get; init; } = "1.0.0";
}
```

## Algorithm / Logic

### Step 1: web_search validation of MCP SDK (CRITICAL)
Before creating csproj:
1. `web_search(query="ModelContextProtocol.SDK NuGet C# latest stable .NET 10", categories="it", language="en")`
2. `web_search(query="ModelContextProtocol C# SDK SSE Streamable HTTP server example", categories="it", language="en")`
3. `fetch_and_extract` top-2 URLs from results.
4. Evaluate: package stability, .NET 10 support, SSE/Streamable HTTP transport, AOT-compatibility (optional).
5. Decision:
   - If SDK is stable and supports .NET 10 → use `ModelContextProtocol.SDK`.
   - If unstable / no .NET 10 → fallback to `StreamJsonRpc` + manual MCP protocol implementation (initialize, tools/list, tools/call).
6. Record the decision in DevelopmentPlan.md or ADR with tag `[SOURCE: web_search, query="...", ts=<ISO8601>]`.

### Step 2: Create solution
```bash
dotnet new sln -n McpMemoryService
dotnet new web -n McpMemoryService -o src/McpMemoryService --framework net10.0
dotnet new xunit -n McpMemoryService.Tests -o tests/McpMemoryService.Tests --framework net10.0
dotnet sln add src/McpMemoryService/McpMemoryService.csproj
dotnet sln add tests/McpMemoryService.Tests/McpMemoryService.Tests.csproj
dotnet add tests/McpMemoryService.Tests/McpMemoryService.Tests.csproj reference src/McpMemoryService/McpMemoryService.csproj
```

### Step 3: Install packages
Install packages via `dotnet add package`. Versions — latest stable (verify via `web_search` if needed).

### Step 4: Program.cs + Options + health
Implement Program.cs structure per the contract above. Register Options via `IOptions<T>`. MCP endpoint configured per SDK documentation (step 1).

### Step 5: appsettings.json
Copy from SPEC §8 + additions (appsettings.Development.json with local paths).

### Step 6: Smoke test
The test must:
- Spin up `WebApplicationFactory<Program>`
- GET `/health` → 200 OK
- (optional) MCP-handshake: POST `initialize` → valid response

## Tests (unit, inline)

### tests/McpMemoryService.Tests/Smoke/HostSmokeTests.cs
```csharp
public class HostSmokeTests
{
    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        // Arrange: WebApplicationFactory<Program>
        // Act: GET /health
        // Assert: StatusCode == 200
    }

    [Fact]
    public async Task McpInitialize_ReturnsServerInfo()
    {
        // Arrange: factory + MCP client (or raw POST)
        // Act: initialize request
        // Assert: response contains serverInfo.name == "McpMemoryService"
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build McpMemoryService.sln` — OK without warnings
- [ ] `dotnet test` — smoke tests PASS
- [ ] GET `/health` returns 200
- [ ] MCP `initialize` returns serverInfo with name="McpMemoryService"
- [ ] MCP `tools/list` returns an empty array (tools not implemented yet)
- [ ] Options classes registered and bound to appsettings.json
- [ ] ADR/note about SDK choice recorded (SDK name, version, source)
- [ ] csproj targets net10.0

## Context for @code
- Read: `SPEC.md` (in full — especially §2 tech stack, §8 configuration)
- Read: `milestones/M1-foundation-spec-corrections.md` (fixed SPEC corrections)
- Skills: `csharp-conventions` (#region, XML docs, Options pattern)
- Previous artifacts: M1 (SPEC corrections)
- External prerequisite: NuGet access (for package install)
