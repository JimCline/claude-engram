using System.Globalization;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>What a conversion produced, and what it could not account for.</summary>
public sealed record LegacyImport(
    IReadOnlyList<JournalFact> Facts,
    int Statements,
    int Retractions,
    int Superseded,
    int DanglingLinks,
    int Skipped);

/// <summary>
/// Reads the JSON directory user facts used to live in, and rewrites them as journal facts.
/// </summary>
/// <remarks>
/// <para>The old store kept one file per statement with its own <c>supersedes</c> and
/// <c>retracts</c> pointers — a second implementation of the validity window <c>fact</c> already
/// had (see <see cref="UserFacts"/>). Nothing in the current code reads that directory, so an
/// instance upgraded across the change keeps its files and loses its memory. This converts them
/// rather than reimplementing the reader, and hands the result to
/// <see cref="FactJournal.Replay"/>, which is already additive, idempotent, and tested.</para>
///
/// <para><b>Addresses come from <see cref="UserFacts"/>, not from here.</b> A migrated statement
/// has to land where a natively captured one would, or the same sentence typed again tomorrow
/// would create a second live fact beside its own history. That means the path leaf is
/// <see cref="FactStore.Fingerprint"/> of the text, which is also why this is C# rather than a
/// throwaway script: reimplementing that hash elsewhere would agree until it did not.</para>
///
/// <para><b>A chain shares one address.</b> The old model linked a restatement to its predecessor
/// by id, so the two texts differ and would fingerprint to different entities — writing both
/// would leave two live facts where the user meant one belief that changed. So every member of a
/// chain is addressed at its root's path and predicate, which is exactly what
/// <see cref="UserFacts.Restate"/> does for a live one, and the supersession is expressed the way
/// the store expresses it rather than by a pointer only this file understands.</para>
/// </remarks>
public static class LegacyUserFacts
{
    public const string DirectoryName = "user-facts";

    /// <summary>The old kind that meant an instruction rather than something about the user.</summary>
    public const string DirectiveKind = "directive";

    /// <summary>The old kind that was not a statement at all, but an operation closing one.</summary>
    public const string RetractionKind = "retraction";

    public static string DirectoryIn(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        return Path.Combine(home.Root, DirectoryName);
    }

    /// <summary>Reads every <c>*.json</c> in a directory. Missing directory is an empty read.</summary>
    public static IReadOnlyList<string> ReadDocuments(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = Directory.GetFiles(directory, "*.json");
        Array.Sort(files, StringComparer.Ordinal);

        var documents = new List<string>(files.Length);
        foreach (var file in files)
        {
            try
            {
                documents.Add(File.ReadAllText(file));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return documents;
    }

    public static LegacyImport Convert(IEnumerable<string> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var entries = new List<Entry>();
        var skipped = 0;

        foreach (var document in documents)
        {
            if (Parse(document) is { } entry)
            {
                entries.Add(entry);
            }
            else
            {
                skipped++;
            }
        }

        // Chronological, and by id where two share a timestamp, so a conversion of the same
        // directory twice produces byte-identical journal ids.
        entries.Sort((left, right) => left.At != right.At
            ? left.At.CompareTo(right.At)
            : string.CompareOrdinal(left.Id, right.Id));

        var byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byId[entry.Id] = entry;
        }

        var statements = entries.Where(entry => !entry.IsRetraction).ToList();
        var retractions = entries.Where(entry => entry.IsRetraction).ToList();

        // Who closes whom, and when. A statement can be closed by the one that supersedes it or
        // by a retraction naming it; if the old store somehow recorded both, the earlier event is
        // the one that actually ended the belief.
        var closers = new Dictionary<string, Closer>(StringComparer.Ordinal);
        var dangling = 0;

        foreach (var entry in statements)
        {
            if (entry.Supersedes is not { Length: > 0 } target)
            {
                continue;
            }

            if (!byId.ContainsKey(target))
            {
                dangling++;
                continue;
            }

            Offer(closers, target, new Closer(entry.At, entry.Id, "the user restated this"));
        }

        foreach (var retraction in retractions)
        {
            if (retraction.Retracts is not { Length: > 0 } target)
            {
                continue;
            }

            if (!byId.ContainsKey(target))
            {
                dangling++;
                continue;
            }

            Offer(closers, target, new Closer(retraction.At, Successor: null, "the user retracted this"));
        }

        var journalIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < statements.Count; i++)
        {
            journalIds[statements[i].Id] = i + 1;
        }

        var facts = new List<JournalFact>(statements.Count);
        var superseded = 0;

        foreach (var entry in statements)
        {
            var root = RootOf(entry, byId);
            var topic = TopicOf(root);
            var path = UserFacts.PathFor(topic, root.Statement);
            var predicate = UserFacts.PredicateFor(topic);

            long? validTo = null;
            long? supersededBy = null;
            string? reason = null;

            if (closers.TryGetValue(entry.Id, out var closer))
            {
                validTo = closer.At;
                reason = closer.Reason;

                if (closer.Successor is { } successor && journalIds.TryGetValue(successor, out var successorId))
                {
                    supersededBy = successorId;
                    superseded++;
                }
            }

            facts.Add(new JournalFact(
                Id: journalIds[entry.Id],
                Subject: path,
                SubjectKind: UserFacts.StatementKind,
                Predicate: predicate,
                Body: entry.Statement,
                Object: null,
                ObjectKind: null,
                Scope: UserFacts.Scope,
                LearnedVia: UserFacts.LearnedVia,
                Regenerable: false,
                Evidence: "stated by the user",
                ValidFrom: entry.At,
                ValidTo: validTo,
                SupersededBy: supersededBy,
                SupersessionReason: reason,
                CreatedAt: entry.At));
        }

        return new LegacyImport(facts, statements.Count, retractions.Count, superseded, dangling, skipped);
    }

