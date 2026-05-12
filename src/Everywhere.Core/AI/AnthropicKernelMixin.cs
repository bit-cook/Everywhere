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

    public override bool IsPersistentSpanMetadataKey(string key) => key == "ProtectedData";

    public override void Dispose()
    {
        _client.Dispose();
    }

    private sealed class OptimizedChatClient(IChatClient originalClient, AnthropicKernelMixin owner) : DelegatingChatClient(originalClient)
    {
        private void BuildOptions(ref ChatOptions? options)
        {
            options ??= new ChatOptions();
            options.RawRepresentationFactory = RawRepresentationFactory;

            if (owner.Temperature is { } temperature) options.Temperature = (float)temperature;
            if (owner.TopP is { } topP) options.TopP = (float)topP;
        }

        private MessageCreateParams RawRepresentationFactory(IChatClient _)
        {
            var maxTokens = owner.OutputLimit switch
            {
                > 0 => owner.OutputLimit,
                _ => 4096,
            };

            ThinkingConfigParam? thinkingConfigParam = null;
            if (owner.ThinkingType?.Equals("disabled", StringComparison.OrdinalIgnoreCase) is true)
            {
                thinkingConfigParam = new ThinkingConfigParam(new ThinkingConfigDisabled());
            }
            else if (long.TryParse(owner.ThinkingBudget, out var thinkingBudget))
            {
                thinkingConfigParam = new ThinkingConfigParam(new ThinkingConfigEnabled { BudgetTokens = thinkingBudget });
            }

            OutputConfig? outputConfig = null;
            if (owner.ReasoningEffort is { Length: > 0 } reasoningEffort)
            {
                outputConfig = new OutputConfig
                {
                    Effort = reasoningEffort
                };
            }

            return new MessageCreateParams
            {
                MaxTokens = maxTokens,
                Messages = [], // Leave empty and underlying implementation will handle it
                Model = owner.ModelId,
                Thinking = thinkingConfigParam,
                OutputConfig = outputConfig,
                CacheControl = new CacheControlEphemeral()
            };
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