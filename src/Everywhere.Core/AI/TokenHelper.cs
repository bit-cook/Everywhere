using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace Everywhere.AI;

public static class TokenHelper
{
    public enum OmitPosition
    {
        Middle,
        Start,
        End
    }

    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>
    /// Approximates the number of LLM tokens for a given string.
    /// </summary>
    /// <param name="text">The input string to calculate the token count for.</param>
    /// <returns>An approximate number of tokens.</returns>
    public static int EstimateTokenCount(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : Tokenizer.CountTokens(text);
    }

    /// <summary>
    /// Omits parts of the input text to ensure the total token count does not exceed the specified maximum.
    /// Note that omitText is not in count
    /// </summary>
    /// <param name="text"></param>
    /// <param name="maxTokenCount"></param>
    /// <param name="omitText"></param>
    /// <param name="position"></param>
    /// <returns></returns>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Omit(
        string? text,
        int maxTokenCount = 8000,
        string omitText = "[... OMITTED ...]",
        OmitPosition position = OmitPosition.Middle)
    {
        if (string.IsNullOrEmpty(text) || Tokenizer.CountTokens(text) <= maxTokenCount) return text;

        return position switch
        {
            OmitPosition.Middle => string.Concat(
                text.AsSpan(0, Tokenizer.GetIndexByTokenCount(text, maxTokenCount / 2, out _, out _)),
                omitText,
                text.AsSpan(Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount / 2, out _, out _))),
            OmitPosition.Start => string.Concat(omitText, text.AsSpan(Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount, out _, out _))),
            OmitPosition.End => string.Concat(text.AsSpan(0, Tokenizer.GetIndexByTokenCount(text, maxTokenCount, out _, out _)), omitText),
            _ => text
        };
    }

    /// <summary>
    /// Omits parts of the input text to ensure the total token count does not exceed the specified maximum.
    /// Note that omitText is not in count
    /// </summary>
    /// <param name="text"></param>
    /// <param name="resultBuilder"></param>
    /// <param name="maxTokenCount"></param>
    /// <param name="omitText"></param>
    /// <param name="position"></param>
    /// <returns></returns>
    public static void OmitTo(
        string? text,
        StringBuilder resultBuilder,
        int maxTokenCount = 8000,
        string omitText = "[... OMITTED ...]",
        OmitPosition position = OmitPosition.Middle)
    {
        if (string.IsNullOrEmpty(text) || Tokenizer.CountTokens(text) <= maxTokenCount)
        {
            resultBuilder.Append(text);
            return;
        }

        switch (position)
        {
            case OmitPosition.Middle:
            {
                resultBuilder.Append(text, 0, Tokenizer.GetIndexByTokenCount(text, maxTokenCount / 2, out _, out _));
                resultBuilder.Append(omitText);
                var endIndex = Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount / 2, out _, out _);
                resultBuilder.Append(text, endIndex, endIndex - endIndex);
                break;
            }
            case OmitPosition.Start:
            {
                resultBuilder.Append(omitText);
                var endIndex = Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount, out _, out _);
                resultBuilder.Append(text, endIndex, endIndex - endIndex);
                break;
            }
            case OmitPosition.End:
            {
                resultBuilder.Append(text, 0, Tokenizer.GetIndexByTokenCount(text, maxTokenCount, out _, out _));
                resultBuilder.Append(omitText);
                break;
            }
            default:
            {
                resultBuilder.Append(text);
                break;
            }
        }
    }
}