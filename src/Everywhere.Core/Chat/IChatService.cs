using Everywhere.AI;
using Everywhere.StrategyEngine;

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
    /// Run a sub-agent within the context of the current chat. The sub-agent will have access to the chat history and can send messages back to the main agent.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="customAssistant"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RunSubagentAsync(
        ChatContext chatContext,
        CustomAssistant customAssistant,
        AssistantChatMessage assistantChatMessage,
        CancellationToken cancellationToken);
}