using LLama;

namespace Engram.Core;

/// <summary>
/// Embeds through llama.cpp loaded into this process, for <c>provider = "local"</c>.
/// </summary>
/// <remarks>
/// <para><b>It owns nothing.</b> The weights and the context behind <paramref name="inner"/> cost
/// seconds to load and hundreds of megabytes to hold, so exactly one of them exists per loaded
/// model and <see cref="LocalRuntime"/> holds it. This wrapper is the cheap, droppable thing the
/// factory hands out — same contract the HTTP providers already have, where creating an embedder
/// is free and disposing one does not disturb whatever is serving it. Consequently this type is
/// deliberately not <see cref="IDisposable"/>: there is nothing here to dispose, and being
/// disposable would invite a caller to end the model everyone else is sharing.</para>
///
/// <para><b>One text at a time, behind a gate.</b> llama.cpp's context is not reentrant and
/// <see cref="LLamaEmbedder"/> does not serialize for us, so concurrent calls would corrupt the
/// KV cache rather than merely contend. The batch API above it is still the right shape — it is
/// what keeps callers from embedding in a loop on the write path (D4) — but the batching happens
/// in the queue, not in the call: this loop is where a batch turns back into single inferences.</para>
///
/// <para><b>A failure on one text is that text's failure.</b> Per <see cref="IEmbedder"/>, one
/// input that will not embed returns null and stays in the backfill queue; it must not take the
/// batch with it, because the queue is ordered and a poison text would block every fact behind it
/// forever. A wrong <em>width</em> is the opposite case and throws, because it is systematic — it
/// says the configuration is wrong, not that this sentence is.</para>
/// </remarks>
public sealed class LLamaSharpEmbedder : IEmbedder
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LLamaEmbedder inner;

    public LLamaSharpEmbedder(EmbeddingSpace space, LLamaEmbedder inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        Space = space;
        this.inner = inner;
    }

    public EmbeddingSpace Space { get; }

    public async Task<IReadOnlyList<float[]?>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new float[]?[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                results[i] = await EmbedOneAsync(texts[i], cancellationToken).ConfigureAwait(false);
            }

            return results;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<float[]?> EmbedOneAsync(string text, CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await inner.GetEmbeddings(text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Deliberately broad: see below.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Anything llama.cpp raises about one input — a tokenizer edge case, a text longer
            // than the context — is this text's problem and not the batch's. The alternative is
            // enumerating exception types across a P/Invoke boundary that does not promise them,
            // and getting that list wrong fails the whole batch instead of one member of it.
            return null;
        }

        if (vectors is not [{ } vector, ..])
        {
            return null;
        }

        if (vector.Length != Space.Dimensions)
        {
            throw new InvalidOperationException(
                $"{Space.Model} is configured for {Space.Dimensions} dimensions but produced "
                + $"{vector.Length}. Fix the model's row in EmbeddingModels — a mismatched width "
                + "corrupts every query it reaches without erroring anywhere.");
        }

        return vector;
    }
}
