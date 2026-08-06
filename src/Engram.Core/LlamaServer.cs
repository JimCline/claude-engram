using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Engram.Core;

/// <summary>Where a <c>llama-server</c> binary was found, and how.</summary>
public sealed record LlamaServerLocation(string Path, string Source);

/// <summary>
/// A running <c>llama-server</c> child, and the port it answers on.
/// </summary>
/// <remarks>
/// Disposing kills the process tree. llama.cpp's server does not exit when its parent does, so a
/// handle that leaked would leave a multi-gigabyte process holding a GPU until someone noticed.
/// </remarks>
public sealed class LlamaServerHandle : IDisposable
{
    private readonly Process process;
    private bool disposed;

    internal LlamaServerHandle(Process process, int port, string modelPath)
    {
        this.process = process;
        Port = port;
        ModelPath = modelPath;
    }

    public int Port { get; }

    public string ModelPath { get; }

    public Uri Endpoint => new($"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/v1");

    public bool Running => !disposed && !process.HasExited;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone, or the platform will not let us ask. Either way there is nothing left
            // to do and failing to dispose helps nobody.
        }

        process.Dispose();
    }
}

/// <summary>What happened when a local runtime was asked for.</summary>
public sealed record LlamaServerStart(LlamaServerHandle? Handle, string Reason)
{
    public bool Started => Handle is not null;

    public static LlamaServerStart Failed(string reason) => new(null, reason);
}

/// <summary>
/// Runs a model locally by managing a <c>llama-server</c> child, rather than by loading llama.cpp
/// into this process.
/// </summary>
/// <remarks>
/// <para><b>Why a child process and not an in-process binding.</b> D1 keeps llama.cpp out of the
/// core binary, so the question was only ever which out-of-process shape to use. A .NET binding in
/// a sidecar would need its own project, its own IPC, its own native loading, and its own
/// answer to whether it can run encoder-only models at all — in order to reimplement something
/// llama.cpp already ships. Its server speaks the same <c>/v1/embeddings</c> API that
/// <see cref="OpenAiCompatibleEmbedder"/> already talks to and that the probe already tests, so
/// "local" costs a process launch and no new embedding code whatsoever. It also settles the
/// platform question the way D28 asks for: llama.cpp's own builds are what carry Metal on a Mac
/// and CUDA elsewhere, and neither is Engram's to maintain.</para>
///
/// <para><b>Found, not fetched.</b> Three places, in order: the config, <c>lib/</c> in the Engram
/// home, then <c>PATH</c>. Engram does not download this one. <c>sqlite-vec</c> is a single small
/// extension with a digest per platform; llama.cpp is a large native artifact whose build varies by
/// accelerator, and pinning digests across every platform and accelerator combination is precisely
/// the packaging burden D28 declines. A package manager or a local build already does it better.</para>
/// </remarks>
public static class LlamaServer
{
    public static string FileName => OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    /// <summary>How long to wait for the model to load before giving up.</summary>
    /// <remarks>
    /// Generous because it is bounded by disk and model size rather than by anything Engram does —
    /// spike E measured 6.5 s for a cold load, and a first run from a cold page cache is slower
    /// still. This runs once per server lifetime, so waiting is cheaper than failing.
    /// </remarks>
    public static TimeSpan DefaultStartupTimeout => TimeSpan.FromSeconds(90);

