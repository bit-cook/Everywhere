using Avalonia.Collections;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// A hub control representing a settings category page, which displays a list of items that can be navigated to.
/// </summary>
public class SettingsCategoryPage : TemplatedControl
{
    /// <summary>
    /// Defines the <see cref="Title"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsCategoryPage, string?>(nameof(Title));

    /// <summary>
    /// Gets or sets the title of the settings category page, which is displayed in the navigation bar.
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="Items"/> property.
    /// </summary>
    public static readonly DirectProperty<SettingsCategoryPage, AvaloniaList<Item>> ItemsProperty =
        AvaloniaProperty.RegisterDirect<SettingsCategoryPage, AvaloniaList<Item>>(
        nameof(Items),
        o => o.Items);

    /// <summary>
    /// Gets the list of items to display on the settings category page. Each item represents a navigable setting.
    /// </summary>
    public AvaloniaList<Item> Items { get; } = [];

    /// <summary>
    /// Defines the <see cref="Command"/> property.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<NavigationBarItem>?> CommandProperty =
        AvaloniaProperty.Register<SettingsCategoryPage, IRelayCommand<NavigationBarItem>?>(nameof(Command));

    /// <summary>
    /// Gets or sets the command to execute when an item is selected.
    /// The command parameter will be the <see cref="Item.Route"/> of the selected item.
    /// </summary>
    public IRelayCommand<NavigationBarItem>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>
    /// Represents an individual item in the settings category page, which can be navigated to.
    /// </summary>
    public sealed record Item(
        LucideIconKind Icon,
        IDynamicResourceKey TitleKey,
        IDynamicResourceKey? DescriptionKey,
        NavigationBarItem Route);
}