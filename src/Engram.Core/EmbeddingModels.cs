using LLama.Native;

namespace Engram.Core;

/// <summary>
/// Where a model file comes from, and what it must hash to.
/// </summary>
/// <remarks>
/// <para>Pinned by digest for the reason <c>fetch-vec0.sh</c> is: this file is loaded into
/// Engram's own process. Null until the digest has been checked against a real download — a
/// registry row with an invented hash is worse than one with no hash, because the first looks
/// verified.</para>
///
/// <para><b><see cref="Revision"/> is a commit hash, never a branch.</b> The obvious value is
/// <c>main</c>, and it is wrong: a branch moves, so the same URL fetches different bytes over
/// time and the digest below starts failing for a reason that looks like corruption. Two
/// independent pins — an immutable URL and a content hash — mean a mismatch can only be a
/// damaged download, which is what makes deleting the file and retrying the correct
/// response.</para>
/// </remarks>
public sealed record ModelSource(string Repository, string Revision, string File, string? Sha256)
{
    /// <summary>The direct download URL. No hub client, no Python.</summary>
    public string Url => $"https://huggingface.co/{Repository}/resolve/{Revision}/{File}";
}

/// <summary>One local embedding model Engram knows how to run.</summary>
/// <param name="Pooling">
/// How the token vectors collapse into one sentence vector. Encoder models average their tokens; a
/// decoder-based embedder like Qwen3 carries the meaning on the last one.
///
/// <para>Stated per model rather than left to whatever the GGUF's metadata happens to say, because
/// this setting fails quietly: the wrong value produces a vector of the right width, from the right
/// model, that encodes something other than the sentence, and nothing errors. Measured on MiniLM,
/// the damage is real but partial — cos(mean, last) = 0.76, cos(mean, cls) = 0.50 — which is worse
/// than it sounds, since a lane that is 76% right is one whose failures look like ordinary misses
/// rather than like a bug.</para>
///
/// <para>Worth knowing before trusting this column: the tests establish that the value reaches
/// llama.cpp, not that it is the right value. Nothing here is measured against a retrieval
/// benchmark, so each row is an argument from the model's architecture.</para>
/// </param>
public sealed record EmbeddingModel(
    string Id,
    string DisplayName,
    int Dimensions,
    int ContextTokens,
    long ApproximateBytes,
    string Languages,
    string Tradeoff,
    LLamaPoolingType Pooling,
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
            ApproximateBytes: 25_008_064,
            Languages: "English",
            Tradeoff:
                "Runs on anything, including machines with no GPU and little spare RAM. "
                + "The 256-token window is the real limit: a long fact gets truncated rather "
                + "than embedded, so this rung suits short stated facts better than prose.",
            Pooling: LLamaPoolingType.Mean,
            Source: new ModelSource(
                "second-state/All-MiniLM-L6-v2-Embedding-GGUF",
                "544f204f2eaa2d71361ffc74d6df7170285b286a",
                "all-MiniLM-L6-v2-Q8_0.gguf",
                "263215c3cadd6e16740741a7624ab4cbb6c8e777688bd5331ecfbf5681c2f8ed")),

        new EmbeddingModel(
            Id: "nomic-embed-text-v1.5",
            DisplayName: "Nomic Embed Text v1.5",
            Dimensions: 768,
            ContextTokens: 8192,
            ApproximateBytes: 146_146_432,
            Languages: "English",
            Tradeoff:
                "The middle rung, and the first with a context window long enough that nothing "
                + "Engram stores gets truncated. Costs about six times the disk of the small "
                + "rung and twice the index width, for markedly better recall on paraphrase.",
            Pooling: LLamaPoolingType.Mean,
            Source: new ModelSource(
                "nomic-ai/nomic-embed-text-v1.5-GGUF",
                "0188c9bf409793f810680a5a431e7b899c46104c",
                "nomic-embed-text-v1.5.Q8_0.gguf",
                "3e24342164b3d94991ba9692fdc0dd08e3fd7362e0aacc396a9a5c54a544c3b7")),

        new EmbeddingModel(
            Id: "qwen3-embedding-0.6b",
            DisplayName: "Qwen3 Embedding 0.6B",
            Dimensions: 1024,
            ContextTokens: 32768,
            ApproximateBytes: 639_150_592,
            Languages: "100+ languages",
            Tradeoff:
                "Best recall, and the only rung that is multilingual. Wants roughly a gigabyte "
                + "of RAM resident and takes seconds to load, so it earns its keep on a machine "
                + "with headroom and is the wrong choice on one without.",
            // The odd one out: a decoder, so there is no [CLS] and averaging would dilute the
            // token that actually carries the summary.
            Pooling: LLamaPoolingType.Last,
            Source: new ModelSource(
                "Qwen/Qwen3-Embedding-0.6B-GGUF",
                "370f27d7550e0def9b39c1f16d3fbaa13aa67728",
                "Qwen3-Embedding-0.6B-Q8_0.gguf",
                "06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439")),
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
