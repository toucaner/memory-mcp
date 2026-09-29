#region MODULE_CONTRACT [DOMAIN(Tests): MemoryCaptureTool unit tests; CONCEPT(MemoryCaptureToolTests): Mocked IEmbeddingService+IQdrantService + NullLogger; TECH(M7)]
/**
 * [GREP_SUMMARY]: MemoryCaptureToolTests, CaptureAsync, ValidInput, Embedding, GUIDv4, QdrantFails, SummaryRejected, EmptyContent
 * [STRUCTURE]: MemoryCaptureToolTests → 6 Fact methods → new MemoryCaptureTool(mockEmbedding, mockQdrant, NullLogger)
 *
 * <summary>
 * [PURPOSE]: Unit tests for MemoryCaptureTool — validates input, embedding generation, GUID v4, and Qdrant failure handling.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Tests run in-process, NO real Qdrant/ONNX/Docker required.
 * [RATIONALE]: Tests use Mock<IEmbeddingService> + Mock<IQdrantService> + NullLogger — zero external dependencies.
 * [CHANGES]: LAST_CHANGE: M7 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Tools;

using FluentAssertions;
using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Models;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>
/// [PURPOSE]: Unit tests for MemoryCaptureTool.
/// </summary>
public class MemoryCaptureToolTests
{
    #region Test fixtures

    private static MemoryCaptureInput CreateValidInput(
        string content = "Test content",
        string projectId = "test-project",
        AgentRole agentRole = AgentRole.Code,
        EntryType entryType = EntryType.Decision)
    {
        return new MemoryCaptureInput
        {
            Content = content,
            ProjectId = projectId,
            AgentRole = agentRole,
            EntryType = entryType,
            Tags = Array.Empty<string>(),
            SessionId = "test-session"
        };
    }

    private static Mock<IEmbeddingService> CreateEmbeddingMock(float[] vector)
    {
        var mock = new Mock<IEmbeddingService>();
        mock.Setup(m => m.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(vector);
        mock.Setup(m => m.Dimension).Returns(384);
        return mock;
    }

    #endregion Test fixtures

    #region Tests

    /// <summary>
    /// [PURPOSE]: Valid input returns success with a valid point ID.
    /// </summary>
    [Fact]
    public async Task CaptureAsync_ValidInput_ReturnsSuccessWithPointId()
    {
        // Arrange
        var mockEmbedding = CreateEmbeddingMock(new float[384]);
        var mockQdrant = new Mock<IQdrantService>();
        var logger = NullLogger<MemoryCaptureTool>.Instance;

        // BUG_FIX_CONTEXT: [HYPOTHESIS: M10 added QdrantResiliencePolicy + EmbeddingResiliencePolicy as ctor params; old tests constructed with only (embedding, qdrant, logger) — CS7036. Pass real sealed policy instances with NullLogger; on success the policies pass through, so existing success-path assertions hold.]
        var tool = new MemoryCaptureTool(
            mockEmbedding.Object,
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput();

        // Act
        var result = await tool.CaptureAsync(input);

        // Assert
        result.Success.Should().BeTrue();
        result.PointId.Should().NotBeNullOrEmpty();
        result.Error.Should().BeNull();
        mockQdrant.Verify(x => x.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: CaptureAsync calls EmbedAsync with the content string.
    /// </summary>
    [Fact]
    public async Task CaptureAsync_GeneratesEmbeddingFromContent()
    {
        // Arrange
        const string content = "test content";
        var mockEmbedding = CreateEmbeddingMock(new float[384]);
        var mockQdrant = new Mock<IQdrantService>();
        var logger = NullLogger<MemoryCaptureTool>.Instance;

        var tool = new MemoryCaptureTool(
            mockEmbedding.Object,
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput(content: content);

        // Act
        await tool.CaptureAsync(input);

        // Assert
        mockEmbedding.Verify(m => m.EmbedAsync(content, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: PointId is a valid GUID v4 (version nibble = 4, variant nibble = 8/9/a/b).
    /// </summary>
    [Fact]
    public async Task CaptureAsync_GeneratesGuidV4PointId()
    {
        // Arrange
        var mockEmbedding = CreateEmbeddingMock(new float[384]);
        var mockQdrant = new Mock<IQdrantService>();
        var logger = NullLogger<MemoryCaptureTool>.Instance;

        var tool = new MemoryCaptureTool(
            mockEmbedding.Object,
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput();

        // Act
        var result = await tool.CaptureAsync(input);

        // Assert
        var guid = Guid.Parse(result.PointId);
        guid.Version.Should().Be(4);
        // Variant nibble (bits 6-7 of byte at index 8) should be 10xx (8/9/a/b)
        var variantNibble = (guid.ToString()[19]) | 0x20; // lowercase + mask
        variantNibble.Should().BeOneOf('8', '9', 'a', 'b');
    }

    /// <summary>
    /// [PURPOSE]: Qdrant failure returns Success=false (does NOT throw).
    /// </summary>
    [Fact]
    public async Task CaptureAsync_QdrantFails_ReturnsFailureNotThrow()
    {
        // Arrange
        var mockEmbedding = CreateEmbeddingMock(new float[384]);
        var mockQdrant = new Mock<IQdrantService>();
        var logger = NullLogger<MemoryCaptureTool>.Instance;

        mockQdrant.Setup(m => m.UpsertAsync(It.IsAny<Guid>(), It.IsAny<float[]>(), It.IsAny<MemoryPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Qdrant is down"));

        var tool = new MemoryCaptureTool(
            mockEmbedding.Object,
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput();

        // Act
        var result = await tool.CaptureAsync(input);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        // BUG_FIX_CONTEXT: [HYPOTHESIS: With QdrantResiliencePolicy wrapping the upsert, the policy swallows the thrown InvalidOperationException and returns upsertOk=false; the tool then emits the stable sentinel "Qdrant upsert failed" rather than propagating the raw exception text (ADR-005 silent-fallback at tool boundary).]
        // BUG_FIX_CONTEXT: [Why old approach failed: old assertion .Contain("Qdrant is down") expected the raw exception message; the policy now decouples the tool from upstream exception text. Asserting on "upsert failed" preserves the test's intent (failure result, no throw) without coupling to policy internals.]
        result.Error.Should().Contain("upsert failed");
    }

    /// <summary>
    /// [PURPOSE]: entry_type=Summary throws ArgumentException (ADR-001).
    /// </summary>
    [Fact]
    public async Task CaptureAsync_EntryTypeSummary_ThrowsValidation()
    {
        // Arrange
        var mockEmbedding = CreateEmbeddingMock(new float[384]);
        var mockQdrant = new Mock<IQdrantService>();
        var logger = NullLogger<MemoryCaptureTool>.Instance;

        var tool = new MemoryCaptureTool(
            mockEmbedding.Object,
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput(entryType: EntryType.Summary);

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.CaptureAsync(input));
    }

    /// <summary>
    /// [PURPOSE]: Empty content throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task CaptureAsync_EmptyContent_ThrowsValidation()
    {
        // Arrange
        var mockEmbedding = CreateEmbeddingMock(new float[384]);
        var mockQdrant = new Mock<IQdrantService>();
        var logger = NullLogger<MemoryCaptureTool>.Instance;

        var tool = new MemoryCaptureTool(
            mockEmbedding.Object,
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            new EmbeddingResiliencePolicy(NullLogger<EmbeddingResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput(content: "");

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.CaptureAsync(input));
    }

    #endregion Tests
}
