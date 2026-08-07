using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

public class ServerLifecycleE2ETests
{
    [Fact]
    public void Start_Status_StartAgain_Stop_Status_FullLifecycle()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();

        var (statusBeforeExit, statusBeforeOut, _) = EngramProcess.Run(home.Root, "status");
        Assert.Equal(1, statusBeforeExit);
        Assert.Contains("not running", statusBeforeOut);

        var (startExit, startOut, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        try
        {
            var (statusRunningExit, statusRunningOut, _) = EngramProcess.Run(home.Root, "status");
            Assert.Equal(0, statusRunningExit);
            Assert.Contains("server: running", statusRunningOut);
            Assert.Contains($"port: {port}", statusRunningOut);

            var (startAgainExit, startAgainOut, startAgainErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
            Assert.True(startAgainExit == 0, $"second start failed: {startAgainErr}");
            Assert.Contains("already running", startAgainOut);
        }
        finally
        {
            var (stopExit, _, stopErr) = EngramProcess.Run(home.Root, "stop");
            Assert.True(stopExit == 0, $"stop failed: {stopErr}");
        }

        var (statusAfterExit, statusAfterOut, _) = EngramProcess.Run(home.Root, "status");
        Assert.Equal(1, statusAfterExit);
        Assert.Contains("not running", statusAfterOut);
    }

    [Fact]
    public void Stop_NothingRunning_ExitsZero()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exit, _, _) = EngramProcess.Run(home.Root, "stop");

        Assert.Equal(0, exit);
    }

    [Fact]
    public void Status_StalePidFile_ReportsStaleAndExitsNonZero()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        File.WriteAllText(
            Path.Combine(home.Root, "engram.pid"),
            """{"pid":999999,"port":7433,"version":"0.1.0","start_time":"2026-01-01T00:00:00Z"}""");

        var (exit, output, _) = EngramProcess.Run(home.Root, "status");

        Assert.Equal(1, exit);
        Assert.Contains("not running", output);
        Assert.Contains("stale", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_TwoConcurrentInvocations_ProduceExactlyOneServer()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();

        var first = Task.Run(() => EngramProcess.Run(home.Root, "start", "--port", port.ToString()));
        var second = Task.Run(() => EngramProcess.Run(home.Root, "start", "--port", port.ToString()));
        var results = await Task.WhenAll(first, second);

        try
        {
            Assert.All(results, r => Assert.True(r.ExitCode == 0, $"start exited {r.ExitCode}: {r.Stderr}"));

            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/health", TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    /// <summary>
    /// What identifies a running server, proved against the shipped binary and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>Two claims, both black-box. <b>That the token is reader-independent</b>: the process
    /// that ran <c>start</c> recorded the server's own view of it, and the separate process running
    /// <c>status</c> reads that pid's token for itself — so a value that differs per reader makes
    /// <c>status</c> report a healthy server dead. That is precisely what
    /// <c>Process.StartTime</c> did on Linux, measured at 24 disagreements out of 24 cross-process
    /// reads, and sourcing the token from it again fails this on Linux essentially always.</para>
    ///
    /// <para><b>That the token is what decides</b>: a pid file whose token has been altered, and
    /// whose every other field including <c>start_time</c> is untouched, must make the same server
    /// unrecognisable. This half fails on every platform if the comparison drifts back to the wall
    /// clock, which is what keeps the test honest on macOS — where the first claim cannot fail,
    /// because that kernel hands out an absolute creation time.</para>
    ///
    /// <para>The pid file is restored before <c>stop</c> deliberately: a server the pid file can no
    /// longer name is a server this test would leak.</para>
    /// </remarks>
    [Fact]
    public void PidFileStartToken_IsReaderIndependent_AndIsWhatIdentifiesTheServer()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        var pidFilePath = Path.Combine(home.Root, "engram.pid");
        var original = File.ReadAllText(pidFilePath);

        try
        {
            var record = JsonNode.Parse(original)!.AsObject();
            var token = record["start_token"]?.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(token), $"start recorded no token: {original}");

            var (runningExit, runningOut, _) = EngramProcess.Run(home.Root, "status");
            Assert.True(runningExit == 0, $"status called a live server dead: {runningOut}");
            Assert.Contains("server: running", runningOut);

            record["start_token"] = token + "-altered";
            File.WriteAllText(pidFilePath, record.ToJsonString());

            var (alteredExit, alteredOut, _) = EngramProcess.Run(home.Root, "status");
            Assert.Equal(1, alteredExit);
            Assert.Contains("not running", alteredOut);
        }
        finally
        {
            File.WriteAllText(pidFilePath, original);
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public async Task Health_RequestWithOriginHeader_IsRejected()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        try
        {
            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");
            request.Headers.Add("Origin", "http://evil.example.com");

            var response = await httpClient.SendAsync(request, cancellationToken);

            Assert.False(response.IsSuccessStatusCode);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public async Task Serve_NormalRequestCycle_WritesNothingToStdout()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var startInfo = new ProcessStartInfo(EndToEndBinary.Path!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.Environment["ENGRAM_HOME"] = home.Root;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start serve");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            using var httpClient = new HttpClient();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            HttpResponseMessage? health = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    health = await httpClient.GetAsync($"http://127.0.0.1:{port}/health", cancellationToken);
                    break;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }

            Assert.NotNull(health);
            Assert.True(health!.IsSuccessStatusCode);

            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);
            await client.ListToolsAsync(cancellationToken);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        var stdout = await stdoutTask;
        Assert.Equal(string.Empty, stdout);
    }
}
