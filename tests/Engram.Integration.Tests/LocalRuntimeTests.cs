using System.Runtime.InteropServices;
using Engram.Core;
using LLama.Native;

namespace Engram.Integration.Tests;

/// <summary>
/// The local provider, now that llama.cpp is loaded rather than launched.
/// </summary>
/// <remarks>
/// <para>Two halves, and the split is deliberate. The first asserts what Engram decides — which
/// model, which pooling, how wide a batch — and needs no weights, because
/// <see cref="LocalRuntime.ParametersFor"/> exists precisely so those decisions can be read without
/// paying seconds to load a GGUF. The second actually loads one, and is the only thing that can
/// prove a vector comes back.</para>
///
/// <para>The second half is skipped unless a real model is present, on the same reasoning as
/// <c>ENGRAM_TEST_BINARY</c> for the end-to-end tier: a suite that silently downloads several
/// hundred megabytes is one nobody can run offline, and a stand-in that returns
/// <c>[0.1] * 768</c> — which is what the llama-server era used — proves the plumbing and nothing
/// about the embedding. Point <c>ENGRAM_TEST_MODEL_HOME</c> at an Engram home whose
/// <c>models/</c> holds the file, and these run.</para>
/// </remarks>
public sealed class LocalRuntimeTests
{
    private static readonly EmbeddingModel Nomic = EmbeddingModels.Find("nomic-embed-text-v1.5")!;
    private static readonly EmbeddingModel Mini = EmbeddingModels.Find("all-minilm-l6-v2")!;

    /// <summary>A home holding real downloaded weights, or null if the suite was not given one.</summary>
    private static string? ModelHome =>
        Environment.GetEnvironmentVariable("ENGRAM_TEST_MODEL_HOME") // engram-lint:allow(opt-in test fixture, not a home resolver)
            is { Length: > 0 } path && Directory.Exists(path)
            ? path
            : null;

    private static EngramHome RequireRealWeights(EmbeddingModel model)
    {
        var root = ModelHome;
        Assert.SkipWhen(root is null, "Set ENGRAM_TEST_MODEL_HOME to an Engram home with downloaded weights.");

        var home = EngramHome.Resolve(
            root!, new Dictionary<string, string?>(), root!, Environment.CurrentDirectory);

        Assert.SkipUnless(
            File.Exists(ModelFetcher.PathFor(home, model)),
            $"{model.Id} is not downloaded in {home.ModelsDir}.");

        return home;
    }

    /// <summary>Puts a placeholder where the model would be, for the cases that never load it.</summary>
    private static void PretendTheModelIsDownloaded(EngramHome home, EmbeddingModel model)
    {
        Directory.CreateDirectory(home.ModelsDir);
        File.WriteAllText(ModelFetcher.PathFor(home, model), "not really a gguf");
    }

    // -- what Engram decides, without loading anything --

    [Fact]
    public void TheParameters_AskForEmbeddingsAndTheGpu()
    {
        var parameters = LocalRuntime.ParametersFor(Nomic, "/models/x.gguf");

        Assert.True(parameters.Embeddings);
        Assert.Equal("/models/x.gguf", parameters.ModelPath);
        Assert.Equal(99, parameters.GpuLayerCount);
    }

    [Fact]
    public void TheBatchSize_MatchesTheWindowRatherThanTheDefault()
    {
        // llama.cpp will not pool an embedding across physical batches, so an input longer than
        // the micro-batch is refused outright. Leaving these at the 512-token default would fail
        // on long facts and on nothing else, which is the worst shape a bug can have.
        var parameters = LocalRuntime.ParametersFor(Nomic, "/models/x.gguf");

        Assert.Equal(parameters.ContextSize, parameters.BatchSize);
        Assert.Equal(parameters.ContextSize, parameters.UBatchSize);
    }

