namespace Everywhere.AI;

/// <summary>
/// Unifies the reasoning effort level for different AI models,
/// allowing for consistent configuration of reasoning capabilities across various providers.
/// </summary>
public enum ReasoningEffortLevel
{
    [DynamicResourceKey(LocaleKey.ReasoningEffortLevel_Minimal)]
    Minimal = 0,

    [DynamicResourceKey(LocaleKey.ReasoningEffortLevel_Default)]
    Default = 1,

    [DynamicResourceKey(LocaleKey.ReasoningEffortLevel_Detailed)]
    Detailed = 2,
}