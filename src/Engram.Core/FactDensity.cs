using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>How many facts a session actually produces (D16's gate).</summary>
public sealed record FactsPerSessionStat(
    [property: JsonPropertyName("sessions")] int Sessions,
    [property: JsonPropertyName("facts")] int Facts,
    [property: JsonPropertyName("median")] double Median,
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max")] int Max,
    [property: JsonPropertyName("gate")] int Gate,
    [property: JsonPropertyName("meets_gate")] bool MeetsGate,
    [property: JsonPropertyName("note")] string Note);

/// <summary>
/// The measurement D16 gates itself on: a timeline view over session neighbours is context
/// only if a session produces enough facts to have neighbours.
/// </summary>
/// <remarks>
/// This reads the store rather than telemetry because telemetry counts tool <em>calls</em>,
/// and a call is not a fact — a repeat records nothing, and a restatement replaces rather
/// than adds. The question is what memory ended up holding.
/// </remarks>
public static class FactDensity
{
    /// <summary>
    /// D16's threshold: below this median the decision lapses. "Roughly five" in the
    /// decision, five here — a gate that moves when it is close to being missed is not one.
    /// </summary>
    public const int Gate = 5;

    private const string CountingNote =
        "Counts distinct subject+predicate per session, so a statement restated in the same " +
        "session counts once — the timeline would show one row for it either way. Sessions " +
        "that wrote nothing are absent by construction: a session row exists only once that " +
        "session has written, so this median is over sessions that produced at least one fact " +
        "and reads high against 'all sessions'.";

    public static FactsPerSessionStat Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // char(10) rather than a plain separator: subject_id is an integer and a predicate is
        // a verb phrase, so a newline cannot appear in either and cannot forge a pair boundary.
        command.CommandText =
            """
            SELECT COUNT(DISTINCT subject_id || char(10) || predicate)
              FROM fact
             WHERE session_id IS NOT NULL
             GROUP BY session_id;
            """;

        var counts = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts.Add(reader.GetInt32(0));
        }

        return Summarize(counts);
    }

    public static FactsPerSessionStat Summarize(IReadOnlyList<int> factsPerSession)
    {
        if (factsPerSession.Count == 0)
        {
            return new FactsPerSessionStat(
                Sessions: 0, Facts: 0, Median: 0, Min: 0, Max: 0, Gate: Gate, MeetsGate: false, Note: CountingNote);
        }

        var sorted = factsPerSession.Order().ToList();
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;

        return new FactsPerSessionStat(
            Sessions: sorted.Count,
            Facts: sorted.Sum(),
            Median: Math.Round(median, 1, MidpointRounding.AwayFromZero),
            Min: sorted[0],
            Max: sorted[^1],
            Gate: Gate,
            MeetsGate: median >= Gate,
            Note: CountingNote);
    }
}
