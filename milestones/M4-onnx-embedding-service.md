# M4 — OnnxEmbeddingService (ONNX CPU AVX2 + mean pooling + L2)

## Dependencies
- M2 (solution skeleton, OnnxModelOptions)
- M3 (models)
- **External prerequisite:** none (model is downloaded by a script in this milestone)

## Goal
Implement `OnnxEmbeddingService` (Singleton, lazy init) to produce 384-dimensional embeddings via ONNX Runtime on CPU (AVX2, InterOp=1/IntraOp=4). Includes tokenization, inference, mean pooling with attention_mask, L2 normalization. Also create a download script for the `paraphrase-multilingual-MiniLM-L12-v2` model.

## Deliverables
- `src/McpMemoryService/Services/IEmbeddingService.cs`
- `src/McpMemoryService/Services/OnnxEmbeddingService.cs`
- `src/McpMemoryService/Scripts/Download-Model.ps1` (PowerShell, Windows)
- `src/McpMemoryService/Scripts/download-model.sh` (bash, Linux/Docker)
- `src/McpMemoryService/Models/.gitignore` (ignore model.onnx, tokenizer.json)
- `tests/McpMemoryService.Tests/Services/OnnxEmbeddingServiceTests.cs`
- Registration in `Program.cs` (add `services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>()`)

## Contracts

### IEmbeddingService.cs

```csharp
namespace McpMemoryService.Services;

/// <summary>
/// Service for generating embeddings for semantic search.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an L2-normalized embedding for the text.
    /// </summary>
    /// <param name="text">Text to vectorize.</param>
    /// <returns>L2-normalized vector of dimension 384.</returns>
    /// <exception cref="ArgumentNullException">text == null.</exception>
    /// <exception cref="InvalidOperationException">Model not loaded.</exception>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Vector dimension (384 for paraphrase-multilingual-MiniLM-L12-v2).
    /// </summary>
    int Dimension { get; }
}
```

### OnnxEmbeddingService.cs — algorithm

```csharp
namespace McpMemoryService.Services;

public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    # region Fields
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
    private readonly ILogger<OnnxEmbeddingService> _logger;
    # endregion

    # region Constants
    public int Dimension => 384;
    private const int MaxSequenceLength = 256;
    # endregion

    # region Constructor
    // Lazy init: InferenceSession created in constructor.
    // SessionOptions:
    //   - AppendExecutionProvider_CPU(0)
    //   - InterOpNumThreads = 1
    //   - IntraOpNumThreads = options.IntraOpNumThreads (4 by default)
    // Do NOT use OpenVINO (Broadwell).
    // On model load failure — log + throw (ONNX-fail → Exit 1 per SPEC §7).
    # endregion

    # region EmbedAsync
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // 1. Validate: text != null
        // 2. Tokenize: Tokenizer.Create(tokenizerPath) — cache in constructor
        //    - Encode(text) → tokens
        //    - Truncate to MaxSequenceLength
        //    - Build input_ids, attention_mask, token_type_ids
        // 3. Create DenseTensor<long> for three inputs [1, seq_len]
        // 4. session.Run(inputs) → results
        // 5. Extract LastHiddenState [1, seq_len, 384]
        // 6. Mean Pooling accounting for attention_mask:
        //    sum(token_embeddings[i] * mask[i]) / sum(mask)
        // 7. L2-normalization: vector[i] / sqrt(sum(vector[i]^2))
        // 8. Return float[384]
    }
    # endregion

    # region MeanPooling
    private static float[] MeanPooling(DenseTensor<float> lastHiddenState, long[] attentionMask);
    # endregion

    # region L2Normalize
    private static void L2Normalize(float[] vector);
    # endregion

    # region Dispose
    public void Dispose() => _session.Dispose();
    # endregion
}
```

### Scripts/Download-Model.ps1

```powershell
# Download paraphrase-multilingual-MiniLM-L12-v2 (ONNX) from HuggingFace
# Target: src/McpMemoryService/Models/model.onnx + tokenizer.json
$ErrorActionPreference = "Stop"
$modelDir = Join-Path $PSScriptRoot "..\Models"
New-Item -ItemType Directory -Path $modelDir -Force | Out-Null

# URL:
#   https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2
#   Converted ONNX: huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2
# Files:
#   onnx/model.onnx → Models/model.onnx
#   tokenizer.json → Models/tokenizer.json

$onnxUrl = "https://huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/onnx/model.onnx"
$tokenizerUrl = "https://huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/tokenizer.json"

Invoke-WebRequest -Uri $onnxUrl -OutFile (Join-Path $modelDir "model.onnx")
Invoke-WebRequest -Uri $tokenizerUrl -OutFile (Join-Path $modelDir "tokenizer.json")
Write-Host "Model downloaded to $modelDir"
```

### Scripts/download-model.sh — equivalent for Linux/Docker (wget/curl)

