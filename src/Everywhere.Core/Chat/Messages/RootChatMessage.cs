using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class RootChatMessage : ChatMessage
{
    public static RootChatMessage Shared { get; } = new();

    public override AuthorRole Role => AuthorRole.System;
}