using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Global strategy that provides always-available commands.
/// These commands work regardless of context and have lower priority.
/// </summary>
public sealed class GlobalStrategy : StrategyBase, IBuiltInStrategy
{
    public override string Id => "builtin.global";
    public override IDynamicResourceKey Name { get; } = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_Name);
    public override IDynamicResourceKey Description { get; } = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_Description);
    public override int Priority => -100; // Low priority, context-specific strategies take precedence

    protected override IStrategyCondition Condition =>
        new HasAttachmentsCondition { MinCount = 1 };

    public override IEnumerable<StrategyCommand> GetCommands(StrategyContext context) =>
    [
        // "What is this?" - Universal explanation command
        new()
        {
            Id = "what-is-this",
            Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_WhatIsThisCommand_Name),
            Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_WhatIsThisCommand_Description),
            Icon = LucideIconKind.Info,
            Priority = -100,
            SystemPrompt =
                """
                You are a helpful assistant that explains things clearly and concisely.
                The user has selected something and wants to understand what it is.
                Provide a clear, informative explanation appropriate to the context.
                If it's code, explain what it does.
                If it's text, summarize or explain its meaning.
                If it's a UI element, describe its purpose and how to use it.
                """,
            UserMessage = "What is this? Please explain."
        },

        // "Summarize" - Universal summarization command
        new()
        {
            Id = "summarize",
            Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_SummarizeCommand_Name),
            Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_SummarizeCommand_Description),
            Icon = LucideIconKind.FileText,
            Priority = -90,
            SystemPrompt =
                """
                You are a helpful assistant that creates clear, concise summaries.
                Summarize the provided content, highlighting the key points.
                Be concise but comprehensive.
                Use bullet points for multiple items when appropriate.
                """,
            UserMessage = "Please summarize this content."
        },

        // "Help me with this" - Open-ended assistance
        new()
        {
            Id = "help",
            Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_HelpCommand_Name),
            Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_Global_HelpCommand_Description),
            Icon = LucideIconKind.Sparkles,
            Priority = -80,
            SystemPrompt =
                """
                You are a helpful AI assistant.
                The user needs help with something they're currently looking at or working on.
                Analyze the context and provide relevant assistance.
                Ask clarifying questions if needed to better understand their needs.
                """,
            UserMessage = "I need help with this. What can you tell me or do for me?"
        }
    ];
}