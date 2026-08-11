namespace Engram.Core;

public static class TokenEstimator
{
    public const double CharactersPerToken = 3.6;

    public static int Estimate(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharactersPerToken);
}
