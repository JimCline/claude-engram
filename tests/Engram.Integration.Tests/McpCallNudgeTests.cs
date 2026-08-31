using System.Text.Json;
using Engram.Cli;

namespace Engram.Integration.Tests;

/// <summary>
/// docs/specs/mcp-param-error-nudge.md §4.1/§6.2: the filter's predicate must be positive —
/// it recognizes argument-binding failure types and nothing else — never a catch-all with an
/// exclusion list. A real server + real client call proves the filter is wired in and fires
/// (Engram.EndToEnd.Tests.McpParamErrorNudgeTests, tier 3, §6.1/§6.4); this proves the predicate
/// itself is positive rather than a disguised `catch (Exception)`, which is what §6.2 is actually
/// about. Driving a genuine mid-flight `OperationCanceledException` through a real MCP call isn't
/// reachable here — every engram tool method is a synchronous, fast, non-cancellable call, so
/// there is no tool whose body can observe a cancellation once dispatched — so this exercises the
/// predicate directly rather than through the pipeline.
/// </summary>
public class McpCallNudgeTests
{
    [Fact]
    public void ArgumentException_IsABindingFailure()
    {
        Assert.True(McpCallNudge.IsArgumentBindingFailure(new ArgumentException("missing required parameter 'query'")));
    }

    [Fact]
    public void ArgumentNullException_IsABindingFailure()
    {
        // ArgumentNullException derives from ArgumentException, so the single `ArgumentException`
        // arm of the predicate already covers it — no separate case needed.
        Assert.True(McpCallNudge.IsArgumentBindingFailure(new ArgumentNullException("query")));
    }

    [Fact]
    public void JsonException_IsABindingFailure()
    {
        Assert.True(McpCallNudge.IsArgumentBindingFailure(new JsonException("The JSON value could not be converted to System.Int32.")));
    }

    [Fact]
    public void NotSupportedException_IsABindingFailure()
    {
        Assert.True(McpCallNudge.IsArgumentBindingFailure(new NotSupportedException("unconvertible type")));
    }

    [Fact]
    public void OperationCanceledException_IsNotABindingFailure()
    {
        Assert.False(McpCallNudge.IsArgumentBindingFailure(new OperationCanceledException()));
    }

    [Fact]
    public void SqliteException_IsNotABindingFailure()
    {
        // A downstream store failure must reach the SDK's own sanitized error, not be relabeled
        // as a schema mismatch (spec §4.1) — the SqliteException type itself is out of reach in
        // Engram.Cli's dependency set, so a plain InvalidOperationException stands in for "any
        // exception a tool body can throw that isn't a binding failure."
        Assert.False(McpCallNudge.IsArgumentBindingFailure(new InvalidOperationException("database is locked")));
    }
}
