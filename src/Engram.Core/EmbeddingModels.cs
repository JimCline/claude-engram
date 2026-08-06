namespace Engram.Core;

/// <summary>
/// Where a model file comes from, and what it must hash to.
/// </summary>
/// <remarks>
/// Pinned by digest for the reason <c>fetch-vec0.sh</c> is: this file is loaded into Engram's
/// own process. Null until the digest has been checked against a real download — a registry row
/// with an invented hash is worse than one with no hash, because the first looks verified.
/// </remarks>
public sealed record ModelSource(string Repository, string File, string? Sha256);

/// <summary>One local embedding model Engram knows how to run.</summary>
public sealed record EmbeddingModel(
    string Id,
    string DisplayName,
    int Dimensions,
    int ContextTokens,
    long ApproximateBytes,
    string Languages,
    string Tradeoff,
    ModelSource? Source)
{
    /// <summary>The file this model occupies inside <see cref="EngramHome.ModelsDir"/>.</summary>
    public string FileName => Source?.File ?? $"{Id}.gguf";

    /// <summary>Whether the artifact is pinned well enough to fetch.</summary>
    public bool IsFetchable => Source?.Sha256 is { Length: > 0 };

    public string SizeLabel => ApproximateBytes >= 1_000_000_000L
        ? $"{ApproximateBytes / 1_000_000_000d:0.0} GB"
        : $"{ApproximateBytes / 1_000_000d:0} MB";
}

/// <summary>
/// The local models offered at install, smallest first.
/// </summary>
/// <remarks>
/// <para>One registry, and adding a model is one row here. Nothing else — not the installer,
/// not <c>doctor</c>, not the config writer — may keep its own copy of this list or branch on a
/// specific id; if it does, the abstraction has not landed and the next model costs five edits
/// instead of one.</para>
///
/// <para><b>Why a ladder and not one default.</b> The vector lane is optional (D18), so the
/// question at install is not "which model is best" but "what will this machine actually run
/// without the user turning the feature off". A 610 MB download and ~600M parameters is the
/// wrong first answer on a laptop with 8 GB of RAM, and the failure mode is not a slow install
/// — it is a user who disables embeddings and never comes back. The bottom rung exists so that
/// answer is never "none".</para>
///
/// <para><b>Dimensions are a cost, not just a quality knob.</b> Every vector is
/// <c>dimensions × 4</c> bytes in the index plus the same again in the KNN scan, so the 384-wide
/// rung is not merely a weaker model — it is an index a bit over a third the size with
/// proportionally cheaper search. That matters more here than in most retrieval systems because
/// facts are append-only and the index grows monotonically.</para>
/// </remarks>
public static class EmbeddingModels
{
    public static IReadOnlyList<EmbeddingModel> All { get; } =
    [
        new EmbeddingModel(
            Id: "all-minilm-l6-v2",
            DisplayName: "MiniLM L6 v2",
            Dimensions: 384,
            ContextTokens: 256,
            ApproximateBytes: 25_000_000,
            Languages: "English",
            Tradeoff:
                "Runs on anything, including machines with no GPU and little spare RAM. "
                + "The 256-token window is the real limit: a long fact gets truncated rather "
                + "than embedded, so this rung suits short stated facts better than prose.",
            Source: null),

        new EmbeddingModel(
            Id: "nomic-embed-text-v1.5",
            DisplayName: "Nomic Embed Text v1.5",
            Dimensions: 768,
            ContextTokens: 8192,
            ApproximateBytes: 140_000_000,
            Languages: "English",
            Tradeoff:
                "The middle rung, and the first with a context window long enough that nothing "
                + "Engram stores gets truncated. Costs about six times the disk of the small "
                + "rung and twice the index width, for markedly better recall on paraphrase.",
            Source: null),

        new EmbeddingModel(
            Id: "qwen3-embedding-0.6b",
            DisplayName: "Qwen3 Embedding 0.6B",
            Dimensions: 1024,
            ContextTokens: 32768,
            ApproximateBytes: 610_000_000,
            Languages: "100+ languages",
            Tradeoff:
                "Best recall, and the only rung that is multilingual. Wants roughly a gigabyte "
                + "of RAM resident and takes seconds to load, so it earns its keep on a machine "
                + "with headroom and is the wrong choice on one without.",
            Source: null),
    ];

    /// <summary>The rung chosen when nobody chooses — the middle one.</summary>
    /// <remarks>
    /// Not the best model and not the smallest, because a default is a guess about a machine
    /// nobody has measured. The middle rung is the one whose worst case — a slightly larger
    /// download than needed — is the mildest.
    /// </remarks>
    public const string DefaultId = "nomic-embed-text-v1.5";

    public static EmbeddingModel? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static EmbeddingModel Default => Find(DefaultId)!;
}
