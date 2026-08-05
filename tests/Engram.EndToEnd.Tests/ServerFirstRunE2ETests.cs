namespace Engram.EndToEnd.Tests;

public class ServerFirstRunE2ETests
{
    // Every other sandbox home in this suite arrives already created, which is exactly
    // why the daemon's first-run path went unexercised: Kestrel logs while it binds, so
    // the log file is the first thing to need the home directory to exist. On a machine
    // where ~/.engram has never been created, that write threw inside a process whose
    // stderr is /dev/null, and the only symptom was `engram start` spending its whole
    // ten-second budget before reporting that the server never became healthy.
    [Fact]
    public void Start_HomeDirectoryDoesNotExistYet_StillBecomesHealthy()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var scratch = new TestHome();
        var absentHome = Path.Combine(scratch.Root, "never-created");
        Assert.False(Directory.Exists(absentHome));

        var port = FreeTcpPort.Next();
        var (startExit, _, startErr) = EngramProcess.Run(absentHome, "start", "--port", port.ToString());

        try
        {
            Assert.True(startExit == 0, $"start failed against a home that does not exist yet: {startErr}");

            var (statusExit, statusOut, _) = EngramProcess.Run(absentHome, "status");
            Assert.Equal(0, statusExit);
            Assert.Contains("server: running", statusOut);
        }
        finally
        {
            EngramProcess.Run(absentHome, "stop");
        }
    }
}
