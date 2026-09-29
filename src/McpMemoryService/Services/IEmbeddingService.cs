#region MODULE_CONTRACT [DOMAIN(Embedding): Semantic vectorization; CONCEPT(IEmbeddingService): contract-only; TECH(ONNX): 384-dim L2-normalized vectors]
/**
 * [GREP_SUMMARY]: IEmbeddingService EmbedAsync Dimension CancellationToken embedding contract
 * [STRUCTURE]: EmbedAsync(text, ct) -> Task<float[384]> | ArgumentNullException | InvalidOperationException
 *
 * <summary>
 * [PURPOSE]: Contract for generating L2-normalized embedding vectors from text.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Dimension is always 384 (paraphrase-multilingual-MiniLM-L12-v2).
 * [RATIONALE]: Decouples embedding producer from consumers (M7 capture, M8 retrieve) so the ONNX model can be swapped.
 * [CHANGES]: LAST_CHANGE: M4 creation (IEmbeddingService contract).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpMemoryService.Services;

/// <summary>
/// [PURPOSE]: Produces L2-normalized embedding vectors for text input.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an L2-normalized embedding vector for the specified text.
    /// </summary>
    /// <param name="text">Text to vectorize. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>L2-normalized vector of 384 dimensions.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the embedding model is not loaded.</exception>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Vector dimension (384 for paraphrase-multilingual-MiniLM-L12-v2).
    /// </summary>
    int Dimension { get; }
}
