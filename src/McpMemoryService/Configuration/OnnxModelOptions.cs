#region MODULE_CONTRACT [DOMAIN(Options): ONNX model configuration; CONCEPT(POCO): sealed + init setters; TECH(Microsoft.ML): OnnxRuntime]
/**
 * [GREP_SUMMARY]: OnnxModelOptions ModelPath TokenizerPath IntraOpNumThreads EnableCpuMemArena EnableMemoryPattern UseOrtModelBytesForInitializers embedding model inference bounded RSS
 * [STRUCTURE]: OnnxRuntime → Session → IntraOpNumThreads → Parallelism → MemoryPool → EnableCpuMemArena → EnableMemoryPattern → UseOrtModelBytesForInitializers
 *
 * <summary>
 * [PURPOSE]: Binds ONNX model inference settings from appsettings.json.
 * </summary>
 * <remarks>
 * [INVARIANTS]: ModelPath must exist at startup (validated in M5).
 * [RATIONALE]: IntraOpNumThreads capped at 4 to leave CPU headroom for LLM processes.
 * [CHANGES]: LAST_CHANGE: M2 skeleton creation.
 * [CHANGES]: M13 — added 3 ORT memory knobs (EnableCpuMemArena=false, EnableMemoryPattern=true, UseOrtModelBytesForInitializers=true) + IntraOpNumThreads default 4→2 (halved to bound per-thread arena allocations).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Configuration;

/// <summary>
/// [PURPOSE]: ONNX model path and inference configuration.
/// </summary>
/// <remarks>
/// [INVARIANTS]: IntraOpNumThreads > 0 (validated in M5 startup).
/// [RATIONALE]: Model and tokenizer paths co-located for ease of deployment.
/// </remarks>
public sealed class OnnxModelOptions
{
    /// <summary>Gets the path to the ONNX model file.</summary>
    public string ModelPath { get; init; } = "Models/model.onnx";

    /// <summary>Gets the path to the tokenizer JSON file.</summary>
    public string TokenizerPath { get; init; } = "Models/tokenizer.json";

    /// <summary>Gets the number of threads for intra-op parallelism. Defaults to 2 (M13: halved from 4 to bound per-thread arena allocations; SPEC §5.1 headroom intent preserved).</summary>
    public int IntraOpNumThreads { get; init; } = 2;

    /// <summary>Enables the ONNX Runtime CPU arena allocator (retains activation memory; default false for bounded RSS).</summary>
    public bool EnableCpuMemArena { get; init; } = false;

    /// <summary>Enables ONNX Runtime memory-pattern optimization (faster, higher retained memory; default true).</summary>
    public bool EnableMemoryPattern { get; init; } = true;

    /// <summary>References initializers directly from the model file bytes to avoid duplicating weights (default true).</summary>
    public bool UseOrtModelBytesForInitializers { get; init; } = true;
}
