#region MODULE_CONTRACT [DOMAIN(Options): Configuration; CONCEPT(POCO): sealed + init setters; TECH(IOptions): Microsoft.Extensions.Options]
/**
  * [GREP_SUMMARY]: QdrantOptions QdrantUrl CollectionName GrpcPort ApiKey collection connection stateless
  * [STRUCTURE]: AppSettings → IOptions<T> → QdrantClient(host, GrpcPort, ApiKey) → ConnectionPool → Collection
  *
  * <summary>
  * [PURPOSE]: Binds Qdrant vector database configuration from appsettings.json.
  * </summary>
  * <remarks>
  * [INVARIANTS]: Url must be non-empty at runtime (validated in M5 startup).
  *   GrpcPort defaults to 6334 (Qdrant gRPC port, distinct from REST port 6333 in Url).
  *   ApiKey defaults to null (no authentication) — backward-compatible with unauthenticated deployments.
  * [RATIONALE]: Collection name isolated for environment-specific overrides. GrpcPort separated
  *   because Qdrant.Client uses gRPC exclusively — Url is only for host extraction.
  *   ApiKey added in M11a to support Qdrant deployments that require API-key authentication
  *   (the operator's production Qdrant at <ip>:<port> enforces auth).
  * [CHANGES]: LAST_CHANGE: M5 — added GrpcPort property (default 6334) per Qdrant.Client gRPC requirement.
  *             M11a — added ApiKey property (nullable, init-only) for Qdrant Cloud authentication support.
  * </remarks>
  */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Configuration;

/// <summary>
/// [PURPOSE]: Qdrant vector database connection and collection configuration.
/// </summary>
/// <remarks>
 /// [INVARIANTS]: Url is never null/empty after binding (default = empty, validated upstream).
 ///   GrpcPort defaults to 6334 (Qdrant gRPC port, distinct from REST port 6333 in Url).
 ///   ApiKey defaults to null (no authentication) — backward-compatible with unauthenticated deployments.
 /// [RATIONALE]: Collection name isolated for environment-specific overrides. GrpcPort separated
 ///   because Qdrant.Client uses gRPC exclusively — Url is only for host extraction.
 ///   ApiKey added in M11a to support Qdrant deployments that require API-key authentication
 ///   (the operator's production Qdrant at <ip>:<port> enforces auth).
 /// </remarks>
 public sealed class QdrantOptions
{
    /// <summary>Gets the base URL of the Qdrant instance (used for host extraction, NOT for gRPC connection).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Gets the Qdrant collection name. Defaults to <c>"opencode_memory"</c>.</summary>
    public string CollectionName { get; init; } = "opencode_memory";

    /// <summary>
    /// Gets the gRPC port for Qdrant client communication. Defaults to <c>6334</c>.
    /// </summary>
    /// <remarks>
     /// Qdrant exposes REST on port 6333 and gRPC on port 6334. Qdrant.Client uses gRPC exclusively.
     /// The Url property contains the REST URL — only the host component is extracted for the gRPC client.
     /// </remarks>
     public int GrpcPort { get; init; } = 6334;

     /// <summary>
     /// Gets the optional API key for Qdrant authentication. Defaults to <c>null</c> (no authentication).
     /// </summary>
     /// <remarks>
     /// [INVARIANTS]: When <c>null</c>, the QdrantClient connects without authentication (backward-compatible
     ///   with M5 behavior and the existing integration tests against an unauthenticated Docker Qdrant).
     ///   When set, the key is passed to the QdrantClient constructor which includes it as an
     ///   <c>Authorization: Bearer &lt;key&gt;</c> header on every gRPC call.
     /// [RATIONALE]: Added in M11a to support Qdrant deployments that require API-key authentication
     ///   (the operator's production Qdrant at <ip>:<port> enforces auth). Nullable + default null
     ///   keeps the change strictly backward-compatible — no test, appsettings, or env-var change is REQUIRED
     ///   for existing unauthenticated deployments.
     /// [CHANGES]: LAST_CHANGE: M11a — added ApiKey property (nullable, init-only) per the M11 @qa
     ///   operator-action-required note.
     /// </remarks>
     public string? ApiKey { get; init; }
 }
