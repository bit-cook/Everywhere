using System.Text.Json.Serialization;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Interop;

namespace Everywhere.Configuration;

/// <summary>
/// Represents a switchable composite keyboard shortcut that includes a main shortcut and an alternative shortcut.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class CompositeKeyboardShortcut : ObservableObject
{
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CompositeKeyboardShortcut_IsEnabled_Header,
        LocaleKey.CompositeKeyboardShortcut_IsEnabled_Description)]
    [SettingsItem(Group = "_")]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CompositeKeyboardShortcut_Main_Header,
        LocaleKey.CompositeKeyboardShortcut_Main_Description)]
    [SettingsItem(Group = "_")]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut Main { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CompositeKeyboardShortcut_Alternative_Header,
        LocaleKey.CompositeKeyboardShortcut_Alternative_Description)]
    [SettingsItem(Group = "_")]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut Alternative { get; set; }

    /// <summary>
    /// Set by IConfiguration.Bind, so this can be converted from KeyboardShortcut
    /// </summary>
    [JsonIgnore]
    [Obsolete("This property is designed for forward compatibility and should not be used directly. Use Main property instead.", true)]
    public Key Key
    {
        get => default;
        set => Main = Main with { Key = value };
    }

    [JsonIgnore]
    [Obsolete("This property is designed for forward compatibility and should not be used directly. Use Main property instead.", true)]
    public KeyModifiers Modifiers
    {
        get => default;
        set => Main = Main with { Modifiers = value };
    }

    public static implicit operator CompositeKeyboardShortcut(KeyboardShortcut shortcut) => new() { Main = shortcut };
}