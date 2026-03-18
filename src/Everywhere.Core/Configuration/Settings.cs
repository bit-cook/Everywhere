using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

/// <summary>
/// Represents the application settings.
/// A singleton that holds all the settings categories.
/// And automatically saves the settings to a JSON file when any setting is changed.
/// </summary>
[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public partial class Settings : ObservableObject
{
    [ObservableProperty]
    public partial string? Version { get; set; }

    #region Common

    public CommonSettings Common { get; set; } = new();

    public DisplaySettings Display { get; set; } = new();

    public ShortcutSettings Shortcut { get; set; } = new();

    public ProxySettings Proxy { get; set; } = new();

    #endregion

    public ModelSettings Model { get; set; } = new();

    public SystemAssistantSettings SystemAssistant { get; set; } = new();

    public ChatWindowSettings ChatWindow { get; set; } = new();

    public PluginSettings Plugin { get; set; } = new();
}