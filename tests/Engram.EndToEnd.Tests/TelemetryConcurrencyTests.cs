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

            Assert.Equal(30, lines.Length);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
            }
        }
    }
}
