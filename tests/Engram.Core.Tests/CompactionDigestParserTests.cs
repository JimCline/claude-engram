using Engram.Core;

namespace Engram.Core.Tests;

public class CompactionDigestParserTests
{
    [Fact]
    public void ParseBlock_WellFormedSingleItem_ReturnsTheItem()
    {
        var record = """
            Some summary prose before the block.
            <engram-digest v="1">
            - a durable fact
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Equal(["a durable fact"], items);
    }

    [Fact]
    public void ParseBlock_EmptyPair_ReturnsEmptyListNotNull()
    {
        var record = """
            <engram-digest v="1">
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.NotNull(items);
        Assert.Empty(items);
    }

    [Fact]
    public void ParseBlock_NoBlockAtAll_ReturnsNull()
    {
        var items = CompactionDigestParser.ParseBlock("just an ordinary summary with no digest block");

        Assert.Null(items);
    }

    // Rule 1: the sentinel must be alone on its line. Extra trailing text on the same line means
    // this never opens a candidate block at all.
    [Fact]
    public void ParseBlock_OpenSentinelNotAloneOnItsLine_IsNotRecognised()
    {
        var record = """
            <engram-digest v="1"> and some trailing words
            - an item
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Null(items);
    }

    // The v="EXAMPLE" convention (used throughout the design doc and in the instruction's own
    // prose) must never be mistaken for a real block — rule 1 is an exact match, not a pattern
    // over the version attribute.
    [Fact]
    public void ParseBlock_ExampleVersionSentinel_YieldsNothing()
    {
        var record = """
            <engram-digest v="EXAMPLE">
            - not a real item
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Null(items);
    }

    // Sentinel-shaped text mid-sentence (exactly what a summarizer describing the mechanism in
    // prose produces) must not be mistaken for an open sentinel either.
    [Fact]
    public void ParseBlock_SentinelMentionedInsideProse_IsNotRecognised()
    {
        var record = """
            The instruction asks the model to emit a <engram-digest v="1">...</engram-digest>
            block following specific rules.
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Null(items);
    }

    // Rule 2: more than one well-formed block means the last one wins — echoes of an earlier
    // block (this document, the instruction, a previous summary) are not the one this summarizer
    // authored.
    [Fact]
    public void ParseBlock_MultipleWellFormedBlocks_TakesTheLastOne()
    {
        var record = """
            <engram-digest v="1">
            - an earlier echoed item
            </engram-digest>
            some text in between
            <engram-digest v="1">
            - the real item
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Equal(["the real item"], items);
    }

    // Rule 3: any non-blank, non-item line inside the block makes that candidate malformed —
    // the whole candidate yields nothing, not a partial read.
    [Fact]
    public void ParseBlock_ProseLineInsideBlock_MakesTheWholeBlockMalformed()
    {
        var record = """
            <engram-digest v="1">
            - a real item
            This is a heading, not an item.
            - another real item
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Null(items);
    }

    [Fact]
    public void ParseBlock_NumberedListInsideBlock_IsMalformed()
    {
        var record = """
            <engram-digest v="1">
            1. not an accepted marker
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Null(items);
    }

    // Rule 4: an open sentinel with no matching close is unterminated and never becomes a
    // well-formed candidate.
    [Fact]
    public void ParseBlock_UnterminatedBlock_YieldsNothing()
    {
        var record = """
            <engram-digest v="1">
            - an item that never gets closed
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Null(items);
    }

    [Fact]
    public void ParseBlock_BlankLinesInsideBlock_AreSkippedNotViolations()
    {
        var record = """
            <engram-digest v="1">
            - first item

            - second item
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Equal(["first item", "second item"], items);
    }

