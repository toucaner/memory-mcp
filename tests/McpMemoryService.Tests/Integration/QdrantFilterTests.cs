#region MODULE_CONTRACT [DOMAIN(Integration): Qdrant filter correctness; CONCEPT(QdrantFilterTests): Verify filter logic with real Qdrant + ONNX; TECH(M12)]
/**
 * [GREP_SUMMARY]: QdrantFilterTests, IClassFixture<TestFixture>, CallToolAsync, agent_role_filter, entry_type_filter, project_id, limit, ResetAsync, filter correctness
 * [STRUCTURE]: InitializeAsync → Capture entries → Retrieve with filters → Verify results → ResetAsync → Repeat for next test
 *
 * <summary>
 * [PURPOSE]: 5 integration tests verifying Qdrant filter correctness with real Qdrant (via TestFixture's testcontainer) and real ONNX embeddings.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Requires Docker (for Qdrant testcontainer) + ONNX model (downloaded via M4 script). All tests use IClassFixture<TestFixture>.
 * [RATIONALE]: M12 verification of filter correctness — ensures agent_role_filter, entry_type_filter, project_id filtering, and limit enforcement work correctly.
 * [CHANGES]: LAST_CHANGE: M12-unit-3-fix — corrected JSON navigation across all 5 tests; TestFixture.CallToolAsync returns the tool output DTO (already-unwrapped from result.content[0].text), so retrieve results are accessed via GetProperty("results") and get_stats count via GetProperty("count").
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace McpMemoryService.Tests.Integration;

/// <summary>
/// [PURPOSE]: 5 integration tests verifying Qdrant filter correctness with real Qdrant (via TestFixture's testcontainer) and real ONNX embeddings.
/// </summary>
[Trait("Category", "Integration")]
public class QdrantFilterTests : IClassFixture<TestFixture>
{
    private readonly TestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public QdrantFilterTests(TestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    #region Test 1: Filter by Agent Role

    [Fact]
    public async Task Retrieve_FilterByAgentRole_ReturnsOnlyMatching()
    {
        await _fixture.ResetAsync();

        // Capture entries with different agent_role values
        var architectContent = "Architect decision about the project structure";
        var codeContent = "Code implementation of the capture tool";
        var debugContent = "Debug fix for the retrieve tool";
        var qaContent = "QA verification of the compact tool";

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = architectContent,
            ["project_id"] = _fixture.ProjectId,
            ["agent_role"] = "architect",
            ["entry_type"] = "decision"
        });

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = codeContent,
            ["project_id"] = _fixture.ProjectId,
            ["agent_role"] = "code",
            ["entry_type"] = "requirement"
        });

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = debugContent,
            ["project_id"] = _fixture.ProjectId,
            ["agent_role"] = "debug",
            ["entry_type"] = "BugFix"
        });

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = qaContent,
            ["project_id"] = _fixture.ProjectId,
            ["agent_role"] = "qa",
            ["entry_type"] = "summary"
        });

        // Retrieve with agent_role_filter=debug → verify only the debug entry is returned
        var debugResult = await _fixture.CallToolAsync("memory_retrieve", new Dictionary<string, object?>
        {
            ["query"] = "debug",
            ["project_id"] = _fixture.ProjectId,
            ["agent_role_filter"] = "debug",
            ["limit"] = 10
        });

        // BUG_FIX_CONTEXT: [HYPOTHESIS: TestFixture.CallToolAsync already extracts result.content[0].text and deserializes the tool output DTO, so the returned JsonElement IS the MemoryRetrieveOutput {results:[...]}. Navigation must use GetProperty("results"), not the raw envelope path result.content[0].text.]
        // BUG_FIX_CONTEXT: [Why the old approach failed: it re-walked result.content[0].text on an already-extracted DTO, causing KeyNotFound. Why this solution was chosen: matches the contract proven in MemoryPipelineE2ETests.cs and FallbackTests.cs.]
        var debugResults = debugResult.GetProperty("results").EnumerateArray().ToList();
        Assert.Single(debugResults);
        Assert.Equal(debugContent, debugResults[0].GetProperty("content").GetString());

        // Retrieve with agent_role_filter=architect → verify only the architect entry is returned
        var architectResult = await _fixture.CallToolAsync("memory_retrieve", new Dictionary<string, object?>
        {
            ["query"] = "architect",
            ["project_id"] = _fixture.ProjectId,
            ["agent_role_filter"] = "architect",
            ["limit"] = 10
        });

        var architectResults = architectResult.GetProperty("results").EnumerateArray().ToList();
        Assert.Single(architectResults);
        Assert.Equal(architectContent, architectResults[0].GetProperty("content").GetString());
    }

    #endregion

    #region Test 2: Filter by Entry Type

    [Fact]
    public async Task Retrieve_FilterByEntryType_ReturnsOnlyMatching()
    {
        await _fixture.ResetAsync();

        // Capture entries with types decision, bug_fix, insight
        var decisionContent = "Architect decision about the project structure";
        var bugFixContent = "Bug fix for the retrieve tool";
        var insightContent = "Process observation about the MCP protocol";

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = decisionContent,
            ["project_id"] = _fixture.ProjectId,
            ["agent_role"] = "architect",
            ["entry_type"] = "decision"
        });

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = bugFixContent,
            ["project_id"] = _fixture.ProjectId,
            ["agent_role"] = "debug",
            ["entry_type"] = "BugFix"
        });

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = insightContent,
            ["project_id"] = _fixture.ProjectId,
            ["agent_role"] = "debug",
            ["entry_type"] = "insight"
        });

        // Retrieve with entry_type_filter=bug_fix → verify only bug_fix returned
        var bugFixResult = await _fixture.CallToolAsync("memory_retrieve", new Dictionary<string, object?>
        {
            ["query"] = "bug fix",
            ["project_id"] = _fixture.ProjectId,
            ["entry_type_filter"] = "BugFix",
            ["limit"] = 10
        });

        // BUG_FIX_CONTEXT: [Scar] navigate the tool-output DTO via GetProperty("results") — the raw envelope path was already unwrapped by TestFixture.CallToolAsync.
        var bugFixResults = bugFixResult.GetProperty("results").EnumerateArray().ToList();
        Assert.Single(bugFixResults);
        Assert.Equal(bugFixContent, bugFixResults[0].GetProperty("content").GetString());
    }

    #endregion

    #region Test 3: Filter by ProjectId

    [Fact]
    public async Task Retrieve_FilterByProjectId_IsolatesProjects()
    {
        await _fixture.ResetAsync();

        var otherProjectId = "other-test-project";

        // Capture entries with project_id "project-a" and "project-b"
        var projectAContent = "Content for project-a";
        var projectBContent = "Content for project-b";

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = projectAContent,
            ["project_id"] = otherProjectId,
            ["agent_role"] = "code",
            ["entry_type"] = "decision"
        });

        await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
        {
            ["content"] = projectBContent,
            ["project_id"] = otherProjectId,
            ["agent_role"] = "code",
            ["entry_type"] = "requirement"
        });

        // Retrieve with the TestFixture's default project_id → no entries from other projects
        var result = await _fixture.CallToolAsync("memory_retrieve", new Dictionary<string, object?>
        {
            ["query"] = "content",
            ["project_id"] = _fixture.ProjectId,
            ["limit"] = 10
        });

        // BUG_FIX_CONTEXT: [Scar] navigate the tool-output DTO via GetProperty("results") — the raw envelope path was already unwrapped by TestFixture.CallToolAsync.
        var entriesArray = result.GetProperty("results").EnumerateArray().ToList();
        Assert.Empty(entriesArray);
    }

    #endregion

    #region Test 4: GetStats Filter by Entry Type

    [Fact]
    public async Task GetStats_FilterByEntryType_ReturnsCorrectCount()
    {
        await _fixture.ResetAsync();

        // Capture 3 entries with entry_type=bug_fix, 2 with entry_type=decision
        for (int i = 0; i < 3; i++)
        {
            await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
            {
                ["content"] = $"Bug fix #{i+1}",
                ["project_id"] = _fixture.ProjectId,
                ["agent_role"] = "debug",
                ["entry_type"] = "BugFix"
            });
        }

        for (int i = 0; i < 2; i++)
        {
            await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
            {
                ["content"] = $"Decision #{i+1}",
                ["project_id"] = _fixture.ProjectId,
                ["agent_role"] = "architect",
                ["entry_type"] = "decision"
            });
        }

        // Call get_stats with entry_type_filter=bug_fix → count=3
        var bugFixStats = await _fixture.CallToolAsync("memory_get_stats", new Dictionary<string, object?>
        {
            ["project_id"] = _fixture.ProjectId,
            ["entry_type"] = "BugFix"
        });

        // BUG_FIX_CONTEXT: [Scar] get_stats DTO is {count, entry_type?}; TestFixture.CallToolAsync already unwrapped result.content[0].text, so access count directly.
        var bugFixCount = bugFixStats.GetProperty("count").GetInt32();
        Assert.Equal(3, bugFixCount);

        // Call get_stats with entry_type_filter=decision → count=2
        var decisionStats = await _fixture.CallToolAsync("memory_get_stats", new Dictionary<string, object?>
        {
            ["project_id"] = _fixture.ProjectId,
            ["entry_type"] = "decision"
        });

        var decisionCount = decisionStats.GetProperty("count").GetInt32();
        Assert.Equal(2, decisionCount);
    }

    #endregion

    #region Test 5: Retrieve Limit Enforced

    [Fact]
    public async Task Retrieve_LimitEnforced()
    {
        await _fixture.ResetAsync();

        // Capture 10 entries
        for (int i = 0; i < 10; i++)
        {
            await _fixture.CallToolAsync("memory_capture", new Dictionary<string, object?>
            {
                ["content"] = $"Entry #{i+1}",
                ["project_id"] = _fixture.ProjectId,
                ["agent_role"] = "code",
                ["entry_type"] = "requirement"
            });
        }

        // Retrieve with limit=3 → exactly 3 results
        var result = await _fixture.CallToolAsync("memory_retrieve", new Dictionary<string, object?>
        {
            ["query"] = "entry",
            ["project_id"] = _fixture.ProjectId,
            ["limit"] = 3
        });

        // BUG_FIX_CONTEXT: [Scar] retrieve DTO is {results:[...]}; navigate via GetProperty("results") — TestFixture.CallToolAsync already unwrapped the JSON-RPC envelope.
        var entriesArray = result.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(3, entriesArray.Count);
    }

    #endregion
}