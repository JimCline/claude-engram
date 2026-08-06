using System.Security.Cryptography;

namespace Engram.Core;

public enum FetchOutcome
{
    /// <summary>The file was already there and hashed correctly. No network.</summary>
    AlreadyPresent,

    Downloaded,

    /// <summary>The registry row carries no digest, so nothing was fetched.</summary>
    NotPinned,

    /// <summary>The download failed. The message says how.</summary>
    Failed,

    /// <summary>The bytes arrived but did not hash to the pin, and were deleted.</summary>
    Corrupt,
}

public sealed record FetchResult(FetchOutcome Outcome, string Path, string Message)
{
    public bool Usable => Outcome is FetchOutcome.AlreadyPresent or FetchOutcome.Downloaded;
}

public sealed record FetchProgress(string ModelId, long Downloaded, long? Total)
{
    public double? Fraction => Total is > 0 ? (double)Downloaded / Total.Value : null;
}

/// <summary>
/// Downloads pinned model files over plain HTTPS.
/// </summary>
/// <remarks>
/// <para>No hub client and no Python: the artifact is one URL built from a repository, an
/// immutable revision, and a filename, which is the whole of what a hub client would do here and
/// none of what it would install.</para>
///
/// <para><b>Verified before use, always, including on a file that was already there.</b> A model
/// is loaded into Engram's own process, so the check is the same one <c>fetch-vec0.sh</c> makes
/// and for the same reason. Re-hashing a cached file costs a read of it, which against a
/// multi-second model load is not the cost worth saving.</para>
///
/// <para><b>Resumable, because the largest rung is over half a gigabyte.</b> A partial file is
/// kept and continued with a range request rather than restarted, but only when the server
/// actually honours the range — a 200 where a 206 was asked for means the response body is the
/// whole file, and appending it to what is already on disk would produce a corrupt file that
/// only the digest would catch. Which it would; this just avoids the wasted download.</para>
/// </remarks>
public static class ModelFetcher
{
    private const string PartialSuffix = ".partial";

    public static string PathFor(EngramHome home, EmbeddingModel model)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(model);

        return Path.Combine(home.ModelsDir, model.FileName);
    }

    /// <summary>True when the model is present and hashes to its pin.</summary>
    public static bool IsInstalled(EngramHome home, EmbeddingModel model)
    {
        var path = PathFor(home, model);
        return model.Source?.Sha256 is { } expected
            && File.Exists(path)
            && Matches(path, expected);
    }

    /// <summary>
    /// Ensures the model file is on disk and correct, downloading it if it is not.
    /// </summary>
    /// <param name="environment">
    /// Reads an environment variable by name — used only for <c>HF_TOKEN</c>, so a gated
    /// repository works without the token ever being written to a config file. Injected so a
    /// test can supply one without setting a real variable for the whole process.
    /// </param>
    public static async Task<FetchResult> EnsureAsync(
        EngramHome home,
        EmbeddingModel model,
        HttpClient client,
        Func<string, string?> environment,
        IProgress<FetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);

        var path = PathFor(home, model);

        if (model.Source is not { Sha256: { Length: > 0 } expected } source)
        {
            return new FetchResult(
                FetchOutcome.NotPinned,
                path,
                $"{model.Id} has no pinned digest, so it cannot be fetched safely.");
        }

        if (File.Exists(path))
        {
            if (Matches(path, expected))
            {
                return new FetchResult(FetchOutcome.AlreadyPresent, path, $"{model.Id} is already installed.");
            }

            // Not a tampering story — the revision is immutable, so the same URL cannot have
            // served different bytes. Something damaged the file locally, and the fix is to
            // fetch it again rather than to keep a file nothing may load.
            File.Delete(path);
        }

        Directory.CreateDirectory(home.ModelsDir);
        var partial = path + PartialSuffix;

        try
        {
            await DownloadAsync(source, partial, model, client, environment, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult(FetchOutcome.Failed, path, $"Could not download {model.Id}: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new FetchResult(FetchOutcome.Failed, path, $"Could not write {model.Id}: {ex.Message}");
        }

        if (!Matches(partial, expected))
        {
            File.Delete(partial);
            return new FetchResult(
                FetchOutcome.Corrupt,
                path,
                $"{model.Id} did not match its pinned digest and was discarded. Run the fetch again.");
        }

        // Into place only once verified, so a failed or interrupted run can never leave a file
        // at the real path that something would happily load.
        File.Move(partial, path, overwrite: true);

        return new FetchResult(FetchOutcome.Downloaded, path, $"Installed {model.Id} ({model.SizeLabel}).");
    }

    private static async Task DownloadAsync(
        ModelSource source,
        string partial,
        EmbeddingModel model,
        HttpClient client,
        Func<string, string?> environment,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resumeFrom = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (environment("HF_TOKEN") is { Length: > 0 } token)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        if (resumeFrom > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // A 200 in answer to a range request means the body is the whole file. Appending it to
        // what is on disk would produce a file the digest rejects after a full download.
        var appending = resumeFrom > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var written = appending ? resumeFrom : 0;
        long? total = response.Content.Headers.ContentLength is { } length ? written + length : null;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partial,
            appending ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            progress?.Report(new FetchProgress(model.Id, written, total));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool Matches(string path, string expectedSha256) =>
        string.Equals(Sha256Of(path), expectedSha256, StringComparison.OrdinalIgnoreCase);

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
