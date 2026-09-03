using System.Globalization;
using System.Text;

namespace Engram.Core;

/// <summary>
/// What the hook-safe file stamp knows about one checkout: the recorded enrollment decision, when
/// it was made, and when the repo was last fully indexed. <see cref="State"/> is null for a repo
/// whose decision was reset (or never recorded under this root).
/// </summary>
public sealed record RepoIndexStampRow(
    string Identity,
    RepoEnrollmentState? State,
    long? DecidedAt,
    long? LastIndexedAt);

/// <summary>
/// A plain-file mirror of the two <c>repo_enrollment</c> facts a PreToolUse hook needs — is this
/// checkout enrolled, and has it been indexed at least once — for the one caller that may not open
/// the database to ask (D4, D66: <c>lookup-nudge</c> is in <c>file-touched</c>'s frequency class).
/// Written from exactly two places, <c>RepoCommand.ApplyDecision</c> and the indexer's full-scan
/// stamp, so it cannot disagree with the table by having a third author.
/// </summary>
/// <remarks>
/// Append-only, one event per line, folded on read with the last event per root winning — the
/// same shape as <see cref="SessionNudgeState"/>, and for the same reason: a rewrite-style file
/// can lose a line to a concurrent append, and an append cannot. Each line carries a timestamp
/// rather than a flag because the gate that reads it will one day need "how stale" and not just
/// "whether" (D72's freshness work); a boolean would give that threshold nowhere to live.
/// Keyed on the canonical checkout root, since the hook has a cwd and may not shell out for the
/// git identity — a checkout that moves on disk therefore reads as never-decided, which fails
/// toward silence.
/// </remarks>
public static class RepoIndexStamp
{
    public const string Indexed = "indexed";

    private static readonly TimeSpan AppendRetryBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Records one event for a checkout: an enrollment decision (<c>enroll</c>, <c>decline</c>,
    /// <c>later</c>, <c>reset</c> — the verbs <c>ApplyDecision</c> already takes) or
    /// <see cref="Indexed"/>. Best-effort: the table is the authority and a lost line costs one
    /// silent nudge, never a wrong one.
    /// </summary>
    public static void Append(string path, DateTimeOffset now, string root, string identity, string eventName)
    {
        var line = string.Join('\t',
            now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            eventName,
            identity,
            PathCanonicalizer.Canonical(root)) + "\n";
        DurableAppend.TryAppend(path, Encoding.UTF8.GetBytes(line), AppendRetryBudget);
    }

    /// <summary>
    /// Folds the file for one checkout root. Null when no event was ever recorded for it. Any read
    /// failure also answers null: the caller is a hook that must fail toward silence.
    /// </summary>
    public static RepoIndexStampRow? Read(string path, string root)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var canonicalRoot = PathCanonicalizer.Canonical(root);
        RepoIndexStampRow? row = null;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length != 4
                    || !string.Equals(parts[3], canonicalRoot, StringComparison.Ordinal)
                    || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var at))
                {
                    continue;
                }

                var identity = parts[2];
                row = parts[1] switch
                {
                    "enroll" => new RepoIndexStampRow(identity, RepoEnrollmentState.Enrolled, at, row?.LastIndexedAt),
                    "decline" => new RepoIndexStampRow(identity, RepoEnrollmentState.Declined, at, row?.LastIndexedAt),
                    "later" => new RepoIndexStampRow(identity, RepoEnrollmentState.Deferred, at, row?.LastIndexedAt),
                    // Reset deletes the repo_enrollment row, last_full_scan_at included.
                    "reset" => new RepoIndexStampRow(identity, null, null, null),
                    Indexed => new RepoIndexStampRow(identity, row?.State, row?.DecidedAt, at),
                    _ => row,
                };
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return row;
    }
}