    [Fact]
    public void AWindowLargerThanEngramNeeds_IsCappedRatherThanAllocated()
    {
        var qwen = EmbeddingModels.Find("qwen3-embedding-0.6b")!;
        Assert.True(qwen.ContextTokens > LocalRuntime.MaxContext, "the cap must actually bind");

        var parameters = LocalRuntime.ParametersFor(qwen, "/models/x.gguf");

        Assert.Equal((uint)LocalRuntime.MaxContext, parameters.ContextSize);
    }

    [Fact]
    public void AWindowSmallerThanTheCap_IsLeftAlone()
    {
        var parameters = LocalRuntime.ParametersFor(Mini, "/models/x.gguf");

        Assert.Equal((uint)Mini.ContextTokens, parameters.ContextSize);
    }

    [Fact]
    public void EveryModel_StatesItsPoolingRatherThanInheritingADefault()
    {
        // The registry is the only place this is decided, and a row added without thinking about
        // it would embed successfully and rank worse for no visible reason. Unspecified hands the
        // choice to whatever metadata the GGUF happens to carry, which is the guess this column
        // exists to replace.
        Assert.All(EmbeddingModels.All, model => Assert.NotEqual(LLamaPoolingType.Unspecified, model.Pooling));

        Assert.Equal(LLamaPoolingType.Mean, Mini.Pooling);
        Assert.Equal(LLamaPoolingType.Last, EmbeddingModels.Find("qwen3-embedding-0.6b")!.Pooling);
    }

    // -- refusing, without loading anything --

    [Fact]
    public void AModelNameEngramDoesNotKnow_IsRefusedBeforeAnythingLoads()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var runtime = new LocalRuntime(sandbox.Home);

        var opened = runtime.Open("not-a-real-model");

