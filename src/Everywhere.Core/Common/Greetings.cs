namespace Everywhere.Common;

/// <summary>
/// Common greeting messages. Including Tips and holiday greetings.
/// </summary>
public static class Greetings
{
    private static ReadOnlySpan<string> Tips => new[]
    {
        LocaleKey.Greetings_Tip1,
        LocaleKey.Greetings_Tip2,
        LocaleKey.Greetings_Tip3,
    };

    public static DynamicResourceKey GetRandomTip()
    {
        var tips = Tips;
        return new DynamicResourceKey(tips[Random.Shared.Next(tips.Length)]);
    }
}