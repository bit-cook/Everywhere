using System.ClientModel;
using System.ClientModel.Primitives;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for OpenAI models via Chat Completions.
/// </summary>
public sealed class OpenAIKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public OpenAIKernelMixin(
        CustomAssistant customAssistant,
        HttpClient httpClient,
        ILoggerFactory loggerFactory
    ) : base(customAssistant)
    {
        ChatCompletionService = new OptimizedOpenAIApiClient(
            new ChatClient(
                ModelId,
                // some models don't need API key (e.g. LM Studio)
                new ApiKeyCredential(ApiKey.IsNullOrWhiteSpace() ? "NO_API_KEY" : ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(Endpoint, UriKind.Absolute),
                    Transport = new HttpClientPipelineTransport(httpClient, true, loggerFactory)
                }
            ).AsIChatClient(),
            this
        ).AsChatCompletionService();
    }

    /// <summary>
    /// optimized wrapper around MEAI's IChatClient to extract reasoning content from internal properties.
    /// </summary>
    private sealed class OptimizedOpenAIApiClient(IChatClient client, OpenAIKernelMixin owner) : DelegatingChatClient(client)
    {
        private static readonly PropertyInfo? ChoicesProperty =
            typeof(StreamingChatCompletionUpdate).GetProperty("Choices", BindingFlags.NonPublic | BindingFlags.Instance);
        private static PropertyInfo? _choiceCountProperty;
        private static PropertyInfo? _choiceIndexerProperty;
        private static PropertyInfo? _choiceDeltaProperty;
        private static PropertyInfo? _deltaPatchProperty;

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // cache the value to avoid property changes during enumeration
            var isDeepThinkingSupported = owner.IsDeepThinkingSupported;
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Why you keep reasoning in the fucking internal properties, OpenAI???
                if (isDeepThinkingSupported && update is { Text: not { Length: > 0 }, RawRepresentation: StreamingChatCompletionUpdate detail })
                {
                    // Get the value of the internal 'Choices' property.
                    var choices = ChoicesProperty?.GetValue(detail);
                    if (choices is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the 'Count' property of the Choices collection.
                    _choiceCountProperty ??= choices.GetType().GetProperty("Count");
                    if (_choiceCountProperty?.GetValue(choices) is not int count || count == 0)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the indexer 'Item' property of the Choices collection.
                    _choiceIndexerProperty ??= choices.GetType().GetProperty("Item");
                    if (_choiceIndexerProperty is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Get the first choice from the collection.
                    var firstChoice = _choiceIndexerProperty.GetValue(choices, [0]);
                    if (firstChoice is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the 'Delta' property of a choice.
                    _choiceDeltaProperty ??= firstChoice.GetType().GetProperty("Delta", BindingFlags.Instance | BindingFlags.NonPublic);
                    var delta = _choiceDeltaProperty?.GetValue(firstChoice);
                    if (delta is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the internal 'Patch' property of the delta.
                    _deltaPatchProperty ??= delta.GetType().GetProperty(
                        "Patch",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    // Extract and process the raw data if it exists.
                    if (_deltaPatchProperty?.GetValue(delta) is not JsonPatch jsonPatch ||
                        !jsonPatch.TryGetValue("$.reasoning_content"u8, out string? reasoningContent))
                    {
                        yield return update;
                        continue;
                    }

                    if (string.IsNullOrEmpty(reasoningContent))
                    {
                        yield return update;
                        continue;
                    }

                    update.Contents.Add(
                        new TextContent(reasoningContent)
                        {
                            AdditionalProperties = ReasoningProperties
                        });
                    update.AdditionalProperties = ApplyReasoningProperties(update.AdditionalProperties);
                }

                // Ensure that all FunctionCallContent items have a unique CallId.
                for (var i = 0; i < update.Contents.Count; i++)
                {
                    var item = update.Contents[i];
                    if (item is FunctionCallContent { Name.Length: > 0, CallId: null or { Length: 0 } } missingIdContent)
                    {
                        // Generate a unique ToolCallId for the function call update.
                        update.Contents[i] = new FunctionCallContent(
                            Guid.CreateVersion7().ToString("N"),
                            missingIdContent.Name,
                            missingIdContent.Arguments);
                    }
                }

                yield return update;
            }
        }
    }
}