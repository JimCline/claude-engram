using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Engram.Cli;

/// <summary>
/// A <c>CallTool</c> filter that turns an argument-binding failure — the SDK's own
/// <c>AIFunction</c> marshaller throwing before a tool method ever runs — into a nudge that
/// points the calling model back at the failing tool's schema, rather than the sanitized
/// "An error occurred invoking '...'" the SDK returns for any non-<c>McpException</c> failure
/// (docs/specs/mcp-param-error-nudge.md §1).
/// </summary>
internal static class McpCallNudge
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            catch (Exception e) when (IsArgumentBindingFailure(e))
            {
                return NudgeResult(request, e);
            }
        };
    }

    // Positive predicate, not a catch-all with an exclusion list (spec §4.1): this filter sits
    // inside the SDK's own catch, so `next(...)` can throw anything any tool raises. Naming the
    // binding-failure types means OperationCanceledException, McpProtocolException, and
    // InputRequiredException are never caught in the first place — no exclusion list to rot.
    internal static bool IsArgumentBindingFailure(Exception e) =>
        e is ArgumentException or JsonException or NotSupportedException;

    private static CallToolResult NudgeResult(RequestContext<CallToolRequestParams> request, Exception e)
    {
        var toolName = request.Params?.Name ?? "(unknown tool)";
        var received = request.Params?.Arguments is { } arguments && arguments.Count > 0
            ? string.Join(", ", arguments.Keys)
            : null;

        var text = received is null
            ? $"{toolName}: the arguments did not match this tool's schema, so nothing ran. "
                + $"Detail: {e.Message} Re-read {toolName}'s inputSchema — its parameter names, "
                + "which are required, and their types — and retry."
            : $"{toolName}: the arguments did not match this tool's schema, so nothing ran. "
                + $"Received: {received}. Detail: {e.Message} Re-read {toolName}'s inputSchema — "
                + "its parameter names, which are required, and their types — and retry.";

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = text }],
        };
    }
}
