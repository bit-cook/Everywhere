using Everywhere.AI;

namespace Everywhere.Chat;

public interface IChatService
{
    /// <summary>
    /// Send a message to the chat service. This method is NOT thread safe.
    /// </summary>
    /// <param name="message"></param>
    void SendMessage(UserChatMessage message);

    /// <summary>
    /// Retry sending a message that previously failed. This will create a branch in the chat history. This method is NOT thread safe.
    /// </summary>
    /// <param name="node"></param>
    void Retry(ChatMessageNode node);

    /// <summary>
    /// Edit a previously sent user message. This will create a branch in the chat history. This method is NOT thread safe.
    /// </summary>
    /// <param name="originalNode"></param>
    /// <param name="newMessage"></param>
    void Edit(ChatMessageNode originalNode, UserChatMessage newMessage);

    /// <summary>
    /// Generates a response for the given chat context and assistant chat message.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="assistant"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="systemPromptOverride"></param>
    /// <param name="enableNotifications">Send notifications for function calls and other events during the generation process.</param>
    /// <param name="cancellationToken"></param>
    Task GenerateAsync(
        ChatContext chatContext,
        Assistant assistant,
        AssistantChatMessage assistantChatMessage,
        string? systemPromptOverride = null,
        bool enableNotifications = true,
        CancellationToken cancellationToken = default);
}