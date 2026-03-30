using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.I18N;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Cloud;

public sealed partial class OfficialModelProvider :
    IOfficialModelProvider,
    IRecipient<UserProfileUpdatedMessage>,
    IRecipient<SubscriptionInformationUpdatedMessage>,
    IAsyncInitializer,
    IDisposable
{
    public ReadOnlyObservableCollection<ModelDefinitionTemplate> ModelDefinitions { get; }

    private readonly PersistentState _persistentState;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OfficialModelProvider> _logger;

    private readonly SourceList<ModelDefinitionTemplate> _modelDefinitionsSource = new();
    private readonly IDisposable _modelDefinitionsSubscription;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset _nextFetchCooldownTime = DateTimeOffset.MinValue;

    public OfficialModelProvider(PersistentState persistentState, IHttpClientFactory httpClientFactory, ILogger<OfficialModelProvider> logger)
    {
        _persistentState = persistentState;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        if (persistentState.OfficialModelDefinitionTemplate is not null)
        {
            _modelDefinitionsSource.AddRange(persistentState.OfficialModelDefinitionTemplate);
        }

        ModelDefinitions = _modelDefinitionsSource.Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _modelDefinitionsSubscription);

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    /// <summary>
    /// Actually performs the refresh by calling the official API, parsing the response, and updating the internal list and cache.
    /// </summary>
    /// <param name="exceptionHandler"></param>
    /// <param name="cancellationToken"></param>
    public async Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken)) return;

        try
        {
            if (CloudConstants.AIGatewayBaseUrl.IsNullOrEmpty()) return;
            if (DateTimeOffset.Now < _nextFetchCooldownTime) return; // Enforce a cooldown between fetches to avoid hammering the endpoint.

            using var httpClient = _httpClientFactory.CreateClient(nameof(ICloudClient));
            var request = new HttpRequestMessage(HttpMethod.Get, $"{CloudConstants.AIGatewayBaseUrl}/v1/models");

            // If not login, a UserNotLoginException will be thrown
            var response = await httpClient.SendAsync(request, cancellationToken);
            var payload = await ApiPayload<IReadOnlyList<CloudModelDefinition>>.EnsureSuccessFromHttpResponseJsonAsync(
                response,
                ModelsResponseJsonSerializerContext.Default.Options,
                cancellationToken);

            var cloudModelDefinitions = payload.EnsureData();
            var result = cloudModelDefinitions.AsValueEnumerable().Select(m => m.ToModelDefinitionTemplate()).ToList();
            _modelDefinitionsSource.Edit(list =>
            {
                list.Clear();
                list.AddRange(result);
            });
            _persistentState.OfficialModelDefinitionTemplate = result;

            _nextFetchCooldownTime = DateTimeOffset.Now.AddSeconds(10);
        }
        catch (UserNotLoginException)
        {
            _modelDefinitionsSource.Clear();
            _nextFetchCooldownTime = DateTimeOffset.Now;
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (HttpRequestException ex)
        {
            exceptionHandler?.HandleException(HandledSystemException.Handle(ex));

            if (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _nextFetchCooldownTime = DateTimeOffset.Now.AddMinutes(1);
            }
            else
            {
                // Avoid hammering the endpoint on failure, but allow retries sooner than the normal 10s.
                _nextFetchCooldownTime = DateTimeOffset.Now.AddSeconds(3);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing model definitions");

            exceptionHandler?.HandleException(HandledSystemException.Handle(ex));
            _nextFetchCooldownTime = DateTimeOffset.Now.AddSeconds(3);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Receive(UserProfileUpdatedMessage message)
    {
        _nextFetchCooldownTime = DateTimeOffset.Now;
        RefreshAsync().Detach();
    }

    public void Receive(SubscriptionInformationUpdatedMessage message)
    {
        _nextFetchCooldownTime = DateTimeOffset.Now;
        RefreshAsync().Detach();
    }

    public void Dispose()
    {
        _modelDefinitionsSource.Dispose();
        _modelDefinitionsSubscription.Dispose();
        _refreshLock.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    /// <summary>
    /// Standard model definition according to https://models.dev/api.json
    /// </summary>
    /// <param name="ModelId"></param>
    /// <param name="Name"></param>
    /// <param name="SupportsReasoning"></param>
    /// <param name="SupportsToolCall"></param>
    /// <param name="Modalities"></param>
    /// <param name="LimitInfo"></param>
    private sealed record CloudModelDefinition(
        [property: JsonPropertyName("id")] string ModelId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("icon")] string Icon,
        [property: JsonPropertyName("description")] JsonDynamicResourceKey? DescriptionKey,
        [property: JsonPropertyName("reasoning")] bool SupportsReasoning,
        [property: JsonPropertyName("toolCall")] bool SupportsToolCall,
        [property: JsonPropertyName("knowledge")] string? KnowledgeCutoff,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
        [property: JsonPropertyName("deprecationDate")] string? DeprecationDate,
        [property: JsonPropertyName("modalities")] CloudModelModalities Modalities,
        [property: JsonPropertyName("limit")] CloudModelLimitInfo LimitInfo,
        [property: JsonPropertyName("pricing")] CloudModelPricing Pricing
    )
    {
        public ModelDefinitionTemplate ToModelDefinitionTemplate() =>
            new()
            {
                ModelId = ModelId,
                Name = Name,
                SupportsReasoning = SupportsReasoning,
                SupportsToolCall = SupportsToolCall,
                KnowledgeCutoff = DateOnly.TryParse(KnowledgeCutoff, out var knowledgeDate) ? knowledgeDate : null,
                ReleaseDate = DateOnly.TryParse(ReleaseDate, out var releaseDate) ? releaseDate : null,
                DeprecationDate = DateOnly.TryParse(DeprecationDate, out var deprecationDate) ? deprecationDate : null,
                InputModalities = ConvertModalities(Modalities.Input),
                OutputModalities = ConvertModalities(Modalities.Output),
                ContextLimit = LimitInfo.Context,
                OutputLimit = LimitInfo.Output,
                IconUrl = Icon,
                DescriptionKey = DescriptionKey,
                Pricing = ConvertPricing(Pricing)
            };

        private static Modalities ConvertModalities(IReadOnlyList<string> modalityStrings) => modalityStrings.AsValueEnumerable().Aggregate(
            AI.Modalities.None,
            (current, modality) => current | modality.ToLower() switch
            {
                "text" => AI.Modalities.Text,
                "image" => AI.Modalities.Image,
                "audio" => AI.Modalities.Audio,
                "video" => AI.Modalities.Video,
                "pdf" => AI.Modalities.Pdf,
                _ => AI.Modalities.None
            });

        private static ModelPricing ConvertPricing(CloudModelPricing pricing)
        {
            const double CreditsMultiplier = 0.01d; // Convert from "per MTokens" to "per Token"
            var tiers = pricing.AsValueEnumerable().Select(t => new PricingTier(
                t.Threshold,
                new TokenPricing(
                    t.Pricing.Input * CreditsMultiplier,
                    t.Pricing.Output * CreditsMultiplier,
                    t.Pricing.CachedInput * CreditsMultiplier))).ToList();
            return new ModelPricing(tiers, ModelPricingUnit.CreditPerToken);
        }
    }

    private sealed record CloudModelModalities(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("output")] IReadOnlyList<string> Output
    );

    private sealed record CloudModelLimitInfo(
        [property: JsonPropertyName("context")] int Context,
        [property: JsonPropertyName("input")] int Input = 0,
        [property: JsonPropertyName("output")] int Output = 0
    );

    private sealed record CloudTokenPricing(
        [property: JsonPropertyName("input")] long Input,
        [property: JsonPropertyName("output")] long Output,
        [property: JsonPropertyName("cachedInput")] long CachedInput
    );

    private sealed record CloudPricingTier(
        [property: JsonPropertyName("threshold")] long Threshold,
        [property: JsonPropertyName("pricing")] CloudTokenPricing Pricing
    );

    private sealed class CloudModelPricing : List<CloudPricingTier>;

    [JsonSerializable(typeof(ApiPayload<IReadOnlyList<CloudModelDefinition>>))]
    private sealed partial class ModelsResponseJsonSerializerContext : JsonSerializerContext;

    #region Async Initializer Implementation

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        RefreshAsync().Detach();
        return Task.CompletedTask;
    }

    #endregion
}