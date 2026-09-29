#region MODULE_CONTRACT [DOMAIN(Mapping): Qdrant payload round-trip; CONCEPT(PayloadMappingExtensions): MemoryPayload ↔ Qdrant Value types; TECH(SPEC §3, Qdrant.Client.Grpc.Value, M5)]
/**
 * [GREP_SUMMARY]: PayloadMappingExtensions, extension methods, Qdrant, Value, RetrievedPoint, ScoredPoint, payload, round-trip, snake_case
 * [STRUCTURE]: PayloadMappingExtensions static class → ToQdrantPayload(MemoryPayload) → Dictionary<string,Value> → ToPayload(IDictionary<string,Value>) → MemoryPayload → ToEntry(RetrievedPoint/ScoredPoint) → MemoryEntry
 *
 * <summary>
 * [PURPOSE]: Static extension methods for round-trip conversion between MemoryPayload records and Qdrant payload dictionaries (Value protobuf type).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Round-trip is faithful — MemoryPayload → ToQdrantPayload → ToPayload → MemoryPayload preserves all fields.
 * [RATIONALE]: M5 replaces M3 stubs with real Qdrant.Client types (Value, RetrievedPoint, ScoredPoint).
 *   The tuple-based ToEntry is removed — RetrievedPoint and ScoredPoint overloads replace it.
 * [CHANGES]: LAST_CHANGE: M5 debug — fixed HasStructValue→KindCase check, VectorOutput.Data→Dense.Data (obsolete replacement), VectorsOutput type for retrieved vectors.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Mapping;

using System.Globalization;
using System.Text.Json;
using McpMemoryService.Enums;
using McpMemoryService.Models;
using Qdrant.Client.Grpc;

/// <summary>
/// [PURPOSE]: Static extension methods for round-trip conversion between MemoryPayload and Qdrant payload dictionaries.
/// </summary>
public static class PayloadMappingExtensions
{
    /// <summary>
    /// [PURPOSE]: Converts a MemoryPayload into a Qdrant-compatible payload dictionary with snake_case keys and Value protobuf types.
    /// </summary>
    /// <param name="payload">The MemoryPayload to convert.</param>
    /// <returns>A Dictionary with snake_case string keys and Qdrant Value protobuf values.</returns>
    /// <remarks>
    /// [INVARIANTS]: Keys are snake_case (e.g., "entry_type", "agent_role").
    ///   Enums are converted via JsonNamingPolicy.SnakeCaseLower.ConvertName(enum.ToString()).
    ///   Tags are stored as Value.ListValue (list of string Values).
    ///   Metadata is stored as Value.StructValue (nested struct) if present.
    /// </remarks>
    public static Dictionary<string, Value> ToQdrantPayload(this MemoryPayload payload)
    {
        var dict = new Dictionary<string, Value>(8);

        dict["project_id"] = new Value { StringValue = payload.ProjectId };
        dict["session_id"] = new Value { StringValue = payload.SessionId };
        dict["agent_role"] = new Value { StringValue = JsonNamingPolicy.SnakeCaseLower.ConvertName(payload.AgentRole.ToString()) };
        dict["entry_type"] = new Value { StringValue = JsonNamingPolicy.SnakeCaseLower.ConvertName(payload.EntryType.ToString()) };
        dict["timestamp"] = new Value { StringValue = payload.Timestamp.ToString("o", CultureInfo.InvariantCulture) };
        dict["content"] = new Value { StringValue = payload.Content };

        // Tags as list of string Values
        var tagValues = new ListValue();
        tagValues.Values.AddRange(payload.Tags.Select(t => new Value { StringValue = t }));
        dict["tags"] = new Value { ListValue = tagValues };

        // Metadata as nested struct (if present)
        if (payload.Metadata is not null)
        {
            var metadataStruct = new Struct();
            if (payload.Metadata.FilePath is not null)
            {
                metadataStruct.Fields["file_path"] = new Value { StringValue = payload.Metadata.FilePath };
            }
            if (payload.Metadata.ErrorCode is not null)
            {
                metadataStruct.Fields["error_code"] = new Value { StringValue = payload.Metadata.ErrorCode };
            }
            if (payload.Metadata.Session is not null)
            {
                metadataStruct.Fields["session"] = new Value { StringValue = payload.Metadata.Session };
            }
            dict["metadata"] = new Value { StructValue = metadataStruct };
        }

        return dict;
    }

    /// <summary>
    /// [PURPOSE]: Converts a Qdrant payload dictionary (Value types) back into a MemoryPayload.
    /// </summary>
    /// <param name="qdrantPayload">The dictionary from Qdrant (IDictionary&lt;string, Value&gt;).</param>
    /// <returns>A fully populated MemoryPayload.</returns>
    /// <remarks>
    /// [INVARIANTS]: Parses enums via SnakeToPascalCase + Enum.Parse with ignoreCase:true.
    ///   Timestamp parsed with CultureInfo.InvariantCulture.
    ///   Tags extracted from Value.ListValue.Values.
    ///   Metadata reconstructed from Value.StructValue.Fields (if present).
    /// </remarks>
    public static MemoryPayload ToPayload(this IDictionary<string, Value> qdrantPayload)
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: Enum.Parse("bug_fix") fails because C# enum members are PascalCase (BugFix), not snake_case.
        //   ignoreCase only handles case differences like "BUGFIX"→"BugFix", not underscore differences.
        //   Must convert snake_case → PascalCase before parsing.]
        // BUG_FIX_CONTEXT: [Why snake-to-pascal: Qdrant stores enums as snake_case strings (e.g., "bug_fix", "agent_role").
        //   C# enum members are PascalCase (e.g., BugFix, AgentRole). JsonNamingPolicy.SnakeCaseLower.ConvertName
        //   converts PascalCase→snake_case for writing; we need the reverse for reading. The reverse conversion
        //   replaces underscores and capitalizes the following letter (e.g., "bug_fix"→"BugFix").]
        var agentRole = Enum.Parse<AgentRole>(SnakeToPascalCase(qdrantPayload["agent_role"].StringValue), ignoreCase: true);
        var entryType = Enum.Parse<EntryType>(SnakeToPascalCase(qdrantPayload["entry_type"].StringValue), ignoreCase: true);
        var timestamp = DateTimeOffset.Parse(qdrantPayload["timestamp"].StringValue, CultureInfo.InvariantCulture);

        // Tags from ListValue
        var tags = qdrantPayload["tags"].ListValue.Values
            .Select(v => v.StringValue)
            .ToList();

        // Metadata from StructValue (if present)
        // BUG_FIX_CONTEXT: [HYPOTHESIS: Value type has HasXxx for primitives (DoubleValue, StringValue etc.) but NOT for StructValue/ListValue which are message types]
        // BUG_FIX_CONTEXT: [Why KindCase check: Value.KindOneofCase.StructValue is the correct way to test if the oneof is set to StructValue.
        //   HasStructValue doesn't exist because protobuf message fields are always non-null in C# proto-gen; use KindCase to check oneof selection.]
        Metadata? metadata = null;
        if (qdrantPayload.TryGetValue("metadata", out var metaValue) && metaValue.KindCase == Value.KindOneofCase.StructValue)
        {
            var fields = metaValue.StructValue.Fields;
            metadata = new Metadata
            {
                FilePath = fields.TryGetValue("file_path", out var fp) ? fp.StringValue : null,
                ErrorCode = fields.TryGetValue("error_code", out var ec) ? ec.StringValue : null,
                Session = fields.TryGetValue("session", out var s) ? s.StringValue : null
            };
        }

        return new MemoryPayload
        {
            ProjectId = qdrantPayload["project_id"].StringValue,
            SessionId = qdrantPayload["session_id"].StringValue,
            AgentRole = agentRole,
            EntryType = entryType,
            Timestamp = timestamp,
            Content = qdrantPayload["content"].StringValue,
            Tags = tags,
            Metadata = metadata
        };
    }

    /// <summary>
    /// [PURPOSE]: Converts a RetrievedPoint from Qdrant into a MemoryEntry.
    /// </summary>
    /// <param name="point">The RetrievedPoint from Qdrant (no score, has payload and optionally vectors).</param>
    /// <param name="withVector">Whether to extract the vector (true by default; false for compact batch where vectors are not loaded).</param>
    /// <returns>A MemoryEntry with Score = null (RetrievedPoint has no score).</returns>
    /// <remarks>
    /// [INVARIANTS]: Score is always null for RetrievedPoint (no similarity computation).
    ///   Vector extraction: point.Vectors.Vector.Dense.Data contains the float array (if withVectors was true).
    /// </remarks>
    public static MemoryEntry ToEntry(this RetrievedPoint point, bool withVector = true)
    {
        var payload = point.Payload.ToPayload();
        var pointId = Guid.Parse(point.Id.Uuid);

        // BUG_FIX_CONTEXT: [HYPOTHESIS: VectorOutput.Data is obsolete; use Vector.Dense.Data instead]
        // BUG_FIX_CONTEXT: [Why Vector.Dense.Data: RetrievedPoint.Vectors is VectorsOutput (not Vectors).
        //   VectorsOutput.Vector is VectorOutput. VectorOutput.Dense is DenseVector (non-obsolete).
        //   DenseVector.Data is the RepeatedField<float> accessor replacing the obsolete VectorOutput.Data.]
        float[] vector = withVector && point.Vectors != null && point.Vectors.Vector != null && point.Vectors.Vector.Dense != null
            ? point.Vectors.Vector.Dense.Data.ToArray()
            : Array.Empty<float>();

        return new MemoryEntry
        {
            PointId = pointId,
            Payload = payload,
            Vector = vector,
            Score = null
        };
    }

    /// <summary>
    /// [PURPOSE]: Converts a ScoredPoint from Qdrant search results into a MemoryEntry.
    /// </summary>
    /// <param name="point">The ScoredPoint from Qdrant search (has score, payload, and optionally vectors).</param>
    /// <returns>A MemoryEntry with the similarity Score from the search result.</returns>
    /// <remarks>
    /// [INVARIANTS]: Score is always present (float from search). Vector extraction same as RetrievedPoint.
    /// </remarks>
    public static MemoryEntry ToEntry(this ScoredPoint point)
    {
        var payload = point.Payload.ToPayload();
        var pointId = Guid.Parse(point.Id.Uuid);

        // BUG_FIX_CONTEXT: [HYPOTHESIS: ScoredPoint.Vectors is VectorsOutput, not Vectors. Use Vector.Dense.Data, not Vector.Data]
        // BUG_FIX_CONTEXT: [Why Vector.Dense.Data: ScoredPoint.Vectors is VectorsOutput → .Vector (VectorOutput) → .Dense (DenseVector) → .Data.
        //   VectorOutput.Data is obsolete; DenseVector.Data is the replacement.]
        float[] vector = point.Vectors != null && point.Vectors.Vector != null && point.Vectors.Vector.Dense != null
            ? point.Vectors.Vector.Dense.Data.ToArray()
            : Array.Empty<float>();

        return new MemoryEntry
        {
            PointId = pointId,
            Payload = payload,
            Vector = vector,
            Score = point.Score
        };
    }

    #region Private Helpers

    /// <summary>
    /// [PURPOSE]: Converts a snake_case string back to PascalCase for enum parsing.
    /// </summary>
    /// <param name="snakeCase">The snake_case string (e.g., "bug_fix", "agent_role").</param>
    /// <returns>The PascalCase equivalent (e.g., "BugFix", "AgentRole").</returns>
    /// <remarks>
    /// [RATIONALE]: Qdrant stores enum values as snake_case (via JsonNamingPolicy.SnakeCaseLower.ConvertName).
    ///   C# enum members are PascalCase. Enum.Parse with ignoreCase handles "BUGFIX"→"BugFix" but NOT "bug_fix"→"BugFix".
    ///   This method reverses the snake_case conversion for correct parsing.
    /// </remarks>
    private static string SnakeToPascalCase(string snakeCase)
    {
        // BUG_FIX_CONTEXT: [Enum.Parse("bug_fix") fails because C# enum is BugFix. ignoreCase only handles case, not underscores.]
        if (string.IsNullOrEmpty(snakeCase))
            return snakeCase;

        var result = new System.Text.StringBuilder(snakeCase.Length);
        bool nextUpper = true;
        foreach (var c in snakeCase)
        {
            if (c == '_')
            {
                nextUpper = true;
            }
            else
            {
                result.Append(nextUpper ? char.ToUpperInvariant(c) : c);
                nextUpper = false;
            }
        }

        return result.ToString();
    }

    #endregion Private Helpers
}
