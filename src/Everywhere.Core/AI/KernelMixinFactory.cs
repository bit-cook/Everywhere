using System.ClientModel;
using System.Net;
using System.Text.Json;
using Anthropic.Exceptions;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.AI;

/// <summary>
/// A factory for creating instances of <see cref="KernelMixin"/>.
/// </summary>
public sealed class KernelMixinFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : IKernelMixinFactory
{
    /// <summary>
    /// Creates a new instance of <see cref="KernelMixin"/>.
    /// </summary>
    /// <param name="assistant"></param>
    /// <returns>A new instance of <see cref="KernelMixin"/>.</returns>
    /// <exception cref="HandledChatException">Thrown if the model provider or definition is not found or not supported.</exception>
    public KernelMixin Create(Assistant assistant)
    {
        if (assistant.ModelId.IsNullOrWhiteSpace())
        {
            throw new HandledChatException(
                new InvalidOperationException("Model ID cannot be empty."),
                HandledChatExceptionType.InvalidConfiguration);
        }

        var connection = ResolveConnection(assistant);
        return connection.Schema switch
        {
            ModelProviderSchema.OpenAI => new OpenAIKernelMixin(assistant, connection, loggerFactory),
            ModelProviderSchema.OpenAIResponses => new OpenAIResponsesKernelMixin(assistant, connection, loggerFactory),
            ModelProviderSchema.Anthropic => new AnthropicKernelMixin(assistant, connection),
            ModelProviderSchema.Google => new GoogleKernelMixin(assistant, connection, loggerFactory),
            ModelProviderSchema.Ollama => new OllamaKernelMixin(assistant, connection),
            _ => throw new HandledChatException(
                new NotSupportedException($"Model provider schema '{connection.Schema}' is not supported."),
                HandledChatExceptionType.InvalidConfiguration,
                new DynamicResourceKey(LocaleKey.KernelMixinFactory_UnsupportedModelProviderSchema))
        };
    }

    /// <summary>
    /// Resolves the <see cref="ModelConnection"/> and <see cref="HttpClient"/> for the given <see cref="CustomAssistant"/>.
    /// For Official mode, the schema is inferred from the ModelId prefix, endpoint comes from the AI gateway,
    /// and the API key is null (OAuth is handled by the named HttpClient).
    /// For user-configured modes, endpoint/apiKey are read from the assistant configuration.
    /// </summary>
    private ModelConnection ResolveConnection(Assistant assistant) =>
        assistant.Schema is ModelProviderSchema.Official ? ResolveOfficialConnection(assistant) : ResolveUserConnection(assistant);

    /// <summary>
    /// Resolves connection for Official (cloud gateway) mode.
    /// The actual provider schema is inferred from the model ID prefix (e.g. "openai/gpt-4o" → OpenAIResponses).
    /// </summary>
    private ModelConnection ResolveOfficialConnection(Assistant assistant)
    {
        var schema = InferSchemaFromModelId(assistant.ModelId);

        var endpoint = schema.NormalizeEndpoint(CloudConstants.AIGatewayBaseUrl) ??
            throw new HandledChatException(
                new InvalidOperationException("AI Gateway base URL is not configured."),
                HandledChatExceptionType.InvalidEndpoint);

        // Official mode uses OAuth via the named HttpClient — no user API key needed.
        // Some SDKs require a non-null credential, so we pass null and let each mixin handle it
        // (e.g. OpenAIKernelMixin uses NoneAuthenticationPolicy, others use "official" placeholder).
        var httpClient = httpClientFactory.CreateClient(nameof(ICloudClient));

        return new ModelConnection(schema, endpoint, ApiKey: null, httpClient, TransformOfficialException);
    }

    /// <summary>
    /// Resolves connection for user-configured (non-Official) modes.
    /// </summary>
    private ModelConnection ResolveUserConnection(Assistant assistant)
    {
        if (!Uri.TryCreate(assistant.Endpoint, UriKind.Absolute, out _))
        {
            throw new HandledChatException(
                new InvalidOperationException("Invalid endpoint URL."),
                HandledChatExceptionType.InvalidEndpoint);
        }

        var endpoint = assistant.Schema.NormalizeEndpoint(assistant.Endpoint) ??
            throw new HandledChatException(
                new InvalidOperationException("Endpoint cannot be empty."),
                HandledChatExceptionType.InvalidEndpoint);

        var apiKey = ApiKey.GetKey(assistant.ApiKey);

        // Create an HttpClient instance using the factory.
        // It will have the configured settings (timeout and proxy).
        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(assistant.RequestTimeoutSeconds);

        return new ModelConnection(assistant.Schema, endpoint, apiKey, httpClient, null);
    }

    /// <summary>
    /// Infers the actual <see cref="ModelProviderSchema"/> from an Official model ID.
    /// Official model IDs follow the format "provider/model-name" (e.g. "openai/gpt-4o", "google/gemini-2.5-flash").
    /// </summary>
    private static ModelProviderSchema InferSchemaFromModelId(string? modelId)
    {
        var provider = modelId?.Split('/').FirstOrDefault()?.ToLowerInvariant();
        return provider switch
        {
            "openai" => ModelProviderSchema.OpenAIResponses,
            "google" => ModelProviderSchema.Google,
            "anthropic" or "minimax" => ModelProviderSchema.Anthropic,
            _ => ModelProviderSchema.OpenAI
        };
    }

    private Exception TransformOfficialException(Exception exception)
    {
        try
        {
            var payload = exception switch
            {
                ClientResultException { Status: 502 } clientResultException when clientResultException.GetRawResponse() is { } response =>
                    response.BufferContent().ToObjectFromJson<ApiPayload>(),
                Anthropic5xxException { StatusCode: HttpStatusCode.BadGateway, ResponseBody: { } responseBody } =>
                    JsonSerializer.Deserialize<ApiPayload>(responseBody),
                HttpOperationException { StatusCode: HttpStatusCode.BadGateway, ResponseContent: { } responseContent } =>
                    JsonSerializer.Deserialize<ApiPayload>(responseContent),
                _ => null
            };

            if (payload is not { Success: false, Error.Upstream: { } upstream })
            {
                return exception;
            }

            return new HttpOperationException(
                upstream.StatusCode,
                upstream.Body?.RootElement.GetRawText() ?? "Upstream error with no body",
                "upstream_error",
                exception);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(ICloudClient)).LogError(ex, "Failed to transform official exception. Returning original exception.");

            return exception; // If any error occurs during transformation, return the original exception to avoid masking the issue.
        }
    }
}