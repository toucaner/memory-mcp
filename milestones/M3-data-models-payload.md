# M3 — Data models: enums, payload records, DTOs

## Dependencies
- M2 (solution skeleton, csproj, Program.cs, Options)

## Goal
Create C# domain models: enums for entry_type (6 values) and agent_role (5 values), record for Qdrant payload, input/output DTOs for 4 MCP-tools, mapping extensions. Models must validate and serialize to JSON per SPEC §3.

## Deliverables
- `src/McpMemoryService/Enums/EntryType.cs`
- `src/McpMemoryService/Enums/AgentRole.cs`
- `src/McpMemoryService/Models/MemoryPayload.cs`
- `src/McpMemoryService/Models/MemoryEntry.cs` (payload + vector)
- `src/McpMemoryService/Models/Metadata.cs`
- `src/McpMemoryService/Contracts/MemoryCaptureInput.cs`
- `src/McpMemoryService/Contracts/MemoryCaptureOutput.cs`
- `src/McpMemoryService/Contracts/MemoryRetrieveInput.cs`
- `src/McpMemoryService/Contracts/MemoryRetrieveOutput.cs`
- `src/McpMemoryService/Contracts/MemoryGetStatsInput.cs`
- `src/McpMemoryService/Contracts/MemoryGetStatsOutput.cs`
- `src/McpMemoryService/Contracts/MemoryCompactInput.cs`
- `src/McpMemoryService/Contracts/MemoryCompactOutput.cs`
- `src/McpMemoryService/Mapping/PayloadMappingExtensions.cs`
- `tests/McpMemoryService.Tests/Models/PayloadMappingTests.cs`
- `tests/McpMemoryService.Tests/Models/DtoValidationTests.cs`

## Contracts

### Enums/EntryType.cs

```csharp
namespace McpMemoryService.Enums;

/// <summary>
/// Memory entry type. Matches SPEC.md §3 payload.entry_type.
/// </summary>
public enum EntryType
{
    /// <summary>Architectural choice, pattern, stack (why we chose it).</summary>
    Decision,

    /// <summary>Root cause of a bug and the fix approach.</summary>
    BugFix,

    /// <summary>Important business rule or constraint.</summary>
    Requirement,

    /// <summary>Compressed summary of old entries (created only via compact).</summary>
    Summary,

    /// <summary>Rejected architectural option (why we did NOT choose it).</summary>
    Rejection,

    /// <summary>Process observation, workaround, non-standard API usage.</summary>
    Insight
}
```

JSON serialization: snake_case (`decision`, `bug_fix`, `requirement`, `summary`, `rejection`, `insight`). Use `[JsonStringConverter]` or a custom converter.

### Enums/AgentRole.cs

```csharp
namespace McpMemoryService.Enums;

/// <summary>
/// Source agent role. Matches SPEC.md §3 payload.agent_role.
/// </summary>
public enum AgentRole
{
    Orchestrator,
    Architect,
    Code,
    Debug,
    Qa
}
```

JSON: snake_case (`orchestrator`, `architect`, `code`, `debug`, `qa`).

### Models/Metadata.cs

```csharp
namespace McpMemoryService.Models;

public sealed record Metadata
{
    public string? FilePath { get; init; }
    public string? ErrorCode { get; init; }
    public string? Session { get; init; }
}
```

### Models/MemoryPayload.cs (matches SPEC §3 payload)

```csharp
namespace McpMemoryService.Models;

public sealed record MemoryPayload
{
    public required string ProjectId { get; init; }
    public required string SessionId { get; init; }
    public required AgentRole AgentRole { get; init; }
    public required EntryType EntryType { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Content { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public Metadata? Metadata { get; init; }
}
```

### Models/MemoryEntry.cs (payload + vector for internal use)

```csharp
namespace McpMemoryService.Models;

public sealed record MemoryEntry
{
    public required Guid PointId { get; init; }
    public required MemoryPayload Payload { get; init; }
    public required float[] Vector { get; init; }
    public float? Score { get; init; }
}
```

### Contracts/ — DTOs for 4 MCP-tools

Each input class is marked `[JsonSerializable]` for AOT-friendly serialization (if SDK requires). Field names — snake_case for MCP-client compatibility.

#### MemoryCaptureInput.cs (SPEC §4.2)
```csharp
public sealed record MemoryCaptureInput
{
    public required string Content { get; init; }
    public required string ProjectId { get; init; }
    public required AgentRole AgentRole { get; init; }
    public required EntryType EntryType { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? SessionId { get; init; }
    public Metadata? Metadata { get; init; }
}
```

#### MemoryCaptureOutput.cs
```csharp
public sealed record MemoryCaptureOutput
{
    public required bool Success { get; init; }
    public required string PointId { get; init; }
    public string? Error { get; init; }
}
```

#### MemoryRetrieveInput.cs (SPEC §4.1 + S4, S5)
```csharp
public sealed record MemoryRetrieveInput
{
    public required string Query { get; init; }
    public required string ProjectId { get; init; }
    public AgentRole? AgentRoleFilter { get; init; }
    public EntryType? EntryTypeFilter { get; init; }
    public int Limit { get; init; } = 5;
}
```
Limit: default=5, maximum=10 (validation in tool).

