namespace Everywhere.Common;

/// <summary>
/// Common greeting messages. Including Tips and holiday greetings.
/// </summary>
public static class Greetings
{
    private static ReadOnlySpan<string> Tips => new[]
    {
        LocaleKey.Greetings_Tip1
    };

    public static DynamicResourceKey GetRandomTip()
    {
        var tips = Tips;
        return new DynamicResourceKey(tips[Random.Shared.Next(tips.Length)]);
    }
}