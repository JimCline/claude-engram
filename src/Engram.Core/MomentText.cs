using System.Globalization;

namespace Engram.Core;

/// <summary>
/// How an instant stored as unix seconds is shown to whoever is reading memory back.
/// </summary>
/// <remarks>
/// <para><b>Local, not UTC.</b> The agent reading these lines is told the current date in local
/// terms, so rendering a fact's timestamp in UTC puts two clocks in one comparison. It is not a
/// cosmetic difference: west of Greenwich every fact recorded after late afternoon renders with
/// tomorrow's date, which turns "what did I decide today" into a wrong answer rather than a vague
/// one.</para>
/// <para><b>To the second, because the stored resolution is the only defensible stopping point.</b>
/// Facts carry <c>valid_from</c> and <c>created_at</c> to the second, and truncating the read to a
/// day threw that away — the model could report which day a memory was made and never what time.
/// Within one working session, which is where most supersession happens, a day-resolution stamp
/// leaves every fact of that session mutually unordered. It also made the analysis behind D44
/// impossible from tool output: establishing that two <c>coverage: none</c> recalls fired 82 minutes
/// before the fact answering them was written needed the store directly, because the read path had
/// discarded exactly the resolution that decides it.</para>
/// <para>This first stopped at the minute, and a real supersession chain is what showed that was
/// the same bug one level finer. Walking the history of a preference, the superseded fact was
/// written at 00:02:11 and its replacement at 00:02:20 — nine seconds apart, and identical on
/// screen. A reader could see that one belief replaced another but not, from the render, which came
/// first. Any truncation coarser than what is stored reintroduces that, so the render now stops
/// where the data does: three more characters a fact, against an ordering that is either correct or
/// silently absent.</para>
/// </remarks>
public static class MomentText
{
    public const string Format = "yyyy-MM-dd HH:mm:ss";

    public static string Local(long unixSeconds) => In(unixSeconds, TimeZoneInfo.Local);

    /// <summary>The same rendering against a stated zone, which is what makes it testable.</summary>
    public static string In(long unixSeconds, TimeZoneInfo zone) =>
        TimeZoneInfo
            .ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone)
            .ToString(Format, CultureInfo.InvariantCulture);
}
