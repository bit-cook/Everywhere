using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.AI;

/// <summary>
/// Allowing users to define and manage their own custom AI assistants.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class CustomAssistant : Assistant, ISystemPromptProvider
{
    [HiddenSettingsItem]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial ColoredIcon? Icon { get; set; } = new(ColoredIconType.Lucide) { Kind = LucideIconKind.Bot };

    [ObservableProperty]
    [HiddenSettingsItem]
    [MinLength(1)]
    [MaxLength(128)]
    public partial string? Name { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? Description { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(LocaleKey.Empty)]
    public SettingsControl<CustomAssistantInformationForm> InformationForm => new(
        new CustomAssistantInformationForm
        {
            CustomAssistant = this
        });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SystemPrompt_Header,
        LocaleKey.CustomAssistant_SystemPrompt_Description)]
    [SettingsStringItem(IsMultiline = true, MaxLength = 40960, Watermark = Prompts.DefaultSystemPrompt)]
    [DefaultValue(null)]
    public partial string? SystemPrompt { get; set; }
}