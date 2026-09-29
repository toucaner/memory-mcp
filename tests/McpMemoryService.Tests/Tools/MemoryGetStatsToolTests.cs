#region MODULE_CONTRACT [DOMAIN(Tests): MemoryGetStatsTool unit tests; CONCEPT(MemoryGetStatsToolTests): Mocked IQdrantService + NullLogger; TECH(M7)]
/**
 * [GREP_SUMMARY]: MemoryGetStatsToolTests, GetStatsAsync, ValidInput, EntryTypeFilter, NullFilter, EmptyProjectId
 * [STRUCTURE]: MemoryGetStatsToolTests → 4 Fact methods → new MemoryGetStatsTool(mockQdrant, NullLogger)
 *
 * <summary>
 * [PURPOSE]: Unit tests for MemoryGetStatsTool — validates input, count, and filter passing.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Tests run in-process, NO real Qdrant required.
 * [RATIONALE]: Tests use Mock<IQdrantService> + NullLogger — zero external dependencies.
 * [CHANGES]: LAST_CHANGE: M7 creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Tools;

using FluentAssertions;
using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Resilience;
using McpMemoryService.Services;
using McpMemoryService.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>
/// [PURPOSE]: Unit tests for MemoryGetStatsTool.
/// </summary>
public class MemoryGetStatsToolTests
{
    #region Test fixtures

    private static MemoryGetStatsInput CreateValidInput(
        string projectId = "test-project",
        EntryType? entryType = null)
    {
        return new MemoryGetStatsInput
        {
            ProjectId = projectId,
            EntryType = entryType
        };
    }

    private static Mock<IQdrantService> CreateQdrantMock(int count)
    {
        var mock = new Mock<IQdrantService>();
        mock.Setup(m => m.CountAsync(It.IsAny<string>(), It.IsAny<EntryType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(count);
        return mock;
    }

    #endregion Test fixtures

    #region Tests

    /// <summary>
    /// [PURPOSE]: Valid input returns the count from QdrantService.
    /// </summary>
    [Fact]
    public async Task GetStatsAsync_ValidInput_ReturnsCount()
    {
        // Arrange
        var mockQdrant = CreateQdrantMock(42);
        var logger = NullLogger<MemoryGetStatsTool>.Instance;

        // BUG_FIX_CONTEXT: [HYPOTHESIS: M10 added QdrantResiliencePolicy as ctor param (GetStats has no embedding dependency); old tests constructed with only (qdrant, logger) — CS7036. Pass a real sealed policy instance with NullLogger; on success it passes the count through.]
        var tool = new MemoryGetStatsTool(
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput();

        // Act
        var result = await tool.GetStatsAsync(input);

        // Assert
        result.Count.Should().Be(42);
    }

    /// <summary>
    /// [PURPOSE]: Non-null entry_type filter is passed through to CountAsync.
    /// </summary>
    [Fact]
    public async Task GetStatsAsync_PassesEntryTypeFilter()
    {
        // Arrange
        var mockQdrant = CreateQdrantMock(10);
        var logger = NullLogger<MemoryGetStatsTool>.Instance;

        var tool = new MemoryGetStatsTool(
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput(entryType: EntryType.BugFix);

        // Act
        await tool.GetStatsAsync(input);

        // Assert
        mockQdrant.Verify(
            m => m.CountAsync(It.IsAny<string>(), EntryType.BugFix, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: Null entry_type filter is passed through to CountAsync.
    /// </summary>
    [Fact]
    public async Task GetStatsAsync_NoEntryTypeFilter_PassesNull()
    {
        // Arrange
        var mockQdrant = CreateQdrantMock(5);
        var logger = NullLogger<MemoryGetStatsTool>.Instance;

        var tool = new MemoryGetStatsTool(
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput(entryType: null);

        // Act
        await tool.GetStatsAsync(input);

        // Assert
        mockQdrant.Verify(
            m => m.CountAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// [PURPOSE]: Empty project_id throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task GetStatsAsync_EmptyProjectId_ThrowsValidation()
    {
        // Arrange
        var mockQdrant = CreateQdrantMock(0);
        var logger = NullLogger<MemoryGetStatsTool>.Instance;

        var tool = new MemoryGetStatsTool(
            mockQdrant.Object,
            new QdrantResiliencePolicy(NullLogger<QdrantResiliencePolicy>.Instance),
            logger);

        var input = CreateValidInput(projectId: "");

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.GetStatsAsync(input));
    }

    #endregion Tests
}
