using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Engram.Core;

public interface IServerLauncher
{
    void LaunchDetached(string executablePath, string homeRoot, int port);
}

public sealed class ProcessServerLauncher : IServerLauncher
{
    public void LaunchDetached(string executablePath, string homeRoot, int port)
    {
        // A plain Process.Start without its own redirection makes the child inherit
        // whatever file descriptors this process currently has for stdout/stderr. If
        // the caller (engram start) was itself launched with its stdout piped — as any
        // test harness or supervising shell does — that pipe's write end stays open for
        // as long as this detached server keeps running, so a reader doing
        // ReadToEnd() on the launcher's stdout blocks forever waiting for an EOF that
        // never comes. Routing through a shell that redirects to /dev/null before exec
        // gives the server process its own, disconnected file descriptors.
        var command = new StringBuilder("exec ")
            .Append(ShellQuote(executablePath))
            .Append(" serve --port ")
            .Append(port.ToString(CultureInfo.InvariantCulture))
            .Append(" --home ")
            .Append(ShellQuote(homeRoot))
            .Append(" </dev/null >/dev/null 2>&1")
            .ToString();

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
