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
    public AnthropicKernelMixin(Assistant assistant, ModelConnection connection) : base(assistant, connection)
    {
        _client = new OptimizedChatClient(
            new AnthropicClient(
                new ClientOptions
                {
                    ApiKey = ApiKey,
                    HttpClient = connection.HttpClient,
                    BaseUrl = Endpoint,
                    Timeout = TimeSpan.FromSeconds(Math.Clamp(assistant.RequestTimeoutSeconds, 1, 24 * 60 * 60))
                }).AsIChatClient(),
            this);
        ChatCompletionService = _client.AsChatCompletionService();
    }

    public override void Dispose()
    {
        _client.Dispose();
    }

    private sealed class OptimizedChatClient(IChatClient originalClient, AnthropicKernelMixin owner) : DelegatingChatClient(originalClient)
    {
        private void BuildOptions(ref ChatOptions? options)
        {
            var chatOptions = options ??= new ChatOptions();

            if (owner.Temperature is { } temperature) options.Temperature = (float)temperature;
            if (owner.TopP is { } topP) options.TopP = (float)topP;

            options.RawRepresentationFactory = OptionsRawRepresentationFactory;

            object? OptionsRawRepresentationFactory(IChatClient _)
            {
                var maxTokens = owner.OutputLimit switch
                {
                    > 0 => owner.OutputLimit,
                    _ when owner.ModelId.StartsWith("claude-3-haiku") => 4096,
                    _ when owner.ModelId.StartsWith("claude-3-5-haiku") => 8192,
                    _ when owner.ModelId.StartsWith("claude-opus-4") => 32000,
                    _ when owner.ModelId.StartsWith("claude-opus-4-1") => 32000,
                    _ when owner.ModelId.StartsWith("claude-opus-4-6") => 128000,
                    _ => 64000,
                };

                ThinkingConfigParam thinking;
                if (owner.SupportsReasoning)
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

                    if (budgetTokens == -1 && (owner.ModelId.StartsWith("claude-opus-4-6") || owner.ModelId.StartsWith("claude-opus-4-7")))
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
                    Model = owner.ModelId,
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