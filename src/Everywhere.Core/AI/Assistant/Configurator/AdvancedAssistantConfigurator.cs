using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;

namespace Everywhere.AI.Configurator;

/// <summary>
/// Configurator for advanced model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class AdvancedAssistantConfigurator(Assistant owner) : AssistantConfigurator
{
    [HiddenSettingsItem]
    [CustomValidation(typeof(AdvancedAssistantConfigurator), nameof(ValidateEndpoint))]
    public string? Endpoint
    {
        get => owner.Endpoint;
        set
        {
            if (owner.Endpoint == value) return;

            ValidateProperty(value);
            owner.Endpoint = value;
            OnPropertyChanged();
        }
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Endpoint_Header,
        LocaleKey.CustomAssistant_Endpoint_Description)]
    public SettingsControl<PreviewEndpointTextBox> PreviewEndpointControl => new(
        new PreviewEndpointTextBox
        {
            MinWidth = 320d,
            [!PreviewEndpointTextBox.EndpointProperty] = new Binding(nameof(Endpoint))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
            [!PreviewEndpointTextBox.SchemaProperty] = new Binding(nameof(Schema))
            {
                Source = this,
                Mode = BindingMode.OneWay
            }
        });

    [HiddenSettingsItem]
    public Guid ApiKey
    {
        get => owner.ApiKey;
        set
        {
            if (owner.ApiKey == value) return;

            owner.ApiKey = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ApiKey_Header,
        LocaleKey.CustomAssistant_ApiKey_Description)]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(ServiceLocator.Resolve<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
        });

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Schema_Header,
        LocaleKey.CustomAssistant_Schema_Description)]
    public ModelProviderSchema Schema
    {
        get => owner.Schema;
        set
        {
            if (owner.Schema == value) return;

            owner.Schema = value;
            OnPropertyChanged();
        }
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelId_Header,
        LocaleKey.CustomAssistant_ModelId_Description)]
    [Required, MinLength(1)]
    public string? ModelId
    {
        get => owner.ModelId;
        set => owner.ModelId = value;
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SupportsReasoning_Header,
        LocaleKey.CustomAssistant_SupportsReasoning_Description)]
    public bool SupportsReasoning
    {
        get => owner.SupportsReasoning;
        set => owner.SupportsReasoning = value;
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SupportsToolCall_Header,
        LocaleKey.CustomAssistant_SupportsToolCall_Description)]
    public bool SupportsToolCall
    {
        get => owner.SupportsToolCall;
        set => owner.SupportsToolCall = value;
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_InputModalities_Header,
        LocaleKey.CustomAssistant_InputModalities_Description)]
    public SettingsControl<ModalitiesSelector> InputModalitiesSelector => new(
        new ModalitiesSelector
        {
            [!ModalitiesSelector.ModalitiesProperty] = new Binding(nameof(owner.InputModalities))
            {
                Source = owner,
                Mode = BindingMode.TwoWay
            }
        });

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ContextLimit_Header,
        LocaleKey.CustomAssistant_ContextLimit_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int ContextLimit
    {
        get => owner.ContextLimit;
        set => owner.ContextLimit = value;
    }

    /// <summary>
    /// Maximum number of tokens that the model can output in a single request.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_OutputLimit_Header,
        LocaleKey.CustomAssistant_OutputLimit_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int OutputLimit
    {
        get => owner.OutputLimit;
        set => owner.OutputLimit = value;
    }

    public override void Backup()
    {
        Backup(Endpoint);
        Backup(Schema);
        Backup(ModelId);
        Backup(SupportsToolCall);
        Backup(SupportsReasoning);
        Backup(owner.InputModalities);
        Backup(owner.OutputModalities);
        Backup(ContextLimit);
        Backup(OutputLimit);
    }

    public override void Apply()
    {
        owner.ModelProviderTemplateId = null;
        owner.ModelDefinitionTemplateId = null;

        Endpoint = Restore(Endpoint);
        Schema = Restore(Schema);
        ModelId = Restore(ModelId);
        SupportsToolCall = Restore(SupportsToolCall);
        SupportsReasoning = Restore(SupportsReasoning);
        owner.InputModalities = Restore(owner.InputModalities);
        owner.OutputModalities = Restore(owner.OutputModalities);
        ContextLimit = Restore(ContextLimit);
        OutputLimit = Restore(OutputLimit);
    }

    /// <summary>
    /// For advanced configurator, we will directly use the owner as the assistant, and the specialization is determined by the user input. So we can just return the owner here.
    /// </summary>
    /// <param name="specialization"></param>
    /// <returns></returns>
    public override Assistant ResolveAssistant(ModelSpecializations specialization) => owner;

    public static ValidationResult? ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ValidationResult(LocaleResolver.AdvancedAssistantConfigurator_InvalidEndpoint);
        }

        return ValidationResult.Success;
    }
}