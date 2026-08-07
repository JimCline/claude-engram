using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram export</c> — writes facts as a portable JSONL bundle (spec §3.2): the whole
/// store, or one subtree with <c>--path</c>. The format is the backup journal's, so the
/// bundle replays into any store, any later schema, with the machinery that already exists.
/// </summary>
internal static class ExportCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        string? pathPrefix = null;
        string? outFile = null;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--path" when i + 1 < rest.Length:
                    pathPrefix = rest[++i];
                    break;
                case "--out" when i + 1 < rest.Length:
                    outFile = rest[++i];
                    break;
                default:
                    stderr.WriteLine($"error: unexpected argument '{rest[i]}'");
                    return 1;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        // Refused rather than replaced: the file may be somebody's only copy of a previous
        // export, and this tool cannot tell.
        if (outFile is not null && File.Exists(outFile))
        {
            stderr.WriteLine($"error: {outFile} already exists — pick another name or remove it first");
            return 1;
        }

        using var connection = EngramDatabase.Open(home);

        int written;
        if (outFile is null)
        {
            written = FactJournal.WriteTo(connection, stdout, pathPrefix, DateTimeOffset.UtcNow);

            // The bundle owns stdout, so the summary goes beside it.
            stderr.WriteLine(Summary(written, pathPrefix, "to stdout"));
        }
        else
        {
            using (var writer = new StreamWriter(new FileStream(outFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                written = FactJournal.WriteTo(connection, writer, pathPrefix, DateTimeOffset.UtcNow);
            }

            stdout.WriteLine(Summary(written, pathPrefix, $"to {outFile}"));
        }

        return 0;
    }

    private static string Summary(int written, string? pathPrefix, string destination) =>
        $"Exported {written} {(written == 1 ? "fact" : "facts")}"
            + (pathPrefix is null ? string.Empty : $" under {pathPrefix}")
            + $" {destination}.";
}
