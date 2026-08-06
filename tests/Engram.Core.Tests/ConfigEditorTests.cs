using Engram.Core;

namespace Engram.Core.Tests;

public sealed class ConfigEditorTests
{
    private const string Sample =
        """
        [embedding]
        # The vector lane is optional.
        #   none  lexical only
        provider = "none"

        # -- local --
        model = "nomic-embed-text-v1.5"

        # endpoint = "http://localhost:1234/v1"
        # dim = 768

        max_batch = 16

        [retrieval]
        seed_k = 32
        """;

    [Fact]
    public void Set_ChangesTheValueAndNothingElse()
    {
        var edited = ConfigEditor.Set(Sample, "embedding", "provider", "\"local\"");

        Assert.Equal("\"local\"", ConfigEditor.Read(edited, "embedding", "provider"));
        Assert.Equal(Sample.Split('\n').Length, edited.Split('\n').Length);
    }

    [Fact]
    public void Set_KeepsTheCommentsThatExplainTheSection()
    {
        var edited = ConfigEditor.Set(Sample, "embedding", "provider", "\"local\"");

        Assert.Contains("# The vector lane is optional.", edited, StringComparison.Ordinal);
        Assert.Contains("#   none  lexical only", edited, StringComparison.Ordinal);
        Assert.Contains("# -- local --", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_LeavesOtherSectionsUntouched()
    {
        var edited = ConfigEditor.Set(Sample, "embedding", "provider", "\"local\"");

        Assert.Equal("32", ConfigEditor.Read(edited, "retrieval", "seed_k"));
    }

    [Fact]
    public void Set_OfAKeyThatIsOnlyCommentedOut_AddsARealOne()
    {
        var edited = ConfigEditor.Set(Sample, "embedding", "dim", "1024");

        Assert.Equal("1024", ConfigEditor.Read(edited, "embedding", "dim"));
        // The suggestion the user never uncommented is still there to read.
        Assert.Contains("# dim = 768", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_OfANewKey_PutsItAfterTheLastRealSettingRatherThanAboveTheProse()
    {
        var lines = ConfigEditor.Set(Sample, "embedding", "endpoint", "\"http://x\"").Split('\n');
        var added = Array.FindIndex(lines, l => l.StartsWith("endpoint =", StringComparison.Ordinal));
        var lastExisting = Array.FindIndex(lines, l => l.StartsWith("max_batch =", StringComparison.Ordinal));

        Assert.True(added > lastExisting, "a new key should land below the settings already in the section");
        Assert.True(added < Array.IndexOf(lines, "[retrieval]"), "and above the next section");
    }

    [Fact]
    public void Set_OnAMissingSection_AddsIt()
    {
        var edited = ConfigEditor.Set("[retrieval]\nseed_k = 32\n", "embedding", "provider", "\"none\"");

        Assert.Equal("\"none\"", ConfigEditor.Read(edited, "embedding", "provider"));
        Assert.Equal("32", ConfigEditor.Read(edited, "retrieval", "seed_k"));
    }

    [Fact]
    public void Set_OnTheLastSection_DoesNotRunIntoTheNextOne()
    {
        var edited = ConfigEditor.Set(Sample, "retrieval", "seed_k", "64");

        Assert.Equal("64", ConfigEditor.Read(edited, "retrieval", "seed_k"));
        Assert.Equal("\"none\"", ConfigEditor.Read(edited, "embedding", "provider"));
    }

    [Fact]
    public void Read_OfACommentedKey_FindsNothing()
    {
        Assert.Null(ConfigEditor.Read(Sample, "embedding", "endpoint"));
    }

    [Fact]
    public void Read_DoesNotReachIntoAnotherSection()
    {
        Assert.Null(ConfigEditor.Read(Sample, "retrieval", "provider"));
    }

    [Fact]
    public void IsUntouched_WhenTheFileStillSaysWhatWeShipped_IsTrue()
    {
        Assert.True(ConfigEditor.IsUntouched(Sample, Sample, "embedding", "provider"));
    }

    [Fact]
    public void IsUntouched_WhenSomeoneChangedItByHand_IsFalse()
    {
        var theirs = Sample.Replace("provider = \"none\"", "provider = \"ollama\"", StringComparison.Ordinal);

        Assert.False(ConfigEditor.IsUntouched(theirs, Sample, "embedding", "provider"));
    }

    [Fact]
    public void IsUntouched_AfterOurOwnEarlierEdit_IsStillTrue()
    {
        // Without this the second run of any picker refuses the edit the first run made: by then
        // the file no longer matches the shipped default, and nothing else says who changed it.
        var ours = ConfigEditor.Set(Sample, "embedding", "provider", "\"ollama\"");

        Assert.True(ConfigEditor.IsUntouched(ours, Sample, "embedding", "provider"));
    }

    [Fact]
    public void AValueWeWrote_SaysSoOnItsOwnLine()
    {
        var ours = ConfigEditor.Set(Sample, "embedding", "provider", "\"local\"");

        Assert.Contains("provider = \"local\"   " + ConfigEditor.Marker, ours, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_StopsAtACommentRatherThanSwallowingIt()
    {
        var ours = ConfigEditor.Set(Sample, "embedding", "provider", "\"local\"");

        Assert.Equal("\"local\"", ConfigEditor.Read(ours, "embedding", "provider"));
    }

    [Fact]
    public void Read_KeepsAHashThatIsInsideTheValue()
    {
        // An endpoint is entitled to a fragment, and truncating one writes a config that points
        // somewhere other than where it says.
        var text = ConfigEditor.Set(Sample, "embedding", "endpoint", "\"http://host/v1#one\"");

        Assert.Equal("\"http://host/v1#one\"", ConfigEditor.Read(text, "embedding", "endpoint"));
    }

    [Fact]
    public void IsUntouched_WhenTheKeyIsOnlyCommentedOut_IsTrue()
    {
        // Leaving the shipped suggestion alone is not a decision, so writing a real value
        // takes nothing away from them.
        Assert.True(ConfigEditor.IsUntouched(Sample, Sample, "embedding", "dim"));
    }

    [Fact]
    public void TheShippedConfig_CanHaveEveryEmbeddingKeySet()
    {
        var text = DefaultConfig.Content;

        foreach (var key in new[] { "provider", "model", "endpoint", "dim", "api_key_env" })
        {
            var edited = ConfigEditor.Set(text, "embedding", key, "\"x\"");
            Assert.Equal("\"x\"", ConfigEditor.Read(edited, "embedding", key));
            Assert.Equal(
                ConfigEditor.Read(text, "retrieval", "default_budget_tokens"),
                ConfigEditor.Read(edited, "retrieval", "default_budget_tokens"));
        }
    }

    [Fact]
    public void Quote_EscapesWhatWouldOtherwiseEndTheString()
    {
        Assert.Equal("\"a\\\"b\"", ConfigEditor.Quote("a\"b"));
        Assert.Equal("\"a\\\\b\"", ConfigEditor.Quote("a\\b"));
    }

    [Fact]
    public void Backup_OfAMissingFile_DoesNothing()
    {
        Assert.Null(ConfigEditor.Backup(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Backup_TwiceInTheSameSecond_KeepsBoth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "engram-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.toml");
            File.WriteAllText(path, "first");
            var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

            var one = ConfigEditor.Backup(path, now);
            File.WriteAllText(path, "second");
            var two = ConfigEditor.Backup(path, now);

            Assert.NotEqual(one, two);
            Assert.Equal("first", File.ReadAllText(one!));
            Assert.Equal("second", File.ReadAllText(two!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
