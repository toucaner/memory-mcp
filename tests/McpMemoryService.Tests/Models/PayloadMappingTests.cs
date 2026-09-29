#region MODULE_CONTRACT [DOMAIN(Test): Mapping round-trip verification; CONCEPT(PayloadMappingTests): ToQdrantPayload, ToPayload, EntryType JSON; TECH(xUnit, Qdrant.Client.Grpc.Value)]
/**
 * [GREP_SUMMARY]: PayloadMappingTests, xUnit, ToQdrantPayload, ToPayload, round-trip, snake_case, EntryType, JSON serialization, AgentRole, Value
 * [STRUCTURE]: PayloadMappingTests xUnit class → ToQdrantPayload_ReturnsSnakeCaseKeys + ToPayload_RoundTrip_PreservesAllFields + EntryType/AgentRole Theory ×11
 *
 * <summary>
 * [PURPOSE]: Tests for PayloadMappingExtensions (round-trip with Value types) and enum JSON serialization.
 * </summary>
 * <remarks>
 * [INVARIANTS]: All tests use pure in-memory data (no Qdrant/Docker/ONNX/LLM).
 *   [IMP:1] on EntryType/AgentRole serialization Theory.
 *   [IMP:2] on ToQdrantPayload snake_case keys test.
 *   [IMP:4] on round-trip preservation test.
 * [RATIONALE]: M3 spec lines 251-282 + addition of AgentRole ×5 Theory (AC-4).
 *   Updated in M5 to use Qdrant.Client.Grpc.Value types instead of Dictionary<string,object>.
 * [CHANGES]: LAST_CHANGE: M5 — updated to Value protobuf types per PayloadMappingExtensions migration.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Models;

using System.Text.Json;
using McpMemoryService.Enums;
using McpMemoryService.Mapping;
using McpMemoryService.Models;
using Qdrant.Client.Grpc;

/// <summary>
/// [PURPOSE]: Unit tests for PayloadMappingExtensions and enum JSON serialization.
/// </summary>
public class PayloadMappingTests
{
    /// <summary>
    /// [PURPOSE]: ToQdrantPayload must produce snake_case keys with correct Value types.
    /// </summary>
    [Fact]
    public void ToQdrantPayload_ReturnsSnakeCaseKeys()
    {
        // [IMP:2][ToQdrantPayload_ReturnsSnakeCaseKeys][INIT] Arranging payload with EntryType=BugFix, AgentRole=Debug, Tags=["a","b"]
        var payload = new MemoryPayload
        {
            ProjectId = "test-project",
            SessionId = "test-session",
            AgentRole = AgentRole.Debug,
            EntryType = EntryType.BugFix,
            Timestamp = DateTimeOffset.Parse("2026-01-15T10:30:00Z"),
            Content = "Test content",
            Tags = new List<string> { "a", "b" }
        };

        var dict = payload.ToQdrantPayload();

        // [IMP:2][ToQdrantPayload_ReturnsSnakeCaseKeys][CHECK] Verifying snake_case keys and Value types
        Assert.Equal("bug_fix", dict["entry_type"].StringValue);
        Assert.Equal("debug", dict["agent_role"].StringValue);
        Assert.Equal("test-project", dict["project_id"].StringValue);
        Assert.Equal("test-session", dict["session_id"].StringValue);
        Assert.Equal("2026-01-15T10:30:00.0000000+00:00", dict["timestamp"].StringValue);
        Assert.Equal("Test content", dict["content"].StringValue);

        // Tags via ListValue
        var tags = dict["tags"].ListValue.Values.Select(v => v.StringValue).ToList();
        Assert.Equal(new List<string> { "a", "b" }, tags);

        Assert.Equal(7, dict.Count); // project_id, session_id, agent_role, entry_type, timestamp, content, tags (metadata absent)
    }

    /// <summary>
    /// [PURPOSE]: ToPayload round-trip must preserve all fields including Tags and Metadata.
    /// </summary>
    [Fact]
    public void ToPayload_RoundTrip_PreservesAllFields()
    {
        // [IMP:4][ToPayload_RoundTrip_PreservesAllFields][INIT] Arranging full MemoryPayload with Tags + Metadata
        var original = new MemoryPayload
        {
            ProjectId = "proj-123",
            SessionId = "sess-456",
            AgentRole = AgentRole.Architect,
            EntryType = EntryType.Decision,
            Timestamp = DateTimeOffset.Parse("2026-06-30T12:00:00Z"),
            Content = "Architecture decision: use Dapper over EF for performance",
            Tags = new List<string> { "architecture", "performance" },
            Metadata = new Metadata
            {
                FilePath = "E:\\Projects\\ai\\Projects\\memory-mcp\\DevelopmentPlan.md",
                ErrorCode = null,
                Session = "arch-design-2026"
            }
        };

        // [IMP:4][ToPayload_RoundTrip_PreservesAllFields][ACT] Round-trip: ToQdrantPayload → ToPayload
        var dict = original.ToQdrantPayload();
        var restored = dict.ToPayload();

        // [IMP:4][ToPayload_RoundTrip_PreservesAllFields][CHECK] Asserting all fields preserved
        Assert.Equal(original.ProjectId, restored.ProjectId);
        Assert.Equal(original.SessionId, restored.SessionId);
        Assert.Equal(original.AgentRole, restored.AgentRole);
        Assert.Equal(original.EntryType, restored.EntryType);
        Assert.Equal(original.Timestamp, restored.Timestamp);
        Assert.Equal(original.Content, restored.Content);
        Assert.Equal(original.Tags, restored.Tags);
        Assert.NotNull(restored.Metadata);
        Assert.Equal(original.Metadata!.FilePath, restored.Metadata.FilePath);
        Assert.Null(restored.Metadata.ErrorCode);
        Assert.Equal(original.Metadata.Session, restored.Metadata.Session);
    }

    /// <summary>
    /// [PURPOSE]: EntryType must serialize to snake_case via the [JsonConverter] attribute.
    /// </summary>
    /// <remarks>
    /// [IMP:1]: Enum JSON serialization via default JsonSerializerOptions (the [JsonConverter] attribute supplies the converter).
    /// </remarks>
    [Theory]
    [InlineData(EntryType.Decision, "decision")]
    [InlineData(EntryType.BugFix, "bug_fix")]
    [InlineData(EntryType.Requirement, "requirement")]
    [InlineData(EntryType.Summary, "summary")]
    [InlineData(EntryType.Rejection, "rejection")]
    [InlineData(EntryType.Insight, "insight")]
    public void EntryType_SerializesToSnakeCase(EntryType type, string expected)
    {
        // [IMP:1][EntryType_SerializesToSnakeCase][INIT] Serializing with default options (attribute supplies converter)
        var json = JsonSerializer.Serialize(type);
        var result = JsonSerializer.Deserialize<string>(json);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// [PURPOSE]: AgentRole must serialize to snake_case via the [JsonConverter] attribute.
    /// </summary>
    /// <remarks>
    /// [IMP:1]: Same mechanism as EntryType — the [JsonConverter] attribute on the enum type supplies the converter.
    /// </remarks>
    [Theory]
    [InlineData(AgentRole.Orchestrator, "orchestrator")]
    [InlineData(AgentRole.Architect, "architect")]
    [InlineData(AgentRole.Code, "code")]
    [InlineData(AgentRole.Debug, "debug")]
    [InlineData(AgentRole.Qa, "qa")]
    public void AgentRole_SerializesToSnakeCase(AgentRole role, string expected)
    {
        // [IMP:1][AgentRole_SerializesToSnakeCase][INIT] Serializing with default options (attribute supplies converter)
        var json = JsonSerializer.Serialize(role);
        var result = JsonSerializer.Deserialize<string>(json);
        Assert.Equal(expected, result);
    }
}
