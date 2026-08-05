using System.Text.RegularExpressions;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The slash commands under plugin/commands are prompts, so nothing compiles them and
/// nothing checks them at load time. A command that names a moved script or a renamed
/// MCP tool fails for the first user who types it, silently and in their session rather
/// than in CI. These are the checks that would otherwise never run.
/// </summary>
public partial class PluginCommandTests
{
    private static readonly string CommandsDirectory =
        Path.Combine(PluginSandbox.PluginDirectory, "commands");

    public static TheoryData<string> CommandFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(CommandsDirectory, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    // The whole reason scripts/engram-cli.sh exists rather than reusing hooks/engram-exec.sh.
    // That one swallows a missing binary because a broken hook is worse than an absent one;
    // here somebody typed a command and is waiting, so silence is the bug.
    [Fact]
    public void EngramCli_NoBinaryAnywhere_SaysSoAndExitsNonZero()
    {
        using var sandbox = new PluginSandbox();

        var (exitCode, stdout, _) = sandbox.Run("scripts/engram-cli.sh", "status");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("engram is not installed", stdout);
        Assert.Contains("install.sh", stdout);
    }

    [Fact]
    public void EngramCli_BinaryInstalled_ForwardsArgumentsIncludingOnesContainingSpaces()
    {
        using var sandbox = new PluginSandbox();
        sandbox.InstallStubAt(".local/bin/engram", body: """for a in "$@"; do echo "[$a]"; done""");

        var (exitCode, stdout, _) = sandbox.Run("scripts/engram-cli.sh", "probe", "--since", "7 d");

        Assert.Equal(0, exitCode);
        Assert.Equal("[probe]\n[--since]\n[7 d]\n", stdout.Replace("\r\n", "\n"));
    }

    // One resolver, two audiences: a command and a hook must never disagree about which
    // binary they are talking to, or a diagnostic reports on a different install than the
    // one actually answering.
    [Fact]
    public void EngramCli_ResolvesThroughTheSameOrderAsTheHooks()
    {
        using var sandbox = new PluginSandbox();
        sandbox.InstallStubAt(".local/bin/engram", body: """echo "default" """);
        var overridePath = sandbox.InstallStubAt("elsewhere/engram", body: """echo "override" """);

        var (_, stdout, _) = sandbox.Run(
            "scripts/engram-cli.sh", environment: ("ENGRAM_BIN", overridePath), "status");

        Assert.Equal("override", stdout.Trim());
    }

    [Theory]
    [MemberData(nameof(CommandFiles))]
    public void EveryCommand_HasFrontmatterWithADescription(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(CommandsDirectory, fileName)).Replace("\r\n", "\n");

        Assert.StartsWith("---\n", text);
        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        Assert.True(end > 0, $"{fileName} has an unterminated frontmatter block.");

        var frontmatter = text[..end];
        Assert.Contains("description:", frontmatter);
    }

    // A command referencing a script that has since been renamed or moved fails at the
    // moment a user types it, with an error only they see.
    [Theory]
    [MemberData(nameof(CommandFiles))]
    public void EveryCommand_ReferencesOnlyPluginFilesThatExist(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(CommandsDirectory, fileName));

        foreach (Match match in PluginRootReference().Matches(text))
        {
            var relative = match.Groups[1].Value;
            var resolved = Path.Combine(PluginSandbox.PluginDirectory, relative);

            Assert.True(
                File.Exists(resolved),
                $"{fileName} references ${{CLAUDE_PLUGIN_ROOT}}/{relative}, which does not exist.");
        }
    }

    // Executability is carried in the git index, not in the file's text, so it is exactly
    // the kind of thing a rename or a fresh clone loses without anything looking wrong.
    [Fact]
    public void EveryPluginShellScript_IsExecutable()
    {
        // Written as an early return rather than Assert.SkipUnless so the platform
        // analyzer can see that GetUnixFileMode below is unreachable on Windows.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes do not exist on Windows.");
            return;
        }

        var scripts = Directory.GetFiles(PluginSandbox.PluginDirectory, "*.sh", SearchOption.AllDirectories);
        Assert.NotEmpty(scripts);

        foreach (var script in scripts)
        {
            var mode = File.GetUnixFileMode(script);
            Assert.True(
                mode.HasFlag(UnixFileMode.UserExecute),
                $"{Path.GetRelativePath(PluginSandbox.PluginDirectory, script)} is not executable.");
        }
    }

    // Commands name MCP tools in allowed-tools and in their prose. Nothing binds those
    // strings to the server, so a renamed tool leaves a command that reads perfectly and
    // does nothing. Ask the running server what it actually registers.
    [Fact]
    public async Task EveryMcpToolNamedByACommand_IsOneTheServerRegisters()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(CommandsDirectory, "*.md"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in QualifiedToolName().Matches(text))
            {
                named.Add(match.Groups[1].Value);
            }

            foreach (Match match in BacktickedToolName().Matches(text))
            {
                named.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(named);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            var toolsNode = await client.ListToolsAsync(cancellationToken);
            var registered = toolsNode!["result"]!["tools"]!.AsArray()
                .Select(t => t!["name"]!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var name in named)
            {
                Assert.True(
                    registered.Contains(name),
                    $"A command names the MCP tool '{name}', which the server does not register. "
                        + $"Registered: {string.Join(", ", registered.OrderBy(n => n, StringComparer.Ordinal))}");
            }
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [GeneratedRegex(@"\$\{CLAUDE_PLUGIN_ROOT\}/([A-Za-z0-9_./-]+)")]
    private static partial Regex PluginRootReference();

    [GeneratedRegex(@"mcp__plugin_engram_engram__(engram_[a-z]+)")]
    private static partial Regex QualifiedToolName();

    [GeneratedRegex(@"`(engram_[a-z]+)`")]
    private static partial Regex BacktickedToolName();
}
