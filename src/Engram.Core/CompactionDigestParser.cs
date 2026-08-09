namespace Engram.Core;

/// <summary>
/// The result of parsing one compaction-summary record for a digest block. <see cref="Seen"/> and
/// <see cref="Items"/>.Count can disagree — by design, so a caller can tell "the summarizer wrote
/// fewer than 25" from "something was dropped." The three drop counts are separate rather than one
/// generic counter, because each means something different to whoever reads it later: length means
/// a pasted paragraph, duplicate means an echoed block, placeholder means the instruction's own
/// example leaked through (see docs/session-capture-design.md, "found in review before any code
/// existed").
/// </summary>
public sealed record ParsedDigest(
    IReadOnlyList<string> Items,
    int Seen,
    int DroppedForLength,
    int DroppedAsDuplicate,
    int DroppedAsPlaceholder);

/// <summary>
/// Reads a <c>&lt;engram-digest v="1"&gt;</c> block out of a compaction-summary record. Parsing and
/// filtering are deliberately separate methods, not stages inside one loop: <see cref="ParseBlock"/>
/// answers only "is a well-formed block present, and what are its raw item lines" per the four
/// block-level rules, while <see cref="Parse"/> applies the item-level filters and the cap on top.
/// A round-trip test against <see cref="CompactionDigest.Instruction"/>'s own example block needs
/// both halves separately — asserted on <see cref="ParseBlock"/> before the placeholder filter
/// exists, on <see cref="Parse"/> after — or the assertion silently degrades to checking an empty
/// result once that filter exists. See docs/session-capture-design.md, "Parse rules — strict where
/// it matters, tolerant where it does not."
/// </summary>
public static class CompactionDigestParser
{
    /// <summary>
    /// Scans the whole record — the block is not required to be at the end, since Claude Code's own
    /// harness appends text after the model's final section into the same record. Any non-blank
    /// line inside a candidate block that is not a valid item line makes that candidate malformed;
    /// an unterminated open sentinel is likewise malformed. Among the candidates that are
    /// well-formed, the last one wins (earlier ones are echoes — of this document, of the
    /// instruction, or of a previous summary sitting at the head of context). Returns
    /// <see langword="null"/> when no well-formed block exists anywhere in the record — distinct
    /// from an empty list, which means a well-formed block was found and it was the empty pair.
    /// </summary>
    public static IReadOnlyList<string>? ParseBlock(string record)
    {
        var lines = record.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        List<string>? lastWellFormed = null;
        List<string>? current = null;
        var malformed = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (current is null)
            {
                if (trimmed == CompactionDigest.OpenSentinel)
                {
                    current = [];
                    malformed = false;
                }

                continue;
            }

            if (trimmed == CompactionDigest.CloseSentinel)
            {
                if (!malformed)
                {
                    lastWellFormed = current;
                }

                current = null;
                continue;
            }

            if (trimmed == CompactionDigest.OpenSentinel)
            {
                // The previous candidate never closed — unterminated, so it was never a
                // well-formed candidate at all. Start fresh rather than nest.
                current = [];
                malformed = false;
                continue;
            }

            if (trimmed.Length == 0)
            {
                // Blank lines are skipped: whitespace is not content, and not a violation either.
                continue;
            }

            if (TryReadItem(trimmed, out var item))
            {
                current.Add(item);
            }
            else
            {
                malformed = true;
            }
        }

        // A candidate still open at end-of-record is unterminated and was never merged in.
        return lastWellFormed;
    }

    /// <summary>
    /// Full read: <see cref="ParseBlock"/>, then the item-level filters, then the cap. Filters run
    /// in this order — placeholder, length, duplicate — because placeholder is the most specific
    /// diagnosis and must be counted even when a placeholder item repeats; checking it first means
    /// a repeated placeholder is never miscounted as an ordinary duplicate, which would break the
    /// nonce-escalation tripwire that depends on this count. The cap applies last, to the kept
    /// items only, per docs/session-capture-design.md's "harvester takes the first 25 after
    /// filtering."
    /// </summary>
    public static ParsedDigest Parse(string record)
    {
        var raw = ParseBlock(record);
        if (raw is null)
        {
            return new ParsedDigest([], Seen: 0, DroppedForLength: 0, DroppedAsDuplicate: 0, DroppedAsPlaceholder: 0);
        }

        var droppedForLength = 0;
        var droppedAsDuplicate = 0;
        var droppedAsPlaceholder = 0;
        var kept = new List<string>();
        var takenInThisBlock = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in raw)
        {
            if (item == CompactionDigest.ExampleItem1 || item == CompactionDigest.ExampleItem2)
            {
                droppedAsPlaceholder++;
                continue;
            }

            if (item.Length > CompactionDigest.MaxItemLength)
            {
                droppedForLength++;
                continue;
            }

            if (!takenInThisBlock.Add(item))
            {
                droppedAsDuplicate++;
                continue;
            }

            kept.Add(item);
        }

        if (kept.Count > CompactionDigest.MaxItems)
        {
            kept.RemoveRange(CompactionDigest.MaxItems, kept.Count - CompactionDigest.MaxItems);
        }

        return new ParsedDigest(kept, raw.Count, droppedForLength, droppedAsDuplicate, droppedAsPlaceholder);
    }

    private static bool TryReadItem(string trimmedLine, out string item)
    {
        if (trimmedLine.Length > 2 &&
            (trimmedLine.StartsWith("- ", StringComparison.Ordinal) || trimmedLine.StartsWith("* ", StringComparison.Ordinal)))
        {
            var text = trimmedLine[2..].Trim();
            if (text.Length > 0)
            {
                item = text;
                return true;
            }
        }

        item = "";
        return false;
    }
}
