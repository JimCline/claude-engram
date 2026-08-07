using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// Lands a set of values in one section of the user's config, backing the file up first and
/// refusing anything Engram did not write.
/// </summary>
/// <remarks>
/// Extracted from the embedding picker when a second caller appeared (D51). The rules here —
/// what counts as ours, when to refuse, when to say nothing changed — are the ones D33 argues
/// for, and a second copy of them would start disagreeing with this one the first time either
/// was tuned. Callers differ only in which keys they are landing.
/// </remarks>
internal static class ConfigWriter
{
    public static int Apply(
        EngramHome home,
        string section,
        IReadOnlyList<(string Key, string Value)> keys,
        bool force,
        DateTimeOffset now,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(keys);

        var path = home.ConfigPath;
        if (!File.Exists(path))
        {
            stderr.WriteLine($"error: no config at {path}. Run 'engram init' first.");
            return 1;
        }

        var text = File.ReadAllText(path);
        var shipped = DefaultConfig.Content;

        var conflicts = keys
            .Where(k => !ConfigEditor.IsUntouched(text, shipped, section, k.Key))
            .Select(k => new ConfigConflict(
                section,
                k.Key,
                ConfigEditor.Read(text, section, k.Key) ?? "nothing",
                ConfigEditor.Read(shipped, section, k.Key) ?? "nothing"))
            .ToList();

        if (conflicts.Count > 0 && !force)
        {
            foreach (var conflict in conflicts)
            {
                stderr.WriteLine("error: " + conflict.Describe());
            }

            stderr.WriteLine();
            stderr.WriteLine("Refusing to overwrite a value Engram did not write. Edit " + path
                + " by hand, or re-run with --force.");
            return 1;
        }

        // Compared by value rather than by the text of the file: the marker comment differs from a
        // hand-written line holding the same setting, and rewriting one to stamp the other would
        // back up and rewrite the config to say what it already said.
        if (keys.All(k => ConfigEditor.Read(text, section, k.Key) == k.Value))
        {
            stdout.WriteLine("Config already says that — left " + path + " alone.");
            return 0;
        }

        var edited = keys.Aggregate(text, (current, k) => ConfigEditor.Set(current, section, k.Key, k.Value));

        if (ConfigEditor.Backup(path, now) is { } backup)
        {
            stdout.WriteLine("Backed up " + path + " to " + backup);
        }

        File.WriteAllText(path, edited);

        foreach (var (key, value) in keys)
        {
            stdout.WriteLine($"  [{section}] {key} = {value}");
        }

        return 0;
    }
}
