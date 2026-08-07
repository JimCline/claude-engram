using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>How one part of an instance is doing.</summary>
public enum DiagnosisState
{
    /// <summary>Working.</summary>
    Ok,

    /// <summary>Deliberately not on. A supported configuration, never a fault (D18).</summary>
    Off,

    /// <summary>Working, and something here is worth knowing.</summary>
    Warn,

    /// <summary>Asked for, and not working. The only state that fails the exit code.</summary>
    Broken,
}

/// <summary>One check, what it found, and what to type about it.</summary>
public sealed record Diagnosis(string Name, DiagnosisState State, string Detail, string? Fix = null);

public sealed record DiagnosticReport(IReadOnlyList<Diagnosis> Checks)
{
    /// <summary>
    /// True unless something is <see cref="DiagnosisState.Broken"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="DiagnosisState.Off"/> is deliberately not a failure. An instance with
    /// <c>provider = "none"</c> is fully supported (D18), and a doctor that reports a fault for a
    /// choice the user made is one people learn to ignore — which costs the real faults their
    /// audience.
    /// </remarks>
    public bool Healthy => !Checks.Any(check => check.State is DiagnosisState.Broken);

    public int Broken => Checks.Count(check => check.State is DiagnosisState.Broken);

    public int Warnings => Checks.Count(check => check.State is DiagnosisState.Warn);
}

/// <summary>
/// Reads an instance and reports what is wrong with it — the reader for every <c>Problems</c>
/// list and every <see cref="EmbedderResolution.Reason"/> the rest of the system produces.
/// </summary>
/// <remarks>
/// <para><b>It opens the store, and must never initialize it.</b>
/// <see cref="EngramDatabase.OpenInitialized(EngramHome)"/> migrates an out-of-date schema on
/// open, and D31 makes that migration snapshot first. Running it here would mean a command whose
/// entire purpose is to describe the instance had rewritten the instance before it printed a
/// word — and specifically that "your store is one schema version behind" becomes an
/// unreportable state, because asking the question performs the answer. So this uses
/// <see cref="EngramDatabase.Open(EngramHome)"/>, which configures a connection and nothing
/// else.</para>
///
/// <para><b>No check may take the report down with it.</b> Every check runs inside
/// <see cref="Try"/>, and one that throws becomes a broken row naming its own exception while the
/// rest still run. This is not defensive habit: a diagnostic is reached for when something is
/// already wrong, so the state most likely to make a check throw is exactly the state someone is
/// running it in.</para>
///
/// <para><b>The only network call is the embedding endpoint, on a deadline of its own.</b>
/// <see cref="ProbeDeadline"/> replaces the configured timeout rather than honouring it, because
/// those two numbers answer different questions: an indexing run should wait a configured 30
/// seconds for a busy endpoint, and a person asking what is broken should not. A provider that is
/// merely slow reports as unreachable here, which is the correct answer to "is it answering right
/// now" and is why the fix line names <c>engram embed --probe</c> — the command that waits.</para>
/// </remarks>
public static class Diagnostics
{
    /// <summary>How long the endpoint gets to answer before doctor calls it unreachable.</summary>
    public static TimeSpan ProbeDeadline => TimeSpan.FromSeconds(3);

    /// <summary>How long the server gets to answer its health check.</summary>
    public static TimeSpan HealthDeadline => TimeSpan.FromSeconds(2);

