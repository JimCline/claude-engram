using System.Diagnostics;
using System.Net.Http;

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
