namespace Engram.Core;

/// <summary>
/// Resolves symlinks component by component, because .NET has no realpath and the gap is
/// not theoretical: macOS mounts <c>/tmp</c> as a symlink to <c>/private/tmp</c>, so a
/// hook records the path a tool used while git reports the canonical form — two spellings
/// of one file that compare as strangers. Measured on the published binary: a drained
/// queue entry for <c>/tmp/…</c> was left behind as another repo's edit because the root
/// resolved to <c>/private/tmp/…</c>.
/// </summary>
public static class PathCanonicalizer
{
    public static string Canonical(string path) => Canonical(path, depth: 0);

    private static string Canonical(string path, int depth)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || depth > 8)
        {
            return full;
        }

        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            try
            {
                // Directory.ResolveLinkTarget resolves only when this component itself is
                // a link — which is exactly right, walked front to back: every prefix gets
                // its turn. The recursion is not optional: a link's target is spelled
                // however the link was written, so its own prefix may contain further
                // links (/var inside a target that jumped out of /tmp), and returning it
                // unwalked reintroduces the exact mismatch this class exists to remove.
                if (Directory.ResolveLinkTarget(current, returnFinalTarget: true) is { } target)
                {
                    current = Canonical(target.FullName, depth + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A component that cannot be inspected is kept as spelled; comparison
                // degrades to the literal path rather than the walk failing.
            }
        }

        return current;
    }
}
