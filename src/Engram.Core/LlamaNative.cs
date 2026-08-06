using LLama.Native;

namespace Engram.Core;

/// <summary>
/// Points llama.cpp's log somewhere Engram can use it, before anything loads llama.cpp.
/// </summary>
/// <remarks>
/// <para><b>The setting is process-wide and one-shot.</b> It takes effect only for a native library
/// loaded after it is applied, so <see cref="Prepare"/> has to run before the first
/// <c>LLamaWeights.LoadFromFile</c> and there is no second chance. <see cref="LocalRuntime"/> is the
/// only thing that loads weights, so it is the only caller.</para>
///
/// <para><b>What this deliberately does not do is state library paths.</b> LLamaSharp resolves its
/// own natives, and the obvious worry — that it does so through <c>Assembly.Location</c>, which is
/// empty under Native AOT, which ILC reports as IL3000 — turns out to be a warning about a branch
/// that has a working fallback behind it. Measured on the published osx-arm64 binary: with no path
/// configuration of any kind, <c>embed --rebuild --apply</c> loads MiniLM and writes 45 vectors. So
/// the warning is real and the failure it predicts is not.</para>
///
/// <para><b>Stating the paths was tried and is worse.</b> Computing
/// <c>runtimes/&lt;rid&gt;/native/</c> from <see cref="AppContext.BaseDirectory"/> and handing the
/// result to <c>WithLibrary</c> replaces LLamaSharp's selecting policy rather than assisting it, and
/// that policy is choosing between builds that are not interchangeable. The backends do not even
/// agree on a shape: the CPU package puts <c>libllama.dylib</c> directly in <c>native/</c> on macOS,
/// but on linux-x64 it ships only <c>native/{noavx,avx,avx2,avx512}/</c> with nothing at the top,
/// and the CUDA package adds <c>native/cuda12/</c> beside them. Any search simple enough to write
/// here picks by sort order, and <c>avx</c> sorts first — so the version of this that looked correct
/// on a Mac would have silently run the weakest CPU build on a CUDA machine. Detecting the host's
/// AVX level is what LLamaSharp's policy already does, and reimplementing it to work around a
/// warning that costs nothing is a bad trade.</para>
///
/// <para><b>Discarding llama.cpp's log outright would be worse than the noise.</b> It writes a few
/// dozen lines per load, and Engram's callers are not people watching a terminal: the hooks emit
/// JSON that Claude Code parses and <c>probe --json</c> asserts an empty stderr in an end-to-end
/// test. But when a GGUF will not load, the managed exception says little while llama.cpp's log
/// says exactly what is wrong. So errors and warnings are kept, bounded, and handed to whoever
/// reports the failure; the tensor inventory below them is dropped.</para>
/// </remarks>
public static class LlamaNative
{
    private const int Keep = 8;

    private static readonly Lock Gate = new();
    private static readonly Queue<string> Recent = new();
    private static bool prepared;

    /// <summary>Routes llama.cpp's log once per process. Safe to call repeatedly.</summary>
    /// <remarks>
    /// The lock is held across the registration rather than just the flag, because the flag is what
    /// every other caller waits on. Releasing it early let a second thread see <c>prepared</c>, skip
    /// straight to <c>LoadFromFile</c>, and load the library out from under the first thread — which
    /// LLamaSharp then rejects, since configuration is refused once anything is loaded. Two
    /// <see cref="LocalRuntime"/> instances have two locks of their own and do not order this.
    /// </remarks>
    public static void Prepare()
    {
        lock (Gate)
        {
            if (prepared)
            {
                return;
            }

            prepared = true;

            try
            {
                NativeLibraryConfig.All.WithLogCallback(Record);
            }
            catch (InvalidOperationException)
            {
                // Something outside Engram got there first. Nothing here can un-load it, and the
                // log is a diagnostic rather than a dependency — failing a load that would other-
                // wise work, in order to report on loads, would be the wrong way round.
            }
        }
    }

    /// <summary>The most recent errors and warnings from llama.cpp, oldest first.</summary>
    public static IReadOnlyList<string> RecentProblems()
    {
        lock (Gate)
        {
            return [.. Recent];
        }
    }

    private static void Record(LLamaLogLevel level, string message)
    {
        // Named rather than compared, because the levels are not ordered the way the comparison
        // wants: Error sorts above Warning, and Continue sorts above both while meaning None.
        if (level is not (LLamaLogLevel.Warning or LLamaLogLevel.Error))
        {
            return;
        }

        var trimmed = message.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            Recent.Enqueue(trimmed);
            while (Recent.Count > Keep)
            {
                Recent.Dequeue();
            }
        }
    }
}
