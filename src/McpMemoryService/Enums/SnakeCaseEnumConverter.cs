#region MODULE_CONTRACT [DOMAIN(Enum): JSON serialization helper; CONCEPT(SnakeCaseEnumConverter): JsonStringEnumConverter subclass; TECH(System.Text.Json)]
/**
 * [GREP_SUMMARY]: SnakeCaseEnumConverter, JsonStringEnumConverter, JsonNamingPolicy, snake_case, JSON serialization
 * [STRUCTURE]: SnakeCaseEnumConverter : JsonStringEnumConverter → base(JsonNamingPolicy.SnakeCaseLower) → [JsonConverter] on enums
 *
 * <summary>
 * [PURPOSE]: Enables snake_case JSON serialization of enum values when applied as a [JsonConverter] attribute.
 * </summary>
 * <remarks>
 * [INVENTORY]: Inherits from JsonStringEnumConverter (System.Text.Json, .NET 8+).
 * [RATIONALE]: C# attribute arguments must be compile-time constants (ECMA-334 §17.2.2); JsonNamingPolicy.SnakeCaseLower is a runtime static property — NOT a constant — so [JsonConverter(typeof(JsonStringEnumConverter), JsonNamingPolicy.SnakeCaseLower)] fails to compile. The subclass form calls the base constructor with the policy at runtime, making it attribute-compatible.
 * [INVARIANTS]: The converter always converts to snake_case (lowercase with underscores).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Enums;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// [PURPOSE]: JSON converter that serializes enum values to snake_case lowercase strings.
/// </summary>
/// <remarks>
/// [RATIONALE]: C# attribute arguments must be compile-time constants. JsonNamingPolicy.SnakeCaseLower
/// is a runtime static property, so [JsonConverter(typeof(JsonStringEnumConverter), JsonNamingPolicy.SnakeCaseLower)]
/// does not compile. This subclass form passes the policy to the base constructor at runtime.
/// </remarks>
public sealed class SnakeCaseEnumConverter : JsonStringEnumConverter
{
    /// <summary>
    /// [PURPOSE]: Constructs the converter using snake_case lowercase naming policy.
    /// </summary>
    public SnakeCaseEnumConverter() : base(JsonNamingPolicy.SnakeCaseLower)
    {
    }
}
