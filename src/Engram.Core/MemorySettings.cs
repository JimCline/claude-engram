namespace Engram.Core;

/// <summary>How the model is told to rank Engram against any other memory store available to it.</summary>
public enum MemoryPrecedence
{
    /// <summary>Say nothing. Engram competes on whatever its tool descriptions claim, and no more.</summary>
    Off,

    /// <summary>Engram is primary for reads and writes; another store may still be used.</summary>
    EngramFirst,

    /// <summary>Engram is the only durable store; nothing else is written.</summary>
    EngramOnly,
}

/// <summary>
/// The <c>[memory]</c> section: whether the primer states that Engram outranks any other memory
/// system the agent has, and how strongly.
/// </summary>
/// <remarks>
/// <para><b>Why this is a setting and the tool descriptions are not.</b> An agent can arrive
/// carrying a second memory system it was told about somewhere Engram cannot see — Claude Code's
/// file-based memory is one, and its instructions are longer, more specific, and carry a literal
/// trigger ("if the user asks you to remember something, save it immediately"). Engram had no
/// equivalent claim anywhere the top-level agent could read: the write instruction existed only in
/// the subagent primer, which reads as extending a baseline that was never stated. Faced with one
/// instruction that fires on the user's exact words and one that does not mention writing at all,
/// the agent correctly followed the first. Fixing that in <c>engram_remember</c>'s own description
/// is not a preference and ships unconditionally. Declaring somebody else's memory system
/// subordinate is a preference, because the files already in it are the user's and Engram did not
/// put them there — so that half is this key, and <see cref="MemoryPrecedence.EngramFirst"/> is the
/// default because it corrects the ranking without silently disabling a system in use.</para>
///
/// <para><b>Why the primer carries it rather than a tool description.</b> Descriptions are
/// <c>[Description]</c> attributes — compile-time constants, identical for every install, so a
/// per-user setting cannot reach them. The primer is the only channel that is both configurable and
/// durable: SessionStart matches <c>startup|resume|clear|compact</c>, so it is re-injected at every
/// point where context was reset, including after each compaction. That is weaker than the system
/// prompt, which never decays, and the honest limit of this approach — between compactions the line
/// is ordinary context and fades like any other.</para>
/// </remarks>
public sealed record MemorySettings(MemoryPrecedence Precedence, IReadOnlyList<string> Problems)
{
    public const string Section = "memory";

    public const string Key = "precedence";

    public const MemoryPrecedence DefaultPrecedence = MemoryPrecedence.EngramFirst;

    public static MemorySettings Default { get; } = new(DefaultPrecedence, []);

    /// <summary>The names accepted in the config file and on the command line, in reporting order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["off", "engram-first", "engram-only"];

    public static MemorySettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.String(Section, Key) is not { } text)
        {
            return Default;
        }

        if (!TryParse(text, out var precedence))
        {
            return new MemorySettings(
                DefaultPrecedence,
                [$"[{Section}] {Key} is '{text}', which is not one of {string.Join(", ", Names)}; using {ToText(DefaultPrecedence)}."]);
        }

        return new MemorySettings(precedence, []);
    }

    public static bool TryParse(string? text, out MemoryPrecedence precedence)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off":
                precedence = MemoryPrecedence.Off;
                return true;
            case "engram-first":
                precedence = MemoryPrecedence.EngramFirst;
                return true;
            case "engram-only":
                precedence = MemoryPrecedence.EngramOnly;
                return true;
            default:
                precedence = DefaultPrecedence;
                return false;
        }
    }

    public static string ToText(MemoryPrecedence precedence) => precedence switch
    {
        MemoryPrecedence.Off => "off",
        MemoryPrecedence.EngramFirst => "engram-first",
        MemoryPrecedence.EngramOnly => "engram-only",
        _ => "engram-first",
    };

    /// <summary>The line injected into the primer, or null when nothing is to be said.</summary>
    /// <remarks>
    /// <para>Both wordings name <c>engram_remember</c> and the trigger the competing instruction
    /// uses — the user asking to remember or save something — because a rule with no trigger loses
    /// to a rule with one regardless of which is more correct. Both also cover subagents, since a
    /// spawn inherits the parent's other memory system but not this line: SessionStart never fires
    /// for a subagent, and the subagent primer states the same ranking through its own path.</para>
    ///
    /// <para><b>Both also name the second trigger, and the asymmetry that existed without it is
    /// what D51 half-fixed.</b> The subagent primer has always said to write down what the agent
    /// itself learns; this line said only that the <i>user</i> could ask for a write. So the main
    /// session — the one that reaches most of the decisions — was told memory serves requests,
    /// while a subagent was told it serves discovery. The <c>engram_remember</c> description does
    /// name decisions and findings, but by D51's own split that channel is the unconditional one
    /// and this is the one re-injected whenever context resets (SessionStart matches
    /// <c>compact</c>), so leaving the weaker statement here put the weaker claim on the surface
    /// that survives compaction. Measured over four days: 106 <c>remember</c> calls against 0
    /// <c>digest</c> calls, which says incremental capture is the path that works and therefore
    /// the path whose trigger has to be stated. Keep both triggers — replacing the first with the
    /// second would surrender the competing-instruction property that trigger was chosen for.</para>
    /// </remarks>
    public static string? PrimerLine(MemoryPrecedence precedence) => precedence switch
    {
        MemoryPrecedence.EngramFirst =>
            "Engram is this session's durable memory store, for you and for any subagent you spawn. "
                + "Call engram_remember when the user asks you to remember or save something, and "
                + "whenever you reach a decision or finding worth keeping; search Engram before any "
                + "file-based memory directory rather than after it.",
        MemoryPrecedence.EngramOnly =>
            "Engram is this session's only durable memory store, for you and for any subagent you spawn. "
                + "Call engram_remember when the user asks you to remember or save something, and whenever "
                + "you reach a decision or finding worth keeping, rather than writing to a file-based "
                + "memory directory; read from Engram rather than one.",
        _ => null,
    };
}
