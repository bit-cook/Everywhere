using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for Anthropic models.
/// </summary>
public sealed partial class AnthropicKernelMixin : KernelMixin
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

    private sealed partial class OptimizedChatClient(IChatClient originalClient, AnthropicKernelMixin owner) : DelegatingChatClient(originalClient)
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

            var isClaude = owner.ModelId.Contains("claude", StringComparison.OrdinalIgnoreCase);
            var isClaude46 = isClaude && Claude46Regex().IsMatch(owner.ModelId);
            var isClaude47 = isClaude && Claude47Regex().IsMatch(owner.ModelId);

            ThinkingConfigParam thinkingConfigParam;
            if (owner.ThinkingType?.Equals("disabled", StringComparison.OrdinalIgnoreCase) is true)
            {
                thinkingConfigParam = new ThinkingConfigParam(new ThinkingConfigDisabled());
            }
            else
            {
                // Only Claude 4.6 and 4.7 supports adaptive and don't support budgetTokens so we ignore ThinkingBudget for them.
                if (isClaude46 || isClaude47)
                {
                    thinkingConfigParam = new ThinkingConfigParam(new ThinkingConfigAdaptive());
                }
                else
                {
                    if (!long.TryParse(owner.ThinkingBudget, out var budgetTokens))
                    {
                        budgetTokens = -1L;
                    }

                    thinkingConfigParam = new ThinkingConfigParam(
                        new ThinkingConfigEnabled
                        {
                            BudgetTokens = Math.Max(budgetTokens, 2048)
                        });
                }
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

        private static IEnumerable<ChatMessage> PreprocessMessages(IEnumerable<ChatMessage> originalMessages)
        {
            return originalMessages.Invoke(static chatMessage =>
            {
                for (var i = chatMessage.Contents.Count - 1; i >= 0; i--)
                {
                    // Remove those TextReasoningContent with empty ProtectedData as they are likely to cause issues for some models (e.g. Claude 4.6) that don't support reasoning effort and expect the content to be text-only.
                    if (chatMessage.Contents[i] is TextReasoningContent { ProtectedData: not { Length: > 0 } })
                    {
                        chatMessage.Contents.RemoveAt(i);
                    }
                }
            });
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetResponseAsync(PreprocessMessages(messages), options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetStreamingResponseAsync(PreprocessMessages(messages), options, cancellationToken);
        }

        /// <summary>
        /// Check if the model is Claude Opus 4.6
        /// Supports various formats including:
        /// - Direct API: claude-opus-4-6
        /// - AWS Bedrock: anthropic.claude-opus-4-6-v1
        /// - GCP Vertex AI: claude-opus-4-6
        /// </summary>
        [GeneratedRegex(@"(?:anthropic\.)?claude-(?:opus|sonnet)-4[.-]6(?:[@\-:][\w\-:]+)?$")]
        private static partial Regex Claude46Regex();
        
        /// <summary>
        /// Check if the model is Claude Opus 4.7.
        /// 4.7 rejects temperature/top_p/top_k and natively supports xhigh reasoning effort.
        /// </summary>
        /// <returns></returns>
        [GeneratedRegex(@"(?:anthropic\.)?claude-opus-4[.-]7(?:[@\-:][\w\-:]+)?$")]
        private static partial Regex Claude47Regex();
    }
}