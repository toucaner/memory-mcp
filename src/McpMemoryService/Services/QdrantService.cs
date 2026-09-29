#region MODULE_CONTRACT [DOMAIN(Services): Qdrant persistence; CONCEPT(QdrantService): Thin wrapper over QdrantClient; TECH(SPEC §5.2, ADR-003/005, M5)]
/**
  * [GREP_SUMMARY]: QdrantService, Singleton, QdrantClient, EnsureCollection, Upsert, Search, Count, Delete, Get, Compact, Filter, Conditions, ApiKey, ADR-003/005
  * [STRUCTURE]: QdrantService → ctor(QdrantOptions, ApiKey) → QdrantClient(host, GrpcPort, ApiKey) → EnsureCollection(384/Dot/5 indexes: 4 keyword + 1 Datetime on timestamp) → Upsert(Search) → Count → Delete → Get → GetBatchForCompact
  *
  * <summary>
  * [PURPOSE]: Thin Singleton wrapper over QdrantClient for semantic memory CRUD operations.
  * </summary>
  * <remarks>
  * [INVARIANTS]: All methods throw on Qdrant failure (M10 middleware catches and applies ADR-005 policies).
  *   Constructor does NOT call EnsureCollectionExistsAsync (QdrantCollectionInitializer handles startup).
  *   Collection is 384-dim, Dot (Distance.Dot, equivalent to DotProduct, ADR-011). Indexes: keyword on project_id, entry_type, agent_role, tags + Datetime range index on timestamp (enables ScrollAsync order_by for compact ASC ordering, AC-10).
  * [RATIONALE]: Thin wrapper pattern separates infrastructure concerns (resilience/fallback) from persistence logic.
  *   M10 adds try/catch + fallback; M5 stays pure (throws on failure).
  * [CHANGES]: LAST_CHANGE: M5 debug counter=1 — added PayloadSchemaType.Datetime range index on "timestamp" in EnsureCollectionExistsAsync (Qdrant requires a range index for ScrollAsync order_by); extracted CreatePayloadIndexIdempotentAsync helper; updated [IMP:1] log to "5 indexes". Previous: fixed Distance.Dot (was DotProduct), PointStruct.Payload (readonly MapField), BuildFilter param name (entryType), Direction.Asc (was OrderingType).
  *             M11a — added apiKey: _options.ApiKey pass-through to the QdrantClient constructor (backward-compatible: apiKey defaults null; M5 integration tests unchanged).
  * </remarks>
  */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Services;

using System.Text.Json;
using Grpc.Core;
using McpMemoryService.Configuration;
using McpMemoryService.Enums;
using McpMemoryService.Logging;
using McpMemoryService.Mapping;
using McpMemoryService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

/// <summary>
/// [PURPOSE]: Thin wrapper over QdrantClient for semantic memory CRUD operations.
/// </summary>
public sealed class QdrantService : IQdrantService
{
    #region Fields

