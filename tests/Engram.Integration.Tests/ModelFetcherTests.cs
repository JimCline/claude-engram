using System.Net;
using System.Security.Cryptography;
using System.Text;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. The fetcher is filesystem behaviour more than it is HTTP behaviour — staging, moving,
/// deleting, resuming — so it is tested against real files and a fake transport.
/// </summary>
public class ModelFetcherTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(new string('g', 4096) + "GGUF");
    private static readonly string PayloadSha = Convert.ToHexStringLower(SHA256.HashData(Payload));

    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly byte[] body;

        public FakeServer(byte[]? body = null) => this.body = body ?? Payload;

        public int Requests { get; private set; }

        public List<string?> Ranges { get; } = [];

        public List<string?> Authorizations { get; } = [];

        /// <summary>A server that answers a range request with the whole file anyway.</summary>
        public bool IgnoreRange { get; init; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Ranges.Add(request.Headers.Range?.ToString());
            Authorizations.Add(request.Headers.Authorization?.ToString());

            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            if (from is null or 0 || IgnoreRange)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body),
                });
            }

            var offset = (int)from.Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body[offset..]),
            });
        }
    }

    private static EmbeddingModel Pinned(string? sha = null) =>
        new(
            Id: "test-model",
            DisplayName: "Test",
            Dimensions: 8,
            ContextTokens: 128,
            ApproximateBytes: Payload.Length,
            Languages: "English",
            Tradeoff: "None, it is fake.",
            Pooling: LLama.Native.LLamaPoolingType.Mean,
            Source: new ModelSource("owner/repo", "0123456789abcdef", "test-model.gguf", sha ?? PayloadSha));

    private static string? NoEnvironment(string name) => null;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(FetchResult Result, FakeServer Server)> FetchAsync(
        SandboxHome sandbox,
        EmbeddingModel model,
        FakeServer? server = null,
        Func<string, string?>? environment = null,
        IProgress<FetchProgress>? progress = null)
    {
        server ??= new FakeServer();
        using var client = new HttpClient(server, disposeHandler: false);

        var result = await ModelFetcher.EnsureAsync(
            sandbox.Home,
            model,
            client,
            environment ?? NoEnvironment,
            progress,
            Token);

        return (result, server);
    }

    [Fact]
    public async Task Ensure_DownloadsAndVerifies()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (result, _) = await FetchAsync(sandbox, Pinned());

        Assert.Equal(FetchOutcome.Downloaded, result.Outcome);
        Assert.True(result.Usable);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path, Token));
    }

    [Fact]
    public async Task Ensure_LeavesNoPartialBehind()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (result, _) = await FetchAsync(sandbox, Pinned());

        Assert.Empty(Directory.GetFiles(sandbox.Home.ModelsDir, "*.partial"));
        Assert.Single(Directory.GetFiles(sandbox.Home.ModelsDir));
        Assert.Equal(Path.Combine(sandbox.Home.ModelsDir, "test-model.gguf"), result.Path);
    }

    /// <summary>
    /// The cheap path, and the one the backlog hits on every start: an installed model costs a
    /// hash of a local file and no network at all.
    /// </summary>
    [Fact]
    public async Task Ensure_OnAnInstalledModel_MakesNoRequest()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var model = Pinned();
        await FetchAsync(sandbox, model);

        var (result, server) = await FetchAsync(sandbox, model);

        Assert.Equal(FetchOutcome.AlreadyPresent, result.Outcome);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task IsInstalled_TracksTheFetch()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var model = Pinned();

        Assert.False(ModelFetcher.IsInstalled(sandbox.Home, model));

        await FetchAsync(sandbox, model);

        Assert.True(ModelFetcher.IsInstalled(sandbox.Home, model));
    }

    /// <summary>
    /// A file of the right name and the wrong bytes must not count as installed, because the
    /// only thing standing between that file and Engram's own process is this check.
    /// </summary>
    [Fact]
    public async Task IsInstalled_OnAFileWithTheWrongBytes_IsFalse()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var model = Pinned();
        Directory.CreateDirectory(sandbox.Home.ModelsDir);
        await File.WriteAllTextAsync(ModelFetcher.PathFor(sandbox.Home, model), "not a model", Token);

        Assert.False(ModelFetcher.IsInstalled(sandbox.Home, model));
    }

    [Fact]
    public async Task Ensure_ReplacesAFileThatNoLongerMatches()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var model = Pinned();
        Directory.CreateDirectory(sandbox.Home.ModelsDir);
        await File.WriteAllTextAsync(ModelFetcher.PathFor(sandbox.Home, model), "truncated", Token);

        var (result, server) = await FetchAsync(sandbox, model);

        Assert.Equal(FetchOutcome.Downloaded, result.Outcome);
        Assert.Equal(1, server.Requests);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path, Token));
    }

    /// <summary>
    /// The whole point of staging: bytes that fail the digest never reach the path anything
    /// loads from, and are not left around to be resumed into a second wrong file.
    /// </summary>
    [Fact]
    public async Task Ensure_WhenTheBytesDoNotMatch_KeepsNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (result, _) = await FetchAsync(sandbox, Pinned(), new FakeServer(Encoding.UTF8.GetBytes("wrong")));

        Assert.Equal(FetchOutcome.Corrupt, result.Outcome);
        Assert.False(result.Usable);
        Assert.False(File.Exists(result.Path));
        Assert.Empty(Directory.GetFiles(sandbox.Home.ModelsDir));
    }

    [Fact]
    public async Task Ensure_WithNoPinnedDigest_RefusesToFetch()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var model = Pinned() with
        {
            Source = new ModelSource("owner/repo", "0123456789abcdef", "test-model.gguf", null),
        };

        var (result, server) = await FetchAsync(sandbox, model);

        Assert.Equal(FetchOutcome.NotPinned, result.Outcome);
        Assert.Equal(0, server.Requests);
        Assert.False(File.Exists(result.Path));
    }

    [Fact]
    public async Task Ensure_WhenTheServerFails_ReportsItWithoutThrowing()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (result, _) = await FetchAsync(
            sandbox,
            Pinned(),
            new FakeServer { Status = HttpStatusCode.ServiceUnavailable });

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(result.Path));
    }

    [Fact]
    public async Task Ensure_ResumesFromAPartialFile()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var model = Pinned();
        Directory.CreateDirectory(sandbox.Home.ModelsDir);
        var partial = ModelFetcher.PathFor(sandbox.Home, model) + ".partial";
        await File.WriteAllBytesAsync(partial, Payload[..1000], Token);

        var (result, server) = await FetchAsync(sandbox, model);

        Assert.Equal(FetchOutcome.Downloaded, result.Outcome);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path, Token));
        Assert.Equal("bytes=1000-", server.Ranges.Single());
    }

    /// <summary>
    /// A server may answer a range request with the whole file. Appending that to the 1000 bytes
    /// already on disk yields a file 1000 bytes too long — which the digest catches, but only
    /// after paying for the download twice. Restarting instead is the cheap correct move.
    /// </summary>
    [Fact]
    public async Task Ensure_WhenTheServerIgnoresTheRange_RestartsRatherThanConcatenates()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var model = Pinned();
        Directory.CreateDirectory(sandbox.Home.ModelsDir);
        var partial = ModelFetcher.PathFor(sandbox.Home, model) + ".partial";
        await File.WriteAllBytesAsync(partial, Payload[..1000], Token);

        var (result, _) = await FetchAsync(sandbox, model, new FakeServer { IgnoreRange = true });

        Assert.Equal(FetchOutcome.Downloaded, result.Outcome);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path, Token));
    }

    [Fact]
    public async Task Ensure_WithNoToken_SendsNoAuthorization()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (_, server) = await FetchAsync(sandbox, Pinned());

        Assert.Null(server.Authorizations.Single());
    }

    [Fact]
    public async Task Ensure_WithATokenInTheEnvironment_SendsItAsBearer()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (_, server) = await FetchAsync(
            sandbox,
            Pinned(),
            environment: name => name == "HF_TOKEN" ? "hf_secret" : null);

        Assert.Equal("Bearer hf_secret", server.Authorizations.Single());
    }

    [Fact]
    public async Task Ensure_ReportsProgressThatEndsAtTheFullSize()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var reports = new List<FetchProgress>();

        await FetchAsync(sandbox, Pinned(), progress: new Progress<FetchProgress>(reports.Add));

        // Progress<T> posts asynchronously, so wait for the last report rather than assuming it
        // has already landed by the time the fetch returns.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (reports.Count == 0 || reports[^1].Downloaded != Payload.Length)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Progress stalled at {reports.Count} reports.");
            await Task.Delay(10, Token);
        }

        Assert.Equal(Payload.Length, reports[^1].Total);
        Assert.Equal(1d, reports[^1].Fraction);
        Assert.All(reports, r => Assert.Equal("test-model", r.ModelId));
    }

    [Fact]
    public void Url_IsBuiltFromTheRepositoryRevisionAndFile()
    {
        var source = new ModelSource("owner/repo", "abc123", "model.gguf", null);

        Assert.Equal("https://huggingface.co/owner/repo/resolve/abc123/model.gguf", source.Url);
    }

    /// <summary>
    /// Every shipped rung must be fetchable, or the picker offers a model that cannot install.
    /// </summary>
    [Fact]
    public void EveryShippedModel_IsPinnedToACommitAndADigest()
    {
        Assert.All(EmbeddingModels.All, model =>
        {
            var source = Assert.IsType<ModelSource>(model.Source);

            Assert.True(model.IsFetchable, $"{model.Id} carries no digest.");
            Assert.Equal(64, source.Sha256!.Length);
            Assert.Equal(40, source.Revision.Length);
            Assert.DoesNotContain("main", source.Revision, StringComparison.Ordinal);
        });
    }
}
