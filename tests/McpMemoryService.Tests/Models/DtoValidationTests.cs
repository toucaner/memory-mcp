#region MODULE_CONTRACT [DOMAIN(Test): DTO validation; CONCEPT(DtoValidationTests): Defaults + required-field enforcement; TECH(xUnit)]
/**
 * [GREP_SUMMARY]: DtoValidationTests, xUnit, MemoryRetrieveInput, MemoryCompactInput, MemoryCaptureInput, required, RespectRequiredConstructorParameters
 * [STRUCTURE]: DtoValidationTests xUnit class → DefaultLimit + DefaultBatchSize + RequiredFieldsThrow
 *
 * <summary>
 * [PURPOSE]: Tests for DTO default values and required-field enforcement at deserialize time.
 * </summary>
 * <remarks>
 * [INVARIANTS]: All tests use in-memory data only (no external dependencies).
 *   [IMP:3] on all three tests.
 * [RATIONALE]: M3 spec lines 284-308. The required-field test uses RespectRequiredConstructorParameters=true
 *   because System.Text.Json does NOT enforce 'required' at deserialize time by default (it silently defaults).
 *   With this option, JsonException is thrown when a required member is absent — this is .NET 8+ documented behavior.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Models;

using System.Text.Json;
using McpMemoryService.Contracts;
using McpMemoryService.Enums;
using McpMemoryService.Models;

/// <summary>
/// [PURPOSE]: Unit tests for DTO default values and required-field enforcement.
/// </summary>
public class DtoValidationTests
{
    /// <summary>
    /// [PURPOSE]: MemoryRetrieveInput must default Limit to 5.
    /// </summary>
    [Fact]
    public void MemoryRetrieveInput_DefaultLimit_Is5()
    {
        // [IMP:3][MemoryRetrieveInput_DefaultLimit_Is5][INIT] Creating MemoryRetrieveInput with minimal fields
        var input = new MemoryRetrieveInput { Query = "q", ProjectId = "p" };
        Assert.Equal(5, input.Limit);
    }

    /// <summary>
    /// [PURPOSE]: MemoryCompactInput must default BatchSize to 20.
    /// </summary>
    [Fact]
    public void MemoryCompactInput_DefaultBatchSize_Is20()
    {
        // [IMP:3][MemoryCompactInput_DefaultBatchSize_Is20][INIT] Creating MemoryCompactInput with minimal fields
        var input = new MemoryCompactInput { ProjectId = "p" };
        Assert.Equal(20, input.BatchSize);
    }

    /// <summary>
    /// [PURPOSE]: MemoryCaptureInput must throw JsonException when required fields (Content, AgentRole, EntryType) are missing.
    /// </summary>
    /// <remarks>
    /// [IMP:3]: Required-field enforcement test.
    ///
    /// WHY RespectRequiredConstructorParameters=true:
    /// The C# 'required' keyword enforces required members at compile time for object initializers
    /// (new X { ... }), but System.Text.Json does NOT enforce 'required' members during deserialization
    /// by default — it silently initializes them to null/default. To make this test actually throw
    /// on missing required members, we MUST set RespectRequiredConstructorParameters=true on
    /// JsonSerializerOptions. This is .NET 8+ documented behavior (BCL) → confirmed on net10.0.
    /// Without this option, the test would pass for the wrong reason (no exception thrown).
    /// </remarks>
    [Fact]
    public void MemoryCaptureInput_RequiredFields_ThrowWhenMissing()
    {
        // [IMP:3][MemoryCaptureInput_RequiredFields_ThrowWhenMissing][INIT] Setting up options with RespectRequiredConstructorParameters=true
        var opts = new JsonSerializerOptions
        {
            RespectRequiredConstructorParameters = true
        };

        // [IMP:3][MemoryCaptureInput_RequiredFields_ThrowWhenMissing][ACT] Deserializing JSON missing required members
        var json = "{\"project_id\":\"p\"}";
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<MemoryCaptureInput>(json, opts));

        // [IMP:3][MemoryCaptureInput_RequiredFields_ThrowWhenMissing][CHECK] Asserting JsonException was thrown
        Assert.NotNull(ex);
    }
}
