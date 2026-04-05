using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for Anthropic models.
/// </summary>
public sealed class AnthropicKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly OptimizedChatClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicKernelMixin"/> class.
    /// </summary>
    public AnthropicKernelMixin(CustomAssistant customAssistant, ModelConnection connection) : base(customAssistant, connection)
    {
        var anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = ApiKey,
                HttpClient = connection.HttpClient,
                BaseUrl = Endpoint,
                Timeout = TimeSpan.FromSeconds(customAssistant.RequestTimeoutSeconds)
            }).AsIChatClient();
        _client = new OptimizedChatClient(customAssistant, ModelId, anthropicClient);
        ChatCompletionService = _client.AsChatCompletionService();
    }

    public override void Dispose()
    {
        _client.Dispose();
    }

    private sealed class OptimizedChatClient(CustomAssistant customAssistant, string modelId, IChatClient anthropicClient)
        : DelegatingChatClient(anthropicClient)
    {
        private void BuildOptions(ref ChatOptions? options)
        {
            var chatOptions = options ??= new ChatOptions();

            double? temperature = customAssistant.Temperature.IsCustomValueSet ? customAssistant.Temperature.ActualValue : null;
            double? topP = customAssistant.TopP.IsCustomValueSet ? customAssistant.TopP.ActualValue : null;

            if (temperature is not null) options.Temperature = (float)temperature.Value;
            if (topP is not null) options.TopP = (float)topP.Value;

            options.RawRepresentationFactory = OptionsRawRepresentationFactory;

            object? OptionsRawRepresentationFactory(IChatClient _)
            {
                var maxTokens = customAssistant.OutputLimit switch
                {
                    > 0 => customAssistant.OutputLimit,
                    _ when modelId.StartsWith("claude-3-haiku") => 4096,
                    _ when modelId.StartsWith("claude-3-5-haiku") => 8192,
                    _ when modelId.StartsWith("claude-opus-4") => 32000,
                    _ when modelId.StartsWith("claude-opus-4-1") => 32000,
                    _ when modelId.StartsWith("claude-opus-4-6") => 128000,
                    _ => 64000,
                };

                ThinkingConfigParam thinking;
                if (customAssistant.SupportsReasoning)
                {
                    int budgetTokens;
                    if (chatOptions.AdditionalProperties?.TryGetValue("reasoning_effort_level", out var reasoningEffortLevelObj) is not true ||
                        reasoningEffortLevelObj is not ReasoningEffortLevel reasoningEffortLevel)
                    {
                        budgetTokens = -1;
                    }
                    else
                    {
                        budgetTokens = reasoningEffortLevel switch
                        {
                            ReasoningEffortLevel.Detailed => Math.Min(maxTokens / 2, 4096),
                            ReasoningEffortLevel.Minimal => 1024,
                            _ => -1
                        };
                    }

                    if (budgetTokens == -1 && modelId.StartsWith("claude-opus-4-6"))
                    {
                        thinking = new ThinkingConfigParam(new ThinkingConfigAdaptive());
                    }
                    else
                    {
                        thinking = new ThinkingConfigParam(
                            new ThinkingConfigEnabled
                            {
                                BudgetTokens = Math.Max(budgetTokens, 2048)
                            });
                    }
                }
                else
                {
                    thinking = new ThinkingConfigParam(new ThinkingConfigDisabled());
                }

                return new MessageCreateParams
                {
                    MaxTokens = maxTokens,
                    Messages = [], // Leave empty and underlying implementation will handle it
                    Model = modelId,
                    Thinking = thinking,
                    CacheControl = new CacheControlEphemeral()
                };
            }
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }
}