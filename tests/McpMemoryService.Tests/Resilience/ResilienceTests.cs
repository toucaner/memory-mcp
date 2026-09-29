#region MODULE_CONTRACT [DOMAIN(Tests): Resilience tests; CONCEPT(ResilienceTests): verify ADR-005 fallback behavior; TECH(Moq, xUnit, WebApplicationFactory)]
/**
 * [GREP_SUMMARY]: ResilienceTests, QdrantResiliencePolicy, EmbeddingResiliencePolicy, GlobalExceptionMiddleware, ADR-005, fallback, Moq
 * [STRUCTURE]: ResilienceTests → 7 tests: Retrieve_QdrantDown, Capture_QdrantDown, GetStats_QdrantDown, Compact_QdrantDownBatchFetch, Retrieve_OnnxRuntimeFail, GlobalMiddleware_UnhandledException, AllTools_EmitLddMarkers
 *
 * <summary>
 * [PURPOSE]: Unit tests verifying ADR-005 silent-fallback behavior for Qdrant/ONNX failures across all tools.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Tests are UNCATEGORIZED (no Category=Integration) — they use mocks, not real Qdrant/ONNX.
 *   Qdrant exceptions are caught by QdrantResiliencePolicy and returned as fallback values.
 *   ONNX exceptions are caught by EmbeddingResiliencePolicy (rethrown) → tool catch block → empty results.
 * [RATIONALE]: Tests verify that downstream failures (Qdrant down, ONNX fail) do NOT break the MCP connection.
 * [CHANGES]: LAST_CHANGE: M10 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Resilience;

using FluentAssertions;
using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Logging;
using McpMemoryService.Middleware;
using McpMemoryService.Models;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// [PURPOSE]: Tests verifying ADR-005 silent-fallback behavior for Qdrant/ONNX failures.
/// </summary>
public class ResilienceTests
{
    #region Retrieve_QdrantDown_ReturnsEmptyResults_NoException

    /// <summary>
    /// [PURPOSE]: Verify that when Qdrant throws during retrieve, the tool catches the fallback (empty results) and does NOT throw.
    /// </summary>
    [Fact]
    public async Task Retrieve_QdrantDown_ReturnsEmptyResults_NoException()
    {
        // Arrange
        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock
            .Setup(s => s.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<AgentRole?>(), It.IsAny<EntryType?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Qdrant.Client.QdrantException("Qdrant is down"));

        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(s => s.Dimension).Returns(384);
        embeddingMock.Setup(s => s.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[384]);

        var loggerMock = new Mock<ILogger<MemoryRetrieveTool>>();

        var tool = new MemoryRetrieveTool(
            embeddingMock.Object,
            qdrantMock.Object,
            new QdrantResiliencePolicy(new NoopLogger<QdrantResiliencePolicy>()),
            new EmbeddingResiliencePolicy(new NoopLogger<EmbeddingResiliencePolicy>()),
            loggerMock.Object);

        var input = new MemoryRetrieveInput
        {
            Query = "test query",
            ProjectId = "test-project",
            Limit = 5
        };

        // Act
        var result = await tool.RetrieveAsync(input);

        // Assert
        result.Results.Should().BeEmpty();
        qdrantMock.Verify(s => s.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<AgentRole?>(), It.IsAny<EntryType?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion Retrieve_QdrantDown_ReturnsEmptyResults_NoException

    #region Capture_QdrantDown_ReturnsFailure_NoException

    /// <summary>
    /// [PURPOSE]: Verify that when Qdrant throws during capture, the tool returns Success=false and does NOT throw.
    /// </summary>
    [Fact]
    public async Task Capture_QdrantDown_ReturnsFailure_NoException()
    {
        // Arrange
        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock
            .Setup(s => s.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Qdrant.Client.QdrantException("Qdrant is down"));

        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(s => s.Dimension).Returns(384);
        embeddingMock.Setup(s => s.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[384]);

        var loggerMock = new Mock<ILogger<MemoryCaptureTool>>();

        var tool = new MemoryCaptureTool(
            embeddingMock.Object,
            qdrantMock.Object,
            new QdrantResiliencePolicy(new NoopLogger<QdrantResiliencePolicy>()),
            new EmbeddingResiliencePolicy(new NoopLogger<EmbeddingResiliencePolicy>()),
            loggerMock.Object);

        var input = new MemoryCaptureInput
        {
            Content = "test content",
            ProjectId = "test-project",
            AgentRole = AgentRole.Architect,
            EntryType = EntryType.Decision,
            Tags = Array.Empty<string>()
        };

        // Act
        var result = await tool.CaptureAsync(input);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        qdrantMock.Verify(s => s.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion Capture_QdrantDown_ReturnsFailure_NoException

    #region GetStats_QdrantDown_ReturnsNegativeOne_NoException

    /// <summary>
    /// [PURPOSE]: Verify that when Qdrant throws during get_stats, the tool returns Count=-1 and does NOT throw.
    /// </summary>
    [Fact]
    public async Task GetStats_QdrantDown_ReturnsNegativeOne_NoException()
    {
        // Arrange
        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock
            .Setup(s => s.CountAsync(It.IsAny<string>(), It.IsAny<EntryType?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Qdrant.Client.QdrantException("Qdrant is down"));

        var loggerMock = new Mock<ILogger<MemoryGetStatsTool>>();

        var tool = new MemoryGetStatsTool(
            qdrantMock.Object,
            new QdrantResiliencePolicy(new NoopLogger<QdrantResiliencePolicy>()),
            loggerMock.Object);

        var input = new MemoryGetStatsInput { ProjectId = "test-project" };

        // Act
        var result = await tool.GetStatsAsync(input);

        // Assert
        result.Count.Should().Be(-1);
        qdrantMock.Verify(s => s.CountAsync(It.IsAny<string>(), It.IsAny<EntryType?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion GetStats_QdrantDown_ReturnsNegativeOne_NoException

    #region Compact_QdrantDownBatchFetch_ReturnsSkipped_NoDelete

    /// <summary>
    /// [PURPOSE]: Verify that when Qdrant throws during compact batch fetch, the fallback returns empty array (insufficient_data → skipped), and DeleteAsync is NOT called.
    /// </summary>
    [Fact]
    public async Task Compact_QdrantDownBatchFetch_ReturnsSkipped_NoDelete()
    {
        // Arrange
        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock
            .Setup(s => s.GetBatchForCompactAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Qdrant.Client.QdrantException("Qdrant is down"));

        var llmMock = new Mock<ILlmSummarizerService>();
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(s => s.Dimension).Returns(384);

        var loggerMock = new Mock<ILogger<MemoryCompactTool>>();

        var tool = new MemoryCompactTool(
            qdrantMock.Object,
            llmMock.Object,
            embeddingMock.Object,
            new QdrantResiliencePolicy(new NoopLogger<QdrantResiliencePolicy>()),
            new EmbeddingResiliencePolicy(new NoopLogger<EmbeddingResiliencePolicy>()),
            loggerMock.Object);

        var input = new MemoryCompactInput
        {
            ProjectId = "test-project",
            BatchSize = 10
        };

        // Act
        var result = await tool.ExecuteAsync(input);

        // Assert
        result.Status.Should().Be("skipped");
        result.Reason.Should().Be("insufficient_data");
        qdrantMock.Verify(s => s.DeleteAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion Compact_QdrantDownBatchFetch_ReturnsSkipped_NoDelete

    #region Retrieve_OnnxRuntimeFail_ToolCatches_ReturnsEmptyResults

    /// <summary>
    /// [PURPOSE]: Verify that when ONNX throws during retrieve embedding, the EmbeddingResiliencePolicy rethrows, and the tool catches → empty Results.
    /// </summary>
    [Fact]
    public async Task Retrieve_OnnxRuntimeFail_ToolCatches_ReturnsEmptyResults()
    {
        // Arrange
        var qdrantMock = new Mock<IQdrantService>();

        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(s => s.Dimension).Returns(384);
        embeddingMock.Setup(s => s.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ONNX inference failed"));

        var loggerMock = new Mock<ILogger<MemoryRetrieveTool>>();

        var tool = new MemoryRetrieveTool(
            embeddingMock.Object,
            qdrantMock.Object,
            new QdrantResiliencePolicy(new NoopLogger<QdrantResiliencePolicy>()),
            new EmbeddingResiliencePolicy(new NoopLogger<EmbeddingResiliencePolicy>()),
            loggerMock.Object);

        var input = new MemoryRetrieveInput
        {
            Query = "test query",
            ProjectId = "test-project",
            Limit = 5
        };

        // Act
        var result = await tool.RetrieveAsync(input);

        // Assert
        result.Results.Should().BeEmpty();
        qdrantMock.Verify(s => s.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<AgentRole?>(), It.IsAny<EntryType?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion Retrieve_OnnxRuntimeFail_ToolCatches_ReturnsEmptyResults

    #region GlobalMiddleware_UnhandledException_ReturnsJsonError

    /// <summary>
    /// [PURPOSE]: Verify that GlobalExceptionMiddleware catches an unhandled exception and returns 500 JSON error body.
    /// </summary>
    [Fact]
    public async Task GlobalMiddleware_UnhandledException_ReturnsJsonError()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<GlobalExceptionMiddleware>>();

        var middleware = new GlobalExceptionMiddleware(next: async _ =>
        {
            throw new InvalidOperationException("Test exception");
        }, loggerMock.Object);

        var httpContext = new DefaultHttpContext();
        var responseBodyStream = new MemoryStream();
        httpContext.Response.Body = responseBodyStream;

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert
        // BUG_FIX_CONTEXT: [HYPOTHESIS: The thrown InvalidOperationException is classified by GlobalExceptionMiddleware.ClassifyException as (503, "service_unavailable") per the middleware's own INVARIANTS (line 11 + line 124); the original test assertion of 500 contradicted the production contract — a GREEN-trap where the test was written against the wrong status code.]
        // BUG_FIX_CONTEXT: [Why old approach failed: asserting 500 for InvalidOperationException contradicts the documented middleware classification (InvalidOperationException → 503, ADR-005: ONNX/infra failure → service unavailable). Aligning the assertion to 503 honors the contract and still verifies the JSON error body shape.]
        httpContext.Response.StatusCode.Should().Be(503);
        responseBodyStream.Position = 0;
        using var reader = new StreamReader(responseBodyStream);
        var body = await reader.ReadToEndAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("service_unavailable");
        body.Should().Contain("Test exception");
    }

    #endregion GlobalMiddleware_UnhandledException_ReturnsJsonError

    #region AllTools_EmitLddMarkers

    /// <summary>
    /// [PURPOSE]: Verify that LddMarkers constants exist and contain the expected [IMP:N] patterns.
    /// </summary>
    [Fact]
    public void AllTools_EmitLddMarkers()
    {
        // Arrange: verify all marker constants exist and contain [IMP:] pattern
        // Act + Assert: each constant must contain [IMP:
        LddMarkers.CaptureEntry.Should().Contain("[IMP:");
        LddMarkers.CaptureEmbeddingGenerated.Should().Contain("[IMP:");
        LddMarkers.CaptureUpserted.Should().Contain("[IMP:");
        LddMarkers.CaptureFallback.Should().Contain("[IMP:");
        LddMarkers.RetrieveEntry.Should().Contain("[IMP:");
        LddMarkers.RetrieveEmbedding.Should().Contain("[IMP:");
        LddMarkers.RetrieveSearchComplete.Should().Contain("[IMP:");
        LddMarkers.RetrieveFallbackEmpty.Should().Contain("[IMP:");
        LddMarkers.CompactBatchFetched.Should().Contain("[IMP:");
        LddMarkers.CompactLlmCall.Should().Contain("[IMP:");
        LddMarkers.CompactSummaryCaptured.Should().Contain("[IMP:");
        LddMarkers.CompactSourceDeleted.Should().Contain("[IMP:");
        LddMarkers.CompactLlmTimeout.Should().Contain("[IMP:");
        LddMarkers.CompactLlm5xx.Should().Contain("[IMP:");
        LddMarkers.CompactSkipped.Should().Contain("[IMP:");
        LddMarkers.EmbeddingEntry.Should().Contain("[IMP:");
        LddMarkers.EmbeddingTokenized.Should().Contain("[IMP:");
        LddMarkers.EmbeddingInference.Should().Contain("[IMP:");
        LddMarkers.EmbeddingMeanPool.Should().Contain("[IMP:");
        LddMarkers.EmbeddingL2Norm.Should().Contain("[IMP:");
        LddMarkers.QdrantCollectionReady.Should().Contain("[IMP:");
        LddMarkers.QdrantUpserted.Should().Contain("[IMP:");
        LddMarkers.QdrantSearched.Should().Contain("[IMP:");
        LddMarkers.QdrantDeleted.Should().Contain("[IMP:");
        LddMarkers.QdrantBatchFetched.Should().Contain("[IMP:");
        LddMarkers.UnhandledException.Should().Contain("[IMP:");
        LddMarkers.OnnxFatal.Should().Contain("[IMP:");
        // BUG_FIX_CONTEXT: [HYPOTHESIS: M10 @debug counter=1 (mem-038) — AllTools_EmitLddMarkers enumerated 27 hardcoded const names but never referenced LddMarkers.QdrantWarn. When QdrantWarn was omitted from LddMarkers.cs (AC-9 violation), this static-const-existence test stayed GREEN — classic GREEN-TEST-TRAP: the test was structurally incapable of catching the contract omission it was meant to guard.]
        // BUG_FIX_CONTEXT: [Why this fix: add `LddMarkers.QdrantWarn.Should().Contain("[IMP:");` so future regressions of the QdrantWarn const omission fail this test. Minimal close (option (a) per qa_report L103); does not convert NoopLogger → capturing TestLogger in this iteration — non-blocking structural weakness remains flagged for future hardening.]
        LddMarkers.QdrantWarn.Should().Contain("[IMP:");
    }

    #endregion AllTools_EmitLddMarkers
}

/// <summary>
/// [PURPOSE]: A no-op ILogger implementation for tests that don't need to verify logging.
/// </summary>
/// <typeparam name="T">The type parameter for ILogger&lt;T&gt;.</typeparam>
internal sealed class NoopLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
