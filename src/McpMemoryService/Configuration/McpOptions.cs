#region MODULE_CONTRACT [DOMAIN(Options): MCP server identity; CONCEPT(POCO): sealed + init setters; TECH(MCP): serverInfo]
/**
 * [GREP_SUMMARY]: McpOptions ServerName ServerVersion MCP server identity handshake initialize
 * [STRUCTURE]: McpOptions → IOptions<McpOptions> → serverInfo → initialize response
 *
 * <summary>
 * [PURPOSE]: Binds MCP server identity and version information.
 * </summary>
 * <remarks>
 * [INVARIANTS]: ServerName and ServerVersion are always non-empty (defaults provided).
 * [RATIONALE]: Server identity sent in the MCP initialize handshake response (serverInfo).
 * [CHANGES]: LAST_CHANGE: M2 skeleton creation.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Configuration;

/// <summary>
/// [PURPOSE]: MCP server identity configuration (name and version).
/// </summary>
/// <remarks>
/// [INVARIANTS]: Never empty — defaults guarantee valid identity.
/// [RATIONALE]: Identity used in MCP initialize response and for log correlation.
/// </remarks>
public sealed class McpOptions
{
    /// <summary>Gets the MCP server name. Defaults to <c>"McpMemoryService"</c>.</summary>
    public string ServerName { get; init; } = "McpMemoryService";

    /// <summary>Gets the MCP server version. Defaults to <c>"1.0.0"</c>.</summary>
    public string ServerVersion { get; init; } = "1.0.0";
}
