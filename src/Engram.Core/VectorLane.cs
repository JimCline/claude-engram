using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>Whether the vector lane ran, and if not, why not.</summary>
public enum VectorLaneState
{
    /// <summary>No provider configured. A supported configuration, not a fault (D18).</summary>
    Off,

    /// <summary>Asked for, and something between here and the index stopped it.</summary>
    Unavailable,

    /// <summary>The index was queried. It may still have returned nothing.</summary>
    Queried,
}

/// <summary>What the vector lane produced, in rank order.</summary>
public sealed record VectorLaneResult(
    VectorLaneState State,
    string Reason,
    IReadOnlyList<VectorMatch> Matches,
    EmbeddingSpace? Space)
{
    public static VectorLaneResult Stopped(VectorLaneState state, string reason) =>
        new(state, reason, [], null);

    /// <summary>Nearest first, one-based — the shape RRF fuses on.</summary>
    public IReadOnlyDictionary<long, int> Ranks { get; } =
        Matches.Select((match, index) => (match.FactId, Rank: index + 1))
            .ToDictionary(pair => pair.FactId, pair => pair.Rank);
}

/// <summary>
/// Everything <see cref="VectorLane.Run"/> does before it searches: resolve the embedder, load the
/// extension, check the index space, embed the query.
/// </summary>
/// <remarks>
/// Exists because <see cref="RecallRanker"/> runs the KNN search itself, inside the ranking
/// statement (D59's boundary: the search is SQL, not a round trip). Calling <see cref="VectorLane.Run"/>
/// for that path would perform a second search whose result is thrown away — the query still has to
/// be embedded in C#, but the nearest-neighbour lookup must not happen twice.
/// </remarks>
public sealed record VectorLaneQuery(VectorLaneState State, string Reason, float[]? Embedding, EmbeddingSpace? Space)
{
    public static VectorLaneQuery Stopped(VectorLaneState state, string reason) => new(state, reason, null, null);
}

/// <summary>
/// The vector lane, as one implementation that both recall and <c>explain</c> call.
/// </summary>
/// <remarks>
/// <para><b>Why this is not private to the explainer.</b> D30 says the explainer describes the
/// ranker that runs, not the one that was planned. This code began inside
/// <see cref="RetrievalExplainer"/>, where it was the only vector query in the system and reported
/// itself as "answerable, read by nothing on the recall path" — accurate at the time. The moment
/// recall grew a vector lane of its own, a second copy would have made that promise unkeepable:
/// two implementations diverge, and the one being explained would stop being the one being run.</para>
///
/// <para><b>The extension is loaded here, and the result is the point.</b>
/// <see cref="EngramDatabase.Open(EngramHome)"/> already loads it on every connection, but
/// deliberately does not look at what happened — an instance without embeddings is the ordinary
/// case there, not a fault. This lane is the opposite: it has to tell "sqlite-vec is not
/// installed" apart from "installed but this store has no index", and those produce different
/// advice. The load call is how that state is obtained, not a second attempt at the same job.
/// Loading against the connection in hand is also the only correct way to ask, because loadable
/// extensions are connection-scoped and pooling recycles handles — a successful query proves some
/// connection loaded it, never this one.</para>
///
/// <para><b>Failing here can never fail recall.</b> Every stop returns a reason; nothing throws
/// out of <see cref="Run"/>. Recall without a vector lane is the ordinary configuration, so a
/// provider that is down has to degrade to lexical rather than take the tool with it.</para>
/// </remarks>
public static class VectorLane
{
    /// <param name="settings">
    /// The already-parsed <c>[embedding]</c> section. Taken rather than read because both callers
    /// have loaded the config for their own reasons before they get here, and recall is a hot
    /// path — re-reading and re-parsing a TOML file per query to answer a question already
    /// answered is a cost that buys nothing.
    /// </param>
    public static VectorLaneResult Run(
        SqliteConnection connection,
        EngramHome home,
        EmbeddingSettings settings,
        string query,
        Func<string, string?> environment,
        int seedK,
        LocalRuntime? local = null,
        HttpClient? client = null)
    {
        var prepared = PrepareQuery(connection, home, settings, query, environment, local, client);
        if (prepared.State is not VectorLaneState.Queried)
        {
            return VectorLaneResult.Stopped(prepared.State, prepared.Reason);
        }

        try
        {
            var matches = VectorIndex.Search(connection, prepared.Embedding!, seedK);
            return new VectorLaneResult(VectorLaneState.Queried, "queried", matches, prepared.Space);
        }
        catch (SqliteException exception)
        {
            // A loaded extension that then refuses a query is rare and worth naming rather than
            // letting it escape: recall is expected to work without this lane, never to fail with it.
            return VectorLaneResult.Stopped(
                VectorLaneState.Unavailable, $"the index would not answer: {exception.Message}");
        }
    }

    /// <summary>
    /// Everything <see cref="Run"/> does short of the KNN search itself: resolve the embedder, load
    /// the extension, check the index space, embed the query. <see cref="RecallRanker"/> calls this
    /// so it can bind the embedding into the ranking statement, which does the search in SQL.
    /// </summary>
    public static VectorLaneQuery PrepareQuery(
        SqliteConnection connection,
        EngramHome home,
        EmbeddingSettings settings,
        string query,
        Func<string, string?> environment,
        LocalRuntime? local = null,
        HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(environment);

        var resolution = EmbedderFactory.Create(settings, environment, client, local);
        if (!resolution.Resolved)
        {
            return VectorLaneQuery.Stopped(
                settings.Provider == EmbeddingProvider.None ? VectorLaneState.Off : VectorLaneState.Unavailable,
                resolution.Reason);
        }

        if (VectorExtension.Load(connection, home.LibDir) is not VectorExtensionState.Loaded and var state)
        {
            return VectorLaneQuery.Stopped(
                VectorLaneState.Unavailable,
                state == VectorExtensionState.NotInstalled
                    ? $"sqlite-vec is not in {home.LibDir}, so the index cannot be queried"
                    : $"sqlite-vec is in {home.LibDir} and would not load — wrong architecture, or truncated");
        }

        if (!VectorIndex.Exists(connection) || VectorIndex.ReadSpace(connection) is not { } indexed)
        {
            return VectorLaneQuery.Stopped(VectorLaneState.Unavailable, "no vector index in this store yet");
        }

        if (indexed != resolution.Embedder!.Space)
        {
            // D18's quiet failure: distances between spaces are real numbers and mean nothing.
            return VectorLaneQuery.Stopped(
                VectorLaneState.Unavailable,
                $"the index holds {indexed} but the configured provider produces {resolution.Embedder.Space} — "
                + "vectors from different spaces are not comparable, so this lane is not queried");
        }

        float[]? embedded;
        try
        {
            embedded = resolution.Embedder
                .EmbedAsync([VectorIndex.InputFor(query)])
                .GetAwaiter()
                .GetResult()
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return VectorLaneQuery.Stopped(
                VectorLaneState.Unavailable, $"the provider did not answer: {exception.Message}");
        }

        if (embedded is null)
        {
            return VectorLaneQuery.Stopped(
                VectorLaneState.Unavailable, "the provider returned no vector for this query");
        }

        return new VectorLaneQuery(VectorLaneState.Queried, "queried", embedded, indexed);
    }
}
