using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>What <c>sqlite-vec</c> turned out to be on one particular connection.</summary>
public enum VectorExtensionState
{
    /// <summary>No extension file in <c>lib/</c>. Embeddings were never installed (D1).</summary>
    NotInstalled,

    /// <summary>Registered on this connection, and only this one.</summary>
    Loaded,

    /// <summary>The file is there and would not load — wrong architecture, or truncated.</summary>
    Failed,
}

/// <summary>
/// Loads <c>sqlite-vec</c> onto a connection. Every connection, every time.
/// </summary>
/// <remarks>
/// <para><b>Why unconditionally, and why per connection.</b> Loadable extensions are
/// connection-scoped, and connection pooling hides that: load the extension, dispose the
/// connection, and the next open in the same process gets the same <c>sqlite3</c> handle with
/// the module still registered. So a vector query written against an opt-in loader passes its
/// test — some earlier connection did the loading — and fails in a hook that happens to draw a
/// cold handle. The bug is invisible exactly where tests run and visible only in production.
/// Loading on every open removes the class of defect rather than documenting it.</para>
///
/// <para><b>What it costs</b>, because that is the objection: 0.195 ms on a cold connection and
/// 0.036 ms on a pooled one, measured over 200 opens each. The database open it rides along
/// with is 1.0–1.5 ms, so this is under a fifth of an open it is already paying for and
/// comfortably inside the margin the primer hooks run in. <c>file-touched</c> is unaffected —
/// it never opens the database at all.</para>
///
/// <para><b>Never infer loadedness from a query.</b> <c>SELECT vec_version()</c> answering does
/// not mean this connection loaded anything; it means some connection did and the pool recycled
/// its handle. The only honest report is the one this routine returns for the connection it was
/// just handed, which is why the state is a return value and not a lookup.</para>
/// </remarks>
public static class VectorExtension
{
    /// <summary>
    /// The extension's filename in <see cref="EngramHome.LibDir"/>, per platform.
    /// </summary>
    /// <remarks>
    /// SQLite appends a platform suffix when the path as given does not resolve, so an
    /// extension-less path would also work — naming the file outright means a failure reports
    /// the path that was actually tried instead of one SQLite invented.
    /// </remarks>
    public static string FileName =>
        OperatingSystem.IsWindows() ? "vec0.dll"
        : OperatingSystem.IsMacOS() ? "vec0.dylib"
        : "vec0.so";

    public static string PathIn(string libraryDirectory) =>
        Path.Combine(libraryDirectory, FileName);

    /// <summary>
    /// Registers the extension on <paramref name="connection"/>, reporting what happened.
    /// </summary>
    /// <remarks>
    /// Never throws. An absent <c>lib/</c> is the ordinary state of an instance that has not
    /// opted into embeddings, and a failed load still leaves a working connection — measured —
    /// so the vector lane degrades to FTS5 rather than taking recall down with it. Callers that
    /// need to know call this and read the result; callers that just want a usable connection
    /// can ignore it.
    ///
    /// <para>Safe to call twice on one connection: measured, the second load is a no-op rather
    /// than an error. That is what lets <see cref="EngramDatabase.Open(EngramHome)"/> load
    /// eagerly without constraining a caller who loads again to learn the state.</para>
    /// </remarks>
    public static VectorExtensionState Load(SqliteConnection connection, string? libraryDirectory)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrEmpty(libraryDirectory))
        {
            return VectorExtensionState.NotInstalled;
        }

        var path = PathIn(libraryDirectory);
        if (!File.Exists(path))
        {
            return VectorExtensionState.NotInstalled;
        }

        try
        {
            connection.LoadExtension(path);
            return VectorExtensionState.Loaded;
        }
        catch (SqliteException)
        {
            return VectorExtensionState.Failed;
        }
    }
}
