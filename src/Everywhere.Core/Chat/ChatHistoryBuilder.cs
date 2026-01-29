using Everywhere.Common;
using Everywhere.Utilities;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;
using ZLinq;

namespace Everywhere.Chat;

/// <summary>
/// Builds ChatHistory (SK) from ChatMessages (Everywhere).
/// </summary>
public static class ChatHistoryBuilder
{
    public static async ValueTask<ChatHistory> BuildChatHistoryAsync(
        IEnumerable<ChatMessage> chatMessages,
        CancellationToken cancellationToken = default)
    {
        var chatHistory = new ChatHistory();
        foreach (var chatMessage in chatMessages)
        {
            await foreach (var chatMessageContent in CreateChatMessageContentsAsync(chatMessage, cancellationToken))
            {
                chatHistory.Add(chatMessageContent);
            }
        }

        return chatHistory;
    }

    /// <summary>
    /// Creates chat message contents from a chat message.
    /// </summary>
    /// <param name="chatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async IAsyncEnumerable<ChatMessageContent> CreateChatMessageContentsAsync(
        ChatMessage chatMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (chatMessage)
        {
            case SystemChatMessage system:
            {
                yield return new ChatMessageContent(AuthorRole.System, system.SystemPrompt);
                break;
            }
            case AssistantChatMessage assistant:
            {
                // assistant.Items property is IReadOnlyList of a SourceList, which is copied for thread safety.
                var spans = assistant.Items;
                var metadata = assistant.Metadata;
                if (spans.AsValueEnumerable()
                        .OfType<AssistantChatMessageReasoningSpan>()
                        .Select(s => s.ReasoningOutput)
                        .LastOrDefault(r => !r.IsNullOrEmpty()) is { } reasoningOutput)
                {
                    // If any span has reasoning output, add it to the assistant message metadata.
                    // Add this into the top-level assistant message metadata for compatibility with DeepSeek requirements.
                    // We take last reasoning output in case of multiple reasoning spans. For example:
                    // - Reasoning: I should look up the weather.
                    // - [Tool call: get_weather]
                    // - Reasoning: The weather returns error. I have make a mistake. Let me try again.
                    // - [Tool call: get_weather_v2]
                    // At this point, we want to take the last reasoning output.
                    metadata ??= new MetadataDictionary();
                    metadata["reasoning_content"] = reasoningOutput;
                }

                foreach (var span in assistant.Items)
                {
                    var items = new ChatMessageContentItemCollection();
                    switch (span)
                    {
                        case AssistantChatMessageTextSpan { Content: { Length: > 0 } content }:
                        {
                            items.Add(new TextContent(content, metadata: span.Metadata));
                            break;
                        }
                        case AssistantChatMessageFunctionCallSpan { Items: { Count: > 0 } functionCalls }:
                        {
                            // First, yield any accumulated items before the function calls.
                            if (items.Count > 0)
                            {
                                yield return new ChatMessageContent(AuthorRole.Assistant, items, metadata: metadata);

                                // Clear items for function call contents.
                                items = [];
                            }

                            foreach (var functionCallChatMessage in functionCalls)
                            {
                                await foreach (var actionChatMessageContent in CreateChatMessageContentsAsync(
                                                   functionCallChatMessage,
                                                   cancellationToken))
                                {
                                    if (metadata is not null)
                                    {
                                        actionChatMessageContent.Metadata ??= new MetadataDictionary();
                                        var nestedMetadata = (MetadataDictionary)actionChatMessageContent.Metadata;
                                        foreach (var (key, value) in metadata)
                                        {
                                            nestedMetadata[key] = value;
                                        }
                                    }

                                    yield return actionChatMessageContent;
                                }
                            }
                            break;
                        }
                        case AssistantChatMessageImageSpan { ImageOutput: { } imageOutput }:
                        {
                            try
                            {
                                var imageData = await File.ReadAllBytesAsync(imageOutput.FilePath, cancellationToken);
                                items.Add(
                                    new ImageContent(imageData, imageOutput.MimeType)
                                    {
                                        Metadata = span.Metadata
                                    });
                            }
                            catch
                            {
                                items.Add(new TextContent("The image is generated but failed to be read from disk.", metadata: span.Metadata));
                            }
                            break;
                        }
                    }

                    if (items.Count > 0)
                    {
                        yield return new ChatMessageContent(AuthorRole.Assistant, items, metadata: metadata);
                    }
                }
                break;
            }
            case UserChatMessage user:
            {
                var items = new ChatMessageContentItemCollection();
                foreach (var chatAttachment in user.Attachments.AsValueEnumerable().ToList())
                {
                    await PopulateKernelContentsAsync(chatAttachment, items, cancellationToken);
                }

                if (items.Count > 0)
                {
                    // If there are attachments, add the user content as a separate item.
                    items.Add(
                        new TextContent(
                            $"""
                             <UserRequestStart/>
                             {user.Content}
                             """));
                }
                else
                {
                    // No attachments, just add the content directly.
                    items.Add(new TextContent(user.Content));
                }

                yield return new ChatMessageContent(AuthorRole.User, items);
                break;
            }
            case FunctionCallChatMessage functionCall:
            {
                var functionCallMessage = new ChatMessageContent(AuthorRole.Assistant, content: null);
                functionCallMessage.Items.AddRange(functionCall.Calls);
                yield return functionCallMessage;

                // ReSharper disable once ForCanBeConvertedToForeach
                // foreach would create an enumerator object, which will cause thread lock issues.
                for (var callIndex = 0; callIndex < functionCall.Calls.Count; callIndex++)
                {
                    var callId = functionCall.Calls[callIndex].Id;
                    if (callId.IsNullOrEmpty())
                    {
                        throw new InvalidOperationException("Function call ID cannot be null or empty when creating chat message contents.");
                    }

                    var resultContent = functionCall.Results.AsValueEnumerable().FirstOrDefault(r => r.CallId == callId);
                    yield return resultContent?.ToChatMessage() ?? new ChatMessageContent(
                        AuthorRole.Tool,
                        [
                            new FunctionResultContent(
                                functionCall.Calls[callIndex],
                                $"Error: No result found for function call ID '{callId}'. " +
                                $"This may caused by an error during function execution or user cancellation.")
                        ]);

                    // If the function call result is a ChatAttachment, add it as extra attachment message(s).
                    if (resultContent?.Result is not ChatAttachment extraToolCallResult) break;

                    var items = new ChatMessageContentItemCollection { new TextContent("<ExtraToolCallResultAttachments>") };
                    await PopulateKernelContentsAsync(extraToolCallResult, items, cancellationToken);

                    // No valid attachment added
                    if (items.Count == 1) break;

                    items.Add(new TextContent("</ExtraToolCallResultAttachments>"));
                    yield return new ChatMessageContent(AuthorRole.User, items);
                }

                break;
            }
            case { Role.Label: "system" or "user" or "developer" or "tool" }:
            {
                yield return new ChatMessageContent(chatMessage.Role, chatMessage.ToString());
                break;
            }
        }
    }

    /// <summary>
    /// Creates KernelContent from a chat attachment, and adds them to the contents list.
    /// </summary>
    /// <param name="chatAttachment"></param>
    /// <param name="contents"></param>
    /// <param name="cancellationToken"></param>
    private static async ValueTask PopulateKernelContentsAsync(
        ChatAttachment chatAttachment,
        ChatMessageContentItemCollection contents,
        CancellationToken cancellationToken)
    {
        switch (chatAttachment)
        {
            case ChatTextSelectionAttachment textSelection:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="text-selection">
                         <Text>
                         {textSelection.Text}
                         </Text>
                         <AssociatedElement>
                         {textSelection.Content ?? "omitted due to duplicate"}
                         </AssociatedElement>
                         </Attachment>
                         """));
                break;
            }
            case ChatVisualElementAttachment visualElement:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="visual-element">
                         {visualElement.Content ?? "omitted due to duplicate"}
                         </Attachment>
                         """));
                break;
            }
            case ChatTextAttachment text:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="text">
                         {text}
                         </Attachment>
                         """));
                break;
            }
            case ChatFileAttachment file:
            {
                var fileInfo = new FileInfo(file.FilePath);
                if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > 25 * 1024 * 1024) // TODO: Configurable max file size?
                {
                    return;
                }

                byte[] data;
                try
                {
                    data = await File.ReadAllBytesAsync(file.FilePath, cancellationToken);
                }
                catch (Exception ex)
                {
                    // If we fail to read the file, just skip it.
                    // The file might be deleted or moved.
                    // We don't want to fail the whole message because of one attachment.
                    // Just log the error and continue.
                    ex = HandledSystemException.Handle(ex, true); // treat all as expected
                    Log.ForContext(typeof(ChatHistoryBuilder)).Warning(ex, "Failed to read attachment file '{FilePath}'", file.FilePath);
                    return;
                }

                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="file" path="{file.FilePath}" mimeType="{file.MimeType}" description="{file.Description}">
                         """));
                contents.Add(
                    FileUtilities.GetCategory(file.MimeType) switch
                    {
                        FileTypeCategory.Audio => new AudioContent(data, file.MimeType),
                        FileTypeCategory.Image => new ImageContent(data, file.MimeType),
                        _ => new BinaryContent(data, file.MimeType)
                    });
                contents.Add(new TextContent("</Attachment>"));
                break;
            }
        }
    }
}