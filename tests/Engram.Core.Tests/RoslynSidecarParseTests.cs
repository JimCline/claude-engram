namespace Engram.Core.Tests;

/// <summary>
/// D-code-nav B1/item 26: <see cref="RoslynSidecar.Parse"/> resolves a call's
/// <c>enclosing_id</c> through an id map, never an array position — <see
/// cref="DeepTier.Fragments"/> can drop a symbol (empty name) independently of what the
/// wire sent, so a positional zip would silently misattribute every call whose enclosing
/// symbol comes after a gap.
/// </summary>
public class RoslynSidecarParseTests
{
    [Fact]
    public void EnclosingId_ResolvesByExplicitId_AcrossAGapInTheIdSequence()
    {
        // ids 0 and 2 are real symbols; id 1 is missing from the wire entirely, standing in
        // for a symbol Fragments() would drop — a positional zip (symbols[1] means "the
        // second entry") would resolve the call below to "Outer" instead of "Inner".
        var line = """
            {"path":"a.cs","symbols":[
              {"id":0,"name":"Outer","kind":"type","declaration":"class Outer"},
              {"id":2,"name":"Inner","kind":"method","declaration":"void Inner()"}
            ],"calls":[
              {"callee":"Target","line":1,"enclosing_id":2}
            ]}
            """;

        var analysis = RoslynSidecar.Parse(line);

        Assert.NotNull(analysis);
        var call = Assert.Single(analysis.Calls);
        Assert.Equal("Inner", call.EnclosingFragment);
    }

    [Fact]
    public void EnclosingId_Absent_LeavesTheCallUnenclosed()
    {
        var line = """{"path":"a.cs","symbols":[{"id":0,"name":"Outer","kind":"type","declaration":"class Outer"}],"calls":[{"callee":"Target","line":1}]}""";

        var analysis = RoslynSidecar.Parse(line);

        Assert.NotNull(analysis);
        Assert.Null(Assert.Single(analysis.Calls).EnclosingFragment);
    }
}
