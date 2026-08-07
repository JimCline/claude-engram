using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engram.Core;

/// <param name="StartToken">
/// The server's own view of what identifies this run of it (see <see cref="ProcessStartToken"/>).
/// It travels in the payload rather than being read from the pid by whoever records it: between a
/// health response and a separate lookup the pid could in principle be recycled, and the payload
/// has no such window. Null from a server that predates tokens.
/// </param>
public sealed record HealthResponsePayload(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("start_time")] DateTimeOffset StartTimeUtc,
    [property: JsonPropertyName("start_token")] string? StartToken = null);

[JsonSerializable(typeof(HealthResponsePayload))]
public sealed partial class HealthResponseJsonContext : JsonSerializerContext;

public enum HealthCheckStatus
{
    NoResponse,
    Healthy,
    Unrecognized,
}

public sealed record HealthCheckOutcome(HealthCheckStatus Status, HealthResponsePayload? Result);

public interface IServerHealthChecker
{
    HealthCheckOutcome Check(int port, TimeSpan timeout);
}

public sealed class HttpServerHealthChecker : IServerHealthChecker
{
    public HealthCheckOutcome Check(int port, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = timeout };

        HttpResponseMessage response;
        try
        {
            response = client.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return new HealthCheckOutcome(HealthCheckStatus.NoResponse, null);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new HealthCheckOutcome(HealthCheckStatus.Unrecognized, null);
            }

            string body;
            try
            {
                body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                return new HealthCheckOutcome(HealthCheckStatus.Unrecognized, null);
            }

            try
            {
                var payload = JsonSerializer.Deserialize(body, HealthResponseJsonContext.Default.HealthResponsePayload);
                return payload is null
                    ? new HealthCheckOutcome(HealthCheckStatus.Unrecognized, null)
                    : new HealthCheckOutcome(HealthCheckStatus.Healthy, payload);
            }
            catch (JsonException)
            {
                return new HealthCheckOutcome(HealthCheckStatus.Unrecognized, null);
            }
        }
    }
}