    /// <param name="repoRoot">
    /// Where to report indexing coverage from, or <c>null</c> to leave that check out entirely.
    /// </param>
    /// <param name="reachOut">
    /// Whether the embedding endpoint may be contacted. False in tests that have no endpoint, so
    /// they assert configuration rather than the network.
    /// </param>
    /// <param name="executablePath">
    /// This binary, which the server check compares against the running process to tell a live
    /// server from a recycled pid. Defaults to the current process, which is right for every
    /// caller except a test standing one in.
    /// </param>
    /// <param name="claudeSettingsPath">
    /// Which settings file the Claude Code check reads. It defaults to
    /// <see cref="EngramHome.ClaudeSettingsPath"/>, which is correctly outside the Engram home —
    /// Claude Code's settings live in the user profile no matter where Engram does. That makes it
    /// the one check a sandboxed home cannot sandbox, so it is overridable: a test that asserted
    /// against the real file would be asserting on whoever ran it.
    /// </param>
    public static DiagnosticReport Run(
        EngramHome home,
        Func<string, string?> environment,
        string? repoRoot = null,
        ServerLifecycle? lifecycle = null,
        bool reachOut = true,
        HttpClient? client = null,
        string? executablePath = null,
        string? claudeSettingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(environment);

        var checks = new List<Diagnosis>();
        var config = ConfigFile.Load(home.ConfigPath);
        var embedding = EmbeddingSettings.Read(config);

        Try(checks, "home", list => list.Add(CheckHome(home, config)));

        using var connection = OpenStore(home, checks);

        Try(checks, "store", list => list.Add(CheckStore(home, connection)));
        Try(checks, "server", list => list.Add(CheckServer(home, lifecycle, executablePath)));
        Try(checks, "claude code", list => list.Add(CheckClaudeCode(claudeSettingsPath ?? home.ClaudeSettingsPath)));
        Try(checks, "embedding", list => CheckEmbedding(home, embedding, environment, reachOut, client, list));
        Try(checks, "vector index", list => list.Add(CheckIndex(home, connection, embedding)));
        Try(checks, "metal", list => CheckMetal(home, embedding, list));
        Try(checks, "backups", list => list.Add(CheckBackups(home, connection, config)));
        Try(checks, "edit queue", list => list.Add(CheckQueue(home)));
        Try(checks, "code analysis", list => list.Add(CheckRoslyn(environment)));

        if (repoRoot is not null)
        {
            Try(checks, "indexing", list => list.Add(CheckRepo(repoRoot, config, connection)));
        }

        return new DiagnosticReport(checks);
    }

    /// <summary>
    /// Presence only, never a launch — running the sidecar to ask about it would need the
    /// runtime whose absence is one of the answers. Absent is a supported configuration
    /// (C# indexes at tier 0), so it reports Ok; the one Broken state is an override that
    /// points at nothing, because an explicit configuration that lies is a fault and a
    /// silent fallback would hide it (D37). Public for the same reason Corroborated is:
    /// the tier-0 branch needs a base directory no test process can supply through Run.
    /// </summary>
    public static Diagnosis CheckRoslyn(Func<string, string?> environment, string? baseDirectory = null)
    {
        if (environment(RoslynSidecar.EnvironmentOverride) is { Length: > 0 } overridePath
            && !File.Exists(overridePath))
        {
            return new Diagnosis(
                "code analysis",
                DiagnosisState.Broken,
                $"{RoslynSidecar.EnvironmentOverride} points at {overridePath}, which is not there",
                "unset it, or point it at an engram-roslyn binary");
        }

        var sidecar = RoslynSidecar.Locate(environment, baseDirectory);
        return sidecar is null
            ? new Diagnosis(
                "code analysis",
                DiagnosisState.Ok,
                "tier 0 only — engram-roslyn is not installed, so C# indexes without Roslyn")
            : new Diagnosis("code analysis", DiagnosisState.Ok, $"tier 2: {sidecar}");
    }

