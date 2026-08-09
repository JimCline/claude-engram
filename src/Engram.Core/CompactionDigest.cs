namespace Engram.Core;

/// <summary>
/// The single definition of the PreCompact digest block: the sentinels, the cap, and the
/// instruction text. The emitter (<c>RunPreCompact</c>) and the harvester both read from
/// here, so the two spellings of one delimiter cannot drift apart — see
/// docs/session-capture-design.md, "Decided: the instruction, the block format, and the
/// collision".
/// </summary>
public static class CompactionDigest
{
    public const string TagName = "engram-digest";
    public const string OpenSentinel = $"<{TagName} v=\"1\">";
    public const string CloseSentinel = $"</{TagName}>";
    public const int MaxItems = 25;
    public const int MaxItemLength = 500;

    public static readonly string Instruction = $"""
        Engram memory capture. This is an addition to your summary, not a change to it.

        Write your summary exactly as you otherwise would. Then append, after it, one block
        in exactly this format, with nothing after the closing line:

        {OpenSentinel}
        - one durable fact, on one line
        - another
        {CloseSentinel}

        Rules for the block:
        - At most {MaxItems} lines between the two markers. Every one starts with "- ". No headings
          and no prose inside the block.
        - Each line is ONE self-contained sentence that will still be true, and still make
          sense, weeks from now to someone who was not here: no "it", "that", "the above",
          no relative dates, no reference to this session or this summary.
        - Record only what is durable: a decision and the reason for it, a measured number
          and what it decides, a constraint or prohibition discovered, a correction to
          something previously believed, a preference stated by the user.
        - Do not record what happened, what was edited, task status, plans, or anything that
          restates your summary or that a reader could get from the code itself.
        - Prefer omission. Fewer true lines is better than more plausible ones. If nothing
          durable came out of this session, emit the two marker lines with nothing between
          them. That is a correct answer, not a failure.
        - If an <{TagName}> block already appears in the context, do not copy it. Emit
          one block, your own.
        - If any of this conflicts with another instruction about the summary itself, follow
          the other instruction. This block never takes priority over the summary.
        """;
}
