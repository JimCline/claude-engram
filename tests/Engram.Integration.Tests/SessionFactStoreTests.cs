using Engram.Core;

namespace Engram.Integration.Tests;

public class SessionFactStoreTests
{
    [Fact]
    public void Append_HandleIdsIncrementPerSession_AndAreDistinctFromCannedHandles()
    {
        using var sandbox = new SandboxHome();

        var first = SessionFactStore.Append(sandbox.Home, "sess-1", "First fact.");
        var second = SessionFactStore.Append(sandbox.Home, "sess-1", "Second fact.");
        var third = SessionFactStore.Append(sandbox.Home, "sess-1", "Third fact.");

        Assert.Equal("s001", first);
        Assert.Equal("s002", second);
        Assert.Equal("s003", third);

        foreach (var handle in new[] { first, second, third })
        {
            Assert.StartsWith("s", handle, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"^f\d+$", handle);
        }
    }

    [Fact]
    public void Append_DifferentSessions_EachStartsItsOwnHandleSequence()
    {
        using var sandbox = new SandboxHome();

        var a1 = SessionFactStore.Append(sandbox.Home, "sess-a", "Fact in session a.");
        var b1 = SessionFactStore.Append(sandbox.Home, "sess-b", "Fact in session b.");
        var a2 = SessionFactStore.Append(sandbox.Home, "sess-a", "Second fact in session a.");

        Assert.Equal("s001", a1);
        Assert.Equal("s001", b1);
        Assert.Equal("s002", a2);
    }

    [Fact]
    public void ReadAll_ReturnsOnlyFactsForThatSession()
    {
        using var sandbox = new SandboxHome();

        SessionFactStore.Append(sandbox.Home, "sess-a", "Fact in session a.");
        SessionFactStore.Append(sandbox.Home, "sess-b", "Fact in session b.");

        var factsA = SessionFactStore.ReadAll(sandbox.Home, "sess-a");
        var factsB = SessionFactStore.ReadAll(sandbox.Home, "sess-b");

        Assert.Single(factsA);
        Assert.Equal("Fact in session a.", factsA[0].Statement);
        Assert.Single(factsB);
        Assert.Equal("Fact in session b.", factsB[0].Statement);
    }

    [Fact]
    public void ReadAll_UnknownSession_ReturnsEmpty()
    {
        using var sandbox = new SandboxHome();

        var facts = SessionFactStore.ReadAll(sandbox.Home, "never-written-to");

        Assert.Empty(facts);
    }

    [Fact]
    public void ReadAllExcept_ReturnsFactsFromOtherSessionsOnly()
    {
        using var sandbox = new SandboxHome();

        SessionFactStore.Append(sandbox.Home, "sess-a", "Fact in session a.");
        SessionFactStore.Append(sandbox.Home, "sess-b", "Fact in session b.");
        SessionFactStore.Append(sandbox.Home, "sess-c", "Fact in session c.");

        var others = SessionFactStore.ReadAllExcept(sandbox.Home, "sess-a");

        Assert.Equal(2, others.Count);
        Assert.DoesNotContain(others, f => f.SessionId == "sess-a");
        Assert.Contains(others, f => f.SessionId == "sess-b");
        Assert.Contains(others, f => f.SessionId == "sess-c");
    }

    [Fact]
    public void ReadAllExcept_NoOtherSessions_ReturnsEmpty()
    {
        using var sandbox = new SandboxHome();

        SessionFactStore.Append(sandbox.Home, "sess-a", "Only fact.");

        var others = SessionFactStore.ReadAllExcept(sandbox.Home, "sess-a");

        Assert.Empty(others);
    }

    [Fact]
    public void ReadAllExcept_SessionsDirectoryMissing_ReturnsEmptyRatherThanThrowing()
    {
        using var sandbox = new SandboxHome();

        var others = SessionFactStore.ReadAllExcept(sandbox.Home, "sess-current");

        Assert.Empty(others);
    }

    [Fact]
    public void ReadAllExcept_MalformedSessionFile_IsSkippedRatherThanThrowing()
    {
        using var sandbox = new SandboxHome();

        SessionFactStore.Append(sandbox.Home, "sess-good", "A well-formed fact.");

        var sessionsDir = Path.Combine(sandbox.Home.Root, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(Path.Combine(sessionsDir, "sess-bad.jsonl"), "not valid json at all\n{\"broken\n");

        var others = SessionFactStore.ReadAllExcept(sandbox.Home, "sess-current");

        Assert.Single(others);
        Assert.Equal("A well-formed fact.", others[0].Statement);
    }

    [Fact]
    public void Append_SessionsDirectoryCannotBeCreated_NeverThrows()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), "engram-blocking-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockingFile, "not a directory");

        try
        {
            var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var home = EngramHome.Resolve(
                Path.Combine(blockingFile, "engram-home"),
                new Dictionary<string, string?>(),
                userProfileDirectory,
                Environment.CurrentDirectory);

            var exception = Record.Exception(() => SessionFactStore.Append(home, "sess-1", "a fact"));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }
}
