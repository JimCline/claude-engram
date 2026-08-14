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
[Collection(SqlitePoolCollection.Name)]
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

    private static DiagnosticReport Run(
        SandboxHome sandbox,
        bool reachOut = false,
        string? repoRoot = null,
        ServerLifecycle? lifecycle = null) =>
        Diagnostics.Run(
            sandbox.Home,
            _ => null,
            repoRoot,
            // Nothing real: a doctor run must not depend on whether a server happens to be up on
            // the machine running the tests.
            lifecycle ?? new ServerLifecycle(new FakeProcessInspector(), new FakeServerHealthChecker(), new FakeServerLauncher()),
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

    /// <summary>A healthy server on this home, launched from <paramref name="from"/>.</summary>
    private static ServerLifecycle LiveServer(SandboxHome sandbox, string from, string version)
    {
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        PidFile.Write(sandbox.Home, new PidFileRecord(4242, 7433, version, started));

        var inspector = new FakeProcessInspector();
        inspector.SetAlive(4242, new ProcessIdentity(from, started, "boot-a:4242"));

        var health = new FakeServerHealthChecker();
        health.Enqueue(new HealthCheckOutcome(
            HealthCheckStatus.Healthy,
            new HealthResponsePayload(4242, 7433, version, started)));

        return new ServerLifecycle(inspector, health, new FakeServerLauncher());
    }

    /// <summary>
    /// The row that sent the author looking for a dead server that was serving requests.
    /// </summary>
    /// <remarks>
    /// Measured on this instance: the installed binary reported the server up while a freshly built
    /// one reported the same pid file dead, in the same second. Identity used to include the
    /// executable path, which made "a working copy is asking about the installed server" — the
    /// normal case while developing — indistinguishable from a recycled pid.
    /// </remarks>
    [Fact]
    public void AServerStartedFromAnotherBinary_IsFineAndSaysWhichBinary()
    {
        using var sandbox = new SandboxHome();
        var report = Run(sandbox, lifecycle: LiveServer(sandbox, "/opt/engram/engram", EngramVersion.Current));

        var server = Check(report, "server");
        Assert.Equal(DiagnosisState.Ok, server.State);
        Assert.Contains("pid 4242", server.Detail, StringComparison.Ordinal);
        Assert.Null(server.Fix);

        // Named, because a row about a binary you are not running explains nothing without it.
        Assert.Contains("started from /opt/engram/engram", server.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// An upgraded binary against a server nobody restarted warns; it is not broken.
    /// </summary>
    /// <remarks>
    /// This used to land in <c>Wedged</c> — reported as "alive and not answering its health check"
    /// about a server that answered immediately and correctly. D37 reserves red for faults, and a
    /// red row for a working server is how a doctor stops being read.
    /// </remarks>
    [Fact]
    public void AServerOnAnotherVersion_WarnsAndSaysToRestartIt()
    {
        using var sandbox = new SandboxHome();
        var report = Run(sandbox, lifecycle: LiveServer(sandbox, "/opt/engram/engram", "0.0.0-ancient"));

        var server = Check(report, "server");
        Assert.Equal(DiagnosisState.Warn, server.State);
        Assert.Contains("0.0.0-ancient", server.Detail, StringComparison.Ordinal);
        Assert.Contains(EngramVersion.Current, server.Detail, StringComparison.Ordinal);
        Assert.Equal("engram stop, then engram start", server.Fix);
    }

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
    /// A line Engram wrote and no longer reads is untidy rather than wrong, so it warns and the
    /// instance stays healthy — D37 keeps exit 1 for what is actually broken. Reported ahead of the
    /// provider branch on purpose: a config old enough to carry a retired key is no more likely to
    /// be one with embeddings switched on.
    /// </summary>
    [Fact]
    public void ARetiredEmbeddingKey_WarnsWithoutFailingTheInstance()
    {
        using var sandbox = new SandboxHome();
        WriteConfig(sandbox, "[embedding]\nprovider = \"none\"\nmodel_path = \"~/somewhere/a.gguf\"\n");

        var report = Run(sandbox);

        var warned = report.Checks.First(
            check => check.Name == "embedding" && check.State == DiagnosisState.Warn);

        Assert.Contains("model_path", warned.Detail, StringComparison.Ordinal);
        Assert.Contains("ignored", warned.Detail, StringComparison.Ordinal);
        Assert.Equal("delete it from config.toml", warned.Fix);
        Assert.Equal(0, report.Broken);
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

    [Fact]
    public void TokenIndex_Ready_ReportsOk()
    {
        using var sandbox = new SandboxHome();

        var report = Run(sandbox);

        Assert.Equal(DiagnosisState.Ok, Check(report, "token index").State);
        Assert.True(report.Healthy);
    }

    /// <summary>
    /// Never <see cref="DiagnosisState.Broken"/> (D37, spec ruling 3): an unbuilt or stale token
    /// index costs the overlap lane and nothing else — recall still answers from the other lanes.
    /// </summary>
    [Fact]
    public void TokenIndexStale_WarnsWithoutFailingTheInstance()
    {
        using var sandbox = new SandboxHome();

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE schema_meta SET value = '0' WHERE key = 'fact_token_version';";
            stale.ExecuteNonQuery();
        }

        var report = Run(sandbox);

        var tokenIndex = Check(report, "token index");
        Assert.Equal(DiagnosisState.Warn, tokenIndex.State);
        Assert.NotNull(tokenIndex.Fix);
        Assert.Equal(0, report.Broken);
        Assert.True(report.Healthy);
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
    /// D35's boundary, from the other side. Resolving a local embedder now means reading a GGUF
    /// into this process, so doctor checks the ingredients instead — a diagnostic that loaded a
    /// model to find out whether one would load has both answered the question and changed it, and
    /// leaves several hundred megabytes resident behind a read-only command.
    /// </summary>
    /// <remarks>
    /// The llama-server era could prove this directly: put an executable where the binary goes and
    /// see whether it ran. There is no process to catch any more, so the evidence is that the
    /// weights are unreadable. <c>File.Exists</c> answers true for a file with no permissions;
    /// anything that opens it fails. A doctor that resolved an embedder would therefore report this
    /// row Broken, and it reports Ok.
    /// </remarks>
    [Fact]
    public void ProviderLocal_NeverLoadsTheWeightsItReportsOn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes do not deny reads the same way.");

        using var sandbox = new SandboxHome();
        var model = EmbeddingModels.Default;

        Directory.CreateDirectory(sandbox.Home.ModelsDir);
        var weights = Path.Combine(sandbox.Home.ModelsDir, model.FileName);
        File.WriteAllText(weights, "not really weights");

        WriteConfig(sandbox, $"[embedding]\nprovider = \"local\"\nmodel = \"{model.Id}\"\n");

        // Written as a guard rather than relying on the skip above, so the platform analyzer can
        // see that these calls are unreachable on Windows.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(weights, UnixFileMode.None);
        }

        try
        {
            var embedding = Check(report: Run(sandbox), name: "embedding");

            Assert.Equal(DiagnosisState.Ok, embedding.State);
            Assert.Contains(model.Id, embedding.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                // Restored so the sandbox can delete itself.
                File.SetUnixFileMode(weights, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public void ProviderLocal_WithNoWeightsDownloaded_SaysHowToGetThem()
    {
        using var sandbox = new SandboxHome();
        var model = EmbeddingModels.Default;

        WriteConfig(sandbox, $"[embedding]\nprovider = \"local\"\nmodel = \"{model.Id}\"\n");

        var embedding = Check(Run(sandbox), "embedding");

        Assert.Equal(DiagnosisState.Broken, embedding.State);
        Assert.Contains(sandbox.Home.ModelsDir, embedding.Detail, StringComparison.Ordinal);
        Assert.Contains($"engram model install {model.Id}", embedding.Fix ?? "", StringComparison.Ordinal);
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
    /// <c>file-touched</c> spools an edit per invocation and its consumer, the code indexer, is not
    /// built. Counting is the whole check — a doctor that stayed silent about a directory filling
    /// up would be hiding the one number that says so.
    /// </summary>
    [Fact]
    public void SpooledEditsAreCounted_AndSaidToBeWaitingForAnIndexer()
    {
        using var sandbox = new SandboxHome();

        Assert.Contains("empty", Check(Run(sandbox), "edit queue").Detail, StringComparison.Ordinal);

        Spool(sandbox, 3);

        var queue = Check(Run(sandbox), "edit queue");

        Assert.Equal(DiagnosisState.Off, queue.State);
        Assert.Contains("3 edits", queue.Detail, StringComparison.Ordinal);
        Assert.Contains("waiting for an indexer", queue.Detail, StringComparison.Ordinal);

        // A backlog is the expected state until that indexer exists, so there is nothing to fix.
        Assert.Null(queue.Fix);
    }

    /// <summary>
    /// Past the compaction threshold the count stops being ordinary.
    /// </summary>
    /// <remarks>
    /// Session start compacts an oversized queue on its own, so a queue this large means that pass
    /// has not been running — an install with no hooks, or a home nothing has opened in a while.
    /// Still <see cref="DiagnosisState.Off"/>, because it is not a fault (D37); the fix appears
    /// because there is now something a person can usefully type.
    /// </remarks>
    [Fact]
    public void AnOversizedQueue_GainsTheCompactionFixWithoutBecomingAFault()
    {
        using var sandbox = new SandboxHome();

        Spool(sandbox, SpoolCompactor.Threshold + 1);

        var queue = Check(Run(sandbox), "edit queue");

        Assert.Equal(DiagnosisState.Off, queue.State);
        Assert.Equal("engram queue compact --apply", queue.Fix);
    }

    private static void Spool(SandboxHome sandbox, int count)
    {
        Directory.CreateDirectory(sandbox.Home.QueueDir);
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(sandbox.Home.QueueDir, $"{i}.spool"), "{}");
        }
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

    /// <summary>
    /// Starts the stand-in embedding endpoint and returns its URL.
    /// </summary>
    /// <remarks>
    /// <para><b>The server picks its own port and reports it back.</b> Choosing one here with
    /// <see cref="FreePort"/> first meant handing back a port that was free at that instant and
    /// binding it a process start later — and on a loaded CI runner something else can take it in
    /// between, at which point python exits with <c>Address already in use</c>. Binding port 0
    /// closes the window rather than narrowing it, and the socket is listening before the number is
    /// printed, because <c>HTTPServer</c> binds and listens in its constructor.</para>
    ///
    /// <para><b>A server that dies says why.</b> stdout and stderr were already redirected and then
    /// never read, so the previous version of this spent twenty seconds polling a process that had
    /// already exited and reported only that it "never came up" — which is how this stayed an
    /// unexplained macOS flake instead of a fixed bug.</para>
    /// </remarks>
    private string StartStandInEmbedder(string directory, int dimensions)
    {
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "doctor-embed-server.py");

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
            server = HTTPServer(("127.0.0.1", 0), H)
            print(server.server_address[1], flush=True)
            server.serve_forever()

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

        var port = ReadPort(process);
        var endpoint = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v1";
        WaitUntilAnswering(endpoint, process);
        return endpoint;
    }

    /// <summary>The port the server bound, or a failure carrying whatever it said instead.</summary>
    private static int ReadPort(Process process)
    {
        // Bounded off-thread, because a python that hangs before printing would otherwise hang the
        // whole test run rather than fail it. The bound is sized for a CI runner's first python
        // spawn, not a warm one: the alphabetically-first test in this class paid a cold-start
        // that 30 seconds did not cover, twice, while every later test on the same runner passed.
        var line = Task.Run(process.StandardOutput.ReadLine);
        if (!line.Wait(TimeSpan.FromSeconds(120)))
        {
            // Killed before Complaint so stderr becomes readable — draining it while the
            // process lives blocks, and a timeout that reports nothing is how this stayed
            // an unexplained flake instead of a fixed bug.
            process.Kill(entireProcessTree: true);
            Assert.Fail($"the stand-in embedding server never reported a port: {Complaint(process)}");
        }

        if (int.TryParse(line.Result, CultureInfo.InvariantCulture, out var port))
        {
            return port;
        }

        Assert.Fail($"the stand-in embedding server did not start: {Complaint(process)}");
        return 0;
    }

    /// <summary>
    /// Whatever the process has to say for itself, read only once it is safe to read.
    /// </summary>
    /// <remarks>
    /// stderr is drained only after the process has exited. Draining it while the server is running
    /// blocks forever, which would turn a diagnostic into the hang it is meant to explain.
    /// </remarks>
    private static string Complaint(Process process)
    {
        if (!process.WaitForExit(2000))
        {
            return "it is still running but said nothing";
        }

        var stderr = process.StandardError.ReadToEnd().Trim();
        return $"exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}"
            + (stderr.Length == 0 ? ", and no output" : $": {stderr}");
    }

    private static void WaitUntilAnswering(string endpoint, Process process)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                Assert.Fail($"the stand-in embedding server exited while starting: {Complaint(process)}");
            }

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

        Assert.Fail($"the stand-in embedding server never answered: {Complaint(process)}");
    }

    // ---- doctor in a directory too large to scan ----

    /// <summary>
    /// The advice is the point, not the state. Left as <c>Off</c> with its usual fix line, the row
    /// answers a home directory with "engram index --apply" — an instruction to index the thing
    /// that just could not be walked.
    /// </summary>
    [Fact]
    public void ADirectoryTooLargeToScan_WarnsInsteadOfOfferingToIndexIt()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "enormous");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var row = Diagnostics.CheckRepo(
            directory,
            ConfigFile.Empty,
            connection,
            new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000));

        Assert.Equal(DiagnosisState.Warn, row.State);
        Assert.Contains("partial", row.Detail, StringComparison.Ordinal);
        Assert.NotNull(row.Fix);
        Assert.DoesNotContain("engram index", row.Fix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half that would catch a budget so tight it fires on real work: an ordinary directory is
    /// still reported flat, with the offer to index it intact.
    /// </summary>
    [Fact]
    public void AnOrdinaryUnindexedDirectory_IsStillOffOfferingToIndexIt()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Off, row.State);
        Assert.DoesNotContain("partial", row.Detail, StringComparison.Ordinal);
        Assert.Equal("engram index --apply", row.Fix);
    }

    // ---- doctor on a repo whose deletions were suppressed (commit E3) ----

    /// <summary>
    /// Asserts the state, not the Notes text (docs/repo-index-remediation-spec.md §7) — a
    /// Notes-only assertion passes with the defect fully present, which is how the E2 version of
    /// this was missed.
    /// </summary>
    [Fact]
    public void RegisteredRepo_DeletionsSuppressedByTruncation_WarnsWithState()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var identity = CodeIndexer.ResolveIdentity(directory);
        RepoEnrollment.Enroll(connection, identity, directory, DateTimeOffset.UtcNow);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(directory, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(
                directory,
                Apply: true,
                Drain: false,
                Full: true,
                Budget: new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000)),
            DateTimeOffset.UtcNow);

        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Warn, row.State);
    }

    /// <summary>
    /// Ruling 2's required negative (docs/repo-index-remediation-spec.md §14): unlike
    /// <see cref="RegisteredRepo_DeletionsSuppressedByTruncation_WarnsWithState"/>, this repo has
    /// never been indexed before — its very first <see cref="CodeIndexer.Index"/> call is the one
    /// that truncates. <c>EnsureRepo</c>'s dry-run early return never inserts a
    /// <c>repo_registry</c> row, so this only warns if <c>CodeIndexer</c> creates that row before
    /// the suppression write reaches it within this same call — a row the suppression write can
    /// attach to has to already exist, and both happen inside one <c>Index</c> invocation here.
    /// </summary>
    [Fact]
    public void NeverBeforeIndexedRepo_TruncatedOnFirstScan_StillWarns()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var identity = CodeIndexer.ResolveIdentity(directory);
        RepoEnrollment.Enroll(connection, identity, directory, DateTimeOffset.UtcNow);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(
                directory,
                Apply: true,
                Drain: false,
                Full: true,
                Budget: new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000)),
            DateTimeOffset.UtcNow);

        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Warn, row.State);
    }

    /// <summary>
    /// Required negative test (docs/repo-index-remediation-spec.md §14.2): a repo that is
    /// registered but never enrolled — the population <c>engram index --apply &lt;path&gt;</c>
    /// produces on a repo nobody enrolled — must still warn on suppressed deletions.
    /// Suppression describes the integrity of the index, which is identical whether or not
    /// anyone enrolled the repo; the other E3 tests all call <see cref="RepoEnrollment.Enroll"/>
    /// first, so this population is untested by construction and would miss a suppression write
    /// keyed to <c>repo_enrollment</c> instead of <c>repo_registry</c> (§14.2's "why not
    /// repo_enrollment").
    /// </summary>
    [Fact]
    public void RegisteredButNeverEnrolledRepo_TruncatedScan_StillWarns()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(
                directory,
                Apply: true,
                Drain: false,
                Full: true,
                Budget: new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000)),
            DateTimeOffset.UtcNow);

        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Warn, row.State);
    }

    /// <summary>
    /// A store with no <c>repo_registry</c> table at all — the "no such table" catch in
    /// <see cref="Diagnostics.CheckRepo"/> — must say why the repo check did not run rather than
    /// reporting a plain <c>Ok</c> row indistinguishable from one that actually checked (§14.5.2).
    /// </summary>
    [Fact]
    public void StoreWithNoRepoRegistryTable_NotTruncated_ExplainsWhyTheCheckDidNotRun()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE repo_registry;";
            command.ExecuteNonQuery();
        }

        using var reopened = EngramDatabase.Open(sandbox.Home);
        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, reopened);

        Assert.Equal(DiagnosisState.Ok, row.State);
        Assert.Contains("store predates the repository index", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same missing-table path as above, but the scan is also truncated — the Warn branch carries
    /// the same explanatory clause as the Ok branch, and both must be guarded independently.
    /// </summary>
    [Fact]
    public void StoreWithNoRepoRegistryTable_TruncatedScan_ExplainsWhyTheCheckDidNotRun()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE repo_registry;";
            command.ExecuteNonQuery();
        }

        using var reopened = EngramDatabase.Open(sandbox.Home);
        var row = Diagnostics.CheckRepo(
            directory, ConfigFile.Empty, reopened, new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000));

        Assert.Equal(DiagnosisState.Warn, row.State);
        Assert.Contains("store predates the repository index", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredRepo_DeletionsSuppressedByEmptyScan_WarnsWithState()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var identity = CodeIndexer.ResolveIdentity(directory);
        RepoEnrollment.Enroll(connection, identity, directory, DateTimeOffset.UtcNow);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(directory, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        File.Delete(Path.Combine(directory, "a.cs"));

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(directory, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Warn, row.State);
    }

    /// <summary>
    /// The load-bearing half (docs/repo-index-remediation-spec.md §7): a column set on suppression
    /// and never cleared warns forever about a resolved condition (D33's retired-key shape).
    /// Falsified by deleting the <c>ClearSuppressedReason</c> call from
    /// <c>CodeIndexer.cs</c>'s post-write block: confirmed red — this test reported <c>Warn</c>
    /// where it asserts <c>Ok</c> — then restored.
    /// </summary>
    [Fact]
    public void SuppressionClears_AfterACleanFullScanCompletes()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var identity = CodeIndexer.ResolveIdentity(directory);
        RepoEnrollment.Enroll(connection, identity, directory, DateTimeOffset.UtcNow);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(directory, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(
                directory,
                Apply: true,
                Drain: false,
                Full: true,
                Budget: new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000)),
            DateTimeOffset.UtcNow);

        Assert.Equal(DiagnosisState.Warn, Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection).State);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(directory, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Ok, row.State);
    }

    /// <summary>The negative that stops this warning from firing on every registered repo.</summary>
    [Fact]
    public void NeverSuppressedRegisteredRepo_ReportsOkWithNoSuppressionWording()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "project");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.cs"), "class A;\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var identity = CodeIndexer.ResolveIdentity(directory);
        RepoEnrollment.Enroll(connection, identity, directory, DateTimeOffset.UtcNow);

        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(directory, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        var row = Diagnostics.CheckRepo(directory, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Ok, row.State);
        Assert.DoesNotContain("skipped", row.Detail, StringComparison.Ordinal);
    }
}
