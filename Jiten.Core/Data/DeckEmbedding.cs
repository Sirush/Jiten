namespace Jiten.Core.Data;

/// <summary>
/// Dense FastText vocabulary embedding for a parent deck (the L2-normalized, SIF-processed
/// vector). Stored in Postgres so it survives restarts and loads into RAM at startup without
/// needing the FastText model. Similarity is computed on the fly from these vectors.
/// </summary>
public class DeckEmbedding
{
    /// <summary>Parent deck this embedding belongs to.</summary>
    public int DeckId { get; set; }

    /// <summary>The dense vector as little-endian float32 bytes (length = Dimension * 4).</summary>
    public byte[] Vector { get; set; } = [];

    /// <summary>
    /// The deck's "distinctive vocabulary signature": its top-N content WordIds by IDF, sorted
    /// ascending, as little-endian int32 bytes. Used to gate short/saturated-regime decks by
    /// distinctive-vocabulary overlap (cheap in-RAM set intersection). Null until first computed.
    /// </summary>
    public byte[]? Signature { get; set; }
}

/// <summary>
/// The single-row SIF "all-but-the-top" transform fitted on the last full rebuild. Persisted so
/// individual decks added/reparsed between rebuilds can be embedded into the same vector space
/// (subtract <see cref="Mean"/>, project out <see cref="Components"/>) without a full recompute.
/// </summary>
public class DeckEmbeddingSpace
{
    /// <summary>Always 1 — single-row table.</summary>
    public int Id { get; set; }

    /// <summary>Vector dimension (e.g. 300).</summary>
    public int Dimension { get; set; }

    /// <summary>Dataset mean as little-endian float32 bytes (length = Dimension * 4).</summary>
    public byte[] Mean { get; set; } = [];

    /// <summary>The removed principal components concatenated as little-endian float32 bytes (length = K * Dimension * 4).</summary>
    public byte[] Components { get; set; } = [];

    /// <summary>
    /// The IDF word-weight table from the last full build, as repeated (int WordId, float Idf) little-endian
    /// records. Lets incremental embedding weight new decks' words identically without re-querying the corpus.
    /// </summary>
    public byte[] Idf { get; set; } = [];
}
