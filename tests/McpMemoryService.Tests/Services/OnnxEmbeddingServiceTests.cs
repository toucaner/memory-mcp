#region MODULE_CONTRACT [DOMAIN(Tests): ONNX embedding integration tests; CONCEPT(OnnxEmbeddingServiceTests): 6 [Fact] tests with real model; TECH(xUnit): Category=Integration]
/**
 * [GREP_SUMMARY]: OnnxEmbeddingServiceTests Dimension EmbedAsync L2Normalized SimilarTexts NullText EmptyText IDisposable CosineSimilarity RepeatedCalls DoNotDegrade WarmupMemoryGrowth WorkingSet64 GetTotalMemory CallCount MultilingualSimilarity IsPreserved
 * [STRUCTURE]: ctor(builds OnnxEmbeddingService) -> 9 [Fact] tests (6 M4 + 2 M13 stability + 1 M14 multilingual) -> Dispose()
 *
 * <summary>
 * [PURPOSE]: 9 integration tests exercising the OnnxEmbeddingService with the real paraphrase-multilingual-MiniLM-L12-v2 model (6 M4 semantic tests + 2 M13 stability tests + 1 M14 multilingual quality-gate test).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Category=Integration (class-level) — model file (~113 MB int8 quantized, M14 variant) is an external artifact.
 *               Model must be downloaded via Download-Model.ps1 before running.
 * [RATIONALE]: Mocking ONNX inference is pointless (M4 spec line 169); tests must load the real model.
 * [CHANGES]: M13 — added EmbedAsync_RepeatedCalls_DoNotDegrade (500 calls: dim 384 + norm≈1.0 every call) + EmbedAsync_WarmupMemoryGrowth_IsBounded (working-set growth &lt; 100 MB [UNVERIFIED_VERSION] threshold; managed GC delta is telemetry only, NOT asserted). No absolute RSS assertion (M15's deployment gate).
 *            M14 — added EmbedAsync_MultilingualSimilarity_IsPreserved (int8-variant multilingual quality gate, ADR-011a).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Tests.Services;

using McpMemoryService.Configuration;
using McpMemoryService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Xunit.Abstractions;

/// <summary>
/// Integration tests for <see cref="OnnxEmbeddingService"/> using the real ONNX model.
/// </summary>
/// <remarks>
/// All tests carry [Trait("Category","Integration")] — they load the real
/// paraphrase-multilingual-MiniLM-L12-v2 model (~113 MB int8 quantized, gitignored — M14 variant).
/// Model must be downloaded BEFORE running: pwsh src/McpMemoryService/Scripts/Download-Model.ps1
/// (or powershell.exe -NoProfile -ExecutionPolicy Bypass -File ... on Windows PowerShell 5.1 — M14 fix).
/// M13: the 2 stability tests additionally exercise the deterministic-dispose path
/// (using results + SessionOptions using; input NamedOnnxValue wrappers are non-disposable
/// in ORT 1.27.0 — probe-dropped, see OnnxEmbeddingService.RunInference PROBE scar)
/// under sustained 500-call traffic.
/// </remarks>
[Trait("Category", "Integration")]
public class OnnxEmbeddingServiceTests : IDisposable
{
    /// <summary>Number of EmbedAsync calls for the M13 stability loops.</summary>
    private const int CallCount = 500;

    /// <summary>Number of warm-up calls before the memory baseline sample (let ORT arenas/JIT settle).</summary>
    private const int WarmupCount = 10;

    /// <summary>
    /// Initial working-set growth bound over the 500-call loop.
    /// [UNVERIFIED_VERSION] threshold — calibrate to observed_growth * ~2 (headroom) after the first instrumented run (M13 Notes #8).
    /// </summary>
    private const long CalibratedWorkingSetGrowthLimitBytes = 100L * 1024 * 1024;

    private readonly OnnxEmbeddingService _service;
    private readonly ITestOutputHelper _output;

    public OnnxEmbeddingServiceTests(ITestOutputHelper output)
    {
        _output = output;

        // Resolve model paths relative to the test output directory.
        // Test cwd is bin/Debug/net10.0 (or Release) — go up to repo root, then into src/...
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var modelsDir = Path.Combine(repoRoot, "src", "McpMemoryService", "Models");

        var modelPath = Path.GetFullPath(Path.Combine(modelsDir, "model.onnx"));
        var tokenizerPath = Path.GetFullPath(Path.Combine(modelsDir, "tokenizer.json"));

        var options = Microsoft.Extensions.Options.Options.Create(new OnnxModelOptions
        {
            ModelPath = modelPath,
            TokenizerPath = tokenizerPath,
            IntraOpNumThreads = 1 // Fast for tests
        });

        _service = new OnnxEmbeddingService(options, NullLogger<OnnxEmbeddingService>.Instance);
    }

    /// <summary>
    /// Verifies that the Dimension property returns 384.
    /// </summary>
    [Fact]
    public void Dimension_Returns384()
    {
        var dimension = _service.Dimension;
        Assert.Equal(384, dimension);
    }

    /// <summary>
    /// Verifies that EmbedAsync returns a vector of 384 dimensions for non-empty text.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ReturnsVectorOf384()
    {
        var result = await _service.EmbedAsync("Hello, world!");
        Assert.Equal(384, result.Length);
    }

    /// <summary>
    /// Verifies that the returned vector is L2-normalized to approximately 1.0 (precision 4).
    /// </summary>
    [Fact]
    public async Task EmbedAsync_VectorIsL2Normalized()
    {
        var result = await _service.EmbedAsync("The quick brown fox");
        var norm = ComputeL2Norm(result);
        Assert.Equal(1.0, norm, 4);
    }

    /// <summary>
    /// Verifies that similar texts produce higher cosine similarity than dissimilar ones.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_SimilarTexts_ProduceSimilarVectors()
    {
        var threadText1 = await _service.EmbedAsync("error when working with threads");
        var threadText2 = await _service.EmbedAsync("memory leak in threads");
        var foodText = await _service.EmbedAsync("borscht recipe");

        var sim12 = CosineSimilarity(threadText1, threadText2);
        var sim13 = CosineSimilarity(threadText1, foodText);

        Assert.True(sim12 > sim13, $"Similar texts should have higher similarity: {sim12:F4} vs {sim13:F4}");
        Assert.True(sim12 > 0.5, $"Similar texts should have high similarity: {sim12:F4}");
    }

    /// <summary>
    /// Verifies that EmbedAsync throws ArgumentNullException for null text.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_NullText_ThrowsArgumentNullException()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () => await _service.EmbedAsync(null!));
        Assert.IsType<ArgumentNullException>(ex);
    }

    /// <summary>
    /// Verifies that EmbedAsync returns a valid 384-element vector for empty text.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_EmptyText_ReturnsValidVector()
    {
        var result = await _service.EmbedAsync(string.Empty);
        Assert.Equal(384, result.Length);
    }

    /// <summary>
    /// M13 stability: {CallCount} EmbedAsync calls must not degrade — every result is 384-dim and L2-normalized.
    /// </summary>
    /// <remarks>
    /// Asserts the [IMP:9] contract (valid 384-dim L2-normalized vectors) under sustained traffic and exercises the
    /// M13 deterministic-dispose path (RunInference finally + using results) on every call. No exception = the dispose
    /// fix does not break inference. [IMP:M13]/[IMP:9] markers emitted via ITestOutputHelper for the @qa log audit.
    /// </remarks>
    [Fact]
    public async Task EmbedAsync_RepeatedCalls_DoNotDegrade()
    {
        _output.WriteLine("[IMP:M13][EmbedAsync_RepeatedCalls_DoNotDegrade][INIT] warm-up 1 call, then {0} calls", CallCount);

        // Warm up once so ORT initializes arenas/thread pools before the measured loop (per-call assertions below still run on the warm call).
        var warm = await _service.EmbedAsync("warm-up");
        Assert.Equal(384, warm.Length);

        var texts = new[] { "error when working with threads", "memory leak in threads", "borscht recipe", "semantic search ranking", "deadlock in payment service" };

        for (var i = 0; i < CallCount; i++)
        {
            var result = await _service.EmbedAsync(texts[i % texts.Length]);
            Assert.Equal(384, result.Length);

            // M4 precision convention (Assert.Equal(1.0, norm, 4)) + explicit ~1e-2 tolerance guard (task contract).
            var norm = ComputeL2Norm(result);
            Assert.Equal(1.0, norm, 4);
            Assert.True(Math.Abs(norm - 1.0) < 1e-2, $"Norm deviated from 1.0 at call {i}: {norm:F6}");
        }

        _output.WriteLine("[IMP:9][EmbedAsync_RepeatedCalls_DoNotDegrade][SUCCESS] {0} calls: all 384-dim, norm≈1.0, no exception", CallCount);
    }

    /// <summary>
    /// M13 stability: working-set growth over the warm 500-call loop stays below the calibrated threshold.
    /// </summary>
    /// <remarks>
    /// Samples <c>GC.GetTotalMemory(false)</c> + <c>Process.GetCurrentProcess().WorkingSet64</c> before/after the
    /// 500-call loop. Only the WorkingSet64 delta is asserted (&lt; CalibratedWorkingSetGrowthLimitBytes). The managed
    /// GC delta is telemetry ONLY (GC noise makes it flaky) — logged, NOT asserted. No absolute RSS assertion here —
    /// that is M15's deployment gate. [UNVERIFIED_VERSION] threshold: initial 100 MB bound; calibrate to
    /// observed_growth * ~2 (headroom) after the first instrumented run (M13 Notes #8).
    /// </remarks>
    [Fact]
    public async Task EmbedAsync_WarmupMemoryGrowth_IsBounded()
    {
        _output.WriteLine("[IMP:M13][EmbedAsync_WarmupMemoryGrowth_IsBounded][INIT] warm-up {0} calls, then sample baseline", WarmupCount);

        // Warm up FIRST so ORT arenas/JIT/first-inference allocations settle BEFORE the baseline sample.
        for (var i = 0; i < WarmupCount; i++)
        {
            await _service.EmbedAsync("warm-up memory baseline");
        }

        var managedBefore = GC.GetTotalMemory(false);
        var wsBefore = Process.GetCurrentProcess().WorkingSet64;
        _output.WriteLine("[IMP:M13][EmbedAsync_WarmupMemoryGrowth_IsBounded][PROGRESS] baseline managed={0} bytes, workingSet={1} bytes", managedBefore, wsBefore);

        var texts = new[] { "error when working with threads", "memory leak in threads", "borscht recipe", "semantic search ranking", "deadlock in payment service" };
        for (var i = 0; i < CallCount; i++)
        {
            var result = await _service.EmbedAsync(texts[i % texts.Length]);
            Assert.Equal(384, result.Length);
        }

        var managedAfter = GC.GetTotalMemory(false);
        var wsAfter = Process.GetCurrentProcess().WorkingSet64;
        var managedDelta = managedAfter - managedBefore;
        var wsDelta = wsAfter - wsBefore;

        _output.WriteLine("[IMP:M13][EmbedAsync_WarmupMemoryGrowth_IsBounded][CHECKPOINT] managed delta={0} bytes, working-set delta={1} bytes, limit={2} bytes", managedDelta, wsDelta, CalibratedWorkingSetGrowthLimitBytes);

        // Telemetry only — managed growth is GC-noisy; do NOT hard-assert it (M13 Notes #8).
        _output.WriteLine("[IMP:M13][EmbedAsync_WarmupMemoryGrowth_IsBounded][INFO] managed delta is telemetry (GC noise) — not asserted");

        Assert.True(wsDelta < CalibratedWorkingSetGrowthLimitBytes,
            $"Working-set growth {wsDelta} bytes exceeded calibrated limit {CalibratedWorkingSetGrowthLimitBytes} bytes over {CallCount} calls");

        _output.WriteLine("[IMP:9][EmbedAsync_WarmupMemoryGrowth_IsBounded][SUCCESS] working-set growth {0} bytes below limit {1} bytes", wsDelta, CalibratedWorkingSetGrowthLimitBytes);
    }

    /// <summary>
    /// M14 quality gate: multilingual (RU/EN) embedding geometry is preserved by the int8 quantized
    /// model variant (ADR-011a) — a RU similar pair must rank closer than a RU-vs-EN unrelated pair.
    /// </summary>
    /// <remarks>
    /// Ordering-only assertion — no absolute cross-lingual floor (model-dependent; the quantitative EN
    /// floor sim12 &gt; 0.5 is covered by the M4 EmbedAsync_SimilarTexts_ProduceSimilarVectors test, which
    /// doubles as the second half of the M14 gate). [IMP:M14] checkpoint line logs both similarity values
    /// for the @qa audit. Runs against the real int8 quantized file on disk (retrieval-quality gate).
    /// </remarks>
    [Fact]
    public async Task EmbedAsync_MultilingualSimilarity_IsPreserved()
    {
        var ruThread1 = await _service.EmbedAsync("ошибка при работе с потоками");  // RU mirror of "error when working with threads"
        var ruThread2 = await _service.EmbedAsync("утечка памяти в потоках");       // RU mirror of "memory leak in threads"
        var enUnrelated = await _service.EmbedAsync("borscht recipe");              // existing M4 unrelated EN anchor (consistency)

        var simRuPair = CosineSimilarity(ruThread1, ruThread2);
        var simRuVsEnUnrelated = CosineSimilarity(ruThread1, enUnrelated);

        _output.WriteLine("[IMP:M14][EmbedAsync_MultilingualSimilarity_IsPreserved][CHECKPOINT] simRuPair={0:F4}, simRuVsEnUnrelated={1:F4}", simRuPair, simRuVsEnUnrelated);

        Assert.True(simRuPair > simRuVsEnUnrelated,
            $"RU similar pair must outrank RU-vs-EN unrelated: {simRuPair:F4} vs {simRuVsEnUnrelated:F4}");
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    /// <summary>
    /// Computes cosine similarity between two vectors (dot product since inputs are L2-normalized).
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length.");

        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }
        return (float)sum;
    }

    /// <summary>
    /// Computes the L2 norm of a vector using double accumulation.
    /// </summary>
    private static double ComputeL2Norm(float[] vector)
    {
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSq += (double)vector[i] * vector[i];
        }
        return Math.Sqrt(sumSq);
    }
}
