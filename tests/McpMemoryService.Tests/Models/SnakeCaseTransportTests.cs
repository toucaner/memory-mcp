#region MODULE_CONTRACT [DOMAIN(Test): M7 snake_case transport; CONCEPT(SnakeCaseTransportTests): JSON-RPC contract proof; TECH(xUnit, System.Text.Json)]
/**
 * [GREP_SUMMARY]: SnakeCaseTransportTests, JsonPropertyName, snake_case, deserialize, serialize, MemoryCaptureInput, MemoryCaptureOutput, MemoryGetStatsInput, MemoryGetStatsOutput, Metadata
 * [STRUCTURE]: SnakeCaseTransportTests → 4 Fact methods → Deserialize(snake_case JSON) + Serialize(DTO) using DEFAULT options (no PropertyNamingPolicy)
 *
 * <summary>
 * [PURPOSE]: Gap-closure tests that BREAK the M7 GREEN TEST TRAP — the 37/37 tool unit tests bypass the SDK
 *   JSON-RPC transport (ctor-direct construction), so they cannot detect a snake_case property-key contract gap.
 *   These tests prove the [JsonPropertyName] rung-d fix yields snake_case keys at the actual System.Text.Json
 *   transport boundary, in BOTH directions (input DTOs deserialize from snake_case JSON; output DTOs serialize
 *   to snake_case JSON), under DEFAULT JsonSerializerOptions (mirroring the SDK's default serializer config).
 * </summary>
 * <remarks>
 * [INVARIANTS]: All tests use in-memory data only (no external dependencies). Default options = no PropertyNamingPolicy,
 *   case-sensitive matching — exactly what the SDK's default serializer applies. The [JsonPropertyName] overrides are
 *   the SINGLE source of snake_case truth here (the McpServerOptions serializer knob does NOT exist on SDK 1.4.0).
 * [RATIONALE]: mode-debug skill — GREEN TEST TRAP (100% passed is NOT proof of correctness). @qa flagged AC-4/AC-8 BLOCK
 *   because the snake_case transport deliverable was OMITTED; these tests prove the contract is now enforced so the gap
 *   cannot silently regress. Blocks both tools/list input-schema publishing AND tools/call argument deserialization.
 * [CHANGES]: LAST_CHANGE: M7 fix (counter=1, scope=fix:M7-snake-case-transport) — added to close the GREEN TEST TRAP.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Models;

using System.Text.Json;
using FluentAssertions;
using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Models;
using Xunit;

/// <summary>
/// [PURPOSE]: Proves the M7 snake_case transport contract at the System.Text.Json boundary (both directions).
/// </summary>
public class SnakeCaseTransportTests
{
    // Reuse the test-suite convention (DtoValidationTests): RespectRequiredConstructorParameters=true so a missing
    // required member is NOT silently defaulted — it throws. This makes "snake_case key actually bound" falsifiable:
    // if a multi-word key (e.g. project_id) did NOT bind, the required ProjectId member would be missing -> JsonException.
    private static JsonSerializerOptions Options => new()
    {
        RespectRequiredConstructorParameters = true
    };

    /// <summary>
    /// [PURPOSE]: memory_capture input must deserialize from SPEC §4.2 snake_case JSON (incl. nested metadata).
    /// </summary>
    /// <remarks>
    /// [IMP:3]: BREAKS the GREEN TEST TRAP — proves multi-word keys (project_id/agent_role/entry_type/session_id)
    /// and the nested metadata object (file_path/error_code/session) actually BIND under default options. If the
    /// [JsonPropertyName] rung-d fix regressed (attribute removed / new multi-word property added without an
    /// attribute), this test fails — either a value mismatch or a JsonException (required member missing).
    /// </remarks>
    [Fact]
    public void MemoryCaptureInput_DeserializesFromSnakeCase()
    {
        // [IMP:3][MemoryCaptureInput_DeserializesFromSnakeCase][INIT] Building snake_case JSON per SPEC §4.2
        var json = """
        {
          "content": "Decided to use Qdrant for vector storage",
          "project_id": "proj-1",
          "agent_role": "code",
          "entry_type": "decision",
          "tags": ["adr", "qdrant"],
          "session_id": "sess-123",
          "metadata": { "file_path": "src/Foo.cs", "error_code": "ERR42", "session": "sess-123" }
        }
        """;

        // [IMP:3][MemoryCaptureInput_DeserializesFromSnakeCase][ACT] Deserializing under DEFAULT options
        var dto = JsonSerializer.Deserialize<MemoryCaptureInput>(json, Options);

        // [IMP:3][MemoryCaptureInput_DeserializesFromSnakeCase][CHECK] Asserting snake_case keys bound to DTO
        dto.Should().NotBeNull();
        dto!.Content.Should().Be("Decided to use Qdrant for vector storage");
        dto.ProjectId.Should().Be("proj-1");
        dto.AgentRole.Should().Be(AgentRole.Code);
        dto.EntryType.Should().Be(EntryType.Decision);
        dto.Tags.Should().BeEquivalentTo(new[] { "adr", "qdrant" });
        dto.SessionId.Should().Be("sess-123");
        dto.Metadata.Should().NotBeNull();
        dto.Metadata!.FilePath.Should().Be("src/Foo.cs");
        dto.Metadata.ErrorCode.Should().Be("ERR42");
        dto.Metadata.Session.Should().Be("sess-123");
    }

    /// <summary>
    /// [PURPOSE]: memory_capture output must SERIALIZE to SPEC §4.2 snake_case JSON (not PascalCase).
    /// </summary>
    /// <remarks>
    /// [IMP:3]: Proves the OUTPUT direction — the SDK serializes MemoryCaptureOutput back over JSON-RPC; the
    /// client must receive success/point_id/error (snake_case), not Success/PointId/Error. A regression (attribute
    /// removed) would surface PascalCase keys in the serialized JSON.
    /// </remarks>
    [Fact]
    public void MemoryCaptureOutput_SerializesToSnakeCase()
    {
        // [IMP:3][MemoryCaptureOutput_SerializesToSnakeCase][INIT] Building DTO
        var dto = new MemoryCaptureOutput
        {
            Success = true,
            PointId = "11111111-2222-4333-8444-555555555555",
            Error = null
        };

        // [IMP:3][MemoryCaptureOutput_SerializesToSnakeCase][ACT] Serializing under DEFAULT options
        var json = JsonSerializer.Serialize(dto, Options);

        // [IMP:3][MemoryCaptureOutput_SerializesToSnakeCase][CHECK] Asserting snake_case keys present, PascalCase absent
        json.Should().Contain("\"success\":true");
        json.Should().Contain("\"point_id\":\"11111111-2222-4333-8444-555555555555\"");
        json.Should().Contain("\"error\":null");
        json.Should().NotContain("\"Success\"");
        json.Should().NotContain("\"PointId\"");
        json.Should().NotContain("\"Error\"");
    }

    /// <summary>
    /// [PURPOSE]: memory_get_stats input must deserialize from SPEC §4.3 snake_case JSON.
    /// </summary>
    /// <remarks>
    /// [IMP:3]: Proves project_id + entry_type (multi-word) bind under default options. The enum VALUE (bug_fix) is
    /// handled by SnakeCaseEnumConverter (M3); the enum PROPERTY NAME (entry_type) is handled by [JsonPropertyName]
    /// (M7 rung-d) — this test proves both layers compose correctly.
    /// </remarks>
    [Fact]
    public void MemoryGetStatsInput_DeserializesFromSnakeCase()
    {
        // [IMP:3][MemoryGetStatsInput_DeserializesFromSnakeCase][INIT] Building snake_case JSON per SPEC §4.3
        var json = """
        {
          "project_id": "proj-1",
          "entry_type": "bug_fix"
        }
        """;

        // [IMP:3][MemoryGetStatsInput_DeserializesFromSnakeCase][ACT] Deserializing under DEFAULT options
        var dto = JsonSerializer.Deserialize<MemoryGetStatsInput>(json, Options);

        // [IMP:3][MemoryGetStatsInput_DeserializesFromSnakeCase][CHECK] Asserting snake_case keys bound to DTO
        dto.Should().NotBeNull();
        dto!.ProjectId.Should().Be("proj-1");
        dto.EntryType.Should().Be(EntryType.BugFix);
    }

    /// <summary>
    /// [PURPOSE]: memory_get_stats input must deserialize when the optional entry_type is omitted (null passthrough).
    /// </summary>
    /// <remarks>
    /// [IMP:3]: Covers the null-filter path — project_id present, entry_type omitted -> EntryType is null (passed
    /// straight through to IQdrantService.CountAsync as a null filter meaning "all entry types"). Proves the required
    /// project_id binds AND the nullable entry_type defaults cleanly (AC-10 null-passthrough invariant). GUARD: using
    /// a fresh instance per test (xUnit) — no shared mutation.
    /// </remarks>
    [Fact]
    public void MemoryGetStatsInput_DeserializesFromSnakeCase_NoEntryFilter_BindsNull()
    {
        var json = """{ "project_id": "proj-9" }""";

        var dto = JsonSerializer.Deserialize<MemoryGetStatsInput>(json, Options);

        dto.Should().NotBeNull();
        dto!.ProjectId.Should().Be("proj-9");
        dto.EntryType.Should().BeNull();
    }

    /// <summary>
    /// [PURPOSE]: memory_get_stats output must SERIALIZE to SPEC §4.3 snake_case JSON (not PascalCase).
    /// </summary>
    /// <remarks>
    /// [IMP:3]: Proves ADR-005 Count=-1 signal is transported under the snake_case "count" key (the client reads
    /// result.count, never result.Count). Regression = PascalCase key in the serialized JSON -> client miss.
    /// </remarks>
    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    public void MemoryGetStatsOutput_SerializesToSnakeCase(int count)
    {
        var dto = new MemoryGetStatsOutput { Count = count };

        var json = JsonSerializer.Serialize(dto, Options);

        json.Should().Contain($"\"count\":{count}");
        json.Should().NotContain("\"Count\"");
    }

    // =============================================================
    // M8: memory_retrieve DTO transport tests (rung-d preemptive, mem-027/mem-028)
    // =============================================================

    /// <summary>
    /// [PURPOSE]: memory_retrieve input must deserialize from SPEC §4.1 snake_case JSON.
    /// [IMP:3][M8]: Proves multi-word keys (project_id/agent_role_filter/entry_type_filter) and enum filters
    /// bind under default options. M8 rung-d preemptive discipline — applied at creation time, not after QA.
    /// </summary>
    [Fact]
    public void MemoryRetrieveInput_DeserializesFromSnakeCase()
    {
        var json = """
        {
          "query": "How to fix memory leak",
          "project_id": "proj-8",
          "agent_role_filter": "debug",
          "entry_type_filter": "bug_fix",
          "limit": 5
        }
        """;

        var dto = JsonSerializer.Deserialize<MemoryRetrieveInput>(json, Options);

        dto.Should().NotBeNull();
        dto!.Query.Should().Be("How to fix memory leak");
        dto.ProjectId.Should().Be("proj-8");
        dto.AgentRoleFilter.Should().Be(AgentRole.Debug);
        dto.EntryTypeFilter.Should().Be(EntryType.BugFix);
        dto.Limit.Should().Be(5);
    }

    /// <summary>
    /// [PURPOSE]: memory_retrieve output must SERIALIZE to SPEC §4.1 snake_case JSON (not PascalCase).
    /// [IMP:3][M8]: Proves the OUTPUT direction — all result fields (point_id, agent_role, entry_type, content,
    /// timestamp, score, tags) are snake_case in the serialized JSON, not PascalCase.
    /// </summary>
    [Fact]
    public void MemoryRetrieveOutput_SerializesToSnakeCase()
    {
        var result = new MemoryRetrieveResult
        {
            PointId = "11111111-2222-4333-8444-555555555555",
            AgentRole = AgentRole.Debug,
            EntryType = EntryType.BugFix,
            Content = "Found memory leak in cache",
            Timestamp = DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
            Score = 0.92f,
            Tags = new[] { "memory", "cache" }
        };
        var dto = new MemoryRetrieveOutput { Results = new[] { result } };

        var json = JsonSerializer.Serialize(dto, Options);

        json.Should().Contain("\"results\"");
        json.Should().Contain("\"point_id\":\"11111111-2222-4333-8444-555555555555\"");
        json.Should().Contain("\"agent_role\":\"debug\"");
        json.Should().Contain("\"entry_type\":\"bug_fix\"");
        json.Should().Contain("\"content\":\"Found memory leak in cache\"");
        json.Should().Contain("\"timestamp\":\"2026-07-01T12:00:00");
        json.Should().Contain("\"score\":0.92");
        json.Should().Contain("\"tags\":[\"memory\",\"cache\"]");
        json.Should().NotContain("\"Results\"");
        json.Should().NotContain("\"PointId\"");
        json.Should().NotContain("\"AgentRole\"");
        json.Should().NotContain("\"EntryType\"");
        json.Should().NotContain("\"Content\"");
        json.Should().NotContain("\"Timestamp\"");
        json.Should().NotContain("\"Score\"");
        json.Should().NotContain("\"Tags\"");
    }
}