        Assert.False(opened.Open);
        Assert.Contains("not a model Engram knows", opened.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelThatWasNeverDownloaded_SaysHowToGetIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var runtime = new LocalRuntime(sandbox.Home);

        var opened = runtime.Open(Nomic.Id);

        Assert.False(opened.Open);
        Assert.Contains("engram model install", opened.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotAModel_FailsWithAReasonRatherThanAnException()
    {
        // The vector lane is optional, so a corrupt or truncated download degrades to lexical
        // recall. Throwing here would take down whichever process asked — including the server.
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        using var runtime = new LocalRuntime(sandbox.Home);

        var opened = runtime.Open(Nomic.Id);

        Assert.False(opened.Open);
        Assert.Contains(Nomic.Id, opened.Reason, StringComparison.Ordinal);
        Assert.Null(runtime.Loaded);
    }

    [Fact]
    public void NothingIsLoadedUntilSomethingAsks()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var runtime = new LocalRuntime(sandbox.Home);

        Assert.Null(runtime.Loaded);
    }

    [Fact]
    public void WithoutARuntimeToHostIt_TheFactorySaysWhoDoes()
    {
        var resolution = EmbedderFactory.Create(Local(Nomic.Id), _ => null);

        Assert.False(resolution.Resolved);
        Assert.Contains("engram serve", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuntimeThatCannotLoad_ReachesTheCallerAsTheReasonItGave()
    {
        // The factory must not paper over the runtime's account with a generic one. "the model is
        // not downloaded" and "this file is not a GGUF" need different actions.
        using var sandbox = new SandboxHome(initialize: false);
        using var runtime = new LocalRuntime(sandbox.Home);

        var resolution = EmbedderFactory.Create(Local(Nomic.Id), _ => null, client: null, runtime);

        Assert.False(resolution.Resolved);
        Assert.Contains("engram model install", resolution.Reason, StringComparison.Ordinal);
    }

    // -- with weights actually loaded --

    [Fact]
    public async Task RealWeights_ProduceAVectorOfTheDeclaredWidth()
    {
        var home = RequireRealWeights(Mini);
        using var runtime = new LocalRuntime(home);

        var opened = runtime.Open(Mini.Id);
        Assert.True(opened.Open, opened.Reason);
        Assert.Equal(Mini.Id, runtime.Loaded);

        var vectors = await opened.Embedder!.EmbedAsync(
            ["one fact", "another"], TestContext.Current.CancellationToken);

        Assert.Equal(2, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(Mini.Dimensions, v!.Length));

        // Not merely the right shape. A null embedder returning zeros would pass every assertion
        // above, and would rank identically against every query forever.
        Assert.Contains(vectors[0]!, component => component != 0f);
    }

    /// <summary>
    /// What ggml-metal reported reaches the record a load leaves behind (D28).
    /// </summary>
    /// <remarks>
    /// Deliberately does not assert that the tensor path is <i>on</i>. Measured on one M5 Pro, that
    /// value follows the SDK stamped in the main executable rather than the hardware: this suite
    /// runs under the <c>dotnet</c> host, stamped <c>sdk 15.5</c>, and observes <c>false</c>, while
    /// the published binary is stamped <c>sdk 26.5</c> and observes <c>true</c> from the same weights
    /// on the same machine in the same minute. Asserting either value would pin this to whichever
    /// host happened to run it — and the gap between them is the reason the record exists at all.
    /// What is invariant is that the observation is made, parses, and lands.
    /// </remarks>
    [Fact]
    public void RealWeights_RecordWhatGgmlMetalReported()
    {
        var home = RequireRealWeights(Mini);
        using var runtime = new LocalRuntime(home);

        // Gated on the platform, never on the sink's own output: skipping when no lines were
        // captured would let deleting the capture turn this green instead of red.
        Assert.SkipUnless(
            OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
            "ggml-metal only reports on macOS arm64.");

        // The model home outlives the run, so a record left by a previous one would let this pass
        // while observing nothing. Measured: without this, deleting the capture entirely still left
        // the test green.
        File.Delete(home.MetalRecordPath);

        var opened = runtime.Open(Mini.Id);
        Assert.True(opened.Open, opened.Reason);

        var record = MetalRecord.Read(home);

        Assert.NotNull(record);
        Assert.All(record.Lines, line => Assert.StartsWith("ggml_metal", line, StringComparison.Ordinal));
        Assert.NotNull(record.HasTensor);
        Assert.NotNull(record.Gpu);
    }

    /// <summary>
    /// The one assertion a stand-in server could never make: that the numbers mean something.
    /// </summary>
    /// <remarks>
    /// It proves the vectors are semantically ordered, and deliberately does not claim to prove the
    /// pooling is right — measured, because the obvious reading is wrong. Flipping MiniLM's row from
    /// Mean to Last and re-running this leaves it passing: a degraded embedding still keeps enough
    /// topical signal to sort a paraphrase above an unrelated sentence. That the setting is live
    /// rather than ignored is held by
    /// <see cref="TheConfiguredPooling_ReachesLlamaCppRatherThanBeingIgnored"/>; that the chosen
    /// value is the best one for a given model is not something any cheap test establishes, and
    /// pretending otherwise here would be worse than admitting it.
    /// </remarks>
    [Fact]
    public async Task RealWeights_PlaceAParaphraseNearerThanAnUnrelatedSentence()
    {
        var home = RequireRealWeights(Mini);
        using var runtime = new LocalRuntime(home);

        var opened = runtime.Open(Mini.Id);
        Assert.True(opened.Open, opened.Reason);

        var vectors = await opened.Embedder!.EmbedAsync(
            [
                "the nightly backup runs at three in the morning",
                "a scheduled overnight copy of the data is taken",
                "cats cannot digest plant protein",
            ],
            TestContext.Current.CancellationToken);

        var paraphrase = Cosine(vectors[0]!, vectors[1]!);
        var unrelated = Cosine(vectors[0]!, vectors[2]!);

        Assert.True(
            paraphrase > unrelated,
            $"paraphrase similarity {paraphrase:0.000} should exceed unrelated {unrelated:0.000}");
    }

    /// <summary>
    /// That the pooling in the registry is a setting and not a decoration.
    /// </summary>
    /// <remarks>
    /// The failure this catches is total and silent: if llama.cpp ever ignored <c>PoolingType</c> —
    /// deferring to whatever the GGUF's metadata says, which is what <c>Unspecified</c> already
    /// means — the column would still be there, still be read, still be printed by doctor, and
    /// change nothing. Measured on MiniLM at the time of writing: cos(mean, last) = 0.76 and
    /// cos(mean, cls) = 0.50, so the vectors are not merely permuted.
    /// </remarks>
    [Fact]
    public async Task TheConfiguredPooling_ReachesLlamaCppRatherThanBeingIgnored()
    {
        var home = RequireRealWeights(Mini);
        const string Text = "the nightly backup runs at three in the morning";

        var asConfigured = await EmbedUnder(home, Mini, Mini.Pooling, Text);
        var asSomethingElse = await EmbedUnder(
            home,
            Mini,
            Mini.Pooling == LLamaPoolingType.Mean ? LLamaPoolingType.CLS : LLamaPoolingType.Mean,
            Text);

        Assert.NotEqual(asConfigured, asSomethingElse);
        Assert.True(
            Cosine(asConfigured, asSomethingElse) < 0.99,
            "two pooling strategies produced all but the same vector, so the setting is not reaching llama.cpp");
    }

    /// <summary>Embeds one text with the pooling overridden, bypassing the registry.</summary>
    private static async Task<float[]> EmbedUnder(
        EngramHome home,
        EmbeddingModel model,
        LLamaPoolingType pooling,
        string text)
    {
        LlamaNative.Prepare();

        var parameters = LocalRuntime.ParametersFor(model, ModelFetcher.PathFor(home, model));
        parameters.PoolingType = pooling;

        using var weights = LLama.LLamaWeights.LoadFromFile(parameters);
        using var context = new LLama.LLamaEmbedder(weights, parameters);
        var vectors = await context.GetEmbeddings(text, TestContext.Current.CancellationToken);

        return vectors[0];
    }

    [Fact]
    public void AskingTwiceForTheSameModel_ReusesTheWeightsRatherThanLoadingThemAgain()
    {
        var home = RequireRealWeights(Mini);
        using var runtime = new LocalRuntime(home);

        var first = runtime.Open(Mini.Id);
        var second = runtime.Open(Mini.Id);

        Assert.True(first.Open, first.Reason);
        Assert.Same(first.Embedder, second.Embedder);
        Assert.Contains("already loaded", second.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForADifferentModel_ReplacesTheWeightsRatherThanHoldingBoth()
    {
        var home = RequireRealWeights(Mini);
        Assert.SkipUnless(
            File.Exists(ModelFetcher.PathFor(home, Nomic)),
            $"{Nomic.Id} is not downloaded in {home.ModelsDir}.");

        using var runtime = new LocalRuntime(home);

        var first = runtime.Open(Mini.Id);
        var second = runtime.Open(Nomic.Id);

        Assert.True(second.Open, second.Reason);
        Assert.NotSame(first.Embedder, second.Embedder);
        Assert.Equal(Nomic.Id, runtime.Loaded);
        Assert.Equal(Nomic.Dimensions, second.Embedder!.Space.Dimensions);
    }

    [Fact]
    public void DisposingTheRuntime_ReleasesTheWeights()
    {
        var home = RequireRealWeights(Mini);

        var runtime = new LocalRuntime(home);
        Assert.True(runtime.Open(Mini.Id).Open);
        runtime.Dispose();

        Assert.Null(runtime.Loaded);
        Assert.Throws<ObjectDisposedException>(() => runtime.Open(Mini.Id));
    }

    private static EmbeddingSettings Local(string modelId) => EmbeddingSettings.Disabled with
    {
        Provider = EmbeddingProvider.Local,
        Model = modelId,
        Dimensions = EmbeddingModels.Find(modelId)!.Dimensions,
    };

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
