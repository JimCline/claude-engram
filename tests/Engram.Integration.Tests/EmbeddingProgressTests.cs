using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. What the store can answer and what only the running loop can answer are different
/// questions, and most of these assert that the split holds under a file that is wrong, stale, or
/// missing — the three states it is actually found in.
/// </summary>
public class EmbeddingProgressTests
{
    private const int Dimensions = 4;

    private sealed class CountingEmbedder : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new("test-embedder", Dimensions);

        public Task<IReadOnlyList<float[]?>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            var vectors = new float[]?[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                vectors[i] = [1f, 0f, 0f, 0f];
            }

            return Task.FromResult<IReadOnlyList<float[]?>>(vectors);
        }
    }

    private static EmbeddingProgress Sample(DateTimeOffset now, int embedded = 40) =>
        new(now, now.AddSeconds(-10), 4242, "test-embedder/4", embedded, 0, "running", null, ["a body"]);

    [Fact]
    public void Progress_RoundTripsThroughTheFile()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        EmbeddingProgress.Write(sandbox.Home, Sample(now));
        var read = EmbeddingProgress.Read(sandbox.Home);

        Assert.NotNull(read);
        Assert.Equal(4242, read.Pid);
        Assert.Equal("test-embedder/4", read.Space);
        Assert.Equal(40, read.SessionEmbedded);
        Assert.Equal(["a body"], read.Recent);
    }

    /// <summary>
    /// Every question this file answers is "as of when", so a note without a timestamp answers
    /// none of them and must not be read as a partial one.
    /// </summary>
    [Fact]
    public void Progress_WithoutATimestamp_ReadsAsAbsent()
    {
        using var sandbox = new SandboxHome();
        File.WriteAllText(sandbox.Home.EmbeddingProgressPath, """{"pid": 7, "session_embedded": 99}""");

        Assert.Null(EmbeddingProgress.Read(sandbox.Home));
    }

    [Fact]
    public void Progress_Malformed_ReadsAsAbsentRatherThanThrowing()
    {
        using var sandbox = new SandboxHome();
        File.WriteAllText(sandbox.Home.EmbeddingProgressPath, "{ this is not json");

        Assert.Null(EmbeddingProgress.Read(sandbox.Home));
    }

    /// <summary>
    /// The liveness rule. A loop waits at most the idle interval between passes, so a note older
    /// than that with room to spare means stuck rather than slow.
    /// </summary>
    [Fact]
    public void LooksLive_IsKeyedToTheIdleInterval()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(Sample(now) with { UpdatedAt = now.AddSeconds(-5) } is var fresh && fresh.LooksLive(now));
        Assert.False((Sample(now) with { UpdatedAt = now.AddMinutes(-5) }).LooksLive(now));
    }

    /// <summary>
    /// A body containing a newline would cost the live display a row nobody counted, which is the
    /// defect D52 was paid for. Flattening happens where the body is recorded, not where it is drawn.
    /// </summary>
    [Fact]
    public void Summarize_FlattensNewlinesAndCutsLongBodies()
    {
        Assert.Equal("one two", EmbeddingProgress.Summarize("one\ntwo"));
        Assert.DoesNotContain("\n", EmbeddingProgress.Summarize("a\r\nb\nc"), StringComparison.Ordinal);
        Assert.Equal(EmbeddingProgress.RecentLength, EmbeddingProgress.Summarize(new string('x', 500)).Length);
    }

    // ---- what only the running loop knows ----

    /// <summary>
    /// The whole reason the file exists: after a pass, something outside the server can say what
    /// was embedded. Nothing in the store records this — a vector row carries no note of when or by
    /// whom it was written.
    /// </summary>
    [Fact]
    public async Task ADrain_RecordsWhatItEmbedded_WhereAnotherProcessCanReadIt()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFact("alpha", "the first body to be embedded");
        sandbox.AddFact("beta", "the second body to be embedded");

        var backlog = new EmbeddingBacklog(
            sandbox.Home, new CountingEmbedder(), EmbeddingSettings.Disabled with { MaxBatch = 16 });
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        var progress = EmbeddingProgress.Read(sandbox.Home);

        Assert.NotNull(progress);
        Assert.Equal(2, progress.SessionEmbedded);
        Assert.Contains("the first body to be embedded", progress.Recent);
        Assert.Contains("the second body to be embedded", progress.Recent);
    }

    /// <summary>The ring is bounded, so a long backfill cannot grow the file without limit.</summary>
    [Fact]
    public async Task TheRecentList_NeverGrowsPastWhatItKeeps()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(EmbeddingProgress.RecentKept * 3);

        var backlog = new EmbeddingBacklog(
            sandbox.Home, new CountingEmbedder(), EmbeddingSettings.Disabled with { MaxBatch = 4 });
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        var progress = EmbeddingProgress.Read(sandbox.Home);

        Assert.NotNull(progress);
        Assert.Equal(EmbeddingProgress.RecentKept, progress.Recent.Count);
        Assert.Equal(EmbeddingProgress.RecentKept * 3, progress.SessionEmbedded);
    }

    // ---- the status view ----

    /// <summary>
    /// The split the design rests on. A file claiming something else must not move the counts,
    /// because the file is written by a process that may have died mid-sentence and the store is
    /// the only thing that knows what is actually there.
    /// </summary>
    [Fact]
    public void Status_TakesItsCountsFromTheStore_EvenWhenTheFileDisagrees()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(3);

        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.Write(
            sandbox.Home,
            new EmbeddingProgress(now, now.AddSeconds(-10), 1, "lies/9", 999, 0, "running", null, []));

        var view = EmbedStatus.Read(sandbox.Home, now);

        Assert.Equal(0, view.Embedded);
        Assert.Equal(3, view.Pending);
        Assert.Equal(3, view.Total);
    }

    /// <summary>
    /// An estimate is worse than none when it is confidently wrong: a rate measured by a process
    /// that has since stopped predicts nothing about a queue nothing is working on.
    /// </summary>
    [Fact]
    public void Status_WithAStaleNote_ReportsNoRateAndNoEta()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.Write(
            sandbox.Home,
            new EmbeddingProgress(now.AddMinutes(-30), now.AddMinutes(-40), 5, "x/4", 600, 0, "running", null, []));

        var view = EmbedStatus.Read(sandbox.Home, now) with { Embedded = 10, Pending = 90 };

        Assert.False(view.Live);
        Assert.Null(view.Eta);

        var lines = string.Join('\n', EmbedStatus.Lines(view, now, decorated: false));
        Assert.Contains("rate       —", lines, StringComparison.Ordinal);
        Assert.Contains("eta        —", lines, StringComparison.Ordinal);
        Assert.Contains("stalled or stopped", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_WhileRunning_ReportsARateAndAnEta()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.Write(
            sandbox.Home,
            new EmbeddingProgress(now, now.AddSeconds(-100), 5, "x/4", 500, 0, "running", null, ["a body"]));

        var view = EmbedStatus.Read(sandbox.Home, now) with { Embedded = 500, Pending = 500 };

        Assert.True(view.Live);
        Assert.Equal(5, view.Progress!.RatePerSecond);
        Assert.Equal(100, view.Eta!.Value.TotalSeconds, precision: 0);

        var lines = string.Join('\n', EmbedStatus.Lines(view, now, decorated: false));
        Assert.Contains("5.0/s mean since", lines, StringComparison.Ordinal);
        Assert.Contains("recently embedded", lines, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reason for there being no loop does not decay into "stalled" after forty-five seconds. It
    /// is written once by a service that is about to return, and it stays true until a restart.
    /// </summary>
    [Fact]
    public void AnUnavailableNote_IsNeverLive_HoweverFreshItIs()
    {
        var now = DateTimeOffset.UtcNow;
        var note = Sample(now) with { Outcome = EmbeddingProgress.Unavailable, SessionEmbedded = 0 };

        Assert.False(note.LooksLive(now));
    }

    /// <summary>
    /// The case that sent a person to start a server that was already running. The only process
    /// that knows why the loop never started is the one that decided not to start it, and it used
    /// to say so only in a log that nobody asking this question has cause to open.
    /// </summary>
    [Fact]
    public void Status_WhenTheBacklogDeclinedToStart_SaysWhyInsteadOfSayingStartTheServer()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.WriteUnavailable(sandbox.Home, "qwen3-embedding-0.6b is not downloaded yet.");

        var view = EmbedStatus.Read(sandbox.Home, now) with { Embedded = 0, Pending = 873 };
        var lines = string.Join('\n', EmbedStatus.Lines(view, now, decorated: false));

        Assert.Contains("not running — qwen3-embedding-0.6b is not downloaded yet.", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("start the server", lines, StringComparison.Ordinal);
    }

    /// <summary>
    /// Piped output is what a script and an agent read, so it stays key-and-value. The bar is a
    /// terminal decoration and appears only where one was detected.
    /// </summary>
    [Fact]
    public void Status_Piped_HasNoProgressBar()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        var view = EmbedStatus.Read(sandbox.Home, now) with { Embedded = 25, Pending = 75 };

        var plain = string.Join('\n', EmbedStatus.Lines(view, now, decorated: false));
        var rich = string.Join('\n', EmbedStatus.Lines(view, now, decorated: true));

        Assert.DoesNotContain("█", plain, StringComparison.Ordinal);
        Assert.Contains("embedded   25 of 100 facts (25%)", plain, StringComparison.Ordinal);
        Assert.Contains("█", rich, StringComparison.Ordinal);
        Assert.Contains("25 / 100 facts  25%", rich, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bar reading 100% for a store with nothing in it looks like success. There is no fraction
    /// when there is no denominator, and the line says so rather than inventing one.
    /// </summary>
    /// <remarks>
    /// An uninitialized home rather than an initialized one: <c>EngramInitializer</c> seeds a
    /// corpus, so the only store with a zero denominator is a store that does not exist yet — which
    /// is also the state someone runs this command in first.
    /// </remarks>
    [Fact]
    public void Status_WithNothingToEmbed_ClaimsNoProgressRatherThanCompletion()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var now = DateTimeOffset.UtcNow;
        var view = EmbedStatus.Read(sandbox.Home, now);

        Assert.Equal(0, view.Total);
        Assert.Null(view.Fraction);

        var lines = string.Join('\n', EmbedStatus.Lines(view, now, decorated: true));
        Assert.DoesNotContain("█", lines, StringComparison.Ordinal);
        Assert.Contains("no store yet", lines, StringComparison.Ordinal);
    }
}
