using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram profile</c> — the <c>[mcp] tool_profile</c> config key
/// (docs/memory-expansion/03-tool-profiles-spec.md). <c>set</c> acts immediately, unlike
/// destructive verbs elsewhere: a profile choice is a freely reversible preference, not a
/// removal or rewrite of authored data, so D49's dry-run rule does not apply to it.
/// </summary>
public static class ProfileCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            stderr.WriteLine("error: expected a subcommand — show or set.");
            return 2;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var rest = args[1..];

        return args[0] switch
        {
            "show" => Show(home, rest, stdout, stderr),
            "set" => Set(home, rest, stdout, stderr),
            _ => Unknown(args[0], stderr),
        };
    }

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown subcommand '{subcommand}' — expected show or set.");
        return 2;
    }

    private static int Show(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 0)
        {
            stderr.WriteLine("Usage: engram profile show");
            return 2;
        }

        var config = ConfigFile.Load(home.ConfigPath);
        var settings = ToolProfileSettings.Read(config);

        foreach (var problem in settings.Problems)
        {
            stdout.WriteLine("note: " + problem);
        }

        stdout.WriteLine(ToolProfileSettings.ToText(settings.Profile));
        return 0;
    }

    private static int Set(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var force = args.Contains("--force");
        var positional = args.Where(a => a != "--force").ToArray();

        if (positional.Length != 1 || !ToolProfileSettings.TryParse(positional[0], out var profile))
        {
            stderr.WriteLine($"""Usage: engram profile set <{string.Join('|', ToolProfileSettings.Names)}> [--force]""");
            return 2;
        }

        return ConfigWriter.Apply(
            home,
            ToolProfileSettings.Section,
            [(ToolProfileSettings.Key, ConfigEditor.Quote(ToolProfileSettings.ToText(profile)))],
            force,
            DateTimeOffset.UtcNow,
            stdout,
            stderr);
    }
}