    /// <summary>Finds a <c>llama-server</c>, naming where it came from.</summary>
    public static LlamaServerLocation? Locate(
        EngramHome home,
        string? configured,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(environment);

        if (configured is { Length: > 0 })
        {
            // Named explicitly, so a missing file is an error to report rather than a reason to go
            // looking somewhere else: silently running a different binary than the one someone
            // configured is worse than not running one.
            return File.Exists(configured) ? new LlamaServerLocation(configured, "config") : null;
        }

        var beside = Path.Combine(home.LibDir, FileName);
        if (File.Exists(beside))
        {
            return new LlamaServerLocation(beside, "lib");
        }

        foreach (var directory in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate))
            {
                return new LlamaServerLocation(candidate, "PATH");
            }
        }

        return null;
    }

    public static string WhereItLooked(EngramHome home) =>
        $"Looked at [embedding] server_path, {Path.Combine(home.LibDir, FileName)}, and PATH. "
            + "Install llama.cpp (Homebrew: 'brew install llama.cpp'), or build it and put the "
            + $"binary in {home.LibDir}.";

    /// <summary>Starts a server for one model and waits until it will answer.</summary>
    public static LlamaServerStart Start(
        EngramHome home,
        EmbeddingModel model,
        LlamaServerLocation location,
        TimeSpan startupTimeout,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(location);

        var modelPath = ModelFetcher.PathFor(home, model);
        if (!File.Exists(modelPath))
        {
            return LlamaServerStart.Failed(
                $"{model.Id} is not downloaded yet. Run 'engram model install {model.Id}'.");
        }

        var port = FreePort();
        var startInfo = new ProcessStartInfo(location.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in Arguments(modelPath, model, port))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return LlamaServerStart.Failed($"Could not run {location.Path}: {exception.Message}");
        }

        if (process is null)
        {
            return LlamaServerStart.Failed($"Could not run {location.Path}.");
        }

        // Drained, not read. A child whose pipes fill up blocks forever, and llama.cpp's server is
        // talkative while a model loads.
        var log = new ChildLog(process);

        var handle = new LlamaServerHandle(process, port, modelPath);
        if (WaitUntilReady(port, process, startupTimeout, handler))
        {
            return new LlamaServerStart(handle, $"{location.Path} ({location.Source}) on port {port}");
        }

        // Asked before disposing, not after: Process.Dispose detaches the object from the OS
        // handle, and HasExited then throws rather than answering — which would replace the
        // diagnosis with an exception exactly when there is something to diagnose.
        var died = process.HasExited;
        if (died)
        {
            // The parameterless overload additionally waits for the async output readers to
            // drain. Without it the last thing the server said — the whole reason to look — is
            // still in flight when it is asked for.
            process.WaitForExit();
        }

        var tail = log.Tail();
        handle.Dispose();

        return LlamaServerStart.Failed(
            died
                ? $"{location.Path} exited while loading {model.Id}. {tail}"
                : $"{location.Path} did not answer within {startupTimeout.TotalSeconds:0}s. {tail}");
    }

    /// <summary>The context window actually served, which is not always the model's.</summary>
    /// <remarks>
    /// Capped because a pooled embedding has to fit in one physical batch, so the batch buffers
    /// below are sized to this number — and at qwen3's 32k that allocation is far larger than
    /// anything Engram would put through it. Facts and queries are sentences. 8k is already
    /// generous for both, and the two models under the cap are unaffected.
    /// </remarks>
    public const int MaxServedContext = 8192;

    /// <remarks>
    /// <para><c>-ngl 99</c> offloads every layer. These models are 25–640 MB, so there is no case
    /// where splitting them is right, and leaving it off is the difference between Metal and a CPU
    /// fallback on a Mac — the one performance property D28 names outright.</para>
    ///
    /// <para>Batch and micro-batch are pinned to the window rather than left at their defaults:
    /// llama.cpp cannot pool an embedding across physical batches, so an input longer than the
    /// micro-batch is refused rather than split. The default is 512 tokens, well inside the range
    /// a long fact can reach.</para>
    ///
    /// <para>The log is deliberately not disabled — <see cref="ChildLog"/> drains it, so it cannot
    /// block, and it is the only account of why a model failed to load.</para>
    /// </remarks>
    public static IEnumerable<string> Arguments(string modelPath, EmbeddingModel model, int port)
    {
        ArgumentNullException.ThrowIfNull(model);

        var context = Math.Min(model.ContextTokens, MaxServedContext).ToString(CultureInfo.InvariantCulture);
        return
        [
            "--model", modelPath,
            "--embedding",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--host", "127.0.0.1",
            "--ctx-size", context,
            "--batch-size", context,
            "--ubatch-size", context,
            "-ngl", "99",
        ];
    }

    /// <summary>A port the OS says is free right now.</summary>
    /// <remarks>
    /// Racy by nature — something else may take it between the listener closing and the child
    /// binding. The alternative is a fixed port, which collides with a second Engram instance
    /// every time rather than almost never, and the failure here is a startup error rather than
    /// two servers quietly sharing one.
    /// </remarks>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool WaitUntilReady(int port, Process process, TimeSpan timeout, HttpMessageHandler? handler)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(2);
        var health = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/health");
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return false;
            }

            try
            {
                using var response = client.GetAsync(health).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not up yet, or still loading. Both are answered by waiting.
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>Keeps the child's pipes drained and its last words available.</summary>
    private sealed class ChildLog
    {
        private readonly Lock gate = new();
        private readonly Queue<string> lines = new();

        public ChildLog(Process process)
        {
            process.OutputDataReceived += (_, e) => Add(e.Data);
            process.ErrorDataReceived += (_, e) => Add(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public string Tail()
        {
            lock (gate)
            {
                return lines.Count == 0 ? "It said nothing." : "It said: " + string.Join(" / ", lines);
            }
        }

        private void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (gate)
            {
                lines.Enqueue(line.Trim());
                while (lines.Count > 5)
                {
                    lines.Dequeue();
                }
            }
        }
    }
}