### Models/.gitignore
```
model.onnx
tokenizer.json
```

### Registration in Program.cs
```csharp
services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
```

## Algorithm / Logic

### Step 1: Create model download scripts
1. Create `Scripts/Download-Model.ps1` and `Scripts/download-model.sh`.
2. Run `Download-Model.ps1` to download the model into `src/McpMemoryService/Models/`.
3. Verify: `model.onnx` (~90-120MB), `tokenizer.json` exist.
4. Add `Models/.gitignore`.

### Step 2: Implement IEmbeddingService and OnnxEmbeddingService
1. Verify `Microsoft.ML.OnnxRuntime` and `Microsoft.ML.Tokenizers` are in csproj (from M2).
2. Implement constructor: accept `IOptions<OnnxModelOptions>`, `ILogger`. Create `InferenceSession` with CPU provider and thread options.
3. Implement `EmbedAsync` per the algorithm above.
4. Implement `MeanPooling` and `L2Normalize` as private static methods.
5. Logging via `ILogger` with `[IMP:1]`..`[IMP:5]` markers (LDD):
   - `[IMP:1]` EmbedAsync entry (text length)
   - `[IMP:2]` Tokenization complete (token count)
   - `[IMP:3]` Inference complete (duration)
   - `[IMP:4]` Mean pooling complete
   - `[IMP:5]` L2 norm complete (norm value — should be ~1.0)

### Step 3: DI registration
Add `services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();` in Program.cs.

### Step 4: Unit tests
Tests use the real model (not a mock), since mocking ONNX inference is pointless. The model must be downloaded in step 1.

## Tests (unit, inline)

### tests/.../Services/OnnxEmbeddingServiceTests.cs
```csharp
public class OnnxEmbeddingServiceTests : IDisposable
{
    private readonly OnnxEmbeddingService _service;

    public OnnxEmbeddingServiceTests()
    {
        // Arrange: IOptions<OnnxModelOptions> with paths to Models/model.onnx
        // Create the service
    }

    [Fact]
    public void Dimension_Returns384()
    {
        Assert.Equal(384, _service.Dimension);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsVectorOf384()
    {
        var result = await _service.EmbedAsync("test text");
        Assert.Equal(384, result.Length);
    }

    [Fact]
    public async Task EmbedAsync_VectorIsL2Normalized()
    {
        var result = await _service.EmbedAsync("test text");
        var norm = Math.Sqrt(result.Sum(x => x * x));
        Assert.Equal(1.0, norm, precision: 4);  // ~1.0 with tolerance
    }

    [Fact]
    public async Task EmbedAsync_SimilarTexts_ProduceSimilarVectors()
    {
        var v1 = await _service.EmbedAsync("error when working with threads");
        var v2 = await _service.EmbedAsync("memory leak in threads");
        var v3 = await _service.EmbedAsync("borscht recipe");
        var sim12 = CosineSimilarity(v1, v2);  // DotProduct, since normalized
        var sim13 = CosineSimilarity(v1, v3);
        Assert.True(sim12 > sim13, "Similar texts must yield higher cosine similarity");
    }

    [Fact]
    public async Task EmbedAsync_NullText_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.EmbedAsync(null!));
    }

    [Fact]
    public async Task EmbedAsync_EmptyText_ReturnsValidVector()
    {
        var result = await _service.EmbedAsync("");
        Assert.Equal(384, result.Length);
    }

    private static float CosineSimilarity(float[] a, float[] b)
        => a.Zip(b).Sum(p => p.First * p.Second);

    public void Dispose() => _service.Dispose();
}
```

## Acceptance Criteria
- [ ] `dotnet build` — OK
- [ ] `dotnet test` — all ONNX tests PASS (model downloaded)
- [ ] `EmbedAsync` returns float[384] for any non-empty text
- [ ] L2-norm of vector ≈ 1.0 (precision 4 digits)
- [ ] Similar texts yield higher cosine similarity than dissimilar ones
- [ ] SessionOptions: CPU provider, InterOpNumThreads=1, IntraOpNumThreads=4
- [ ] Model download script works (PowerShell + bash)
- [ ] `Models/model.onnx` and `Models/tokenizer.json` are in .gitignore
- [ ] Logs contain `[IMP:1]`..`[IMP:5]` markers
- [ ] On missing model — clear error in log + throw

## Context for @code
- Read: `SPEC.md` §5.1 (OnnxEmbeddingService spec), §7 (ONNX error handling)
- Read: `milestones/M1-foundation-spec-corrections.md` (S2: tags, S3: agent_role index — indirect context)
- Skills: `csharp-conventions` (#region, XML docs, LDD logging, Singleton DI)
- Previous artifacts: M2 (OnnxModelOptions, csproj with packages), M3 (models)
- External prerequisite: HuggingFace access (for model download script)
- Web search: if needed, verify `Microsoft.ML.OnnxRuntime` and `Microsoft.ML.Tokenizers` API for .NET 10
