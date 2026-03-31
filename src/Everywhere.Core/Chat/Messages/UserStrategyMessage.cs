using Everywhere.StrategyEngine;
using MessagePack;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class UserStrategyMessage : UserChatMessage
{
    [Key(4)]
    public StrategyCommand StrategyCommand { get; }

    public UserStrategyMessage(
        string content,
        IReadOnlyList<ChatAttachment> attachments,
        StrategyCommand strategyCommand) : base(content, attachments)
    {
        StrategyCommand = strategyCommand;
    }

    [SerializationConstructor]
    private UserStrategyMessage(
        string content,
        IReadOnlyList<ChatAttachment> attachments,
        DateTimeOffset createdAt,
        StrategyCommand strategyCommand) : base(content, attachments)
    {
        CreatedAt = createdAt;
        StrategyCommand = strategyCommand;
    }
}