    private static void Try(List<Diagnosis> checks, string name, Action<List<Diagnosis>> check)
    {
        try
        {
            check(checks);
        }
#pragma warning disable CA1031 // A check that throws must still leave the other checks readable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            checks.Add(new Diagnosis(
                name,
                DiagnosisState.Broken,
                $"this check could not run: {exception.GetType().Name}: {exception.Message}"));
        }
    }

    /// <summary>
    /// Opens the store if there is one, reporting rather than throwing when there is one and it
    /// will not open.
    /// </summary>
    private static SqliteConnection? OpenStore(EngramHome home, List<Diagnosis> checks)
    {
        if (!File.Exists(home.DatabasePath))
        {
            return null;
        }

        try
        {
            return EngramDatabase.Open(home);
        }
#pragma warning disable CA1031 // The point of the check is to survive whatever a bad file throws.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            checks.Add(new Diagnosis(
                "store",
                DiagnosisState.Broken,
                $"{home.DatabasePath} will not open: {exception.Message}",
                "engram backup restore"));
            return null;
        }
    }

    private static Diagnosis CheckHome(EngramHome home, ConfigFile config)
    {
        if (!Directory.Exists(home.Root))
        {
            return new Diagnosis("home", DiagnosisState.Broken, $"{home.Root} does not exist", "engram init");
        }

        if (!File.Exists(home.ConfigPath))
        {
            return new Diagnosis(
                "home",
                DiagnosisState.Broken,
                $"{home.Root} — no config.toml, so every setting is a default",
                "engram init");
        }

        if (config.Errors.Count > 0)
        {
            return new Diagnosis(
                "home",
                DiagnosisState.Broken,
                $"{home.Root} — config.toml has {Plural(config.Errors.Count, "unreadable line")}: {config.Errors[0]}",
                "fix that line — an unreadable one is skipped, so the setting on it is silently a default");
        }

        return new Diagnosis("home", DiagnosisState.Ok, home.Root);
    }

    private static Diagnosis CheckStore(EngramHome home, SqliteConnection? connection)
    {
        if (connection is null)
        {
            return File.Exists(home.DatabasePath)
                ? new Diagnosis("store", DiagnosisState.Broken, "unreadable — see above")
                : new Diagnosis(
                    "store",
                    DiagnosisState.Warn,
                    "no engram.db yet — nothing has been remembered here",
                    "it is created by the first write; start a session, or run engram serve");
        }

        int version;
        try
        {
            version = EngramDatabase.ReadSchemaVersion(connection);
        }
        catch (SqliteException)
        {
            // The shape a WAL database copied with cp leaves behind: the file opens and holds
            // nothing, because everything real was still in the log (D31).
            return new Diagnosis(
                "store",
                DiagnosisState.Broken,
                $"{home.DatabasePath} opens but has no schema — it is not an Engram store, or it was "
                    + "copied from a live one with cp rather than snapshotted",
                "engram backup restore, or move the file aside and let Engram make a new one");
        }

        if (version > EngramDatabase.SchemaVersion)
        {
            return new Diagnosis(
                "store",
                DiagnosisState.Broken,
                $"schema {version}, and this binary knows {EngramDatabase.SchemaVersion} — it was written "
                    + "by a newer Engram than this one",
                "upgrade Engram; an older binary must not write to a newer store");
        }

        var facts = BackupFingerprint.Read(connection);
        var detail =
            $"schema {version}, {Plural(facts.Facts, "live fact")}, {facts.ClosedFacts} closed, "
            + $"{facts.Entities} entities, {Bytes(new FileInfo(home.DatabasePath).Length)}";

        return version < EngramDatabase.SchemaVersion
            ? new Diagnosis(
                "store",
                DiagnosisState.Warn,
                $"{detail} — schema {version} is behind this binary's {EngramDatabase.SchemaVersion}",
                "the next command that opens it will migrate, snapshotting first; doctor deliberately does not")
            : new Diagnosis("store", DiagnosisState.Ok, detail);
    }

    private static Diagnosis CheckServer(EngramHome home, ServerLifecycle? lifecycle, string? executablePath)
    {
        lifecycle ??= new ServerLifecycle(new ProcessInspector(), new HttpServerHealthChecker(), new ProcessServerLauncher());
        var status = lifecycle.Status(home, EngramVersion.Current, HealthDeadline);
        var asking = executablePath ?? Environment.ProcessPath ?? string.Empty;

        // Only worth a word when it is not the binary being asked — then it explains a surprising
        // row, and most often it is a working copy asking about the installed server.
        var from = status.LaunchedFrom is { Length: > 0 } launched
            && !string.Equals(launched, asking, StringComparison.Ordinal)
                ? $", started from {launched}"
                : string.Empty;

        return status.Kind switch
        {
            ServerStatusKind.Running => new Diagnosis(
                "server",
                DiagnosisState.Ok,
                $"pid {status.Health!.Pid} on port {status.Health.Port}, version {status.Health.Version}{from}"),

            // Up and answering correctly, just not this build. Warn rather than Broken: nothing is
            // wrong with it, and calling a working server broken is how a doctor stops being read.
            ServerStatusKind.VersionMismatch => new Diagnosis(
                "server",
                DiagnosisState.Warn,
                $"running version {status.Health!.Version}, but this engram is {EngramVersion.Current}{from}",
                "engram stop, then engram start"),

            // Not a fault: the hooks and the whole CLI work without it, and the plugin starts it
            // on demand. Only the MCP tools need it up.
            ServerStatusKind.NotRunning => new Diagnosis(
                "server",
                DiagnosisState.Off,
                "not running — hooks and the CLI do not need it, the MCP tools do",
                "engram start"),

            ServerStatusKind.Wedged => new Diagnosis(
                "server",
                DiagnosisState.Broken,
                $"pid {status.Recorded!.Pid} is alive and not answering its health check",
                "engram stop, then engram start"),

            ServerStatusKind.Stale => new Diagnosis(
                "server",
                DiagnosisState.Warn,
                "not running, and a pid file was left behind",
                "engram start clears it"),

            ServerStatusKind.Reused => new Diagnosis(
                "server",
                DiagnosisState.Warn,
                "not running — the recorded pid now belongs to something else",
                "engram start clears it"),

            _ => new Diagnosis("server", DiagnosisState.Warn, "in a state this check does not name"),
        };
    }

    private static Diagnosis CheckClaudeCode(string settingsPath)
    {
        var plan = ClaudePermissions.PlanGrant(settingsPath);

        if (!plan.SettingsFileExisted)
        {
            return new Diagnosis(
                "claude code",
                DiagnosisState.Warn,
                $"no settings file at {plan.SettingsPath} — Engram's tools will ask for approval every session",
                "engram permissions --apply");
        }

        return plan.ToAdd.Count == 0
            ? new Diagnosis(
                "claude code",
                DiagnosisState.Ok,
                $"{Plural(plan.AlreadyPresent.Count, "tool")} pre-approved")
            : new Diagnosis(
                "claude code",
                DiagnosisState.Warn,
                $"{plan.ToAdd.Count} of {ClaudePermissions.GrantedTools.Count} tools are not pre-approved, "
                    + "so the model is prompted before it can remember or recall",
                "engram permissions --apply");
    }

    /// <summary>
    /// Reports the embedding configuration, and for a local model its ingredients — without
    /// starting anything.
    /// </summary>
    /// <remarks>
    /// <c>provider = "local"</c> is checked by looking for the weights and the server binary
    /// rather than by resolving an embedder, because resolving one means launching llama.cpp
    /// (D35). A diagnostic that starts a model process to find out whether a model process would
    /// start has both answered the question and changed it, and leaves several hundred megabytes
    /// resident behind a command the user expected to be read-only.
    /// </remarks>
    private static void CheckEmbedding(
        EngramHome home,
        EmbeddingSettings settings,
        Func<string, string?> environment,
        bool reachOut,
        HttpClient? client,
        List<Diagnosis> checks)
    {
        foreach (var problem in settings.Problems)
        {
            checks.Add(new Diagnosis("embedding", DiagnosisState.Broken, problem, "edit [embedding] in config.toml"));
        }

        if (settings.Provider == EmbeddingProvider.None)
        {
            checks.Add(new Diagnosis(
                "embedding",
                DiagnosisState.Off,
                "provider = \"none\" — recall runs on term overlap and bm25, with no vector lane",
                "engram init --with-embeddings"));
            return;
        }

        if (settings.Provider == EmbeddingProvider.Local)
        {
            CheckLocalIngredients(home, settings, checks);
            return;
        }

        var resolution = EmbedderFactory.Create(settings, environment, client);

        // The factory hands back an owner of an HttpClient. Nothing else on this path would ever
        // dispose it, and a caller-supplied client is left alone because HttpEmbedder only owns
        // the one it made itself.
        using var owned = resolution.Embedder as IDisposable;

        if (!resolution.Resolved)
        {
            checks.Add(new Diagnosis("embedding", DiagnosisState.Broken, resolution.Reason, "engram embed --probe"));
            return;
        }

        if (!reachOut)
        {
            checks.Add(new Diagnosis("embedding", DiagnosisState.Ok, resolution.Reason));
            return;
        }

        var probe = EmbeddingProbe.Run(settings with { Timeout = ProbeDeadline }, environment);

        if (!probe.Answered)
        {
            checks.Add(new Diagnosis(
                "embedding",
                DiagnosisState.Broken,
                $"{settings.Endpoint} did not answer within {ProbeDeadline.TotalSeconds:0} s: {probe.Reason}",
                "engram embed --probe waits the configured timeout instead of this one"));
            return;
        }

        if (settings.Dimensions is { } stated && stated != probe.Dimensions)
        {
            // D34's silent failure, and the reason doctor reaches the endpoint at all: a wrong
            // width errors nowhere. It stores vectors that rank like noise.
            checks.Add(new Diagnosis(
                "embedding",
                DiagnosisState.Broken,
                $"config says dim = {stated} and {settings.Endpoint} returns {probe.Dimensions} — "
                    + "a wrong width does not error anywhere, it just stores vectors no query matches",
                "engram embed --probe --use-it, then rebuild the index"));
            return;
        }

        checks.Add(new Diagnosis(
            "embedding",
            DiagnosisState.Ok,
            $"{resolution.Embedder!.Space} — endpoint answered in "
                + $"{probe.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms"));
    }

    private static void CheckLocalIngredients(EngramHome home, EmbeddingSettings settings, List<Diagnosis> checks)
    {
        var model = EmbeddingModels.Find(settings.Model);
        if (model is null)
        {
            checks.Add(new Diagnosis(
                "embedding",
                DiagnosisState.Broken,
                $"model = \"{settings.Model}\" is not one this build knows",
                "engram model list"));
            return;
        }

        // The weights are the whole prerequisite. There is deliberately no second check for a
        // runtime: llama.cpp ships with the binary now, so "is the engine here" cannot be false
        // in a way a user could fix, and a row that is always green is a row people stop reading
        // (D45). Pooling is printed because it is the one remaining setting on this path that
        // fails silently — a wrong one embeds successfully and ranks like noise.
        var weights = Path.Combine(home.ModelsDir, model.FileName);
        checks.Add(File.Exists(weights)
            ? new Diagnosis(
                "embedding",
                DiagnosisState.Ok,
                $"local {model.Id}/{model.Dimensions}, {model.Pooling.ToString().ToLowerInvariant()} "
                    + $"pooling, weights in {home.ModelsDir}")
            : new Diagnosis(
                "embedding",
                DiagnosisState.Broken,
                $"local {model.Id}, and {weights} is not there",
                $"engram model install {model.Id}"));
    }

    private static Diagnosis CheckIndex(EngramHome home, SqliteConnection? connection, EmbeddingSettings settings)
    {
        if (settings.Provider == EmbeddingProvider.None)
        {
            return new Diagnosis("vector index", DiagnosisState.Off, "no provider, so nothing to index into");
        }

        if (connection is null)
        {
            return new Diagnosis("vector index", DiagnosisState.Warn, "no store to hold one yet");
        }

        var extension = VectorExtension.Load(connection, home.LibDir);
        if (extension is VectorExtensionState.NotInstalled)
        {
            return new Diagnosis(
                "vector index",
                DiagnosisState.Broken,
                $"embeddings are configured and sqlite-vec is not in {home.LibDir}, so no vector query can run",
                "engram init --with-embeddings fetches it");
        }

        if (extension is VectorExtensionState.Failed)
        {
            return new Diagnosis(
                "vector index",
                DiagnosisState.Broken,
                $"{VectorExtension.PathIn(home.LibDir)} is there and will not load — wrong architecture, or truncated",
                "delete it and run engram init --with-embeddings");
        }

        if (!VectorIndex.Exists(connection) || VectorIndex.ReadSpace(connection) is not { } indexed)
        {
            return new Diagnosis(
                "vector index",
                DiagnosisState.Warn,
                "sqlite-vec loads, and this store has no index yet",
                "it is created with the first embedding written");
        }

        var pending = VectorIndex.CountPending(connection);
        var rows = VectorIndex.Count(connection, liveOnly: true);

        if (settings.Model is { } configured
            && settings.Dimensions is { } width
            && indexed != new EmbeddingSpace(configured, width))
        {
            // Distances between two spaces are real numbers and mean nothing, so the lane declines
            // to run rather than ranking on them (D18). Nothing errors; recall just quietly loses
            // a lane.
            return new Diagnosis(
                "vector index",
                DiagnosisState.Broken,
                $"the index holds {indexed} and the config asks for {configured}/{width} — vectors from "
                    + "two spaces are not comparable, so recall skips the vector lane entirely",
                "rebuild the index against the configured model");
        }

        var detail = $"{indexed}, {Plural(rows, "vector")}";
        return pending == 0
            ? new Diagnosis("vector index", DiagnosisState.Ok, detail)
            : new Diagnosis("vector index", DiagnosisState.Ok, $"{detail}, {pending} facts waiting to be embedded");
    }

    /// <summary>The first Apple silicon generation with tensor cores to lose (D28).</summary>
    private const int FirstTensorGeneration = 5;

    /// <summary>
    /// Whether ggml-metal compiled the tensor path, as seen by whoever last loaded a model here.
    /// </summary>
    /// <remarks>
    /// <para>Adds no row at all off macOS-arm64, or when the provider is not local — deliberately not
    /// <see cref="DiagnosisState.Off"/>, which claims the user chose something. There is no Metal
    /// choice to make on a machine with no Metal, and the arm64 gate keeps an Intel Mac, where these
    /// lines never appear, from sitting on "not yet observed" forever.</para>
    ///
    /// <para>Reads only. Why the observation cannot be made here is <see cref="MetalRecord"/>'s whole
    /// reason to exist (D35, D37).</para>
    /// </remarks>
    private static void CheckMetal(EngramHome home, EmbeddingSettings settings, List<Diagnosis> checks)
    {
        if (settings.Provider != EmbeddingProvider.Local
            || !OperatingSystem.IsMacOS()
            || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return;
        }

        if (MetalRecord.Read(home) is not { } record)
        {
            checks.Add(new Diagnosis(
                "metal",
                DiagnosisState.Warn,
                "tensor path not yet observed",
                "it is recorded the first time a local model loads"));
            return;
        }

        var when = Observed(record.ObservedAt);

        // Reported, never enforced — two engram binaries legitimately serve one home (D42).
        var by = record.Loader is { Length: > 0 } loader && loader != Environment.ProcessPath
            ? $", recorded by {loader}"
            : string.Empty;

        if (record.HasTensor is not { } tensor)
        {
            checks.Add(new Diagnosis(
                "metal",
                DiagnosisState.Ok,
                $"this llama.cpp build reports no tensor capability{when}{by}"));
            return;
        }

        var gpu = record.Gpu ?? "this GPU";

        if (tensor)
        {
            checks.Add(new Diagnosis("metal", DiagnosisState.Ok, $"tensor path on — {gpu}{when}{by}"));
            return;
        }

        if (record.AppleGeneration is >= FirstTensorGeneration)
        {
            checks.Add(new Diagnosis(
                "metal",
                DiagnosisState.Warn,
                $"tensor path off on {gpu}{when}{by} — ggml-metal compiled the pre-tensor shaders, "
                    + "roughly half speed",
                "the executable that loaded it records an SDK older than 26; rebuild with current "
                    + "Xcode or command line tools and load again (D28)"));
            return;
        }

        // Hardware that never had tensor cores, or a device name that did not parse. Either way this
        // stays quiet: a wrong reading should cost a report, never manufacture one.
        checks.Add(new Diagnosis("metal", DiagnosisState.Ok, $"tensor path off — {gpu}{when}{by}"));
    }

    private static string Observed(string? stamp) =>
        DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, out var at)
            ? $", observed {at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : string.Empty;

    private static Diagnosis CheckBackups(EngramHome home, SqliteConnection? connection, ConfigFile config)
    {
        var settings = BackupSettings.Read(config);

        foreach (var problem in settings.Problems)
        {
            return new Diagnosis("backups", DiagnosisState.Broken, problem, "edit [backup] in config.toml");
        }

        if (!settings.Enabled)
        {
            return new Diagnosis(
                "backups",
                DiagnosisState.Off,
                "enabled = false — a migration still snapshots, nothing else does",
                "set enabled = true under [backup]");
        }

        var snapshots = BackupStore.List(home);
        var journal = Path.Combine(home.BackupDir, FactJournal.FileName);

        if (snapshots.Count == 0)
        {
            return connection is null
                ? new Diagnosis("backups", DiagnosisState.Ok, "on, and there is no store to snapshot yet")
                : new Diagnosis(
                    "backups",
                    DiagnosisState.Warn,
                    "on, and no snapshot has ever been taken",
                    "engram backup take");
        }

        var newest = snapshots[0];
        var age = DateTimeOffset.UtcNow - newest.TakenAt;
        var detail =
            $"{Plural(snapshots.Count, "snapshot")}, newest {Age(age)} old ({Bytes(newest.Bytes)}), "
            + $"every {settings.IntervalMinutes} min when facts change";

        if (settings.Journal && !File.Exists(journal))
        {
            // The .db snapshot only restores into the schema that wrote it; the journal is the
            // half that replays into any later one (D32).
            return new Diagnosis(
                "backups",
                DiagnosisState.Warn,
                $"{detail} — journal = true and {journal} is missing, so these snapshots restore only "
                    + "into schema " + newest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                "engram backup take writes it");
        }

        return new Diagnosis("backups", DiagnosisState.Ok, detail);
    }

    /// <summary>
    /// Counts what <c>file-touched</c> has spooled.
    /// </summary>
    /// <remarks>
    /// <para>Still <see cref="DiagnosisState.Off"/> rather than a warning, and still without a
    /// threshold chosen because it sounded large. The consumer is the code indexer, which is not
    /// built; until it is, a backlog is the expected state and not a fault (D37). When something
    /// does drain it the interesting question becomes whether the backlog grows faster than the
    /// reader clears it, and that is the point to pick a number with a measurement behind it.</para>
    ///
    /// <para>It counts and does not read. Reading every entry would let it say how many distinct
    /// files are behind the number, which is the more useful figure — and it is what
    /// <c>engram queue status</c> prints, precisely so doctor does not have to open a thousand
    /// files to draw one row. Past <see cref="SpoolCompactor.Threshold"/> the count means the
    /// automatic compaction has not been running, so that is where the fix appears.</para>
    /// </remarks>
    private static Diagnosis CheckQueue(EngramHome home)
    {
        if (!Directory.Exists(home.QueueDir))
        {
            return new Diagnosis("edit queue", DiagnosisState.Off, "nothing spooled");
        }

        var spooled = Directory.EnumerateFiles(home.QueueDir, "*.spool").Count();

        if (spooled == 0)
        {
            return new Diagnosis("edit queue", DiagnosisState.Off, "empty");
        }

        return new Diagnosis(
            "edit queue",
            DiagnosisState.Off,
            $"{Plural(spooled, "edit")} spooled by file-touched, waiting for an indexer to drain them",
            spooled > SpoolCompactor.Threshold ? "engram queue compact --apply" : null);
    }

    private static Diagnosis CheckRepo(string repoRoot, ConfigFile config, SqliteConnection? connection)
    {
        var scan = RepoScanner.Scan(repoRoot, IndexingSettings.Read(config));

        if (connection is null)
        {
            return new Diagnosis(
                "indexing",
                DiagnosisState.Off,
                $"{scan.Summary()} in {repoRoot}, and no store to index them into yet",
                "engram init");
        }

        // Registration is looked up the same way the indexer does it, because two answers
        // to "which repo is this" is how a report drifts from the system it describes.
        var identity = CodeIndexer.ResolveIdentity(repoRoot);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.repo_path, COUNT(f.path), MAX(f.indexed_at)
            FROM repo_registry r LEFT JOIN file_state f ON f.repo_path = r.repo_path
            WHERE r.identity = $identity
            GROUP BY r.repo_path;
            """;
        command.Parameters.AddWithValue("$identity", identity);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            // Not indexed is a choice, not a fault (D37) — indexing starts on the next
            // session start once enabled, or right now by hand.
            return new Diagnosis(
                "indexing",
                DiagnosisState.Off,
                $"{scan.Summary()} in {repoRoot}, none indexed yet",
                "engram index --apply");
        }

        var repoPath = reader.GetString(0);
        var indexed = reader.GetInt64(1);
        var newest = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        var age = newest is { } stamp
            ? $", newest {Age(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(stamp))} old"
            : string.Empty;

        return new Diagnosis(
            "indexing",
            DiagnosisState.Ok,
            $"{Plural((int)indexed, "file")} indexed into {repoPath}{age}; {scan.Summary()} on disk");
    }

    private static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalDays >= 1 ? $"{(int)age.TotalDays}d"
            : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h"
            : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m"
            : $"{(int)age.TotalSeconds}s";
    }

    private static string Bytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0} KB"
        : $"{bytes} B";

    private static string Plural(long count, string noun) =>
        count == 1
            ? $"1 {noun}"
            : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";
}
