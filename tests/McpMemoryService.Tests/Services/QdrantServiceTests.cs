#region MODULE_CONTRACT [DOMAIN(Test): Qdrant service + mapping verification; CONCEPT(QdrantServiceTests): Unit mapping + Integration CRUD; TECH(xUnit, Qdrant.Client.Grpc, IAsyncLifetime)]
/**
 * [GREP_SUMMARY]: QdrantServiceTests, xUnit, QdrantService, mapping, Value, RetrievedPoint, ScoredPoint, Category=Integration, CRUD, round-trip
 * [STRUCTURE]: QdrantServiceMappingTests (5 unit) + QdrantServiceIntegrationTests (7 integration, IAsyncLifetime)
 *
 * <summary>
 * [PURPOSE]: Unit tests for mapping extensions (Value types) and integration tests for QdrantService CRUD round-trips.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Unit tests construct protobuf messages manually (no external deps).
 *   Integration tests require Docker Qdrant at localhost:6334 (gRPC).
 *   Integration tests use unique collection names per run and clean up in DisposeAsync.
 * [CHANGES]: LAST_CHANGE: M5 — initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Services;

using System.Globalization;
using System.Text.Json;
using McpMemoryService.Configuration;
using McpMemoryService.Enums;
using McpMemoryService.Mapping;
using McpMemoryService.Models;
using McpMemoryService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;

/// <summary>
/// [PURPOSE]: Unit tests for PayloadMappingExtensions with real Qdrant.Client.Grpc Value types.
/// </summary>
public class QdrantServiceMappingTests
{
    /// <summary>
    /// [PURPOSE]: ToQdrantPayload must map all MemoryPayload fields to Value types with correct keys.
    /// </summary>
    [Fact]
    public void ToQdrantPayload_MapsAllFields()
    {
        var payload = new MemoryPayload
        {
            ProjectId = "proj-1",
            SessionId = "sess-1",
            AgentRole = AgentRole.Code,
            EntryType = EntryType.BugFix,
            Timestamp = DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
            Content = "Found null reference in service",
            Tags = new List<string> { "critical", "runtime" },
            Metadata = new Metadata { FilePath = "Service.cs", ErrorCode = "NullRef", Session = "debug-1" }
        };

        var dict = payload.ToQdrantPayload();

        Assert.Equal(8, dict.Count);
        Assert.Equal("proj-1", dict["project_id"].StringValue);
        Assert.Equal("sess-1", dict["session_id"].StringValue);
        Assert.Equal("code", dict["agent_role"].StringValue);
        Assert.Equal("bug_fix", dict["entry_type"].StringValue);
        Assert.Equal("Found null reference in service", dict["content"].StringValue);

        var tags = dict["tags"].ListValue.Values.Select(v => v.StringValue).ToList();
        Assert.Equal(new List<string> { "critical", "runtime" }, tags);

        // BUG_FIX_CONTEXT: [Value type has no HasStructValue; check KindCase == StructValue instead]
        Assert.True(dict["metadata"].KindCase == Value.KindOneofCase.StructValue);
        Assert.Equal("Service.cs", dict["metadata"].StructValue.Fields["file_path"].StringValue);
        Assert.Equal("NullRef", dict["metadata"].StructValue.Fields["error_code"].StringValue);
        Assert.Equal("debug-1", dict["metadata"].StructValue.Fields["session"].StringValue);
    }

    /// <summary>
    /// [PURPOSE]: ToPayload must correctly parse snake_case enum values from Value types.
    /// </summary>
    [Fact]
    public void ToPayload_MapsSnakeCaseEnums()
    {
        var qdrantPayload = new Dictionary<string, Value>
        {
            ["project_id"] = new Value { StringValue = "test" },
            ["session_id"] = new Value { StringValue = "s1" },
            ["agent_role"] = new Value { StringValue = "debug" },
            ["entry_type"] = new Value { StringValue = "bug_fix" },
            ["timestamp"] = new Value { StringValue = "2026-07-01T00:00:00+00:00" },
            ["content"] = new Value { StringValue = "content" },
            ["tags"] = new Value { ListValue = new ListValue() }
        };

        var result = qdrantPayload.ToPayload();

        Assert.Equal(AgentRole.Debug, result.AgentRole);
        Assert.Equal(EntryType.BugFix, result.EntryType);
    }

    /// <summary>
    /// [PURPOSE]: Round-trip: payload → ToQdrantPayload → ToPayload must preserve all fields.
    /// </summary>
    [Fact]
    public void ToPayload_RoundTrip_PreservesAllFields()
    {
        var original = new MemoryPayload
        {
            ProjectId = "proj-rt",
            SessionId = "sess-rt",
            AgentRole = AgentRole.Architect,
            EntryType = EntryType.Decision,
            Timestamp = DateTimeOffset.Parse("2026-06-30T12:00:00Z"),
            Content = "Architecture decision text",
            Tags = new List<string> { "design", "api" },
            Metadata = new Metadata { FilePath = "Plan.md", ErrorCode = null, Session = "arch-1" }
        };

        var dict = original.ToQdrantPayload();
        var restored = dict.ToPayload();

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
    /// [PURPOSE]: ToEntry from RetrievedPoint must map correctly with Score = null.
    /// </summary>
    [Fact]
    public void ToEntry_FromRetrievedPoint()
    {
        var point = new RetrievedPoint
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString() },
            Payload =
            {
                ["project_id"] = new Value { StringValue = "proj-rp" },
                ["session_id"] = new Value { StringValue = "sess-rp" },
                ["agent_role"] = new Value { StringValue = "code" },
                ["entry_type"] = new Value { StringValue = "insight" },
                ["timestamp"] = new Value { StringValue = "2026-07-01T10:00:00+00:00" },
                ["content"] = new Value { StringValue = "Retrieved point content" },
                ["tags"] = new Value { ListValue = new ListValue() }
            },
            // BUG_FIX_CONTEXT: [RetrievedPoint.Vectors is VectorsOutput (read type). VectorsOutput.Vector is VectorOutput. Use Dense.Data.]
            Vectors = new VectorsOutput
            {
                Vector = new VectorOutput
                {
                    Dense = new DenseVector { Data = { Enumerable.Range(0, 384).Select(_ => 0.1f) } }
                }
            }
        };

        var entry = point.ToEntry(withVector: true);

        Assert.Equal(Guid.Parse(point.Id.Uuid), entry.PointId);
        Assert.Equal("proj-rp", entry.Payload.ProjectId);
        Assert.Equal(EntryType.Insight, entry.Payload.EntryType);
        Assert.Equal(384, entry.Vector.Length);
        Assert.Null(entry.Score);
    }

    /// <summary>
    /// [PURPOSE]: ToEntry from ScoredPoint must map correctly with Score from search result.
    /// </summary>
    [Fact]
    public void ToEntry_FromScoredPoint()
    {
        var point = new ScoredPoint
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString() },
            Score = 0.95f,
            Payload =
            {
                ["project_id"] = new Value { StringValue = "proj-sp" },
                ["session_id"] = new Value { StringValue = "sess-sp" },
                ["agent_role"] = new Value { StringValue = "qa" },
                ["entry_type"] = new Value { StringValue = "requirement" },
                ["timestamp"] = new Value { StringValue = "2026-07-01T11:00:00+00:00" },
                ["content"] = new Value { StringValue = "Scored point content" },
                ["tags"] = new Value { ListValue = new ListValue() }
            },
            // BUG_FIX_CONTEXT: [ScoredPoint.Vectors is VectorsOutput (read type). VectorsOutput.Vector is VectorOutput. Use Dense.Data.]
            Vectors = new VectorsOutput
            {
                Vector = new VectorOutput
                {
                    Dense = new DenseVector { Data = { Enumerable.Range(0, 384).Select(_ => 0.2f) } }
                }
            }
        };

        var entry = point.ToEntry();

        Assert.Equal(Guid.Parse(point.Id.Uuid), entry.PointId);
        Assert.Equal("proj-sp", entry.Payload.ProjectId);
        Assert.Equal(AgentRole.Qa, entry.Payload.AgentRole);
        Assert.Equal(EntryType.Requirement, entry.Payload.EntryType);
        Assert.Equal(384, entry.Vector.Length);
        Assert.Equal(0.95f, entry.Score);
    }
}

/// <summary>
/// [PURPOSE]: Integration tests for QdrantService CRUD round-trips (requires Docker Qdrant at localhost:6334).
/// </summary>
/// <remarks>
/// [INVARIANTS]: All tests use a unique collection name per test run (prefixed "test_memory_").
///   Collection is created in InitializeAsync and deleted in DisposeAsync.
///   These tests require Docker Qdrant running: docker run -d --rm -p 6333:6333 -p 6334:6334 qdrant/qdrant
/// </remarks>
[Trait("Category", "Integration")]
public class QdrantServiceIntegrationTests : IAsyncLifetime
{
    private readonly QdrantClient _client;
    private readonly QdrantService _service;
    private readonly string _testCollectionName;

    public QdrantServiceIntegrationTests()
    {
        _testCollectionName = "test_memory_" + Guid.NewGuid().ToString("N")[..8];

        var options = Options.Create(new QdrantOptions
        {
            Url = "http://localhost:6333",
            GrpcPort = 6334,
            CollectionName = _testCollectionName
        });

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<QdrantService>();

        _service = new QdrantService(options, logger);
        _client = new QdrantClient("localhost", port: 6334);
    }

    public async Task InitializeAsync()
    {
        await _service.EnsureCollectionExistsAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _client.DeleteCollectionAsync(_testCollectionName);
        }
        catch
        {
            // Best-effort cleanup — ignore if collection already gone
        }
    }

    /// <summary>
    /// [PURPOSE]: EnsureCollectionExistsAsync must create the collection with 384-dim DotProduct and 4 keyword indexes.
    /// </summary>
    [Fact]
    public async Task EnsureCollectionExistsAsync_CreatesCollectionWithIndexes()
    {
        // Collection should already exist (created in InitializeAsync)
        var exists = await _client.CollectionExistsAsync(_testCollectionName);
        Assert.True(exists);

        var info = await _client.GetCollectionInfoAsync(_testCollectionName);
        Assert.NotNull(info);
        Assert.Equal((ulong)384, info.Config.Params.VectorsConfig.Params.Size);
        // BUG_FIX_CONTEXT: [Distance enum member is "Dot", not "DotProduct" per Qdrant.Client 1.18.1]
        Assert.Equal(Distance.Dot, info.Config.Params.VectorsConfig.Params.Distance);
    }

    /// <summary>
    /// [PURPOSE]: Upsert then Search must round-trip preserving all payload fields.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_SearchAsync_RoundTrip()
    {
        var pointId = Guid.NewGuid();
        var vector = Enumerable.Range(0, 384).Select(i => (float)Math.Sin(i * 0.01)).ToArray();
        var payload = new MemoryPayload
        {
            ProjectId = "round-trip-proj",
            SessionId = "rt-session",
            AgentRole = AgentRole.Code,
            EntryType = EntryType.BugFix,
            Timestamp = DateTimeOffset.Parse("2026-07-01T12:00:00+00:00"),
            Content = "Null reference exception in QdrantService",
            Tags = new List<string> { "critical", "runtime" },
            Metadata = new Metadata { FilePath = "QdrantService.cs", ErrorCode = "NullRef", Session = "debug-1" }
        };

        await _service.UpsertAsync(pointId, vector, payload);

        // Wait for index consistency
        await Task.Delay(500);

        var results = await _service.SearchAsync(vector, "round-trip-proj", limit: 1);

        Assert.Single(results);
        var entry = results[0];
        Assert.Equal(pointId, entry.PointId);
        Assert.Equal("round-trip-proj", entry.Payload.ProjectId);
        Assert.Equal("rt-session", entry.Payload.SessionId);
        Assert.Equal(AgentRole.Code, entry.Payload.AgentRole);
        Assert.Equal(EntryType.BugFix, entry.Payload.EntryType);
        Assert.Equal("Null reference exception in QdrantService", entry.Payload.Content);
        Assert.Equal(new List<string> { "critical", "runtime" }, entry.Payload.Tags);
        Assert.NotNull(entry.Payload.Metadata);
        Assert.Equal("QdrantService.cs", entry.Payload.Metadata!.FilePath);
        Assert.Equal("NullRef", entry.Payload.Metadata.ErrorCode);
        Assert.Equal("debug-1", entry.Payload.Metadata.Session);
        Assert.NotNull(entry.Score);
        Assert.True(entry.Score > 0.9f); // L2-normalized identical vector should score > 0.9 with DotProduct
    }

    /// <summary>
    /// [PURPOSE]: Search must filter by project_id — only matching points returned.
    /// </summary>
    [Fact]
    public async Task SearchAsync_FiltersByProjectId()
    {
        var vector = Enumerable.Range(0, 384).Select(i => 0.01f * i).ToArray();

        await _service.UpsertAsync(Guid.NewGuid(), vector, new MemoryPayload
        {
            ProjectId = "proj-A", SessionId = "s1", AgentRole = AgentRole.Code,
            EntryType = EntryType.Decision, Timestamp = DateTimeOffset.UtcNow,
            Content = "Decision A", Tags = Array.Empty<string>()
        });

        await _service.UpsertAsync(Guid.NewGuid(), vector, new MemoryPayload
        {
            ProjectId = "proj-B", SessionId = "s2", AgentRole = AgentRole.Code,
            EntryType = EntryType.Decision, Timestamp = DateTimeOffset.UtcNow,
            Content = "Decision B", Tags = Array.Empty<string>()
        });

        await Task.Delay(500);

        var resultsA = await _service.SearchAsync(vector, "proj-A", limit: 10);
        var resultsB = await _service.SearchAsync(vector, "proj-B", limit: 10);

        Assert.All(resultsA, r => Assert.Equal("proj-A", r.Payload.ProjectId));
        Assert.All(resultsB, r => Assert.Equal("proj-B", r.Payload.ProjectId));
    }

    /// <summary>
    /// [PURPOSE]: Search must filter by entry_type — only matching types returned.
    /// </summary>
    [Fact]
    public async Task SearchAsync_FiltersByEntryType()
    {
        var vector = Enumerable.Range(0, 384).Select(i => 0.02f * i).ToArray();

        await _service.UpsertAsync(Guid.NewGuid(), vector, new MemoryPayload
        {
            ProjectId = "etype-proj", SessionId = "s1", AgentRole = AgentRole.Code,
            EntryType = EntryType.Decision, Timestamp = DateTimeOffset.UtcNow,
            Content = "Decision entry", Tags = Array.Empty<string>()
        });

        await _service.UpsertAsync(Guid.NewGuid(), vector, new MemoryPayload
        {
            ProjectId = "etype-proj", SessionId = "s2", AgentRole = AgentRole.Debug,
            EntryType = EntryType.BugFix, Timestamp = DateTimeOffset.UtcNow,
            Content = "Bug fix entry", Tags = Array.Empty<string>()
        });

        await Task.Delay(500);

        var decisions = await _service.SearchAsync(vector, "etype-proj", entryTypeFilter: EntryType.Decision, limit: 10);
        var bugfixes = await _service.SearchAsync(vector, "etype-proj", entryTypeFilter: EntryType.BugFix, limit: 10);

        Assert.All(decisions, r => Assert.Equal(EntryType.Decision, r.Payload.EntryType));
        Assert.All(bugfixes, r => Assert.Equal(EntryType.BugFix, r.Payload.EntryType));
    }

    /// <summary>
    /// [PURPOSE]: Count must return the correct number of points matching the filter.
    /// </summary>
    [Fact]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        var vector = Enumerable.Range(0, 384).Select(i => 0.03f * i).ToArray();

        for (int i = 0; i < 5; i++)
        {
            await _service.UpsertAsync(Guid.NewGuid(), vector, new MemoryPayload
            {
                ProjectId = "count-proj", SessionId = $"s-{i}", AgentRole = AgentRole.Code,
                EntryType = EntryType.Insight, Timestamp = DateTimeOffset.UtcNow,
                Content = $"Insight {i}", Tags = Array.Empty<string>()
            });
        }

        await Task.Delay(500);

        var count = await _service.CountAsync("count-proj");
        Assert.Equal(5, count);
    }

    /// <summary>
    /// [PURPOSE]: Delete must remove points from the collection.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_RemovesPoints()
    {
        var pointId = Guid.NewGuid();
        var vector = Enumerable.Range(0, 384).Select(i => 0.04f * i).ToArray();

        await _service.UpsertAsync(pointId, vector, new MemoryPayload
        {
            ProjectId = "del-proj", SessionId = "s1", AgentRole = AgentRole.Code,
            EntryType = EntryType.Requirement, Timestamp = DateTimeOffset.UtcNow,
            Content = "To be deleted", Tags = Array.Empty<string>()
        });

        await Task.Delay(500);

        var beforeCount = await _service.CountAsync("del-proj");
        Assert.Equal(1, beforeCount);

        await _service.DeleteAsync(new[] { pointId });

        await Task.Delay(500);

        var afterCount = await _service.CountAsync("del-proj");
        Assert.Equal(0, afterCount);
    }

    /// <summary>
    /// [PURPOSE]: GetBatchForCompactAsync must exclude summary entries and order by timestamp ASC.
    /// </summary>
    [Fact]
    public async Task GetBatchForCompactAsync_ExcludesSummary()
    {
        var vector = Enumerable.Range(0, 384).Select(i => 0.05f * i).ToArray();

        // Insert entries with different entry types including summary
        var entries = new[]
        {
            (Guid.NewGuid(), EntryType.Decision, "First decision", DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            (Guid.NewGuid(), EntryType.BugFix, "First bug", DateTimeOffset.Parse("2026-02-01T00:00:00Z")),
            (Guid.NewGuid(), EntryType.Summary, "Summary of old entries", DateTimeOffset.Parse("2026-03-01T00:00:00Z")),
            (Guid.NewGuid(), EntryType.Insight, "Later insight", DateTimeOffset.Parse("2026-04-01T00:00:00Z"))
        };

        foreach (var (id, entryType, content, ts) in entries)
        {
            await _service.UpsertAsync(id, vector, new MemoryPayload
            {
                ProjectId = "compact-proj", SessionId = "s1", AgentRole = AgentRole.Code,
                EntryType = entryType, Timestamp = ts,
                Content = content, Tags = Array.Empty<string>()
            });
        }

        await Task.Delay(500);

        var batch = await _service.GetBatchForCompactAsync("compact-proj", batchSize: 20);

        // Summary should be excluded
        Assert.All(batch, e => Assert.NotEqual(EntryType.Summary, e.Payload.EntryType));

        // Should have 3 non-summary entries
        Assert.Equal(3, batch.Count);

        // Should be ordered by timestamp ASC (oldest first)
        Assert.Equal("First decision", batch[0].Payload.Content);
        Assert.Equal("First bug", batch[1].Payload.Content);
        Assert.Equal("Later insight", batch[2].Payload.Content);
    }
}
