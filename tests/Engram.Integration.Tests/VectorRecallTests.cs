using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The vector lane through the tool agents actually call.
/// </summary>
/// <remarks>
/// <para>Everything here rests on one query — "retry policy" — against one fact — "The uploader
/// uses exponential backoff." They share no term, so both lexical lanes are blind to the pair by
/// construction: term overlap matches literals and bm25 stems, and neither turns "backoff" into
/// "retry". If that fact comes back, an embedding is the only thing that could have found it.</para>
///
/// <para>The stand-in embedder is a keyword table rather than a real model, because a test that
/// depended on genuine semantic proximity would be asserting the quality of somebody else's
/// weights. What is under test is the wiring: that a rank produced by the vector index reaches the
/// fusion and changes what recall returns.</para>
/// </remarks>
public sealed class VectorRecallTests : IDisposable
{
    private const string Model = "stand-in-embed";
    private const int Dimensions = 8;
    private const string OnlyFindableByVector = "The uploader uses exponential backoff.";
    private const string Query = "retry policy";

    private readonly List<Process> started = [];

    private static string? Python =>
        new[] { "/usr/bin/python3", "/usr/local/bin/python3", "/opt/homebrew/bin/python3" }
            .FirstOrDefault(File.Exists);

    private static void RequireTheRealThings()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        if (OperatingSystem.IsWindows() || Python is null)
        {
            Assert.Skip("The stand-in embedding server is a python3 shebang script.");
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Starts an endpoint that maps text to a one-hot vector by concept, and returns its base URL.
    /// </summary>
    /// <remarks>
    /// Both the fact and the query land on dimension 0 without sharing a word, which is exactly the
    /// relationship a real embedding model would supply and the lexical lanes cannot.
    /// </remarks>
    private string StartStandInEmbedder(string directory)
    {
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "embed-server.py");
        var port = FreePort();

        File.WriteAllText(script, $$"""
            #!{{Python}}
            import sys, json
            from http.server import BaseHTTPRequestHandler, HTTPServer
            CONCEPTS = [
                ("retry", ["retry", "retries", "backoff", "redeliver"]),
                ("storage", ["vacuum", "compaction", "compact"]),
            ]
            def vector(text):
                low = text.lower()
                v = [0.0] * {{Dimensions}}
                for i, (_, words) in enumerate(CONCEPTS):
                    if any(w in low for w in words):
                        v[i] = 1.0
                        return v
                v[{{Dimensions}} - 1] = 1.0
                return v
            class H(BaseHTTPRequestHandler):
                def log_message(self, *a): pass
                def do_POST(self):
                    n = int(self.headers.get("content-length", 0))
                    body = json.loads(self.rfile.read(n) or b"{}")
                    texts = body.get("input", [])
                    if isinstance(texts, str): texts = [texts]
                    out = {"data": [{"index": i, "embedding": vector(t)} for i, t in enumerate(texts)]}
                    raw = json.dumps(out).encode()
                    self.send_response(200)
                    self.send_header("content-type", "application/json")
                    self.send_header("content-length", str(len(raw)))
                    self.end_headers()
                    self.wfile.write(raw)
            HTTPServer(("127.0.0.1", {{port}}), H).serve_forever()

            """);

        // Guarded rather than relying on the skip above, so the platform analyzer can see this is
        // unreachable on Windows.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var process = Process.Start(new ProcessStartInfo(script)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        started.Add(process);

        var endpoint = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v1";
        WaitUntilAnswering(endpoint);
        return endpoint;
    }

