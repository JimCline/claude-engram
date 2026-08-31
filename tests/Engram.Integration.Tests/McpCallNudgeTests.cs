using System.Text.Json;
using System.Threading.Channels;
using Engram.Cli;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

    // F1 (Reviewer, review-mcp-param-error-nudge): the tests above exercise the predicate
    // function directly, so widening McpCallNudge.Filter's own `catch (Exception e) when (...)`
    // to a bare `catch (Exception e)` would pass all of them undetected — exactly the hazard
    // §4.1 names. This one goes through Filter itself.
    [Fact]
    public async Task Filter_ANonBindingFailure_PropagatesWithoutBeingCaught()
    {
        var request = BuildRequest("engram_test_tool", arguments: null);
        var filtered = McpCallNudge.Filter((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await filtered(request, CancellationToken.None));
    }

    [Fact]
    public async Task Filter_ABindingFailure_ReturnsTheNudgeInsteadOfPropagating()
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["view"] = JsonDocument.Parse("\"history\"").RootElement,
        };
        var request = BuildRequest("engram_expand", arguments);
        var filtered = McpCallNudge.Filter(
            (_, _) => throw new ArgumentException("The arguments dictionary is missing a value for the required parameter 'fact_id'."));

        var result = await filtered(request, CancellationToken.None);

        Assert.True(result.IsError);
        var text = Assert.Single(result.Content) is TextContentBlock block ? block.Text : null;
        Assert.NotNull(text);
        Assert.Contains("engram_expand", text);
        Assert.Contains("the arguments did not match this tool's schema, so nothing ran", text);
        Assert.Contains("Received: view", text);
    }

    // Minimal but real RequestContext<CallToolRequestParams>: NudgeResult dereferences
    // request.Params on the matching path, so a null request would NRE rather than exercise the
    // code under test. McpServer.Create needs only an ITransport and never starts a message loop
    // here (Filter/NudgeResult touch neither), so a no-op transport is enough.
    private static RequestContext<CallToolRequestParams> BuildRequest(string toolName, IDictionary<string, JsonElement>? arguments)
    {
        var server = McpServer.Create(new NoopTransport(), new McpServerOptions());
        var jsonRpcRequest = new JsonRpcRequest { Method = "tools/call", Id = new RequestId(1) };
        var callParams = new CallToolRequestParams { Name = toolName, Arguments = arguments };
        return new RequestContext<CallToolRequestParams>(server, jsonRpcRequest, callParams);
    }

    private sealed class NoopTransport : ITransport
    {
        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader { get; } = Channel.CreateUnbounded<JsonRpcMessage>().Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
