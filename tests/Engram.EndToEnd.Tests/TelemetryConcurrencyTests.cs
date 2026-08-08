using System.Text.Json;

namespace Engram.EndToEnd.Tests;

[Collection("Hook execution")]
public class TelemetryConcurrencyTests
{
    [Fact(Timeout = 300_000)]
    public async Task SessionStart_ThirtyConcurrentProcesses_AllThirtyTelemetryRecordsLand_AcrossFiveRounds()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        for (var round = 0; round < 5; round++)
        {
            using var home = new TestHome();

            var runs = Enumerable.Range(0, 30)
                .Select(_ => Task.Run(() => EngramProcess.Run(home.Root, "hook", "session-start")));
            var results = await Task.WhenAll(runs);

            Assert.All(results, r => Assert.Equal(0, r.ExitCode));

            var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
            var lines = File.ReadAllLines(telemetryPath);

            // Every line must parse, including the maintenance child's — that half is about
            // DurableAppend not interleaving two writers into one corrupt record, and more
            // concurrent writers makes it a stronger check, not a weaker one.
            var kinds = new List<string?>();
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                kinds.Add(document.RootElement.GetProperty("kind").GetString());
            }

            // The count is about these thirty processes. Session start also spawns the detached
            // maintenance child, which records its own indexing, so a total-line count would be
            // asserting on how much of someone else's work happened to land first.
            Assert.Equal(30, kinds.Count(kind => kind == "session-start"));
        }
    }
}