    private static void WaitUntilAnswering(string endpoint)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var content = new StringContent(
                    """{"input":["ping"],"model":"x"}""", System.Text.Encoding.UTF8, "application/json");
                using var response = client.PostAsync(new Uri(endpoint + "/embeddings"), content)
                    .GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            Thread.Sleep(50);
        }

        Assert.Fail("the stand-in embedding server never came up");
    }

    private static void WriteConfig(EngramHome home, string providerLine) =>
        File.WriteAllText(home.ConfigPath, providerLine);

    private static string EmbeddingConfig(string endpoint) => $"""
        [embedding]
        provider = "openai-compat"
        endpoint = "{endpoint}"
        model = "{Model}"
        dim = {Dimensions.ToString(CultureInfo.InvariantCulture)}
        """;

    /// <summary>Stores the fact's vector directly, so the test controls the geometry exactly.</summary>
    private static void IndexFact(VectorSandbox sandbox, long factId, int concept)
    {
        VectorIndex.EnsureCreated(sandbox.Connection, new EmbeddingSpace(Model, Dimensions));
        var vector = new float[Dimensions];
        vector[concept] = 1f;
        VectorIndex.Write(sandbox.Connection, null, factId, vector);
    }

    private static LocalRuntime NoRuntime(EngramHome home) => new(home);

    [Fact]
    public void AFactNoLexicalLaneCanReach_ComesBackFromRecall()
    {
        RequireTheRealThings();
        using var sandbox = new VectorSandbox();
        var endpoint = StartStandInEmbedder(Path.Combine(sandbox.Home.Root, "stand-in"));

        var factId = sandbox.AddFact("uploader", OnlyFindableByVector);
        IndexFact(sandbox, factId, concept: 0);
        WriteConfig(sandbox.Home, EmbeddingConfig(endpoint));

        var answer = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("s1"), new McpHomeState(false), NoRuntime(sandbox.Home), Query);

        Assert.Contains("backoff", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameQueryWithTheLaneOff_FindsNothing()
    {
        // The control. Without it the test above proves only that recall returns facts, not that
        // the vector lane is what reached this one.
        RequireTheRealThings();
        using var sandbox = new VectorSandbox();

        var factId = sandbox.AddFact("uploader", OnlyFindableByVector);
        IndexFact(sandbox, factId, concept: 0);
        WriteConfig(sandbox.Home, "[embedding]\nprovider = \"none\"\n");

        var answer = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("s1"), new McpHomeState(false), NoRuntime(sandbox.Home), Query);

        Assert.DoesNotContain("backoff", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderThatIsDown_LeavesRecallWorking()
    {
        // The whole lane is optional, so an endpoint that refuses connections must cost recall
        // its vector hits and nothing else. Failing the tool here would make configuring
        // embeddings strictly more dangerous than leaving them off.
        RequireTheRealThings();
        using var sandbox = new VectorSandbox();

        sandbox.AddFact("uploader", OnlyFindableByVector);
        var lexicallyFindable = sandbox.AddFact("nightly", "The nightly vacuum truncates the audit log.");
        IndexFact(sandbox, lexicallyFindable, concept: 1);
        WriteConfig(sandbox.Home, EmbeddingConfig($"http://127.0.0.1:{FreePort().ToString(CultureInfo.InvariantCulture)}/v1"));

        var answer = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("s1"), new McpHomeState(false), NoRuntime(sandbox.Home),
            "nightly vacuum");

        Assert.Contains("vacuum", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLane_ReportsOffWithoutAskingAnything()
    {
        RequireTheRealThings();
        using var sandbox = new VectorSandbox();
        WriteConfig(sandbox.Home, "[embedding]\nprovider = \"none\"\n");

        var result = VectorLane.Run(
            sandbox.Connection,
            sandbox.Home,
            EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)),
            Query,
            _ => null,
            seedK: 8);

        Assert.Equal(VectorLaneState.Off, result.State);
        Assert.Empty(result.Ranks);
    }

    [Fact]
    public void TheLane_RefusesAnIndexBuiltInADifferentSpace()
    {
        // D18's quiet failure. Distances between spaces are real numbers that mean nothing, so
        // this has to stop rather than rank.
        RequireTheRealThings();
        using var sandbox = new VectorSandbox();
        var endpoint = StartStandInEmbedder(Path.Combine(sandbox.Home.Root, "stand-in"));

        var factId = sandbox.AddFact("uploader", OnlyFindableByVector);
        VectorIndex.EnsureCreated(sandbox.Connection, new EmbeddingSpace("some-other-model", Dimensions));
        var vector = new float[Dimensions];
        vector[0] = 1f;
        VectorIndex.Write(sandbox.Connection, null, factId, vector);
        WriteConfig(sandbox.Home, EmbeddingConfig(endpoint));

        var result = VectorLane.Run(
            sandbox.Connection,
            sandbox.Home,
            EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)),
            Query,
            _ => null,
            seedK: 8);

        Assert.Equal(VectorLaneState.Unavailable, result.State);
        Assert.Contains("not comparable", result.Reason, StringComparison.Ordinal);
        Assert.Empty(result.Ranks);
    }

    [Fact]
    public void TheLane_RanksNearestFirstFromOne()
    {
        RequireTheRealThings();
        using var sandbox = new VectorSandbox();
        var endpoint = StartStandInEmbedder(Path.Combine(sandbox.Home.Root, "stand-in"));

        var near = sandbox.AddFact("uploader", OnlyFindableByVector);
        var far = sandbox.AddFact("nightly", "Compaction runs nightly at three.");
        IndexFact(sandbox, near, concept: 0);
        IndexFact(sandbox, far, concept: 1);
        WriteConfig(sandbox.Home, EmbeddingConfig(endpoint));

        var result = VectorLane.Run(
            sandbox.Connection,
            sandbox.Home,
            EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)),
            Query,
            _ => null,
            seedK: 8);

        Assert.Equal(VectorLaneState.Queried, result.State);
        Assert.Equal(1, result.Ranks[near]);
        Assert.True(result.Ranks[far] > result.Ranks[near], "the unrelated fact must not outrank the match");
    }

    [Fact]
    public void WithNoExtensionInstalled_TheLaneSaysSoRatherThanBlamingTheIndex()
    {
        // Deliberately does NOT install sqlite-vec, and so does not skip when it is absent: the
        // whole point is the diagnosis given when it is missing. "sqlite-vec is not there" sends
        // someone to fetch it; "no vector index in this store yet" sends them to build an index
        // they cannot build. Only the load performed here can tell those two apart, because
        // EngramDatabase.Open throws its own result away on purpose.
        if (OperatingSystem.IsWindows() || Python is null)
        {
            Assert.Skip("The stand-in embedding server is a python3 shebang script.");
        }

        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var endpoint = StartStandInEmbedder(Path.Combine(sandbox.Home.Root, "stand-in"));
        WriteConfig(sandbox.Home, EmbeddingConfig(endpoint));

        var result = VectorLane.Run(
            connection,
            sandbox.Home,
            EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)),
            Query,
            _ => null,
            seedK: 8);

        Assert.Equal(VectorLaneState.Unavailable, result.State);
        Assert.Contains("sqlite-vec is not in", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_DescribesTheLaneAsFeedingTheRankingBecauseItNowDoes()
    {
        // D30 in one assertion. The explainer used to report this lane as answerable and read by
        // nothing, which was true. If it ever says that again while recall is fusing vector ranks,
        // the explanation has stopped describing the ranker that runs.
        RequireTheRealThings();
        using var sandbox = new VectorSandbox();
        var endpoint = StartStandInEmbedder(Path.Combine(sandbox.Home.Root, "stand-in"));

        var factId = sandbox.AddFact("uploader", OnlyFindableByVector);
        IndexFact(sandbox, factId, concept: 0);
        WriteConfig(sandbox.Home, EmbeddingConfig(endpoint));

        var explanation = RetrievalExplainer.Explain(
            sandbox.Connection, sandbox.Home, Query, 500, null, DateTimeOffset.UtcNow, _ => null);

        var lane = Assert.Single(
            explanation.Lanes, l => l.Name.StartsWith("vector", StringComparison.Ordinal));
        Assert.Equal(LaneState.Contributing, lane.State);

        var candidate = Assert.Single(explanation.Candidates, c => c.Candidate.FactId == factId);
        Assert.Equal(1, candidate.Candidate.VectorRank);
    }

    public void Dispose()
    {
        foreach (var process in started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // Already gone.
            }

            process.Dispose();
        }
    }
}
