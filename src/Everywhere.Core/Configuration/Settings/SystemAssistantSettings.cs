using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public partial class SystemAssistantSettings : ObservableObject, IMainViewNavigationSubItem
{
    [HiddenSettingsItem]
    public int Index => 4;

    [HiddenSettingsItem]
    public LucideIconKind Icon => LucideIconKind.Sparkles;

    [HiddenSettingsItem]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Header);

    [HiddenSettingsItem]
    public Type GroupType => typeof(SettingsCategory);

    [HiddenSettingsItem]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_SystemAssistant_Description);
}