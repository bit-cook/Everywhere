using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Interop;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public partial class ShortcutSettings : ObservableObject, IMainViewNavigationSubItem
{
    [HiddenSettingsItem]
    public int Index => 2;

    [HiddenSettingsItem]
    public LucideIconKind Icon => LucideIconKind.Keyboard;

    [HiddenSettingsItem]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Header);

    [HiddenSettingsItem]
    public Type GroupType => typeof(SettingsCategory);

    [HiddenSettingsItem]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Description);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_ChatWindow_Header,
        LocaleKey.ShortcutSettings_ChatWindow_Desription)]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut ChatWindow { get; set; } = new(Key.E, KeyModifiers.Control | KeyModifiers.Shift);
}