using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Covers <c>engram doctor</c>, which reads an instance and says what is wrong with it.
/// </summary>
/// <remarks>
/// The two assertions that carry the design are
/// <see cref="AStoreOneSchemaBehind_IsReported_AndDoctorDoesNotMigrateIt"/> and
/// <see cref="ProviderLocal_NeverStartsTheServerItReportsOn"/>. Both say the same thing about a
/// diagnostic: it may not perform the state it was asked to describe.
/// </remarks>
public sealed class DiagnosticsTests : IDisposable
{
    private const string Python = "/usr/bin/env python3";

    private readonly List<Process> started = [];

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
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }
    }

    private static DiagnosticReport Run(SandboxHome sandbox, bool reachOut = false, string? repoRoot = null) =>
        Diagnostics.Run(
            sandbox.Home,
            _ => null,
            repoRoot,
            // Nothing real: a doctor run must not depend on whether a server happens to be up on
            // the machine running the tests.
            new ServerLifecycle(new FakeProcessInspector(), new FakeServerHealthChecker(), new FakeServerLauncher()),
            reachOut,
            executablePath: "/nonexistent/engram",

            // Never the developer's own settings file: Claude Code's lives in the user profile,
            // outside anything SandboxHome can redirect, so a test that let this default would be
            // asserting on whoever happened to run it.
            claudeSettingsPath: Path.Combine(sandbox.Home.Root, "claude-settings.json"));

    private static Diagnosis Check(DiagnosticReport report, string name) =>
        Assert.Single(report.Checks, check => check.Name == name);

    private static void WriteConfig(SandboxHome sandbox, string toml) =>
        File.WriteAllText(sandbox.Home.ConfigPath, toml);

    [Fact]
    public void AHomeThatWasNeverInitialised_IsBrokenAndSaysToRunInit()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Directory.Delete(sandbox.Home.Root, recursive: true);

        var report = Run(sandbox);

        var home = Check(report, "home");
        Assert.Equal(DiagnosisState.Broken, home.State);
        Assert.Equal("engram init", home.Fix);
        Assert.False(report.Healthy);
    }

    [Fact]
    public void AnInitialisedHome_IsHealthy()
    {
        using var sandbox = new SandboxHome();

        var report = Run(sandbox);

        Assert.True(report.Healthy, string.Join("; ", report.Checks
            .Where(check => check.State is DiagnosisState.Broken)
            .Select(check => $"{check.Name}: {check.Detail}")));
        Assert.Equal(DiagnosisState.Ok, Check(report, "home").State);
        Assert.Equal(DiagnosisState.Ok, Check(report, "store").State);
    }

    /// <summary>
    /// D18 says an instance without embeddings is a supported configuration, not a degraded one.
    /// A doctor that fails on a choice the user made is one people stop reading, which costs the
    /// real faults their audience.
    /// </summary>
    [Fact]
    public void EmbeddingsSwitchedOff_ReportOffAndKeepTheInstanceHealthy()
    {
        using var sandbox = new SandboxHome();
        WriteConfig(sandbox, "[embedding]\nprovider = \"none\"\n");

        var report = Run(sandbox);

        Assert.Equal(DiagnosisState.Off, Check(report, "embedding").State);
        Assert.Equal(DiagnosisState.Off, Check(report, "vector index").State);
        Assert.True(report.Healthy);
        Assert.Equal(0, report.Broken);
    }

    /// <summary>
    /// The load-bearing one. <c>OpenInitialized</c> migrates on open and D31 makes that migration
    /// snapshot first, so reaching for it here would mean the question "is my store behind?" could
    /// not be asked — asking it would perform the answer.
    /// </summary>
    [Fact]
    public void AStoreOneSchemaBehind_IsReported_AndDoctorDoesNotMigrateIt()
    {
        using var sandbox = new SandboxHome();

        // Labelled old rather than genuinely old: what is under test is that doctor reads the
        // version and leaves it, and a real migration against these tables would be observable
        // either as a bumped version or as a failure, both of which this test catches.
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_meta SET value = '1' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        var snapshotsBefore = BackupStore.List(sandbox.Home).Count;

        var report = Run(sandbox);

        var store = Check(report, "store");
        Assert.Equal(DiagnosisState.Warn, store.State);
        Assert.Contains("schema 1", store.Detail, StringComparison.Ordinal);
        Assert.True(report.Healthy);

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Assert.Equal(1, EngramDatabase.ReadSchemaVersion(connection));
        }

        Assert.Equal(snapshotsBefore, BackupStore.List(sandbox.Home).Count);
    }

    [Fact]
    public void AStoreFromANewerEngram_IsBrokenRatherThanOpenedForWriting()
    {
        using var sandbox = new SandboxHome();

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_meta SET value = '99' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        var report = Run(sandbox);

        var store = Check(report, "store");
        Assert.Equal(DiagnosisState.Broken, store.State);
        Assert.Contains("newer Engram", store.Detail, StringComparison.Ordinal);
        Assert.False(report.Healthy);
    }

    /// <summary>
    /// The shape a WAL database copied with <c>cp</c> leaves behind: the file opens, and holds
    /// nothing, because everything real was still in the log (D31). Blaming the schema would send
    /// someone looking in the wrong place.
    /// </summary>
    [Fact]
    public void AStoreWithNoSchema_NamesTheCopyMistakeRatherThanTheSchema()
    {
        // Uninitialised, so this file is the only store there is. Adding a stray table to an
        // initialised home would leave a perfectly valid store that reports ok, which is what the
        // first version of this test actually asserted.
        using var sandbox = new SandboxHome(initialize: false);

        using (var connection = new SqliteConnection($"Data Source={sandbox.Home.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (x INTEGER);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        var report = Run(sandbox);

        var store = Check(report, "store");
        Assert.Equal(DiagnosisState.Broken, store.State);
        Assert.Contains("cp", store.Detail, StringComparison.Ordinal);
        Assert.Contains("backup restore", store.Fix!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A diagnostic is reached for when something is already wrong, so the state most likely to
    /// make a check throw is the state someone is running it in.
    /// </summary>
    [Fact]
    public void ACheckThatThrows_BecomesOneBrokenRowAndTheOthersStillRun()
    {
        using var sandbox = new SandboxHome();

        // Deliberately synthetic. A missing directory does not throw — RepoScanner walks it and
        // reports nothing, which is a fair answer and was this test's first, useless input. An
        // embedded null is rejected by every file API on every platform, so it induces the throw
        // itself rather than a condition that happens to cause one here.
        var report = Run(sandbox, repoRoot: "\0not-a-path");

        var indexing = Check(report, "indexing");
        Assert.Equal(DiagnosisState.Broken, indexing.State);
        Assert.Contains("could not run", indexing.Detail, StringComparison.Ordinal);

        Assert.Equal(DiagnosisState.Ok, Check(report, "home").State);
        Assert.Equal(DiagnosisState.Ok, Check(report, "store").State);
    }

    /// <summary>
    /// An unparseable line is skipped, which means the setting on it is silently a default — the
    /// failure is invisible at the point it matters, so it has to be loud here.
    /// </summary>
    [Fact]
    public void AConfigLineThatWillNotParse_IsBrokenBecauseTheSettingOnItSilentlyDefaults()
    {
        using var sandbox = new SandboxHome();
        WriteConfig(sandbox, "[embedding]\nthis line is not toml at all\n");

        var report = Run(sandbox);

        var home = Check(report, "home");
        Assert.Equal(DiagnosisState.Broken, home.State);
        Assert.Contains("unreadable", home.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderLocalWithNoWeights_SaysWhichModelToInstall()
    {
        using var sandbox = new SandboxHome();
        var model = EmbeddingModels.Default;
        WriteConfig(sandbox, $"[embedding]\nprovider = \"local\"\nmodel = \"{model.Id}\"\n");

        var report = Run(sandbox);

        var embedding = report.Checks.First(check => check.Name == "embedding" && check.State is DiagnosisState.Broken);
        Assert.Contains(model.FileName, embedding.Detail, StringComparison.Ordinal);
        Assert.Equal($"engram model install {model.Id}", embedding.Fix);
    }

    /// <summary>
    /// D35's boundary, from the other side. Resolving a local embedder means launching llama.cpp,
    /// so doctor checks the ingredients instead — a diagnostic that started a model process to
    /// find out whether one would start has both answered the question and changed it, and leaves
    /// several hundred megabytes resident behind a read-only command.
    /// </summary>
    [Fact]
    public void ProviderLocal_NeverStartsTheServerItReportsOn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in is a shell-executable script.");

        using var sandbox = new SandboxHome();
        var model = EmbeddingModels.Default;

        Directory.CreateDirectory(sandbox.Home.ModelsDir);
        File.WriteAllText(Path.Combine(sandbox.Home.ModelsDir, model.FileName), "not really weights");

        var marker = Path.Combine(sandbox.Home.Root, "it-ran");
        var standIn = InstallStandInThatRecordsBeingRun(sandbox.Home.LibDir, marker);

        WriteConfig(
            sandbox,
            $"[embedding]\nprovider = \"local\"\nmodel = \"{model.Id}\"\nserver_path = \"{standIn}\"\n");

        var report = Run(sandbox);

        Assert.Equal(DiagnosisState.Ok, Check(report, "llama-server").State);
        Assert.False(File.Exists(marker), "doctor executed llama-server rather than just locating it");
    }

    [Fact]
    public void ALocalServerThatIsNowhereToBeFound_ListsWhereItLooked()
    {
        using var sandbox = new SandboxHome();
        var model = EmbeddingModels.Default;

        Directory.CreateDirectory(sandbox.Home.ModelsDir);
        File.WriteAllText(Path.Combine(sandbox.Home.ModelsDir, model.FileName), "not really weights");
        WriteConfig(sandbox, $"[embedding]\nprovider = \"local\"\nmodel = \"{model.Id}\"\n");

        var report = Run(sandbox);

        var server = Check(report, "llama-server");
        Assert.Equal(DiagnosisState.Broken, server.State);
        Assert.Contains(sandbox.Home.LibDir, server.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deliberately does not skip when sqlite-vec is absent: its absence is the subject.
    /// </summary>
    [Fact]
    public void EmbeddingsConfiguredWithoutSqliteVec_BlamesTheExtensionNotTheStore()
    {
        using var sandbox = new SandboxHome();
        WriteConfig(
            sandbox,
            "[embedding]\nprovider = \"openai-compat\"\nmodel = \"m\"\ndim = 8\nendpoint = \"http://127.0.0.1:1/v1\"\n");

        var report = Run(sandbox);

        var index = Check(report, "vector index");
        Assert.Equal(DiagnosisState.Broken, index.State);
        Assert.Contains("sqlite-vec", index.Detail, StringComparison.Ordinal);
        Assert.Contains(sandbox.Home.LibDir, index.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A space mismatch is D18's quiet failure: distances between two spaces are real numbers that
    /// mean nothing, so the lane declines to run and recall loses it without anything erroring.
    /// </summary>
    [Fact]
    public void AnIndexInADifferentSpaceFromTheConfig_IsBrokenBecauseRecallSilentlyDropsTheLane()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new SandboxHome();
        VectorExtensionFile.InstallInto(sandbox.Home);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            VectorIndex.EnsureCreated(connection, new EmbeddingSpace("old-model", 8));
        }

        SqliteConnection.ClearAllPools();

        WriteConfig(
            sandbox,
            "[embedding]\nprovider = \"openai-compat\"\nmodel = \"new-model\"\ndim = 8\n"
                + "endpoint = \"http://127.0.0.1:1/v1\"\n");

        var report = Run(sandbox);

        var index = Check(report, "vector index");
        Assert.Equal(DiagnosisState.Broken, index.State);
        Assert.Contains("old-model", index.Detail, StringComparison.Ordinal);
        Assert.Contains("new-model", index.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// D34's silent failure, and the only reason doctor makes a network call at all: a wrong width
    /// errors nowhere. It stores vectors that rank like noise.
    /// </summary>
    [Fact]
    public void AWidthTheEndpointDoesNotAgreeWith_IsFoundOnlyByAsking()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in is a shell-executable script.");

        using var sandbox = new SandboxHome();
        var endpoint = StartStandInEmbedder(sandbox.Home.Root, dimensions: 8);

        WriteConfig(
            sandbox,
            $"[embedding]\nprovider = \"openai-compat\"\nmodel = \"m\"\ndim = 384\nendpoint = \"{endpoint}\"\n");

        var offline = Run(sandbox);
        Assert.Equal(
            DiagnosisState.Ok,
            offline.Checks.First(check => check.Name == "embedding").State);

        var online = Run(sandbox, reachOut: true);
        var embedding = online.Checks.First(check => check.Name == "embedding");
        Assert.Equal(DiagnosisState.Broken, embedding.State);
        Assert.Contains("dim = 384", embedding.Detail, StringComparison.Ordinal);
        Assert.Contains("8", embedding.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The endpoint accepts the connection and then says nothing, which is what a wedged provider
    /// looks like and is the only shape that reaches the timeout at all.
    /// </summary>
    /// <remarks>
    /// The first version of this test pointed at a closed port, and proved nothing: a refused
    /// connection comes back immediately, so it passed identically with the configured timeout in
    /// place of <see cref="Diagnostics.ProbeDeadline"/>. Falsification caught it. Held open, the
    /// configured two minutes is reachable, and only doctor's own deadline gets the answer back.
    /// </remarks>
    [Fact]
    public void AnEndpointThatAcceptsAndThenSaysNothing_IsBrokenOnDoctorsOwnDeadline()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in is a shell-executable script.");

        using var sandbox = new SandboxHome();
        var endpoint = StartBlackHole(sandbox.Home.Root);

        WriteConfig(
            sandbox,
            "[embedding]\nprovider = \"openai-compat\"\nmodel = \"m\"\ndim = 8\n"
                + $"endpoint = \"{endpoint}\"\ntimeout_ms = 120000\n");

        var clock = Stopwatch.StartNew();
        var report = Run(sandbox, reachOut: true);
        clock.Stop();

        Assert.Equal(
            DiagnosisState.Broken,
            report.Checks.First(check => check.Name == "embedding").State);

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(30),
            $"doctor waited {clock.Elapsed} on a wedged endpoint whose configured timeout is 120 s");
    }

    [Fact]
    public void TheClaudeCodeCheck_ReadsTheSettingsFileItIsGiven()
    {
        using var sandbox = new SandboxHome();

        var report = Run(sandbox);

        var claude = Check(report, "claude code");
        Assert.Equal(DiagnosisState.Warn, claude.State);
        Assert.Equal("engram permissions --apply", claude.Fix);
    }

    /// <summary>
    /// <c>file-touched</c> spools an edit per invocation and <see cref="SpoolReader.Drain"/> is
    /// called by nothing in production, so the count only rises. Counting it is the whole check —
    /// a doctor that stayed silent about a directory filling up would be hiding the one number
    /// that says so.
    /// </summary>
    [Fact]
    public void SpooledEditsAreCounted_AndSaidToBeGoingNowhere()
    {
        using var sandbox = new SandboxHome();

        Assert.Contains("empty", Check(Run(sandbox), "edit queue").Detail, StringComparison.Ordinal);

        Directory.CreateDirectory(sandbox.Home.QueueDir);
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(sandbox.Home.QueueDir, $"{i}.spool"), "{}");
        }

        var queue = Check(Run(sandbox), "edit queue");

        Assert.Equal(DiagnosisState.Off, queue.State);
        Assert.Contains("3 edits", queue.Detail, StringComparison.Ordinal);
        Assert.Contains("nothing drains them", queue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningsAlone_DoNotFailTheReport()
    {
        var report = new DiagnosticReport(
        [
            new Diagnosis("a", DiagnosisState.Ok, ""),
            new Diagnosis("b", DiagnosisState.Off, ""),
            new Diagnosis("c", DiagnosisState.Warn, ""),
        ]);

        Assert.True(report.Healthy);
        Assert.Equal(1, report.Warnings);
        Assert.Equal(0, report.Broken);
    }

    private static string InstallStandInThatRecordsBeingRun(string directory, string marker)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, LlamaServer.FileName);

        File.WriteAllText(path, $"#!/bin/sh\ntouch '{marker}'\nsleep 30\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Accepts connections and never answers them, holding each socket open.</summary>
    private string StartBlackHole(string directory)
    {
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "doctor-black-hole.py");
        var port = FreePort();

        File.WriteAllText(script, $$"""
            #!{{Python}}
            import socket
            s = socket.socket()
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            s.bind(("127.0.0.1", {{port}}))
            s.listen(16)
            held = []
            while True:
                c, _ = s.accept()
                held.append(c)

            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        started.Add(Process.Start(new ProcessStartInfo(script)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!);

        WaitUntilAccepting(port);
        return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v1";
    }

    private static void WaitUntilAccepting(int port)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }

        Assert.Fail("the stand-in black hole never started listening");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private string StartStandInEmbedder(string directory, int dimensions)
    {
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "doctor-embed-server.py");
        var port = FreePort();

        File.WriteAllText(script, $$"""
            #!{{Python}}
            import json
            from http.server import BaseHTTPRequestHandler, HTTPServer
            class H(BaseHTTPRequestHandler):
                def log_message(self, *a): pass
                def do_POST(self):
                    n = int(self.headers.get("content-length", 0))
                    body = json.loads(self.rfile.read(n) or b"{}")
                    texts = body.get("input", [])
                    if isinstance(texts, str): texts = [texts]
                    out = {"data": [{"index": i, "embedding": [0.1] * {{dimensions}}}
                                    for i in range(len(texts))]}
                    raw = json.dumps(out).encode()
                    self.send_response(200)
                    self.send_header("content-type", "application/json")
                    self.send_header("content-length", str(len(raw)))
                    self.end_headers()
                    self.wfile.write(raw)
            HTTPServer(("127.0.0.1", {{port}}), H).serve_forever()

            """);

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
}