    private static void Offer(Dictionary<string, Closer> closers, string target, Closer candidate)
    {
        if (!closers.TryGetValue(target, out var held) || candidate.At < held.At)
        {
            closers[target] = candidate;
        }
    }

    /// <summary>
    /// Walks <c>supersedes</c> back to the statement that opened the chain.
    /// </summary>
    /// <remarks>
    /// Bounded by the entry count rather than trusting the data to be acyclic: these pointers
    /// were written by a store with no foreign keys, and a cycle here would otherwise hang the
    /// migration rather than fail it.
    /// </remarks>
    private static Entry RootOf(Entry entry, Dictionary<string, Entry> byId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { entry.Id };
        var current = entry;

        while (current.Supersedes is { Length: > 0 } parentId
            && byId.TryGetValue(parentId, out var parent)
            && !parent.IsRetraction
            && seen.Add(parentId))
        {
            current = parent;
        }

        return current;
    }

    private static UserFactTopic TopicOf(Entry entry) =>
        string.Equals(entry.Kind, DirectiveKind, StringComparison.OrdinalIgnoreCase)
            ? UserFactTopic.Instruction
            : UserFactTopic.AboutYou;

    private static Entry? Parse(string document)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(document);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (node is not JsonObject record
            || Text(record, "id") is not { Length: > 0 } id
            || Text(record, "timestamp") is not { Length: > 0 } timestamp
            || !DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var at))
        {
            return null;
        }

        var kind = Text(record, "kind") ?? string.Empty;
        var isRetraction = string.Equals(kind, RetractionKind, StringComparison.OrdinalIgnoreCase);
        var statement = Text(record, "statement");

        // A statement is the whole point of a non-retraction entry; a retraction is allowed to
        // have none, because what it carries is the pointer.
        if (!isRetraction && statement is not { Length: > 0 })
        {
            return null;
        }

        return new Entry(
            id,
            at.ToUnixTimeSeconds(),
            kind,
            statement ?? string.Empty,
            Text(record, "supersedes"),
            Text(record, "retracts"),
            isRetraction);
    }

    private static string? Text(JsonObject record, string key) =>
        record.TryGetPropertyValue(key, out var value) ? value?.GetValue<string>() : null;

    private readonly record struct Closer(long At, string? Successor, string Reason);

    private sealed record Entry(
        string Id,
        long At,
        string Kind,
        string Statement,
        string? Supersedes,
        string? Retracts,
        bool IsRetraction);
}
