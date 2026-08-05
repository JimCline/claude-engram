using Engram.Core;

namespace Engram.Integration.Tests;

public class UserFactStoreTests
{
    [Fact]
    public void AppendedFactsComeBackOldestFirst()
    {
        using var sandbox = new SandboxHome();

        UserFactStore.Append(sandbox.Home, "personal", "I grew up in Fort Collins");
        UserFactStore.Append(sandbox.Home, "directive", "always use BEGIN IMMEDIATE");

        var facts = UserFactStore.ReadActive(sandbox.Home);

        Assert.Equal(2, facts.Count);
        Assert.Equal("I grew up in Fort Collins", facts[0].Statement);
        Assert.Equal("always use BEGIN IMMEDIATE", facts[1].Statement);
    }

    [Fact]
    public void ReadingAnEmptyOrAbsentDirectoryIsNotAnError()
    {
        using var sandbox = new SandboxHome();

        Assert.Empty(UserFactStore.ReadActive(sandbox.Home));
        Assert.Empty(UserFactStore.ReadAll(sandbox.Home));
    }

    // The delete key. Capturing what someone says about their life without a way to take
    // it back is not something this should ever do.
    [Fact]
    public void ARetractedFactStopsBeingActive()
    {
        using var sandbox = new SandboxHome();
        var id = UserFactStore.Append(sandbox.Home, "personal", "I saw a film last Saturday");

        UserFactStore.Append(sandbox.Home, "retraction", $"retracted {id}", retracts: id);

        Assert.Empty(UserFactStore.ReadActive(sandbox.Home));
    }

    // Facts are closed, never erased (CLAUDE.md: append-only). The retraction itself is
    // part of the record — that someone took a fact back is history too.
    [Fact]
    public void RetractionClosesTheFactWithoutDeletingAnything()
    {
        using var sandbox = new SandboxHome();
        var id = UserFactStore.Append(sandbox.Home, "personal", "I saw a film last Saturday");
        UserFactStore.Append(sandbox.Home, "retraction", $"retracted {id}", retracts: id);

        var all = UserFactStore.ReadAll(sandbox.Home);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Id == id && r.Statement == "I saw a film last Saturday");
        Assert.Contains(all, r => r.Retracts == id);
    }

    // The model rewriting "last Saturday" into a date must replace the raw capture, not
    // sit beside it. Two near-identical facts is how a memory store starts lying about
    // how much it knows.
    [Fact]
    public void ASupersedingFactReplacesTheCaptureRatherThanDuplicatingIt()
    {
        using var sandbox = new SandboxHome();
        var captureId = UserFactStore.Append(sandbox.Home, "personal", "I saw a movie last Saturday");

        UserFactStore.Append(
            sandbox.Home, "personal", "Saw a Spider-Man film on 2026-08-01", supersedes: captureId);

        var active = UserFactStore.ReadActive(sandbox.Home);

        var fact = Assert.Single(active);
        Assert.Equal("Saw a Spider-Man film on 2026-08-01", fact.Statement);
    }

    [Fact]
    public void AHalfWrittenFileDoesNotTakeTheRestOfMemoryDownWithIt()
    {
        using var sandbox = new SandboxHome();
        UserFactStore.Append(sandbox.Home, "personal", "I use a Dvorak keyboard");

        File.WriteAllText(
            Path.Combine(sandbox.Home.UserFactsDir, "999-0-utruncated.json"), "{\"id\": \"utrunc");

        var fact = Assert.Single(UserFactStore.ReadActive(sandbox.Home));
        Assert.Equal("I use a Dvorak keyboard", fact.Statement);
    }

    [Fact]
    public void StandingFactsSurfaceToRecallAsLongTermFacts()
    {
        using var sandbox = new SandboxHome();
        UserFactStore.Append(sandbox.Home, "personal", "I went to see a Spiderman movie last Saturday");
        UserFactStore.Append(sandbox.Home, "directive", "always use BEGIN IMMEDIATE");

        var facts = UserFactStore.ToFacts(sandbox.Home, DateTime.UtcNow);

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal("user", f.Scope));
        Assert.Contains(facts, f => f.Predicate == "stated" && f.Body.Contains("Spiderman"));
        Assert.Contains(facts, f => f.Predicate == "requires" && f.Body.Contains("BEGIN IMMEDIATE"));
    }

    [Fact]
    public void RetractedFactsNeverReachRecall()
    {
        using var sandbox = new SandboxHome();
        var id = UserFactStore.Append(sandbox.Home, "personal", "I went to see a Spiderman movie");
        UserFactStore.Append(sandbox.Home, "retraction", $"retracted {id}", retracts: id);

        Assert.Empty(UserFactStore.ToFacts(sandbox.Home, DateTime.UtcNow));
    }
}
