using Avalonia.Controls;
using Everywhere.Configuration;
using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

/// <summary>
/// Represents a settings category page that displays a list of settings items.
/// It dynamically creates settings items based on the properties of a specified settings category.
/// </summary>
public abstract partial class SettingsCategoryPage : UserControl, IMainViewNavigationItem
{
    public int Index { get; }

    public LucideIconKind Icon { get; }

    public IDynamicResourceKey TitleKey { get; }

    public SettingsItems Items { get; }

    protected SettingsCategoryPage(int index, LucideIconKind icon, IDynamicResourceKey titleKey, SettingsItems items)
    {
        Index = index;
        TitleKey = titleKey;
        Icon = icon;
        Items = items;

        InitializeComponent();
    }
}

public sealed class SettingsCategoryTopLevelPage(
    IMainViewNavigationTopLevelItem item,
    SettingsItems items
) : SettingsCategoryPage(item.Index, item.Icon, item.TitleKey, items), IMainViewNavigationTopLevelItem;

public sealed class SettingsCategorySubPage(IMainViewNavigationSubItem item, SettingsItems items)
    : SettingsCategoryPage(item.Index, item.Icon, item.TitleKey, items), IMainViewNavigationSubItem
{
    public Type GroupType => item.GroupType;

    public IDynamicResourceKey? DescriptionKey => item.DescriptionKey;
}

public class SettingsCategoryPageFactory(Settings settings) : IMainViewNavigationItemFactory
{
    public IEnumerable<IMainViewNavigationItem> CreateItems() =>
    [
        new SettingsCategory(),
        new SettingsCategorySubPage(settings.Common, settings.Common.SettingsItems),
        new SettingsCategorySubPage(settings.Display, settings.Display.SettingsItems),
        new SettingsCategorySubPage(settings.Shortcut, settings.Shortcut.SettingsItems),
        new SettingsCategorySubPage(settings.Proxy, settings.Proxy.SettingsItems),
        new SettingsCategorySubPage(settings.ChatWindow, settings.ChatWindow.SettingsItems),
        new SettingsCategorySubPage(settings.SystemAssistant, settings.SystemAssistant.SettingsItems),
    ];
}