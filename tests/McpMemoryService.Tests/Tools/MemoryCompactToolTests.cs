#region MODULE_CONTRACT [DOMAIN(Test): memory_compact tool; CONCEPT(MemoryCompactToolTests): ctor-direct unit tests; TECH(xUnit, Moq, NullLogger)]
/**
 * [GREP_SUMMARY]: MemoryCompactToolTests, ExecuteAsync, MemoryCompactInput, MemoryCompactOutput, Mock IQdrantService, Mock ILlmSummarizerService, Mock IEmbeddingService, transactional, ADR-003, ADR-004, ADR-005, validation, skipped, completed, error
 * [STRUCTURE]: MemoryCompactToolTests → 10 Fact methods → ctor-direct construction (no SDK transport) → mocked services
 *
 * <summary>
 * [PURPOSE]: Unit tests for the memory_compact MCP tool using ctor-direct construction (no MCP SDK transport).
 * </summary>
 * <remarks>
 * [INVARIANTS]: All tests use mocked IQdrantService + mocked ILlmSummarizerService + mocked IEmbeddingService + NullLogger.
 *   No real Qdrant/Docker/ONNX/LLM required — UNCATEGORISED (runs in the "Category!=Integration" gate).
 * [RATIONALE]: Follows the M7 MemoryCaptureToolTests pattern — ctor-direct construction bypasses the SDK JSON-RPC
 *   transport, isolating tool logic from transport concerns.
 * [CHANGES]: LAST_CHANGE: M9 creation.
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
/// [PURPOSE]: Unit tests for MemoryCompactTool.ExecuteAsync — covers success, skip, LLM errors, validation, summary payload, transactional guarantees.
/// </summary>
public class MemoryCompactToolTests
{
    private static MemoryCompactInput ValidInput => new()
    {
        ProjectId = "test-project",
        BatchSize = 20
    };

    private static IReadOnlyList<MemoryEntry> CreateEntries(int count)
    {
        return Enumerable.Range(0, count).Select(i => new MemoryEntry
        {
            PointId = Guid.NewGuid(),
            Payload = new MemoryPayload
            {
                ProjectId = "test-project",
                SessionId = "session",
                AgentRole = AgentRole.Debug,
                EntryType = EntryType.BugFix,
                Timestamp = DateTimeOffset.UtcNow.AddDays(-i),
                Content = $"log entry {i}",
                Tags = Array.Empty<string>(),
                Metadata = new Metadata { Session = "test" }
            },
            Vector = new float[384],
            Score = 0.5f
        }).ToList().AsReadOnly();
    }

    private static (MemoryCompactTool Tool, Mock<IQdrantService> Qdrant, Mock<ILlmSummarizerService> Llm, Mock<IEmbeddingService> Embedding) CreateTool()
    {
        var qdrant = new Mock<IQdrantService>();
        var llm = new Mock<ILlmSummarizerService>();
        var embedding = new Mock<IEmbeddingService>();
        // BUG_FIX_CONTEXT: [HYPOTHESIS: M10 added QdrantResiliencePolicy + EmbeddingResiliencePolicy as ctor params; old test constructed with only (qdrant, llm, embedding, logger) — CS7036. Pass real sealed policy instances with NullLogger; only the batch fetch + summary embedding are wrapped by the policies, so the transactional LLM/delete guarantees (ADR-003) in the existing 10 tests still hold.]
        var tool = new MemoryCompactTool(
            qdrant.Object,
            llm.Object,
            embedding.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            NullLogger<MemoryCompactTool>.Instance);
        return (tool, qdrant, llm, embedding);
    }

    /// <summary>
    /// [PURPOSE]: Sufficient data — LLM succeeds → summary captured → sources deleted → status=completed.
    /// [IMP:1]: batch fetched, [IMP:2]: LLM summarized, [IMP:3]: summary captured, [IMP:4]: sources deleted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SufficientData_CompletesAndDeletesSources()
    {
        // Arrange
        var (tool, qdrant, llm, embedding) = CreateTool();
        qdrant.Setup(x => x.GetBatchForCompactAsync("test-project", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEntries(20));
        llm.Setup(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("summary text");
        embedding.Setup(x => x.EmbedAsync("summary text", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[384]);
        qdrant.Setup(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        qdrant.Setup(x => x.DeleteAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await tool.ExecuteAsync(ValidInput);

        // Assert
        Assert.Equal("completed", result.Status);
        Assert.Equal(20, result.SourceCount);
        Assert.NotNull(result.SummaryPointId);
        Assert.True(Guid.TryParse(result.SummaryPointId, out _));
        qdrant.Verify(x => x.DeleteAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 20), It.IsAny<CancellationToken>()), Times.Once);
        qdrant.Verify(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: Insufficient data — returns skipped (non-blocking, ADR-004).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InsufficientData_ReturnsSkippedNonBlocking()
    {
        // Arrange
        var (tool, qdrant, llm, embedding) = CreateTool();
        qdrant.Setup(x => x.GetBatchForCompactAsync("test-project", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEntries(5));

        // Act
        var result = await tool.ExecuteAsync(ValidInput);

        // Assert
        Assert.Equal("skipped", result.Status);
        Assert.Equal("insufficient_data", result.Reason);
        Assert.Equal(5, result.Available);
        Assert.Equal(20, result.Required);
        llm.Verify(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        qdrant.Verify(x => x.DeleteAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        qdrant.Verify(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// [PURPOSE]: LLM timeout — returns error (llm_timeout) — sources NOT deleted (ADR-003 transactional guarantee).
    /// [IMP:2]: LLM timeout caught, [IMP:4]: sources NOT deleted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LlmTimeout_ReturnsErrorSourceNotDeleted()
    {
        // Arrange
        var (tool, qdrant, llm, embedding) = CreateTool();
        qdrant.Setup(x => x.GetBatchForCompactAsync("test-project", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEntries(20));
        llm.Setup(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("LLM timeout"));

        // Act
        var result = await tool.ExecuteAsync(ValidInput);

        // Assert
        Assert.Equal("error", result.Status);
        Assert.Equal("llm_timeout", result.Reason);
        qdrant.Verify(x => x.DeleteAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        qdrant.Verify(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// [PURPOSE]: LLM 5xx — returns error (llm_5xx) — sources NOT deleted (ADR-003 transactional guarantee).
    /// [IMP:2]: LLM 5xx caught, [IMP:4]: sources NOT deleted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Llm5xx_ReturnsErrorSourceNotDeleted()
    {
        // Arrange
        var (tool, qdrant, llm, embedding) = CreateTool();
        qdrant.Setup(x => x.GetBatchForCompactAsync("test-project", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEntries(20));
        llm.Setup(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));

        // Act
        var result = await tool.ExecuteAsync(ValidInput);

        // Assert
        Assert.Equal("error", result.Status);
        Assert.Equal("llm_5xx", result.Reason);
        qdrant.Verify(x => x.DeleteAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        qdrant.Verify(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// [PURPOSE]: Summary entry has correct payload (EntryType=Summary, AgentRole=Orchestrator, tags=[compact,summary]).
    /// [IMP:3]: summary captured with correct payload.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SummaryEntryHasCorrectPayload()
    {
        // Arrange
        var (tool, qdrant, llm, embedding) = CreateTool();
        qdrant.Setup(x => x.GetBatchForCompactAsync("test-project", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEntries(20));
        llm.Setup(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("summary text");
        embedding.Setup(x => x.EmbedAsync("summary text", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[384]);
        MemoryPayload? capturedPayload = null;
        qdrant.Setup(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, float[], MemoryPayload, CancellationToken>((_, _, p, _) => capturedPayload = p)
            .Returns(Task.CompletedTask);
        qdrant.Setup(x => x.DeleteAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await tool.ExecuteAsync(ValidInput);

        // Assert
        Assert.NotNull(capturedPayload);
        Assert.Equal(EntryType.Summary, capturedPayload.EntryType);
        Assert.Equal(AgentRole.Orchestrator, capturedPayload.AgentRole);
        Assert.Contains("compact", capturedPayload.Tags);
        Assert.Contains("summary", capturedPayload.Tags);
        Assert.Equal("summary text", capturedPayload.Content);
    }

    /// <summary>
    /// [PURPOSE]: Empty ProjectId throws ArgumentException (validation, NOT swallowed).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ValidatesEmptyProjectId()
    {
        var (tool, _, _, _) = CreateTool();
        var input = ValidInput with { ProjectId = "" };
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(input));
    }

    /// <summary>
    /// [PURPOSE]: BatchSize=0 throws ArgumentException (validation, NOT swallowed).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ValidatesBatchSizeZero()
    {
        var (tool, _, _, _) = CreateTool();
        var input = ValidInput with { BatchSize = 0 };
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(input));
    }

    /// <summary>
    /// [PURPOSE]: BatchSize=101 throws ArgumentException (validation, NOT swallowed).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ValidatesBatchSizeOver100()
    {
        var (tool, _, _, _) = CreateTool();
        var input = ValidInput with { BatchSize = 101 };
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(input));
    }

    /// <summary>
    /// [PURPOSE]: Embedding is called with the LLM summary text for the summary entry vector.
    /// [IMP:3]: summary vectorization for upsert.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GeneratesEmbeddingForSummary()
    {
        // Arrange
        var (tool, qdrant, llm, embedding) = CreateTool();
        qdrant.Setup(x => x.GetBatchForCompactAsync("test-project", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEntries(20));
        llm.Setup(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("summary text");
        embedding.Setup(x => x.EmbedAsync("summary text", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[384]);
        qdrant.Setup(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        qdrant.Setup(x => x.DeleteAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await tool.ExecuteAsync(ValidInput);

        // Assert
        embedding.Verify(x => x.EmbedAsync("summary text", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: Post-LLM failure (embedding error) — returns error (post_llm_failure) — sources NOT deleted (delete never reached).
    /// [IMP:3]: post-LLM error caught, sources NOT deleted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PostLlmFailure_ReturnsError()
    {
        // Arrange
        var (tool, qdrant, llm, embedding) = CreateTool();
        qdrant.Setup(x => x.GetBatchForCompactAsync("test-project", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEntries(20));
        llm.Setup(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("summary text");
        embedding.Setup(x => x.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Embedding failed"));

        // Act
        var result = await tool.ExecuteAsync(ValidInput);

        // Assert
        Assert.Equal("error", result.Status);
        Assert.Equal("post_llm_failure", result.Reason);
    }
}
