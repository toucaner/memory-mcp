#region MODULE_CONTRACT [DOMAIN(Embedding): ONNX CPU embedding with mean pooling + L2 norm; CONCEPT(OnnxEmbeddingService): sealed singleton service; TECH(ONNX Runtime 1.27, Tokenizers 2.0)]
/**
 * [GREP_SUMMARY]: OnnxEmbeddingService EmbedAsync MeanPooling L2Normalize Dispose SessionOptions SentencePieceTokenizer IEmbeddingService RunInference NamedOnnxValue dispose EnableCpuMemArena EnableMemoryPattern AddSessionConfigEntry use_ort_model_bytes_for_initializers ORT memory knobs
  * [STRUCTURE]: ctor(Options, Logger) → build SessionOptions(CPU, InterOp=1, IntraOp=N, arena/memoryPattern/ortModelBytes knobs) → new InferenceSession(model) → [IMP:M13] effective-knobs log → LoadUnigramTokenizerFromHfJson (System.Text.Json→Sentencepiece.ModelProto via reflection→SentencePieceTokenizer.Create(stream)) → EmbedAsync(text, ct) → EncodeToIds([CLS,body,SEP]) → DenseTensor<long>[3] → RunInference(inputs) [catch scope: Run only; inputs non-disposable in ORT 1.27.0] → using results → meanPool → L2Norm → float[384]
  * [LDD]: [IMP:1][EmbedAsync][INIT] → [IMP:2][EmbedAsync][PROGRESS] → [IMP:3][EmbedAsync][MILESTONE] → [IMP:4][EmbedAsync][CHECKPOINT] → [IMP:5][EmbedAsync][CHECKPOINT] → [IMP:9][EmbedAsync][SUCCESS] | [IMP:10][EmbedAsync][FATAL] on inference fail | [IMP:10][OnnxEmbeddingService.ctor][FATAL] on load fail | [IMP:9][OnnxEmbeddingService.ctor][SUCCESS] on init | [IMP:M13][OnnxEmbeddingService.ctor][SUCCESS] ORT memory knobs
 *
 * <summary>
 * [PURPOSE]: Loads paraphrase-multilingual-MiniLM-L12-v2 into ONNX Runtime (CPU), tokenizes input, runs mean-pooling + L2 normalization, returns a 384-dim float array.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Model is loaded in the constructor (not lazy-on-first-call) so startup failure = host Exit 1 (ADR-005).
 *               EmbedAsync throws on inference failure (M10 middleware catches later; M4 does NOT swallow — ADR-005).
 *               Dimension is always 384 (ADR-011: paraphrase-multilingual-MiniLM-L12-v2).
 * [RATIONALE]: Constructor-throws-on-load is the semantic that makes ADR-005's "ONNX fail at startup → Exit 1" enforceable at the host level.
 *              InterOpNumThreads=1, IntraOpNumThreads from options (default 2 — M13 halved from 4 to bound per-thread arena allocations; SPEC §5.1 headroom intent preserved).
 * [CHANGES]: LAST_CHANGE: M4 creation (OnnxEmbeddingService).
 * [CHANGES]: 2026-06-30 @debug M4-attempt1 — replaced broken base64 SentencePiece extraction (LoadSentencePieceTokenizer + ExtractSentencePieceModelFromXenovaJson) with LoadUnigramTokenizerFromHfJson: rebuilds Sentencepiece.ModelProto from HF tokenizer.json "model.vocab" via internal reflection, feeds the serialized proto to SentencePieceTokenizer.Create(stream). Reviewer note: Tokenizer.Create(string) does NOT exist in ML.Tokenizers 2.0.0 stable; BertTokenizer.Create rejects HF Unigram JSON.
 * [CHANGES]: 2026-07-01 @debug M4-verify — confirmed LoadUnigramTokenizerFromHfJson fix is correct: all 6 Category=Integration tests PASS. Probed Tokenizer.Create(string) → CS0117 (does not exist in 2.0.0). EncodeToIds + manual attention_mask/type_ids synthesis verified correct for single-sentence Unigram embedding. BUG_FIX_CONTEXT scar added at LoadUnigramTokenizerFromHfJson method.
*  [CHANGES]: M10 @debug counter=0 — migrated the 3 remaining inline [IMP:N] string literals (ctor-success IMP:9, tokenization IMP:2, embedding-produced IMP:9) to LddMarkers constants; added 2 new constants (EmbeddingServiceInitialized, EmbeddingProduced) to LddMarkers.cs to cover the IMP:9 SUCCESS milestones documented in this file's [LDD] header.
 *  [CHANGES]: M13 — dispose hygiene (SessionOptions `using`, inference `results` `using`, `inputs` NamedOnnxValue `finally` dispose via new private RunInference helper) + 3 ORT memory knobs wired in the ctor (EnableCpuMemArena/EnableMemoryPattern/AddSessionConfigEntry "session.use_ort_model_bytes_for_initializers") + [IMP:M13] effective-knobs LDD marker (LddMarkers.EmbeddingOrtKnobs). [PROBE: 2026-09-25 — all ORT 1.27.0 knob names VERIFIED against the local package artifacts (Microsoft.ML.OnnxRuntime.xml + onnxruntime_session_options_config_keys.h); SessionOptions extends System.SafeHandle → `using var` is legal.]
 *  [CHANGES]: M13 @code probe follow-up 2026-09-25 — compile probe CONFIRMED the 3 knob lines + AddSessionConfigEntry compile clean (VERIFIED), but the spec §Contracts 2 inputs-disposal loop (`v.Dispose()`) FAILED: CS1061 — NamedOnnxValue has NO Dispose() in ORT 1.27.0 (package XML: "not disposable and can not contain any disposable items"; input wrappers hold no native state; native input OrtValue handles are session-managed). Dropped the finally loop per the probe protocol (no guessed API substitution); `using var results` (output disposal) + SessionOptions `using` remain the M13 RSS wins. PROBE scar documented on the RunInference helper.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Services;

using McpMemoryService.Configuration;
using McpMemoryService.Logging;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// ONNX-based embedding service: loads paraphrase-multilingual-MiniLM-L12-v2, tokenizes, infers, mean-pools, L2-normalizes.
/// Singleton — owned by the DI container, disposed at host shutdown.
/// </summary>
/// <remarks>
/// Implements <see cref="IEmbeddingService"/> with <see cref="IDisposable"/>.
/// Constructor loads model + tokenizer synchronously; failure throws (host Exit 1 path per ADR-005).
/// Runtime inference failures are logged and rethrown (M10 middleware catches later).
/// </remarks>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    #region Fields

    private readonly InferenceSession? _session;
    private readonly Tokenizer _tokenizer;
    private readonly ILogger<OnnxEmbeddingService> _logger;

    #endregion Fields

    #region Constants

    /// <summary>Vector dimension — always 384 for paraphrase-multilingual-MiniLM-L12-v2.</summary>
    public int Dimension => 384;

    /// <summary>Maximum sequence length for tokenization (truncates beyond this).</summary>
    private const int MaxSequenceLength = 256;

    #endregion Constants

    #region Constructors

    /// <summary>
    /// Constructs an <see cref="OnnxEmbeddingService"/> by loading the ONNX model and tokenizer.
    /// </summary>
    /// <param name="options">ONNX model configuration (model path, tokenizer path, thread count).</param>
    /// <param name="logger">Logger for embedding pipeline diagnostics.</param>
    /// <exception cref="InvalidOperationException">Thrown when the model or tokenizer file does not exist at the resolved path.</exception>
    public OnnxEmbeddingService(IOptions<OnnxModelOptions> options, ILogger<OnnxEmbeddingService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(opts.ModelPath))
        {
            throw new InvalidOperationException("OnnxModelOptions.ModelPath must not be null or empty.");
        }
        if (string.IsNullOrWhiteSpace(opts.TokenizerPath))
        {
            throw new InvalidOperationException("OnnxModelOptions.TokenizerPath must not be null or empty.");
        }

        // Resolve paths relative to AppContext.BaseDirectory (handles relative appsettings paths + test cwd).
        var modelPath = Path.IsPathRooted(opts.ModelPath)
            ? opts.ModelPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, opts.ModelPath));
        var tokenizerPath = Path.IsPathRooted(opts.TokenizerPath)
            ? opts.TokenizerPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, opts.TokenizerPath));

        if (!File.Exists(modelPath))
        {
            var msg = $"ONNX model file not found: {modelPath}";
            logger.LogCritical("{Marker} {Message}", LddMarkers.OnnxFatal + "[OnnxEmbeddingService.ctor][FATAL]", msg);
            throw new InvalidOperationException(msg);
        }
        if (!File.Exists(tokenizerPath))
        {
            var msg = $"Tokenizer file not found: {tokenizerPath}";
            logger.LogCritical("{Marker} {Message}", LddMarkers.OnnxFatal + "[OnnxEmbeddingService.ctor][FATAL]", msg);
            throw new InvalidOperationException(msg);
        }

        // Build SessionOptions per SPEC §5.1 + M13 memory knobs: CPU provider, InterOp=1, IntraOp=2 (default).
        // M13: SessionOptions wraps native state — dispose after InferenceSession copies what it needs (documented-safe pattern).
        // BUG_FIX_CONTEXT: [PROBE: 2026-09-25 — ORT 1.27.0 knob names VERIFIED via local package artifacts (Microsoft.ML.OnnxRuntime.xml
        // + onnxruntime_session_options_config_keys.h): EnableCpuMemArena/EnableMemoryPattern/AddSessionConfigEntry(String,String) all exist;
        // "session.use_ort_model_bytes_for_initializers" key confirmed. SessionOptions extends System.SafeHandle → `using var` is legal.]
        using var sessionOptions = new SessionOptions();
        sessionOptions.AppendExecutionProvider_CPU(0);
        sessionOptions.InterOpNumThreads = 1;
        sessionOptions.IntraOpNumThreads = opts.IntraOpNumThreads;
        sessionOptions.EnableCpuMemArena = opts.EnableCpuMemArena;
        sessionOptions.EnableMemoryPattern = opts.EnableMemoryPattern;
        sessionOptions.AddSessionConfigEntry("session.use_ort_model_bytes_for_initializers",
            opts.UseOrtModelBytesForInitializers ? "1" : "0");

        // BUG_FIX_CONTEXT: [HYPOTHESIS: OnnxEmbeddingService ctor fails at tokenizer load — InvalidProtocolBufferException from SentencePieceTokenizer.Create. Root cause: LoadSentencePieceTokenizer + ExtractSentencePieceModelFromXenovaJson operates on FALSE PREMISE — assumed tokenizer.json "model" field is a base64 SentencePiece .model blob. Actual structure (verified at token.json line 167): "model": { "type":"Unigram", "unk_id":3, "vocab":[[token,score]... 250002 entries] }. @code was MISLED by tests/test_guide.md note #15 (M4) which falsely claimed the base64 embedding.]
        // BUG_FIX_CONTEXT: [RESOLVED 2026-06-30 @debug M4 attempt 1: The architect's planned fallbacks (Tokenizer.Create(path) / BertTokenizer.Create(path, options)) were PROBED against Microsoft.ML.Tokenizers 2.0.0 — NEITHER exists/works: Tokenizer.Create(string) does not exist in stable 2.0.0 (latest stable as of mid-2026), and BertTokenizer.Create parses tokenizer.json line-by-line as a BERT vocab.txt (WordPiece), throwing duplicate-key ArgumentException. SentencePieceUnigramModel is internal. The PRINCIPLED FIX keeps the only public Unigram-capable factory — SentencePieceTokenizer.Create(Stream modelStream, ...) — and synthesises a SentencePiece ModelProto at runtime from the HF tokenizer.json "model.vocab" array via internal reflection on the Sentencepiece.* proto types + Google.Protobuf's public IMessage.ToByteArray(), then feeds the resulting stream to that factory. EncodeToIds(text, considerPreTokenization:true, considerNormalization:true) is compatible and yields [CLS, body, SEP]. This preserves ADR-005 (ctor-throws-on-load) and pins to the 2.0.0 internal layout — DO NOT reintroduce the base64-extraction path or switch to BertTokenizer.Create for this Unigram model.]
        try
        {
            _session = new InferenceSession(modelPath, sessionOptions);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "{Marker} ONNX model load failed: {Path}", LddMarkers.OnnxFatal + "[OnnxEmbeddingService.ctor][FATAL]", modelPath);
            throw;
        }

        // M13: log the effective ORT memory knobs (single-source marker per M10 invariant) — AC-11 grep target.
        // Reached only on session-load success (the catch above rethrows on failure).
        _logger.LogInformation("{Marker} arena={Arena}, memoryPattern={Pattern}, intraOpThreads={Threads}, useOrtModelBytes={ModelBytes}",
            LddMarkers.EmbeddingOrtKnobs, opts.EnableCpuMemArena, opts.EnableMemoryPattern, opts.IntraOpNumThreads, opts.UseOrtModelBytesForInitializers);

        try
        {
            // Tokenizer is a SentencePiece Unigram model reconstructed from the HF tokenizer.json (see
            // BUG_FIX_CONTEXT scar above + the LoadUnigramTokenizerFromHfJson helper). Build is non-lazy by design
            // so any load failure rethrows here (ADR-005: ONNX fail at startup → Exit 1).
            _tokenizer = LoadUnigramTokenizerFromHfJson(tokenizerPath);
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            logger.LogCritical(ex, "{Marker} Tokenizer load failed: {Path}", LddMarkers.OnnxFatal + "[OnnxEmbeddingService.ctor][FATAL]", tokenizerPath);
            throw;
        }

        // BUG_FIX_CONTEXT: [HYPOTHESIS: Inline [IMP:9]...[SUCCESS] literal had no LddMarkers constant; replaced with the structured {Marker} placeholder + LddMarkers.EmbeddingServiceInitialized per M10 single-source invariant. The LDD header [LDD] line already documents IMP:9 for ctor-success.]
        _logger.LogInformation("{Marker} ONNX embedding service initialized (model={ModelPath}, tokenizer={TokenizerPath})", LddMarkers.EmbeddingServiceInitialized, modelPath, tokenizerPath);
    }

    #endregion Constructors

    #region Public

    /// <summary>
    /// Generates an L2-normalized embedding vector for the specified text.
    /// </summary>
    /// <param name="text">Text to vectorize. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>L2-normalized vector of 384 dimensions.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the embedding model is not loaded.</exception>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_session == null)
        {
            _logger.LogCritical("{Marker} Session not loaded", LddMarkers.OnnxFatal + "[OnnxEmbeddingService.EmbedAsync][FATAL]");
            throw new InvalidOperationException("ONNX embedding model is not loaded.");
        }

        // [IMP:1] Entry marker
        _logger.LogInformation("{Marker} Entry: text length={Len}", LddMarkers.EmbeddingEntry, text.Length);

        // Tokenize using EncodeToIds (returns IReadOnlyList<int>)
        var tokenIds = _tokenizer.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true);

        // Truncate to max sequence length
        var seqLen = Math.Min(tokenIds.Count, MaxSequenceLength);

        // Build DenseTensor<long>[1, seqLen] for each input
        // For single-sentence embedding, attention_mask = all 1s, token_type_ids = all 0s.
        var idsArray = new long[seqLen];
        var maskArray = new long[seqLen];
        var typeIdsArray = new long[seqLen];

        for (var i = 0; i < seqLen; i++)
        {
            idsArray[i] = (long)tokenIds[i];
            maskArray[i] = 1; // all tokens are "real" (no padding in single sentence)
            typeIdsArray[i] = 0; // single sentence → all type 0
        }

        var idsTensor = new DenseTensor<long>(idsArray, new[] { 1, seqLen });
        var maskTensor = new DenseTensor<long>(maskArray, new[] { 1, seqLen });
        var typeIdsTensor = new DenseTensor<long>(typeIdsArray, new[] { 1, seqLen });

        // [IMP:2] Tokenization marker
        // BUG_FIX_CONTEXT: [HYPOTHESIS: Inline [IMP:2][EmbedAsync][PROGRESS] literal duplicates LddMarkers.EmbeddingTokenized; replaced with the structured {Marker} placeholder + constant per M10 single-source invariant. Custom "Tokenization complete" suffix preserved in the message body.]
        _logger.LogInformation("{Marker} Tokenization complete: token count={Count}", LddMarkers.EmbeddingTokenized, seqLen);

        // Wrap as NamedOnnxValue inputs
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", typeIdsTensor)
        };

        // Run inference
        var sw = Stopwatch.StartNew();
        // M13: outputs disposed deterministically (using). Input wrappers are non-disposable in ORT 1.27.0 (probe-dropped, see RunInference scar).
        using var results = RunInference(inputs);

        sw.Stop();
        _logger.LogInformation("{Marker} Inference complete in {Ms} ms", LddMarkers.EmbeddingInference, sw.ElapsedMilliseconds);

        // Extract LastHiddenState — output node name is "last_hidden_state" for the Xenova sentence-transformers export.
        // _session.OutputMetadata contains the actual output names; use the first (and typically only) output.
        var outputName = _session.OutputMetadata.Keys.First();
        var output = results.First(r => r.Name == outputName);

        var lhsTensor = output.AsTensor<float>();

        // lhsTensor should be DenseTensor<float>[1, seqLen, 384].
        // Access via indexing: [batch, seq, dim]
        var lhs = (DenseTensor<float>)lhsTensor;

        // Mean pooling with attention mask
        var pooled = MeanPooling(lhs, maskArray);

        _logger.LogInformation("{Marker} Mean pooling complete: tokens={TokenCount}", LddMarkers.EmbeddingMeanPool, maskArray.Count(m => m > 0));

        // L2 normalization
        L2Normalize(pooled);

        _logger.LogInformation("{Marker} L2 norm complete: norm={Norm:F6}", LddMarkers.EmbeddingL2Norm, ComputeL2Norm(pooled));

        // BUG_FIX_CONTEXT: [HYPOTHESIS: Inline [IMP:9][EmbedAsync][SUCCESS] literal had no LddMarkers constant; replaced with the structured {Marker} placeholder + LddMarkers.EmbeddingProduced per M10 single-source invariant. The LDD header [LDD] line documents IMP:9 for EmbedAsync-success.]
        _logger.LogInformation("{Marker} Embedding produced: dim={Dim}, norm={Norm:F4}", LddMarkers.EmbeddingProduced, Dimension, ComputeL2Norm(pooled));

        return pooled;
    }

    #endregion Public

    #region Private

    /// <summary>
    /// Loads an <b>Unigram</b> SentencePiece tokenizer from a HuggingFace/Xenova <c>tokenizer.json</c>.
    /// </summary>
    /// <param name="tokenizerPath">Absolute path to a HF tokenizer.json whose <c>model.type == "Unigram"</c>.</param>
    /// <returns>A <see cref="Tokenizer"/> that emits <c>[CLS, body, SEP]</c> ids for a single sentence.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <c>model.type</c> is not Unigram.</exception>
    /// <remarks>
    /// <b>BUG_FIX_CONTEXT:</b> The original M4 implementation (<c>LoadSentencePieceTokenizer</c> +
    /// <c>ExtractSentencePieceModelFromXenovaJson</c>) failed because it assumed <c>tokenizer.json</c>'s
    /// <c>"model"</c> field contained a base64-encoded SentencePiece <c>.model</c> binary blob. In reality,
    /// the Xenova export stores a JSON object <c>{"type":"Unigram","unk_id":N,"vocab":[[token,score],...]}</c>.
    /// The old code naively base64-decoded whatever followed <c>IndexOf("\"model\"")</c>, producing garbage that
    /// caused <see cref="InvalidProtocolBufferException"/> in <c>SentencePieceTokenizer.Create</c>.
    /// <para>
    /// <b>Why this solution was chosen:</b>
    /// <list type="bullet">
    /// <item><c>Tokenizer.Create(string path)</c> does NOT exist in Microsoft.ML.Tokenizers 2.0.0 (verified via
    /// compilation probe — CS0117 "Tokenizer does not contain a definition for Create").</item>
    /// <item><c>BertTokenizer.Create(path, options)</c> parses the file as a BERT WordPiece vocab.txt (line-by-line),
    /// throwing <c>ArgumentException: An item with the same key has already been added</c> on JSON content.</item>
    /// <item><c>SentencePieceUnigramModel</c> is <c>internal</c> — cannot be constructed directly.</item>
    /// <item>The ONLY public Unigram-capable factory is
    /// <see cref="SentencePieceTokenizer.Create(Stream, bool, bool, IReadOnlyDictionary{string,int})"/>,
    /// which requires a binary SentencePiece <c>.model</c> protobuf stream.</item>
    /// </list>
    /// Therefore this method reconstructs a <c>Sentencepiece.ModelProto</c> from the JSON vocab array via
    /// internal reflection, serialises it through <see cref="IMessage.ToByteArray"/>, and feeds the result
    /// to <c>SentencePieceTokenizer.Create</c>. Pinned to Microsoft.ML.Tokenizers 2.0.0 internal layout.
    /// DO NOT reintroduce the base64-extraction path or switch to <c>BertTokenizer.Create</c> for Unigram models.
    /// </para>
    /// </remarks>
    private static Tokenizer LoadUnigramTokenizerFromHfJson(string tokenizerPath)
    {
        // Parse the HF tokenizer.json structure (System.Text.Json streaming parser — no base64 / no protobuf in the file).
        using var doc = JsonDocument.Parse(File.OpenRead(tokenizerPath), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var model = doc.RootElement.GetProperty("model");
        var modelType = model.GetProperty("type").GetString();
        if (!string.Equals(modelType, "Unigram", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported HF tokenizer model.type '{modelType}' in {tokenizerPath}; this loader only supports Unigram.");
        }

        var unkId = model.GetProperty("unk_id").GetInt32();
        var vocab = model.GetProperty("vocab").EnumerateArray()
            .Select(e => (Token: e[0].GetString() ?? string.Empty, Score: (float)e[1].GetDouble()))
            .ToList();

        if (vocab.Count == 0)
        {
            throw new InvalidOperationException($"Tokenizer file contains an empty vocab: {tokenizerPath}");
        }

        // added_tokens flagged special=true → SentencePiece CONTROL pieces; the rest stay NORMAL.
        var specialPieces = doc.RootElement.GetProperty("added_tokens").EnumerateArray()
            .Where(a => a.GetProperty("special").GetBoolean())
            .Select(a => a.GetProperty("content").GetString() ?? string.Empty)
            .ToHashSet();

        // ---- Build Sentencepiece.ModelProto via reflection (internal types in Microsoft.ML.Tokenizers 2.0.0). ----
        var tokenizerAssembly = typeof(Tokenizer).Assembly;
        var modelProtoT = tokenizerAssembly.GetType("Sentencepiece.ModelProto")
            ?? throw new InvalidOperationException("Sentencepiece.ModelProto type not found in Microsoft.ML.Tokenizers 2.0.0.");
        var trainerSpecT = tokenizerAssembly.GetType("Sentencepiece.TrainerSpec")
            ?? throw new InvalidOperationException("Sentencepiece.TrainerSpec type not found.");
        var normalizerSpecT = tokenizerAssembly.GetType("Sentencepiece.NormalizerSpec")
            ?? throw new InvalidOperationException("Sentencepiece.NormalizerSpec type not found.");
        var piecesProp = modelProtoT.GetProperty("Pieces")
            ?? throw new InvalidOperationException("ModelProto.Pieces property not found.");
        var pieceT = piecesProp.PropertyType.GetGenericArguments()[0];
        var pieceTypeT = pieceT.GetNestedType("Type");   // Piece.Type enum
        var modelTypeEnumT = trainerSpecT.GetNestedType("ModelType"); // TrainerSpec.ModelType enum

        object modelProto = Activator.CreateInstance(modelProtoT, nonPublic: true)!;
        object trainerSpec = Activator.CreateInstance(trainerSpecT, nonPublic: true)!;
        object normalizerSpec = Activator.CreateInstance(normalizerSpecT, nonPublic: true)!;
        modelProtoT.GetProperty("TrainerSpec")!.SetValue(modelProto, trainerSpec);
        modelProtoT.GetProperty("NormalizerSpec")!.SetValue(modelProto, normalizerSpec);

        // TrainerSpec: Unigram(1); ids come from the HF vocab ordering (0=<s>, 1=<pad>, 2=</s>, 3=<unk>, ..., 250001=<mask>).
        SetEnumOrInt(trainerSpecT.GetProperty("ModelType"), trainerSpec, 1);
        SetInt(trainerSpecT.GetProperty("UnkId"), trainerSpec, unkId);
        SetInt(trainerSpecT.GetProperty("VocabSize"), trainerSpec, vocab.Count);
        SetInt(trainerSpecT.GetProperty("BosId"), trainerSpec, 0);
        SetInt(trainerSpecT.GetProperty("EosId"), trainerSpec, 2);
        SetInt(trainerSpecT.GetProperty("PadId"), trainerSpec, 1);
        SetString(trainerSpecT.GetProperty("UnkPiece"), trainerSpec, "<unk>");
        SetString(trainerSpecT.GetProperty("BosPiece"), trainerSpec, "<s>");
        SetString(trainerSpecT.GetProperty("EosPiece"), trainerSpec, "</s>");
        SetString(trainerSpecT.GetProperty("PadPiece"), trainerSpec, "<pad>");

        // NormalizerSpec: identity normalisation (HF JSON already carries pre/post-processing through the
        // SentencePiece pipeline when considerPreTokenization/considerNormalization are true on EncodeToIds).
        SetString(normalizerSpecT.GetProperty("Name"), normalizerSpec, "identity");
        SetBool(normalizerSpecT.GetProperty("AddDummyPrefix"), normalizerSpec, true);
        SetBool(normalizerSpecT.GetProperty("EscapeWhitespaces"), normalizerSpec, true);
        SetBool(normalizerSpecT.GetProperty("RemoveExtraWhitespaces"), normalizerSpec, true);

        // Pieces: vocab ordering == id ordering (positional).
        const int ptNormal = 1; // NORMAL
        const int ptUnknown = 2; // UNKNOWN
        const int ptControl = 3; // CONTROL
        var piecesField = piecesProp.GetValue(modelProto)!;
        var addMethod = piecesField.GetType().GetMethod("Add", new[] { pieceT })
            ?? throw new InvalidOperationException("RepeatedField<Piece>.Add(Piece) method not found.");
        var piecePieceProp = pieceT.GetProperty("Piece");
        var pieceScoreProp = pieceT.GetProperty("Score");
        var pieceTypeProp = pieceT.GetProperty("Type");
        if (piecePieceProp is null || pieceScoreProp is null || pieceTypeProp is null)
        {
            throw new InvalidOperationException("Sentencepiece.Piece proto accessors not found (Piece/Score/Type).");
        }
        for (var i = 0; i < vocab.Count; i++)
        {
            var (token, score) = vocab[i];
            var piece = Activator.CreateInstance(pieceT, nonPublic: true)!;
            piecePieceProp.SetValue(piece, token);
            pieceScoreProp.SetValue(piece, score);
            var pieceType = (token == "<unk>" || (unkId >= 0 && unkId < vocab.Count && token == vocab[unkId].Token))
                ? ptUnknown
                : (specialPieces.Contains(token) ? ptControl : ptNormal);
            SetEnumOrInt(pieceTypeProp, piece, pieceType, pieceTypeT);
            addMethod.Invoke(piecesField, new[] { piece });
        }

        // Serialize via the public Google.Protobuf IMessage surface and feed to the SentencePieceTokenizer factory.
        var protoBytes = ((IMessage)modelProto).ToByteArray();
        return SentencePieceTokenizer.Create(new MemoryStream(protoBytes), addBeginningOfSentence: true, addEndOfSentence: true, specialTokens: null);
    }

    private static void SetInt(PropertyInfo? property, object target, int value)
    {
        property?.SetValue(target, value);
    }

    private static void SetString(PropertyInfo? property, object target, string value)
    {
        property?.SetValue(target, value);
    }

    private static void SetBool(PropertyInfo? property, object target, bool value)
    {
        property?.SetValue(target, value);
    }

    private static void SetEnumOrInt(PropertyInfo? property, object target, int value, Type? enumType = null)
    {
        if (property is null)
        {
            return;
        }
        var type = enumType ?? property.PropertyType;
        if (type.IsEnum)
        {
            property.SetValue(target, Enum.ToObject(type, value));
        }
        else
        {
            property.SetValue(target, value);
        }
    }

    /// <summary>
    /// Attention-masked mean pooling over the last hidden state tensor.
    /// </summary>
    /// <param name="lastHiddenState">Shape [batch=1, seqLen, dim=384].</param>
    /// <param name="attentionMask">Attention mask values (1 for real tokens, 0 for padding).</param>
    /// <returns>Mean-pooled vector of 384 dimensions.</returns>
    /// <remarks>
    /// Formula: pooled[j] = sum_i(lhs[i, j] * maskFloat[i]) / sum(maskFloat),
    /// where maskFloat[i] = attentionMask[i] != 0 ? 1f : 0f.
    /// Zero-guard: if all masks are zero, returns zeros of length 384.
    /// </remarks>
    private static float[] MeanPooling(DenseTensor<float> lastHiddenState, long[] attentionMask)
    {
        var seqLen = lastHiddenState.Dimensions[1];
        var dim = lastHiddenState.Dimensions[2]; // 384

        var sum = new float[dim];
        float countSum = 0f;

        for (var i = 0; i < seqLen; i++)
        {
            float mask = attentionMask[i] != 0 ? 1f : 0f; // explicit float, not long
            for (var j = 0; j < dim; j++)
            {
                // Output tensor is rank-3 [batch=1, seqLen, 384]; index all three dims to walk row-major
                // (using a 2-element indexer would incorrectly bind to strides[0]=seqLen*384, OOB at i≥1).
                sum[j] += lastHiddenState[0, i, j] * mask;
                countSum += mask;
            }
        }

        var denom = countSum > 0 ? countSum : 1f;
        var pooled = new float[dim];
        for (var j = 0; j < dim; j++)
        {
            pooled[j] = sum[j] / denom;
        }

        return pooled;
    }

    /// <summary>
    /// In-place L2 normalization of a vector.
    /// </summary>
    /// <param name="vector">Vector to normalize (modified in place).</param>
    /// <remarks>
    /// Uses double accumulation for numerical stability (float accumulator drifts on 384 sums).
    /// If norm &lt; 1e-12 (zero vector), leaves the vector as-is — avoids NaN from division.
    /// </remarks>
    private static void L2Normalize(float[] vector)
    {
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSq += (double)vector[i] * vector[i];
        }
        var norm = Math.Sqrt(sumSq);
        if (norm > 1e-12)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }
    }

    /// <summary>Computes the L2 norm of a vector (for logging / verification).</summary>
    private static double ComputeL2Norm(float[] vector)
    {
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSq += (double)vector[i] * vector[i];
        }
        return Math.Sqrt(sumSq);
    }

    /// <summary>
    /// Runs inference, preserving the M13 catch-scope contract (the <c>OnnxFatal</c> log wraps the <c>Run</c> call ONLY).
    /// </summary>
    /// <param name="inputs">NamedOnnxValue input wrappers created by the caller. NOT disposable in ORT 1.27.0 (see remarks).</param>
    /// <returns>The native-backed output collection (caller must dispose it, e.g. <c>using var results = RunInference(inputs)</c>).</returns>
    /// <remarks>
    /// The <c>catch</c> scope is the M13 contract: it wraps ONLY <c>_session.Run</c> — pooling/L2/logging failures
    /// must NEVER be logged as "Inference failed" <c>[FATAL]</c>.
    /// <para>
    /// <b>BUG_FIX_CONTEXT: [PROBE: 2026-09-25 @code compile-probe]</b> The M13 spec §Contracts 2 required
    /// <c>foreach (var v in inputs) v.Dispose()</c> in a <c>finally</c>. The compile probe FAILED: CS1061 —
    /// <c>NamedOnnxValue</c> has NO <c>Dispose()</c> in ORT 1.27.0. Verified against the restored package API doc
    /// (<c>Microsoft.ML.OnnxRuntime.xml</c>, line 882): "The problem with NamedOnnxValue is that it is not disposable
    /// and can not contain any disposable items." <c>NamedOnnxValue.CreateFromTensor</c> wrappers hold NO native state —
    /// the native input <c>OrtValue</c> handles are created transiently inside <c>Run()</c> and disposed by the session
    /// machinery. Only <c>DisposableNamedOnnxValue</c> (the OUTPUT type returned by <c>Run</c>) has <c>Dispose()</c>,
    /// which <c>using var results</c> already handles. Per the M13 probe protocol (do NOT substitute a guessed API),
    /// the inputs-disposal loop was DROPPED — the primary RSS win (deterministic output disposal) is intact.
    /// </para>
    /// <c>_session</c> is null-checked at <c>EmbedAsync</c> entry; this helper is only called from there, so <c>_session!</c> is safe.
    /// </remarks>
    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunInference(IReadOnlyList<NamedOnnxValue> inputs)
    {
        try
        {
            return _session!.Run(inputs);
        }
        catch (Exception ex)
        {
            _logger.LogCritical("{Marker} Inference failed: {Error}", LddMarkers.OnnxFatal + "[FATAL]", ex.Message);
            throw;
        }
    }

    #endregion Private

    #region IDisposable

    /// <summary>
    /// Disposes the ONNX inference session.
    /// </summary>
    /// <remarks>
    /// Singleton — owned by the DI container. Plain Dispose (no full pattern, no finalizer)
    /// because the session is a managed wrapper over native memory.
    /// </remarks>
    public void Dispose()
    {
        _session?.Dispose();
    }

    #endregion IDisposable
}
