using System.Text.Json;
using HarmonyLib;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace Everywhere.Patches.SemanticKernel;

/// <summary>
/// The original `ChatResponseUpdateExtensions.ToStreamingChatMessageContent` method does not properly handle the case where the update contains a mix of content types, such as text and function calls.
/// This patch ensures that all content items are processed and included in the resulting `StreamingChatMessageContent`, allowing for richer and more accurate streaming chat messages that reflect the full range of content provided by the model.
/// Additionally, it ensures that metadata and raw representations are preserved for each content item, which is crucial for downstream processing and accurate rendering of the chat message.
/// </summary>
internal static class ChatResponseUpdateExtensions_ToStreamingChatMessageContent
{
    public static void Patch(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(ChatResponseUpdateExtensions), nameof(ChatResponseUpdateExtensions.ToStreamingChatMessageContent));
        harmony.Patch(original, new HarmonyMethod(Prefix));
    }

    // ReSharper disable once InconsistentNaming
    // ReSharper disable once RedundantAssignment
    private static bool Prefix(ref ChatResponseUpdate update, ref StreamingChatMessageContent __result)
    {
        StreamingChatMessageContent content = new(
            update.Role is not null ? new AuthorRole(update.Role.Value.Value) : null,
            null)
        {
            InnerContent = update.RawRepresentation,
            Metadata = update.AdditionalProperties,
            ModelId = update.ModelId
        };

        foreach (var item in update.Contents)
        {
            StreamingKernelContent? resultContent =
                item switch
                {
                    TextContent tc => new StreamingTextContent(tc.Text),
                    FunctionCallContent fcc => new StreamingFunctionCallUpdateContent(
                        fcc.CallId,
                        fcc.Name,
                        fcc.Arguments is not null ?
                            JsonSerializer.Serialize(fcc.Arguments, AbstractionsJsonContext.Default.IDictionaryStringObject) :
                            null),
                    TextReasoningContent trc => new StreamingReasoningContent(trc.Text),
                    _ => null
                };

            if (resultContent is not null)
            {
                resultContent.Metadata = item.AdditionalProperties;
                resultContent.InnerContent = item.RawRepresentation;
                resultContent.ModelId = update.ModelId;
                content.Items.Add(resultContent);
            }

            if (item is UsageContent uc)
            {
                content.Metadata = new Dictionary<string, object?>(update.AdditionalProperties ?? [])
                {
                    ["Usage"] = uc
                };
            }
        }

        __result = content;
        return false;
    }
}