    [Fact]
    public void ParseBlock_AsteriskMarker_IsAcceptedTheSameAsHyphen()
    {
        var record = """
            <engram-digest v="1">
            * an item with an asterisk marker
            </engram-digest>
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Equal(["an item with an asterisk marker"], items);
    }

    // The block need not be at the end of the record: Claude Code's own harness appends its
    // "read the full transcript at <path>" trailer after the model's content, into the same
    // record. A parser anchored to end-of-string would find nothing on a well-formed summary.
    [Fact]
    public void ParseBlock_TrailerTextAfterTheBlock_DoesNotHideIt()
    {
        var record = """
            <engram-digest v="1">
            - a real item
            </engram-digest>
            If you need specific details from before compaction, read the full transcript at
            some/path/to/the/transcript.jsonl
            """;

        var items = CompactionDigestParser.ParseBlock(record);

        Assert.Equal(["a real item"], items);
    }

    [Fact]
    public void Parse_NoBlock_ReturnsAllZerosAndEmptyItems()
    {
        var result = CompactionDigestParser.Parse("no digest block anywhere in this text");

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Seen);
        Assert.Equal(0, result.DroppedForLength);
        Assert.Equal(0, result.DroppedAsDuplicate);
        Assert.Equal(0, result.DroppedAsPlaceholder);
    }

    [Fact]
    public void Parse_ItemOverMaxLength_IsDroppedAndCountedUnderLength()
    {
        var longItem = new string('x', CompactionDigest.MaxItemLength + 1);
        var record = $"""
            <engram-digest v="1">
            - {longItem}
            - a normal item
            </engram-digest>
            """;

        var result = CompactionDigestParser.Parse(record);

        Assert.Equal(["a normal item"], result.Items);
        Assert.Equal(2, result.Seen);
        Assert.Equal(1, result.DroppedForLength);
        Assert.Equal(0, result.DroppedAsDuplicate);
        Assert.Equal(0, result.DroppedAsPlaceholder);
    }

    [Fact]
    public void Parse_ItemAtExactlyMaxLength_IsKept()
    {
        var exactItem = new string('x', CompactionDigest.MaxItemLength);
        var record = $"""
            <engram-digest v="1">
            - {exactItem}
            </engram-digest>
            """;

        var result = CompactionDigestParser.Parse(record);

        Assert.Equal([exactItem], result.Items);
        Assert.Equal(0, result.DroppedForLength);
    }

    [Fact]
    public void Parse_DuplicateItemWithinSameBlock_IsDroppedAndCountedAsDuplicate()
    {
        var record = """
            <engram-digest v="1">
            - a repeated fact
            - a repeated fact
            </engram-digest>
            """;

        var result = CompactionDigestParser.Parse(record);

        Assert.Equal(["a repeated fact"], result.Items);
        Assert.Equal(2, result.Seen);
        Assert.Equal(1, result.DroppedAsDuplicate);
    }

    [Fact]
    public void Parse_NoMinimumLength_TerseItemIsKept()
    {
        var record = """
            <engram-digest v="1">
            - x
            </engram-digest>
            """;

        var result = CompactionDigestParser.Parse(record);

        Assert.Equal(["x"], result.Items);
    }

    // The instruction's own example items must be dropped and counted under their own name,
    // never folded into length or duplicate — found in review before any code existed: a
    // summarizer that quotes the instruction rather than complying with it makes this text the
    // last well-formed block in the record (rule 2), which would otherwise reach the store as
    // two facts.
    [Fact]
    public void Parse_InstructionsOwnExampleItems_AreDroppedAsPlaceholdersNotAsOrdinaryContent()
    {
        var record = $"""
            <engram-digest v="1">
            - {CompactionDigest.ExampleItem1}
            - {CompactionDigest.ExampleItem2}
            </engram-digest>
            """;

        var result = CompactionDigestParser.Parse(record);

        Assert.Empty(result.Items);
        Assert.Equal(2, result.Seen);
        Assert.Equal(2, result.DroppedAsPlaceholder);
        Assert.Equal(0, result.DroppedForLength);
        Assert.Equal(0, result.DroppedAsDuplicate);
    }

    // A repeated placeholder must still count fully as placeholder drops — not have its second
    // occurrence miscounted as an ordinary duplicate, which would understate the count the
    // nonce-escalation tripwire depends on.
    [Fact]
    public void Parse_RepeatedPlaceholder_CountsBothAsPlaceholderNotAsDuplicate()
    {
        var record = $"""
            <engram-digest v="1">
            - {CompactionDigest.ExampleItem1}
            - {CompactionDigest.ExampleItem1}
            </engram-digest>
            """;

        var result = CompactionDigestParser.Parse(record);

        Assert.Empty(result.Items);
        Assert.Equal(2, result.DroppedAsPlaceholder);
        Assert.Equal(0, result.DroppedAsDuplicate);
    }

    [Fact]
    public void Parse_MoreThanCapUniqueItems_KeepsOnlyTheFirstMaxItems()
    {
        var lines = Enumerable.Range(1, CompactionDigest.MaxItems + 5).Select(i => $"- item number {i}");
        var record = $"""
            <engram-digest v="1">
            {string.Join('\n', lines)}
            </engram-digest>
            """;

        var result = CompactionDigestParser.Parse(record);

        Assert.Equal(CompactionDigest.MaxItems, result.Items.Count);
        Assert.Equal(CompactionDigest.MaxItems + 5, result.Seen);
        Assert.Equal("item number 1", result.Items[0]);
        Assert.Equal($"item number {CompactionDigest.MaxItems}", result.Items[^1]);
    }

    // The round-trip test, split across the parse/filter seam per the design doc: ParseBlock
    // (no filtering) must reproduce the instruction's own example items exactly, proving the
    // emitter and parser agree on rules 1/2/3/4 and on scanning the whole record — using the
    // real Instruction string, prose and all, not just the extracted block region.
    [Fact]
    public void ParseBlock_RoundTripAgainstTheWholeInstructionString_YieldsTheTwoExampleItemsUnfiltered()
    {
        var items = CompactionDigestParser.ParseBlock(CompactionDigest.Instruction);

        Assert.Equal(CompactionDigest.ExampleItems, items);
    }

    // The other half of the same round trip: once the placeholder filter runs, those same two
    // items must be dropped rather than kept — proving the filter actually catches the exact
    // strings the emitter would leak if a summarizer echoed the instruction. Asserting this on
    // Parse (not ParseBlock) is deliberate: it is the only test that would fail if the filter
    // and the instruction's example text ever drifted apart.
    [Fact]
    public void Parse_RoundTripAgainstTheWholeInstructionString_DropsBothExampleItemsAsPlaceholders()
    {
        var result = CompactionDigestParser.Parse(CompactionDigest.Instruction);

        Assert.Empty(result.Items);
        Assert.Equal(2, result.Seen);
        Assert.Equal(2, result.DroppedAsPlaceholder);
    }
}
