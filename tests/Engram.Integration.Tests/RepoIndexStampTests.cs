using Engram.Core;

namespace Engram.Integration.Tests;

public class RepoIndexStampTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static string StampPath(SandboxHome sandbox) => sandbox.Home.RepoIndexStampPath;

    [Fact]
    public void NoFile_ReadsNull()
    {
        using var sandbox = new SandboxHome();

        Assert.Null(RepoIndexStamp.Read(StampPath(sandbox), Path.Combine(sandbox.Home.Root, "checkout")));
    }

    [Fact]
    public void EnrollThenIndexed_FoldsToEnrolledWithBothTimes()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");

        RepoIndexStamp.Append(StampPath(sandbox), T0, root, "id-a", "enroll");
        RepoIndexStamp.Append(StampPath(sandbox), T0.AddMinutes(5), root, "id-a", RepoIndexStamp.Indexed);

        var row = RepoIndexStamp.Read(StampPath(sandbox), root);
        Assert.NotNull(row);
        Assert.Equal("id-a", row.Identity);
        Assert.Equal(RepoEnrollmentState.Enrolled, row.State);
        Assert.Equal(T0.ToUnixTimeSeconds(), row.DecidedAt);
        Assert.Equal(T0.AddMinutes(5).ToUnixTimeSeconds(), row.LastIndexedAt);
    }

    [Fact]
    public void EnrollWithoutIndexed_HasNoIndexTime()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");

        RepoIndexStamp.Append(StampPath(sandbox), T0, root, "id-a", "enroll");

        var row = RepoIndexStamp.Read(StampPath(sandbox), root);
        Assert.Equal(RepoEnrollmentState.Enrolled, row?.State);
        Assert.Null(row?.LastIndexedAt);
    }

    [Theory]
    [InlineData("decline", RepoEnrollmentState.Declined)]
    [InlineData("later", RepoEnrollmentState.Deferred)]
    public void LaterDecision_ReplacesStateAndKeepsIndexTime(string decision, RepoEnrollmentState expected)
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");

        RepoIndexStamp.Append(StampPath(sandbox), T0, root, "id-a", "enroll");
        RepoIndexStamp.Append(StampPath(sandbox), T0.AddMinutes(1), root, "id-a", RepoIndexStamp.Indexed);
        RepoIndexStamp.Append(StampPath(sandbox), T0.AddMinutes(2), root, "id-a", decision);

        var row = RepoIndexStamp.Read(StampPath(sandbox), root);
        Assert.Equal(expected, row?.State);
        Assert.Equal(T0.AddMinutes(2).ToUnixTimeSeconds(), row?.DecidedAt);
        Assert.Equal(T0.AddMinutes(1).ToUnixTimeSeconds(), row?.LastIndexedAt);
    }

    [Fact]
    public void Reset_ClearsStateAndIndexTime()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");

        RepoIndexStamp.Append(StampPath(sandbox), T0, root, "id-a", "enroll");
        RepoIndexStamp.Append(StampPath(sandbox), T0.AddMinutes(1), root, "id-a", RepoIndexStamp.Indexed);
        RepoIndexStamp.Append(StampPath(sandbox), T0.AddMinutes(2), root, "id-a", "reset");

        var row = RepoIndexStamp.Read(StampPath(sandbox), root);
        Assert.NotNull(row);
        Assert.Null(row.State);
        Assert.Null(row.LastIndexedAt);
    }

    [Fact]
    public void OtherRootsLines_AreIgnored()
    {
        using var sandbox = new SandboxHome();
        var a = Path.Combine(sandbox.Home.Root, "a");
        var b = Path.Combine(sandbox.Home.Root, "b");

        RepoIndexStamp.Append(StampPath(sandbox), T0, a, "id-a", "enroll");
        RepoIndexStamp.Append(StampPath(sandbox), T0, a, "id-a", RepoIndexStamp.Indexed);
        RepoIndexStamp.Append(StampPath(sandbox), T0, b, "id-b", "decline");

        Assert.Equal(RepoEnrollmentState.Enrolled, RepoIndexStamp.Read(StampPath(sandbox), a)?.State);
        Assert.Equal(RepoEnrollmentState.Declined, RepoIndexStamp.Read(StampPath(sandbox), b)?.State);
        Assert.Null(RepoIndexStamp.Read(StampPath(sandbox), Path.Combine(sandbox.Home.Root, "c")));
    }

    [Fact]
    public void MalformedLines_AreSkippedNotFatal()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");

        File.WriteAllText(StampPath(sandbox), "garbage\nnot\ta\tvalid\tline\tat\tall\n");
        RepoIndexStamp.Append(StampPath(sandbox), T0, root, "id-a", "enroll");

        Assert.Equal(RepoEnrollmentState.Enrolled, RepoIndexStamp.Read(StampPath(sandbox), root)?.State);
    }
}
