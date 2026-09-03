namespace Engram.Integration.Tests;

/// <summary>
/// Every tier-2 test that feeds a hook verb through <c>Console.SetIn</c> joins this collection.
/// <c>Console.In</c> is process-global, and xunit runs different test classes on parallel threads,
/// so two stdin-reading classes race each other silently — one hook parses the other's payload
/// (or an already-drained reader) and the failure reads as an empty-JSON parse error in whichever
/// test lost. Measured with the two hook classes alone in the filter: 3–5 of 23 red on every run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleStdinCollection
{
    public const string Name = "console-stdin";
}
