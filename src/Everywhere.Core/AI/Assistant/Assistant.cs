using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI.Configurator;
using Everywhere.Configuration;
using Everywhere.Views;

namespace Everywhere.AI;

public abstract partial class Assistant : ObservableValidator, IModelDefinition
{
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? Endpoint { get; set; }

    /// <summary>
    /// The GUID of the API key to use for this custom assistant.
    /// </summary>
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial Guid ApiKey { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial ModelProviderSchema Schema { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? ModelId { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool SupportsReasoning { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool SupportsToolCall { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial Modalities InputModalities { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial Modalities OutputModalities { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial int ContextLimit { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial int OutputLimit { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial ModelSpecializations Specializations { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    [NotifyPropertyChangedFor(nameof(Configurator))]
    public partial AssistantConfiguratorType ConfiguratorType { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? ModelProviderTemplateId { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? ModelDefinitionTemplateId { get; set; }

    [JsonIgnore]
    [HiddenSettingsItem]
    public AssistantConfigurator Configurator => GetConfigurator(ConfiguratorType);

    [JsonIgnore]
    [DynamicResourceKey(LocaleKey.CustomAssistant_ConfiguratorSelector_Header)]
    protected SettingsControl<AssistantConfiguratorSelector> ConfiguratorSelector => new(
        new AssistantConfiguratorSelector
        {
            Assistant = this
        });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_RequestTimeoutSeconds_Header,
        LocaleKey.CustomAssistant_RequestTimeoutSeconds_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    [DefaultValue(20)]
    public partial int RequestTimeoutSeconds { get; set; } = 20;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Temperature_Header,
        LocaleKey.CustomAssistant_Temperature_Description)]
    [SettingsDoubleItem(Min = 0.0, Max = 2.0, Step = 0.01)]
    public partial Customizable<double> Temperature { get; set; } = 1.0;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_TopP_Header,
        LocaleKey.CustomAssistant_TopP_Description)]
    [SettingsDoubleItem(Min = 0.0, Max = 1.0, Step = 0.01)]
    public partial Customizable<double> TopP { get; set; } = 0.9;

    private readonly OfficialAssistantConfigurator _officialConfigurator;
    private readonly PresetBasedAssistantConfigurator _presetBasedConfigurator;
    private readonly AdvancedAssistantConfigurator _advancedConfigurator;

    protected Assistant()
    {
        _officialConfigurator = new OfficialAssistantConfigurator(this);
        _presetBasedConfigurator = new PresetBasedAssistantConfigurator(this);
        _advancedConfigurator = new AdvancedAssistantConfigurator(this);
    }

    public AssistantConfigurator GetConfigurator(AssistantConfiguratorType type) => type switch
    {
        AssistantConfiguratorType.Official => _officialConfigurator,
        AssistantConfiguratorType.PresetBased => _presetBasedConfigurator,
        _ => _advancedConfigurator
    };

    public void ApplyTemplate(ModelProviderTemplate? modelProviderTemplate)
    {
        if (modelProviderTemplate is not null)
        {
            Endpoint = modelProviderTemplate.Endpoint;
            Schema = modelProviderTemplate.Schema;
            RequestTimeoutSeconds = modelProviderTemplate.RequestTimeoutSeconds;
        }
        else
        {
            Endpoint = string.Empty;
            Schema = ModelProviderSchema.OpenAI;
            RequestTimeoutSeconds = 20;
        }
    }

    public void ApplyTemplate(ModelDefinitionTemplate? modelDefinitionTemplate)
    {
        if (modelDefinitionTemplate is not null)
        {
            ModelId = modelDefinitionTemplate.ModelId;
            SupportsReasoning = modelDefinitionTemplate.SupportsReasoning;
            SupportsToolCall = modelDefinitionTemplate.SupportsToolCall;
            InputModalities = modelDefinitionTemplate.InputModalities;
            OutputModalities = modelDefinitionTemplate.OutputModalities;
            ContextLimit = modelDefinitionTemplate.ContextLimit;
            OutputLimit = modelDefinitionTemplate.OutputLimit;
        }
        else
        {
            ModelId = string.Empty;
            SupportsReasoning = false;
            SupportsToolCall = false;
            InputModalities = default;
            OutputModalities = default;
            ContextLimit = 0;
            OutputLimit = 0;
        }
    }
}