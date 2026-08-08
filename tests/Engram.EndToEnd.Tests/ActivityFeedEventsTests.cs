using System.Text.Json;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3. Three kinds of activity happened with nothing written down: the hook that catches a
/// fact stated in passing, the hook that sees an edit, and the server coming up. Two of the three
/// had a constant declared and no emission site at all, which reads from the outside exactly like
/// a feature that is switched off.
/// </summary>
/// <remarks>
/// Through the published binary rather than the JIT build, because two of the three are hooks, and
/// a hook is a process — whether it reads stdin, and what it costs to start, are not properties an
/// in-process call can exercise honestly.
/// </remarks>
public class ActivityFeedEventsTests
{
    private static IReadOnlyList<JsonElement> Records(TestHome home, string kind)
    {
        var path = Path.Combine(home.Root, "telemetry.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(record => record.GetProperty("kind").GetString() == kind)
            .ToList();
    }

    private static string Payload(string prompt) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["session_id"] = "s1",
            ["prompt"] = prompt,
        });

    [Fact]
    public void AUserPromptThatCapturesAFact_IsRecorded()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("I went to see a Spiderman movie last Saturday"),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var record = Assert.Single(Records(home, "user-prompt"));
        Assert.Equal("s1", record.GetProperty("session_id").GetString());
    }

    [Fact]
    public void AnOrdinaryWorkingPrompt_RecordsNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, _) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("run the tests and tell me which ones fail"),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Empty(Records(home, "user-prompt"));
    }

    /// <summary>
    /// The load-bearing half: the event means a fact was written, not that the hook ran.
    /// </summary>
    /// <remarks>
    /// <para>Restating the same thing classifies exactly as it did the first time, and is then
    /// dropped because the store already holds it — so this is the one arrangement where the hook
    /// does all of its work and still writes nothing. Anything reacting to the feed would otherwise
    /// show memory being saved when nothing was.</para>
    /// <para>An ordinary working prompt does not test this, which was found by breaking it: moving
    /// the append above the "was anything stored" guard left
    /// <see cref="AnOrdinaryWorkingPrompt_RecordsNothing"/> <b>passing</b>, because such a prompt
    /// returns at the earlier "was anything worth capturing" guard and never reaches either
    /// version of the append. That test is a real assertion about a different rule, and no
    /// assertion at all about this one.</para>
    /// </remarks>
    [Fact]
    public void ARestatementThatCapturesNothingNew_RecordsNothingTheSecondTime()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var payload = Payload("I went to see a Spiderman movie last Saturday");

        var (first, _, _) = EngramProcess.RunWithStdin(home.Root, payload, "hook", "user-prompt");
        Assert.Equal(0, first);
        Assert.Single(Records(home, "user-prompt"));

        var (second, _, _) = EngramProcess.RunWithStdin(home.Root, payload, "hook", "user-prompt");
        Assert.Equal(0, second);

        Assert.Single(Records(home, "user-prompt"));
    }

    /// <summary>
    /// The path rides on the record, not only in the spool file. A feed of bare edit pings answers
    /// one bit no matter how fast it arrives, which is the same complaint that put a path into the
    /// spool entry in the first place.
    /// </summary>
    [Fact]
    public void AFileTouchedEvent_CarriesTheEditedPath()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        const string edited = "/Users/someone/project/src/Widget.cs";
        var payload =
            """{"session_id":"s1","tool_name":"Edit","tool_input":{"file_path":"""
            + "\"" + edited + "\"}}";

        var (exitCode, _, stderr) = EngramProcess.RunWithStdin(
            home.Root, payload, "hook", "file-touched");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var record = Assert.Single(Records(home, "file-touched"));
        Assert.Equal(edited, record.GetProperty("path").GetString());
    }

    /// <summary>
    /// What a burst is allowed to cost, and what it is not.
    /// </summary>
    /// <remarks>
    /// <para>The spool files must all survive — they are per-invocation and cannot collide — and no
    /// record may be half-written, because <c>FileShare.None</c> is what makes a collision cost a
    /// whole record rather than tear one. Records themselves may go missing, since this hook is
    /// forbidden to wait for a shared file, and that is a deliberate trade rather than a defect.</para>
    /// <para>No delivery rate is asserted, because the rate is a property of the machine rather
    /// than of the code. Measured over ten rounds on an idle machine: 2.0% lost at twenty editors,
    /// 1.6% at fifty. The same test inside the full suite — where another class is running its own
    /// fifty-way burst — lost 30% in one run. Both are the design working: a busy machine holds the
    /// log open longer, so more openers find it taken and drop, which is the entire point of
    /// refusing to wait. Two earlier versions of this assertion encoded a rate anyway, one exact
    /// and one at three quarters, and both failed on runs where nothing was wrong. What is asserted
    /// instead is every guarantee that does not bend under load.</para>
    /// </remarks>
    [Fact(Timeout = 300_000)]
    public async Task ABurstOfConcurrentEdits_MayDropARecordButNeverASpoolEntry()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        const int editors = 20;
        var runs = Enumerable.Range(0, editors).Select(i => Task.Run(() =>
            EngramProcess.RunWithStdin(
                home.Root,
                """{"session_id":"s1","tool_name":"Edit","tool_input":{"file_path":"/repo/File"""
                    + i + """.cs"}}""",
                "hook", "file-touched")));

        Assert.All(await Task.WhenAll(runs), r => Assert.Equal(0, r.ExitCode));

        Assert.Equal(editors, Directory.GetFiles(Path.Combine(home.Root, "queue")).Length);

        // Parses every line, not only the file-touched ones: a torn record is most likely to be
        // unreadable rather than to arrive with the wrong contents, and Records would skip it.
        var lines = File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        Assert.All(lines, line => JsonDocument.Parse(line));

        var expected = Enumerable.Range(0, editors).Select(i => (string?)$"/repo/File{i}.cs").ToHashSet();
        var paths = Records(home, "file-touched")
            .Select(record => record.GetProperty("path").GetString())
            .ToList();

        // Every record is a real edit, recorded once. A drop loses a whole record; nothing may
        // invent one, duplicate one, or land with a path no editor asked for.
        Assert.All(paths, path => Assert.Contains(path, expected));
        Assert.Equal(paths.Count, paths.Distinct().Count());
        Assert.NotEmpty(paths);
    }

    /// <summary>
    /// Uncontended, nothing is dropped at all — which is what makes the burst above a statement
    /// about contention rather than about the append being unreliable.
    /// </summary>
    [Fact]
    public void SequentialEdits_AreAllRecorded()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        const int edits = 10;
        for (var i = 0; i < edits; i++)
        {
            var (exit, _, _) = EngramProcess.RunWithStdin(
                home.Root,
                """{"session_id":"s1","tool_name":"Edit","tool_input":{"file_path":"/repo/File"""
                    + i + """.cs"}}""",
                "hook", "file-touched");
            Assert.Equal(0, exit);
        }

        Assert.Equal(
            Enumerable.Range(0, edits).Select(i => $"/repo/File{i}.cs"),
            Records(home, "file-touched").Select(record => record.GetProperty("path").GetString()));
    }

    /// <summary>
    /// The server says when it came up and when it went down.
    /// </summary>
    /// <remarks>
    /// D14 retired an earlier <c>server-start</c> record because one-per-process only counted a
    /// session under stdio, and a daemon mints many. That reasoning is about counting sessions and
    /// leaves the lifecycle itself unrecorded — which is why this asserts the two events exist and
    /// asserts nothing about them being a session count. <c>session-open</c> still owns that.
    /// </remarks>
    [Fact(Timeout = 300_000)]
    public async Task TheServer_RecordsComingUpAndGoingDown()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        try
        {
            Assert.True(
                await Settles(() => Records(home, "server-start").Count == 1),
                "the server never recorded starting");
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        Assert.True(
            await Settles(() => Records(home, "server-stop").Count == 1),
            "the server never recorded stopping");
    }

    private static async Task<bool> Settles(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return condition();
    }
}
