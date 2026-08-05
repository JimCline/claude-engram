namespace Engram.Core;

public static class ServerPort
{
    public const int Default = 7433;

    public static int Resolve(int? explicitPort) =>
        Resolve(explicitPort, Environment.GetEnvironmentVariable("ENGRAM_PORT"));

    public static int Resolve(int? explicitPort, string? environmentPort)
    {
        if (explicitPort is { } port)
        {
            return port;
        }

        if (!string.IsNullOrWhiteSpace(environmentPort)
            && int.TryParse(environmentPort, out var parsed)
            && parsed is > 0 and <= 65535)
        {
            return parsed;
        }

        return Default;
    }
}