    private readonly QdrantClient _client;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantService> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// [PURPOSE]: Creates a new QdrantService wrapping a QdrantClient connected to the configured gRPC endpoint.
    /// </summary>
    /// <param name="options">Qdrant connection options (Url for host extraction, GrpcPort for gRPC port).</param>
    /// <param name="logger">Logger for LDD markers and diagnostics.</param>
    /// <remarks>
    /// [INVARIANTS]: Does NOT call EnsureCollectionExistsAsync — that is QdrantCollectionInitializer's job.
    ///   The QdrantClient constructor creates a gRPC channel (connection is deferred until first call).
    /// </remarks>
    public QdrantService(IOptions<QdrantOptions> options, ILogger<QdrantService> logger)
     {
         _options = options.Value;
         _logger = logger;

         var uri = new Uri(_options.Url);
         var host = uri.Host;
         // [IMP:M11a][QdrantService][PROGRESS] ApiKey pass-through to QdrantClient — backward-compatible (apiKey defaults null)
         _client = new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey);
     }

    #endregion Constructors

    #region Public

    /// <inheritdoc />
    public async Task EnsureCollectionExistsAsync(CancellationToken ct = default)
    {
        var exists = await _client.CollectionExistsAsync(_options.CollectionName, ct);

        if (!exists)
        {
            await _client.CreateCollectionAsync(
                _options.CollectionName,
                vectorsConfig: new VectorParams { Size = 384, Distance = Distance.Dot },
                // BUG_FIX_CONTEXT: [HYPOTHESIS: Distance enum member was "DotProduct" but actual Qdrant.Client 1.18.1 enum is "Dot"]
                // BUG_FIX_CONTEXT: [Why Dot: protobuf enum Distance { Unknown=0, Cosine=1, Euclid=2, Dot=3, Manhattan=4 }. "Dot" = DotProduct distance per Qdrant proto spec.]
                onDiskPayload: true,
                cancellationToken: ct);
        }

        // Create keyword indexes (idempotent — swallow "already exists" errors)
        // BUG_FIX_CONTEXT: [HYPOTHESIS: GetBatchForCompactAsync uses ScrollAsync with orderBy on "timestamp"; Qdrant requires a range-type index on the order_by field, but only keyword indexes existed, causing RpcException InvalidArgument "No range index for order_by key: timestamp". Add a PayloadSchemaType.Datetime range index for timestamp (stored as ISO 8601 string).]
        var indexFields = new[] { "project_id", "entry_type", "agent_role", "tags" };
        foreach (var field in indexFields)
        {
            await CreatePayloadIndexIdempotentAsync(field, PayloadSchemaType.Keyword, ct);
        }

        // Create a datetime range index on "timestamp" so ScrollAsync order_by works (M5 AC-10).
        // BUG_FIX_CONTEXT: [Why Datetime: timestamp is stored as ISO 8601 string (PayloadMappingExtensions.ToQdrantPayload). PayloadSchemaType.Datetime is the Qdrant range index for datetime-shaped string payloads — it enables order_by / range queries without changing the on-disk payload format. Integer would require re-encoding timestamps to unix epoch ms and would break the existing mapping round-trip. Datetime is the least-invasive, format-preserving fix.]
        await CreatePayloadIndexIdempotentAsync("timestamp", PayloadSchemaType.Datetime, ct);

        _logger.LogInformation("{Marker} Collection ready: {Collection}, 384-dim, Dot, 5 indexes",
            LddMarkers.QdrantCollectionReady, _options.CollectionName);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Guid pointId, float[] vector, MemoryPayload payload, CancellationToken ct = default)
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: PointStruct.Payload is a read-only MapField<string,Value> protobuf map, not a settable property]
        // BUG_FIX_CONTEXT: [Why foreach: PointStruct.Payload is MapField<string,Value> (readonly collection). Must add entries after construction via indexer.]
        var point = new PointStruct
        {
            Id = new PointId { Uuid = pointId.ToString() },
            Vectors = vector
        };
        foreach (var kv in payload.ToQdrantPayload())
        {
            point.Payload[kv.Key] = kv.Value;
        }

        await _client.UpsertAsync(_options.CollectionName, new[] { point }, cancellationToken: ct);

        _logger.LogInformation("{Marker} Point upserted: {PointId}", LddMarkers.QdrantUpserted, pointId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        float[] queryVector,
        string projectId,
        AgentRole? agentRoleFilter = null,
        EntryType? entryTypeFilter = null,
        int limit = 5,
        CancellationToken ct = default)
    {
        var filter = BuildFilter(projectId, agentRoleFilter, entryTypeFilter);

        var results = await _client.SearchAsync(
            _options.CollectionName,
            queryVector,
            filter: filter,
            limit: (ulong)limit,
            vectorsSelector: new WithVectorsSelector { Enable = true },
            cancellationToken: ct);

        var entries = results.Select(p => p.ToEntry()).ToList();

        _logger.LogInformation("{Marker} Search returned {Count} results", LddMarkers.QdrantSearched, entries.Count);

        return entries;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string projectId, EntryType? entryTypeFilter = null, CancellationToken ct = default)
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: BuildFilter param named "entryType", not "entryTypeFilter"]
        // BUG_FIX_CONTEXT: [Why: C# named param must match declaration. BuildFilter(string, AgentRole?, EntryType? entryType)]
        var filter = BuildFilter(projectId, entryType: entryTypeFilter);

        var count = await _client.CountAsync(_options.CollectionName, filter, exact: true, cancellationToken: ct);

        // Cast ulong → int; acceptable for our use case (memory entry count will never exceed int.MaxValue)
        return (int)count;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(IEnumerable<Guid> pointIds, CancellationToken ct = default)
    {
        var idList = pointIds.ToList();

        await _client.DeleteAsync(_options.CollectionName, idList, cancellationToken: ct);

        _logger.LogInformation("{Marker} Deleted {Count} points", LddMarkers.QdrantDeleted, idList.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> GetAsync(IEnumerable<Guid> pointIds, CancellationToken ct = default)
    {
        var ids = pointIds.Select(id => new PointId { Uuid = id.ToString() }).ToList();

        var results = await _client.RetrieveAsync(
            _options.CollectionName,
            ids,
            withPayload: true,
            withVectors: true,
            cancellationToken: ct);

        return results.Select(p => p.ToEntry()).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> GetBatchForCompactAsync(
        string projectId,
        int batchSize,
        CancellationToken ct = default)
    {
        var filter = BuildCompactFilter(projectId);

        var scrollResult = await _client.ScrollAsync(
            _options.CollectionName,
            filter: filter,
            limit: (uint)batchSize,
            payloadSelector: new WithPayloadSelector { Enable = true },
            vectorsSelector: new WithVectorsSelector { Enable = false },
            orderBy: new OrderBy { Key = "timestamp", Direction = Direction.Asc },
            // BUG_FIX_CONTEXT: [HYPOTHESIS: "OrderingType" doesn't exist; correct type is Direction enum with Asc/Desc]
            // BUG_FIX_CONTEXT: [Why: Qdrant.Client.Grpc.Direction is the protobuf enum for OrderBy.Direction. Members: Asc, Desc.]
            cancellationToken: ct);

        var entries = scrollResult.Result.Select(p => p.ToEntry(withVector: false)).ToList();

        _logger.LogInformation("{Marker} Fetched {Count} points for compact", LddMarkers.QdrantBatchFetched, entries.Count);

        return entries;
    }

    #endregion Public

    #region Private

    /// <summary>
    /// [PURPOSE]: Builds a Qdrant Filter with project_id (required) and optional agent_role/entry_type conditions.
    /// </summary>
    /// <param name="projectId">Required project ID to match.</param>
    /// <param name="agentRole">Optional agent role filter (converted to snake_case).</param>
    /// <param name="entryType">Optional entry type filter (converted to snake_case).</param>
    /// <returns>A Qdrant Filter with Must conditions.</returns>
    private Filter BuildFilter(string projectId, AgentRole? agentRole = null, EntryType? entryType = null)
    {
        var conditions = new List<Condition> { MatchKeyword("project_id", projectId) };

        if (agentRole.HasValue)
        {
            var snakeCase = JsonNamingPolicy.SnakeCaseLower.ConvertName(agentRole.Value.ToString());
            conditions.Add(MatchKeyword("agent_role", snakeCase));
        }

        if (entryType.HasValue)
        {
            var snakeCase = JsonNamingPolicy.SnakeCaseLower.ConvertName(entryType.Value.ToString());
            conditions.Add(MatchKeyword("entry_type", snakeCase));
        }

        return new Filter { Must = { conditions } };
    }

    /// <summary>
    /// [PURPOSE]: Builds a Qdrant Filter for compact batch retrieval: project_id match + exclude summary entries.
    /// </summary>
    /// <param name="projectId">Project ID to match.</param>
    /// <returns>A Qdrant Filter with Must (project_id) and MustNot (entry_type=summary) conditions.</returns>
    private Filter BuildCompactFilter(string projectId)
    {
        return new Filter
        {
            Must = { MatchKeyword("project_id", projectId) },
            MustNot = { MatchKeyword("entry_type", "summary") }
        };
    }

    /// <summary>
    /// [PURPOSE]: Creates a payload index idempotently — swallows the "already exists" error thrown by Qdrant on duplicate index creation.
    /// </summary>
    /// <param name="field">Payload field name to index.</param>
    /// <param name="schemaType">Qdrant schema type (Keyword for exact match, Datetime for range/order_by on datetime-shaped strings, Integer/Double for numeric ranges).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// [CHANGES]: M5 debug — extracted shared try/catch for idempotent index creation (handles both RpcException InvalidArgument/AlreadyExists and QdrantException on duplicate). Used by EnsureCollectionExistsAsync for the 4 keyword indexes + the 1 Datetime index on "timestamp" (required by ScrollAsync order_by, AC-10).
    /// </remarks>
    private async Task CreatePayloadIndexIdempotentAsync(string field, PayloadSchemaType schemaType, CancellationToken ct)
    {
        try
        {
            await _client.CreatePayloadIndexAsync(
                _options.CollectionName,
                field,
                schemaType,
                cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument ||
                                       ex.StatusCode == StatusCode.AlreadyExists)
        {
            // Index already exists — idempotent, ignore
            _logger.LogDebug("Index '{Field}' already exists on collection '{Collection}': {Message}",
                field, _options.CollectionName, ex.Message);
        }
        catch (QdrantException ex)
        {
            // Some versions of Qdrant throw QdrantException for duplicate indexes
            _logger.LogDebug("Index '{Field}' already exists on collection '{Collection}': {Message}",
                field, _options.CollectionName, ex.Message);
        }
    }

    #endregion Private
}
