using Tiktoken;
using Tiktoken.Encodings;

namespace Everywhere.Chat;

public static class TokenCounter
{
    private static readonly Encoder Encoder = new(new O200KBase());

    /// <summary>
    /// Approximates the number of LLM tokens for a given string.
    /// </summary>
    /// <param name="text">The input string to calculate the token count for.</param>
    /// <returns>An approximate number of tokens.</returns>
    public static int EstimateTokenCount(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : Encoder.CountTokens(text);
    }
}