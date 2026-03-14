using Lucide.Avalonia;

namespace Everywhere.Configuration;

/// <summary>
/// Represents a category of settings in the application.
/// Each category can have a display name and an icon associated with it.
/// </summary>
public interface ISettingsCategory
{
    /// <summary>
    /// The display name of the settings category.
    /// </summary>
    IDynamicResourceKey DisplayNameKey { get; }

    /// <summary>
    /// The Icon of the settings category.
    /// </summary>
    LucideIconKind Icon { get; }

    SettingsItems? SettingsItems { get; }
}