#### MemoryRetrieveOutput.cs
```csharp
public sealed record MemoryRetrieveOutput
{
    public required IReadOnlyList<MemoryRetrieveResult> Results { get; init; }
}

public sealed record MemoryRetrieveResult
{
    public required string PointId { get; init; }
    public required AgentRole AgentRole { get; init; }
    public required EntryType EntryType { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required float Score { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}
```

#### MemoryGetStatsInput.cs (SPEC §4.3)
```csharp
public sealed record MemoryGetStatsInput
{
    public required string ProjectId { get; init; }
    public EntryType? EntryType { get; init; }
}
```

#### MemoryGetStatsOutput.cs
```csharp
public sealed record MemoryGetStatsOutput
{
    public required int Count { get; init; }
}
```

#### MemoryCompactInput.cs (SPEC §4.4)
```csharp
public sealed record MemoryCompactInput
{
    public required string ProjectId { get; init; }
    public int BatchSize { get; init; } = 20;
}
```

#### MemoryCompactOutput.cs (accounts for non-blocking S8)
```csharp
public sealed record MemoryCompactOutput
{
    public required string Status { get; init; }   // "completed" | "skipped" | "error"
    public string? Reason { get; init; }           // "insufficient_data" | "llm_timeout" | ...
    public int? Available { get; init; }
    public int? Required { get; init; }
    public int? SourceCount { get; init; }
    public string? SummaryPointId { get; init; }
    public string? Error { get; init; }
}
```

### Mapping/PayloadMappingExtensions.cs

Extension methods to convert between MemoryPayload and Qdrant structures (will be used in M5):
```csharp
public static class PayloadMappingExtensions
{
    public static MemoryPayload ToPayload(this IDictionary<string, object> qdrantPayload);
    public static IDictionary<string, object> ToQdrantPayload(this MemoryPayload payload);
    public static MemoryEntry ToEntry(this ... qdrantPoint);
}
```
Implementation — stubs/basic serialization via `JsonSerializer`. Full integration with Qdrant.Client types — in M5.

## Algorithm / Logic
1. Create directories `Enums/`, `Models/`, `Contracts/`, `Mapping/`.
2. Implement enums with XML documentation and JSON snake_case converters.
3. Implement record types with `required` fields and `init` setters.
4. Implement DTOs with validation (Limit ≤ 10, ProjectId non-empty — in tool, not here).
5. Implement mapping extensions (basic JSON serialization).
6. Configure `JsonSerializerOptions` with `PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower` and enum converters.
7. Write unit tests.

## Tests (unit, inline)

### tests/.../Models/PayloadMappingTests.cs
```csharp
public class PayloadMappingTests
{
    [Fact]
    public void ToQdrantPayload_ReturnsSnakeCaseKeys()
    {
        // Arrange: MemoryPayload with EntryType=BugFix, AgentRole=Debug, Tags=["a","b"]
        // Act: ToQdrantPayload()
        // Assert: keys "entry_type"=="bug_fix", "agent_role"=="debug", "tags"==["a","b"]
    }

    [Fact]
    public void ToPayload_RoundTrip_PreservesAllFields()
    {
        // Arrange: MemoryPayload
        // Act: ToQdrantPayload → ToPayload
        // Assert: all fields preserved
    }

    [Theory]
    [InlineData(EntryType.Decision, "decision")]
    [InlineData(EntryType.BugFix, "bug_fix")]
    [InlineData(EntryType.Requirement, "requirement")]
    [InlineData(EntryType.Summary, "summary")]
    [InlineData(EntryType.Rejection, "rejection")]
    [InlineData(EntryType.Insight, "insight")]
    public void EntryType_SerializesToSnakeCase(EntryType type, string expected)
    {
        // Assert JSON serialization
    }
}
```

### tests/.../Models/DtoValidationTests.cs
```csharp
public class DtoValidationTests
{
    [Fact]
    public void MemoryRetrieveInput_DefaultLimit_Is5()
    {
        var input = new MemoryRetrieveInput { Query = "q", ProjectId = "p" };
        Assert.Equal(5, input.Limit);
    }

    [Fact]
    public void MemoryCompactInput_DefaultBatchSize_Is20()
    {
        var input = new MemoryCompactInput { ProjectId = "p" };
        Assert.Equal(20, input.BatchSize);
    }

    [Fact]
    public void MemoryCaptureInput_RequiredFields_ThrowWhenMissing()
    {
        // Assert: required fields throw when absent
    }
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK without warnings
- [ ] `dotnet test` — all unit tests PASS
- [ ] EntryType has 6 values, serializes to snake_case
- [ ] AgentRole has 5 values, serializes to snake_case
- [ ] MemoryPayload matches SPEC §3 (all fields, including tags)
- [ ] DTOs for 4 tools match SPEC §4.1-4.4 (including entry_type_filter from M1/S5)
- [ ] MemoryCompactOutput supports non-blocking status (completed/skipped/error)
- [ ] Mapping round-trip preserves all fields

## Context for @code
- Read: `SPEC.md` §3 (data schema), §4 (tools specification)
- Read: `milestones/M1-foundation-spec-corrections.md` (corrections S1-S8: 6 entry_type, entry_type_filter, non-blocking compact)
- Skills: `csharp-conventions` (records, XML docs, #region)
- Previous artifacts: M2 (csproj, Program.cs, Options)
