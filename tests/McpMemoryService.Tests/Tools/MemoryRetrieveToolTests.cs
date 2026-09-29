#region MODULE_CONTRACT [DOMAIN(Test): memory_retrieve tool; CONCEPT(MemoryRetrieveToolTests): ctor-direct unit tests; TECH(xUnit, Moq, NullLogger)]
/**
 * [GREP_SUMMARY]: MemoryRetrieveToolTests, RetrieveAsync, MemoryRetrieveInput, MemoryRetrieveOutput, MemoryRetrieveResult, Mock IEmbeddingService, Mock IQdrantService, silent-fallback, validation, filters, score, limit
 * [STRUCTURE]: MemoryRetrieveToolTests → 11 Fact methods → ctor-direct construction (no SDK transport) → mocked services
 *
 * <summary>
 * [PURPOSE]: Unit tests for the memory_retrieve MCP tool using ctor-direct construction (no MCP SDK transport).
 * </summary>
 * <remarks>
 * [INVARIANTS]: All tests use mocked IEmbeddingService + mocked IQdrantService + NullLogger.
 *   No real Qdrant/Docker/ONNX required — UNCATEGORISED (runs in the "Category!=Integration" gate).
 * [RATIONALE]: Follows the M7 MemoryCaptureToolTests pattern — ctor-direct construction bypasses the SDK JSON-RPC
 *   transport, isolating tool logic from transport concerns. The snake_case transport contract is verified separately
 *   by SnakeCaseTransportTests.
 * [CHANGES]: LAST_CHANGE: M8 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Tools;

using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Models;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>
/// [PURPOSE]: Unit tests for MemoryRetrieveTool.RetrieveAsync — covers success, filters, limit, silent-fallback, validation.
/// </summary>
public class MemoryRetrieveToolTests
{
    private readonly Mock<IEmbeddingService> _mockEmbedding;
    private readonly Mock<IQdrantService> _mockQdrant;
    private readonly MemoryRetrieveTool _tool;
    private readonly MemoryRetrieveInput _validInput;

    public MemoryRetrieveToolTests()
    {
        _mockEmbedding = new Mock<IEmbeddingService>();
        _mockQdrant = new Mock<IQdrantService>();
        // BUG_FIX_CONTEXT: [HYPOTHESIS: M10 added QdrantResiliencePolicy + EmbeddingResiliencePolicy as ctor params; old test constructed with only (embedding, qdrant, logger) — CS7036. Pass real sealed policy instances with NullLogger; on success they pass through (retrieve still returns mapped results), on Qdrant/ONNX failure they fallback to empty/throw so old silent-fallback assertions still hold.]
        _tool = new MemoryRetrieveTool(
            _mockEmbedding.Object,
            _mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            NullLogger<MemoryRetrieveTool>.Instance);

        _validInput = new MemoryRetrieveInput
        {
            Query = "memory leak",
            ProjectId = "proj-123"
        };

        _mockEmbedding.Setup(x => x.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[384]);

        _mockQdrant.Setup(x => x.SearchAsync(
                It.IsAny<float[]>(),
                It.IsAny<string>(),
                It.IsAny<AgentRole?>(),
                It.IsAny<EntryType?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());
    }

    private static MemoryEntry MakeEntry(float score = 0.85f)
    {
        return new MemoryEntry
        {
            PointId = Guid.NewGuid(),
            Payload = new MemoryPayload
            {
                ProjectId = "proj-123",
                SessionId = "sess-1",
                AgentRole = AgentRole.Debug,
                EntryType = EntryType.BugFix,
                Timestamp = DateTimeOffset.UtcNow,
                Content = "Found memory leak in cache",
                Tags = new[] { "memory", "cache" }
            },
            Vector = new float[384],
            Score = score
        };
    }

    /// <summary>
    /// [PURPOSE]: Valid input returns correctly mapped results (2 entries, all fields mapped).
    /// [IMP:1]: embedding generated, [IMP:2]: search returned results.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_ValidInput_ReturnsResults()
    {
        var entries = new List<MemoryEntry> { MakeEntry(), MakeEntry() };
        _mockQdrant.Setup(x => x.SearchAsync(
                It.IsAny<float[]>(), It.IsAny<string>(),
                It.IsAny<AgentRole?>(), It.IsAny<EntryType?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var output = await _tool.RetrieveAsync(_validInput, CancellationToken.None);

        Assert.Equal(2, output.Results.Count);
        Assert.Equal(entries[0].PointId.ToString("D"), output.Results[0].PointId);
        Assert.Equal(AgentRole.Debug, output.Results[0].AgentRole);
        Assert.Equal(EntryType.BugFix, output.Results[0].EntryType);
        Assert.Equal(entries[0].Payload.Content, output.Results[0].Content);
        Assert.Equal(entries[0].Payload.Timestamp, output.Results[0].Timestamp);
        Assert.Equal(0.85f, output.Results[0].Score);
        Assert.Equal(entries[0].Payload.Tags, output.Results[0].Tags);
    }

    /// <summary>
    /// [PURPOSE]: Verifies EmbedAsync is called with the query from input.
    /// [IMP:1]: embedding generated from query.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_GeneratesEmbeddingFromQuery()
    {
        await _tool.RetrieveAsync(_validInput, CancellationToken.None);

        _mockEmbedding.Verify(x => x.EmbedAsync("memory leak", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: Verifies filters (agent_role_filter, entry_type_filter) are passed to SearchAsync.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_PassesFiltersToQdrant()
    {
        var input = new MemoryRetrieveInput
        {
            Query = "test",
            ProjectId = "proj-123",
            AgentRoleFilter = AgentRole.Debug,
            EntryTypeFilter = EntryType.BugFix,
            Limit = 5
        };

        await _tool.RetrieveAsync(input, CancellationToken.None);

        _mockQdrant.Verify(x => x.SearchAsync(
            It.IsAny<float[]>(),
            "proj-123",
            AgentRole.Debug,
            EntryType.BugFix,
            5,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: Verifies the Limit parameter is passed through to SearchAsync.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_RespectsLimit()
    {
        var input = new MemoryRetrieveInput
        {
            Query = "test",
            ProjectId = "proj-123",
            Limit = 3
        };

        await _tool.RetrieveAsync(input, CancellationToken.None);

        _mockQdrant.Verify(x => x.SearchAsync(
            It.IsAny<float[]>(), It.IsAny<string>(),
            It.IsAny<AgentRole?>(), It.IsAny<EntryType?>(),
            3, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: Qdrant failure returns empty Results (silent fallback per ADR-005) — does NOT throw.
    /// [IMP:3]: fallback to empty on error.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_QdrantFails_ReturnsEmptyResults()
    {
        _mockQdrant.Setup(x => x.SearchAsync(
                It.IsAny<float[]>(), It.IsAny<string>(),
                It.IsAny<AgentRole?>(), It.IsAny<EntryType?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("qdrant down"));

        var output = await _tool.RetrieveAsync(_validInput, CancellationToken.None);

        Assert.Empty(output.Results);
    }

    /// <summary>
    /// [PURPOSE]: Embedding failure also returns empty Results (silent fallback per ADR-005).
    /// [IMP:3]: fallback to empty on error.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_EmbeddingFails_ReturnsEmptyResults()
    {
        _mockEmbedding.Setup(x => x.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("onnx died"));

        var output = await _tool.RetrieveAsync(_validInput, CancellationToken.None);

        Assert.Empty(output.Results);
    }

    /// <summary>
    /// [PURPOSE]: Empty query throws ArgumentException (validation, NOT swallowed).
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_EmptyQuery_ThrowsValidation()
    {
        var input = new MemoryRetrieveInput { Query = "", ProjectId = "proj-123" };

        await Assert.ThrowsAsync<ArgumentException>(() => _tool.RetrieveAsync(input, CancellationToken.None));
    }

    /// <summary>
    /// [PURPOSE]: Whitespace query throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_WhitespaceQuery_ThrowsValidation()
    {
        var input = new MemoryRetrieveInput { Query = "   ", ProjectId = "proj-123" };

        await Assert.ThrowsAsync<ArgumentException>(() => _tool.RetrieveAsync(input, CancellationToken.None));
    }

    /// <summary>
    /// [PURPOSE]: Empty project_id throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_EmptyProjectId_ThrowsValidation()
    {
        var input = new MemoryRetrieveInput { Query = "test", ProjectId = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => _tool.RetrieveAsync(input, CancellationToken.None));
    }

    /// <summary>
    /// [PURPOSE]: Limit > 10 throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_LimitExceeds10_ThrowsValidation()
    {
        var input = new MemoryRetrieveInput { Query = "test", ProjectId = "proj-123", Limit = 50 };

        await Assert.ThrowsAsync<ArgumentException>(() => _tool.RetrieveAsync(input, CancellationToken.None));
    }

    /// <summary>
    /// [PURPOSE]: Limit = 0 throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_LimitZero_ThrowsValidation()
    {
        var input = new MemoryRetrieveInput { Query = "test", ProjectId = "proj-123", Limit = 0 };

        await Assert.ThrowsAsync<ArgumentException>(() => _tool.RetrieveAsync(input, CancellationToken.None));
    }

    /// <summary>
    /// [PURPOSE]: Results contain Score from Qdrant semantic search (mapped from MemoryEntry.Score ?? 0f).
    /// [IMP:2]: search returned results with scores.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_ResultsContainScoreFromQdrant()
    {
        var entry = MakeEntry(0.92f);
        _mockQdrant.Setup(x => x.SearchAsync(
                It.IsAny<float[]>(), It.IsAny<string>(),
                It.IsAny<AgentRole?>(), It.IsAny<EntryType?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { entry });

        var output = await _tool.RetrieveAsync(_validInput, CancellationToken.None);

        Assert.Equal(0.92f, output.Results[0].Score);
    }
}
