#region MODULE_CONTRACT [DOMAIN(Services): Startup initialization; CONCEPT(QdrantCollectionInitializer): IHostedService for Qdrant collection; TECH(ADR-005, M5)]
/**
 * [GREP_SUMMARY]: QdrantCollectionInitializer, IHostedService, startup, fatal-on-failure, ADR-005, EnsureCollectionExistsAsync
 * [STRUCTURE]: QdrantCollectionInitializer → StartAsync(EnsureCollection) → StopAsync(no-op)
 *
 * <summary>
 * [PURPOSE]: Hosted service that ensures the Qdrant collection exists at application startup.
 * </summary>
 * <remarks>
 * [INVARIANTS]: On startup failure (Qdrant unreachable), logs CRITICAL and rethrows — host exits with code 1 (ADR-005).
 *   Runtime Qdrant failures during tool operations are handled by M10 middleware (ADR-005 silent-fallback policy).
 * [RATIONALE]: IHostedService.StartAsync runs after the host is built but before it starts accepting requests.
 *   This ensures the collection is ready before any tool calls arrive.
 * [CHANGES]: LAST_CHANGE: M5 — initial creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Services;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// [PURPOSE]: Hosted service that ensures the Qdrant collection exists at application startup.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Failure during StartAsync is fatal (rethrow, Exit 1 per ADR-005 startup policy).
///   StopAsync is a no-op (the QdrantClient is managed by the DI container).
/// </remarks>
public sealed class QdrantCollectionInitializer : IHostedService
{
    #region Fields

    private readonly IQdrantService _qdrantService;
    private readonly ILogger<QdrantCollectionInitializer> _logger;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// [PURPOSE]: Creates a new QdrantCollectionInitializer.
    /// </summary>
    /// <param name="qdrantService">The Qdrant service to call for collection initialization.</param>
    /// <param name="logger">Logger for LDD markers.</param>
    public QdrantCollectionInitializer(
        IQdrantService qdrantService,
        ILogger<QdrantCollectionInitializer> logger)
    {
        _qdrantService = qdrantService;
        _logger = logger;
    }

    #endregion Constructors

    #region StartAsync

    /// <summary>
    /// [PURPOSE]: Calls EnsureCollectionExistsAsync. On failure, logs CRITICAL and rethrows (fatal startup).
    /// </summary>
    /// <param name="ct">Cancellation token from the host.</param>
    /// <remarks>
    /// [INVARIANTS]: Per ADR-005, startup init failure = Exit 1. The host cannot start if the collection
    ///   does not exist and Qdrant is unreachable. This is different from runtime failures (M10 handles those).
    /// </remarks>
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[IMP:1][StartAsync][INIT] Qdrant collection initialization started");

            await _qdrantService.EnsureCollectionExistsAsync(ct);

            _logger.LogInformation("[IMP:9][StartAsync][SUCCESS] Qdrant collection initialized");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "[IMP:10][StartAsync][FATAL] Qdrant collection initialization failed — host cannot start");
            throw;
        }
    }

    #endregion StartAsync

    #region StopAsync

    /// <summary>
    /// [PURPOSE]: No-op — the QdrantClient is managed by the DI container.
    /// </summary>
    /// <param name="ct">Cancellation token from the host.</param>
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    #endregion StopAsync
}
