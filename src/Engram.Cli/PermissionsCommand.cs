using Engram.Core;

namespace Engram.Cli;

public static class PermissionsCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = false;
        var remove = false;
        string? settingsOverride = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply":
                    apply = true;
                    break;
                case "--remove":
                    remove = true;
                    break;
                case "--settings":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("error: --settings requires a value");
                        return 1;
                    }

                    settingsOverride = args[++i];
                    break;
                default:
                    stderr.WriteLine($"error: unrecognized argument: {args[i]}");
                    return 1;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var settingsPath = settingsOverride ?? home.ClaudeSettingsPath;

        try
        {
            return remove
                ? Revoke(home, settingsPath, apply, stdout)
                : Grant(home, settingsPath, apply, stdout);
        }
        // Native AOT turns an unhandled exception into SIGABRT, so a settings file that is
        // read-only or on a full disk would abort mid-edit rather than say what went wrong.
        catch (Exception ex) when (ex is ClaudeSettingsException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: {ex.Message}");
            stderr.WriteLine();
            stderr.WriteLine("Add these to permissions.allow yourself, and Engram will leave the file alone:");
            foreach (var tool in ClaudePermissions.GrantedTools)
            {
                stderr.WriteLine($"  \"{tool}\"");
            }

            return 1;
        }
    }

    private static int Grant(EngramHome home, string settingsPath, bool apply, TextWriter stdout)
    {
        var plan = ClaudePermissions.PlanGrant(settingsPath);

        stdout.WriteLine($"Claude Code settings: {settingsPath}{(plan.SettingsFileExisted ? "" : " (does not exist yet)")}");
        stdout.WriteLine();

        if (plan.AlreadyPresent.Count > 0)
        {
            stdout.WriteLine("already allowed:");
            foreach (var tool in plan.AlreadyPresent)
            {
                stdout.WriteLine($"  = {tool}");
            }

            stdout.WriteLine();
        }

        if (plan.ToAdd.Count == 0)
        {
            stdout.WriteLine("Every tool Engram grants is already in permissions.allow; nothing to do.");
            return 0;
        }

        stdout.WriteLine(apply ? "adding to permissions.allow:" : "would add to permissions.allow:");
        foreach (var tool in plan.ToAdd)
        {
            stdout.WriteLine($"  + {tool}");
        }

        stdout.WriteLine();
        stdout.WriteLine("Not granted, so a human stays in the loop:");
        stdout.WriteLine("  engram_forget            closes a fact, and there is no un-retract");
        stdout.WriteLine("  engram_start, engram_stop  move the daemon out from under a live session");
        stdout.WriteLine();

        if (!apply)
        {
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to grant these.");
            return 0;
        }

        if (plan.SettingsFileExisted)
        {
            Backup(settingsPath, stdout);
        }

        ClaudePermissions.ApplyGrant(plan, home.GrantedPermissionsPath);

        stdout.WriteLine($"Granted {plan.ToAdd.Count} tool(s).");
        stdout.WriteLine($"Recorded in {home.GrantedPermissionsPath} so uninstall takes back exactly these.");
        stdout.WriteLine();
        stdout.WriteLine("Restart Claude Code, or run /reload-plugins, for the change to take effect.");
        return 0;
    }

    private static int Revoke(EngramHome home, string settingsPath, bool apply, TextWriter stdout)
    {
        var plan = ClaudePermissions.PlanRevoke(settingsPath, home.GrantedPermissionsPath);

        stdout.WriteLine($"Claude Code settings: {settingsPath}");
        stdout.WriteLine();

        if (plan.LeftAlone.Count > 0)
        {
            stdout.WriteLine("left alone — present, but Engram did not add them:");
            foreach (var tool in plan.LeftAlone)
            {
                stdout.WriteLine($"  = {tool}");
            }

            stdout.WriteLine();
        }

        if (plan.ToRemove.Count == 0)
        {
            stdout.WriteLine("Nothing to remove: no permissions.allow entry is recorded as Engram's.");
            return 0;
        }

        stdout.WriteLine(apply ? "removing from permissions.allow:" : "would remove from permissions.allow:");
        foreach (var tool in plan.ToRemove)
        {
            stdout.WriteLine($"  - {tool}");
        }

        stdout.WriteLine();

        if (!apply)
        {
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to remove these.");
            return 0;
        }

        Backup(settingsPath, stdout);

        ClaudePermissions.ApplyRevoke(plan, home.GrantedPermissionsPath);
        stdout.WriteLine($"Removed {plan.ToRemove.Count} tool(s).");
        return 0;
    }

    // The timestamp only resolves to the second, and granting then revoking inside one second
    // is what an uninstall right after an install looks like. Numbering the collision keeps the
    // filename shape install.sh already uses, rather than widening it to milliseconds.
    private static void Backup(string settingsPath, TextWriter stdout)
    {
        var basePath = ClaudePermissions.BackupPath(settingsPath, DateTime.UtcNow);
        var path = basePath;

        for (var n = 2; File.Exists(path); n++)
        {
            path = $"{basePath}-{n}";
        }

        File.Copy(settingsPath, path, overwrite: false);
        stdout.WriteLine($"Backed up {settingsPath} to {path}");
    }
}
