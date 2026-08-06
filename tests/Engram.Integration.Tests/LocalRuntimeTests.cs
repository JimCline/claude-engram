using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The local provider against a real child process.
/// </summary>
/// <remarks>
/// The stand-in below is a Python script named <c>llama-server</c> that answers <c>/health</c> and
/// <c>/v1/embeddings</c>. That is enough to exercise everything Engram owns — locating a binary,
/// launching it, waiting for it, embedding through it, and killing it — without needing llama.cpp
/// installed to run the suite. What it deliberately cannot prove is that the real server accepts
/// these arguments; only running one does that.
/// </remarks>
public sealed class LocalRuntimeTests
{
    private static readonly EmbeddingModel Nomic = EmbeddingModels.Find("nomic-embed-text-v1.5")!;

    private static string? Python =>
        new[] { "/usr/bin/python3", "/usr/local/bin/python3", "/opt/homebrew/bin/python3" }
            .FirstOrDefault(File.Exists);

    private static void RequireAChildProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The stand-in server is a shebang script, which Windows will not execute.");
        }

        if (Python is null)
        {
            Assert.Skip("No python3 to run the stand-in server with.");
        }
    }

    /// <summary>Writes an executable stand-in <c>llama-server</c> and returns where it landed.</summary>
    private static string InstallStandIn(string directory, int dimensions = 768, string behaviour = "serve")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, LlamaServer.FileName);

        // Records its own arguments beside itself, so a test can assert on the command line
        // without parsing anything out of the process table.
        File.WriteAllText(path, $$"""
            #!{{Python}}
            import sys, json, pathlib
            argv = sys.argv[1:]
            pathlib.Path(__file__ + ".args").write_text("\n".join(argv))
            if "{{behaviour}}" == "die":
                print("stand-in refusing to load", file=sys.stderr)
                sys.exit(3)
            from http.server import BaseHTTPRequestHandler, HTTPServer
            port = int(argv[argv.index("--port") + 1])
            class H(BaseHTTPRequestHandler):
                def log_message(self, *a): pass
                def _send(self, obj):
                    raw = json.dumps(obj).encode()
                    self.send_response(200)
                    self.send_header("content-type", "application/json")
                    self.send_header("content-length", str(len(raw)))
                    self.end_headers()
                    self.wfile.write(raw)
                def do_GET(self):
                    self._send({"status": "ok"}) if self.path == "/health" else self.send_error(404)
                def do_POST(self):
                    n = int(self.headers.get("content-length", 0))
                    body = json.loads(self.rfile.read(n) or b"{}")
                    texts = body.get("input", [])
                    if isinstance(texts, str): texts = [texts]
                    self._send({"data": [{"index": i, "embedding": [0.1] * {{dimensions}}}
                                         for i in range(len(texts))]})
            HTTPServer(("127.0.0.1", port), H).serve_forever()

            """);

        // Written as a guard rather than relying on the skip above, so the platform analyzer can
        // see that this call is unreachable on Windows.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Puts a placeholder where the model would be, since only its presence is checked.</summary>
    private static void PretendTheModelIsDownloaded(EngramHome home, EmbeddingModel model)
    {
        Directory.CreateDirectory(home.ModelsDir);
        File.WriteAllText(ModelFetcher.PathFor(home, model), "not really a gguf");
    }

    private static bool Answers(Uri endpoint)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            return client.GetAsync(new Uri(endpoint, "/health")).GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // -- locating --

    [Fact]
    public void AConfiguredPath_WinsOverEverythingElse()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var elsewhere = Path.Combine(sandbox.Home.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var named = Path.Combine(elsewhere, LlamaServer.FileName);
        File.WriteAllText(named, "");

        Directory.CreateDirectory(sandbox.Home.LibDir);
        File.WriteAllText(Path.Combine(sandbox.Home.LibDir, LlamaServer.FileName), "");

        var found = LlamaServer.Locate(sandbox.Home, named, _ => elsewhere);

        Assert.Equal(named, found?.Path);
        Assert.Equal("config", found?.Source);
    }

    [Fact]
    public void AConfiguredPathThatIsNotThere_IsNotQuietlyReplaced()
    {
        // The failure this prevents: someone points at a specific build, mistypes it, and gets a
        // different binary off PATH that appears to work. Running the wrong llama-server is worse
        // than running none, because nothing about the result says which one answered.
        using var sandbox = new SandboxHome(initialize: false);
        var onPath = Path.Combine(sandbox.Home.Root, "bin");
        Directory.CreateDirectory(onPath);
        File.WriteAllText(Path.Combine(onPath, LlamaServer.FileName), "");

        var found = LlamaServer.Locate(sandbox.Home, Path.Combine(sandbox.Home.Root, "typo"), _ => onPath);

        Assert.Null(found);
    }

    [Fact]
    public void TheEngramLibDirectory_ComesBeforePath()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Directory.CreateDirectory(sandbox.Home.LibDir);
        var beside = Path.Combine(sandbox.Home.LibDir, LlamaServer.FileName);
        File.WriteAllText(beside, "");

        var onPath = Path.Combine(sandbox.Home.Root, "bin");
        Directory.CreateDirectory(onPath);
        File.WriteAllText(Path.Combine(onPath, LlamaServer.FileName), "");

        var found = LlamaServer.Locate(sandbox.Home, null, _ => onPath);

        Assert.Equal(beside, found?.Path);
        Assert.Equal("lib", found?.Source);
    }

    [Fact]
    public void Path_IsTheLastPlaceLookedAndTheSourceSaysSo()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var onPath = Path.Combine(sandbox.Home.Root, "bin");
        Directory.CreateDirectory(onPath);
        var binary = Path.Combine(onPath, LlamaServer.FileName);
        File.WriteAllText(binary, "");

        var found = LlamaServer.Locate(sandbox.Home, null, _ => $"{Path.PathSeparator}{onPath}");

        Assert.Equal(binary, found?.Path);
        Assert.Equal("PATH", found?.Source);
    }

    [Fact]
    public void WithNothingAnywhere_NothingIsFound()
    {
        using var sandbox = new SandboxHome(initialize: false);

        Assert.Null(LlamaServer.Locate(sandbox.Home, null, _ => null));
    }

    // -- the command line --

    [Fact]
    public void TheCommandLine_AsksForEmbeddingsAndTheGpu()
    {
        var args = LlamaServer.Arguments("/models/x.gguf", Nomic, 9999).ToList();

        Assert.Contains("--embedding", args);
        Assert.Contains("/models/x.gguf", args);
        Assert.Equal("9999", args[args.IndexOf("--port") + 1]);
        Assert.Equal("127.0.0.1", args[args.IndexOf("--host") + 1]);
        Assert.Equal("99", args[args.IndexOf("-ngl") + 1]);
    }

    [Fact]
    public void TheBatchSize_MatchesTheWindowRatherThanTheDefault()
    {
        // llama.cpp will not pool an embedding across physical batches, so an input longer than
        // the micro-batch is refused outright. Leaving these at the 512-token default would fail
        // on long facts and on nothing else, which is the worst shape a bug can have.
        var args = LlamaServer.Arguments("/models/x.gguf", Nomic, 1).ToList();
        var context = args[args.IndexOf("--ctx-size") + 1];

        Assert.Equal(context, args[args.IndexOf("--batch-size") + 1]);
        Assert.Equal(context, args[args.IndexOf("--ubatch-size") + 1]);
    }

    [Fact]
    public void AWindowLargerThanEngramNeeds_IsCappedRatherThanAllocated()
    {
        var qwen = EmbeddingModels.Find("qwen3-embedding-0.6b")!;
        Assert.True(qwen.ContextTokens > LlamaServer.MaxServedContext, "the cap must actually bind");

        var args = LlamaServer.Arguments("/models/x.gguf", qwen, 1).ToList();

        Assert.Equal(
            LlamaServer.MaxServedContext.ToString(System.Globalization.CultureInfo.InvariantCulture),
            args[args.IndexOf("--ctx-size") + 1]);
    }

    [Fact]
    public void AWindowSmallerThanTheCap_IsLeftAlone()
    {
        var mini = EmbeddingModels.Find("all-minilm-l6-v2")!;
        var args = LlamaServer.Arguments("/models/x.gguf", mini, 1).ToList();

        Assert.Equal(
            mini.ContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            args[args.IndexOf("--ctx-size") + 1]);
    }

    // -- refusing, without starting anything --

    [Fact]
    public void AModelThatWasNeverDownloaded_SaysHowToGetIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var location = new LlamaServerLocation("/nonexistent/llama-server", "config");

        var start = LlamaServer.Start(sandbox.Home, Nomic, location, TimeSpan.FromSeconds(1));

        Assert.False(start.Started);
        Assert.Contains("engram model install", start.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelNameEngramDoesNotKnow_IsRefusedBeforeAnythingIsLaunched()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var opened = runtime.Open("not-a-real-model", null, TimeSpan.FromSeconds(1));

        Assert.False(opened.Open);
        Assert.Contains("not a model Engram knows", opened.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoBinaryAnywhere_TheReasonSaysWhereItLooked()
    {
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var opened = runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(1));

        Assert.False(opened.Open);
        Assert.Contains(sandbox.Home.LibDir, opened.Reason, StringComparison.Ordinal);
        Assert.Contains("PATH", opened.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredBinaryThatIsMissing_NamesThePathRatherThanThePlacesItDidNotLook()
    {
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var opened = runtime.Open(Nomic.Id, "/nowhere/llama-server", TimeSpan.FromSeconds(1));

        Assert.False(opened.Open);
        Assert.Contains("/nowhere/llama-server", opened.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsRunningUntilSomethingAsks()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        Assert.Null(runtime.Loaded);
    }

    // -- actually launching one --

    [Fact]
    public void AStandInServer_IsStartedWaitedForAndReported()
    {
        RequireAChildProcess();
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        InstallStandIn(sandbox.Home.LibDir);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var opened = runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(20));

        Assert.True(opened.Open, opened.Reason);
        Assert.Equal(Nomic.Id, runtime.Loaded);
        Assert.True(Answers(opened.Endpoint!));
    }

    [Fact]
    public void TheModelPathHandedToTheServer_IsTheOneEngramDownloadedTo()
    {
        RequireAChildProcess();
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        var standIn = InstallStandIn(sandbox.Home.LibDir);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        Assert.True(runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(20)).Open);

        var launchedWith = File.ReadAllLines(standIn + ".args");
        Assert.Contains(ModelFetcher.PathFor(sandbox.Home, Nomic), launchedWith);
        Assert.Contains("--embedding", launchedWith);
    }

    [Fact]
    public void AskingTwiceForTheSameModel_ReusesTheServerRatherThanLoadingItAgain()
    {
        RequireAChildProcess();
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        InstallStandIn(sandbox.Home.LibDir);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var first = runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(20));
        var second = runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(20));

        Assert.True(first.Open, first.Reason);
        Assert.Equal(first.Endpoint, second.Endpoint);
        Assert.Contains("already running", second.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForADifferentModel_ReplacesTheServerRatherThanLoadingBoth()
    {
        RequireAChildProcess();
        var mini = EmbeddingModels.Find("all-minilm-l6-v2")!;
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        PretendTheModelIsDownloaded(sandbox.Home, mini);
        InstallStandIn(sandbox.Home.LibDir);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var first = runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(20));
        var second = runtime.Open(mini.Id, null, TimeSpan.FromSeconds(20));

        Assert.True(second.Open, second.Reason);
        Assert.NotEqual(first.Endpoint, second.Endpoint);
        Assert.Equal(mini.Id, runtime.Loaded);
        Assert.False(Answers(first.Endpoint!), "the first server should have been stopped");
    }

    [Fact]
    public void DisposingTheRuntime_KillsTheServerItStarted()
    {
        // The leak this prevents is expensive and silent: llama-server does not exit when its
        // parent does, so one that outlives Engram keeps a model resident until someone notices.
        RequireAChildProcess();
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        InstallStandIn(sandbox.Home.LibDir);

        Uri endpoint;
        using (var runtime = new LocalRuntime(sandbox.Home, _ => null))
        {
            var opened = runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(20));
            Assert.True(opened.Open, opened.Reason);
            endpoint = opened.Endpoint!;
            Assert.True(Answers(endpoint));
        }

        Assert.False(Answers(endpoint));
    }

    [Fact]
    public void AServerThatExitsWhileLoading_ReportsWhatItSaid()
    {
        RequireAChildProcess();
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        InstallStandIn(sandbox.Home.LibDir, behaviour: "die");
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var opened = runtime.Open(Nomic.Id, null, TimeSpan.FromSeconds(20));

        Assert.False(opened.Open);
        Assert.Contains("stand-in refusing to load", opened.Reason, StringComparison.Ordinal);
    }

    // -- the whole path, through the factory --

    private static EmbeddingSettings Local(string modelId) => EmbeddingSettings.Disabled with
    {
        Provider = EmbeddingProvider.Local,
        Model = modelId,
        Dimensions = EmbeddingModels.Find(modelId)!.Dimensions,
    };

    [Fact]
    public void WithoutARuntimeToHostIt_TheFactorySaysWhoDoes()
    {
        var resolution = EmbedderFactory.Create(Local(Nomic.Id), _ => null);

        Assert.False(resolution.Resolved);
        Assert.Contains("engram serve", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithARuntime_TheFactoryReturnsAnEmbedderThatEmbeds()
    {
        RequireAChildProcess();
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        InstallStandIn(sandbox.Home.LibDir, dimensions: Nomic.Dimensions);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var resolution = EmbedderFactory.Create(Local(Nomic.Id), _ => null, client: null, runtime);

        Assert.True(resolution.Resolved, resolution.Reason);
        Assert.Equal(new EmbeddingSpace(Nomic.Id, Nomic.Dimensions), resolution.Embedder!.Space);

        var vectors = await resolution.Embedder.EmbedAsync(
            ["one fact", "another"], TestContext.Current.CancellationToken);

        Assert.Equal(2, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(Nomic.Dimensions, v!.Length));
    }

    [Fact]
    public void ARuntimeThatCannotStart_ReachesTheCallerAsTheReasonItGave()
    {
        // The factory must not paper over the runtime's account with a generic one. "no
        // llama-server on this machine" and "the model is not downloaded" need different actions.
        using var sandbox = new SandboxHome(initialize: false);
        PretendTheModelIsDownloaded(sandbox.Home, Nomic);
        using var runtime = new LocalRuntime(sandbox.Home, _ => null);

        var resolution = EmbedderFactory.Create(Local(Nomic.Id), _ => null, client: null, runtime);

        Assert.False(resolution.Resolved);
        Assert.Contains("No llama-server found", resolution.Reason, StringComparison.Ordinal);
    }
}
